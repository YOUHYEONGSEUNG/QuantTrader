import json, sys, collections as co
from ind import Bars
from strat import Strat, P
DATA = {"7월": ("bars_sep.json", "2026-07-16", "2026-07-31"), "10월": ("bars_dec2.json", "2026-10-05", "2026-10-21")}
_cache = {}
def bars(m):
    if m not in _cache: _cache[m] = Bars(json.load(open(DATA[m][0])))
    return _cache[m]
def run(p=None, hooks=None, months=("7월", "10월")):
    out = {}
    for m in months:
        st = Strat(bars(m), p or P(), hooks or {}); tr = st.run()
        a, z = DATA[m][1], DATA[m][2]
        out[m] = [t for t in tr if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:30"]
    return out
def summ(tr):
    if not tr: return "-"
    w = sum(1 for t in tr if t["usd"] > 0); days = co.defaultdict(float)
    for t in tr: days[t["t0"][:10]] += t["usd"]
    worst = min(days.values()); neg = sum(1 for v in days.values() if v < 0)
    return f"{len(tr)}건 {100*w//len(tr)}% ${sum(t['usd'] for t in tr):,.0f} (손실일 {neg}/{len(days)}, 최악 ${worst:,.0f})"
def tdays(m):
    a, z = DATA[m][1], DATA[m][2]; b = bars(m)
    return sorted(set(t[:10] for t in b.t if a <= t[:10] <= z and "21:30" <= t[11:16] <= "23:27" and t[11:16] == "22:00"))
def row(name, out):
    tot = sum(t["usd"] for v in out.values() for t in v)
    few = []
    for m in out:
        cnt = co.Counter(t["t0"][:10] for t in out[m])
        few.append(sum(1 for d in tdays(m) if cnt[d] < 2))
    print(f"| {name} | {summ(out['7월'])} | {summ(out['10월'])} | ${tot:,.0f} | {few[0]}/{few[1]} |")
if __name__ == "__main__":
    o = run()
    print("| 설정 | 7월 | 10월 | 합계 |\n|---|---|---|---|"); row("지금 (v6-box)", o)
    for m in o:
        g = co.defaultdict(list)
        for t in o[m]: g[t["path"]].append(t)
        print(m, {k: (len(v), round(sum(t['usd'] for t in v))) for k, v in sorted(g.items())})
