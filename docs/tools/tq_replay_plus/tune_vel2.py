# 속도 방식(지금 point / 비교 기준 3봉 평균 ref3) × 속도 기준값 격자 (10/23 밤)
#   ScoreVelMin: 7레짐 시작의심·추세→눌림 속도 기준 (C# ScoreVelMin), cont_vmax: 추세 지속 진입 속도 상한 (C# TrendContMaxVel, None = 조건 없음)
#   모든 시간대 2시간 구간(all_hours.py와 같음). 결과 tune_vel2.json, 보고는 아래 __main__
import sys, os, json, statistics as st, datetime as dt, random, collections as co, itertools
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import all_hours as A
import score
from common import DEFAULT
MODES = ["point", "ref3"]; VMIN = [0.15, 0.2, 0.25, 0.3, 0.35]; CMAX = [0.1, 0.15, 0.2, 0.3, None]
def key(m, vm, cm): return f"{m}|{vm}|{cm}"
def job(a):
    m, vm, cm, start = a; score.Score.VMODE = m; score.Score.ScoreVelMin = vm
    return key(m, vm, cm), start, A.run_one(("", {**DEFAULT, "cont_vmax": cm}, {}, start))[2]
if __name__ == "__main__":
    from multiprocessing import Pool
    if not os.path.exists("tune_vel2.json"):
        jobs = [(m, vm, cm, s) for m, vm, cm in itertools.product(MODES, VMIN, CMAX) for s in A.STARTS]; tab = co.defaultdict(dict)
        with Pool(4) as pool:
            for k, (n, s, out) in enumerate(pool.imap_unordered(job, jobs, chunksize=4)):
                tab[n].update(out)
                if k % 50 == 0: print(f"{k+1}/{len(jobs)}", file=sys.stderr, flush=True)
        json.dump(tab, open("tune_vel2.json", "w", encoding="utf-8"), ensure_ascii=False)
    tab = json.load(open("tune_vel2.json", encoding="utf-8"))
    base = key("point", 0.25, 0.2); cur = tab[base]; keys = sorted(cur)
    con = [k for k in keys if k.startswith("21:30")]; oth = [k for k in keys if k not in con]
    AUG = [k for k in con if k.split("|")[1].startswith("2026-08")]
    for m in MODES:
        print(f"\n### {m}: 대회 시간 60일 합계 (행 ScoreVelMin, 열 지속 진입 속도 상한)\n| | " + " | ".join(str(c) for c in CMAX) + " |\n|---" * (len(CMAX) + 1) + "|")
        for vm in VMIN: print(f"| {vm} | " + " | ".join(f"{sum(tab[key(m, vm, c)][k] for k in con)/1000:+.1f}k" for c in CMAX) + " |")
        print(f"\n{m}: 다른 시간대 합 (지금 point 0.25/0.2 대비)\n| | " + " | ".join(str(c) for c in CMAX) + " |\n|---" * (len(CMAX) + 1) + "|")
        for vm in VMIN: print(f"| {vm} | " + " | ".join(f"{sum(tab[key(m, vm, c)][k] - cur[k] for k in oth)/1000:+.1f}k" for c in CMAX) + " |")
    # 주 단위 5겹 × 300: 학습 주로 격자에서 고른 설정이 남긴 주에서 지금보다 나은가
    wk = {k: "%d-W%02d" % dt.date.fromisoformat(k.split("|")[1]).isocalendar()[:2] for k in con}; W = sorted(set(wk.values())); rnd = random.Random(9)
    for label, pool_ in (("두 방식 전체 격자", list(tab)), ("ref3 격자만", [n for n in tab if n.startswith("ref3")]), ("point 격자만", [n for n in tab if n.startswith("point")])):
        g = []; pick = co.Counter()
        for _ in range(300):
            ws = W[:]; rnd.shuffle(ws)
            for f in [ws[i::5] for i in range(5)]:
                te = [k for k in con if wk[k] in f]; tr = [k for k in con if wk[k] not in f]
                b = max(pool_, key=lambda n: sum(tab[n][k] for k in tr)); pick[b] += 1
                g.append(sum(tab[b][k] - cur[k] for k in te))
        print(f"\n{label}에서 학습 주로 고르면: 남긴 주에서 지금보다 평균 {st.mean(g):+,.0f}$ (나음 {100*sum(x>0 for x in g)/len(g):.0f}% / 나쁨 {100*sum(x<0 for x in g)/len(g):.0f}%), 자주 고른 것 {pick.most_common(3)}")
    rk = sorted(tab, key=lambda n: -sum(tab[n][k] for k in con))[:6]
    print("\n대회 시간 상위 6개: 설정 | 대회 시간 | 8월 | 최악 | 이긴 날 | 다른 시간대 (지금 대비)")
    for n in rk + ([base] if base not in rk else []):
        x = [tab[n][k] for k in con]
        print(f"  {n} | {sum(x):+,.0f} | {sum(tab[n][k] for k in AUG):+,.0f} | {min(x):+,.0f} | {sum(y>0 for y in x)}/{len(x)} | {sum(tab[n][k]-cur[k] for k in oth):+,.0f}")
