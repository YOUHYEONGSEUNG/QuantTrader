# 재현기 실행. 사용법은 README.md
#   py replay.py            지금 기본값과 비교 기준들, 구간별 손익
#   py replay.py days       날짜별 손익 (10/23 오전 30계약 vs 지금 기본값)과 그날 대회 구간 움직임
#   py replay.py trades 2026-09-21,2026-09-23   그날 거래 내역
#   py replay.py exits      청산 늦추기 180가지 (10/23 시험)
#   py replay.py cont       추세 지속 진입 봉 수·조건 (10/23 시험)
import sys, os, json, itertools, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import SETS, trades, summary, day_moves

def row(name, r):
    print(f"| {name} | " + " | ".join(f"{r[m]:+,}" for m in SETS) + f" | {r['합계']:+,} | {r['최악의 날']:+,} | {r['손실일']} | {r['건수']} |")

def head(): print("| 설정 | " + " | ".join(SETS) + " | 합계 | 최악의 날 | 손실일 | 건수 |\n|" + "---|" * (len(SETS) + 5))

def grid(cfgs):
    head(); res = []
    for name, kw in cfgs:
        r = summary(trades(**kw)); res.append(dict(name=name, cfg=kw, **r)); row(name, r)
    return res

cmd = sys.argv[1] if len(sys.argv) > 1 else "base"
if cmd == "base":
    grid([("10/23 오전 (30계약, 속도 조건·불타기 없음)", dict(qty=30, add_q=0, cont_vmax=None)), ("추세 지속 진입 끔 (50+30)", dict(cont_k=None)), ("지금 기본값", {})])
elif cmd == "days":
    A = trades(qty=30, add_q=0, cont_vmax=None); Bn = trades(); da, db = co.defaultdict(float), co.defaultdict(float)
    for v in A.values():
        for t in v: da[t["t0"][:10]] += t["usd"]
    for v in Bn.values():
        for t in v: db[t["t0"][:10]] += t["usd"]
    print("| 날짜 | 순변화 pt | 효율비 | 30계약 (오전) | 지금 기본값 | 차이 |\n|---|---|---|---|---|---|")
    for d, s in sorted(day_moves().items()):
        net = s["c"] - s["o"]; er = abs(net) / s["path"] if s["path"] else 0
        print(f"| {d} | {net:+.1f} | {er:.2f} | {da[d]:+,.0f} | {db[d]:+,.0f} | {db[d]-da[d]:+,.0f} |")
elif cmd == "trades":
    ds = sys.argv[2].split(",")
    for v in trades().values():
        for t in v:
            if t["t0"][:10] not in ds: continue
            ex = " ".join(f"{e[0][11:16]}{e[1]}@{e[3]}x{e[4]}" for e in t["exits"])
            print(f"{t['t0'][5:16]} {t['path']:5s} {'매수' if t['d'] > 0 else '매도'} {t['qty']:2d}계약 손절0={t['stop0']:.2f} "
                  f"최대유리={t['mfe']:.1f} 최대불리={t['mae']:.1f} ${t['usd']:+,.0f} {t['reason']} | {ex}")
elif cmd == "exits":
    cfgs = [(f"최종 {fi} 약한 {we} 본절 {be} 트레일링×{tk} 추세청산 {rx}", dict(cont_k=None, final=fi, weak=we, be=be, trail_k=tk, rx6=rx))
            for fi, we, be, tk, rx in itertools.product(("base", "after_tp1", "after_tp2", "rsi", "off"), ("on", "after_tp1", "off"),
                                                         ("trader", "none"), (1.0, 1.5, 2.0), (True, False))]
    grid(cfgs)
elif cmd == "cont":
    cfgs = [("끔", dict(cont_k=None))]
    cfgs += [(f"{k}봉 한 번={once} 흔들리는 장 제외={ch}", dict(cont_k=k, cont_once=once, cont_chop=ch))
             for k, once, ch in itertools.product((3, 4, 5, 6, 8, 10, 12, 15, 20), (True, False), (True, False))]
    grid(cfgs)
else:
    print(__doc__ or open(__file__, encoding="utf-8").read().split("import")[0])
