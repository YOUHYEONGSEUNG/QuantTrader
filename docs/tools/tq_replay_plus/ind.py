# TQ_Strategy 재현기 1단계: NinjaTrader 내장·팀 지표를 같은 식으로 계산 (1분봉, 3분봉)
import math, json, datetime as dt

TICK = 0.25


class Bars:
    def __init__(self, rows):
        self.t = [r[0] for r in rows]
        self.o = [r[1] for r in rows]; self.h = [r[2] for r in rows]; self.l = [r[3] for r in rows]; self.c = [r[4] for r in rows]
        self.n = len(rows)


def ema(x, p):
    k1 = 2.0 / (1 + p); k2 = 1 - k1; out = []
    for i, v in enumerate(x):
        out.append(v if i == 0 else v * k1 + k2 * out[-1])
    return out


def sma(x, p):
    out = []; s = 0.0
    for i, v in enumerate(x):
        s += v
        if i >= p: s -= x[i - p]
        out.append(s / min(i + 1, p))
    return out


def atr(b, p):
    out = []
    for i in range(b.n):
        if i == 0: out.append(b.h[0] - b.l[0]); continue
        c1 = b.c[i - 1]; tr = max(abs(b.l[i] - c1), max(b.h[i] - b.l[i], abs(b.h[i] - c1)))
        m = min(i + 1, p); out.append(((m - 1) * out[-1] + tr) / m)
    return out


def macd(x, f=12, s=26, sm=9):
    c1, c2 = 2 / (1 + f), 1 - 2 / (1 + f); c3, c4 = 2 / (1 + s), 1 - 2 / (1 + s); c5, c6 = 2 / (1 + sm), 1 - 2 / (1 + sm)
    line, avg, diff = [], [], []; fe = se = 0
    for i, v in enumerate(x):
        if i == 0: fe = se = v; line.append(0); avg.append(0); diff.append(0); continue
        fe = c1 * v + c2 * fe; se = c3 * v + c4 * se; m = fe - se; a = c5 * m + c6 * avg[-1]
        line.append(m); avg.append(a); diff.append(m - a)
    return line, avg, diff


def rsi(x, p=14):
    out = [50.0] * len(x); dn = [0.0] * len(x); up = [0.0] * len(x); ad = au = 0.0
    for i in range(1, len(x)):
        dn[i] = max(x[i - 1] - x[i], 0); up[i] = max(x[i] - x[i - 1], 0)
        if i + 1 < p: continue
        if i + 1 == p:
            ad = sum(dn[max(0, i - p + 1):i + 1]) / min(i + 1, p); au = sum(up[max(0, i - p + 1):i + 1]) / min(i + 1, p)
        else:
            ad = (ad * (p - 1) + dn[i]) / p; au = (au * (p - 1) + up[i]) / p
        out[i] = 100 if ad == 0 else 100 - 100 / (1 + au / ad)
    return out


def rmax(x, p):
    return [max(x[max(0, i - p + 1):i + 1]) for i in range(len(x))]


def rmin(x, p):
    return [min(x[max(0, i - p + 1):i + 1]) for i in range(len(x))]


def to3(b):
    """1분봉 → 3분봉 (끝 시각 = 분 % 3 == 0, NinjaTrader 시각은 봉 끝)"""
    rows = []; cur = None
    for i in range(b.n):
        t = dt.datetime.fromisoformat(b.t[i])
        m = t.hour * 60 + t.minute
        end = t + dt.timedelta(minutes=(-m) % 3)
        key = end.isoformat()
        if cur is None or cur[0] != key:
            if cur: rows.append(cur)
            cur = [key, b.o[i], b.h[i], b.l[i], b.c[i]]
        else:
            cur[2] = max(cur[2], b.h[i]); cur[3] = min(cur[3], b.l[i]); cur[4] = b.c[i]
    rows.append(cur)
    return Bars(rows)


class Signals:
    """TQ_Signals + TQ_MacdTurn + TQ_ATRChannels (전략이 쓰는 값만)"""
    def __init__(self, b):
        n = b.n
        self.mid = ema(b.c, 26); self.atr = atr(b, 14)
        self.up = {k: [self.mid[i] + self.atr[i] * k for i in range(n)] for k in (1, 2, 3)}
        self.dn = {k: [self.mid[i] - self.atr[i] * k for i in range(n)] for k in (1, 2, 3)}
        _, _, diff = macd(b.c); self.hist = diff
        lo10 = rmin(b.l, 10); hi10 = rmax(b.h, 10)
        raw = []
        for i in range(n):
            r = hi10[i] - lo10[i]
            raw.append((50 if i == 0 else raw[-1]) if r == 0 else 100 * (b.c[i] - lo10[i]) / r)
        self.K = sma(raw, 5); self.D = sma(self.K, 5)
        self.swH = rmax(b.h, 5); self.swL = rmin(b.l, 5)
        up_ = [False] * n; dw = [False] * n; self.mUp = [False] * n; self.mDn = [False] * n
        for i in range(n):
            if i >= 2:
                iu = all(diff[i - k] > diff[i - k - 1] for k in range(2)); idn = all(diff[i - k] < diff[i - k - 1] for k in range(2))
            else:
                iu = idn = False
            up_[i] = iu; dw[i] = idn
            self.mUp[i] = iu and not (i > 0 and up_[i - 1]); self.mDn[i] = idn and not (i > 0 and dw[i - 1])
        self.gold = [False] * n; self.dead = [False] * n; self.t2Bull = [False] * n; self.t2Bear = [False] * n
        self.eUpLong = [False] * n; self.eDnShort = [False] * n; self.eSideLong = [False] * n; self.eSideShort = [False] * n
        self.waitSideLong = [-1] * n; self.waitSideShort = [-1] * n
        w = dict(ul=-1, ds=-1, sl=-1, ss=-1); T1B = 5 + 2 - 1
        def step(key, band, rev):
            fire = False
            if w[key] >= 0:
                w[key] += 1
                if rev: fire = True; w[key] = -1
                elif w[key] >= T1B: w[key] = -1
            if band: w[key] = 0
            return fire
        for i in range(n):
            if i < 4: continue
            K, D = self.K, self.D
            kl = any(K[i - j] < 20 for j in range(5)); kh = any(K[i - j] > 80 for j in range(5))
            g = K[i - 1] <= D[i - 1] and K[i] > D[i] and kl; d = K[i - 1] >= D[i - 1] and K[i] < D[i] and kh
            mu, md = self.mUp[i], self.mDn[i]
            self.gold[i] = g; self.dead[i] = d
            tb = (mu and g) or (self.mUp[i - 1] and g) or (self.gold[i - 1] and mu)
            tr = (md and d) or (self.mDn[i - 1] and d) or (self.dead[i - 1] and md)
            if mu and not self.mUp[i - 1] and self.gold[i - 2]: tb = True
            if md and not self.mDn[i - 1] and self.dead[i - 2]: tr = True
            self.t2Bull[i] = tb; self.t2Bear[i] = tr
            ru = mu or g; rd = md or d
            self.eUpLong[i] = step("ul", b.l[i] <= self.mid[i], ru)
            self.eDnShort[i] = step("ds", b.h[i] >= self.mid[i], rd)
            self.eSideLong[i] = step("sl", b.l[i] <= self.dn[2][i], ru) or tb
            self.eSideShort[i] = step("ss", b.h[i] >= self.up[2][i], rd) or tr
            self.waitSideLong[i] = w["sl"]; self.waitSideShort[i] = w["ss"]


class Regime3:
    """TQ_Regime (3분봉)이 내는 이평·MACD선·RSI + 추세 봉 ATR(14)"""
    def __init__(self, b3):
        self.b = b3
        self.sf = sma(b3.c, 6); self.sm = sma(b3.c, 20); self.ss = sma(b3.c, 60)
        self.ml, _, _ = macd(b3.c); self.rsi = rsi(b3.c, 14); self.atr = atr(b3, 14)
