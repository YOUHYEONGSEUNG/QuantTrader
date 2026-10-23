# 설정값 고르기: 앞 22일(7월·9월 초)로 고르고, 뒤 23일(9/21~10/2·10월)로 확인. 고를 때는 짝지은 부트스트랩.
#   py -X utf8 tune.py sweep   한 값씩 바꿔 보기 (학습 구간 부트스트랩: 지금보다 나을 확률·중앙 개선)
#   py -X utf8 tune.py check '{"tp": {...}, "score": {...}, "rg": [6,20,60], "kw": {...}}'   고른 조합을 학습·검증 구간에서 확인
import sys, os, json, random, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common, ind, score
from common import SETS, bars, DEFAULT, TP
from combo19 import Combo20

TRAIN = ("7월", "9월초"); TEST = ("9/21~10/2", "10월"); COST = 2.0
_rg0 = ind.Regime3.__init__
def set_rg(periods):
    f, m, s = periods
    def init(self, b3):
        self.b = b3; self.sf = ind.sma(b3.c, f); self.sm = ind.sma(b3.c, m); self.ss = ind.sma(b3.c, s)
        self.ml, _, _ = ind.macd(b3.c); self.rsi = ind.rsi(b3.c, 14); self.atr = ind.atr(b3, 14)
    ind.Regime3.__init__ = init

def run(cfg, segs=None):
    """날짜별 비용 뒤 손익 {날짜: $}"""
    segs = segs or list(SETS)
    keep = {k: getattr(score.Score, k) for k in cfg.get("score", {})}
    for k, v in cfg.get("score", {}).items(): setattr(score.Score, k, v)
    set_rg(cfg.get("rg", (6, 20, 60)))
    try:
        out = {}
        for m in segs:
            f, a, z = SETS[m]
            st = Combo20(bars(m), tparams=TP(**cfg.get("tp", {})), **{**DEFAULT, **cfg.get("kw", {})}); st.p.MinEntries = 4
            d = co.defaultdict(float)
            for t in st.run():
                if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:29": d[t["t0"][:10]] += t["usd"] - (t["qty"] + t.get("addq", 0)) * COST
            for x in sorted(set(r[:10] for r in bars(m).t if a <= r[:10] <= z and r[11:16] == "22:00")): out[x] = d.get(x, 0.0)
        return out
    finally:
        for k, v in keep.items(): setattr(score.Score, k, v)
        ind.Regime3.__init__ = _rg0

def boot(diff, n=2000, seed=7):
    rnd = random.Random(seed); v = list(diff.values()); k = len(v); s = []
    for _ in range(n): s.append(sum(v[rnd.randrange(k)] for _ in range(k)))
    s.sort(); return sum(x > 0 for x in s) / n, s[n // 2], s[n // 10]

SWEEP = [("tp", "StochK", (5, 7, 14)), ("tp", "StochSmooth", (1, 3, 7)), ("tp", "StochD", (3, 7)),
         ("tp", "CrossLow", (10, 15, 25, 30)), ("tp", "CrossFilterBars", (3, 8)), ("tp", "BandMidPeriod", (20, 34)),
         ("tp", "T1Window", (3, 7)), ("tp", "MacdConfirmBars", (1, 3)), ("tp", "MinStopAtr", (0.75, 1.5, 2.0)),
         ("tp", "SwingBars", (3, 8)), ("tp", "MomentumSmaPeriod", (14, 30, 40)),
         ("score", "ScoreTrendOn", (0.15, 0.25, 0.3)), ("score", "ScoreVelMin", (0.15, 0.35)),
         ("rg", "SMA", ((5, 20, 60), (8, 20, 60), (6, 15, 60), (6, 25, 60), (6, 20, 45), (6, 20, 80))),
         ("kw", "cont_k", (6, 10, 12)), ("kw", "chop_er", (0.1, 0.2))]

def cfg_of(kind, name, v):
    if kind == "tp":
        d = {name: v}
        if name == "CrossLow": d["CrossHigh"] = 100 - v
        return {"tp": d}
    if kind == "score": return {"score": {name: v}}
    if kind == "rg": return {"rg": v}
    return {"kw": {name: v}}

if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "sweep"
    if cmd == "sweep":
        base = run({}, TRAIN); bt = sum(base.values())
        print(f"학습 구간(7월·9월 초 {len(base)}일) 지금: ${bt:+,.0f}")
        print("| 설정 | 학습 합계 | 차이 | 나을 확률 | 차이 중앙 | 차이 하위 10% |\n|---|---|---|---|---|---|")
        res = []
        for kind, name, vals in SWEEP:
            for v in vals:
                r = run(cfg_of(kind, name, v), TRAIN); diff = {d: r[d] - base[d] for d in base}
                p, med, lo = boot(diff); tot = sum(r.values())
                res.append(dict(kind=kind, name=name, v=v, tot=tot, p=p, med=med, lo=lo))
                print(f"| {name}={v} | {tot:+,.0f} | {tot-bt:+,.0f} | {p*100:.0f}% | {med:+,.0f} | {lo:+,.0f} |", flush=True)
        json.dump(res, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "tune_sweep.json"), "w", encoding="utf-8"), ensure_ascii=False, default=str)
    elif cmd == "check":
        cfg = json.loads(sys.argv[2])
        if "rg" in cfg: cfg["rg"] = tuple(cfg["rg"])
        b = run({}); r = run(cfg)
        for name, segs in (("학습(7월·9월 초)", TRAIN), ("검증(9/21~10/2·10월)", TEST)):
            days = [d for d in b if any(SETS[s][1] <= d <= SETS[s][2] for s in segs)]
            diff = {d: r[d] - b[d] for d in days}; p, med, lo = boot(diff)
            print(f"{name}: 지금 ${sum(b[d] for d in days):+,.0f} → ${sum(r[d] for d in days):+,.0f} (차이 {sum(diff.values()):+,.0f}, 나을 확률 {p*100:.0f}%), "
                  f"이긴 날 {sum(b[d] > 0 for d in days)} → {sum(r[d] > 0 for d in days)}/{len(days)}, 최악의 날 {min(b[d] for d in days):+,.0f} → {min(r[d] for d in days):+,.0f}")
