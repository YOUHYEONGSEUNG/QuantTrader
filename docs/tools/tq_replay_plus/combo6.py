# 추세 진입(T51·T54) 청산 변형: 큰 추세를 타게 하려는 실험
import math
from combo5 import Combo5


class Combo6(Combo5):
    def __init__(self, b, final="base", weak="on", be="trader", trail_k=1.0, rx6=True, **kw):
        self.o6 = dict(final=final, weak=weak, be=be, trail_k=trail_k, rx6=rx6)
        super().__init__(b, **kw)

    def first_tp(self, i, px):
        if self.path in ("T51", "T54") and self.o6["be"] == "none":
            self.tm["bePending"] = False; self.tm["risk0"] = math.nan; return
        return super().first_tp(i, px)

    def trail(self, i):
        if not (self.path in ("T51", "T54") and self.trader_trade()) or self.o6["trail_k"] == 1.0:
            return super().trail(i)
        for s in self.live: self.chunks[s].fixed = self.tband(i, s)   # Combo.trail (밴드 익절 가격 옮기기)
        if self.fillQty <= 0: return
        b = self.b; d = self.dir
        self.trailExt = (b.h[i] if d > 0 else b.l[i]) if math.isnan(self.trailExt) else (max(self.trailExt, b.h[i]) if d > 0 else min(self.trailExt, b.l[i]))
        k = 2.5 if (self.tm["tp2"] or self.tm["strong"]) else 3.0 if self.tm["tp1"] else 0
        k *= self.o6["trail_k"]
        if k > 0 and self.sg.atr[i] > 0:
            keep = self.deferred
            self.move_stop(i, self.trailExt - d * k * self.sg.atr[i], "트레일링", b.c[i])
            if not math.isnan(keep) and (math.isnan(self.stopPrice) or d * (keep - self.stopPrice) > 0): self.deferred = keep

    def trend_exits(self, i):
        if self.path not in ("T51", "T54") or not self.trader_trade(): return super().trend_exits(i)
        if self.o6["rx6"] and self._rx(i): return
        ts, c, tm, o = self.ts, self.b.c[i], self.tm, self.o6
        if self.path == "T51":
            if tm["strong"]:
                if c < ts.sma[i] and o["final"] != "off": self.flatten(i, "STRONG_SMA")
                return
            up3, up2 = ts.xUp[3][i], ts.xUp[2][i]
            weak = ts.t2Bear[i] and (o["weak"] == "on" or (o["weak"] == "after_tp1" and tm["tp1"]))
            if up3:
                self.mbe()
                if not tm["tp1"]: self.exit_chunk(i, "UpL1", "TP1")
                self.exit_chunk(i, "UpL2", "TP2"); tm["tp1"] = True; tm["strong"] = True; return
            if not tm["tp1"]:
                if weak: self.flatten(i, "WEAK"); return
                if up2: self.mbe(); self.exit_chunk(i, "UpL1", "TP1"); tm["tp1"] = True
            elif weak: self.flatten(i, "WEAK")
        else:
            above = c > ts.sma[i]; f = o["final"]
            final = (above if f == "base" else above and tm["tp1"] if f == "after_tp1" else above and tm["tp2"] if f == "after_tp2"
                     else ts.rsi[i] < 20 if f == "rsi" else False)
            t1 = c <= ts.band[-2][i] or ts.mUp[i] or ts.k20[i]; t2 = c <= ts.band[-3][i]
            if final: self.flatten(i, "FINAL"); return
            if not tm["tp1"] and t1: self.mbe(); self.exit_chunk(i, "DnS1", "TP1"); tm["tp1"] = True
            if t2 and not tm["tp2"]: self.exit_chunk(i, "DnS2", "TP2"); tm["tp2"] = True
