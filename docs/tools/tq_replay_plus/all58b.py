# 58일(45일 + 8월 13일): 흔들리는 장 판단 변형·하루 손실 막기 비교, 주 단위 교차 검증으로 '고르는 것' 자체 검증
import sys, os, json, random, statistics as st, datetime as dt, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ind import Bars
from common import SETS, bars, DEFAULT, TP, DATA
from combo33 import Combo33
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
      ("흔들림: 대회 전 봉 포함 최근 60봉", dict(chop_src=("roll", 60))),
      ("흔들림: 대회 전 봉 포함 최근 120봉", dict(chop_src=("roll", 120))),
      ("흔들림: 대회 시작 30봉 뒤부터", dict(chop_src=("sess", 30))),
      ("흔들림이면 추세 진입 안 함", dict(chop_act="none")),
      ("흔들림이면 수량 절반", dict(chop_act="half")),
      ("흔들림: 감쇠 효율비 반감기 30", dict(chop_def=("ewer", 30))),
      ("흔들림: Choppiness 20봉 ≥ 61.8", dict(chop_def=("ci", 20, 61.8))),
      ("하루 −$4,000면 멈춤", dict(day_stop=("usd", 4000))),
      ("하루 −$6,000면 멈춤", dict(day_stop=("usd", 6000))),
      ("하루 −$8,000면 멈춤", dict(day_stop=("usd", 8000))),
      ("손실 거래 2번이면 멈춤", dict(day_stop=("n", 2))),
      ("손실 거래 3번이면 멈춤", dict(day_stop=("n", 3)))]
if __name__ == "__main__":
    tab = {}
    for name, k in CF:
        tab[name] = all_days({**DEFAULT, **k}); print("done", name, file=sys.stderr, flush=True)
    json.dump(tab, open("tune_all58b.json", "w", encoding="utf-8"), ensure_ascii=False)
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
