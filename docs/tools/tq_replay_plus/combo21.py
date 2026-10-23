# 흔들리는 장 실험 (10/23 밤 5)
#   chop_src   ("sess", n)  지금 방식: 대회 시작부터 n봉 지난 뒤 그날 효율비 ≤ chop_er
#              ("roll", w)  최근 w봉 효율비(대회 전 봉 포함, 21:30부터 바로) ≤ chop_er
#   chop_act   "pull" 추세 진입은 눌림만(지금) / "none" 추세 진입 안 함 / "half" 수량 절반(눌림만은 그대로)
#   chop_side  True: 흔들리는 장이면 7레짐과 상관없이 팀 횡보 진입(5.5·5.6: 밴드 하단 매수·상단 매도, 청산도 5.5·5.6)
import math
from combo19 import Combo20

class Combo21(Combo20):
    def __init__(self, b, chop_src=("sess", 60), chop_act="pull", chop_side=False, **kw):
        self.o21 = dict(src=chop_src, act=chop_act, side=chop_side)
        super().__init__(b, **kw)
        if chop_src[0] == "roll":
            w = chop_src[1]; c = b.c; self.rerC = [math.nan] * b.n; path = 0.0
            for i in range(1, b.n):
                path += abs(c[i] - c[i - 1])
                if i > w: path -= abs(c[i - w] - c[i - w - 1])
                if i >= w and path > 0: self.rerC[i] = abs(c[i] - c[i - w]) / path

    def chop(self, i):
        er = self.o5["chop_er"]
        if er is None: return False
        src = self.o21["src"]
        if src[0] == "sess": return self.sessBars >= src[1] and self.sess_er(i) <= er
        v = self.rerC[i]; return not math.isnan(v) and v <= er

    def evaluate(self, i):
        act = self.o21["act"]
        keep = self.o5["chop_mode"]
        if act == "none": self.o5["chop_mode"] = "none"
        try: out = super().evaluate(i)
        finally: self.o5["chop_mode"] = keep
        if self.o21["side"] and not out and self.chop(i) and self.inwin(i):
            ts = self.ts
            if not self.blockSideLong and ts.eSideLong[i]: out = [dict(path="T55", d=1, q=100, fb=False, rr=math.nan)]
            elif not self.blockSideShort and ts.eSideShort[i]: out = [dict(path="T56", d=-1, q=100, fb=False, rr=math.nan)]
        return out

    def trader_qty(self, i):
        q = super().trader_qty(i)
        if self.o21["act"] == "half" and self.chop(i): q = max(1, q // 2)
        return q
