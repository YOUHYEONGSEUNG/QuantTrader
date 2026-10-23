# 45일 + 처음 보는 8월 13일 = 58일로 기능·수량 조합을 비교하고, 주 단위 교차 검증으로 고르는 방식 자체를 검증
import sys, os, json, random, statistics as st, datetime as dt, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from loo import days, AUGB
from common import SETS, bars, DEFAULT, TP
def all_days(kw, tp):
    r = days(AUGB, "2026-08-03", "2026-08-31", kw, tp)
    for m, (f, a, z) in SETS.items(): r.update(days(bars(m), a, z, kw, tp))
    return r
CUR = dict(DEFAULT)
CF = [("지금", {}, {}),
      ("지속 진입 끔", dict(cont_k=None), {}),
      ("지속 진입 속도 조건 끔", dict(cont_vmax=None), {}),
      ("손절 최소 1.0", {}, dict(MinStopAtr=1.0)),
      ("지속 진입 끔 + 손절 1.0", dict(cont_k=None), dict(MinStopAtr=1.0)),
      ("불타기 끔", dict(add_q=0), {}),
      ("30계약, 불타기 끔", dict(qty=30, add_q=0, early_q=None), {}),
      ("40계약, 불타기 끔", dict(qty=40, add_q=0, early_q=("21:45", 25)), {}),
      ("지속 진입 끔 + 불타기 끔", dict(cont_k=None, add_q=0), {}),
      ("지속 진입 끔 + 40계약, 불타기 끔", dict(cont_k=None, qty=40, add_q=0), {})]
tab = {}
for name, k, t in CF:
    tab[name] = all_days({**CUR, **k}, dict(t)); print("done", name, flush=True)
json.dump(tab, open("tune_all58.json", "w", encoding="utf-8"), ensure_ascii=False)
D = sorted(tab["지금"]); AUG = [d for d in D if d.startswith("2026-08")]; OLD = [d for d in D if d not in AUG]
def sh(x): return st.mean(x) / st.pstdev(x) * 252 ** .5 if st.pstdev(x) > 0 else 0
print(f"\n| 설정 | 58일 합계 | 8월 13일 | 45일 | 최악 | 하위 5일 평균 | 이긴 날 | 샤프 |\n|---|---|---|---|---|---|---|---|")
for name, *_ in CF:
    v = tab[name]; x = [v[d] for d in D]
    print(f"| {name} | {sum(x):+,.0f} | {sum(v[d] for d in AUG):+,.0f} | {sum(v[d] for d in OLD):+,.0f} | {min(x):+,.0f} | {sum(sorted(x)[:5])/5:+,.0f} | {sum(y>0 for y in x)}/{len(x)} | {sh(x):.1f} |")
wk = {d: "%d-W%02d" % dt.date.fromisoformat(d).isocalendar()[:2] for d in D}; W = sorted(set(wk.values()))
rnd = random.Random(7); g = []; pick = co.Counter()
for _ in range(300):
    ws = W[:]; rnd.shuffle(ws)
    for f in [ws[k::5] for k in range(5)]:
        te = [d for d in D if wk[d] in f]; tr = [d for d in D if wk[d] not in f]
        b = max(tab, key=lambda n: sum(tab[n][d] for d in tr)); pick[b] += 1
        g.append(sum(tab[b][d] for d in te) - sum(tab["지금"][d] for d in te))
print(f"\n주 단위 5겹×300 (58일, {len(W)}주): 학습 주로 고른 설정이 남긴 주에서 '지금'보다 평균 {st.mean(g):+,.0f}$, 나음 {100*sum(x>0 for x in g)/len(g):.0f}%; 고른 설정 {pick.most_common(4)}")
