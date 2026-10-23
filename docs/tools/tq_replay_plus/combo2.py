# 청산을 부품으로 나눈 조합 재현기. 트레이더 진입(T51·T54·T55·T56)의 청산에만 적용 (v6 돌파 경로는 v6 청산 그대로)
#   stop   "trader" 전저·전고점과 ATR×1 중 먼 쪽 / "v6" 전저·전고점을 ATR×[1.5,3](횡보 [1,2])로 자름, 최소 8pt
#   be     "trader" 1차 익절 체결 때(유리할 때만), 횡보는 1R 도달 / "v6" 모든 진입 0.8R 도달 + 1차 뒤 평단(0.5R 모자라면 1차 체결가 ∓ 1.5 ATR)
#   trail  False 없음 / True 1차 뒤 최고가 − 3 ATR, 2차(+3배·−3배) 뒤 2.5 ATR
#   tp     "band" 밴드 익절(±2·±3배, 봉 마감 밴드 돌파 청산 포함) / "R" 1차 0.5R·2차 2.5R 지정가 (횡보 1차는 진입가 ± 2 ATR)
#   rx     False 없음 / True 추세 끝(청산용 판정 0, 그 방향이었던 적 있을 때)·반대 추세면 전량
import math
from combo import Combo, TRADER
from strat import rt


class Combo2(Combo):
    def __init__(self, b, stop="trader", be="trader", trail=False, tp="band", rx=False, tparams=None, **kw):
        self.c_stop, self.c_be, self.c_trail, self.c_tp, self.c_rx = stop, be, trail, tp, rx
        super().__init__(b, exits="trader", tp=tparams, **kw)

    def calc_stop(self, px):
        if self.trader_trade() and self.c_stop == "v6":
            return Combo.__mro__[1].calc_stop(self, px)   # Strat.calc_stop (경로가 T…라 추세/횡보 범위)
        return super().calc_stop(px)

    def tband(self, i, sig):
        if self.c_tp == "R" and self.path in TRADER: return math.nan   # R 익절은 체결 뒤 고정 가격으로
        return super().tband(i, sig)

    def on_entry_fill(self, i, price, qty, tick_px):
        super().on_entry_fill(i, price, qty, tick_px)
        if self.trader_trade() and self.c_tp == "R" and self.posQty > 0 and not math.isnan(self.tr.get("stop0", math.nan)):
            a = self.avg(); r = abs(a - self.tr["stop0"]); d = self.dir
            for c in self.chunks.values():
                if self.path in ("T55", "T56"):
                    c.fixed = a + d * 2 * self.stopAtr if c.stage == 1 else math.nan
                else:
                    c.fixed = a + d * (0.5 if c.stage == 1 else 2.5) * r if c.stage in (1, 2) else math.nan

    def first_tp(self, i, px):
        if self.c_be == "v6" and self.trader_trade():
            self.tm["bePending"] = False
            if self.fillQty <= 0 or not self.live: return
            a = self.avg(); r = abs(a - self.tr["stop0"]) if not math.isnan(self.tr.get("stop0", math.nan)) else math.nan
            gain = self.dir * (px - a)
            if math.isnan(r) or r <= 0 or gain >= 0.5 * r - 0.25: self.move_stop(i, a, "평단", px)
            else: self.move_stop(i, px - self.dir * 1.5 * self.sg.atr[i], "1차 뒤 ATR", px)
            return
        super().first_tp(i, px)

    def protect(self, i):
        if not self.trader_trade(): return super().protect(i)
        if self.c_be == "v6":
            if self.tm["tp1"] or self.beDone or self.fillQty <= 0 or math.isnan(self.tr.get("stop0", math.nan)): return
            a = self.avg(); r = abs(a - self.tr["stop0"])
            if r > 0 and self.tr["mfe"] >= 0.8 * r: self.move_stop(i, a, "이익 보호 본절", self.b.c[i])
            return
        super().protect(i)

    def trail(self, i):
        super().trail(i)   # 트레이더: 밴드 익절 가격 옮기기 (R 익절이면 NaN 그대로)
        if not (self.trader_trade() and self.c_trail) or self.fillQty <= 0: return
        b = self.b; d = self.dir
        self.trailExt = (b.h[i] if d > 0 else b.l[i]) if math.isnan(self.trailExt) else (max(self.trailExt, b.h[i]) if d > 0 else min(self.trailExt, b.l[i]))
        k = 2.5 if (self.tm["tp2"] or self.tm["strong"]) else 3.0 if self.tm["tp1"] else 0
        if k > 0 and self.sg.atr[i] > 0:
            keep = self.deferred if getattr(self, "KEEP_BE", True) else math.nan   # C#처럼 미뤄 둔 본절 이동은 트레일링이 지우지 않음
            self.move_stop(i, self.trailExt - d * k * self.sg.atr[i], "트레일링", b.c[i])
            if not math.isnan(keep) and (math.isnan(self.stopPrice) or d * (keep - self.stopPrice) > 0): self.deferred = keep

    def on_target_fill(self, i, c, price, cur_px):
        super().on_target_fill(i, c, price, cur_px)
        if self.trader_trade() and self.c_tp == "R" and c.stage == 2: self.tm["tp2"] = True

    def _rx(self, i):
        if not (self.trader_trade() and self.c_rx): return False
        xs = self.sc.ExitState()
        if xs == self.dir: self.sawOwn = True
        if xs == -self.dir: self.flatten(i, "OPP_TREND"); return True
        if xs == 0 and self.sawOwn: self.flatten(i, "TREND_END"); return True
        return False

    def trend_exits(self, i):
        if self._rx(i): return
        if self.trader_trade() and self.c_tp == "R":
            # R 익절: 밴드 돌파 청산(+2·+3배, −2·−3배)은 지정가가 대신함. 가격 외 조건은 트레이더 그대로
            ts, c, tm = self.ts, self.b.c[i], self.tm
            if self.path == "T51":
                weak = ts.t2Bear[i]
                if tm["tp2"]:
                    if c < ts.sma[i]: self.flatten(i, "STRONG_SMA")
                    return
                if weak: self.flatten(i, "WEAK")
            else:
                final = c > ts.sma[i]
                if final: self.flatten(i, "FINAL"); return
                if not tm["tp1"] and (ts.mUp[i] or ts.k20[i]): self.mbe(); self.exit_chunk(i, "DnS1", "TP1"); tm["tp1"] = True
            return
        super().trend_exits(i)

    def side_exits(self, i):
        if self._rx(i): return
        if self.trader_trade() and self.c_tp == "R":
            ts, b, tm = self.ts, self.b, self.tm; d = self.dir
            if tm["side"] is None:
                aT = (ts.mDn[i] or ts.dead[i]) if d > 0 else (ts.mUp[i] or ts.gold[i])
                bT = ts.k80[i] if d > 0 else ts.k20[i]
                aR = b.h[i] >= ts.band[3][i] if d > 0 else b.l[i] <= ts.band[-3][i]
                bR = ts.t2Bear[i] if d > 0 else ts.t2Bull[i]
                if aR or bR: self.flatten(i, "SIDE_REST"); return
                if tm["tp1"]: tm["side"] = "A"
                elif aT or bT:
                    tm["side"] = "A" if aT else "B"; self.mbe(); self.exit_chunk(i, "SideL1" if d > 0 else "SideS1", "TP1"); tm["tp1"] = True
            elif tm["side"] == "A":
                if (b.h[i] >= ts.band[3][i]) if d > 0 else (b.l[i] <= ts.band[-3][i]): self.flatten(i, "SIDE_A")
            else:
                if (ts.t2Bear[i] if d > 0 else ts.t2Bull[i]): self.flatten(i, "SIDE_B")
            return
        super().side_exits(i)
