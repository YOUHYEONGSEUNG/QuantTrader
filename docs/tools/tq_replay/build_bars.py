import json, datetime as dt, collections as co
J = (json.load(open("../bars_v5_mnq.json"))["MNQ DEC26"]["bars"] if __import__("os").path.exists("../bars_v5_mnq.json") else [])
runs = json.load(open("logruns.json", encoding="utf-8"))
B = {}
for x in J: B[x[0]] = (x[4], x[5], x[6], x[7])
src = {k: "json" for k in B}
mism = 0; added = 0
for r in runs:
    if r["inst"] != "MNQ DEC26": continue
    for t, b in r["bars"].items():
        v = (b["o"], b["h"], b["l"], b["c"])
        if t in B:
            if B[t] != v: mism += 1
        else: B[t] = v; added += 1
ks = sorted(B)
print("json", len(J), "added", added, "mismatch", mism, ks[0], ks[-1])
# 연속성: 하루별 봉 수와 큰 구멍
days = co.Counter(k[:10] for k in ks); print(dict(days))
prev = None; gaps = []
for k in ks:
    t = dt.datetime.fromisoformat(k)
    if prev and (t - prev).total_seconds() > 60 and not (prev.hour == 5 and t.hour in (6, 7)):
        gaps.append((prev.isoformat(), k))
    prev = t
print("gaps", len(gaps)); [print(" ", g) for g in gaps[:40]]
json.dump([[k, *B[k]] for k in ks], open("bars_dec.json", "w"))
