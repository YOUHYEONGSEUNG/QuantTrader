# 팀 브랜치(score-v1-logic, 8a4036b) TQ_Strategy 재현기. 지표 식은 ind.py, 흐름은 팀 C# 그대로.
# 체결 모형: 봉 마감 시장가 → 다음 봉 시가, 손절 스탑·밴드 익절 지정가 → 봉 안 경로(시가 → 가까운 극값 → 먼 극값 → 종가)
import math, datetime as dt
from ind import Bars, ema, sma, atr, macd, rsi, rmax, rmin, TICK

PV = 2.0
def rt(x): return round(x / TICK) * TICK


def hhmmss(t): return int(t[11:13]) * 10000 + int(t[14:16]) * 100


def to_n(b, n):
    rows = []; cur = None
    for i in range(b.n):
        t = dt.datetime.fromisoformat(b.t[i]); m = t.hour * 60 + t.minute
        key = (t + dt.timedelta(minutes=(-m) % n)).isoformat()
        if cur is None or cur[0] != key:
            if cur: rows.append(cur)
            cur = [key, b.o[i], b.h[i], b.l[i], b.c[i]]
        else: cur[2] = max(cur[2], b.h[i]); cur[3] = min(cur[3], b.l[i]); cur[4] = b.c[i]
    rows.append(cur); return Bars(rows)


def macd_state(close, confirm=2):
    _, _, diff = macd(close); n = len(close); up = [False] * n; dn = [False] * n
    for i in range(confirm, n):
        up[i] = all(diff[i - k] > diff[i - k - 1] for k in range(confirm))
        dn[i] = all(diff[i - k] < diff[i - k - 1] for k in range(confirm))
    return up, dn


def stoch(b, pk=10, sk=5, sd=5):
    lo = rmin(b.l, pk); hi = rmax(b.h, pk); raw = []
    for i in range(b.n):
        r = hi[i] - lo[i]; raw.append((50 if i == 0 else raw[-1]) if r == 0 else 100 * (b.c[i] - lo[i]) / r)
    K = sma(raw, sk); D = sma(K, sd); return K, D


class P:
    def __init__(self, **kw):
        self.UpLongBand = 0; self.DnShortBand = 0; self.SideBand = 1; self.T1Window = 3; self.MacdConfirmBars = 2
        self.TrendT2Entry = True; self.T2CountConfirmBars = True; self.BandTouchEntry = True; self.UseCounterTrend = False
        self.BlockReentryAfterStop = True
        self.RegimeFast = 6; self.RegimeMid = 18; self.RegimeSlow = 36; self.UseRule12 = True; self.Rule12Bars = 3; self.Rule12PriceFilter = True
        self.FastTrendBars = 2; self.FastTrendSlope = False; self.RegimeMode = 2; self.ScoreMin = 2; self.ScoreRsiBand = 5; self.ScoreEventBars = 3
        self.ScoreExit = 0; self.HistBars = 2; self.DivBars = 15; self.PivotStrength = 2
        self.StopPercent = 0.2; self.UseSwingStop = True; self.MinStopAtr = 1.5; self.StopUseFarthest = True; self.BreakevenAtR = 1.0
        self.BreakevenBufferTicks = 8; self.SideRestBand = 3; self.MomentumSmaPeriod = 20; self.DnShortExitSma = True; self.IntrabarTakeProfit = True
        self.EntryQuantity = 30; self.ContestStartTime = 213000; self.EntryEndTime = 231500; self.FlattenTime = 232700
        self.NewsBlock = 0; self.OpenBlock = 0; self.RiskUsd = 0; self.SideMinAge = 0; self.NoSideLong = False
        self.DivFrom = 5; self.DivTo = 30; self.RsiHigh = 70; self.RsiLow = 20; self.CrossLow = 20; self.CrossHigh = 80; self.CrossFilterBars = 5
        self.StochK = 10; self.StochSmooth = 5; self.StochD = 5; self.SwingBars = 5; self.BandMidPeriod = 26
        for k, v in kw.items(): setattr(self, k, v)


class Sig:
    """팀 TQ_Signals (전략이 쓰는 값)"""
    def __init__(self, b, p):
        n = b.n; self.mid = ema(b.c, p.BandMidPeriod); self.atr = atr(b, 14)
        A = self.atr; M = self.mid
        self.band = {0: M}
        for k in (1, 2, 3): self.band[k] = [M[i] + A[i] * k for i in range(n)]; self.band[-k] = [M[i] - A[i] * k for i in range(n)]
        self.mUp, self.mDn = macd_state(b.c, p.MacdConfirmBars)
        self.K, self.D = stoch(b, p.StochK, p.StochSmooth, p.StochD); self.rsi = rsi(b.c, 14); self.sma = sma(b.c, p.MomentumSmaPeriod)
        self.swH = rmax(b.h, p.SwingBars); self.swL = rmin(b.l, p.SwingBars)
        K, D, R = self.K, self.D, self.rsi
        f = lambda: [False] * n
        self.gold, self.dead, self.k80, self.k20, self.t2Bull, self.t2Bear = f(), f(), f(), f(), f(), f()
        self.xUp = {k: f() for k in (1, 2, 3)}; self.xDn = {k: f() for k in (1, 2, 3)}
        self.eUpLong, self.eDnShort, self.eSideLong, self.eSideShort = f(), f(), f(), f()
        w = dict(ul=-1, ds=-1, sl=-1, ss=-1); T1B = p.T1Window + p.MacdConfirmBars - 1
        def step(key, band, rev):
            fire = False
            if w[key] >= 0:
                w[key] += 1
                if rev: fire = True; w[key] = -1
                elif w[key] >= T1B: w[key] = -1
            if band: w[key] = 0
            return fire
        for i in range(max(p.DivTo + 1, 6), n):
            c0, c1 = b.c[i], b.c[i - 1]
            for k in (1, 2, 3):
                self.xUp[k][i] = c1 <= self.band[k][i - 1] and c0 > self.band[k][i]
                self.xDn[k][i] = c1 >= self.band[-k][i - 1] and c0 < self.band[-k][i]
            kl = any(K[i - j] < p.CrossLow for j in range(p.CrossFilterBars)); kh = any(K[i - j] > p.CrossHigh for j in range(p.CrossFilterBars))
            g = K[i - 1] <= D[i - 1] and K[i] > D[i] and kl; d = K[i - 1] >= D[i - 1] and K[i] < D[i] and kh
            self.gold[i] = g; self.dead[i] = d
            self.k80[i] = K[i - 1] <= p.CrossHigh and K[i] > p.CrossHigh; self.k20[i] = K[i - 1] >= p.CrossLow and K[i] < p.CrossLow
            hiIdx = max(range(p.DivFrom, p.DivTo + 1), key=lambda j: (b.h[i - j], -j)); div = b.h[i] > b.h[i - hiIdx] and R[i] < R[i - hiIdx]
            mu, md = self.mUp[i], self.mDn[i]
            tb = (mu and g) or (self.mUp[i - 1] and g) or (self.gold[i - 1] and mu)
            tr = (md and d) or (self.mDn[i - 1] and d) or (self.dead[i - 1] and md)
            if p.T2CountConfirmBars:
                if mu and not self.mUp[i - 1] and self.gold[i - 2]: tb = True
                if md and not self.mDn[i - 1] and self.dead[i - 2]: tr = True
            self.t2Bull[i] = tb; self.t2Bear[i] = tr
            def touch_up(k): return b.h[i - 1] < self.band[k][i - 1] and b.h[i] >= self.band[k][i]
            def touch_dn(k): return b.l[i - 1] > self.band[-k if k else 0][i - 1] and b.l[i] <= self.band[-k if k else 0][i]
            if p.BandTouchEntry:
                eUp = {k: touch_up(k) for k in (0, 1, 2, 3)}; eDn = {k: touch_dn(k) for k in (0, 1, 2, 3)}
            else:
                eUp = {0: c1 <= M[i - 1] and c0 > M[i], **{k: self.xUp[k][i] for k in (1, 2, 3)}}
                eDn = {0: c1 >= M[i - 1] and c0 < M[i], **{k: self.xDn[k][i] for k in (1, 2, 3)}}
            ru = mu or g; rd = md or d
            self.eUpLong[i] = step("ul", eDn[p.UpLongBand], ru)
            self.eDnShort[i] = step("ds", eUp[p.DnShortBand], rd or (R[i] >= p.RsiHigh and div))
            sb = p.SideBand
            self.eSideLong[i] = step("sl", (sb <= 1 and eDn[1]) or (sb <= 2 and eDn[2]) or eDn[3], ru) or tb
            self.eSideShort[i] = step("ss", (sb <= 1 and eUp[1]) or (sb <= 2 and eUp[2]) or eUp[3], rd) or tr


class Regime:
    """팀 TQ_Regime (10분봉). Mode 2 점수 방식, Mode 1 이동평균 + 빠른 추세 + 1.2·2.2"""
    def __init__(self, b, p):
        n = b.n; self.r = [math.nan] * n
        f = sma(b.c, p.RegimeFast); m = sma(b.c, p.RegimeMid); s = sma(b.c, p.RegimeSlow)
        up, dn = macd_state(b.c, p.HistBars); ml, _, _ = macd(b.c); R = rsi(b.c, 14); K, D = stoch(b)
        mxh = rmax(b.h, p.Rule12Bars); mnl = rmin(b.l, p.Rule12Bars)
        minB = max(p.RegimeSlow - 1, p.DivBars + p.PivotStrength, p.CrossFilterBars, p.HistBars + 1) if p.RegimeMode == 2 else p.RegimeSlow - 1
        trend = 0; lastDir = 0; lastBar = -1
        for i in range(n):
            if i < minB: continue
            c = b.c[i]
            if p.RegimeMode == 2:
                hu = up[i] and not up[i - 1]; hd = dn[i] and not dn[i - 1]
                kl = any(K[i - j] < p.CrossLow for j in range(p.CrossFilterBars)); kh = any(K[i - j] > p.CrossHigh for j in range(p.CrossFilterBars))
                g = K[i - 1] <= D[i - 1] and K[i] > D[i] and kl; d = K[i - 1] >= D[i - 1] and K[i] < D[i] and kh
                hi = lo = -1; ps = p.PivotStrength
                for j in range(ps, p.DivBars + 1):
                    ph = all(b.h[i - j] > b.h[i - j + q] and b.h[i - j] > b.h[i - j - q] for q in range(1, ps + 1))
                    pl = all(b.l[i - j] < b.l[i - j + q] and b.l[i - j] < b.l[i - j - q] for q in range(1, ps + 1))
                    if ph and (hi < 0 or b.h[i - j] > b.h[i - hi]): hi = j
                    if pl and (lo < 0 or b.l[i - j] < b.l[i - lo]): lo = j
                bear = hi >= 0 and b.h[i] > b.h[i - hi] and R[i] < R[i - hi]; bull = lo >= 0 and b.l[i] < b.l[i - lo] and R[i] > R[i - lo]
                be = hu or g or bull; se = hd or d or bear
                if be != se: lastDir = 1 if be else -1; lastBar = i
                cMa = 1 if c > f[i] and c > m[i] and c > s[i] and f[i] > f[i - 1] else -1 if c < f[i] and c < m[i] and c < s[i] and f[i] < f[i - 1] else 0
                cM = 1 if ml[i] > 0 and ml[i] >= ml[i - 1] else -1 if ml[i] < 0 and ml[i] <= ml[i - 1] else 0
                cR = 1 if R[i] >= 50 + p.ScoreRsiBand else -1 if R[i] <= 50 - p.ScoreRsiBand else 0
                cS = 1 if K[i] > D[i] and K[i] > 50 else -1 if K[i] < D[i] and K[i] < 50 else 0
                cE = lastDir if p.ScoreEventBars > 0 and lastBar >= 0 and i - lastBar < p.ScoreEventBars else 0
                sc = cMa + cM + cR + cS + cE
                if trend == 1 and sc <= p.ScoreExit: trend = 0
                if trend == -1 and sc >= -p.ScoreExit: trend = 0
                if trend != 1 and cMa > 0 and sc >= p.ScoreMin: trend = 1
                elif trend != -1 and cMa < 0 and sc <= -p.ScoreMin: trend = -1
                self.r[i] = trend
            else:
                r = 0
                if f[i] > m[i] > s[i] and c > f[i]: r = 1
                elif f[i] < m[i] < s[i] and c < f[i]: r = -1
                else:
                    if p.FastTrendBars > 0:
                        aa = all(b.c[i - k] > f[i - k] for k in range(p.FastTrendBars)); bb = all(b.c[i - k] < f[i - k] for k in range(p.FastTrendBars))
                        if aa: r = 1
                        elif bb: r = -1
                    if r == 0 and p.UseRule12 and i >= p.Rule12Bars * 2 - 1:
                        u = mxh[i] > mxh[i - p.Rule12Bars] and mnl[i] > mnl[i - p.Rule12Bars]
                        dd = mxh[i] < mxh[i - p.Rule12Bars] and mnl[i] < mnl[i - p.Rule12Bars]
                        if p.Rule12PriceFilter: u = u and c > f[i]; dd = dd and c < f[i]
                        r = 1 if u else -1 if dd else 0
                self.r[i] = r


MAIN = ("UpLong", "DnShort")


class Team:
    def __init__(self, b, p=None):
        self.p = p or P(); self.b = b; self.sg = Sig(b, self.p)
        self.b10 = to_n(b, 10); self.rg = Regime(self.b10, self.p)
        self.j = []; j = -1
        for i in range(b.n):
            while j + 1 < self.b10.n and self.b10.t[j + 1] <= b.t[i]: j += 1
            self.j.append(j)
        self.trades = []; self.pending = []; self.posQty = 0; self.wasIn = False; self.entryCount = 0
        self.blockLong = self.blockShort = False; self.reset()

    def reset(self):
        self.active = None; self.isLong = False; self.chunks = {}; self.live = []; self.stop = math.nan; self.initialStop = math.nan
        self.fillSum = 0.0; self.fillQty = 0; self.gap = False; self.bePending = False; self.beDone = False; self.beDeferred = False
        self.tp1 = self.tp2 = False; self.strong = False; self.sideSet = None; self.initialRisk = math.nan; self.reachedR = False

    def d(self): return 1 if self.isLong else -1
    def inwin(self, i): t = hhmmss(self.b.t[i]); return self.p.ContestStartTime <= t <= self.p.EntryEndTime
    def flat_time(self, i): t = hhmmss(self.b.t[i]); return t >= self.p.FlattenTime or t < self.p.ContestStartTime

    def calc_stop(self, px, swing, a):
        p = self.p; L = self.isLong
        pct = px * (1 - p.StopPercent / 100) if L else px * (1 + p.StopPercent / 100)
        if p.StopUseFarthest and p.UseSwingStop: st = swing
        else: st = (max(swing, pct) if L else min(swing, pct)) if p.UseSwingStop else pct
        if p.MinStopAtr > 0 and a > 0 and abs(px - st) < p.MinStopAtr * a: st = px - p.MinStopAtr * a if L else px + p.MinStopAtr * a
        return rt(st)

    def target_band(self, i, sig):
        B = self.sg.band
        return {"UpL1": B[2], "UpL2": B[3], "DnS1": B[-2], "DnS2": B[-3], "SideL1": B[2], "SideS1": B[-2]}.get(sig, [math.nan] * self.b.n)[i]

    def begin(self, i, isLong, act, plan):
        self.reset(); self.isLong = isLong; self.active = act; q = self.p.EntryQuantity
        self.stopAtr = self.sg.atr[i]; self.stopSwing = self.sg.swL[i] if isLong else self.sg.swH[i]
        self.initialStop = self.calc_stop(self.b.c[i], self.stopSwing, self.stopAtr); self.stop = self.initialStop
        if self.p.RiskUsd > 0:
            dist = abs(self.b.c[i] - self.initialStop)
            if dist > 0: q = max(1, min(q, int(self.p.RiskUsd / (dist * PV))))
        for sig, num, den in plan:
            pass
        qs = []; used = 0
        for k, (sig, num, den) in enumerate(plan):
            qq = (q * num) // den if k < len(plan) - 1 else q - used; used += qq; qs.append((sig, qq))
        for sig, qq in qs:
            if qq <= 0: continue
            self.chunks[sig] = dict(q=qq, left=0, tgt=self.target_band(i, sig) if self.p.IntrabarTakeProfit else math.nan); self.live.append(sig)
        self.tr = dict(t0=self.b.t[i], path=act, d=1 if isLong else -1, qty=q, pnl=0.0, exits=[], regime=self.regime(i))
        self.pending.append(("entry", None))

    def regime(self, i):
        if getattr(self, "ext", None) is not None: return self.ext[i]
        j = self.j[i]; return self.rg.r[j] if j >= 0 else math.nan

    def route(self, i):
        r = int(self.regime(i)); sg = self.sg; p = self.p
        canL = not (p.BlockReentryAfterStop and self.blockLong); canS = not (p.BlockReentryAfterStop and self.blockShort)
        if r == 1:
            if sg.eUpLong[i] or (p.TrendT2Entry and sg.t2Bull[i]): self.begin(i, True, "UpLong", [("UpL1", 1, 5), ("UpL2", 2, 5), ("UpL3", 0, 1)])
        elif r == -1:
            if sg.eDnShort[i] or (p.TrendT2Entry and sg.t2Bear[i]): self.begin(i, False, "DnShort", [("DnS1", 1, 3), ("DnS2", 1, 3), ("DnS3", 0, 1)])
        else:
            age = 0; jj = self.j[i]
            while jj - age - 1 >= 0 and self.rg.r[jj - age - 1] == 0: age += 1
            if p.SideMinAge and age + 1 < p.SideMinAge: return
            if canL and sg.eSideLong[i] and not p.NoSideLong: self.begin(i, True, "SideLong", [("SideL1", 1, 2), ("SideL2", 0, 1)])
            elif canS and sg.eSideShort[i]: self.begin(i, False, "SideShort", [("SideS1", 1, 2), ("SideS2", 0, 1)])

    # ── 체결 ──
    def exit_fill(self, i, sig, px, q, kind):
        c = self.chunks[sig]; q = min(q, c["left"])
        if q <= 0: return
        c["left"] -= q; self.posQty -= q; avg = self.fillSum / self.fillQty
        self.tr["pnl"] += self.d() * (px - avg) * q; self.tr["exits"].append((self.b.t[i], kind, sig, px, q))
        if self.posQty <= 0:
            self.tr["usd"] = self.tr["pnl"] * PV; self.tr["t1"] = self.b.t[i]; self.tr["reason"] = kind
            self.trades.append(self.tr); self.posQty = 0

    def first_tp(self, i, px):
        self.bePending = False; self.initialRisk = math.nan; self.reachedR = False
        if self.beDone or not self.live: return
        be = rt(self.fillSum / self.fillQty)
        if not ((px > be) if self.isLong else (px < be)): return
        if not self.safe(be, px): self.beDeferred = True; return
        self.stop = be; self.beDone = True; self.beDeferred = False

    def safe(self, ns, cur):
        buf = self.p.BreakevenBufferTicks * TICK
        return (cur - ns >= buf) if self.isLong else (ns - cur >= buf)

    def bar(self, i):
        b = self.b; pend, self.pending = self.pending, []
        for kind, sig in pend:
            if kind == "entry" and self.active:
                for s, c in self.chunks.items(): c["left"] = c["q"]
                q = sum(c["q"] for c in self.chunks.values()); self.posQty = q; self.fillSum = b.o[i] * q; self.fillQty = q
                self.entryCount += 1; self.tr["avg"] = b.o[i]
                if (b.o[i] <= self.initialStop) if self.isLong else (b.o[i] >= self.initialStop): self.gap = True; self.flatten(i)
                else:
                    s = self.calc_stop(b.o[i], self.stopSwing, self.stopAtr)
                    if (b.o[i] <= s) if self.isLong else (b.o[i] >= s): self.gap = True; self.flatten(i)
                    else: self.stop = s; self.initialRisk = abs(b.o[i] - s); self.tr["stop0"] = s
            elif kind == "x":
                if sig in self.chunks and self.chunks[sig]["left"] > 0:
                    self.exit_fill(i, sig, b.o[i], self.chunks[sig]["left"], "x")
                    if self.bePending: self.first_tp(i, b.o[i])
            elif kind == "all":
                for s in list(self.chunks):
                    if self.chunks[s]["left"] > 0: self.exit_fill(i, s, b.o[i], self.chunks[s]["left"], self.tr.get("why", "flat"))
        pend, self.pending = self.pending, []
        self.pending = pend + self.pending
        o, h, l, c = b.o[i], b.h[i], b.l[i], b.c[i]
        pts = [o, h, l, c] if (h - o) <= (o - l) else [o, l, h, c]
        cur = o
        for nxt in pts[1:]: self.walk(i, cur, nxt); cur = nxt
        self.close(i)

    def walk(self, i, a, z):
        for _ in range(10):
            if self.posQty <= 0: return
            d = self.d(); up = z > a; ev = []
            st = self.stop
            if (d > 0 and a <= st) or (d < 0 and a >= st): ev.append((0, "stop", a))
            elif (d > 0 and not up and z <= st) or (d < 0 and up and z >= st): ev.append((abs(st - a), "stop", st))
            for s in self.live:
                cc = self.chunks[s]
                if cc["left"] <= 0 or math.isnan(cc["tgt"]): continue
                tp = rt(cc["tgt"])
                if (d > 0 and a >= tp) or (d < 0 and a <= tp): ev.append((0, "tp", a, s))
                elif (d > 0 and up and z >= tp) or (d < 0 and not up and z <= tp): ev.append((abs(tp - a), "tp", tp, s))
            if not ev: return
            ev.sort(key=lambda e: e[0]); e = ev[0]; px = e[2]
            if e[1] == "stop":
                loss = (px < self.fillSum / self.fillQty) if self.isLong else (px > self.fillSum / self.fillQty)
                if not self.beDone and loss and self.active not in MAIN and self.active is not None:
                    if self.isLong: self.blockLong = True
                    else: self.blockShort = True
                for s in list(self.chunks):
                    if self.chunks[s]["left"] > 0: self.exit_fill(i, s, px, self.chunks[s]["left"], "STOP" if not self.beDone else "BE_STOP")
                return
            s = e[3]
            if s in self.live: self.live.remove(s)
            self.exit_fill(i, s, px, self.chunks[s]["left"], "TP")
            if s == "DnS2": self.tp2 = True
            else:
                self.tp1 = True
                if s == "UpL2": self.strong = True
                if s in ("SideL1", "SideS1"): self.sideSet = "A"
                self.bePending = True; self.first_tp(i, px)
            a = px

    def exit_chunk(self, i, s):
        if s not in self.live: return
        self.live.remove(s); self.pending.append(("x", s))

    def flatten(self, i, why="flat"):
        self.tr["why"] = why; self.pending.append(("all", None)); self.live = []; self.active = None

    def close(self, i):
        b, p, sg = self.b, self.p, self.sg
        j = self.j[i]
        if i < p.DivTo or j < p.RegimeSlow or math.isnan(self.rg.r[j]): return
        w = self.inwin(i)
        if w and not self.wasIn: self.entryCount = 0; self.blockLong = self.blockShort = False
        self.wasIn = w
        if self.flat_time(i):
            if self.posQty > 0 and not any(k == "all" for k, _ in self.pending): self.flatten(i, "FLATTEN")
            return
        if self.posQty == 0:
            if not self.pending: self.reset()
            t = hhmmss(self.b.t[i]); mm = (t // 10000) * 60 + (t // 100) % 100
            blocked = (p.NewsBlock and 21 * 60 + 30 <= mm < 21 * 60 + 30 + p.NewsBlock) or (p.OpenBlock and 22 * 60 + 25 <= mm < 22 * 60 + 30 + p.OpenBlock)
            if w and not self.pending and not blocked: self.route(i)
            return
        if self.gap or self.active is None:
            if not any(k == "all" for k, _ in self.pending): self.flatten(i, self.tr.get("why", "flat"))
            return
        # 이익 보호 본절 (주력 제외)
        if self.active not in MAIN and p.BreakevenAtR > 0 and not self.beDone and self.live and not math.isnan(self.initialRisk):
            avg = self.fillSum / self.fillQty; fav = (b.h[i] - avg) if self.isLong else (avg - b.l[i])
            if fav >= p.BreakevenAtR * self.initialRisk: self.reachedR = True
            if self.reachedR and self.safe(rt(avg), b.c[i]): self.stop = rt(avg); self.beDone = True
        if self.beDeferred and not self.beDone and self.live:
            be = rt(self.fillSum / self.fillQty)
            if self.safe(be, b.c[i]): self.stop = be; self.beDone = True; self.beDeferred = False
        if p.IntrabarTakeProfit:
            for s in self.live: self.chunks[s]["tgt"] = self.target_band(i, s)
        getattr(self, "m_" + self.active)(i)

    def mbe(self):
        if not self.beDone: self.bePending = True

    def m_UpLong(self, i):
        sg, c = self.sg, self.b.c[i]
        if self.strong:
            if c < sg.sma[i]: self.flatten(i, "STRONG_SMA")
            return
        up3, up2, weak = sg.xUp[3][i], sg.xUp[2][i], sg.t2Bear[i]
        if up3:
            self.mbe()
            if not self.tp1: self.exit_chunk(i, "UpL1")
            self.exit_chunk(i, "UpL2"); self.tp1 = True; self.strong = True; return
        if not self.tp1:
            if weak: self.flatten(i, "WEAK"); return
            if up2: self.mbe(); self.exit_chunk(i, "UpL1"); self.tp1 = True
        elif weak: self.flatten(i, "WEAK")

    def m_DnShort(self, i):
        sg, c, p = self.sg, self.b.c[i], self.p
        final = c > sg.sma[i] if p.DnShortExitSma else sg.rsi[i] < p.RsiLow
        tp1 = c <= sg.band[-2][i] or sg.mUp[i] or sg.k20[i]; tp2 = c <= sg.band[-3][i]
        if not self.tp1:
            if final: self.flatten(i, "FINAL"); return
            if tp1: self.mbe(); self.exit_chunk(i, "DnS1"); self.tp1 = True
            if tp2 and not self.tp2: self.exit_chunk(i, "DnS2"); self.tp2 = True
        else:
            if final: self.flatten(i, "FINAL"); return
            if tp2 and not self.tp2: self.exit_chunk(i, "DnS2"); self.tp2 = True

    def m_SideLong(self, i): self.side(i, 1)
    def m_SideShort(self, i): self.side(i, -1)

    def side(self, i, d):
        sg, b = self.sg, self.b; c = b.c[i]
        if self.sideSet is None:
            aT = (c >= sg.band[2][i] or sg.mDn[i] or sg.dead[i]) if d > 0 else (c <= sg.band[-2][i] or sg.mUp[i] or sg.gold[i])
            bT = sg.k80[i] if d > 0 else sg.k20[i]
            aR = b.h[i] >= sg.band[3][i] if d > 0 else b.l[i] <= sg.band[-3][i]
            bR = sg.t2Bear[i] if d > 0 else sg.t2Bull[i]
            if (aT or bT) and (aR or bR): self.flatten(i, "SIDE_BOTH"); return
            if aR or bR: self.flatten(i, "SIDE_REST"); return
            if aT or bT:
                self.sideSet = "A" if aT else "B"; self.mbe(); self.exit_chunk(i, "SideL1" if d > 0 else "SideS1"); self.tp1 = True
        elif self.sideSet == "A":
            if (b.h[i] >= sg.band[3][i]) if d > 0 else (b.l[i] <= sg.band[-3][i]): self.flatten(i, "SIDE_A")
        else:
            if (sg.t2Bear[i] if d > 0 else sg.t2Bull[i]): self.flatten(i, "SIDE_B")

    def run(self):
        for i in range(self.b.n): self.bar(i)
        return self.trades
