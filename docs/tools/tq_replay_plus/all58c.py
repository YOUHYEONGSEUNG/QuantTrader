# 58일(45일 + 8월 13일): 매수에 매도의 SMA20 규칙을 거울로 넣기, 주 단위 교차 검증으로 '고르는 것' 자체 검증
import sys, os, json, random, statistics as st, datetime as dt, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ind import Bars
from common import SETS, bars, DEFAULT, TP, DATA
from combo34 import Combo34 as Combo33
AUGB = Bars(json.load(open(os.path.join(DATA, "bars_aug.json"))))
def days(b, a, z, kw):
    s = Combo33(b, tparams=TP(), **kw); s.p.MinEntries = 4; d = co.defaultdict(float)
    for t in s.run():
        if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:29": d[t["t0"][:10]] += t["usd"] - (t["qty"] + t.get("addq", 0)) * 2
    return {x: d.get(x, 0.0) for x in sorted(set(r[:10] for r in b.t if a <= r[:10] <= z and r[11:16] == "22:00"))}
def all_days(kw):
    r = days(AUGB, "2026-08-03", "2026-08-31", kw)
    for m, (f, a, z) in SETS.items(): r.update(days(bars(m), a, z, kw))
    return r
CF = [("지금", {}),
      ("매수 SMA20 보류·10봉 대기", dict(long_block=10)),
      ("매수 SMA20 청산(항상)", dict(long_exit="always")),
      ("매수 SMA20 청산(1차 전만)", dict(long_exit="pre_tp1")),
      ("매수 보류·대기 + 청산(항상) = 매도와 대칭", dict(long_block=10, long_exit="always")),
      ("매수 보류·대기 + 청산(1차 전만)", dict(long_block=10, long_exit="pre_tp1"))]
if __name__ == "__main__":
    tab = {}
    for name, k in CF:
        tab[name] = all_days({**DEFAULT, **k}); print("done", name, file=sys.stderr, flush=True)
    json.dump(tab, open("tune_all58c.json", "w", encoding="utf-8"), ensure_ascii=False)
    D = sorted(tab["지금"]); AUG = [d for d in D if d.startswith("2026-08")]; OLD = [d for d in D if d not in AUG]
    def sh(x): return st.mean(x) / st.pstdev(x) * 252 ** .5 if st.pstdev(x) > 0 else 0
    print("| 설정 | 58일 | 8월 13일 | 45일 | 최악 | 하위 5일 평균 | 이긴 날 | 샤프 |\n|---|---|---|---|---|---|---|---|")
    for name, _ in CF:
        v = tab[name]; x = [v[d] for d in D]
        print(f"| {name} | {sum(x):+,.0f} | {sum(v[d] for d in AUG):+,.0f} | {sum(v[d] for d in OLD):+,.0f} | {min(x):+,.0f} | {sum(sorted(x)[:5])/5:+,.0f} | {sum(y>0 for y in x)}/{len(x)} | {sh(x):.1f} |")
    wk = {d: "%d-W%02d" % dt.date.fromisoformat(d).isocalendar()[:2] for d in D}; W = sorted(set(wk.values()))
    for crit in ("합계", "샤프"):
        rnd = random.Random(7); g = []; pick = co.Counter(); wg = []
        for _ in range(300):
            ws = W[:]; rnd.shuffle(ws)
            for f in [ws[k::5] for k in range(5)]:
                te = [d for d in D if wk[d] in f]; tr = [d for d in D if wk[d] not in f]
                key = (lambda n: sum(tab[n][d] for d in tr)) if crit == "합계" else (lambda n: sh([tab[n][d] for d in tr]))
                b = max(tab, key=key); pick[b] += 1
                g.append(sum(tab[b][d] for d in te) - sum(tab["지금"][d] for d in te))
                wg.append(min(tab[b][d] for d in te) - min(tab["지금"][d] for d in te))
        print(f"\n{crit}로 고를 때 (주 단위 5겹×300): 남긴 주 손익 '지금'보다 평균 {st.mean(g):+,.0f}$ (나음 {100*sum(x>0 for x in g)/len(g):.0f}% / 나쁨 {100*sum(x<0 for x in g)/len(g):.0f}%), 남긴 주 최악의 날 {st.mean(wg):+,.0f}$; 고른 설정 {pick.most_common(4)}")
