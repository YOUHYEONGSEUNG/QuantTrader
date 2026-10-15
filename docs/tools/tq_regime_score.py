# -*- coding: utf-8 -*-
"""
추세 판단 채점기 (TQ_Regime 재현 + 사후 정답과 비교)

- 1분봉 데이터(NinjaTrader 내보내기 파일, UTC)를 읽어 3분·10분봉을 만들고,
  TQ_Regime.cs(2026-10-14 작업 코드)의 추세 판단을 그대로 재현한다.
- 사후 정답: 1분봉 종가의 지그재그로 큰 움직임(다리)을 찾고, 크기가 기준 이상인 다리를 "추세"로 본다.
- 채점: 다리를 몇 분 만에 잡았는지, 잡은 뒤 남은 움직임이 몇 %인지, 추세를 따라가면 몇 포인트인지, 분 단위 정확도.
- 검증: TQ_log_종목.txt의 "10분봉 마감 추세=… MACD선=… RSI=… 점수=" 줄과 재현값을 비교한다.

표준 라이브러리만 쓴다. 사용법은 맨 아래 main()과 README 참고.
"""
import argparse
import bisect
import math
import os
import re
import sys
from datetime import datetime, timedelta

NAN = float("nan")

# ───────────────────────── 데이터 읽기 ─────────────────────────

def load_nt_export(path, utc_offset_hours=9):
    """NinjaTrader 내보내기 1분봉: 'yyyyMMdd HHmmss;open;high;low;close;volume' (UTC, 봉 마감 시각).
    KST(UTC+9)로 바꿔 돌려준다."""
    bars = []
    off = timedelta(hours=utc_offset_hours)
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            p = line.split(";")
            if len(p) < 5:
                continue
            t = datetime.strptime(p[0], "%Y%m%d %H%M%S") + off
            bars.append((t, float(p[1]), float(p[2]), float(p[3]), float(p[4])))
    bars.sort(key=lambda b: b[0])
    # 같은 시각이 두 번 있으면(파일 여러 개를 합친 경우) 뒤의 것을 쓴다
    out = []
    for b in bars:
        if out and out[-1][0] == b[0]:
            out[-1] = b
        else:
            out.append(b)
    return out


LOG_TIME = re.compile(r"^\[(\d{4}-\d{2}-\d{2}) (오전|오후) (\d{1,2}):(\d{2}):(\d{2})\]")


def parse_log_time(line):
    m = LOG_TIME.match(line)
    if not m:
        return None
    h = int(m.group(3)) % 12
    if m.group(2) == "오후":
        h += 12
    d = datetime.strptime(m.group(1), "%Y-%m-%d")
    return d.replace(hour=h, minute=int(m.group(4)), second=int(m.group(5)))


def load_tq_log_bars(path):
    """TQ_log의 '시고저종=' 줄에서 1분봉을 꺼낸다 (KST). 구간이 띄엄띄엄이라 시험용으로만 쓴다."""
    bars = {}
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            if "시고저종=" not in line:
                continue
            t = parse_log_time(line)
            o, h, l, c = (float(x) for x in line.rsplit("시고저종=", 1)[1].strip().split("/"))
            bars[t] = (t, o, h, l, c)
    return [bars[k] for k in sorted(bars)]


def resample(bars, minutes):
    """1분봉 → N분봉. NinjaTrader처럼 봉 시각 = 마감 시각이고, 마감 시각이 시계의 N분 배수가 되게 묶는다."""
    if minutes == 1:
        return list(bars)
    out = []
    cur = None
    for t, o, h, l, c in bars:
        # 이 1분봉(마감 t)이 들어가는 N분봉의 마감 시각
        mins = t.hour * 60 + t.minute
        rem = mins % minutes
        end = t if rem == 0 and t.second == 0 else (t.replace(second=0) + timedelta(minutes=(minutes - rem)))
        if cur is not None and cur[0] == end:
            cur = [end, cur[1], max(cur[2], h), min(cur[3], l), c]
        else:
            if cur is not None:
                out.append(tuple(cur))
            cur = [end, o, h, l, c]
    if cur is not None:
        out.append(tuple(cur))
    return out

# ───────────────────────── 지표 (NinjaTrader 내장과 같은 식) ─────────────────────────

def sma(values, period):
    """NT SMA: 봉이 period보다 적으면 있는 봉의 평균."""
    out = []
    s = 0.0
    for i, v in enumerate(values):
        s += v
        if i >= period:
            s -= values[i - period]
        out.append(s / min(i + 1, period))
    return out


def ema(values, period):
    k = 2.0 / (1 + period)
    out = []
    for i, v in enumerate(values):
        out.append(v if i == 0 else v * k + (1 - k) * out[-1])
    return out


def macd(close, fast, slow, smooth):
    """NT MACD: 첫 봉은 0, 그 뒤 EMA 차이와 그 EMA."""
    k1, k3, k5 = 2.0 / (1 + fast), 2.0 / (1 + slow), 2.0 / (1 + smooth)
    line, avg, diff = [], [], []
    fe = se = 0.0
    for i, c in enumerate(close):
        if i == 0:
            fe = se = c
            line.append(0.0); avg.append(0.0); diff.append(0.0)
        else:
            fe = k1 * c + (1 - k1) * fe
            se = k3 * c + (1 - k3) * se
            m = fe - se
            a = k5 * m + (1 - k5) * avg[-1]
            line.append(m); avg.append(a); diff.append(m - a)
    return line, avg, diff


def rsi(close, period):
    """NT RSI: period봉째에 단순평균으로 시작, 그 뒤 (이전 × (n−1) + 새 값) / n. 그 전은 NaN."""
    n = len(close)
    out = [NAN] * n
    ups = [0.0] * n
    downs = [0.0] * n
    au = ad = 0.0
    for i in range(1, n):
        downs[i] = max(close[i - 1] - close[i], 0.0)
        ups[i] = max(close[i] - close[i - 1], 0.0)
        if i + 1 < period:
            continue
        if i + 1 == period:
            ad = sum(downs[i - period + 1:i + 1]) / period
            au = sum(ups[i - period + 1:i + 1]) / period
        else:
            ad = (ad * (period - 1) + downs[i]) / period
            au = (au * (period - 1) + ups[i]) / period
        out[i] = 100.0 if ad == 0 else 100 - 100 / (1 + au / ad)
    return out


def atr(bars, period):
    """NT ATR: 첫 봉은 고가−저가, period봉까지는 누적 평균, 그 뒤 와일더 평균."""
    out = []
    for i, (t, o, h, l, c) in enumerate(bars):
        if i == 0:
            out.append(h - l)
            continue
        pc = bars[i - 1][4]
        tr = max(abs(l - pc), max(h - l, abs(h - pc)))
        if i < period:
            out.append((out[-1] * i + tr) / (i + 1))
        else:
            out.append((out[-1] * (period - 1) + tr) / period)
    return out


def rolling_min(values, period):
    return [min(values[max(0, i - period + 1):i + 1]) for i in range(len(values))]


def rolling_max(values, period):
    return [max(values[max(0, i - period + 1):i + 1]) for i in range(len(values))]

# ───────────────────────── TQ_Regime 재현 ─────────────────────────

DEFAULTS = dict(
    bar=10, fast=6, mid=18, slow=36, macd_fast=12, macd_slow=26, macd_smooth=9, hist_bars=2,
    rsi_period=14, rsi_high=60.0, rsi_low=40.0, div_bars=15, stoch_k=10, stoch_smooth=5, stoch_d=5,
    cross_low=20.0, cross_high=80.0, cross_filter=5, suspect_return=True, rsi_lookback=1, mode=1,
    score_min=2, score_rsi_band=5.0, no_sideways=False, score_event_bars=3, rsi_relative=False,
    rsi_center_bars=20, rsi_offset=8.0, score_exit=0, suspect_needs_price=False, pivot=2,
    # 2026-10-14 TQ_Regime 추가 설정 (기본값이면 그 전과 같은 값)
    suspect_max_score=5, suspect_min_evidence=1, suspect_evidence_bars=2, trend_confirm_bars=1, trend_exit_bars=1,
    # v3 (mode=3, docs/v3 지시서 3장). 이동평균은 위의 fast / mid / slow, RSI 폭은 score_rsi_band를 같이 쓴다
    s2_mode=1, slope_bars=5, use_rsi=True, macd_slope_bars=2,
    trend_start_fast=1, trend_start_slow=1, trend_end_slow=0, suspect_fast=2, direct_reverse=0,
)

# 설정 문자열의 짧은 이름 → DEFAULTS 키
ALIASES = {
    "rsirel": "rsi_relative", "center": "rsi_center_bars", "offset": "rsi_offset", "scoremin": "score_min",
    "exit": "score_exit", "event": "score_event_bars", "band": "score_rsi_band", "nosideways": "no_sideways",
    "needsprice": "suspect_needs_price", "return": "suspect_return", "rsibars": "rsi_lookback",
    "maxscore": "suspect_max_score", "evidence": "suspect_min_evidence", "evbars": "suspect_evidence_bars",
    "confirm": "trend_confirm_bars", "exitbars": "trend_exit_bars",
    # v3
    "s2": "s2_mode", "slope": "slope_bars", "userrsi": "use_rsi", "mslope": "macd_slope_bars",
    "tsf": "trend_start_fast", "tss": "trend_start_slow", "tes": "trend_end_slow", "sf": "suspect_fast", "rev": "direct_reverse",
}


def compute_regime_v3(tb, p):
    """v3 (mode=3): 느린 점수 S와 빠른 점수 F로 추세 +1 / 0 / −1을 정하고, 추세 중 F가 반대로 꺾이면 의심 이벤트를 낸다.
    TQ_Regime.cs v3와 같은 식 (v3 지시서 3장). 다른 방식과 같이 채점하려고 regime은 +2 / 0 / −2로 돌려준다
    (v3에는 의심 '상태'가 없다. 의심은 suspect_event로 따로 낸다: 상승추세 중 하락의심 −1, 하락추세 중 상승의심 +1)."""
    n = len(tb)
    C = [b[4] for b in tb]
    sF, sM, sS = sma(C, p["fast"]), sma(C, p["mid"]), sma(C, p["slow"])
    mline, _, _ = macd(C, p["macd_fast"], p["macd_slow"], p["macd_smooth"])
    R = rsi(C, p["rsi_period"])

    def sign(x):
        return 1 if x > 0 else (-1 if x < 0 else 0)

    msb = p["macd_slope_bars"]
    min_bars = max(p["slow"] - 1, p["macd_slow"] + p["macd_smooth"], p["rsi_period"], p["slope_bars"], msb + 1)
    trend = 0
    suspect = 0          # 의심 중이면 그 방향 (상승추세 중 하락의심 = −1)
    out = []
    for i in range(n):
        rec = dict(regime=NAN, macd_line=mline[i], rsi=R[i], aligned=0, score=0, slow=0, fast=0,
                   trend_start=0, suspect_event=0, suspect_active=0, text="")
        if i < min_bars or math.isnan(R[i]):
            out.append(rec)
            continue
        s1 = sign(C[i] - sM[i])
        s2 = sign(sM[i] - sS[i]) if p["s2_mode"] == 1 else sign(sS[i] - sS[i - p["slope_bars"]])
        s3 = sign(mline[i])
        s4 = 0
        if p["use_rsi"]:
            s4 = 1 if R[i] >= 50 + p["score_rsi_band"] else (-1 if R[i] <= 50 - p["score_rsi_band"] else 0)
        f1 = 1 if (C[i] > sF[i] and sF[i] > sF[i - 1]) else (-1 if (C[i] < sF[i] and sF[i] < sF[i - 1]) else 0)
        f2 = 1 if all(mline[i - j] > mline[i - j - 1] for j in range(msb)) else (
            -1 if all(mline[i - j] < mline[i - j - 1] for j in range(msb)) else 0)
        S = s1 + s2 + s3 + s4
        F = f1 + f2

        start_up = F >= p["trend_start_fast"] and S >= p["trend_start_slow"]
        start_dn = F <= -p["trend_start_fast"] and S <= -p["trend_start_slow"]
        prev = trend
        if trend == 1:
            if start_dn:
                trend = -1
            elif S <= p["trend_end_slow"]:
                trend = -1 if p["direct_reverse"] and F <= -2 else 0   # TQ_Regime DirectReverse (2026-10-15)
        elif trend == -1:
            if start_up:
                trend = 1
            elif S >= -p["trend_end_slow"]:
                trend = 1 if p["direct_reverse"] and F >= 2 else 0
        else:
            if start_up:
                trend = 1
            elif start_dn:
                trend = -1
        ts = trend if trend != prev and trend != 0 else 0

        # 의심: 추세가 바뀌면 지운다. 추세 중 F가 반대로 SuspectFast에 닿은 첫 봉에 이벤트, F가 0 이상(하락추세는 0 이하)으로 돌아오면 해제
        ev = 0
        if trend != prev or trend == 0:
            suspect = 0
        if trend != 0:
            if suspect == 0 and F * trend <= -p["suspect_fast"]:
                suspect = -trend
                ev = suspect
            elif suspect != 0 and F * trend >= 0:
                suspect = 0

        rec.update(regime=2 * trend, slow=S, fast=F, score=S, trend_start=ts, suspect_event=ev, suspect_active=suspect,
                   text=f"S1{s1:+d} S2{s2:+d} S3{s3:+d} S4{s4:+d} | F1{f1:+d} F2{f2:+d}")
        out.append(rec)
    return out


def compute_regime(tb, p):
    """추세 봉 목록 tb → 봉마다 dict(regime, macd_line, rsi, aligned, score). TQ_Regime.OnBarUpdate와 같은 순서."""
    if p["mode"] == 3:
        return compute_regime_v3(tb, p)
    n = len(tb)
    H = [b[2] for b in tb]
    L = [b[3] for b in tb]
    C = [b[4] for b in tb]

    sF, sM, sS = sma(C, p["fast"]), sma(C, p["mid"]), sma(C, p["slow"])
    mline, _, hist = macd(C, p["macd_fast"], p["macd_slow"], p["macd_smooth"])
    R = rsi(C, p["rsi_period"])
    # RSI 플롯의 평균(RsiRelative). NT에서 RSI가 아직 없는 처음 몇 봉은 영향이 없을 만큼 앞이라 0으로 둔다
    rAvg = sma([0.0 if math.isnan(x) else x for x in R], p["rsi_center_bars"])

    # TQ_MacdTurn(…, HistBars, 14, 0): 히스토그램 HistBars봉 연속 증가·감소가 처음 확인된 봉
    cb = p["hist_bars"]
    up = [False] * n
    dn = [False] * n
    turn_up = [False] * n
    turn_dn = [False] * n
    for i in range(n):
        if i >= cb:
            iu = all(hist[i - j] > hist[i - j - 1] for j in range(cb))
            idn = all(hist[i - j] < hist[i - j - 1] for j in range(cb))
            up[i], dn[i] = iu, idn
        turn_up[i] = up[i] and not (i > 0 and up[i - 1])
        turn_dn[i] = dn[i] and not (i > 0 and dn[i - 1])

    # 스토캐스틱 (직접 계산)
    lo = rolling_min(L, p["stoch_k"])
    hi = rolling_max(H, p["stoch_k"])
    raw = []
    for i in range(n):
        rng = hi[i] - lo[i]
        raw.append((50.0 if i == 0 else raw[-1]) if rng == 0 else 100 * (C[i] - lo[i]) / rng)
    K = sma(raw, p["stoch_smooth"])
    D = sma(K, p["stoch_d"])

    P = p["pivot"]
    min_bars = max(p["slow"] - 1, p["div_bars"] + P, p["cross_filter"], p["hist_bars"],
                   p["rsi_lookback"] + p["rsi_center_bars"], p["suspect_evidence_bars"])

    suspect_up = suspect_dn = False
    last_dir = trend_dir = last_event_dir = 0
    last_event_bar = -1
    up_start = dn_start = up_end = dn_end = 0      # 시작·종료 조건의 연속 봉 수 (TrendConfirmBars / TrendExitBars)
    bear_evd = [0] * n                             # 의심 근거 비트: 1 다이버전스, 2 MACD 전환, 4 크로스, 8 가격 항목 (SuspectMinEvidence)
    bull_evd = [0] * n
    out = []
    for i in range(n):
        rec = dict(regime=NAN, macd_line=mline[i], rsi=R[i], aligned=0, score=0)
        if i < min_bars:
            out.append(rec)
            continue
        f, m, s = sF[i], sM[i], sS[i]
        ml, r0 = mline[i], R[i]
        k0, d0 = K[i], D[i]
        aligned = 1 if f > m > s else (-1 if f < m < s else 0)

        hist_dn, hist_up = turn_dn[i], turn_up[i]

        k_low = any(K[i - j] < p["cross_low"] for j in range(p["cross_filter"]))
        k_high = any(K[i - j] > p["cross_high"] for j in range(p["cross_filter"]))
        golden = K[i - 1] <= D[i - 1] and k0 > d0 and k_low
        dead = K[i - 1] >= D[i - 1] and k0 < d0 and k_high

        bear_div = bull_div = False
        if P <= 0:
            hi_i = lo_i = 1
            for j in range(2, p["div_bars"] + 1):
                if C[i - j] > C[i - hi_i]:
                    hi_i = j
                if C[i - j] < C[i - lo_i]:
                    lo_i = j
            bear_div = C[i] > C[i - hi_i] and r0 < R[i - hi_i]
            bull_div = C[i] < C[i - lo_i] and r0 > R[i - lo_i]
        else:
            hi_i = lo_i = -1
            for j in range(P, p["div_bars"] + 1):
                ph = all(H[i - j] > H[i - j + q] and H[i - j] > H[i - j - q] for q in range(1, P + 1))
                pl = all(L[i - j] < L[i - j + q] and L[i - j] < L[i - j - q] for q in range(1, P + 1))
                if ph and (hi_i < 0 or H[i - j] > H[i - hi_i]):
                    hi_i = j
                if pl and (lo_i < 0 or L[i - j] < L[i - lo_i]):
                    lo_i = j
            if hi_i >= 0:
                bear_div = H[i] > H[i - hi_i] and r0 < R[i - hi_i]
            if lo_i >= 0:
                bull_div = L[i] < L[i - lo_i] and r0 > R[i - lo_i]

        bull_ev = hist_up or golden or bull_div
        bear_ev = hist_dn or dead or bear_div
        if bull_ev != bear_ev:
            last_event_dir = 1 if bull_ev else -1
            last_event_bar = i

        h0, h1 = hist[i], hist[i - 1]
        c_ma = 1 if (C[i] > f and f > sF[i - 1]) else (-1 if (C[i] < f and f < sF[i - 1]) else 0)
        c_macd = 1 if (h0 > 0 and h0 >= h1) else (-1 if (h0 < 0 and h0 <= h1) else 0)
        c_rsi = 1 if r0 >= 50 + p["score_rsi_band"] else (-1 if r0 <= 50 - p["score_rsi_band"] else 0)
        c_sto = 1 if (k0 > d0 and k0 > 50) else (-1 if (k0 < d0 and k0 < 50) else 0)
        c_evt = last_event_dir if (p["score_event_bars"] > 0 and last_event_bar >= 0
                                   and i - last_event_bar < p["score_event_bars"]) else 0
        score = c_ma + c_macd + c_rsi + c_sto + c_evt

        # 시작·종료 조건 (TQ_Regime과 같음). 두 방식 모두 연속 봉 수를 채워야 바뀐다. 기본 1 / 1이면 그 봉의 조건 그대로
        if p["mode"] == 2:
            start_up = c_ma > 0 and score >= p["score_min"]
            start_dn = c_ma < 0 and score <= -p["score_min"]
            end_up = score <= p["score_exit"]
            end_dn = score >= -p["score_exit"]
        else:
            start_up = aligned > 0 and ml > 0
            start_dn = aligned < 0 and ml < 0
            end_up, end_dn = not start_up, not start_dn
        up_start = up_start + 1 if start_up else 0
        dn_start = dn_start + 1 if start_dn else 0
        up_end = up_end + 1 if end_up else 0
        dn_end = dn_end + 1 if end_dn else 0
        if trend_dir == 1 and up_end >= p["trend_exit_bars"]:
            trend_dir = 0
        if trend_dir == -1 and dn_end >= p["trend_exit_bars"]:
            trend_dir = 0
        if trend_dir != 1 and up_start >= p["trend_confirm_bars"]:
            trend_dir = 1
        elif trend_dir != -1 and dn_start >= p["trend_confirm_bars"]:
            trend_dir = -1
        up_cond, dn_cond = trend_dir == 1, trend_dir == -1

        # 의심의 근거 종류 수 (최근 suspect_evidence_bars봉)와 점수 상한
        bear_evd[i] = (1 if bear_div else 0) | (2 if hist_dn else 0) | (4 if dead else 0) | (8 if c_ma <= 0 else 0)
        bull_evd[i] = (1 if bull_div else 0) | (2 if hist_up else 0) | (4 if golden else 0) | (8 if c_ma >= 0 else 0)
        bear_bits = bull_bits = 0
        for j in range(min(p["suspect_evidence_bars"], i + 1)):
            bear_bits |= bear_evd[i - j]
            bull_bits |= bull_evd[i - j]
        bear_ok = bin(bear_bits).count("1") >= p["suspect_min_evidence"] and score <= p["suspect_max_score"]
        bull_ok = bin(bull_bits).count("1") >= p["suspect_min_evidence"] and score >= -p["suspect_max_score"]

        rsi_hi = rsi_lo = False
        for j in range(p["rsi_lookback"]):
            hl = rAvg[i - j] + p["rsi_offset"] if p["rsi_relative"] else p["rsi_high"]
            ll = rAvg[i - j] - p["rsi_offset"] if p["rsi_relative"] else p["rsi_low"]
            if R[i - j] >= hl:
                rsi_hi = True
            if R[i - j] <= ll:
                rsi_lo = True

        r = 0
        if up_cond:
            suspect_dn = False
            if rsi_hi and (bear_div or hist_dn or dead) and not (p["suspect_needs_price"] and c_ma > 0) and bear_ok:
                suspect_up = True
            elif suspect_up and p["suspect_return"] and (hist_up or golden):
                suspect_up = False
            r = 1 if suspect_up else 2
        elif dn_cond:
            suspect_up = False
            if rsi_lo and (bull_div or hist_up or golden) and not (p["suspect_needs_price"] and c_ma < 0) and bull_ok:
                suspect_dn = True
            elif suspect_dn and p["suspect_return"] and (hist_dn or dead):
                suspect_dn = False
            r = -1 if suspect_dn else -2
        else:
            suspect_up = suspect_dn = False
            if p["no_sideways"]:
                if last_dir == 0:
                    last_dir = 1 if C[i] >= s else -1
                r = last_dir
        if up_cond:
            last_dir = 1
        elif dn_cond:
            last_dir = -1

        rec.update(regime=r, aligned=aligned, score=score)
        out.append(rec)
    return out


def bar_suspect_events(rec):
    """추세 봉마다 의심 이벤트 (+1 상승의심 / −1 하락의심 / 0). mode=3은 판정이 낸 값, mode 1·2는 의심 상태(±1)로 들어간 봉."""
    ev = []
    prev = NAN
    for x in rec:
        if "suspect_event" in x:
            ev.append(x["suspect_event"])
        else:
            r = x["regime"]
            ev.append(-int(r) if (not math.isnan(r) and int(r) in (1, -1) and r != prev) else 0)
        prev = x["regime"]
    return ev


def regime_per_minute(bars1, p, cache):
    """1분봉마다 그 봉 마감에 전략이 보는 추세 값. 추세 봉은 1분봉보다 마감이 앞선 것 중 가장 최근 것 (spec 3장 1봉 지연).
    (추세 값 목록, 의심 이벤트 목록)을 돌려준다. 이벤트는 그 추세 봉 값을 처음 보는 1분봉에 하나만 둔다"""
    key = tuple(sorted(p.items()))
    if key in cache:
        return cache[key]
    tb = resample(bars1, p["bar"])
    rec = compute_regime(tb, p)
    bev = bar_suspect_events(rec)
    if p["bar"] == 1:
        res = [0 if math.isnan(x["regime"]) else int(x["regime"]) for x in rec]
        ev = list(bev)
    else:
        ends = [b[0] for b in tb]
        res, ev = [], []
        last_k = -1
        for b in bars1:
            k = bisect.bisect_left(ends, b[0]) - 1   # 마감 시각 < 1분봉 마감
            v = rec[k]["regime"] if k >= 0 else NAN
            res.append(0 if math.isnan(v) else int(v))
            ev.append(bev[k] if (k >= 0 and k != last_k) else 0)
            last_k = k
    cache[key] = (res, ev)
    return res, ev


def score_suspects(bars1, ev, args, minutes=30):
    """의심 이벤트 채점: 대회 시간 안의 이벤트마다 그 뒤 minutes분 동안 의심 방향으로 간 최대 폭이 반대 방향 최대 폭보다 크면 적중.
    (개수, 적중 수, 의심 방향 평균 폭, 반대 방향 평균 폭)"""
    C = [b[4] for b in bars1]
    H = [b[2] for b in bars1]
    L = [b[3] for b in bars1]
    T = [b[0] for b in bars1]
    n = hit = 0
    fav_sum = adv_sum = 0.0
    for i, d in enumerate(ev):
        if d == 0 or not in_window(T[i], args.start, args.end_all):
            continue
        fav = adv = 0.0
        for j in range(i + 1, len(bars1)):
            if (T[j] - T[i]) > timedelta(minutes=minutes):
                break
            fav = max(fav, (H[j] - C[i]) if d > 0 else (C[i] - L[j]))
            adv = max(adv, (C[i] - L[j]) if d > 0 else (H[j] - C[i]))
        n += 1
        hit += fav > adv
        fav_sum += fav
        adv_sum += adv
    return n, hit, (fav_sum / n if n else NAN), (adv_sum / n if n else NAN)

# ───────────────────────── 사후 정답 ─────────────────────────

def atr_per_minute(bars1, atr_bar=10, period=14):
    """1분봉마다 직전에 마감된 N분봉 ATR (정답의 크기 기준)."""
    tb = resample(bars1, atr_bar)
    a = atr(tb, period)
    ends = [b[0] for b in tb]
    out = []
    for b in bars1:
        k = bisect.bisect_left(ends, b[0]) - 1
        out.append(a[k] if k >= 0 else a[0])
    return out


def zigzag_legs(close, atr_m, rev_atr):
    """1분봉 종가 지그재그. 반대로 rev_atr × ATR 이상 되돌리면 꼭짓점 확정. [(시작 idx, 끝 idx, 방향)]"""
    n = len(close)
    if n < 2:
        return []
    piv = [0]
    trend = 0
    hi = lo = close[0]
    hi_i = lo_i = 0
    for i in range(1, n):
        c = close[i]
        th = rev_atr * atr_m[i]
        if trend == 0:
            if c > hi:
                hi, hi_i = c, i
            if c < lo:
                lo, lo_i = c, i
            if hi - lo >= th:
                if hi_i > lo_i:
                    trend = 1
                    piv[0] = lo_i
                else:
                    trend = -1
                    piv[0] = hi_i
        elif trend == 1:
            if c > hi:
                hi, hi_i = c, i
            elif hi - c >= th:
                piv.append(hi_i)
                trend, lo, lo_i = -1, c, i
        else:
            if c < lo:
                lo, lo_i = c, i
            elif c - lo >= th:
                piv.append(lo_i)
                trend, hi, hi_i = 1, c, i
    # 확정 안 된 마지막 다리도 넣는다 (사후 정답이므로)
    last = hi_i if trend == 1 else lo_i
    if trend != 0 and last != piv[-1]:
        piv.append(last)
    legs = []
    for a, b in zip(piv, piv[1:]):
        if b > a:
            legs.append((a, b, 1 if close[b] > close[a] else -1))
    return legs

# ───────────────────────── 대회 시간 ─────────────────────────

def hhmm(t):
    return t.hour * 100 + t.minute


def in_window(t, start, end):
    v = hhmm(t)
    return (start <= v or v <= end) if start > end else (start <= v <= end)


def session_key(t, start):
    """자정을 넘는 대회 구간을 하나로 묶는 날짜 (시작 시각 전의 새벽은 전날로)."""
    return (t - timedelta(days=1)).date() if hhmm(t) < start else t.date()

# ───────────────────────── 채점 ─────────────────────────

def direction_view(reg, view):
    """추세 값 → 방향. trend: ±2만 방향. organic: 유기 모드처럼 하락 전환 의심(+1)은 매도, 상승 전환 의심(−1)은 매수."""
    if view == "trend":
        return [1 if r == 2 else (-1 if r == -2 else 0) for r in reg]
    return [1 if r in (2, -1) else (-1 if r in (-2, 1) else 0) for r in reg]


def score_method(bars1, pred, legs_ok, args):
    """한 방식의 채점. legs_ok: 정답 추세 다리 [(시작, 끝, 방향, 크기)] (대회 시간으로 잘린 것)."""
    C = [b[4] for b in bars1]
    T = [b[0] for b in bars1]

    # 1) 다리 기준: 지연과 남은 몫
    lags, caps, missed = [], [], 0
    for a, b, d, size in legs_ok:
        hit = next((i for i in range(a, b + 1) if pred[i] == d), None)
        if hit is None:
            missed += 1
            caps.append(0.0)
            continue
        lags.append((T[hit] - T[a]).total_seconds() / 60)
        caps.append(max(0.0, (C[b] - C[hit]) * d / size))

    # 2) 추세 따라가기: 진입 시간대 안에서 방향이 바뀌면 그 봉 종가로 들어가고, 바뀌거나 종료 시각이면 나온다
    trades = []
    pos, entry = 0, 0.0
    for i in range(len(bars1)):
        t = T[i]
        inwin = in_window(t, args.start, args.end_all)
        if pos != 0 and (pred[i] != pos or not inwin or hhmm(t) == args.flatten
                         or (i + 1 < len(bars1) and (T[i + 1] - t) > timedelta(minutes=30))):
            trades.append((C[i] - entry) * pos)
            pos = 0
        if pos == 0 and pred[i] != 0 and in_window(t, args.start, args.entry_end) \
                and (i == 0 or pred[i - 1] != pred[i]):
            pos, entry = pred[i], C[i]
    wins = sum(1 for x in trades if x > 0)

    # 3) 분 단위: 대회 시간 안의 분마다 예측 방향과 정답 방향
    truth = [0] * len(bars1)
    for a, b, d, size in legs_ok:
        for i in range(a + 1, b + 1):
            truth[i] = d
    tot = agree = pred_tr = pred_ok = true_tr = covered = 0
    for i in range(len(bars1)):
        if not in_window(T[i], args.start, args.end_all):
            continue
        tot += 1
        agree += pred[i] == truth[i]
        if pred[i] != 0:
            pred_tr += 1
            pred_ok += pred[i] == truth[i]
        if truth[i] != 0:
            true_tr += 1
            covered += pred[i] == truth[i]

    lags_sorted = sorted(lags)
    return dict(
        legs=len(legs_ok), caught=len(legs_ok) - missed,
        lag_med=lags_sorted[len(lags_sorted) // 2] if lags_sorted else NAN,
        lag_avg=sum(lags) / len(lags) if lags else NAN,
        cap=100 * sum(caps) / len(caps) if caps else NAN,
        n_tr=len(trades), win=100 * wins / len(trades) if trades else NAN,
        pts=sum(trades), pts_avg=sum(trades) / len(trades) if trades else NAN,
        acc=100 * agree / tot if tot else NAN,
        prec=100 * pred_ok / pred_tr if pred_tr else NAN,
        recall=100 * covered / true_tr if true_tr else NAN,
    )


def truth_legs(bars1, args):
    """정답 추세 다리를 대회 시간(기본 21:30~23:30 KST)으로 잘라 돌려준다. 잘린 뒤에도 크기가 기준 이상인 것만."""
    C = [b[4] for b in bars1]
    T = [b[0] for b in bars1]
    atr_m = atr_per_minute(bars1, args.atr_bar)
    legs = zigzag_legs(C, atr_m, args.rev_atr)
    out = []
    for a, b, d in legs:
        idx = [i for i in range(a, b + 1) if in_window(T[i], args.start, args.end_all)]
        if len(idx) < 2:
            continue
        # 한 다리가 두 대회 구간에 걸치면 첫 구간만 (다리는 보통 몇 시간 안이라 드묾)
        sk = session_key(T[idx[0]], args.start)
        idx = [i for i in idx if session_key(T[i], args.start) == sk]
        # 대회 시간 안 구간에서 다시 그 방향의 최저(최고) → 최고(최저)로 맞춘다
        a2 = idx[0]
        b2 = max(idx, key=lambda i: C[i] * d)
        if b2 <= a2:
            continue
        size = (C[b2] - C[a2]) * d
        if size >= args.trend_atr * atr_m[a]:
            out.append((a2, b2, d, size))
    return out

# ───────────────────────── 로그와 대조 (재현 검증) ─────────────────────────

HDR_NUM = {
    "bar": r"추세봉=(\d+)분", "mode": r"추세방식=(\d)", "score_min": r"최소점수=(-?\d+)", "score_rsi_band": r"RSI폭=([\d.]+)",
    "score_event_bars": r"점수사건봉수=(\d+)", "rsi_center_bars": r"중심봉수=(\d+)", "rsi_offset": r"폭=([\d.]+) 추세종료",
    "score_exit": r"추세종료점수=(-?\d+)", "rsi_lookback": r"의심RSI봉수=(\d+)", "div_bars": r"다이버전스=(\d+)봉",
    "hist_bars": r"히스토그램=(\d+)봉",
    "suspect_max_score": r"의심점수상한=(\d+)", "suspect_min_evidence": r"의심근거수=(\d+)",
    "suspect_evidence_bars": r"의심근거수=\d+\((\d+)봉\)", "trend_confirm_bars": r"추세확정봉=(\d+)",
    "trend_exit_bars": r"추세종료봉=(\d+)",
}
HDR_BOOL = {
    "rsi_relative": r"RSI상대기준=(True|False)", "no_sideways": r"횡보없음=(True|False)",
    "suspect_needs_price": r"의심가격확인=(True|False)", "suspect_return": r"의심복귀=(True|False)",
}
REG_LINE = re.compile(r"(\d+)분봉 마감 추세=(\S+) .*?배열=(-?\d) MACD선=(-?[\d.]+) RSI=([\d.]+) 종가=([\d.]+) 점수=(-?\d)")
SYM = {"▲": 2, "△": 1, "◆": 0, "▽": -1, "▼": -2}


# v3 전략 로그 (TQ_Strategy v3): 실행 머리줄에 "버전=v3", 추세 봉 줄은 "N분봉 마감 추세=… S=+3 F=-1 (…) … 종가=… 이평=… MACD선=… RSI=…"
HDR_V3_NUM = {
    "bar": r"추세봉=(\d+)분", "s2_mode": r"S2방식=(\d)", "slope_bars": r"기울기봉=(\d+)", "score_rsi_band": r"RSI폭=([\d.]+)",
    "macd_slope_bars": r"MACD기울기봉=(\d+)", "trend_start_fast": r"시작F=(\d+)", "trend_start_slow": r"시작S=(\d+)",
    "trend_end_slow": r"종료S=(-?\d+)", "suspect_fast": r"의심F=(\d+)",
}
REG_LINE_V3 = re.compile(r"(\d+)분봉 마감 추세=(\S+) .*?S=([+-]?\d+) F=([+-]?\d+) .*?종가=([\d.]+) .*?MACD선=(-?[\d.]+) RSI=([\d.]+)")
SYM_V3 = {"▲": 2, "◆": 0, "▼": -2}


def header_params_v3(h):
    p = dict(DEFAULTS)
    p["mode"] = 3
    for k, rx in HDR_V3_NUM.items():
        m = re.search(rx, h)
        if m:
            p[k] = type(DEFAULTS[k])(float(m.group(1))) if isinstance(DEFAULTS[k], int) else float(m.group(1))
    m = re.search(r"RSI항목=(True|False)", h)
    if m:
        p["use_rsi"] = m.group(1) == "True"
    m = re.search(r"MA=(\d+)/(\d+)/(\d+)", h)
    if m:
        p["fast"], p["mid"], p["slow"] = (int(x) for x in m.groups())
    return p


def header_params(h):
    if re.search(r"버전=v[345]", h):
        return header_params_v3(h)
    if "추세방식=" not in h:
        return None
    p = dict(DEFAULTS)
    for k, rx in HDR_NUM.items():
        m = re.search(rx, h)
        if m:
            p[k] = type(DEFAULTS[k])(float(m.group(1))) if isinstance(DEFAULTS[k], int) else float(m.group(1))
    for k, rx in HDR_BOOL.items():
        m = re.search(rx, h)
        if m:
            p[k] = m.group(1) == "True"
    m = re.search(r"MA=(\d+)/(\d+)/(\d+)", h)
    if m:
        p["fast"], p["mid"], p["slow"] = (int(x) for x in m.groups())
    m = re.search(r"RSI=(\d+)/(\d+)", h)
    if m:
        p["rsi_high"], p["rsi_low"] = float(m.group(1)), float(m.group(2))
    m = re.search(r"다이버전스=\d+봉\(고점폭(\d+)\)", h)
    p["pivot"] = int(m.group(1)) if m else 0
    return p


def validate(bars1, log_path, contract):
    runs = []
    cur = None
    with open(log_path, encoding="utf-8-sig") as f:
        for line in f:
            if line.startswith("===="):
                c = re.search(r"\[([^\]]+)\]", line)
                cur = dict(contract=c.group(1) if c else "", p=header_params(line), rows=[])
                runs.append(cur)
                continue
            if cur is None or cur["p"] is None:
                continue
            if cur["p"]["mode"] == 3:
                m = REG_LINE_V3.search(line)
                if m:
                    # v3 줄: (시각, 봉, 추세, S, MACD선, RSI, 종가, F) — 아래 비교에서 배열 자리에 S, 점수 자리에 F를 둔다
                    cur["rows"].append((parse_log_time(line), int(m.group(1)), SYM_V3.get(m.group(2)[0]), int(m.group(3)),
                                        float(m.group(6)), float(m.group(7)), float(m.group(5)), int(m.group(4))))
                continue
            m = REG_LINE.search(line)
            if m:
                cur["rows"].append((parse_log_time(line), int(m.group(1)), SYM.get(m.group(2)[0]), int(m.group(3)),
                                    float(m.group(4)), float(m.group(5)), float(m.group(6)), int(m.group(7))))
    tbc = {}
    stats = dict(rows=0, close=0, regime=0, aligned=0, score=0, macd=0, rsi=0)
    bad = []
    for run in runs:
        if not run["rows"] or (contract and contract not in run["contract"]):
            continue
        p = run["p"]
        if p["mode"] not in (1, 2, 3):
            continue
        key = tuple(sorted(p.items()))
        if key not in tbc:
            tb = resample(bars1, p["bar"])
            tbc[key] = ({b[0]: i for i, b in enumerate(tb)}, tb, compute_regime(tb, p))
        idx, tb, rec = tbc[key]
        for t, bm, reg, al, ml, rs, cl, sc in run["rows"]:
            i = idx.get(t)
            if i is None:
                continue
            r = rec[i]
            stats["rows"] += 1
            if p["mode"] == 3:
                # v3: 배열 칸 = S, 점수 칸 = F
                ok = dict(close=abs(tb[i][4] - cl) < 1e-6, regime=r["regime"] == reg, aligned=r["slow"] == al,
                          score=r["fast"] == sc, macd=abs(r["macd_line"] - ml) <= 0.011, rsi=abs(r["rsi"] - rs) <= 0.051)
            else:
                ok = dict(close=abs(tb[i][4] - cl) < 1e-6, regime=r["regime"] == reg, aligned=r["aligned"] == al,
                          score=r["score"] == sc, macd=abs(r["macd_line"] - ml) <= 0.011, rsi=abs(r["rsi"] - rs) <= 0.051)
            for k, v in ok.items():
                stats[k] += v
            if not all(ok.values()) and len(bad) < 15:
                bad.append((t, run["contract"], dict(log=(reg, al, ml, rs, cl, sc)),
                            dict(mine=(r["regime"], r["aligned"], round(r["macd_line"], 2), round(r["rsi"], 1), tb[i][4], r["score"]))))
    print("\n[검증] 로그의 추세 봉 줄과 재현값 비교")
    if stats["rows"] == 0:
        print("  비교할 줄이 없음 (데이터 기간·계약월이 로그와 겹치는지 확인)")
        return
    n = stats["rows"]
    print("  비교한 줄 {0}개: 종가 {1}, 추세 {2}, 배열 {3}, 점수 {4}, MACD선 {5}, RSI {6} 일치".format(
        n, *(f"{100 * stats[k] / n:.1f}%" for k in ("close", "regime", "aligned", "score", "macd", "rsi"))))
    for b in bad:
        print("  다름:", b[0], b[1], "로그(추세,배열,MACD,RSI,종가,점수)=", b[2]["log"], "재현=", b[3]["mine"])

# ───────────────────────── 실행 ─────────────────────────

PRESETS = [
    ("문서 정의 10분 (기본)", "mode=1,bar=10"),
    ("문서 정의 3분", "mode=1,bar=3"),
    ("점수 10분", "mode=2,bar=10"),
    ("점수 3분", "mode=2,bar=3"),
    ("예전 유기 기준 세트 (점수 3분 + RSI 상대 26/6)", "mode=2,bar=3,rsirel=1,center=26,offset=6"),
    ("유기 기준 세트 10/14 (RSI 상대 14/6 + 의심 점수 상한 2)", "mode=2,bar=3,rsirel=1,center=14,offset=6,maxscore=2"),
]

# v3 지시서 3-4의 비교 목록: mode=2 10분(score-v1 기본), mode=2 3분(유기 기준), mode=3 3분 {Fast 6/8, Mid 20, Slow 60} × {S2Mode 1/2} × {S4 켬/끔}
PRESETS_V3 = [
    ("mode=2 10분 (score-v1 기본)", "mode=2,bar=10"),
    ("mode=2 3분 (유기 기준)", "mode=2,bar=3,rsirel=1,center=26,offset=6"),
] + [
    (f"mode=3 3분 {fast}/20/60 S2={s2} RSI={'켬' if r else '끔'}",
     f"mode=3,bar=3,fast={fast},mid=20,slow=60,s2={s2},userrsi={r}")
    for fast in (6, 8) for s2 in (1, 2) for r in (1, 0)
]


def parse_cfg(s):
    p = dict(DEFAULTS)
    for kv in filter(None, (x.strip() for x in s.split(","))):
        k, v = kv.split("=")
        k = ALIASES.get(k, k)
        if k not in DEFAULTS:
            raise SystemExit(f"모르는 설정: {k} (쓸 수 있는 것: {', '.join(sorted(DEFAULTS))})")
        d = DEFAULTS[k]
        p[k] = (v.lower() in ("1", "true", "on")) if isinstance(d, bool) else type(d)(float(v))
    return p


def fmt(x, nd=0):
    return "-" if isinstance(x, float) and math.isnan(x) else f"{x:.{nd}f}"


def main():
    ap = argparse.ArgumentParser(description="TQ 추세 판단 채점기")
    ap.add_argument("--data", nargs="+", help="NinjaTrader 내보내기 1분봉 파일 (UTC). 여러 개면 합친다")
    ap.add_argument("--log-bars", help="데이터 대신 TQ_log의 1분봉으로 시험 (구간이 짧아 결과는 참고용)")
    ap.add_argument("--validate", help="TQ_log 파일과 재현값 비교")
    ap.add_argument("--contract", default="", help="검증할 계약월 (예: SEP26)")
    ap.add_argument("--cfg", action="append", default=[], help='비교할 설정. 예: "이름:mode=2,bar=3,rsirel=1". 여러 번 쓸 수 있음. 없으면 기본 5개')
    ap.add_argument("--rev-atr", type=float, default=1.5, help="정답 지그재그: 반대로 ATR × 이 값 이상 되돌리면 다리 끝 (기본 1.5)")
    ap.add_argument("--trend-atr", type=float, default=3.0, help="정답: 다리 크기가 ATR × 이 값 이상이면 추세 (기본 3)")
    ap.add_argument("--atr-bar", type=int, default=10, help="정답 기준 ATR(14)의 봉 (기본 10분)")
    ap.add_argument("--start", type=int, default=2130, help="대회 시작 HHMM (KST)")
    ap.add_argument("--entry-end", type=int, default=2315, help="진입 마감 HHMM")
    ap.add_argument("--flatten", type=int, default=2327, help="종료 청산 HHMM")
    ap.add_argument("--end", type=int, default=2330, help="대회 끝 HHMM (분 단위 채점 범위)")
    ap.add_argument("--all-hours", action="store_true", help="대회 시간 대신 하루 전체를 채점")
    ap.add_argument("--report", help="결과를 마크다운 파일로도 저장")
    ap.add_argument("--v3", action="store_true", help="설정을 주지 않았을 때 v3 비교 목록(PRESETS_V3, v3 지시서 3-4)을 쓴다")
    args = ap.parse_args()
    args.end_all = args.end
    if args.all_hours:
        args.start, args.end_all, args.entry_end, args.flatten = 0, 2359, 2359, -1

    if args.data:
        bars1 = []
        for path in args.data:
            bars1 += load_nt_export(path)
        bars1.sort(key=lambda b: b[0])
    elif args.log_bars:
        bars1 = load_tq_log_bars(args.log_bars)
    else:
        raise SystemExit("--data 또는 --log-bars가 필요함")
    if not bars1:
        raise SystemExit("1분봉을 하나도 읽지 못함")
    sessions = sorted({session_key(b[0], args.start) for b in bars1 if in_window(b[0], args.start, args.end_all)})
    print(f"1분봉 {len(bars1)}개: {bars1[0][0]} ~ {bars1[-1][0]} (KST), 채점 구간 {len(sessions)}개")

    if args.validate:
        validate(bars1, args.validate, args.contract)

    cfgs = []
    for c in args.cfg:
        name, _, s = c.partition(":") if ":" in c else (c, "", c)
        cfgs.append((name, parse_cfg(s)))
    if not cfgs:
        cfgs = [(n, parse_cfg(s)) for n, s in (PRESETS_V3 if args.v3 else PRESETS)]

    legs = truth_legs(bars1, args)
    if legs:
        sizes = sorted(x[3] for x in legs)
        print(f"정답 추세 다리 {len(legs)}개 (상승 {sum(1 for x in legs if x[2] > 0)}, 하락 {sum(1 for x in legs if x[2] < 0)}), "
              f"크기 중간값 {sizes[len(sizes) // 2]:.0f}pt, 기준: 되돌림 ATR×{args.rev_atr}, 크기 ATR×{args.trend_atr} ({args.atr_bar}분봉 ATR14)")
    else:
        print("정답 추세 다리가 없음 (기간이 짧거나 기준이 너무 큼)")

    cache = {}
    lines = []
    head = ("| 방식 | 보기 | 잡은 다리 | 지연 중간(분) | 지연 평균(분) | 잡은 뒤 남은 몫(%) | 따라가기 거래 | 승률(%) | 합계(pt) | 거래당(pt) | 분 정확도(%) | 추세라 한 분 중 맞음(%) | 추세 분 중 잡음(%) | 의심 이벤트 | 30분 적중(%) | 의심 방향/반대(pt) |\n"
            "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    lines.append(head)
    for name, p in cfgs:
        reg, ev = regime_per_minute(bars1, p, cache)
        sn, sh, sfav, sadv = score_suspects(bars1, ev, args)
        # v3(mode=3)에는 의심 '상태'가 없어 유기 방향 보기는 추세만과 같으므로 한 줄만 낸다
        views = (("trend", "추세만"),) if p["mode"] == 3 else (("trend", "추세만"), ("organic", "유기 방향"))
        for view, vname in views:
            r = score_method(bars1, direction_view(reg, view), legs, args)
            lines.append(f"| {name} | {vname} | {r['caught']}/{r['legs']} | {fmt(r['lag_med'])} | {fmt(r['lag_avg'], 1)} | {fmt(r['cap'])} | "
                         f"{r['n_tr']} | {fmt(r['win'])} | {fmt(r['pts'])} | {fmt(r['pts_avg'], 1)} | {fmt(r['acc'])} | {fmt(r['prec'])} | {fmt(r['recall'])} | "
                         f"{sn} | {fmt(100 * sh / sn if sn else NAN)} | {fmt(sfav, 1)} / {fmt(sadv, 1)} |")
    print()
    print("\n".join(lines))
    print("\n읽는 법: 지연 = 정답 다리 시작에서 그 방향 추세로 처음 판정한 1분봉까지. 남은 몫 = 판정 시점부터 다리 끝까지의 움직임 ÷ 다리 크기 (놓친 다리는 0).")
    print("따라가기 = 진입 시간대에 판정이 그 방향으로 바뀐 봉 종가로 들어가 판정이 바뀌거나 종료 시각에 나온 포인트 (수수료·익절·손절 없음).")
    print("'추세만' = ±2만 방향으로 봄. '유기 방향' = 하락 전환 의심은 매도, 상승 전환 의심은 매수 쪽으로 봄 (spec 5-2).")

    if args.report:
        with open(args.report, "w", encoding="utf-8") as f:
            f.write(f"# 추세 판단 채점 결과\n\n- 데이터: {bars1[0][0]} ~ {bars1[-1][0]} (KST), 채점 구간 {len(sessions)}개\n")
            f.write(f"- 정답: 1분봉 종가 지그재그, 되돌림 ATR×{args.rev_atr}, 추세 크기 ATR×{args.trend_atr} ({args.atr_bar}분봉 ATR14), 다리 {len(legs)}개\n\n")
            f.write("\n".join(lines) + "\n")
        print(f"\n보고서 저장: {args.report}")


if __name__ == "__main__":
    main()
