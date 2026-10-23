# 10월 손실 분석에서 나온 일반 패턴 실험 (10/23 밤 8)
#   flip_exit  None / ("all"|"tp1"|"half", lvl): 보유 중 7레짐 부호가 포지션 반대가 되면 청산. lvl 1 = 반대 시작의심(±1)부터, 2 = 반대 추세(±2)만
#              "tp1" = 1차 익절 전에만, "half" = 한 번 남은 수량의 절반(먼 조각부터)
#   mid_skip   (lo, hi): 진입 때 종가가 직전 30봉 극값(매수 최고가·매도 최저가)에서 lo~hi ATR 물러나 있으면 진입 안 함
import math
from combo19 import Combo20

class Combo24(Combo20):
    def __init__(self, b, flip_exit=None, mid_skip=None, **kw):
        self.o24 = dict(flip=flip_exit, mid=mid_skip); self.flipDone = False
        super().__init__(b, **kw)

    def evaluate(self, i):
        out = super().evaluate(i); m = self.o24["mid"]
        if not m or not out: return out
        b = self.b; a = self.ts.atr[i]
        res = []
        for x in out:
            if x["path"] in ("T51", "T54") and a > 0:
                d = x["d"]; ext = max(b.h[i - 30:i]) if d > 0 else min(b.l[i - 30:i])
                back = d * (ext - b.c[i]) / a
                if m[0] <= back < m[1]: continue
            res.append(x)
        return res

    def trend_exits(self, i):
        f = self.o24["flip"]
        if f and self.path in ("T51", "T54") and self.trader_trade() and getattr(self.sc, "ready", False):
            r = self.sc.r7 * self.dir; mode, lvl = f
            hit = r < 0 and (lvl == 1 or abs(self.sc.r7) == 2)
            if hit and not (mode == "tp1" and self.tm["tp1"]):
                if mode == "half":
                    if not self.flipDone:
                        self.flipDone = True
                        self.exit_far(i, max(1, self.posQty // 2), "R7_FLIP_HALF")
                else:
                    self.flatten(i, "R7_FLIP"); return
        return super().trend_exits(i)

    def reset_trade(self):
        super().reset_trade(); self.flipDone = False
