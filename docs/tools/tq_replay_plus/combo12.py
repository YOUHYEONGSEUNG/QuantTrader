# 진입 방식(스탑 돌파·지정가 되돌림)·변동성 수량·불타기 여유 실험 (10/23 밤)
#   ent        "market" 지금(신호 봉 마감 → 시장가) / "stop" 신호 봉 고가+1틱(매도는 저가−1틱) 돌파 때 / "limit" 종가 ∓ lim_k × 1분 ATR 되돌림 때
#   ent_n      스탑·지정가를 몇 봉 동안 걸어 둘지 (신호 다음 봉부터)
#   stop_k     "stop"에서 고가 대신 종가 ± stop_k × ATR을 쓰려면 숫자 (None이면 신호 봉 고가·저가 ± 1틱)
#   vol        None / ("risk", $) 손절 거리로 위험 금액 고정 / ("atr", n, lo, hi) 최근 n봉 평균 ATR 대비 지금 ATR로 수량 비례(역수), lo~hi배로 자름
#   add_room   불타기 때 현재가와 손절 사이가 add_room × ATR 이상일 때만
import math
from combo11 import Combo11H
from ind import TICK
from strat import rt

class Combo12(Combo11H):
    def __init__(self, b, ent="market", ent_n=1, lim_k=0.5, stop_k=None, vol=None, add_room=None, **kw):
        self.o12 = dict(ent=ent, n=ent_n, lim_k=lim_k, stop_k=stop_k, vol=vol, add_room=add_room)
        self.arm = None
        super().__init__(b, **kw)
        if vol and vol[0] == "atr":
            n = vol[1]; a = self.ts.atr; s = 0.0; self.atrRef = [math.nan] * b.n
            for i in range(b.n):
                s += a[i]
                if i >= n: s -= a[i - n]
                if i >= n: self.atrRef[i] = s / n

    def trader_qty(self, i):
        v = self.o12["vol"]
        if not v: return super().trader_qty(i)
        o = self.o11; q = o["qty"]; dist = abs(self.b.c[i] - self.initialStop)
        if v[0] == "risk" and dist > 0: q = min(q, int(v[1] / (dist * 2)))
        elif v[0] == "atr" and not math.isnan(self.atrRef[i]) and self.ts.atr[i] > 0:
            q = int(round(q * min(v[3], max(v[2], self.atrRef[i] / self.ts.atr[i]))))
        if o["cap"] and dist > 0: q = min(q, int(o["cap"] / (dist * 2)))
        return max(1, min(q, o["max_q"]))

    def _arm_px(self, i, d):
        o = self.o12; b = self.b; a = self.ts.atr[i]
        if o["ent"] == "stop":
            if o["stop_k"] is None: return rt(b.h[i] + TICK) if d > 0 else rt(b.l[i] - TICK)
            return rt(b.c[i] + d * o["stop_k"] * a)
        return rt(b.c[i] - d * o["lim_k"] * a)

    def _place(self, i0, d, path, pos):
        # 신호 봉 i0 기준으로 거래 계획을 다시 만들고(손절·수량·조각), 시장가 대신 걸어 둠
        from combo import Combo
        Combo.enter(self, i0, d, path, 100, False, math.nan, pos=pos)
        self.pending = [p for p in self.pending if p[0] != "entry"]
        self.e3 = dict(d=d, px=self.arm["px"], i=i0, kind=self.o12["ent"]); self.e3_renewed = True

    def route(self, i, cands):
        if self.o12["ent"] == "market": return super().route(i, cands)
        tcs = [c for c in cands if c["path"] in ("T51", "T54", "T55", "T56")]
        if tcs:
            c = tcs[0]; pos = ("SL" if c["d"] > 0 else "SS") if c["path"] in ("T55", "T56") else ("TL" if c["d"] > 0 else "TS")
            self.arm = dict(i0=i, d=c["d"], path=c["path"], pos=pos, px=self._arm_px(i, c["d"]), exp=i + self.o12["n"])
            self._place(i, c["d"], c["path"], pos); return
        if self.arm and i < self.arm["exp"]:
            a = self.arm; self._place(a["i0"], a["d"], a["path"], a["pos"]); return
        self.arm = None
        return super().route(i, cands)

    def on_bar_close(self, i):
        if self.posQty > 0: self.arm = None
        super().on_bar_close(i)

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        if path == "FORCE": self.arm = None
        return super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)

    def add_on(self, i):
        k = self.o12["add_room"]
        if k is not None and self.trader_trade() and self.posQty > 0 and not math.isnan(self.stopPrice):
            if abs(self.b.c[i] - self.stopPrice) < k * self.ts.atr[i]: return
        return super().add_on(i)
