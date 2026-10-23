# 대회 시간만이 아니라 하루 모든 2시간 구간에서 시험 (10/23 오후)
#   봉 시각을 h시간 밀어 다른 시간대의 2시간이 21:30~23:30 자리에 오게 함. 지표는 봉 순서만 보므로 그대로이고,
#   대회 시간 규칙(시작·마감·흔들림 판단·21:45 전 수량·불타기 마감·강제 진입)도 그 구간에 똑같이 적용됨.
#   30분 단위로 밀어 3분·10분봉 경계도 그대로. 구간 안 1분봉이 110개 미만(정비 시간·주말)이면 뺌.
#   사용: py all_hours.py            → 설정별 시간대 표 (결과 tune_hours.json)
import sys, os, json, datetime as dt, collections as co
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ind import Bars
from common import DEFAULT, TP, DATA
from combo34 import Combo34
import score
score.Score.C = 0.0; score.Score.V = 0.0   # 밀린 첫날 점수 계산 전에 강제 진입이 나오면 방향을 매수로 (시험용, 재현기 기본 동작은 안 바뀜)

FILES = [("bars_aug.json", "2026-07-16", "2026-09-18"), ("bars_oos.json", "2026-09-21", "2026-10-02"), ("bars_oct_nt.json", "2026-10-05", "2026-10-21")]
STARTS = ["21:30", "23:30", "01:30", "03:30", "05:30", "07:30", "09:30", "11:30", "13:30", "15:30", "17:30", "19:30"]   # KST 구간 시작
_raw = {}

def shifted(f, start):
    if f not in _raw: _raw[f] = json.load(open(os.path.join(DATA, f)))
    hh, mm = map(int, start.split(":")); h = (21 * 60 + 30 - (hh * 60 + mm)) % (24 * 60)
    if h == 0: return Bars(_raw[f])
    d = dt.timedelta(minutes=h)
    return Bars([[(dt.datetime.fromisoformat(r[0]) + d).isoformat(), *r[1:]] for r in _raw[f]])

def run_one(args):
    name, kw, tp, start = args
    out = {}
    for f, a, z in FILES:
        b = shifted(f, start)
        nwin = co.Counter(t[:10] for t in b.t if "21:30" <= t[11:16] <= "23:29")
        s = Combo34(b, tparams=TP(**tp), **kw); s.p.MinEntries = 4; d = co.defaultdict(float)
        for t in s.run():
            if "21:30" <= t["t0"][11:16] <= "23:29": d[t["t0"][:10]] += t["usd"] - (t["qty"] + t.get("addq", 0)) * 2
        # 원래 날짜 범위 안(밀어도 날짜는 최대 하루 차이), 구간이 온전한 날만
        for day, n in nwin.items():
            if n >= 110 and a <= day <= (dt.date.fromisoformat(z) + dt.timedelta(days=1)).isoformat(): out[f"{start}|{day}"] = d.get(day, 0.0)
    return name, start, out

CF = [("지금", {}, {}),
      ("추세 지속 진입 끔", dict(cont_k=None), {}),
      ("지속 진입 속도 조건 끔", dict(cont_vmax=None), {}),
      ("손절 최소 1.0", {}, dict(MinStopAtr=1.0)),
      ("보수안: 지속 진입 끔 + 손절 1.0", dict(cont_k=None), dict(MinStopAtr=1.0)),
      ("흔들리는 장 눌림만 끔", dict(chop_er=None), {}),
      ("5.4 SMA20 보류 끔", dict(sma_block=None, sma_wait=None), {}),
      ("T1 5", {}, dict(T1Window=5)),
      ("불타기 끔", dict(add_q=0), {}),
      ("30계약, 불타기 끔", dict(qty=30, add_q=0, early_q=None), {}),
      ("매수 SMA20 대칭", dict(long_block=10, long_exit="always"), {}),
      ("흔들림 판단 대회 전 60봉 포함", dict(chop_src=("roll", 60)), {})]

if __name__ == "__main__":
    from multiprocessing import Pool
    jobs = [(n, {**DEFAULT, **k}, t, s) for n, k, t in CF for s in STARTS]
    tab = co.defaultdict(dict)
    with Pool(4) as pool:
        for k, (name, start, out) in enumerate(pool.imap_unordered(run_one, jobs)):
            tab[name].update(out); print(f"{k+1}/{len(jobs)} {name} {start}", file=sys.stderr, flush=True)
    json.dump(tab, open("tune_hours.json", "w", encoding="utf-8"), ensure_ascii=False)
    print("saved")
