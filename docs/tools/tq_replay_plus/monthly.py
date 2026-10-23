# 팀 규칙(브랜치 score-v1-logic 8a4036b) vs 지금 규칙(a6e9cb6) 월별 성적. 데이터 7/16~10/21 (bars_aug·bars_oos·bars_oct_nt), 대회 구간 21:30~23:29 신호 봉
#   계약당 왕복 $2(수수료·미끄러짐 가정)를 뺀 값. 결과 tune_monthly.json
import sys, os, json, collections as co, statistics as st
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ind import Bars
from common import DEFAULT, TP, DATA
from combo19 import Combo20
import team
FILES = [("bars_aug.json", "2026-07-16", "2026-09-18"), ("bars_oos.json", "2026-09-21", "2026-10-02"), ("bars_oct_nt.json", "2026-10-05", "2026-10-21")]
def collect(make):
    days = {}; ntr = co.Counter(); intra = {}
    for f, a, z in FILES:
        b = Bars(json.load(open(os.path.join(DATA, f))))
        for x in sorted(set(t[:10] for t in b.t if a <= t[:10] <= z and t[11:16] == "22:00")): days[x] = 0.0
        for t in make(b):
            d = t["t0"][:10]
            if a <= d <= z and "21:30" <= t["t0"][11:16] <= "23:29" and d in days:
                days[d] += t["usd"] - (t["qty"] + t.get("addq", 0)) * 2; ntr[d] += 1
    return days, ntr
def cur(**kw):
    def mk(b):
        s = Combo20(b, tparams=TP(), **{**DEFAULT, **kw}); s.p.MinEntries = 4; return s.run()
    return mk
def tm(**kw):
    def mk(b): return team.Team(b, team.P(**{"T1Window": 5, "MinStopAtr": 1.0, "EntryQuantity": 30, **kw})).run()
    return mk
CF = [("팀 규칙 (30계약)", tm()), ("팀 규칙을 50계약으로", tm(EntryQuantity=50)), ("지금 규칙을 30계약·불타기 없이", cur(qty=30, add_q=0, early_q=None)), ("지금 규칙 (50 + 불타기 30)", cur())]
M = [("7월 (7/16~)", "2026-07"), ("8월", "2026-08"), ("9월", "2026-09"), ("10월 (~10/21)", "2026-10")]
out = {}
for name, mk in CF:
    days, ntr = collect(mk); out[name] = days
    print(f"\n### {name}\n| 월 | 날 | 합계 | 하루 평균 | 이긴 날 | 최악의 날 | 최고의 날 | 거래/일 |\n|---|---|---|---|---|---|---|---|")
    for lab, p in M + [("전체", "")]:
        ds = [d for d in days if d.startswith(p)]; v = [days[d] for d in ds]
        print(f"| {lab} | {len(v)} | {sum(v):+,.0f} | {st.mean(v):+,.0f} | {sum(x > 0 for x in v)}/{len(v)} | {min(v):+,.0f} | {max(v):+,.0f} | {sum(ntr[d] for d in ds)/len(ds):.1f} |", flush=True)
json.dump(out, open("tune_monthly.json", "w", encoding="utf-8"), ensure_ascii=False)
