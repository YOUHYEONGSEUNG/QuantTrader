# 진입 조건과 청산 조건이 부딪히는 자리 고치기 실험 (10/23 밤)
#   sma_block  "short" 5.4 매도: 종가 > 1분 SMA20이면 진입 안 함(다음 봉 최종 청산 조건이 이미 참) / "both" 매수도 종가 < SMA20이면 안 함
#   tp1_fix    None / ("skip", thr) 진입 때 1차 밴드까지 거리 ≤ thr pt면 1차 조각을 지정가 없이 둠(나머지와 같이 청산)
#              / ("r", thr, k) 그때 1차 지정가를 진입가 ± k × 손절 거리로
import math
from combo15 import Combo15

class Combo16(Combo15):
    def __init__(self, b, sma_block=None, tp1_fix=None, **kw):
        self.o16 = dict(sma=sma_block, tp1=tp1_fix); self.tp1Fixed = None
        super().__init__(b, **kw)

    def evaluate(self, i):
        out = super().evaluate(i); m = self.o16["sma"]
        if not m or not out: return out
        c, s = self.b.c[i], self.ts.sma[i]
        res = []
        for x in out:
            if x["path"] == "T54" and c > s: continue
            if m == "both" and x["path"] == "T51" and c < s: continue
            res.append(x)
        return res

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        self.tp1Fixed = None
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        f = self.o16["tp1"]
        if f and path in ("T51", "T54"):
            band = self.ts.band[2 if d > 0 else -2][i]
            if d * (band - self.b.c[i]) <= f[1]:
                if f[0] == "skip": self.tp1Fixed = math.nan
                else: self.tp1Fixed = self.b.c[i] + d * f[2] * abs(self.b.c[i] - self.initialStop)
                sig = "UpL1" if d > 0 else "DnS1"
                if sig in self.chunks: self.chunks[sig].fixed = self.tp1Fixed
        return r

    def tband(self, i, sig):
        if self.tp1Fixed is not None and sig in ("UpL1", "DnS1"): return self.tp1Fixed
        return super().tband(i, sig)

    def trend_exits(self, i):
        # 1차를 지정가 없이 둔 거래는 봉 마감 1차 조건(밴드 돌파·지표)도 그 조각엔 안 씀: 5.1은 +2배 돌파 1차, 5.4는 지표 1차가 남아 있음
        return super().trend_exits(i)

    def reset_trade(self):
        super().reset_trade(); self.tp1Fixed = None
