# 7개 선(중심선, ±1·±2·±3배) 위의 최근 위치로 눌림 기준선을 바꾸는 실험 (10/23 밤 6)
#   dyn = (N, 얕은 기준, 깊은 기준 또는 None, 방식)
#     위치 z = 최근 N봉 (종가 − 중심선) ÷ 1분 ATR 의 평균
#     매수: z ≥ 얕은 기준이면 +1배 선에 저가가 위에서 닿는 것을 눌림으로(중심선 대신), |z| < 깊은 기준이면 −1배 선까지 눌려야 함
#     매도는 거울상(−1배 / +1배). 반전 확인(T1: MACD 상승 전환·골든크로스, 매도는 MACD 하락·데드크로스 또는 RSI+다이버전스)과 대기 봉 수는 그대로
#     방식 "replace" = 그 구간에서는 바꾼 선만 / "both" = 중심선 신호도 인정
import math
from combo19 import Combo20

class Combo22(Combo20):
    def __init__(self, b, dyn=None, **kw):
        super().__init__(b, **kw)
        self.dyn = dyn
        if not dyn: return
        N, shallow, deep, mode = dyn
        ts = self.ts; n = b.n; A = ts.atr; M = ts.mid
        z = [math.nan] * n; s = 0.0; buf = []
        for i in range(n):
            v = (b.c[i] - M[i]) / A[i] if A[i] > 0 else 0.0
            buf.append(v); s += v
            if len(buf) > N: s -= buf[-N - 1]
            if i >= N: z[i] = s / N
        self.zpos = z
        T1B = self.tp.T1Window + self.tp.MacdConfirmBars - 1
        def wait_signal(touch, rev):
            out = [False] * n; w = -1
            for i in range(1, n):
                fire = False
                if w >= 0:
                    w += 1
                    if rev[i]: fire = True; w = -1
                    elif w >= T1B: w = -1
                if touch[i]: w = 0
                out[i] = fire
            return out
        B = ts.band; ru = [ts.mUp[i] or ts.gold[i] for i in range(n)]
        rd = [ts.mDn[i] or ts.dead[i] for i in range(n)]
        tl = lambda L: [i > 0 and b.l[i - 1] > L[i - 1] and b.l[i] <= L[i] for i in range(n)]
        th = lambda L: [i > 0 and b.h[i - 1] < L[i - 1] and b.h[i] >= L[i] for i in range(n)]
        L1 = wait_signal(tl(B[1]), ru); Lm1 = wait_signal(tl(B[-1]), ru)
        S1 = wait_signal(th(B[-1]), rd); Sp1 = wait_signal(th(B[1]), rd)
        base_l, base_s = list(ts.eUpLong), list(ts.eDnShort)
        for i in range(n):
            zi = z[i]
            if math.isnan(zi): continue
            if zi >= shallow: ts.eUpLong[i] = L1[i] or (mode == "both" and base_l[i])
            elif deep is not None and abs(zi) < deep: ts.eUpLong[i] = Lm1[i]
            if zi <= -shallow: ts.eDnShort[i] = S1[i] or (mode == "both" and base_s[i])
            elif deep is not None and abs(zi) < deep: ts.eDnShort[i] = Sp1[i]
