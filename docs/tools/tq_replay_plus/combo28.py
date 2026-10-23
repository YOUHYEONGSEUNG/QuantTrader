# 흔들리는 장 판단이 강한 추세를 막는 문제 실험 (10/23 오후, 10/20 22:32~23:06 급락·급등에서 흔들림 표시가 진입을 막음)
#   chop_ovr ("score", c)  7레짐 '추세'(±2)이고 |점수 C| ≥ c면 흔들림 무시
#            ("move", k)   이 추세 구간 시작 종가부터 진입 방향으로 k × 1분 ATR 넘게 갔으면 흔들림 무시
#            ("roll", w)   흔들림을 대회 구간 시작부터가 아니라 대회 구간 안 최근 w봉 효율비로 판단 (w봉 전까지는 시작부터)
import math
from combo21 import Combo21

class Combo28(Combo21):
    def __init__(self, b, chop_ovr=None, **kw):
        self.ovr = chop_ovr; self.runStartPx = math.nan; self.sessCloses = []
        super().__init__(b, **kw)

    def update_session(self, i):
        prevDir = self.runDir
        super().update_session(i)
        if self.runDir != prevDir: self.runStartPx = self.b.c[i]
        t = self.b.t[i][11:16]
        if t == "21:30": self.sessCloses = []
        if "21:30" <= t <= "23:30": self.sessCloses.append(self.b.c[i])

    def chop(self, i):
        o = self.ovr
        if o and o[0] == "roll":
            er = self.o5["chop_er"]
            if er is None or self.sessBars < 60: return False
            w = o[1]; cs = self.sessCloses[-(w + 1):]
            path = sum(abs(cs[k] - cs[k - 1]) for k in range(1, len(cs)))
            return path > 0 and abs(cs[-1] - cs[0]) / path <= er
        base = super().chop(i)
        if not base or not o: return base
        sc = self.sc
        if not getattr(sc, "ready", False) or abs(sc.r7) != 2: return base
        d = 1 if sc.r7 > 0 else -1
        if o[0] == "score" and abs(sc.C) >= o[1]: return False
        if o[0] == "move" and not math.isnan(self.runStartPx) and self.ts.atr[i] > 0 and d * (self.b.c[i] - self.runStartPx) / self.ts.atr[i] >= o[1]: return False
        return base


class Combo28R(Combo28):
    """chop_ovr ("brk", c, m): 7레짐 '추세'이고 |C| ≥ c이며, 지금 종가가 대회 구간 지금까지 고가·저가(직전 m봉 제외)를 진입 방향으로 넘었으면 흔들림 무시.
    흔들리던 날이 범위를 벗어나 추세로 바뀐 것만 인정 (범위 안에서 튄 것은 그대로 흔들림)"""
    def chop(self, i):
        o = self.ovr
        if not o or o[0] != "brk": return super().chop(i)
        base = Combo28.chop.__wrapped__(self, i) if hasattr(Combo28.chop, "__wrapped__") else super(Combo28, self).chop(i)
        if not base: return base
        sc = self.sc
        if not getattr(sc, "ready", False) or abs(sc.r7) != 2 or abs(sc.C) < o[1]: return base
        d = 1 if sc.r7 > 0 else -1; m = o[2]; cs = self.sessCloses
        if len(cs) <= m + 1: return base
        prior = self.sessHL(i, m)
        if prior is None: return base
        hi, lo = prior
        if (d > 0 and self.b.c[i] > hi) or (d < 0 and self.b.c[i] < lo): return False
        return base
    def sessHL(self, i, m):
        b = self.b; k = i - m; hi = -1e18; lo = 1e18; n = 0
        while k >= 0 and b.t[k][:10] == b.t[i][:10] and b.t[k][11:16] >= "21:30":
            hi = max(hi, b.h[k]); lo = min(lo, b.l[k]); k -= 1; n += 1
        return (hi, lo) if n > 0 else None
