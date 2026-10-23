# 10/23 밤 4: 매수 쪽 충돌, 진입가 기준 1·2차, 21:45 전 수량
#   long_t2_wait  N: 5.1 매수가 '약한 모멘텀 청산(T2 하락)'이 이미 참인 봉에서 나오면 보류, N봉 안에 T2 하락이 꺼지고 7레짐이 상승(추세·눌림)이면 매수. None = 끔
#   tp_shift      True: 진입 때 종가가 1차 밴드(±2배)를 이미 지났으면
#                   2차 밴드(±3배) 전이면  1차 = 종가와 2차 밴드의 가운데
#                   2차 밴드도 지났으면   1차 = 종가 ± 0.5 × 밴드 간격(1 ATR), 2차 = 종가 ± 1 × 밴드 간격
#                 가격은 진입 때 정하고 고정. 봉 마감 1·2차 조건(5.1 +2·+3배 돌파, 5.4 −2·−3배 도달)도 같은 가격
#   early_q       ("HH:MM", 수량) 그 시각 전 신호 봉 진입은 그 수량 이하
import math
from combo18 import Combo18

class Combo19(Combo18):
    def __init__(self, b, long_t2_wait=None, tp_shift=False, early_q=None, **kw):
        self.o19 = dict(lwait=long_t2_wait, shift=tp_shift, early=early_q); self.longWaitUntil = -1; self.tpS = {}
        super().__init__(b, **kw)

    def trader_qty(self, i):
        q = super().trader_qty(i); e = self.o19["early"]
        if e and self.b.t[i][11:16] < e[0]: q = min(q, e[1])
        return q

    def evaluate(self, i):
        out = super().evaluate(i); n = self.o19["lwait"]
        if not n: return out
        ts = self.ts
        if any(x["path"] == "T51" for x in out) and ts.t2Bear[i]:
            out = [x for x in out if x["path"] != "T51"]; self.longWaitUntil = i + n
        if not out and i <= self.longWaitUntil and self.posQty == 0 and not ts.t2Bear[i]:
            sc = self.sc; r7 = sc.r7 if getattr(sc, "ready", False) else 0
            if r7 in (2, 3) and self.inwin(i):
                self.longWaitUntil = -1
                out = [dict(path="T51", d=1, q=100, fb=False, rr=math.nan)]
        return out

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        self.tpS = {}
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        if self.o19["shift"] and path in ("T51", "T54"):
            B = self.ts.band; c = self.b.c[i]; B2 = B[2 * d][i]; B3 = B[3 * d][i]; gap = abs(B3 - B2)
            s1, s2 = ("UpL1", "UpL2") if d > 0 else ("DnS1", "DnS2")
            if d * (c - B2) >= 0:
                if d * (B3 - c) > 0: self.tpS[s1] = (c + B3) / 2
                else: self.tpS[s1] = c + d * 0.5 * gap; self.tpS[s2] = c + d * gap
            for s, v in self.tpS.items():
                if s in self.chunks: self.chunks[s].fixed = v
        return r

    def tband(self, i, sig):
        if sig in self.tpS: return self.tpS[sig]
        return super().tband(i, sig)

    def trend_exits(self, i):
        if not self.tpS or self.path not in ("T51", "T54") or not self.trader_trade(): return super().trend_exits(i)
        ts = self.ts; d = self.dir; c, c1 = self.b.c[i], self.b.c[i - 1]
        s1, s2 = ("UpL1", "UpL2") if d > 0 else ("DnS1", "DnS2")
        saved = []
        def put(arr, v): saved.append((arr, arr[i])); arr[i] = v
        if s1 in self.tpS:
            put(ts.band[2 * d], self.tpS[s1])
            if d > 0: put(ts.xUp[2], c1 <= self.tpS[s1] < c)
        if s2 in self.tpS:
            put(ts.band[3 * d], self.tpS[s2])
            if d > 0: put(ts.xUp[3], c1 <= self.tpS[s2] < c)
        try: return super().trend_exits(i)
        finally:
            for arr, v in reversed(saved): arr[i] = v

    def reset_trade(self):
        super().reset_trade(); self.tpS = {}


class Combo20(Combo19):
    """ext_skip: 진입 때 종가가 1차 밴드(±2배)를 이미 지났으면 1차 조각은 처음부터 사지 않음(1차를 한 것으로 봄, 본절은 안 옮김),
    2차 밴드(±3배)도 지났으면 2차 조각도 사지 않음(5.1은 강한 모멘텀, 5.4는 2차를 한 것으로 봄). 남은 조각만 진입"""
    def __init__(self, b, ext_skip=False, **kw):
        self.ext_skip = ext_skip; super().__init__(b, **kw)
    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        if not (self.ext_skip and path in ("T51", "T54") and self.tr is not None and self.tr.get("i0") == i): return r
        B = self.ts.band; c = self.b.c[i]
        s1, s2 = ("UpL1", "UpL2") if d > 0 else ("DnS1", "DnS2")
        skip = []
        if d * (c - B[2 * d][i]) >= 0: skip.append(s1)
        if d * (c - B[3 * d][i]) >= 0: skip.append(s2)
        if not skip: return r
        cut = 0
        for s in skip:
            if s in self.chunks:
                cut += self.chunks[s].qty; self.chunks[s].qty = 0
                if s in self.live: self.live.remove(s)
        if cut <= 0: return r
        self.pending = [(k, sg, q - cut) if k == "entry" else (k, sg, q) for k, sg, q in self.pending]
        self.baseQty -= cut; self.tr["qty"] -= cut
        self.tm["tp1"] = True
        if s2 in skip:
            if d > 0: self.tm["strong"] = True
            else: self.tm["tp2"] = True
        return r
