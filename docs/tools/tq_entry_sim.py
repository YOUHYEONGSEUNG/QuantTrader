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
TICK, SLIP, PT_USD = 0.25, 0.5, 2.0


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


def zone_ok(t):
    m = mins(t)
    return (21 * 60 + 35 <= m <= 23 * 60 + 15) and not (22 * 60 + 25 <= m <= 22 * 60 + 32)


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
    if r is None or not zone_ok(b["t"]):
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
            if p.get("maxdist") and d * (b["c"] - edge) > p["maxdist"] * a: continue
            if p.get("confirm"):  # 직전 봉도 그 범위 밖에서 마감 (범위는 직전 봉 기준)
                if i < n + 1: continue
                hi1 = max(x["h"] for x in bars[i - n - 1:i - 1]); lo1 = min(x["l"] for x in bars[i - n - 1:i - 1])
                if d * (bars[i - 1]["c"] - (hi1 if d > 0 else lo1)) <= 0: continue
            return d
        return 0
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
    if rule.kind == "combo":
        return opposite(Rule("bo", "breakout", **rule.p), i, bars, ctx, d)
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
                pos = open_pos(d, i, bars, qty_full)
    if pos is not None:
        close_all(pos, bars[-1]["c"], bars[-1]["t"], "END", trades)
    return trades


def open_pos(d, i, bars, qty_full):
    b = bars[i]; a = b["atr"]
    entry = b["c"] + d * SLIP
    swing = min(x["l"] for x in bars[max(0, i - 4):i + 1]) if d > 0 else max(x["h"] for x in bars[max(0, i - 4):i + 1])
    dist = d * (entry - swing) + TICK
    dist = max(1.5 * a, min(3 * a, dist)); dist = max(dist, 8.0)
    stop = entry - d * dist
    qty = qty_full // 2 if mins(b["t"]) < 22 * 60 + 25 else qty_full
    q1 = qty * 33 // 100; q2 = qty * 33 // 100
    chunks = [dict(q=q1, band=2), dict(q=q2, band=3), dict(q=qty - q1 - q2, band=None)]
    tp2done = False
    room1 = d * (b["ema"] + d * 2 * a - b["c"]); room2 = d * (b["ema"] + d * 3 * a - b["c"])
    if room2 < 0.5 * dist:
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
    if why == "STOP":   # 손절 주문 체결: 손익으로 나눠 센다 (본절·트레일링으로 이익이면 TRAIL, 손실이면 STOP)
        why = "STOP" if pos["pnl"] < 0 else "BE/TRAIL"
    trades.append(dict(t0=pos["t0"], t1=t, d=pos["d"], qty=pos["qty"], risk=pos["risk"], usd=pos["pnl"] * PT_USD,
                       r=pos["pnl"] / (pos["risk"] * pos["qty"]), reason=why, mfe=pos["mfe"] / pos["risk"]))


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
        if c["q"] <= 0 or c["band"] is None: continue
        tp = prev["ema"] + d * c["band"] * prev["atr"]
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
    a = ap.parse_args()
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
    out = [f"# 진입 규칙 비교 (같은 청산 엔진)\n\n날짜 {len(days)}일: {', '.join(sorted(days))}\n",
           "| 진입 규칙 | 거래 | 하루 | 승률 | 합계 R | 합계 $ | 7월 $ | 8·9월 $ | 진 날 | 손절 | 큰 움직임 방향으로 들어감 | 지연 중간(분) |",
           "|---|---|---|---|---|---|---|---|---|---|---|---|"]
    detail = {}
    for rule in rules:
        allt, bymon, loss_days, caught, delays, nlegs = [], defaultdict(float), 0, 0, [], 0
        for ds, (bars, reg) in sorted(days.items()):
            tr = run_day(rule, bars)
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
    out.append("\n청산 사유별 (각 규칙)\n")
    for name, allt in detail.items():
        c = defaultdict(lambda: [0, 0.0])
        for x in allt:
            c[x["reason"]][0] += 1; c[x["reason"]][1] += x["usd"]
        out.append(f"- {name}: " + ", ".join(f"{k} {v[0]}건 {v[1]:,.0f}$" for k, v in sorted(c.items())))
    open(a.report, "w", encoding="utf-8").write("\n".join(out))


if __name__ == "__main__":
    main()
