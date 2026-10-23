# 오늘 바꾼 것을 하나씩 쌓아 가며, 처음 보는 8월(8/4~8/31, 13일)과 고를 때 쓴 45일에서 비교
import sys, os, json, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ind import Bars
import common
from common import SETS, bars, DEFAULT, TP, DATA
from combo19 import Combo20
AUG = ("bars_aug.json", "2026-08-03", "2026-08-31")
_aug = Bars(json.load(open(os.path.join(DATA, AUG[0]))))
COST = 2.0
def days(b, a, z, kw, tp):
    st = Combo20(b, tparams=TP(**tp), **kw); st.p.MinEntries = 4; d = co.defaultdict(float); n = 0
    for t in st.run():
        if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:29":
            d[t["t0"][:10]] += t["usd"] - (t["qty"] + t.get("addq", 0)) * COST; n += 1
    ds = sorted(set(r[:10] for r in b.t if a <= r[:10] <= z and r[11:16] == "22:00"))
    return {x: d.get(x, 0.0) for x in ds}, n
OFF = dict(chop_er=None, cont_k=None, cont_vmax=None, qty=30, add_q=0, add_room=None, sma_block=None, sma_wait=None, ext_skip=False, early_q=None)
STEPS = [("0 10/22 밤 3차 (출발점: 30계약)", {}, dict(MinStopAtr=1.0, T1Window=5)),
         ("1 + 흔들리는 장 눌림만 (ChopMaxEr 0.15)", dict(chop_er=0.15), {}),
         ("2 + 추세 지속 진입 8봉", dict(cont_k=8), {}),
         ("3 + 지속 진입 속도 < 0.2", dict(cont_vmax=0.2), {}),
         ("4 + 50계약 + 2차 뒤 불타기 30", dict(qty=50, add_q=30), {}),
         ("5 + 불타기 여유 1 ATR", dict(add_room=1), {}),
         ("6 + 5.4 SMA20 보류·10봉 대기", dict(sma_block="short", sma_wait=10), {}),
         ("7 + 이미 지난 조각 안 삼", dict(ext_skip=True), {}),
         ("8 + 21:45 전 25계약", dict(early_q=("21:45", 25)), {}),
         ("9 + 손절 최소 1.5 ATR", {}, dict(MinStopAtr=1.5)),
         ("10 + T1 3봉 (= 지금)", {}, dict(T1Window=3))]
kw = {**DEFAULT, **OFF}; tp = {}
print("| 단계 | 8월 13일 (처음 봄) | 이긴 날 | 최악의 날 | 거래 | 45일 (고를 때 씀) | 이긴 날 |\n|---|---|---|---|---|---|---|")
for name, k, t in STEPS:
    kw.update(k); tp.update(t)
    a, n = days(_aug, AUG[1], AUG[2], dict(kw), dict(tp))
    tot45 = 0; w45 = 0
    for m, (f, s, e) in SETS.items():
        r, _ = days(bars(m), s, e, dict(kw), dict(tp)); tot45 += sum(r.values()); w45 += sum(v > 0 for v in r.values())
    print(f"| {name} | {sum(a.values()):+,.0f} | {sum(v > 0 for v in a.values())}/{len(a)} | {min(a.values()):+,.0f} | {n} | {tot45:+,.0f} | {w45}/45 |", flush=True)
