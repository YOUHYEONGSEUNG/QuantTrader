# 실제 45일 + 모의 흔들리는 날 90일(실제 흔들리는 날 18일의 대회 구간 1분봉을 10봉 덩어리로 섞어 다른 날 대회 구간에 끼움)을 같이 돌림.
# 사용: py -X utf8 chop_test.py 설정.json   (설정.json = [["이름", {Combo21 인자}], ...])
import sys, json, random, os, math, collections as co, statistics as st
T = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, T)
import common
from common import SETS, DATA, day_moves, DEFAULT, TP, bars
from ind import Bars
from combo21 import Combo21
raw = {m: json.load(open(os.path.join(DATA, f))) for m, (f, a, z) in SETS.items()}
dm = day_moves(); chop = sorted(d for d, s in dm.items() if abs(s["c"] - s["o"]) / s["path"] < 0.1)
pool = []
for m, rows in raw.items():
    for d in chop:
        ks = [k for k, r in enumerate(rows) if r[0][:10] == d and "21:30" <= r[0][11:16] <= "23:30"]
        if not ks: continue
        rel = [(rows[k][1] - rows[k-1][4], rows[k][2] - rows[k-1][4], rows[k][3] - rows[k-1][4], rows[k][4] - rows[k-1][4]) for k in ks]
        for s in range(0, len(rel) - 9, 5): pool.append(rel[s:s+10])
def make(seed):
    rnd = random.Random(seed); out = {}
    for m, rows in raw.items():
        a, z = SETS[m][1], SETS[m][2]; new = [list(r) for r in rows]; off = 0.0
        days = set(r[0][:10] for r in rows if a <= r[0][:10] <= z); k = 0
        while k < len(new):
            d = new[k][0][:10]; hm = new[k][0][11:16]
            if off: new[k][1:] = [x + off for x in new[k][1:]]
            if d in days and hm == "21:30":
                ks = [j for j in range(k, len(new)) if new[j][0][:10] == d and new[j][0][11:16] <= "23:30"]
                seq = []
                while len(seq) < len(ks): seq += rnd.choice(pool)
                pc = new[k - 1][4]; real_end = rows[ks[-1]][4]
                for j, (o, h, l, c) in zip(ks, seq):
                    new[j][1:] = [round((pc + x) * 4) / 4 for x in (o, h, l, c)]; pc = new[j][4]
                off = pc - real_end; k = ks[-1] + 1; continue
            k += 1
        out[m] = Bars(new)
    return out
SYN = [make(1), make(2)]
def days_of(B, kw):
    res = {}
    for m, (f, a, z) in SETS.items():
        stt = Combo21(B[m], tparams=TP(), **{**DEFAULT, **kw}); stt.p.MinEntries = 4
        d = co.defaultdict(float)
        for t in stt.run():
            if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:29": d[t["t0"][:10]] += t["usd"] - (t["qty"] + t.get("addq", 0)) * 2.0
        for x in sorted(set(r[:10] for r in B[m].t if a <= r[:10] <= z and r[11:16] == "22:00")): res[(m, x)] = d.get(x, 0.0)
    return res
cfgs = json.load(open(sys.argv[1], encoding="utf-8"))
print("| 설정 | 실제 7월 | 9월 초 | 9/21~10/2 | 10월 | 실제 합계(비용 뒤) | 실제 최악 | 실제 이긴 날 | 샤프(연) | 모의 흔들림 평균/일 | 모의 이긴 날 | 모의 하위 10% | 모의 최악 |")
for name, kw in cfgs:
    for k in ("chop_src", "early_q"):
        if k in kw: kw[k] = tuple(kw[k])
    r = days_of({m: bars(m) for m in SETS}, kw)
    seg = {m: sum(v for (mm, _), v in r.items() if mm == m) for m in SETS}
    v = list(r.values()); sh = st.mean(v) / st.pstdev(v) * math.sqrt(252) if st.pstdev(v) > 0 else 0
    sy = []
    for B in SYN: sy += list(days_of(B, kw).values())
    sy.sort(); n = len(sy)
    print(f"| {name} | " + " | ".join(f"{seg[m]:+,.0f}" for m in SETS) + f" | {sum(v):+,.0f} | {min(v):+,.0f} | {sum(x > 0 for x in v)}/{len(v)} | {sh:.1f} | {sum(sy)/n:+,.0f} | {100*sum(x > 0 for x in sy)//n}% | {sy[n//10]:+,.0f} | {sy[0]:+,.0f} |", flush=True)
print("ALLDONE")
