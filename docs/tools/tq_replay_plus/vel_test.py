# 속도 계산 방식 비교 (10/23 밤): 지금(C − 6봉 전 C) vs 비교 기준을 평균으로 / 양쪽 평균 / 회귀 기울기
#   모든 시간대 2시간 구간(all_hours.py와 같음)에서 비교. 21:30 구간 = 대회 시간 60일. 결과 tune_vel.json
import sys, os, json, statistics as st, datetime as dt, random, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import all_hours as A
import score
from common import DEFAULT
MODES = [("지금: C − 6봉 전 C", "point"), ("C − (5·6·7봉 전 평균)", "ref3"), ("(지금·1봉 전 평균) − (5·6·7봉 전 평균)", "both"), ("최근 7점 회귀 기울기 × 6", "slope")]
def job(a):
    mode, start = a; score.Score.VMODE = mode
    return mode, start, A.run_one((mode, dict(DEFAULT), {}, start))[2]
if __name__ == "__main__":
    from multiprocessing import Pool
    jobs = [(m, s) for _, m in MODES for s in A.STARTS]; tab = co.defaultdict(dict)
    with Pool(4) as pool:
        for k, (m, s, out) in enumerate(pool.imap_unordered(job, jobs)):
            tab[m].update(out); print(f"{k+1}/{len(jobs)}", file=sys.stderr, flush=True)
    json.dump(tab, open("tune_vel.json", "w", encoding="utf-8"), ensure_ascii=False)
    cur = tab["point"]; keys = sorted(cur); con = [k for k in keys if k.startswith("21:30")]; oth = [k for k in keys if k not in con]
    AUG = [k for k in con if k.split("|")[1].startswith("2026-08")]
    wk = {k: "%d-W%02d" % dt.date.fromisoformat(k.split("|")[1]).isocalendar()[:2] for k in keys}; W = sorted(set(wk.values())); rnd = random.Random(5)
    starts = sorted(set(k.split("|")[0] for k in oth))
    print("| 속도 계산 | 대회 시간 60일 | 그중 8월 | 최악의 날 | 이긴 날 | 다른 시간대 합 (지금 대비) | 나아진 시간대 | 붓스트랩: 다른 시간대에서 지금보다 나을 확률 |\n|---|---|---|---|---|---|---|---|")
    for name, m in MODES:
        v = tab[m]; x = [v[k] for k in con]
        d = sum(v[k] - cur[k] for k in oth); better = sum(sum(v[k] - cur[k] for k in oth if k.startswith(s)) > 0 for s in starts)
        byw = co.defaultdict(float)
        for k in oth: byw[wk[k]] += v[k] - cur[k]
        bs = [sum(byw[rnd.choice(W)] for _ in W) for _ in range(3000)]
        print(f"| {name} | {sum(x):+,.0f} | {sum(v[k] for k in AUG):+,.0f} | {min(x):+,.0f} | {sum(y>0 for y in x)}/{len(x)} | {d:+,.0f} | {better}/{len(starts)} | {'—' if m == 'point' else f'{100*sum(b>0 for b in bs)/len(bs):.0f}%'} |")
