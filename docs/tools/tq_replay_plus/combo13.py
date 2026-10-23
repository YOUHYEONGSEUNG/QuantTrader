# 추세일 모드 실험: 그날 대회 구간 효율비가 높고(td_er 이상, td_n봉 지난 뒤) 7레짐이 그날 방향이면
#   td_reent  지속 진입을 추세 구간마다 한 번이 아니라 청산 뒤에도 다시 허용
#   td_only   그날 반대 방향 진입 막기
#   td_trail  그날 방향 거래의 트레일링 배수
#   td_nobe   그날 방향 거래는 1차 뒤 본절 안 옮김
import math
from combo12 import Combo12

class Combo13(Combo12):
    def __init__(self, b, td_n=None, td_er=0.25, td_reent=False, td_only=False, td_trail=1.0, td_nobe=False, **kw):
        self.o13 = dict(n=td_n, er=td_er, reent=td_reent, only=td_only, trail=td_trail, nobe=td_nobe)
        super().__init__(b, **kw)

    def tday(self, i):
        o = self.o13
        if o["n"] is None or self.sessBars < o["n"] or math.isnan(self.sessStart): return 0
        if self.sess_er(i) < o["er"]: return 0
        return 1 if self.b.c[i] > self.sessStart else -1

    def evaluate(self, i):
        td = self.tday(i); o = self.o13
        if td and o["reent"] and self.runDir == td: self.usedRun = False
        out = super().evaluate(i)
        if td and o["only"]: out = [c for c in out if c["d"] == td or c["path"] not in ("T51", "T54", "T55", "T56")]
        return out

    def first_tp(self, i, px):
        if self.o13["nobe"] and self.path in ("T51", "T54") and self.tday(i) == self.dir:
            self.tm["bePending"] = False; self.tm["risk0"] = math.nan; return
        return super().first_tp(i, px)

    def trail(self, i):
        k = self.o13["trail"]
        if k != 1.0 and self.path in ("T51", "T54") and self.trader_trade() and self.tday(i) == self.dir:
            return self._trail_k(i, k)
        return super().trail(i)
