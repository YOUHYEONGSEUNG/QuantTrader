# 범위가 넓은·좁은 흔들리는 날 실험 (10/23 밤 7)
#   그날 흔들림 = 대회 시작 60봉 뒤 그날 효율비 ≤ chop_er (지금 판단 그대로), 범위 = 대회 시작부터 지금까지 고가−저가 ÷ 1분 ATR
#   wide_fade (k, band)  흔들림 + 범위 ≥ k ATR이면 ±band배 선 터치 → 반전(T1) 때 되돌림 진입(밴드 하단 매수·상단 매도), 익절은 전량 중심선(EMA26), 손절은 팀 손절
#   narrow    (k, act)   흔들림 + 범위 < k ATR이면 act: "half" 수량 절반 / "none" 추세 진입 안 함
import math
from combo21 import Combo21

class Combo23(Combo21):
    def __init__(self, b, wide_fade=None, narrow=None, **kw):
        self.o23 = dict(fade=wide_fade, narrow=narrow); self.fadeTrade = False
        super().__init__(b, **kw)
        n = b.n; self.sH = [math.nan] * n; self.sL = [math.nan] * n; hi = lo = math.nan; cur = None
        for i in range(n):
            d, hm = b.t[i][:10], b.t[i][11:16]
            if "21:30" <= hm <= "23:30":
                if cur != d: cur = d; hi, lo = b.h[i], b.l[i]
                else: hi, lo = max(hi, b.h[i]), min(lo, b.l[i])
                self.sH[i], self.sL[i] = hi, lo
        if wide_fade:
            ts = self.ts; B = ts.band; k = wide_fade[1]; T1B = self.tp.T1Window + self.tp.MacdConfirmBars - 1
            ru = [ts.mUp[i] or ts.gold[i] for i in range(n)]; rd = [ts.mDn[i] or ts.dead[i] for i in range(n)]
            def ws(touch, rev):
                out = [False] * n; w = -1
                for i in range(1, n):
                    f = False
                    if w >= 0:
                        w += 1
                        if rev[i]: f = True; w = -1
                        elif w >= T1B: w = -1
                    if touch[i]: w = 0
                    out[i] = f
                return out
            self.fadeL = ws([i > 0 and b.l[i - 1] > B[-k][i - 1] and b.l[i] <= B[-k][i] for i in range(n)], ru)
            self.fadeS = ws([i > 0 and b.h[i - 1] < B[k][i - 1] and b.h[i] >= B[k][i] for i in range(n)], rd)

    def rngAtr(self, i):
        a = self.ts.atr[i]
        return (self.sH[i] - self.sL[i]) / a if a > 0 and not math.isnan(self.sH[i]) else math.nan

    def evaluate(self, i):
        out = super().evaluate(i); o = self.o23
        ch = self.chop(i); r = self.rngAtr(i)
        if o["narrow"] and ch and not math.isnan(r) and r < o["narrow"][0] and o["narrow"][1] == "none":
            out = [x for x in out if x["path"] not in ("T51", "T54")]
        if o["fade"] and not out and ch and not math.isnan(r) and r >= o["fade"][0] and self.inwin(i):
            if self.fadeL[i]: out = [dict(path="T55", d=1, q=100, fb=False, rr=math.nan, fade=True)]
            elif self.fadeS[i]: out = [dict(path="T56", d=-1, q=100, fb=False, rr=math.nan, fade=True)]
        return out

    def route(self, i, cands):
        self.fadeNext = bool(cands and cands[0].get("fade"))
        return super().route(i, cands)

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        self.fadeTrade = getattr(self, "fadeNext", False) and path in ("T55", "T56"); self.fadeNext = False
        if self.fadeTrade:
            for c in self.chunks.values(): c.fixed = self.ts.mid[i]
        return r

    def tband(self, i, sig):
        if self.fadeTrade: return self.ts.mid[i]
        return super().tband(i, sig)

    def side_exits(self, i):
        if self.fadeTrade: return   # 중심선 지정가·손절·종료 청산만
        return super().side_exits(i)

    def trader_qty(self, i):
        q = super().trader_qty(i); o = self.o23
        if o["narrow"] and o["narrow"][1] == "half" and self.chop(i):
            r = self.rngAtr(i)
            if not math.isnan(r) and r < o["narrow"][0]: q = max(1, q // 2)
        return q

    def reset_trade(self):
        super().reset_trade(); self.fadeTrade = False
