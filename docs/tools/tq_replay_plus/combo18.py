# 10/23 밤 3: 로직 충돌 고치기
#   sma_wait  N: 5.4 매도 신호가 종가 > 1분 SMA20(최종 청산 조건이 이미 참)이라 막혔으면, N봉 안에 종가가 SMA20 아래로 내려오고
#             7레짐이 아직 하락(추세·눌림)이면 그때 매도 진입. None이면 막기만
#   tp1_mid   True: 진입 때 1차 밴드(±2배)를 이미 지났으면 그 거래의 1차 지정가를 ±2배와 ±3배의 가운데로 (봉마다 밴드 따라 옮김)
import math
from combo16 import Combo16

class Combo18(Combo16):
    def __init__(self, b, sma_wait=None, tp1_mid=False, **kw):
        self.o18 = dict(wait=sma_wait, mid=tp1_mid); self.smaWaitUntil = -1; self.tp1Mid = False
        super().__init__(b, **kw)

    def evaluate(self, i):
        # 막기 전 후보를 한 번만 계산(두 번 부르면 지속 진입 기회가 첫 번째에서 소모됨), 막기는 여기서
        keep = self.o16["sma"]; self.o16["sma"] = None
        try: base = super().evaluate(i)
        finally: self.o16["sma"] = keep
        c, s = self.b.c[i], self.ts.sma[i]
        out = base
        if keep:
            out = [x for x in base if not (x["path"] == "T54" and c > s) and not (keep == "both" and x["path"] == "T51" and c < s)]
            if any(x["path"] == "T54" for x in base) and not any(x["path"] == "T54" for x in out) and self.o18["wait"]:
                self.smaWaitUntil = i + self.o18["wait"]
        if not out and self.o18["wait"] and i <= self.smaWaitUntil and self.posQty == 0:
            sc = self.sc; r7 = sc.r7 if getattr(sc, "ready", False) else 0
            if c < s and r7 in (-2, -3) and self.inwin(i):
                self.smaWaitUntil = -1
                out = [dict(path="T54", d=-1, q=100, fb=False, rr=math.nan)]
        return out

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        self.tp1Mid = False
        if self.o18["mid"] and path in ("T51", "T54"):
            band = self.ts.band[2 if d > 0 else -2][i]
            if d * (band - self.b.c[i]) <= 0:
                self.tp1Mid = True; sig = "UpL1" if d > 0 else "DnS1"
                if sig in self.chunks: self.chunks[sig].fixed = self.tband(i, sig)
        return r

    def tband(self, i, sig):
        if self.tp1Mid and sig in ("UpL1", "DnS1"):
            B = self.ts.band; k = 1 if sig == "UpL1" else -1
            return (B[2 * k][i] + B[3 * k][i]) / 2
        return super().tband(i, sig)

    def trend_exits(self, i):
        # 1차 지정가를 가운데로 옮긴 거래는 봉 마감 '±2배 도달' 1차도 같이 미룸: 5.1은 +2배 돌파, 5.4는 −2배 도달을 가운데 가격 도달로 바꿔 봄
        if self.tp1Mid and self.path in ("T51", "T54") and self.trader_trade() and not self.tm["tp1"]:
            ts = self.ts; keep2 = ts.band[2 if self.dir > 0 else -2]
            mid = [(ts.band[2 * self.dir][k] + ts.band[3 * self.dir][k]) / 2 for k in (i,)]
            saved = keep2[i]; keep2[i] = mid[0]
            xs = ts.xUp[2] if self.dir > 0 else None; savedx = xs[i] if xs else None
            if xs is not None: xs[i] = self.b.c[i - 1] <= mid[0] and self.b.c[i] > mid[0]
            try: return super().trend_exits(i)
            finally:
                keep2[i] = saved
                if xs is not None: xs[i] = savedx
        return super().trend_exits(i)

    def reset_trade(self):
        super().reset_trade(); self.tp1Mid = False
