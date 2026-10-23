# 재현기 3단계: TQ_Strategy(v6-box) OnBarUpdate와 주문 흐름을 그대로 옮김.
# 체결 모형: 봉 마감에 낸 시장가 주문은 다음 봉 시가에 체결, 손절 스탑·익절 지정가·E3 스탑 진입·현재가 청산은 봉 안 가격 경로(시가 → 가까운 극값 → 먼 극값 → 종가)에서 닿는 순간.
import math, json
from ind import Bars, Signals, Regime3, to3, TICK, rmax, rmin
from score import Score, R6

PV = 2.0   # MNQ 1pt = $2


def rt(x): return round(x / TICK) * TICK


def hhmmss(t): return int(t[11:13]) * 10000 + int(t[14:16]) * 100 + int(t[17:19])


def mod(h): return (h // 10000) * 60 + (h // 100) % 100


class P:   # 설정 (TQ_Strategy 기본값 + 고정값). 실험은 여기 값을 바꿔서
    def __init__(self, **kw):
        self.UseEntryTrendStart = True; self.UseEntryPullback = True; self.UseEntryOrb = True; self.E3StopEntry = True
        self.UseEntryOpenRange = True; self.UseEntryRangeFade = True; self.UseBreakoutEntry = True; self.UseEntrySide = True
        self.DayLossLimitR = 1.5; self.ForceMinEntries = True; self.E5RangeMinutes = 5; self.E5TargetR = 2.0
        self.StopMinTicks = 32; self.TrailAtr = 2.5; self.TrendTp1R = 0.5; self.TrendTp2R = 2.5; self.UseAddOn = True
        self.B1OppTickExit = True; self.EarlyFullExitAtTp1 = False; self.EarlyStopMult = 1.0
        self.OpenTime = 223000; self.NewsBlockMinutes = 5; self.EntryQuantity = 30; self.RiskPerTrade = 2000; self.MinEntries = 2
        self.ContestStartTime = 213000; self.EntryEndTime = 231500; self.FlattenTime = 232700
        # 고정값
        self.RangeFadeMaxEr = 0.15; self.RangeFadeMinBars = 60; self.RangeFadeBars = 30; self.RangeFadeMaxLag = 3; self.RangeFadeTargetR = 1.0
        self.RangeFadeStopBuf = 0.25; self.RangeFadeStopMinAtr = 1.0; self.RangeFadeStopMaxAtr = 2.5; self.RangeFadeMaxHold = 20; self.DayLossQtyPercent = 50
        self.E5EndTime = 225000; self.NormalEntryEndTime = 230000; self.E1DelayBars = 3; self.QualityHalfBelow = -0.05; self.SizeFloorPercent = 25
        self.ForceStartTime = 232000; self.Tp1TrailAtr = 3.0
        self.SuspectConfirmBars = 5; self.SuspectExitPercent = 50; self.SuspectStrongSlow = 1; self.SuspectStrongExitPercent = 100
        self.PullbackMinSlow = 2; self.OrbEndTime = 230000; self.OrbMinSlow = 1; self.OrbMinFast = 1; self.OrbMaxEntries = 2
        self.E5BreakevenR = 1.0; self.E5StopMinAtr = 1.0; self.E5StopMaxAtr = 4.0; self.BreakoutBars = 15; self.BreakoutMinFast = 1
        self.BreakoutBoxBars = 30; self.BreakoutMaxEr = 0.25; self.EntryMinRR = 1.0; self.SideBlockScore = 3
        self.TrendStopMinAtr = 1.5; self.TrendStopMaxAtr = 3.0; self.SideStopMinAtr = 1.0; self.SideStopMaxAtr = 2.0; self.BreakevenBufferTicks = 8
        self.BreakevenMinR = 0.5; self.BreakevenFallbackAtr = 1.5; self.BreakevenAtR = 0.8; self.TrendTp1Band = 2; self.TrendTp2Band = 3
        self.AddOnMinVel = 0.3; self.AddOnPercent = 50; self.EarlyWindowMinutes = 5; self.ExtendedEntryAtr = 2.0; self.ExtendedQtyPercent = 50
        self.AddOnEndTime = 231000; self.SideTp1Atr = 2; self.SideTp1Percent = 33; self.SideRestBand = 3; self.OpenBlockBefore = 5; self.OpenBlockAfter = 3
        self.PreOpenQtyPercent = 50; self.PreOpenTrendMinSlow = 2; self.MinEntryFallbackTime = 230000; self.MinEntryQtyPercent = 30
        self.TrendTp1Percent = 33; self.TrendTp2Percent = 33
        self.QF = (0.7056, 1.2511, 0.0143, 0.0738, -0.0861); self.QExt = (0.8550, 1.8225, 0.0915, 0.0813, -0.1714)
        self.QR10 = (3.0649, 3.9154, -0.1736, 0.0797, 0.0932); self.QPre = (1.1770, 2.6459, -0.0443, 0.1236, -0.0800)
        self.QRoom = (-0.0269, 0.8333, -0.2536, 0.1931, 0.0610)
        for k, v in kw.items(): setattr(self, k, v)


def qbin(t, x): return t[2] if x < t[0] else t[3] if x < t[1] else t[4]


class Chunk:
    def __init__(self, sig, qty, stage, fixed):
        self.sig, self.qty, self.stage, self.fixed = sig, qty, stage, fixed
        self.left = 0; self.stop = math.nan; self.filled_stage = False; self.exit_sent = False


class Strat:
    def __init__(self, b, p=None, hooks=None):
        self.p = p or P(); self.b = b; self.hooks = hooks or {}
        self.b3 = to3(b); self.rg = Regime3(self.b3); self.sg = Signals(b); self.sc = Score(b, self.b3, self.rg)
        self.rHi = rmax(b.h, 15); self.rLo = rmin(b.l, 15); self.fHi = rmax(b.h, 30); self.fLo = rmin(b.l, 30)
        self.trades = []; self.events = []
        # 상태
        self.pos = None; self.dir = 0; self.path = ""; self.entryCount = 0; self.wasIn = False; self.signalSeq = 0
        self.chunks = {}; self.order = []; self.live = []
        self.posQty = 0      # 실제 보유 계약 (부호 없음)
        self.pending = []    # 다음 봉 시가에 체결될 시장가 주문 [(kind, sig, qty)] kind: entry / exit / flat
        self.e3 = None       # 걸어 둔 E3 스탑 진입 {dir, px, prefix, chunks}
        self.e1AfterExitDir = 0; self.e1AfterExitBar = -1; self.tickOppExitBar = -10
        self.blockSideLong = self.blockSideShort = False; self.prevWaitSideLong = self.prevWaitSideShort = -1
        self.sessPnl = 0.0; self.reset_trade(); self.session_reset(None)

    # ── 상태 ──
    def reset_trade(self):
        self.pos = None; self.dir = 0; self.path = ""; self.fallback = False; self.sawOwn = False
        self.stopSwing = math.nan; self.stopAtr = math.nan; self.stopPrice = math.nan; self.initialStop = math.nan
        self.fillSum = 0.0; self.fillQty = 0; self.gap = False; self.beDone = False; self.e5Trail = False; self.stopNote = ""
        self.deferred = math.nan; self.lastTarget = math.nan; self.lastLabel = ""; self.trailExt = math.nan
        self.tp1Done = self.tp2Done = False; self.tp1Fill = math.nan; self.addSig = ""; self.warnDone = False; self.baseQty = 0
        self.early = False; self.addSum = 0.0; self.addQty = 0; self.susWait = 0
        self.chunks = {}; self.order = []; self.live = []
        self.trOpen = False if not hasattr(self, "trOpen") else self.trOpen

    def session_reset(self, i):
        self.entryCount = 0; self.blockSideLong = self.blockSideShort = False; self.orbL = self.orbS = 0; self.orbAL = self.orbAS = True
        self.boxH = self.boxL = math.nan; self.orH = self.orL = math.nan; self.orLU = self.orSU = False
        self.sessStart = math.nan; self.sessPath = 0.0; self.sessBars = 0; self.sessPnl = 0.0; self.sessPrev = math.nan

    # ── 시간 ──
    def T(self, i): return hhmmss(self.b.t[i])
    def rel(self, h): return (mod(h) - mod(self.p.ContestStartTime) + 1440) % 1440
    def inwin(self, i):
        t, s, e = self.T(i), self.p.ContestStartTime, self.p.EntryEndTime
        return s <= t <= e if s <= e else (t >= s or t <= e)
    def zone(self, i):
        if not self.inwin(i): return "Out"
        r = self.rel(self.T(i)); o = self.rel(self.p.OpenTime)
        if r < self.p.NewsBlockMinutes: return "News"
        if r < o - self.p.OpenBlockBefore: return "Pre"
        if r < o + self.p.OpenBlockAfter: return "OpenBlock"
        return "Post"
    def flat_time(self, i):
        t, f, s = self.T(i), self.p.FlattenTime, self.p.ContestStartTime
        return (f <= t < s) if f < s else (t >= f or t < s)

    def is_trend(self): return self.pos in ("TL", "TS")
    def avg(self): return self.fillSum / self.fillQty if self.fillQty > 0 else math.nan

    def log(self, i, s): self.events.append((self.b.t[i], s))

    # ── 손절 거리 ──
    def stop_dist(self, d, trend, px, swing, a):
        p = self.p
        dist = math.nan if math.isnan(swing) else d * (px - swing) + TICK
        if not math.isnan(a) and a > 0:
            lo = (p.TrendStopMinAtr if trend else p.SideStopMinAtr) * a; hi = max(lo, (p.TrendStopMaxAtr if trend else p.SideStopMaxAtr) * a)
            dist = lo if math.isnan(dist) else max(lo, min(hi, dist))
        if math.isnan(dist): dist = p.StopMinTicks * TICK
        return max(dist, p.StopMinTicks * TICK)

    def calc_stop(self, px):
        p = self.p
        if self.path == "E5":
            dist = self.dir * (px - self.stopSwing) + TICK
            if self.stopAtr > 0: dist = max(p.E5StopMinAtr * self.stopAtr, min(max(p.E5StopMinAtr, p.E5StopMaxAtr) * self.stopAtr, dist))
            dist = max(dist, p.StopMinTicks * TICK)
        elif self.path == "E6":
            a = self.stopAtr; dist = self.dir * (px - self.stopSwing) + p.RangeFadeStopBuf * a
            if a > 0: dist = max(p.RangeFadeStopMinAtr * a, min(p.RangeFadeStopMaxAtr * a, dist))
            dist = max(dist, p.StopMinTicks * TICK)
        else:
            dist = self.stop_dist(self.dir, self.is_trend(), px, self.stopSwing, self.stopAtr)
        if self.early and self.p.EarlyStopMult > 1: dist *= self.p.EarlyStopMult
        if "stop_dist" in self.hooks: dist = self.hooks["stop_dist"](self, px, dist)
        return rt(px - self.dir * dist)

    # ── 점수·후보 ──
    def box_er(self, i):
        n = self.p.BreakoutBoxBars; c = self.b.c
        if i <= n: return 1.0
        net = abs(c[i - 1] - c[i - n]); path = sum(abs(c[i - k] - c[i - k - 1]) for k in range(1, n))
        return net / path if path > 0 else 1.0

    def sess_er(self, i):
        return abs(self.b.c[i] - self.sessStart) / self.sessPath if self.sessPath > 0 and not math.isnan(self.sessStart) else 1.0

    def range_day(self, i): return self.sessBars >= self.p.RangeFadeMinBars and self.sess_er(i) <= self.p.RangeFadeMaxEr

    def fade_sig(self, i):
        b = self.b
        for d in (1, -1):
            for lag in range(1, self.p.RangeFadeMaxLag + 1):
                if i < lag + self.p.RangeFadeBars + 1: return 0
                edge = self.fLo[i - lag - 1] if d > 0 else self.fHi[i - lag - 1]
                broke = b.l[i - lag] < edge if d > 0 else b.h[i - lag] > edge
                if broke and d * (b.c[i] - edge) > 0 and d * (b.c[i - 1] - edge) <= 0: return d
        return 0

    def band(self, i, d, n):
        sg = self.sg
        if n <= 0: return sg.mid[i]
        return sg.up[n][i] if d > 0 else sg.dn[n][i]

    def quality(self, i, d, px):
        sg, sc, b, p = self.sg, self.sc, self.b, self.p
        a = max(sg.atr[i], TICK); F = d * sc.F; ext = d * (px - sg.mid[i]) / a
        r10 = (max(b.h[i - 9:i + 1]) - min(b.l[i - 9:i + 1])) / a
        pre = d * (px - b.c[i - 15]) / a if i > 15 else 0
        hh = max(b.h[i - 30:i]); ll = min(b.l[i - 30:i])
        swing = sg.swL[i] if d > 0 else sg.swH[i]
        sd = self.stop_dist(d, True, px, swing, sg.atr[i]); room = ((hh - px) if d > 0 else (px - ll)) / sd if sd > 0 else 0
        r7 = sc.r7; a7 = abs(r7); d7 = 1 if r7 > 0 else -1 if r7 < 0 else 0
        if sc.r7Warn == -d: rv = -0.0325
        elif a7 == 3 and d7 == d: rv = 0.2566
        elif a7 == 2 and d7 == d: rv = -0.0646
        elif a7 in (2, 3) and d7 == -d: rv = 0.2174
        elif a7 == 1 and d7 == -d: rv = -0.1086
        elif a7 == 0: rv = -0.1444
        else: rv = 0
        return qbin(p.QF, F) + qbin(p.QExt, ext) + qbin(p.QR10, r10) + qbin(p.QPre, pre) + qbin(p.QRoom, room) + rv

    def evaluate(self, i):
        p, sc, sg, b = self.p, self.sc, self.sg, self.b
        out = []; st = sc.RegimeState(); S = sc.SlowNow(); F = sc.FastNow(); z = self.zone(i)
        fb = z == "Post" and self.entryCount < p.MinEntries and self.rel(self.T(i)) >= self.rel(p.MinEntryFallbackTime)
        for d in (1, -1):
            tb = "x" if z in ("Out", "News", "OpenBlock") else None
            if tb is None and sc.r7Warn == -d: tb = "warn"
            if tb is None and not fb and self.rel(self.T(i)) >= self.rel(p.NormalEntryEndTime): tb = "late"
            sb = "sus" if st == d and sc.contSuspect == -d else None
            if sb is None and d * F < 0: sb = "fast"
            if sb is None and sc.r7Warn == -d: sb = "warn"
            e1k = ("start" if sc.trendStartNow == d else "exit" if self.e1AfterExitDir == d and i - self.e1AfterExitBar <= 2
                   else "reacc" if sc.reaccelNow == d else None)
            q = 30 if fb else (p.PreOpenQtyPercent if z == "Pre" else 100)
            # E1
            ck = e1k == "start"; e1d = sc.R7Dir() if ck else st; armed = e1d == d and e1k is not None
            blk = None; rr = math.nan
            if not p.UseEntryTrendStart: blk = "off"
            elif e1d != d: blk = None
            elif tb: blk = tb
            elif sb: blk = sb
            elif not ck and z == "Pre" and not fb and d * S < p.PreOpenTrendMinSlow: blk = "preS"
            if blk is None and armed and not ck:
                tp1 = self.band(i, d, p.TrendTp1Band); tp2 = self.band(i, d, p.TrendTp2Band)
                dist = self.stop_dist(d, True, b.c[i], sg.swL[i] if d > 0 else sg.swH[i], sg.atr[i])
                rr = d * (tp2 - b.c[i]) / dist if dist > 0 else math.nan
                if d * (b.c[i] - tp1) >= 0: blk = "tp1"
                elif p.EntryMinRR > 0 and not (rr >= p.EntryMinRR): blk = "rr"
            if p.UseEntryTrendStart and e1d == d and armed and blk is None:
                out.append(dict(path="E1", d=d, q=q, fb=fb, rr=rr, note=e1k))
            # E2
            if p.UseEntryPullback and st == d:
                sig = sg.eUpLong[i] if d > 0 else sg.eDnShort[i]
                blk = tb or sb
                if blk is None and z == "Pre" and not fb and d * S < p.PreOpenTrendMinSlow: blk = "preS"
                if blk is None and p.PullbackMinSlow > 0 and d * S < p.PullbackMinSlow: blk = "S"
                rr = math.nan
                if blk is None and sig:
                    tp2 = self.band(i, d, p.TrendTp2Band); dist = self.stop_dist(d, True, b.c[i], sg.swL[i] if d > 0 else sg.swH[i], sg.atr[i])
                    rr = d * (tp2 - b.c[i]) / dist if dist > 0 else math.nan
                    if p.EntryMinRR > 0 and not (rr >= p.EntryMinRR): blk = "rr"
                if blk is None and sig: out.append(dict(path="E2", d=d, q=q, fb=fb, rr=rr))
            # E3
            if p.UseEntryOrb:
                cnt = self.orbL if d > 0 else self.orbS; arm = self.orbAL if d > 0 else self.orbAS
                edge = self.boxH if d > 0 else self.boxL
                before = self.rel(self.T(i)) < self.rel(p.OrbEndTime)
                if cnt < p.OrbMaxEntries and arm and z == "Post" and before and self.box_ready(i) and sb is None:
                    q3 = 30 if fb else 100
                    if d * (b.c[i] - edge) > 0 and d * S >= p.OrbMinSlow and d * F >= p.OrbMinFast:
                        out.append(dict(path="E3", d=d, q=q3, fb=fb, rr=math.nan))
                    elif d * (b.c[i] - edge) <= 0 and p.E3StopEntry and d * S >= p.OrbMinSlow and d * F >= p.OrbMinFast:
                        out.append(dict(path="E3", d=d, q=q3, fb=fb, rr=math.nan, stop=rt(edge + d * TICK)))
            # E5
            if p.UseEntryOpenRange and self.or_ready(i):
                edge = self.orH if d > 0 else self.orL; used = self.orLU if d > 0 else self.orSU
                if (fb or self.rel(self.T(i)) < self.rel(p.E5EndTime)) and not used and tb is None and d * (b.c[i] - edge) > 0:
                    out.append(dict(path="E5", d=d, q=30 if fb else 100, fb=fb, rr=math.nan))
            # B1
            if p.UseBreakoutEntry and tb is None:
                er = self.box_er(i)
                if fb or p.BreakoutMaxEr >= 1 or er <= p.BreakoutMaxEr:
                    edge = self.rHi[i - 1] if d > 0 else self.rLo[i - 1]
                    if d * (b.c[i] - edge) > 0 and d * F >= p.BreakoutMinFast: out.append(dict(path="B1", d=d, q=q, fb=fb, rr=math.nan))
            # E4
            if p.UseEntrySide and st == 0 and tb is None:
                ok = not (not fb and p.SideBlockScore > 0 and d * (S + F) <= -p.SideBlockScore)
                ok = ok and not (self.blockSideLong if d > 0 else self.blockSideShort)
                if ok and (sg.eSideLong[i] if d > 0 else sg.eSideShort[i]): out.append(dict(path="E4", d=d, q=q, fb=fb, rr=math.nan))
            # E6
            if p.UseEntryRangeFade and self.sessBars >= p.RangeFadeMinBars and self.range_day(i) and tb is None and z == "Post" and self.fade_sig(i) == d:
                out.append(dict(path="E6", d=d, q=100, fb=False, rr=math.nan))
        if "evaluate" in self.hooks: out = self.hooks["evaluate"](self, i, out)
        return out

    def box_ready(self, i): return not math.isnan(self.boxH) and self.inwin(i) and self.rel(self.T(i)) > self.rel(self.p.OpenTime)
    def or_ready(self, i): return not math.isnan(self.orH) and self.inwin(i) and self.rel(self.T(i)) > self.rel(self.p.OpenTime) + self.p.E5RangeMinutes - 1

    RANK = {"E5": -1, "E1": 0, "E3": 1, "B1": 2, "E2": 3, "E4": 4, "E6": 5}

    def route(self, i, cands):
        ready = [c for c in cands if "stop" not in c]
        best = None
        for c in ready:
            if best is None or self.RANK[c["path"]] < self.RANK[best["path"]]: best = c
        if best is None:
            placed = False
            st = [c for c in cands if "stop" in c and (c.get("path", "E3") != "E3" or (self.p.E3StopEntry and self.p.UseEntryOrb))]
            if st:
                if st:
                    pick = min(st, key=lambda c: abs(c["stop"] - self.b.c[i]))
                    if not (self.e3 and self.e3["d"] != pick["d"]):
                        self.enter(i, pick["d"], pick.get("path", "E3"), pick["q"], pick["fb"], math.nan, stop_px=pick["stop"]); placed = True
                    # 반대 방향 스탑이 살아 있으면 이번 봉에는 안 걸고(C# PlaceE3Stop) 걸려 있던 것은 봉 끝에 취소됨
            if not placed: self.e3 = None
            else: self.e3_renewed = True
            return
        self.e3 = None
        pc = best["path"]
        if pc == "E1": pc = {"exit": "E1X", "reacc": "E1R"}.get(best.get("note"), "E1")
        pos = ("SL" if best["d"] > 0 else "SS") if best["path"] == "E4" else ("TL" if best["d"] > 0 else "TS")
        self.enter(i, best["d"], pc, best["q"], best["fb"], best["rr"], pos=pos)
        if best["path"] == "E3":
            if best["d"] > 0: self.orbL += 1; self.orbAL = False
            else: self.orbS += 1; self.orbAS = False
        if best["path"] == "E1": self.e1AfterExitDir = 0
        if best["path"] == "E5":
            if best["d"] > 0: self.orLU = True
            else: self.orSU = True

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        p, b, sg, sc = self.p, self.b, self.sg, self.sc
        stopEntry = stop_px is not None; ref = stop_px if stopEntry else b.c[i]
        renew = stopEntry and self.e3 is not None and self.e3["d"] == d
        if not renew: self.reset_trade()
        else: self.reset_trade()
        self.pos = pos or ("TL" if d > 0 else "TS"); self.dir = d; self.path = path; self.fallback = fb
        base = qpct if fb else 100; qty = max(1, int(math.floor(p.EntryQuantity * base / 100.0)))
        self.stopAtr = sg.atr[i]; since = self.rel(self.T(i)) - self.rel(p.OpenTime)
        self.early = self.is_trend() and path not in ("E5", "E6") and 0 < since <= p.EarlyWindowMinutes
        self.stopSwing = sg.swL[i] if d > 0 else sg.swH[i]
        if path == "E5": self.stopSwing = (self.orH + self.orL) / 2
        if path == "E6": self.stopSwing = min(b.l[i - 3:i + 1]) if d > 0 else max(b.h[i - 3:i + 1])
        self.initialStop = self.calc_stop(ref); self.stopPrice = self.initialStop
        dist = abs(ref - self.initialStop)
        if dist > 0: qty = max(1, min(qty, int(math.floor(p.RiskPerTrade * base / 100.0 / (dist * PV)))))
        mult = 1.0
        if not fb and qpct < 100: mult *= qpct / 100.0
        if p.DayLossLimitR > 0 and self.sessPnl <= -p.DayLossLimitR * p.RiskPerTrade: mult *= p.DayLossQtyPercent / 100.0
        tp = self.is_trend() and path not in ("E5", "E6", "FORCE")
        if tp and p.ExtendedEntryAtr > 0 and sg.atr[i] > 0 and d * (ref - sg.mid[i]) / sg.atr[i] > p.ExtendedEntryAtr: mult *= p.ExtendedQtyPercent / 100.0
        self.q_score = math.nan
        if tp and sc.ready:
            q = self.quality(i, d, ref); self.q_score = q
            if q < p.QualityHalfBelow: mult *= 0.5
        if "qty_mult" in self.hooks: mult = self.hooks["qty_mult"](self, i, mult)
        if "skip" in self.hooks and self.hooks["skip"](self, i, mult, path):
            self.reset_trade(); self.skipped = getattr(self, "skipped", 0) + 1; return
        if mult < 1: qty = max(1, int(math.floor(qty * max(p.SizeFloorPercent / 100.0, mult))))
        if path == "FORCE": qty = 1
        self.baseQty = qty
        self.tr = dict(t0=b.t[i], i0=i, path=path, d=d, qty=qty, sig_close=b.c[i], stop0=math.nan, pnl=0.0, mfe=0.0, mae=0.0, bars=0, exits=[], reason="", stop_entry=stop_px)
        self.trOpen = True
        if not renew: self.signalSeq += 1
        prefix = f"{self.pos}{self.signalSeq}."
        risk = abs(ref - self.initialStop)
        if path == "E6": self.add_chunk(prefix + "1", qty, 1, ref + d * p.RangeFadeTargetR * risk)
        elif path == "E5": self.add_chunk(prefix + "1", qty, 1, b.c[i] + d * p.E5TargetR * abs(b.c[i] - self.initialStop))
        elif self.is_trend():
            q1 = qty * p.TrendTp1Percent // 100; q2 = qty * p.TrendTp2Percent // 100; all1 = self.early and p.EarlyFullExitAtTp1
            self.add_chunk(prefix + "1", q1, 1, ref + d * self.tp1_dist(i, ref, risk))
            self.add_chunk(prefix + "2", q2, 2, ref + d * (p.TrendTp1R if all1 else p.TrendTp2R) * risk)
            self.add_chunk(prefix + "3", qty - q1 - q2, 3, ref + d * p.TrendTp1R * risk if all1 else math.nan)
        else:
            q1 = qty * p.SideTp1Percent // 100
            self.add_chunk(prefix + "1", q1, 1, b.c[i] + d * p.SideTp1Atr * self.stopAtr)
            self.add_chunk(prefix + "2", qty - q1, 3, math.nan)
        safe = min(self.stopPrice, ref - TICK) if d > 0 else max(self.stopPrice, ref + TICK)
        for c in self.chunks.values(): c.stop = safe
        if stopEntry:
            self.e3 = dict(d=d, px=stop_px, i=i)
        else:
            self.pending.append(("entry", None, qty))
        if not renew: self.log(i, f"진입 {path} {'매수' if d > 0 else '매도'} 수량={qty} 손절={self.stopPrice:.2f}" + (f" 스탑={stop_px}" if stopEntry else ""))

    def tp1_dist(self, i, px, risk):
        d0 = self.p.TrendTp1R * risk
        if "tp1_dist" in self.hooks: return self.hooks["tp1_dist"](self, i, px, risk, d0)
        return d0

    def add_chunk(self, sig, qty, stage, fixed):
        if qty <= 0: return
        self.chunks[sig] = Chunk(sig, qty, stage, fixed); self.order.append(sig); self.live.append(sig)

    # ── 체결 처리 ──
    def on_entry_fill(self, i, price, qty, tick_px):
        for sig in self.order:
            c = self.chunks[sig]
            if c.left == 0 and sig != self.addSig: c.left = c.qty
        if self.posQty == 0: self.entryCount += 1
        self.posQty += qty; self.fillSum += price * qty; self.fillQty += qty
        self.tr["avg"] = self.avg()
        if self.dir * (price - self.initialStop) <= 0 or self.gap:
            self.gap = True; self.flatten(i, "GAP"); return
        if self.beDone: return
        a = self.avg(); s = self.calc_stop(a)
        if (tick_px <= s) if self.dir > 0 else (tick_px >= s):
            self.gap = True; self.flatten(i, "GAP"); return
        self.stopPrice = s; self.tr["stop0"] = s
        for c in self.chunks.values(): c.stop = s
        p = self.p
        if self.is_trend() and self.path not in ("E5", "E6"):
            for c in self.chunks.values():
                all1 = self.early and p.EarlyFullExitAtTp1
                if c.stage in (1, 2) or (all1 and c.stage == 3):
                    rr = p.TrendTp1R if (all1 or c.stage == 1) else p.TrendTp2R
                    c.fixed = a + self.dir * (self.tp1_dist(i, a, abs(a - s)) if c.stage == 1 else rr * abs(a - s))
        if self.path in ("E5", "E6"):
            for c in self.chunks.values(): c.fixed = a + self.dir * (p.E5TargetR if self.path == "E5" else p.RangeFadeTargetR) * abs(a - s)
        if not self.is_trend():
            for c in self.chunks.values():
                if c.stage == 1: c.fixed = a + self.dir * p.SideTp1Atr * self.stopAtr
        if self.tp1Done and not math.isnan(self.tp1Fill): self.after_tp1(i, self.tp1Fill, tick_px)

    def exit_fill(self, i, sig, price, qty, kind):
        c = self.chunks.get(sig)
        if c is None or qty <= 0: return
        avg = self.addSum / self.addQty if sig == self.addSig and self.addQty > 0 else self.avg()
        self.tr["pnl"] += self.dir * (price - avg) * qty
        self.tr["exits"].append((self.b.t[i], kind, sig, price, qty))
        c.left -= qty; self.posQty -= qty
        if c.left <= 0 and sig in self.live: self.live.remove(sig)
        if self.posQty <= 0: self.trade_end(i)

    def trade_end(self, i):
        tr = self.tr; tr["t1"] = self.b.t[i]; tr["usd"] = tr["pnl"] * PV
        risk = abs(tr.get("avg", tr["sig_close"]) - tr["stop0"]) if not math.isnan(tr["stop0"]) else math.nan
        tr["R"] = tr["pnl"] / (risk * self.baseQty) if risk and risk > 0 and self.baseQty else math.nan
        tr["reason"] = self.exitReason
        self.trades.append(tr); self.sessPnl += tr["usd"]; self.trOpen = False; self.posQty = 0

    def on_stop_fill(self, i):
        self.exitReason = "STOP" if not self.beDone else "TRAIL" if self.stopNote == "트레일링" else "BE_STOP"
        if self.fillQty <= 0 or self.beDone or self.is_trend(): return
        if self.dir * (self.stopPrice - self.avg()) < 0:
            if self.dir > 0: self.blockSideLong = True
            else: self.blockSideShort = True

    def on_target_fill(self, i, c, price, cur_px):
        if c.filled_stage: return
        c.filled_stage = True
        if c.stage == 1:
            self.tp1Done = True; self.exitReason = "TP1"; self.tp1Fill = price; self.after_tp1(i, price, cur_px)
        elif c.stage == 2:
            self.tp2Done = True; self.exitReason = "TP2"

    def after_tp1(self, i, tp1, cur_px):
        if self.fillQty <= 0: return
        a = self.avg(); risk = abs(a - self.tr["stop0"]) if not math.isnan(self.tr["stop0"]) else math.nan
        gain = self.dir * (tp1 - a); p = self.p
        if p.BreakevenMinR <= 0 or math.isnan(risk) or risk <= 0 or gain >= p.BreakevenMinR * risk - TICK:
            self.move_stop(i, a, "평단", cur_px); return
        self.move_stop(i, tp1 - self.dir * p.BreakevenFallbackAtr * self.sg.atr[i], "1차 뒤 ATR", cur_px)

    def move_stop(self, i, target, label, cur_px):
        if not self.live or math.isnan(target) or target <= 0: return
        target = rt(target)
        if not (math.isnan(self.stopPrice) or self.dir * (target - self.stopPrice) > 0): return
        self.lastTarget = target; self.lastLabel = label
        buf = max(1, self.p.BreakevenBufferTicks) * TICK
        safe = (cur_px - target >= buf) if self.dir > 0 else (target - cur_px >= buf)
        if not safe: self.deferred = target; return
        self.apply_stop(target, label)

    def apply_stop(self, target, label):
        self.stopPrice = target; self.deferred = math.nan
        be = rt(self.avg()) if self.fillQty > 0 else math.nan
        self.beDone = self.fillQty > 0 and self.dir * (target - be) >= 0
        self.stopNote = label
        for sig in self.live: self.chunks[sig].stop = target

    # ── 주문 ──
    def exit_chunk(self, i, sig, code):
        if sig not in self.live: return
        c = self.chunks[sig]; self.live.remove(sig)
        if c.left <= 0: return
        self.exitReason = code; c.exit_sent = True; self.pending.append(("exit", sig, c.left))

    def flatten(self, i, code):
        self.exitReason = code; sent = 0
        for sig in list(self.live):
            c = self.chunks[sig]
            if c.left <= 0: continue
            c.exit_sent = True; self.pending.append(("exit", sig, c.left)); sent += c.left
        self.live = []; self.pos = None
        if sent == 0 and self.posQty > 0: self.pending.append(("flat", None, self.posQty))

    def exit_far(self, i, want, code):
        order = [s for st in (3, 2, 1) for s in self.order if s in self.live and self.chunks[s].stage == st]
        taken = 0; pick = []
        for s in order:
            q = self.chunks[s].left
            if q <= 0: continue
            if pick and abs(taken + q - want) >= abs(taken - want): break
            pick.append(s); taken += q
        for s in pick: self.exit_chunk(i, s, code)

    # ── 봉 하나 ──
    def bar(self, i):
        b = self.b
        # (1) 봉 시가: 지난 봉 마감에 낸 시장가 주문 체결
        pend, self.pending = self.pending, []
        for kind, sig, q in pend:
            if kind == "entry":
                if self.pos is None: continue
                self.on_entry_fill(i, b.o[i], q, b.o[i])
            elif kind == "add":
                self.addSum += b.o[i] * q; self.addQty += q; self.posQty += q; self.chunks[sig].left += q
            elif kind == "exit":
                c = self.chunks.get(sig)
                if c and c.left > 0: self.exit_fill(i, sig, b.o[i], min(q, c.left), "x")
            elif kind == "flat":
                if self.posQty > 0:
                    for s in self.order:
                        c = self.chunks[s]
                        if c.left > 0: self.exit_fill(i, s, b.o[i], c.left, "flat")
        # (2) 봉 안 가격 경로
        o, h, l, c = b.o[i], b.h[i], b.l[i], b.c[i]
        pts = [o, h, l, c] if (h - o) <= (o - l) else [o, l, h, c]
        cur = o
        for nxt in pts[1:]:
            self.walk(i, cur, nxt); cur = nxt
        if self.e3 is not None and getattr(self, "e3_i", None) != i: pass
        # (3) 봉 마감 판단
        self.on_bar_close(i)

    def walk(self, i, a, z):
        """가격이 a → z로 움직이는 동안 닿는 주문을 가까운 순서대로 체결"""
        guard = 0
        while guard < 20:
            guard += 1
            up = z > a; events = []
            # E3 스탑 진입
            if self.e3 is not None and self.posQty == 0 and self.pos is not None:
                px = self.e3["px"]; kind = self.e3.get("kind")
                if kind is None:   # v6 E3 (그대로)
                    if (up and self.e3["d"] > 0 and a < px <= z) or (not up and self.e3["d"] < 0 and z <= px < a) or (a == px):
                        events.append((abs(px - a), "e3", px))
                else:              # 트레이더 진입 실험: "stop" 돌파 진입 / "limit" 되돌림 지정가. 이미 넘어 있으면(시가 갭) 지금 가격에 체결
                    d3 = self.e3["d"] if kind == "stop" else -self.e3["d"]   # limit은 가격이 반대로 와서 닿음
                    if (d3 > 0 and a >= px) or (d3 < 0 and a <= px): events.append((0, "e3", a))
                    elif (up and d3 > 0 and a < px <= z) or (not up and d3 < 0 and z <= px < a): events.append((abs(px - a), "e3", px))
            if self.posQty > 0:
                d = self.dir
                live = [self.chunks[s] for s in self.order if self.chunks[s].left > 0]
                # 손절 (조각마다 같은 값)
                st = self.stopPrice
                if not math.isnan(st):
                    beyond = (d > 0 and a <= st) or (d < 0 and a >= st)
                    if beyond: events.append((0, "stop", a))
                    elif (d > 0 and not up and z <= st) or (d < 0 and up and z >= st): events.append((abs(st - a), "stop", st))
                for ch in live:
                    if math.isnan(ch.fixed) or ch.sig not in self.live: continue
                    tp = rt(ch.fixed)
                    beyond = (d > 0 and a >= tp) or (d < 0 and a <= tp)
                    if beyond: events.append((0, "tp", a, ch))
                    elif (d > 0 and up and z >= tp) or (d < 0 and not up and z <= tp): events.append((abs(tp - a), "tp", tp, ch))
                # 현재가 안전장치 (손절가를 8틱 넘게 지남)
                if not math.isnan(st) and self.pos is not None:
                    lim = st - d * self.p.BreakevenBufferTicks * TICK
                    if (d > 0 and not up and z <= lim < a) or (d < 0 and up and a < lim <= z): events.append((abs(lim - a) + 1e-9, "safety", lim))
                # B1 현재가 반대 돌파
                if self.p.B1OppTickExit and self.path == "B1" and self.pos is not None and hasattr(self, "tkEdge"):
                    edge = self.tkEdge[0] if d > 0 else self.tkEdge[1]
                    lvl = edge - d * TICK
                    if (d > 0 and not up and z <= lvl < a + 1e-9) or (d < 0 and up and a - 1e-9 < lvl <= z):
                        events.append((abs(lvl - a) + 2e-9, "b1opp", lvl))
            if not events: return
            events.sort(key=lambda e: e[0]); e = events[0]; px = e[2]
            if e[1] == "e3":
                self.e3 = None; q = self.baseQty
                if self.path != "E3": pass
                elif self.dir > 0: self.orbL += 1; self.orbAL = False
                else: self.orbS += 1; self.orbAS = False
                self.on_entry_fill(i, px, q, px)
            elif e[1] == "stop":
                self.on_stop_fill(i)
                for s in list(self.order):
                    ch = self.chunks[s]
                    if ch.left > 0 and not ch.exit_sent: self.exit_fill(i, s, px, ch.left, "stop")
                if self.posQty > 0:   # 직접 청산 주문을 낸 조각이 남은 경우
                    for s in list(self.order):
                        ch = self.chunks[s]
                        if ch.left > 0: self.exit_fill(i, s, px, ch.left, "stop")
                self.pos = None if self.posQty == 0 else self.pos
            elif e[1] == "tp":
                ch = e[3]; q = ch.left
                self.on_target_fill(i, ch, px, px)
                self.exit_fill(i, ch.sig, px, q, "tp")
            elif e[1] == "safety":
                self.flatten(i, "BE_STOP" if self.beDone else "STOP")
                for kind, sig, q in self.pending:
                    pass
                pend, self.pending = self.pending, []
                for kind, sig, q in pend:
                    if kind == "exit": self.exit_fill(i, sig, px, q, "safety")
                    elif kind == "flat":
                        for s in self.order:
                            ch = self.chunks[s]
                            if ch.left > 0: self.exit_fill(i, s, px, ch.left, "safety")
            elif e[1] == "b1opp":
                sf, at, msl = self.sc.tk
                fNow = R6(math.tanh((px - sf) / (0.75 * at)) + msl)
                if -self.dir * fNow >= self.p.BreakoutMinFast * 0.9:
                    self.tickOppExitBar = i - 1
                    self.flatten(i, "OPP_BREAKOUT")
                    pend, self.pending = self.pending, []
                    for kind, sig, q in pend:
                        if kind == "exit": self.exit_fill(i, sig, px, q, "b1tick")
                else:
                    # F 조건이 안 맞으면 이 가격에서는 청산 안 함: 더 가 보며 다시 확인 (1틱씩)
                    nxt = px - self.dir * TICK
                    if (self.dir > 0 and nxt < z) or (self.dir < 0 and nxt > z): return
                    a = px; continue
            a = px

    def on_bar_close(self, i):
        b, p, sc, sg = self.b, self.p, self.sc, self.sg
        if i < 40 or sc.j[i] < 59: return
        sc.update(i)
        # TradeTrack
        if self.trOpen and self.fillQty > 0 and self.posQty > 0:
            a = self.avg(); self.tr["bars"] += 1
            self.tr["mfe"] = max(self.tr["mfe"], (b.h[i] - a) if self.dir > 0 else (a - b.l[i]))
            self.tr["mae"] = max(self.tr["mae"], (a - b.l[i]) if self.dir > 0 else (b.h[i] - a))
        self.update_session(i)
        self.tkEdge = (self.rLo[i], self.rHi[i])
        if self.flat_time(i):
            if self.posQty > 0 and self.pos is not None: self.flatten(i, "FLATTEN_TIME")
            elif self.posQty > 0: self.pending.append(("flat", None, self.posQty))
            self.e3 = None
            return
        force = self.force_needed(i)
        if self.posQty == 0:
            keep_e3 = self.e3
            if self.pos is not None and self.e3 is None: pass
            self.reset_trade()
            self.e3_renewed = False
            if keep_e3 is not None: self.e3_prev = keep_e3
            self.e3 = keep_e3
            cands = self.evaluate(i)
            if force:
                self.e3 = None; d = -1 if sc.C < 0 else 1; self.enter(i, d, "FORCE", 1, True, math.nan)
            elif self.tickOppExitBar == i - 1:
                self.e3 = None
            elif self.inwin(i):
                self.route(i, cands)
                if not getattr(self, "e3_renewed", False) and self.pos is not None and self.e3 is None: pass
            else:
                self.e3 = None
            if self.e3 is not None and not getattr(self, "e3_renewed", False): self.e3 = None
            if self.e3 is None and self.pos is not None and not self.pending: self.reset_trade()
        else:
            if force and not self.gap and self.pos is not None:
                self.flatten(i, "FORCE_EXIT" if self.path == "FORCE" else "FORCE_PREP")
            elif self.gap or self.pos is None:
                if self.posQty > 0 and not any(k in ("exit", "flat") for k, _, _ in self.pending): self.pending.append(("flat", None, self.posQty))
            elif not math.isnan(self.stopPrice) and self.dir * (b.c[i] - self.stopPrice) < 0:
                self.flatten(i, "BE_STOP" if self.beDone else "STOP")
            else:
                self.retry_deferred(i); self.protect(i); self.trail(i)
                if self.is_trend(): self.trend_exits(i)
                else: self.side_exits(i)
                self.warn_check(i); self.add_on(i)
                if "hold" in self.hooks and self.pos is not None: self.hooks["hold"](self, i)
        self.prevWaitSideLong = sg.waitSideLong[i]; self.prevWaitSideShort = sg.waitSideShort[i]
        if "after" in self.hooks: self.hooks["after"](self, i)

    def force_needed(self, i):
        p = self.p
        return p.ForceMinEntries and self.entryCount < p.MinEntries and self.rel(p.ForceStartTime) <= self.rel(self.T(i)) < self.rel(p.FlattenTime)

    def update_session(self, i):
        b, p = self.b, self.p
        w = self.inwin(i)
        if w and not self.wasIn: self.session_reset(i)
        self.wasIn = w
        if w:
            r = self.rel(self.T(i)); o = self.rel(p.OpenTime)
            if 1 <= r <= o:
                self.boxH = b.h[i] if math.isnan(self.boxH) else max(self.boxH, b.h[i]); self.boxL = b.l[i] if math.isnan(self.boxL) else min(self.boxL, b.l[i])
            if math.isnan(self.sessStart): self.sessStart = b.c[i]
            else: self.sessPath += abs(b.c[i] - self.sessPrev); self.sessBars += 1
            self.sessPrev = b.c[i]
            if o <= r <= o + p.E5RangeMinutes - 1:
                self.orH = b.h[i] if math.isnan(self.orH) else max(self.orH, b.h[i]); self.orL = b.l[i] if math.isnan(self.orL) else min(self.orL, b.l[i])
            if self.box_ready(i):
                if b.c[i] <= self.boxH: self.orbAL = True
                if b.c[i] >= self.boxL: self.orbAS = True

    def retry_deferred(self, i):
        if math.isnan(self.deferred) or not self.live or self.fillQty <= 0: return
        if not (math.isnan(self.stopPrice) or self.dir * (self.deferred - self.stopPrice) > 0): self.deferred = math.nan; return
        buf = max(1, self.p.BreakevenBufferTicks) * TICK; cur = self.b.c[i]
        if (cur - self.deferred >= buf) if self.dir > 0 else (self.deferred - cur >= buf): self.apply_stop(self.deferred, self.lastLabel)

    def protect(self, i):
        if self.path == "E6": return
        e5 = self.path == "E5"; atR = self.p.E5BreakevenR if e5 else self.p.BreakevenAtR
        if atR <= 0 or self.tp1Done or (self.beDone and not e5) or self.e5Trail or self.fillQty <= 0 or math.isnan(self.tr["stop0"]): return
        a = self.avg(); risk = abs(a - self.tr["stop0"])
        if risk <= 0 or self.tr["mfe"] < atR * risk: return
        self.move_stop(i, a, "이익 보호 본절", self.b.c[i])
        if e5: self.e5Trail = True

    def trail(self, i):
        if self.fillQty <= 0: return
        b = self.b; d = self.dir
        self.trailExt = (b.h[i] if d > 0 else b.l[i]) if math.isnan(self.trailExt) else (max(self.trailExt, b.h[i]) if d > 0 else min(self.trailExt, b.l[i]))
        k = self.p.TrailAtr if (self.tp2Done or self.e5Trail) else self.p.Tp1TrailAtr if (self.tp1Done and self.path != "E6") else 0
        if not self.is_trend() or k <= 0: return
        a = self.sg.atr[i]
        if a > 0: self.move_stop(i, self.trailExt - d * k * a, "트레일링", b.c[i])

    def trend_exits(self, i):
        b, sc, p = self.b, self.sc, self.p
        if self.path == "B1": return self.breakout_exits(i)
        if self.path in ("E5", "FORCE"): return
        if self.path == "E6":
            if self.tr["bars"] >= p.RangeFadeMaxHold: self.flatten(i, "TIME")
            return
        st = sc.RegimeState(); xs = sc.ExitState()
        if xs == self.dir: self.sawOwn = True
        if xs == -self.dir:
            self.e1AfterExitDir = xs; self.e1AfterExitBar = i; self.flatten(i, "OPP_TREND"); return
        if xs == 0 and self.sawOwn: self.flatten(i, "TREND_END"); return
        if self.suspect(i, st): return
        t2o = self.sg.t2Bear[i] if self.dir > 0 else self.sg.t2Bull[i]
        if t2o and st != self.dir: self.flatten(i, "ENTRY_T2"); return
        ma = self.rg.sm[sc.j[i]]
        if self.tp2Done and self.dir * (self.b3.c[sc.j[i]] - ma) < 0: self.flatten(i, "LAST_MA")

    def breakout_exits(self, i):
        b, sc = self.b, self.sc
        edge = self.rLo[i - 1] if self.dir > 0 else self.rHi[i - 1]
        if -self.dir * (b.c[i] - edge) > 0 and -self.dir * sc.FastNow() >= self.p.BreakoutMinFast: self.flatten(i, "OPP_BREAKOUT"); return
        if self.suspect(i, sc.RegimeState()): return
        ma = self.rg.sm[sc.j[i]]
        if self.tp2Done and self.dir * (self.b3.c[sc.j[i]] - ma) < 0: self.flatten(i, "LAST_MA")

    def suspect(self, i, st):
        sc, p, b = self.sc, self.p, self.b; bo = self.path == "B1"
        trig = sc.fastCrossNow == -self.dir if bo else (sc.suspectEventNow == -self.dir and st == self.dir)
        if trig and i != getattr(self, "susDone", -1):
            self.susEv = i; self.susWait = p.SuspectConfirmBars
        if self.susWait <= 0: return False
        rel = self.dir * sc.FastNow() >= 0 if bo else (sc.contSuspect != -self.dir or st != self.dir)
        if rel: self.susWait = 0; self.susDone = self.susEv; return False
        sw = self.sg.swL[i - 1] if self.dir > 0 else self.sg.swH[i - 1]
        if not (self.dir * (b.c[i] - sw) < 0):
            self.susWait -= 1
            if self.susWait <= 0: self.susDone = self.susEv
            return False
        S = sc.SlowNow(); strong = self.dir * S <= p.SuspectStrongSlow; pct = p.SuspectStrongExitPercent if strong else p.SuspectExitPercent
        code = "SUSPECT_STRONG" if strong else "SUSPECT"; self.susWait = 0; self.susDone = self.susEv
        rem = sum(max(0, self.chunks[s].left) for s in self.live); want = rem * pct // 100
        if want >= rem: self.flatten(i, code); return True
        if want <= 0: return False
        self.exit_far(i, want, code)
        if self.dir * (b.c[i] - self.avg()) > 0: self.move_stop(i, self.avg(), "의심 뒤 평단", b.c[i])
        return len(self.live) == 0

    def side_exits(self, i):
        b, sc, sg = self.b, self.sc, self.sg; st = sc.ExitState()
        if st == -self.dir: self.e1AfterExitDir = st; self.e1AfterExitBar = i; self.flatten(i, "OPP_TREND"); return
        rest = self.band(i, self.dir, self.p.SideRestBand)
        if self.dir * (b.c[i] - rest) >= 0: self.flatten(i, "SIDE_REST_BAND"); return
        if not self.tp1Done: return
        if (sg.mDn[i] if self.dir > 0 else sg.mUp[i]): self.flatten(i, "SIDE_MACD"); return
        if (sg.dead[i] if self.dir > 0 else sg.gold[i]): self.flatten(i, "SIDE_CROSS")

    def warn_check(self, i):
        if self.warnDone or self.pos is None or self.path == "FORCE" or self.sc.r7Warn != -self.dir or self.posQty == 0: return
        self.warnDone = True
        rem = sum(max(0, self.chunks[s].left) for s in self.live); want = rem // 2
        if want >= rem: self.flatten(i, "WARN"); return
        if want > 0: self.exit_far(i, want, "WARN")
        if self.dir * (self.b.c[i] - self.avg()) > 0: self.move_stop(i, self.avg(), "전환의심 뒤 평단", self.b.c[i])

    def add_on(self, i):
        p, sc = self.p, self.sc
        if not p.UseAddOn or not sc.ready or not self.is_trend() or self.path in ("E5", "E6", "FORCE") or self.pos is None or not self.tp1Done or self.addSig: return
        if self.posQty == 0 or self.fillQty <= 0: return
        if self.rel(self.T(i)) >= self.rel(p.AddOnEndTime) or self.dir * sc.V < p.AddOnMinVel: return
        q = max(1, self.baseQty * p.AddOnPercent // 100)
        self.move_stop(i, self.avg(), "추가 진입 본전", self.b.c[i])
        self.addSig = f"{self.pos}{self.signalSeq}.4"
        self.add_chunk(self.addSig, q, 3, math.nan); self.chunks[self.addSig].stop = self.stopPrice
        self.pending.append(("add", self.addSig, q))

    def run(self, start=None, end=None):
        for i in range(self.b.n):
            t = self.b.t[i]
            if end and t > end: break
            self.bar(i)
        return self.trades
