# 데이터 구간과 지금 C# 기본값(TQ_Strategy, 브랜치 test/score-v1-plus)에 맞춘 재현기 설정
import os, json, collections as co
from ind import Bars
import combo, combo2, combo3
from combo4 import Combo4
from combo19 import Combo20
from team import P as TP

DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")
# 구간 이름: (1분봉 파일, 첫날, 끝날). 파일은 그 앞 며칠까지 들어 있어 지표가 채워진 뒤 첫날을 시작함
SETS = {"7월": ("bars_sep.json", "2026-07-17", "2026-07-31"), "9월초": ("bars_sepm.json", "2026-09-01", "2026-09-17"),
        "9/21~10/2": ("bars_oos.json", "2026-09-21", "2026-10-02"), "10월": ("bars_oct_nt.json", "2026-10-05", "2026-10-21")}
_B = {}
def bars(m):
    if m not in _B: _B[m] = Bars(json.load(open(os.path.join(DATA, SETS[m][0]))))
    return _B[m]

# C#과 같게: 미뤄 둔 본절 이동은 트레일링이 지우지 않음, 1차 이익 < 0.25R이면 본절 안 옮김, 강제 채우기는 남은 봉 수로 시작
combo2.Combo2.KEEP_BE = True; combo.Combo.BE_FALLBACK_ATR = 0; combo.Combo.BE_MIN_R = 0.25; Combo4.force_mode = "late"
def _risk_qty(self, i):   # (Combo11 이전 클래스용) RiskCapUsd $4,000: 손절 거리 × $2 × 수량이 넘으면 수량을 줄임
    q = 30; dist = abs(self.b.c[i] - self.initialStop)
    if dist > 0: q = max(1, min(q, int(4000 / (dist * 2))))
    return q
combo3.Combo3.trader_qty = _risk_qty

# 지금 C# 기본값. 키 이름은 Combo2~7 인자 (C# 설정 이름은 README 표)
DEFAULT = dict(force=True, reg="v6", tstate="trendpull", sstate="off", warn=False, news=False, trail=True, rx=True,
               chop_er=0.15, chop_mode="pull", cont_k=8, cont_mid=False, cont_once=True, cont_chop=True, cont_vmax=0.15,
               qty=50, cap=4000, add_q=30, add_when="tp2", add_end="23:10", max_q=80, add_room=1,
               sma_block="short", sma_wait=10, tp1_mid=False, ext_skip=True, early_q=("21:45", 25))
MIN_ENTRIES = 4

def trades(min_entries=MIN_ENTRIES, **kw):
    """구간별 거래 목록 {구간: [거래]}. 대회 구간(21:30~23:29 진입)만"""
    cfg = {**DEFAULT, **kw}; out = {}
    for m, (f, a, z) in SETS.items():
        st = Combo20(bars(m), tparams=TP(), **cfg); st.p.MinEntries = min_entries
        out[m] = [t for t in st.run() if a <= t["t0"][:10] <= z and "21:30" <= t["t0"][11:16] <= "23:29"]
    return out

def summary(out):
    r = {m: round(sum(t["usd"] for t in v)) for m, v in out.items()}
    d = co.defaultdict(float)
    for v in out.values():
        for t in v: d[t["t0"][:10]] += t["usd"]
    r["합계"] = sum(r[m] for m in SETS); r["최악의 날"] = round(min(d.values())) if d else 0
    r["손실일"] = sum(1 for x in d.values() if x < 0); r["건수"] = sum(len(v) for v in out.values())
    return r

def day_moves():
    """날짜별 대회 구간(21:30~23:27) 시가·종가·고저·종가 변화 합"""
    day = {}
    for m, (f, a, z) in SETS.items():
        b = bars(m)
        for i in range(b.n):
            d, hm = b.t[i][:10], b.t[i][11:16]
            if not (a <= d <= z and "21:30" <= hm <= "23:27"): continue
            s = day.setdefault(d, dict(o=b.o[i], h=b.h[i], l=b.l[i], c=b.c[i], path=0.0, pc=None))
            s["h"] = max(s["h"], b.h[i]); s["l"] = min(s["l"], b.l[i]); s["c"] = b.c[i]
            if s["pc"] is not None: s["path"] += abs(b.c[i] - s["pc"])
            s["pc"] = b.c[i]
    return day
