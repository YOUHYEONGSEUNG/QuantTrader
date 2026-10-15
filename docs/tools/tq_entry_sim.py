#!/usr/bin/env python3
"""진입 규칙 비교 시뮬레이터 (v4 설계 검증용, docs/v4-design-draft.md).

전략 로그(TQ_log_v3_종목.txt)의 진입 봉 줄(1분봉 시고저종)과 추세 봉 줄(3분봉 S·F·이평)을 그대로 읽어,
진입 규칙만 바꿔 가며 같은 청산 엔진으로 거래를 재현한다. 매매 규칙(spec)은 바꾸지 않는다.

청산 엔진 = v3 추세 포지션 규칙 (spec v3 4-3, 4-4, 4-5):
  손절: 신호 봉 포함 5봉 저점 − 1틱, 1분 ATR × [1.5, 3]으로 자르고 최소 8pt
  조각 33 / 33 / 나머지: 1차 = 중심선(EMA26) + 2 ATR, 2차 = + 3 ATR (직전 봉 밴드, 봉 진행 중 지정가)
  밴드까지 남은 거리 < 예상 손절 × 0.5면 지난 밴드로 봄 (1차 → 2차, 2차 → 트레일링)
  1차 뒤: 이익 ≥ 손절 거리 × 0.5면 평단, 아니면 1차 체결가 − 1.5 ATR / 이익 보호: 최대 이익 ≥ 0.8R이면 평단
  2차 뒤: 진입 뒤 최고가 − 2.5 ATR 트레일링, 추세 봉 종가 < 중기선이면 나머지 청산
  의심: F가 반대로 2가 되면 5봉 안에 종가가 직전 5봉 저점 아래면 남은 수량 50% (S ≤ 1이면 전량)
  반대 신호(규칙마다 정의) → 전량. 23:27 봉 마감 → 전량
봉 안 순서는 보수적으로: 손절 먼저, 그다음 익절. 체결 미끄러짐 0.5pt.
한계: 1분 EMA·ATR은 로그 첫 봉(21:2x)부터 쌓아 21:35 무렵은 실제와 조금 다르다.

사용:
  py docs/tools/tq_entry_sim.py --log "…\\TQ_log_v3_MNQ.txt" --dates 2026-07-15,2026-07-16 --report out.md
"""
import argparse, re
from collections import defaultdict
from datetime import datetime, timedelta

TS = re.compile(r"^\[(\d{4}-\d{2}-\d{2}) (오전|오후) (\d+):(\d{2}):(\d{2})\]\[[^\]]+\]\[TQ_Strategy\] (.*)$")
BAR = re.compile(r"^추세=(-?\d) S=([+-]?\d) F=([+-]?\d) .*?구간=(\S+(?: \S+)*?) 후보 .*시고저종=([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)")
REG = re.compile(r"^3분봉 마감 추세=\S+ \S+ S=([+-]?\d) F=([+-]?\d) .*?종가=([\d.]+) 이평=([\d.]+)/([\d.]+)/([\d.]+)")
TICK, SLIP, PT_USD, MIN_STOP_TICKS = 0.25, 0.5, 2.0, 32
SIZING, RISK_USD, MAX_QTY = "fixed", 1500.0, 30   # --sizing risk: 계약 = 위험 한도 ÷ (손절 거리 × 계약당 가치), 최대 MAX_QTY   # MNQ 기본. 골드(MGC)는 --tick 0.1 --ptusd 10 --slip 0.2


def ts(d, ap, h, m, s):
    h = int(h)
    if ap == "오후" and h != 12: h += 12
    if ap == "오전" and h == 12: h = 0
    return datetime.strptime(d, "%Y-%m-%d") + timedelta(hours=h, minutes=int(m), seconds=int(s))


def load(log_path, dates):
    segs, cur = [], None
    for raw in open(log_path, encoding="utf-8", errors="replace"):
        if raw.startswith("===="):
            cur = []; segs.append(cur); continue
        m = TS.match(raw.rstrip("\n"))
        if m and cur is not None:
            cur.append((ts(*m.group(1, 2, 3, 4, 5)), m.group(6)))
    days = {}
    for ds in dates:
        day = datetime.strptime(ds, "%Y-%m-%d").date()
        best, bn = None, 0
        for s in segs:
            n = sum(1 for t, x in s if t.date() == day and x.startswith("추세="))
            if n > bn and any(t.date() == day and t.hour == 23 and t.minute >= 27 for t, _ in s):
                best, bn = s, n
        if best is None or bn < 100:
            continue
        bars, reg, sb, sr = [], [], set(), set()
        for t, x in best:
            if t.date() != day: continue
            m = BAR.match(x)
            if m and t not in sb:
                sb.add(t)
                bars.append(dict(t=t, st=int(m.group(1)), zone=m.group(4), o=float(m.group(5)), h=float(m.group(6)), l=float(m.group(7)), c=float(m.group(8))))
            r = REG.match(x)
            if r and t not in sr:
                sr.add(t)
                reg.append(dict(t=t, S=int(r.group(1)), F=int(r.group(2)), c=float(r.group(3)), fast=float(r.group(4)), mid=float(r.group(5))))
        bars.sort(key=lambda b: b["t"]); reg.sort(key=lambda r: r["t"])
        # 1분 ATR(14, Wilder)·EMA26
        ema, atr, pc = None, None, None
        for b in bars:
            ema = b["c"] if ema is None else ema + (b["c"] - ema) * 2 / 27
            tr = b["h"] - b["l"] if pc is None else max(b["h"] - b["l"], abs(b["h"] - pc), abs(b["l"] - pc))
            atr = tr if atr is None else (atr * 13 + tr) / 14
            pc = b["c"]; b["ema"], b["atr"] = ema, atr
        # 진입 봉 마감 때 보이는 추세 봉 값 = 그보다 먼저 마감된 것 (1분 지연)
        j = -1
        for b in bars:
            while j + 1 < len(reg) and reg[j + 1]["t"] < b["t"]:
                j += 1
            b["reg"] = reg[j] if j >= 0 else None
            b["regi"] = j
        days[ds] = (bars, reg)
    return days


def mins(t):
    return t.hour * 60 + t.minute


def zone_ok(t, end=23 * 60 + 15):
    m = mins(t)
    return (21 * 60 + 35 <= m <= end) and not (22 * 60 + 25 <= m <= 22 * 60 + 32)


def in_window(t):
    m = mins(t)
    return 21 * 60 + 30 <= m <= 23 * 60 + 27


class Rule:
    """진입 규칙. signal(i, bars, ctx) → +1 / −1 / 0. opposite(i, bars, ctx, d) → 보유 중 전량 청산할 반대 신호."""
    def __init__(self, name, kind, **p):
        self.name, self.kind, self.p = name, kind, p


def box_of(bars):
    bb = [b for b in bars if 21 * 60 + 31 <= mins(b["t"]) <= 22 * 60 + 30]
    return (max(b["h"] for b in bb), min(b["l"] for b in bb)) if len(bb) > 30 else (None, None)


def signal(rule, i, bars, ctx):
    b = bars[i]; r = b["reg"]; p = rule.p
    if r is None or not zone_ok(b["t"], p.get("end", 23 * 60 + 15)):
        return 0
    S, F, a = r["S"], r["F"], b["atr"]
    if rule.kind == "breakout":
        n = p["n"]
        if i < n: return 0
        hi = max(x["h"] for x in bars[i - n:i]); lo = min(x["l"] for x in bars[i - n:i])
        for d, edge in ((1, hi), (-1, lo)):
            if d * (b["c"] - edge) <= 0: continue
            if d * F < p["fmin"]: continue
            if p.get("sblock") and d * S <= -p["sblock"]: continue
            if p.get("sabsmax") is not None and abs(S) > p["sabsmax"]: continue   # 중립 돌파: |S|가 이 값 이하일 때만
            if p.get("maxdist") and d * (b["c"] - edge) > p["maxdist"] * a: continue
            # 채널 밴드 기준: extmax = 종가가 중심선 ± extmax ATR을 넘었으면 늦은 진입으로 봄, minband = 그 방향 ±minband ATR 밖이어야 함
            if p.get("extmax") and d * (b["c"] - (b["ema"] + d * p["extmax"] * a)) > 0: continue
            if p.get("minband") and d * (b["c"] - (b["ema"] + d * p["minband"] * a)) <= 0: continue
            if p.get("confirm"):  # 직전 봉도 그 범위 밖에서 마감 (범위는 직전 봉 기준)
                if i < n + 1: continue
                hi1 = max(x["h"] for x in bars[i - n - 1:i - 1]); lo1 = min(x["l"] for x in bars[i - n - 1:i - 1])
                if d * (bars[i - 1]["c"] - (hi1 if d > 0 else lo1)) <= 0: continue
            return d
        return 0
    if rule.kind == "pullback":
        # 돌파(B1 조건)로 방향을 정하고, 그 뒤 hold봉 안에 저가가 중심선 + band × ATR(매수)까지 되돌았다가
        # 종가가 다시 그 선 위로 마감하고 F ≥ pfmin이면 진입. 반대 돌파가 나오면 방향을 지운다. withbo면 돌파 봉에서도 진입
        bo = Rule("bo", "breakout", **{k: v for k, v in p.items() if k in ("n", "fmin", "sblock")})
        dbo = signal(bo, i, bars, ctx)
        if dbo:
            ctx["pb_dir"], ctx["pb_i"] = dbo, i
            if p.get("withbo"):
                return dbo
            return 0
        d = ctx.get("pb_dir", 0)
        if not d or i - ctx["pb_i"] > p.get("hold", 30):
            return 0
        line = b["ema"] + d * p.get("band", 1.0) * a
        touched = (b["l"] <= line) if d > 0 else (b["h"] >= line)
        if touched and d * (b["c"] - line) > 0 and d * F >= p.get("pfmin", 1) and not (p.get("sblock") and d * S <= -p["sblock"]):
            ctx["pb_dir"] = 0
            return d
        return 0
    if rule.kind == "v3n":  # v3 E1+E3 먼저, 아니면 중립 돌파(|S| ≤ sabsmax, F ±fmin, N봉 돌파)
        d = signal(Rule("v3", "v3"), i, bars, ctx)
        if d:
            ctx["last_kind"] = "v3"; return d
        d = signal(Rule("bo", "breakout", n=p["n"], fmin=p["fmin"], sabsmax=p["sabsmax"]), i, bars, ctx)
        if d:
            ctx["last_kind"] = "neutral"
        return d
    if rule.kind == "combo":  # 장전 박스 돌파(E3) 먼저, 아니면 N분 돌파
        e3 = Rule("e3", "v3", e1=False)
        d = signal(e3, i, bars, ctx)
        if d: return d
        return signal(Rule("bo", "breakout", **rule.p), i, bars, ctx)
    if rule.kind == "v3":  # v3의 E1(추세 시작) + E3(장전 박스 돌파)만, 로그의 추세 판정을 그대로 씀
        st, prev = b["st"], bars[i - 1]["st"] if i else 0
        if p.get("e1", True) and st != 0 and st != prev and d_ok(st, S, F):
            return st
        if p.get("e3", True) and 22 * 60 + 33 <= mins(b["t"]) < 23 * 60:
            hi, lo = ctx["box"]
            if hi is None: return 0
            for d, edge in ((1, hi), (-1, lo)):
                if d * (b["c"] - edge) > 0 and d * S >= 1 and d * F >= 1 and ctx["orb"][d] < 2 and ctx["orb_armed"][d]:
                    ctx["orb"][d] += 1; ctx["orb_armed"][d] = False
                    return d
        return 0
    return 0


def d_ok(d, S, F):
    return d * F >= 0


def opposite(rule, i, bars, ctx, d):
    b = bars[i]
    if rule.kind == "breakout":
        r = b["reg"]
        if r is None: return False
        n = rule.p["n"]
        if i < n: return False
        edge = min(x["l"] for x in bars[i - n:i]) if d > 0 else max(x["h"] for x in bars[i - n:i])
        return -d * (b["c"] - edge) > 0 and -d * r["F"] >= rule.p["fmin"]
    if rule.kind == "v3":
        return b["st"] == -d
    if rule.kind == "v3n":   # v3로 들어간 포지션은 반대 추세 판정, 중립 돌파 포지션은 반대 돌파로 청산
        if ctx.get("pos_kind") == "neutral":
            return opposite(Rule("bo", "breakout", n=rule.p["n"], fmin=rule.p["fmin"]), i, bars, ctx, d)
        return b["st"] == -d
    if rule.kind in ("combo", "pullback"):
        return opposite(Rule("bo", "breakout", **{k: v for k, v in rule.p.items() if k in ("n", "fmin")}), i, bars, ctx, d)
    return False


def run_day(rule, bars, qty_full=30):
    trades = []
    pos = None
    ctx = {"box": box_of(bars), "orb": {1: 0, -1: 0}, "orb_armed": {1: True, -1: True}}
    for i, b in enumerate(bars):
        if not in_window(b["t"]):
            continue
        hi, lo = ctx["box"]
        if hi is not None and mins(b["t"]) > 22 * 60 + 30:
            if b["c"] <= hi: ctx["orb_armed"][1] = True
            if b["c"] >= lo: ctx["orb_armed"][-1] = True
        if pos is not None:
            pos = step(pos, i, bars, rule, ctx, trades)
        if pos is None and mins(b["t"]) < 23 * 60 + 27:
            d = signal(rule, i, bars, ctx)
            if d and not ctx.get("exit_bar") == i:
                pos = open_pos(d, i, bars, qty_full, rule.p)
                ctx["pos_kind"] = ctx.get("last_kind", "")
                pos["kind"] = ctx["pos_kind"]
    if pos is not None:
        close_all(pos, bars[-1]["c"], bars[-1]["t"], "END", trades)
    return trades


def open_pos(d, i, bars, qty_full, rp=None):
    rp = rp or {}
    b = bars[i]; a = b["atr"]
    entry = b["c"] + d * SLIP
    swing = min(x["l"] for x in bars[max(0, i - 4):i + 1]) if d > 0 else max(x["h"] for x in bars[max(0, i - 4):i + 1])
    dist = d * (entry - swing) + TICK
    dist = max(1.5 * a, min(3 * a, dist)); dist = max(dist, MIN_STOP_TICKS * TICK)
    stop = entry - d * dist
    qty = qty_full // 2 if mins(b["t"]) < 22 * 60 + 25 else qty_full
    if SIZING == "risk":
        qty = max(1, min(MAX_QTY, int(RISK_USD / (dist * PT_USD))))
        if mins(b["t"]) < 22 * 60 + 25: qty = max(1, qty // 2)
    q1 = qty * 33 // 100; q2 = qty * 33 // 100
    chunks = [dict(q=q1, band=2), dict(q=q2, band=3), dict(q=qty - q1 - q2, band=None)]
    tp2done = False
    room1 = d * (b["ema"] + d * 2 * a - b["c"]); room2 = d * (b["ema"] + d * 3 * a - b["c"])
    if rp.get("tp") == "r":   # 익절을 진입가 기준 R 배수로 (밴드를 안 씀)
        chunks[0]["band"] = chunks[1]["band"] = None
        chunks[0]["px"] = entry + d * rp.get("tp1r", 1.0) * dist
        chunks[1]["px"] = entry + d * rp.get("tp2r", 2.0) * dist
    elif room2 < 0.5 * dist:
        chunks[0]["band"] = chunks[1]["band"] = None; tp2done = True
    elif room1 < 0.5 * dist:
        chunks[0]["band"] = 3
    return dict(d=d, i0=i, t0=b["t"], entry=entry, risk=dist, stop=stop, chunks=chunks, qty=qty, pnl=0.0, mfe=0.0,
                tp1=False, tp2=tp2done, be=False, ext=b["c"], sus_wait=0, sus_done=-1, reasons=[])


def close_chunks(pos, chs, price, why):
    for c in chs:
        if c["q"] > 0:
            pos["pnl"] += pos["d"] * (price - pos["entry"]) * c["q"]
            c["q"] = 0
    pos["reasons"].append(why)


def close_all(pos, price, t, why, trades):
    close_chunks(pos, pos["chunks"], price - pos["d"] * SLIP, why)
    kind = pos.get("kind", "")
    if why == "STOP":   # 손절 주문 체결: 손익으로 나눠 센다 (본절·트레일링으로 이익이면 TRAIL, 손실이면 STOP)
        why = "STOP" if pos["pnl"] < 0 else "BE/TRAIL"
    trades.append(dict(t0=pos["t0"], t1=t, d=pos["d"], qty=pos["qty"], risk=pos["risk"], usd=pos["pnl"] * PT_USD,
                       r=pos["pnl"] / (pos["risk"] * pos["qty"]), reason=why, mfe=pos["mfe"] / pos["risk"], cell=kind))


def step(pos, i, bars, rule, ctx, trades):
    b = bars[i]; d = pos["d"]; a = b["atr"]; prev = bars[i - 1]
    # 1) 손절 (봉 안, 먼저)
    hit_stop = (b["l"] <= pos["stop"]) if d > 0 else (b["h"] >= pos["stop"])
    if hit_stop:
        px = min(b["o"], pos["stop"]) if d > 0 else max(b["o"], pos["stop"])
        close_all(pos, px + d * SLIP, b["t"], "STOP", trades)
        ctx["exit_bar"] = i
        return None
    # 2) 밴드 익절 (직전 봉 밴드, 봉 안 지정가)
    for c in pos["chunks"]:
        if c["q"] <= 0 or (c["band"] is None and "px" not in c): continue
        tp = c["px"] if "px" in c else prev["ema"] + d * c["band"] * prev["atr"]
        if (b["h"] >= tp) if d > 0 else (b["l"] <= tp):
            pos["pnl"] += d * (tp - pos["entry"]) * c["q"]; c["q"] = 0
            if c["band"] == 2 or (c is pos["chunks"][0]):
                if not pos["tp1"]:
                    pos["tp1"] = True
                    gain = d * (tp - pos["entry"])
                    ns = pos["entry"] if gain >= 0.5 * pos["risk"] else tp - d * 1.5 * a
                    if d * (ns - pos["stop"]) > 0: pos["stop"] = ns; pos["be"] = d * (ns - pos["entry"]) >= 0
            if c is pos["chunks"][1]:
                pos["tp2"] = True
    if all(c["q"] <= 0 for c in pos["chunks"]):
        close_all(pos, b["c"], b["t"], "TP", trades); return None
    # 3) 봉 마감 판단
    pos["mfe"] = max(pos["mfe"], d * ((b["h"] if d > 0 else b["l"]) - pos["entry"]))
    pos["ext"] = max(pos["ext"], b["h"]) if d > 0 else min(pos["ext"], b["l"])
    if mins(b["t"]) >= 23 * 60 + 27:
        close_all(pos, b["c"], b["t"], "FLATTEN", trades); return None
    if not pos["tp1"] and not pos["be"] and pos["mfe"] >= 0.8 * pos["risk"]:
        if d * (pos["entry"] - pos["stop"]) > 0: pos["stop"] = pos["entry"]; pos["be"] = True
    if pos["tp2"]:
        ns = pos["ext"] - d * 2.5 * a
        if d * (ns - pos["stop"]) > 0: pos["stop"] = ns; pos["be"] = True
    if opposite(rule, i, bars, ctx, d):
        close_all(pos, b["c"], b["t"], "OPP", trades); ctx["exit_bar"] = i; return None
    r = b["reg"]
    if r is not None:
        # 의심: 새 추세 봉에서 F가 반대로 2 → 5봉 동안 가격 확인
        if b["regi"] != pos["sus_done"] and d * r["F"] <= -2 and pos["sus_wait"] == 0 and (i == 0 or bars[i - 1]["regi"] != b["regi"]):
            pos["sus_wait"] = 5; pos["sus_done"] = b["regi"]
        if pos["sus_wait"] > 0:
            sw = min(x["l"] for x in bars[max(0, i - 5):i]) if d > 0 else max(x["h"] for x in bars[max(0, i - 5):i])
            if d * (b["c"] - sw) < 0:
                pos["sus_wait"] = 0
                if d * r["S"] <= 1:
                    close_all(pos, b["c"], b["t"], "SUSPECT_ALL", trades); return None
                live = [c for c in pos["chunks"] if c["q"] > 0]
                close_chunks(pos, live[::-1][:max(1, len(live) // 2)], b["c"] - d * SLIP, "SUSPECT")
                if all(c["q"] <= 0 for c in pos["chunks"]):
                    close_all(pos, b["c"], b["t"], "SUSPECT", trades); return None
            else:
                pos["sus_wait"] -= 1
        # 마지막 조각: 2차 뒤 추세 봉 종가가 중기선 반대편
        if pos["tp2"] and d * (r["c"] - r["mid"]) < 0:
            close_all(pos, b["c"], b["t"], "LAST_MA", trades); return None
    return pos


# ───────────────────────── v5 상태 엔진 (S × F 격자) ─────────────────────────
# 상태: F가 ±2일 때만 방향이 있다(d = F의 부호). S가 반대로 ±2 이상 = "반전", S가 같은 쪽 ±2 이상 = "이어짐", 그 사이 = "중립".
# 상태마다 수량 비중과 익절·손절 성향(profile)이 다르다. 보유 중에는 새 추세 봉마다 상태를 다시 보고 손절·청산을 조정한다.
PROFILES = {
    # 손절 ATR [최소, 최대], 1·2차 익절 R, 1·2차 비율, 트레일링 ATR
    "wide":  dict(smin=1.5, smax=2.5, tp1=1.5, tp2=3.0, q1=0.33, q2=0.33, trail=2.5),
    "mid":   dict(smin=1.5, smax=2.5, tp1=1.0, tp2=2.5, q1=0.33, q2=0.33, trail=2.0),
    "tight": dict(smin=1.0, smax=2.0, tp1=1.0, tp2=2.0, q1=0.5, q2=0.5, trail=1.5),
}


def cell_of(r, p):
    if r is None or abs(r["F"]) < p.get("fneed", 2):
        return None, 0
    d = 1 if r["F"] > 0 else -1
    if d * r["S"] <= -2: return "반전", d
    if d * r["S"] >= 2:  return "이어짐", d
    return "중립", d


def run_day_engine(rule, bars):
    p = rule.p
    cells = p["cells"]            # {"반전": (비중, profile), ...}; 없는 칸은 진입 안 함
    trades, pos, last_regi_entry = [], None, None
    for i, b in enumerate(bars):
        if not in_window(b["t"]):
            continue
        a = b["atr"]
        new_reg = i > 0 and b["regi"] != bars[i - 1]["regi"]
        if pos is not None:
            pos = engine_step(pos, i, bars, p, trades, new_reg)
        if pos is not None or mins(b["t"]) >= 23 * 60 + 27 or not zone_ok(b["t"], p.get("end", 23 * 60 + 15)):
            continue
        cell, d = cell_of(b["reg"], p)
        if cell not in cells:
            continue
        # 진입 시점: now = 상태가 켜진 추세 봉을 처음 본 1분봉 / boN = 그 방향 직전 N봉 돌파
        trig = p.get("trigger", "now")
        if trig == "now":
            ok = new_reg and b["regi"] != last_regi_entry
        else:
            n = int(trig[2:])
            if i < n: continue
            edge = max(x["h"] for x in bars[i - n:i]) if d > 0 else min(x["l"] for x in bars[i - n:i])
            ok = d * (b["c"] - edge) > 0
        if not ok:
            continue
        w, prof = cells[cell]
        pr = PROFILES[prof]
        entry = b["c"] + d * SLIP
        swing = min(x["l"] for x in bars[max(0, i - 4):i + 1]) if d > 0 else max(x["h"] for x in bars[max(0, i - 4):i + 1])
        dist = d * (entry - swing) + TICK
        dist = max(pr["smin"] * a, min(pr["smax"] * a, dist)); dist = max(dist, MIN_STOP_TICKS * TICK)
        risk_usd = RISK_USD * w * (0.5 if mins(b["t"]) < 22 * 60 + 25 else 1.0)
        qty = max(1, min(MAX_QTY, int(risk_usd / (dist * PT_USD))))
        q1 = max(1, int(qty * pr["q1"])) if qty >= 3 else 1
        q2 = max(0, int(qty * pr["q2"])) if qty >= 3 else 0
        chunks = [dict(q=q1, px=entry + d * pr["tp1"] * dist), dict(q=q2, px=entry + d * pr["tp2"] * dist), dict(q=qty - q1 - q2, px=None)]
        pos = dict(d=d, t0=b["t"], entry=entry, risk=dist, stop=entry - d * dist, chunks=chunks, qty=qty, pnl=0.0, mfe=0.0,
                   tp1=False, tp2=False, be=False, ext=b["c"], cell=cell, prof=pr, reasons=[])
        last_regi_entry = b["regi"]
    if pos is not None:
        close_all(pos, bars[-1]["c"], bars[-1]["t"], "END", trades)
    for x in trades:
        x.setdefault("cell", "")
    return trades


def engine_step(pos, i, bars, p, trades, new_reg):
    b = bars[i]; d = pos["d"]; a = b["atr"]; pr = pos["prof"]
    if (b["l"] <= pos["stop"]) if d > 0 else (b["h"] >= pos["stop"]):
        px = min(b["o"], pos["stop"]) if d > 0 else max(b["o"], pos["stop"])
        close_all(pos, px + d * SLIP, b["t"], "STOP", trades); trades[-1]["cell"] = pos["cell"]; return None
    for k, c in enumerate(pos["chunks"][:2]):
        if c["q"] > 0 and c["px"] is not None and ((b["h"] >= c["px"]) if d > 0 else (b["l"] <= c["px"])):
            pos["pnl"] += d * (c["px"] - pos["entry"]) * c["q"]; c["q"] = 0
            if k == 0:
                pos["tp1"] = True
                if d * (pos["entry"] - pos["stop"]) > 0: pos["stop"] = pos["entry"]; pos["be"] = True
            else:
                pos["tp2"] = True
    if all(c["q"] <= 0 for c in pos["chunks"]):
        close_all(pos, b["c"], b["t"], "TP", trades); trades[-1]["cell"] = pos["cell"]; return None
    pos["mfe"] = max(pos["mfe"], d * ((b["h"] if d > 0 else b["l"]) - pos["entry"]))
    pos["ext"] = max(pos["ext"], b["h"]) if d > 0 else min(pos["ext"], b["l"])
    m = mins(b["t"])
    if m >= 23 * 60 + 27:
        close_all(pos, b["c"], b["t"], "FLATTEN", trades); trades[-1]["cell"] = pos["cell"]; return None
    def raise_stop(ns):
        if d * (ns - pos["stop"]) > 0: pos["stop"] = ns
    if pos["mfe"] >= 0.8 * pos["risk"]:
        raise_stop(pos["entry"])
    if pos["tp1"]:   # 1차 뒤: 트레일링 (상태가 같은 방향으로 강해졌으면 넓게)
        r = b["reg"]; k = pr["trail"] + (0.5 if r is not None and d * r["S"] >= 2 and d * r["F"] >= 2 else 0.0)
        raise_stop(pos["ext"] - d * k * a)
    if p.get("late_be", True) and m >= 23 * 60 + 20 and d * (b["c"] - pos["entry"]) > 0:
        raise_stop(pos["entry"])
    if new_reg and b["reg"] is not None:
        F = b["reg"]["F"]
        if d * F <= -2 and p.get("flip_exit", True):   # 상태가 반대로 뒤집힘 → 전량
            close_all(pos, b["c"] - d * SLIP, b["t"], "CTX_FLIP", trades); trades[-1]["cell"] = pos["cell"]; return None
        if d * F <= 0 and not pos["tp1"] and p.get("weak_tighten", True):   # 힘이 빠짐 → 손절을 진입가 − 0.5R로
            raise_stop(pos["entry"] - d * 0.5 * pos["risk"])
    return pos


def legs_of(bars):
    win = [b for b in bars if in_window(b["t"])]
    if not win: return win, []
    a_med = sorted(b["atr"] for b in win)[len(win) // 2]
    rev, size = 4.5 * a_med, 9 * a_med
    cs = [b["c"] for b in win]
    legs, piv, ext, d = [], 0, 0, 0
    for i in range(1, len(cs)):
        if d == 0:
            if cs[i] - cs[piv] >= rev: d, ext = 1, i
            elif cs[piv] - cs[i] >= rev: d, ext = -1, i
            continue
        if d * (cs[i] - cs[ext]) > 0: ext = i
        elif d * (cs[ext] - cs[i]) >= rev:
            legs.append((piv, ext, d)); piv, ext, d = ext, i, -d
    if d: legs.append((piv, ext, d))
    return win, [(win[a]["t"], win[b]["t"], dd, dd * (cs[b] - cs[a])) for a, b, dd in legs if dd * (cs[b] - cs[a]) >= size]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True)
    ap.add_argument("--dates", required=True)
    ap.add_argument("--report", required=True)
    ap.add_argument("--set", default="entry", help="entry = 진입 규칙 비교(기본), v4 = v4 기본값 주변의 익절·마감 시각·밴드 거르기 비교")
    ap.add_argument("--tick", type=float, default=0.25)
    ap.add_argument("--ptusd", type=float, default=2.0)
    ap.add_argument("--slip", type=float, default=0.5)
    ap.add_argument("--sizing", default="fixed", help="fixed = 30계약(장전 15), risk = 위험 한도 기준")
    ap.add_argument("--risk", type=float, default=1500.0)
    a = ap.parse_args()
    global TICK, PT_USD, SLIP, SIZING, RISK_USD
    TICK, PT_USD, SLIP, SIZING, RISK_USD = a.tick, a.ptusd, a.slip, a.sizing, a.risk
    dates = a.dates.split(",")
    days = load(a.log, dates)
    rules = [
        Rule("v3 E1+E3 (로그 추세 판정)", "v3"),
        Rule("v3 E3만", "v3", e1=False),
    ]
    for n in (15, 30):
        for fmin in (1, 2):
            for sblock in (2, 3):
                rules.append(Rule(f"돌파 {n}분 F≥{fmin} S≤−{sblock} 금지", "breakout", n=n, fmin=fmin, sblock=sblock))
    for n in (10, 20):
        rules.append(Rule(f"돌파 {n}분 F≥2 S≤−2 금지", "breakout", n=n, fmin=2, sblock=2))
    rules.append(Rule("돌파 15분 F≥2 S≤−2 금지 + 2봉 확인", "breakout", n=15, fmin=2, sblock=2, confirm=True))
    rules.append(Rule("돌파 15분 F≥2 금지 없음", "breakout", n=15, fmin=2, sblock=0))
    rules.append(Rule("박스 돌파 + 돌파 15분 F≥2 S≤−3 금지", "combo", n=15, fmin=2, sblock=3))
    for n in (15, 30):
        rules.append(Rule(f"돌파 {n}분 F≥1 S≤−2 금지 + 거리 ≤ 2.5 ATR", "breakout", n=n, fmin=1, sblock=2, maxdist=2.5))
        rules.append(Rule(f"돌파 {n}분 F≥1 S≤−2 금지 + 2봉 확인", "breakout", n=n, fmin=1, sblock=2, confirm=True))
        rules.append(Rule(f"돌파 {n}분 F≥1 S≤−2 금지 + 거리 2.5 + 2봉 확인", "breakout", n=n, fmin=1, sblock=2, maxdist=2.5, confirm=True))
    if a.set == "v4":
        base = dict(n=15, fmin=2, sblock=3)
        E = lambda hh, mm: hh * 60 + mm
        rules = [Rule("v3 E1+E3 (로그 추세 판정)", "v3"), Rule("v4 기본 (15봉 F≥2 S3, 밴드 익절, 23:15)", "breakout", **base)]
        for end in ((23, 0), (23, 20), (23, 25)):
            rules.append(Rule(f"v4 + 진입 마감 {end[0]}:{end[1]:02d}", "breakout", **dict(base, end=E(*end))))
        for t1, t2 in ((0.75, 1.5), (1.0, 2.0), (1.0, 3.0), (1.5, 3.0)):
            rules.append(Rule(f"v4 + 익절 R 단위 {t1}R / {t2}R", "breakout", **dict(base, tp="r", tp1r=t1, tp2r=t2)))
        for ext in (2.0, 3.0):
            rules.append(Rule(f"v4 + 종가가 ±{ext}배 밴드 밖이면 안 함", "breakout", **dict(base, extmax=ext)))
        rules.append(Rule("v4 + 종가가 ±1배 밴드 밖이어야 함", "breakout", **dict(base, minband=1.0)))
        rules.append(Rule("v4 + ±3배 밖 안 함 + 익절 1R / 2R", "breakout", **dict(base, extmax=3.0, tp="r", tp1r=1.0, tp2r=2.0)))
        rules.append(Rule("v4 F≥1 + 익절 1R / 2R", "breakout", **dict(base, fmin=1, tp="r", tp1r=1.0, tp2r=2.0)))
        for band in (1.0, 0.0):
            rules.append(Rule(f"돌파로 방향 → ±{band:g}배 선 되돌림 진입 (30봉 안, F≥1)", "pullback", **dict(base, band=band, hold=30, pfmin=1)))
            rules.append(Rule(f"돌파 진입 + ±{band:g}배 선 되돌림 진입", "pullback", **dict(base, band=band, hold=30, pfmin=1, withbo=True)))
    if a.set == "v3n":
        rules = [Rule("v3 E1+E3", "v3")]
        for n in (10, 15, 20):
            rules.append(Rule(f"v3 + 중립 돌파 {n}봉 (|S|≤1, F±2)", "v3n", n=n, fmin=2, sabsmax=1))
        rules.append(Rule("v3 + 중립 돌파 15봉 (|S|≤0, F±2)", "v3n", n=15, fmin=2, sabsmax=0))
        rules.append(Rule("v3 + 중립 돌파 15봉 (|S|≤1, F±1)", "v3n", n=15, fmin=1, sabsmax=1))
        rules.append(Rule("중립 돌파 15봉만 (|S|≤1, F±2)", "breakout", n=15, fmin=2, sabsmax=1))
    if a.set == "v5":
        R, N, C = "반전", "중립", "이어짐"
        rules = [Rule("v3 E1+E3 (로그 추세 판정)", "v3"), Rule("v4 기본 (15봉 F≥2 S3)", "breakout", n=15, fmin=2, sblock=3)]
        base = {R: (1.0, "wide"), N: (0.75, "mid"), C: (0.5, "tight")}
        for trig in ("now", "bo5", "bo15"):
            rules.append(Rule(f"엔진: 세 칸 다, 진입 {trig}", "engine", cells=base, trigger=trig))
        rules.append(Rule("엔진: 반전만, 진입 now", "engine", cells={R: (1.0, "wide")}, trigger="now"))
        rules.append(Rule("엔진: 반전만, 진입 bo5", "engine", cells={R: (1.0, "wide")}, trigger="bo5"))
        rules.append(Rule("엔진: 반전+중립, 진입 bo5", "engine", cells={R: (1.0, "wide"), N: (0.75, "mid")}, trigger="bo5"))
        rules.append(Rule("엔진: 세 칸 다, bo5, 뒤집힘 청산 끔", "engine", cells=base, trigger="bo5", flip_exit=False))
        rules.append(Rule("엔진: 세 칸 다, bo5, 힘 빠짐 조임 끔", "engine", cells=base, trigger="bo5", weak_tighten=False))
        rules.append(Rule("엔진: 세 칸 다, bo5, 모두 wide", "engine", cells={R: (1.0, "wide"), N: (0.75, "wide"), C: (0.5, "wide")}, trigger="bo5"))
        rules.append(Rule("엔진: 세 칸 다, bo5, 마감 23:25", "engine", cells=base, trigger="bo5", end=23 * 60 + 25))
    out = [f"# 진입 규칙 비교 (같은 청산 엔진)\n\n날짜 {len(days)}일: {', '.join(sorted(days))}\n",
           "| 진입 규칙 | 거래 | 하루 | 승률 | 합계 R | 합계 $ | 7월 $ | 8·9월 $ | 진 날 | 손절 | 큰 움직임 방향으로 들어감 | 지연 중간(분) |",
           "|---|---|---|---|---|---|---|---|---|---|---|---|"]
    detail = {}
    for rule in rules:
        allt, bymon, loss_days, caught, delays, nlegs = [], defaultdict(float), 0, 0, [], 0
        for ds, (bars, reg) in sorted(days.items()):
            tr = run_day_engine(rule, bars) if rule.kind == "engine" else run_day(rule, bars)
            allt += [dict(x, ds=ds) for x in tr]
            usd = sum(x["usd"] for x in tr)
            bymon["7월" if ds[5:7] == "07" else "8·9월"] += usd
            loss_days += usd < 0
            win, legs = legs_of(bars)
            for t0, t1, dd, mv in legs:
                nlegs += 1
                first = next((x for x in tr if x["d"] == dd and t0 <= x["t0"] <= t1), None)
                if first:
                    caught += 1; delays.append(int((first["t0"] - t0).total_seconds() // 60))
        n = len(allt)
        w = sum(1 for x in allt if x["usd"] > 0)
        dl = sorted(delays)
        out.append(f"| {rule.name} | {n} | {n/max(1,len(days)):.1f} | {100*w/max(1,n):.0f}% | {sum(x['r'] for x in allt):+.1f} | {sum(x['usd'] for x in allt):,.0f} | "
                   f"{bymon['7월']:,.0f} | {bymon['8·9월']:,.0f} | {loss_days}/{len(days)} | {sum(1 for x in allt if x['reason']=='STOP')} | {caught}/{nlegs} | {dl[len(dl)//2] if dl else '-'} |")
        detail[rule.name] = allt
    out.append("\n상태 칸별 (엔진 규칙)\n")
    for name, allt in detail.items():
        if not any(x.get("cell") for x in allt): continue
        c = defaultdict(lambda: [0, 0.0, 0.0])
        for x in allt:
            c[x.get("cell", "")][0] += 1; c[x.get("cell", "")][1] += x["usd"]; c[x.get("cell", "")][2] += x["r"]
        out.append(f"- {name}: " + ", ".join(f"{k} {v[0]}건 {v[2]:+.1f}R {v[1]:,.0f}$" for k, v in sorted(c.items())))
    out.append("\n청산 사유별 (각 규칙)\n")
    for name, allt in detail.items():
        c = defaultdict(lambda: [0, 0.0])
        for x in allt:
            c[x["reason"]][0] += 1; c[x["reason"]][1] += x["usd"]
        out.append(f"- {name}: " + ", ".join(f"{k} {v[0]}건 {v[1]:,.0f}$" for k, v in sorted(c.items())))
    open(a.report, "w", encoding="utf-8").write("\n".join(out))


if __name__ == "__main__":
    main()
