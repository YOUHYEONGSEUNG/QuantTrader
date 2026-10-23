# 설정값 고르기 2: 촘촘한 값 × 45일 날짜별 손익 표를 한 번 만들고, 주 단위 그룹 K겹 교차 검증을 여러 번 되풀이
#   py -X utf8 tune_cv.py grid     값마다 45일 날짜별 비용 뒤 손익을 tune_grid.json에 저장 (오래 걸림)
#   py -X utf8 tune_cv.py cv       그 표로 교차 검증: 학습 주에서 고른 값이 남겨 둔 주에서 지금 값보다 나았는지
import sys, os, json, random, statistics as st, datetime as dt, collections as co
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
from tune import run

GRID = {
    "MinStopAtr": ("tp", [0.75, 1.0, 1.25, 1.3, 1.5, 1.7, 2.0, 2.5]),
    "MomentumSmaPeriod": ("tp", [14, 16, 18, 20, 22, 25, 30]),
    "StochK": ("tp", [7, 8, 9, 10, 11, 12, 14]),
    "StochSmooth": ("tp", [2, 3, 4, 5, 6, 7]),
    "StochD": ("tp", [3, 4, 5, 6]),
    "CrossLow": ("tp", [15, 18, 20, 22, 25]),
    "BandMidPeriod": ("tp", [22, 24, 26, 28, 30]),
    "T1Window": ("tp", [3, 4, 5, 6]),
    "MacdConfirmBars": ("tp", [1, 2, 3]),
    "SwingBars": ("tp", [3, 4, 5, 6, 7]),
    "ScoreTrendOn": ("score", [0.15, 0.18, 0.2, 0.22, 0.25]),
    "ScoreVelMin": ("score", [0.2, 0.25, 0.3, 0.35]),
    "SmaFast": ("rg0", [5, 6, 7, 8]),
    "SmaMid": ("rg1", [18, 20, 22]),
    "SmaSlow": ("rg2", [50, 60, 70]),
    "cont_k": ("kw", [6, 7, 8, 9, 10]),
    "chop_er": ("kw", [0.12, 0.15, 0.18]),
    "cont_vmax": ("kw", [0.15, 0.2, 0.25]),
}
BASE = {"MinStopAtr": 1.5, "MomentumSmaPeriod": 20, "StochK": 10, "StochSmooth": 5, "StochD": 5, "CrossLow": 20, "BandMidPeriod": 26,
        "T1Window": 5, "MacdConfirmBars": 2, "SwingBars": 5, "ScoreTrendOn": 0.2, "ScoreVelMin": 0.25, "SmaFast": 6, "SmaMid": 20, "SmaSlow": 60,
        "cont_k": 8, "chop_er": 0.15, "cont_vmax": 0.2}

def cfg_of(sets):
    """{이름: 값} → tune.run 설정"""
    c = {"tp": {}, "score": {}, "kw": {}}; rg = [6, 20, 60]
    for n, v in sets.items():
        kind = GRID[n][0]
        if kind == "tp":
            c["tp"][n] = v
            if n == "CrossLow": c["tp"]["CrossHigh"] = 100 - v
        elif kind == "score": c["score"][n] = v
        elif kind.startswith("rg"): rg[int(kind[2])] = v
        else: c["kw"][n] = v
    c["rg"] = tuple(rg); return c

def week(d): y, w, _ = dt.date.fromisoformat(d).isocalendar(); return f"{y}-W{w:02d}"

if __name__ == "__main__":
    cmd = sys.argv[1]
    path = os.path.join(HERE, "tune_grid.json")
    if cmd == "grid":
        res = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {}
        if "base" not in res: res["base"] = run(cfg_of({})); json.dump(res, open(path, "w", encoding="utf-8"))
        for n, (kind, vals) in GRID.items():
            for v in vals:
                key = f"{n}={v}"
                if v == BASE[n] or key in res: continue
                res[key] = run(cfg_of({n: v})); json.dump(res, open(path, "w", encoding="utf-8"))
                print(key, round(sum(res[key].values())), flush=True)
        print("ALLDONE")
    if cmd == "cv":
        res = json.load(open(path, encoding="utf-8")); base = res["base"]; days = sorted(base)
        weeks = sorted(set(week(d) for d in days)); wk = {d: week(d) for d in days}
        R, K = int(sys.argv[2]) if len(sys.argv) > 2 else 300, 5
        rnd = random.Random(11)
        print(f"{len(days)}일, {len(weeks)}주, 주 단위 {K}겹 × {R}번\n")
        summary = []
        for n, (kind, vals) in GRID.items():
            keys = {v: (res["base"] if v == BASE[n] else res.get(f"{n}={v}")) for v in vals}
            if any(x is None for x in keys.values()): continue
            gains = []; pick = co.Counter()
            for _ in range(R):
                ws = weeks[:]; rnd.shuffle(ws); folds = [ws[k::K] for k in range(K)]
                for f in folds:
                    test = [d for d in days if wk[d] in f]; train = [d for d in days if wk[d] not in f]
                    best = max(vals, key=lambda v: (round(sum(keys[v][d] for d in train)), v == BASE[n]))
                    pick[best] += 1
                    gains.append(sum(keys[best][d] - base[d] for d in test))
            mean = st.mean(gains); pos = sum(g > 0 for g in gains) / len(gains); neg = sum(g < 0 for g in gains) / len(gains)
            tot = {v: sum(keys[v].values()) for v in vals}
            worst = {v: min(keys[v].values()) for v in vals}
            wins = {v: sum(x > 0 for x in keys[v].values()) for v in vals}
            curve = "  ".join(f"{v}{'*' if v == BASE[n] else ''}:{tot[v]/1000:+.1f}k" for v in vals)
            top = pick.most_common(2)
            print(f"[{n}] 전체 45일 합계(비용 뒤)  {curve}")
            print(f"   최악의 날 " + "  ".join(f"{v}:{worst[v]/1000:+.1f}k" for v in vals) + " | 이긴 날 " + " ".join(f"{v}:{wins[v]}" for v in vals))
            print(f"   교차 검증: 학습 주에서 고른 값이 남긴 주에서 지금 값보다 평균 {mean:+,.0f}$ (나음 {pos*100:.0f}% / 나쁨 {neg*100:.0f}%), 가장 자주 고른 값 " +
                  ", ".join(f"{v}({c*100//(R*K)}%)" for v, c in top) + "\n", flush=True)
            summary.append(dict(name=n, mean=mean, pos=pos, neg=neg, pick=top[0][0], pickp=top[0][1] / (R * K)))
        print("요약 (평균 이득 순)")
        for s in sorted(summary, key=lambda x: -x["mean"]):
            print(f"  {s['name']:18s} 평균 {s['mean']:+7,.0f}$  나음 {s['pos']*100:3.0f}%  나쁨 {s['neg']*100:3.0f}%  가장 자주 고른 값 {s['pick']} ({s['pickp']*100:.0f}%)")
