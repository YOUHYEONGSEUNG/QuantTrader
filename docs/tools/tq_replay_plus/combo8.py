import math
from combo7 import Combo7
class Combo8(Combo7):
    def __init__(self, b, cont_vmax=None, run_reset=False, age_max=None, trail2_k=1.0, **kw):
        self.o8 = dict(vmax=cont_vmax, reset=run_reset, age=age_max, t2k=trail2_k); self._cont8 = False
        super().__init__(b, **kw)
    def update_session(self, i):
        super().update_session(i)
        if self.o8["reset"] and self.b.t[i][11:16] == "21:30": self.runLen = 0; self.usedRun = False
    def evaluate(self, i):
        base = super(Combo7, self).evaluate(i)
        out = super().evaluate(i); o = self.o8; sc = self.sc
        if out and not base:   # 지속 진입 후보
            d = out[0]["d"]
            if o["vmax"] is not None and d * sc.V >= o["vmax"]: self.usedRun = False; return []
        if out and o["age"] is not None and self.runLen > o["age"] and out[0]["path"] in ("T51", "T54"): return []
        return out
    def trail(self, i):
        if not (self.path in ("T51", "T54") and self.trader_trade()) or self.o8["t2k"] == 1.0: return super().trail(i)
        for s in self.live: self.chunks[s].fixed = self.tband(i, s)
        if self.fillQty <= 0: return
        b = self.b; d = self.dir
        self.trailExt = (b.h[i] if d > 0 else b.l[i]) if math.isnan(self.trailExt) else (max(self.trailExt, b.h[i]) if d > 0 else min(self.trailExt, b.l[i]))
        k = 2.5 * self.o8["t2k"] if (self.tm["tp2"] or self.tm["strong"]) else 3.0 if self.tm["tp1"] else 0
        if k > 0 and self.sg.atr[i] > 0:
            keep = self.deferred
            self.move_stop(i, self.trailExt - d * k * self.sg.atr[i], "트레일링", b.c[i])
            if not math.isnan(keep) and (math.isnan(self.stopPrice) or d * (keep - self.stopPrice) > 0): self.deferred = keep
