# 재현기 2단계: 1분봉마다 연속 점수(spec 3-3)·7레짐(3-4, 3-5)·의심·재가속 — TQ_Strategy.UpdateContScore / ObserveCont / Observe7Regime / ObserveReaccel 그대로
import math
from ind import Bars, Signals, Regime3, to3, TICK


def R6(x): return round(x, 6)


class Score:
    ScoreTrendOn = 0.2; ScoreVelMin = 0.25; ScoreExitOff = -0.1; ScoreTrendOff = 0.0; ScoreScale = 0.9
    R7EndSuspectC = 0.1; E1DelayBars = 3; SuspectFast = 2
    VBARS = 6   # 속도 = 점수 C − VBARS봉 전 C (C# ScoreVelBars)
    CMODE = "equal"; GW = (1.0, 1.0, 1.0); GSCALE = 1.0   # 실험: 점수 C 만드는 방식 (지금은 11개 같은 무게)

    def __init__(self, b, b3, reg):
        self.b, self.b3, self.reg = b, b3, reg
        # 1분봉 i → 그 시각까지 마감된 마지막 3분봉 번호 (같은 시각이면 그 3분봉, NT Playback 4,407번 확인)
        self.j = []; j = -1
        for i in range(b.n):
            while j + 1 < b3.n and b3.t[j + 1] <= b.t[i]: j += 1
            self.j.append(j)
        self.hist = [0.0] * b.n
        self.contState = 0; self.contExit = 0; self.r7 = 0; self.r7Since = -1; self.r7Age = -1; self.r7Warn = 0
        self.contSuspect = 0; self.fPrev = 0; self.dipUp = self.dipDn = False

    def level(self, x, top):
        if not self.ready: return 0
        n = min(top, int(math.floor(abs(x) / self.ScoreScale + 1e-9)))
        return n if x > 0 else -n

    def SlowNow(self): return self.level(self.S, 4)
    def FastNow(self): return self.level(self.F, 2)
    def R7Dir(self): return (1 if self.r7 > 0 else -1) if abs(self.r7) in (2, 3) else 0
    def RegimeState(self): return self.R7Dir() if self.ready else 0
    def ExitState(self): return self.contExit if self.ready else 0

    def update(self, i):
        """진입 봉 i 마감 (가드를 통과한 봉에서만 부름). ObserveRegime 그대로"""
        b, b3, rg = self.b, self.b3, self.reg; j = self.j[i]
        self.trendStartNow = 0; self.suspectEventNow = 0; self.reaccelNow = 0; self.fastCrossNow = 0
        self.ready = j >= 70 and i > 6
        if not self.ready:
            self.hist[i] = 0; return
        a = max(rg.atr[j], TICK); c = b.c[i]
        sm, sf, ss, m = rg.sm[j], rg.sf[j], rg.ss[j], rg.ml[j]
        pxMid = R6(math.tanh((c - sm) / (1.5 * a))); pxFast = R6(math.tanh((c - sf) / (0.75 * a)))
        roc5 = R6(math.tanh((c - b3.c[j - 5]) / (2.0 * a)))
        hh = max([c] + b3.h[j - 19:j + 1]); ll = min([c] + b3.l[j - 19:j + 1])
        don20 = R6(2 * (c - ll) / (hh - ll) - 1 if hh > ll else 0)
        ys = [b3.c[j - (9 - k)] for k in range(9)] + [c]
        xm = 4.5; ym = sum(ys) / 10; num = den = 0.0; path = abs(ys[0] - b3.c[j - 10])
        for k in range(10):
            num += (k - xm) * (ys[k] - ym); den += (k - xm) ** 2
            if k > 0: path += abs(ys[k] - ys[k - 1])
        lrs10 = R6(math.tanh(num / den / (0.25 * a))); er10 = R6((c - b3.c[j - 10]) / path if path > 0 else 0)
        structS = R6(math.tanh((sm - ss) / (2.0 * a))); slope60 = R6(math.tanh((ss - rg.ss[j - 5]) / (0.5 * a)))
        mac = R6(math.tanh(m / a)); macdSl = R6(math.tanh((m - rg.ml[j - 2]) / (0.3 * a)))
        rs = R6(max(-1.0, min(1.0, (rg.rsi[j] - 50) / 20)))
        self.comp = (pxMid, pxFast, roc5, don20, lrs10, er10, structS, slope60, mac, macdSl, rs)   # 항목별 값 (분석용, 판단에는 안 씀)
        if self.CMODE == "group":   # 실험: 겹치는 항목을 묶어 묶음마다 한 번씩 (가격 위치 5·빠른 모멘텀 3·느린 구조 3)
            g1 = (pxMid + don20 + lrs10 + er10 + rs) / 5.0; g2 = (pxFast + roc5 + macdSl) / 3.0; g3 = (structS + slope60 + mac) / 3.0
            w = self.GW; self.C = R6(self.GSCALE * (w[0] * g1 + w[1] * g2 + w[2] * g3) / sum(w))
        else:
            self.C = R6((pxMid + pxFast + roc5 + don20 + lrs10 + er10 + structS + slope60 + mac + macdSl + rs) / 11.0)
        self.S = R6(pxMid + structS + mac + rs); self.F = R6(pxFast + macdSl)
        self.tk = (sf, a, macdSl)
        self.hist[i] = self.C; self.V = R6(self.C - self.hist[i - self.VBARS])
        self.observe(i)
        self.reaccel()

    def observe(self, i):
        C, V = self.C, self.V; On, Vm = self.ScoreTrendOn, self.ScoreVelMin
        prev = self.contState
        upGo = C >= On or (C >= 0 and V >= Vm); dnGo = C <= -On or (C <= 0 and V <= -Vm)
        if self.contState == 1: self.contState = -1 if dnGo else 0 if C <= self.ScoreTrendOff else 1
        elif self.contState == -1: self.contState = 1 if upGo else 0 if C >= -self.ScoreTrendOff else -1
        else: self.contState = 1 if upGo else -1 if dnGo else 0
        x = self.contExit
        if x == 1: self.contExit = -1 if C <= -On else 0 if C <= self.ScoreExitOff else 1
        elif x == -1: self.contExit = 1 if C >= On else 0 if C >= -self.ScoreExitOff else -1
        else: self.contExit = 1 if C >= On else -1 if C <= -On else 0
        if self.contState != prev: self.contSuspect = 0
        f = self.FastNow()
        if f <= -self.SuspectFast and self.fPrev > -self.SuspectFast: self.fastCrossNow = -1
        elif f >= self.SuspectFast and self.fPrev < self.SuspectFast: self.fastCrossNow = 1
        stR = self.R7Dir()
        if stR != 0 and self.fastCrossNow == -stR:
            self.suspectEventNow = -stR; self.contSuspect = -stR
        elif self.contSuspect != 0 and stR * f >= 0:
            self.contSuspect = 0
        self.fPrev = f
        # 7레짐
        prev7 = self.r7; d = 1 if self.r7 > 0 else -1 if self.r7 < 0 else 0; a = abs(self.r7)
        fastUp = C >= 0 and V >= Vm; fastDn = C <= 0 and V <= -Vm
        if a == 0: self.r7 = 2 if C >= On else -2 if C <= -On else 1 if fastUp else -1 if fastDn else 0
        elif a == 1:
            if (fastDn if d > 0 else fastUp) or d * C <= -On: self.r7 = -d
            elif d * C >= On: self.r7 = 2 * d
            elif d * C < 0: self.r7 = 0
        elif a == 2:
            if d * C <= -On: self.r7 = -2 * d
            elif d * C < self.R7EndSuspectC or d * V <= -Vm: self.r7 = 3 * d
        else:
            if d * C <= -On: self.r7 = -2 * d
            elif d * C <= self.ScoreExitOff: self.r7 = 0
            elif d * C >= On and d * V >= 0: self.r7 = 2 * d
        na = abs(self.r7)
        if na == 2 and prev7 != self.r7: self.r7Since = i
        elif na not in (2, 3): self.r7Since = -1
        self.r7Age = i - self.r7Since if self.r7Since >= 0 else -1
        d7 = self.R7Dir()
        self.r7Warn = -d7 if d7 != 0 and d7 * C <= 0 and d7 * V <= -Vm else 0
        if self.r7Age == self.E1DelayBars:
            self.trendStartNow = 1 if self.r7 > 0 else -1

    def reaccel(self):
        st = self.RegimeState(); f = self.FastNow(); s = self.SlowNow()
        if self.trendStartNow != 0 or st == 0:
            self.dipUp = self.dipDn = False; return
        dip = self.dipUp if st > 0 else self.dipDn
        if st * f <= 0: dip = True
        elif dip and st * f >= 2 and st * s >= 2:
            self.reaccelNow = st; dip = False
        self.dipUp = st > 0 and dip; self.dipDn = st < 0 and dip
