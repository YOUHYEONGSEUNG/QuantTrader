# all_hours.py 결과(tune_hours.json) 표로 보기
import json, random, statistics as st, datetime as dt, collections as co
T = json.load(open("tune_hours.json", encoding="utf-8")); cur = T["지금"]
keys = sorted(cur); bys = co.defaultdict(list)
for k in keys: bys[k.split("|")[0]].append(k)
S = [s for s in ["21:30", "23:30", "01:30", "03:30", "05:30", "07:30", "09:30", "11:30", "13:30", "15:30", "17:30", "19:30"] if bys[s]]
print("지금 설정, 시간대별 (KST 시작)\n| 구간 | 날 | 하루 평균 | 합 | 이긴 날 | 최악 |\n|---|---|---|---|---|---|")
for s in S:
    v = [cur[k] for k in bys[s]]
    print(f"| {s} | {len(v)} | {st.mean(v):+,.0f} | {sum(v):+,.0f} | {100*sum(x>0 for x in v)/len(v):.0f}% | {min(v):+,.0f} |")
oth = [k for k in keys if not k.startswith("21:30")]
wk = {k: "%d-W%02d" % dt.date.fromisoformat(k.split("|")[1]).isocalendar()[:2] for k in keys}; W = sorted(set(wk.values()))
rnd = random.Random(3)
print(f"\n| 설정 | 대회 시간 차이 | 다른 {len(S)-1}개 시간대 차이 | 나아진 시간대 수 | 주 단위 붓스트랩 > 0 (다른 시간대) |\n|---|---|---|---|---|")
for name, v in T.items():
    if name == "지금": continue
    dcon = sum(v[k] - cur[k] for k in bys["21:30"]); doth = sum(v[k] - cur[k] for k in oth)
    better = sum(sum(v[k] - cur[k] for k in bys[s]) > 0 for s in S[1:])
    byw = co.defaultdict(float)
    for k in oth: byw[wk[k]] += v[k] - cur[k]
    bs = [sum(byw[rnd.choice(W)] for _ in W) for _ in range(3000)]
    print(f"| {name} | {dcon:+,.0f} | {doth:+,.0f} | {better}/{len(S)-1} | {100*sum(x>0 for x in bs)/len(bs):.0f}% |")
