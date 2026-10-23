# 7레짐을 청산에 쓰기: 1차 뒤 본절·트레일링을 레짐이 같은 방향인 동안 늦춤
import math
import combo
from combo8 import Combo8
class Combo9(Combo8):
    def __init__(self, b, be_reg=False, trail_reg=None, be_min_r=None, **kw):
        self.o9 = dict(be_reg=be_reg, trail_reg=trail_reg, be_min_r=be_min_r); self.bePendReg = False
        super().__init__(b, **kw)
    def same(self):
        r = self.sc.r7 if getattr(self.sc, "ready", False) else 0
        return self.dir * r in (2, 3)
    def first_tp(self, i, px):
        if self.path in ("T51", "T54") and self.trader_trade():
            if self.o9["be_min_r"] is not None:
                keep = combo.Combo.BE_MIN_R; combo.Combo.BE_MIN_R = self.o9["be_min_r"]
                try:
                    if self.o9["be_reg"] and self.same(): self.tm["bePending"] = False; self.bePendReg = True; return
                    return super().first_tp(i, px)
                finally: combo.Combo.BE_MIN_R = keep
            if self.o9["be_reg"] and self.same():
                self.tm["bePending"] = False; self.tm["risk0"] = math.nan; self.bePendReg = True; return
        return super().first_tp(i, px)
    def trail(self, i):
        if self.bePendReg and self.posQty > 0 and not self.same():
            self.bePendReg = False; super(Combo8, self).first_tp(i, self.b.c[i]) if False else combo.Combo.first_tp(self, i, self.b.c[i])
        tr = self.o9["trail_reg"]
        if tr and self.path in ("T51", "T54") and self.trader_trade() and self.same() and self.sc.r7 * self.dir == 2:
            keep = self.o8["t2k"]; self.o8["t2k"] = tr
            try:
                # 추세가 이어지는 동안: 1차 뒤 3 ATR도 tr배
                return self._trail_k(i, tr)
            finally: self.o8["t2k"] = keep
        return super().trail(i)
    def _trail_k(self, i, mult):
        for s in self.live: self.chunks[s].fixed = self.tband(i, s)
        if self.fillQty <= 0: return
        b = self.b; d = self.dir
        self.trailExt = (b.h[i] if d > 0 else b.l[i]) if math.isnan(self.trailExt) else (max(self.trailExt, b.h[i]) if d > 0 else min(self.trailExt, b.l[i]))
        k = (2.5 if (self.tm["tp2"] or self.tm["strong"]) else 3.0 if self.tm["tp1"] else 0) * mult
        if k > 0 and self.sg.atr[i] > 0:
            keep = self.deferred
            self.move_stop(i, self.trailExt - d * k * self.sg.atr[i], "트레일링", b.c[i])
            if not math.isnan(keep) and (math.isnan(self.stopPrice) or d * (keep - self.stopPrice) > 0): self.deferred = keep
    def reset_trade(self):
        super().reset_trade(); self.bePendReg = False
