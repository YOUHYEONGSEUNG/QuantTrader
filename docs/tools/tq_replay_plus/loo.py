# 지금 로직에서 하나씩 끄기 (수량 30·불타기 없음으로 고정해 로직만 비교): 처음 보는 8월 13일 / 고를 때 쓴 45일
import sys, os, json, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ind import Bars
from common import SETS, bars, DEFAULT, TP, DATA
from combo19 import Combo20
AUGB = Bars(json.load(open(os.path.join(DATA, "bars_aug.json"))))
def days(b, a, z, kw, tp):
    st = Combo20(b, tparams=TP(**tp), **kw); st.p.MinEntries = 4; d = co.defaultdict(float)
    for t in st.run():
        if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:29": d[t["t0"][:10]] += t["usd"] - (t["qty"] + t.get("addq", 0)) * 2
    return {x: d.get(x, 0.0) for x in sorted(set(r[:10] for r in b.t if a <= r[:10] <= z and r[11:16] == "22:00"))}
def evalc(kw, tp):
    aug = days(AUGB, "2026-08-03", "2026-08-31", kw, tp); r45 = {}
    for m, (f, a, z) in SETS.items(): r45.update(days(bars(m), a, z, kw, tp))
    return aug, r45
if __name__ == "__main__":
    BASE = {**DEFAULT, "qty": 30, "add_q": 0}
    OFFS = [("흔들리는 장 눌림만", dict(chop_er=None)), ("추세 지속 진입", dict(cont_k=None)), ("지속 진입 속도 조건", dict(cont_vmax=None)),
            ("5.4 SMA20 보류·대기", dict(sma_block=None, sma_wait=None)), ("이미 지난 조각 안 삼", dict(ext_skip=False)), ("21:45 전 25계약", dict(early_q=None))]
    TPOFF = [("손절 최소 1.5 → 1.0", dict(MinStopAtr=1.0)), ("T1 3 → 5", dict(T1Window=5))]
    a0, r0 = evalc(BASE, {}); A0, R0 = sum(a0.values()), sum(r0.values())
    print(f"지금 로직(30계약, 불타기 없음): 8월 {A0:+,.0f} (이긴 날 {sum(v>0 for v in a0.values())}/13, 최악 {min(a0.values()):+,.0f}) | 45일 {R0:+,.0f}")
    print("| 끄는 것 | 8월 차이 | 45일 차이 | 판단 |\n|---|---|---|---|")
    for name, k in OFFS + TPOFF:
        kw = {**BASE, **(k if (name, k) in OFFS else {})}; tp = k if (name, k) in TPOFF else {}
        a, r = evalc(kw, tp); da, dr = sum(a.values()) - A0, sum(r.values()) - R0
        verdict = "둘 다 도움 → 둠" if da < 0 and dr < 0 else "둘 다 손해 → 뺌" if da > 0 and dr > 0 else ("8월엔 손해 (과적합 의심)" if da > 0 else "8월엔 도움")
        print(f"| {name} | {-da:+,.0f} | {-dr:+,.0f} | {verdict} |", flush=True)
