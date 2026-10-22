import json, csv, sys, math, os
from ind import Bars
from strat import Strat, P
rows = json.load(open("bars_dec.json")); b = Bars(rows)
st = Strat(b); tr = st.run()
mine = [t for t in tr if "2026-10-08T21:30" <= t["t0"] <= "2026-10-15T23:59"]
# Playback 기록
hdr = None; pb = []
for line in open(os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8", "TQ_ml_v6_MNQ.csv"), encoding="utf-8-sig"):
    if line.startswith("#"): continue
    if hdr is None: hdr = next(csv.reader([line])); continue
    r = dict(zip(hdr, next(csv.reader([line]))))
    if r["run_id"] == "20261016-234413-438" and r["mode"] == "live": pb.append(r)
pb = [r for r in pb if r["signal_time"] >= "2026-10-08 21:30"]
def key(t): return t.replace(" ", "T")[:16]
M = {key(t["t0"]): t for t in mine}; Pd = {key(r["signal_time"]): r for r in pb}
allk = sorted(set(M) | set(Pd)); ok = 0
tot_m = sum(t["usd"] for t in mine); tot_p = sum(float(r["pnl_usd"]) for r in pb)
for k in allk:
    m = M.get(k); r = Pd.get(k)
    ms = f"{m['path']:5s} {'L' if m['d']>0 else 'S'} q{m['qty']:<3d} avg {m.get('avg', float('nan')):.2f} ${m['usd']:8.0f} {m['reason']:12s} {m.get('t1','')[11:16]}" if m else "-" * 20
    rs = f"{r['path']:5s} {'L' if r['dir'] in ('1','Long') else 'S'} q{int(r['qty']):<3d} avg {float(r['avg_price']):.2f} ${float(r['pnl_usd']):8.0f} {r['exit_reason']:12s} {r['exit_time'][11:16]}" if r else "-" * 20
    same = m and r and m["path"] == r["path"] and m["qty"] == int(r["qty"]) and abs(m["usd"] - float(r["pnl_usd"])) < 30
    ok += bool(same)
    print(f"{k} {'✓' if same else '✗'} SIM {ms} | PB {rs}")
print(f"일치 {ok}/{len(allk)}  SIM ${tot_m:,.0f}  PB ${tot_p:,.0f}")
