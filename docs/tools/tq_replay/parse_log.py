# Playback 로그(v6) → 실행별 봉 기록과 사건 줄. 출력: logbars.json {run_line: {...}}
import re, json, sys, os
LOG = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8", "TQ_log_v6_MNQ.txt")
ts_re = re.compile(r"^\[(\d{4}-\d{2}-\d{2}) (오전|오후) (\d{1,2}):(\d{2}):(\d{2})\]\[([^\]]+)\]\[TQ_Strategy\] (.*)$")
def ts(d, ap, h, m, s):
    h = int(h) % 12 + (12 if ap == "오후" else 0)
    return f"{d}T{h:02d}:{m}:{s}"
runs = []; cur = None
for line in open(LOG, encoding="utf-8", errors="replace"):
    line = line.rstrip("\n")
    if line.startswith("===="):
        cur = dict(header=line, bars={}, events=[]); runs.append(cur); continue
    mt = ts_re.match(line)
    if not mt or cur is None: continue
    t = ts(*mt.groups()[:5]); inst = mt.group(6); body = mt.group(7)
    if "시고저종=" in body and "레짐7=" in body:
        kv = dict(re.findall(r"(점수C|속도|연속S|연속F|레짐7|전환의심)=(-?[\d.]+)", body))
        o, h, l, c = map(float, body.split("시고저종=")[1].split()[0].split("/"))
        cand = body.split(" 후보 ")[1].split(" 보유=")[0] if " 후보 " in body else ""
        hold = body.split(" 보유=")[1].split(" 시고저종=")[0] if " 보유=" in body else ""
        m2 = re.search(r"추세=(-?\d) S=([+-]?\d) F=([+-]?\d)", body)
        cur["bars"][t] = dict(o=o, h=h, l=l, c=c, C=float(kv.get("점수C", "nan")), V=float(kv.get("속도", "nan")), S=float(kv.get("연속S", "nan")),
                             F=float(kv.get("연속F", "nan")), r7=int(kv.get("레짐7", 0)), warn=int(kv.get("전환의심", 0)), cand=cand, hold=hold,
                             st=int(m2.group(1)), Si=int(m2.group(2)), Fi=int(m2.group(3)), inst=inst)
    elif "분봉 마감" not in body and "시고저종=" not in body:
        cur["events"].append((t, body))
runs = [r for r in runs if r["bars"]]
for r in runs: r["inst"] = next(iter(r["bars"].values()))["inst"]
json.dump(runs, open("logruns.json", "w", encoding="utf-8"), ensure_ascii=False)
for i, r in enumerate(runs):
    ks = sorted(r["bars"]); print(i, r["inst"], ks[0], ks[-1], len(ks), len(r["events"]))
