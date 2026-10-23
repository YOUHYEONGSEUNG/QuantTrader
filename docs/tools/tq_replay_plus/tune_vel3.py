# 현재 쪽도 평균: 속도 방식 × 기준값 격자 (10/23 오후 10). tune_vel2.py와 같은 방식, 결과 tune_vel3.json
#   ref3   = 지금 C − 5·6·7봉 전 평균 (지금 C#)
#   both   = (지금·1봉 전 평균) − 5·6·7봉 전 평균
#   both3  = 최근 3봉 평균 − 5·6·7봉 전 평균,  both3s = 최근 3봉 평균 − 6·7·8봉 전 평균
import sys, os, json, statistics as st, datetime as dt, random, collections as co, itertools
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import all_hours as A
import score
from common import DEFAULT
MODES = ["ref3", "both", "both3", "both3s"]; VMIN = [0.2, 0.25, 0.3]; CMAX = [0.1, 0.15, 0.2]
def key(m, vm, cm): return f"{m}|{vm}|{cm}"
def job(a):
    m, vm, cm, start = a; score.Score.VMODE = m; score.Score.ScoreVelMin = vm
    return key(m, vm, cm), start, A.run_one(("", {**DEFAULT, "cont_vmax": cm}, {}, start))[2]
if __name__ == "__main__":
    from multiprocessing import Pool
    if not os.path.exists("tune_vel3.json"):
        jobs = [(m, vm, cm, s) for m, vm, cm in itertools.product(MODES, VMIN, CMAX) for s in A.STARTS]; tab = co.defaultdict(dict)
        with Pool(4) as pool:
            for k, (n, s, out) in enumerate(pool.imap_unordered(job, jobs, chunksize=4)): tab[n].update(out)
        json.dump(tab, open("tune_vel3.json", "w", encoding="utf-8"), ensure_ascii=False)
    tab = json.load(open("tune_vel3.json", encoding="utf-8"))
    base = key("ref3", 0.25, 0.15); cur = tab[base]; keys = sorted(cur)
    con = [k for k in keys if k.startswith("21:30")]; oth = [k for k in keys if k not in con]
    starts = sorted(set(k.split("|")[0] for k in oth))
    for m in MODES:
        print(f"\n### {m}: 대회 시간 60일 / 다른 시간대 (지금 ref3·0.25·0.15 대비)\n| ScoreVelMin \ 지속 상한 | " + " | ".join(str(c) for c in CMAX) + " |\n|---|" + "---|" * len(CMAX))
        for vm in VMIN:
            print(f"| {vm} | " + " | ".join(f"{sum(tab[key(m, vm, c)][k] for k in con)/1000:+.1f}k / {sum(tab[key(m, vm, c)][k] - cur[k] for k in oth)/1000:+.1f}k" for c in CMAX) + " |")
    wk = {k: "%d-W%02d" % dt.date.fromisoformat(k.split("|")[1]).isocalendar()[:2] for k in keys}; W = sorted(set(wk.values())); rnd = random.Random(2)
    print("\n방식별 같은 기준값(0.25·0.15)에서: 대회 시간 | 8월 | 최악 | 다른 시간대 | 나아진 시간대 | 붓스트랩(대회/다른)")
    for m in MODES:
        v = tab[key(m, 0.25, 0.15)]; x = [v[k] for k in con]; res = []
        for ks in (con, oth):
            byw = co.defaultdict(float)
            for k in ks: byw[wk[k]] += v[k] - cur[k]
            bs = [sum(byw[rnd.choice(W)] for _ in W) for _ in range(3000)]; res.append(100 * sum(b > 0 for b in bs) / len(bs))
        better = sum(sum(v[k] - cur[k] for k in oth if k.startswith(s)) > 0 for s in starts)
        print(f"  {m}: {sum(x):+,.0f} | {sum(v[k] for k in con if '|2026-08' in k):+,.0f} | {min(x):+,.0f} | {sum(v[k]-cur[k] for k in oth):+,.0f} | {better}/10 | {res[0]:.0f}% / {res[1]:.0f}%")
    g = []; pick = co.Counter()
    for _ in range(300):
        ws = W[:]; rnd.shuffle(ws)
        for f in [ws[i::5] for i in range(5)]:
            te = [k for k in con if wk[k] in f]; tr = [k for k in con if wk[k] not in f]
            b = max(tab, key=lambda n: sum(tab[n][k] for k in tr)); pick[b] += 1; g.append(sum(tab[b][k] - cur[k] for k in te))
    print(f"\n전체 격자에서 학습 주로 고르면 남긴 주에서 지금보다 평균 {st.mean(g):+,.0f}$ (나음 {100*sum(x>0 for x in g)/len(g):.0f}%), 자주 고른 것 {pick.most_common(3)}")
