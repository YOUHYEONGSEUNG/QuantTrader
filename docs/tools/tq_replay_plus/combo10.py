# 매도 청산을 매수와 같은 구조로: 1차 −2배 밴드만(지표 조건 없음), 2차 −3배 + 강한 모멘텀, 1차 전 약한 청산 T2Bull, 강한 뒤 종가>SMA
from combo9 import Combo9
class Combo10(Combo9):
    def __init__(self, b, short_mirror=False, **kw):
        self.short_mirror = short_mirror; super().__init__(b, **kw)
    def trend_exits(self, i):
        if not (self.short_mirror and self.path == "T54" and self.trader_trade()): return super().trend_exits(i)
        if self.o6["rx6"] and self._rx(i): return
        ts, c, tm = self.ts, self.b.c[i], self.tm
        if tm["strong"]:
            if c > ts.sma[i]: self.flatten(i, "STRONG_SMA")
            return
        dn3, dn2, weak = ts.xDn[3][i], ts.xDn[2][i], ts.t2Bull[i]
        if dn3:
            self.mbe()
            if not tm["tp1"]: self.exit_chunk(i, "DnS1", "TP1")
            self.exit_chunk(i, "DnS2", "TP2"); tm["tp1"] = True; tm["tp2"] = True; tm["strong"] = True; return
        if not tm["tp1"]:
            if weak: self.flatten(i, "WEAK"); return
            if dn2: self.mbe(); self.exit_chunk(i, "DnS1", "TP1"); tm["tp1"] = True
        elif weak: self.flatten(i, "WEAK")
    def on_target_fill(self, i, c, price, cur_px):
        super().on_target_fill(i, c, price, cur_px)
        if self.short_mirror and c.sig == "DnS2" and self.tm: self.tm["strong"] = True; self.tm["tp1"] = True
