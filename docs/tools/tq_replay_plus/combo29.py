# 흔들리는 장 판단 고도화 실험 (10/23 오후): 이론으로 정한 값만 씀, 차트에 맞춰 고르지 않음
#   chop_def
#     ("ewer", h)            지수 감쇠 효율비: 대회 구간 시작부터 Σ wΔc / Σ w|Δc|, 반감기 h봉(최근 봉일수록 무게). ≤ chop_er(0.15)면 흔들림
#     ("ci", n, hi)          Choppiness Index(3분봉 n개) = 100·log10(Σ TR ÷ (최고−최저)) ÷ log10(n). ≥ hi(61.8)면 흔들림
#     ("vr", w, q, thr)      분산비: 최근 w개 1분 수익률로 Var(q봉 합) ÷ (q·Var(1봉)). < thr(1.0)면 흔들림(평균 회귀)
#     ("accept", k)          지금 판단(그날 효율비) + 마지막 k개 종가가 모두 그 전 대회 구간 고가 위(또는 저가 아래)면 흔들림 해제(돌파 뒤 머묾 = 받아들여짐)
#     ("ci_rel", n, lo)      지금 판단 + Choppiness Index ≤ lo(38.2, 추세)면 흔들림 해제
#   warm  대회 시작 뒤 이 봉 수 전에는 흔들림으로 안 봄 (지금 판단과 같은 60)
import math
from combo28 import Combo28

class Combo29(Combo28):
    def __init__(self, b, chop_def=None, warm=60, **kw):
        self.cdef = chop_def; self.warm = warm; self.ewN = 0.0; self.ewP = 0.0; self.prevC = math.nan
        super().__init__(b, **kw)
        if chop_def and chop_def[0] in ("ci", "ci_rel"):
            n = chop_def[1]; b3 = self.b3; m = b3.n; self.ci = [math.nan] * m
            tr = [b3.h[0] - b3.l[0]] + [max(b3.h[k], b3.c[k - 1]) - min(b3.l[k], b3.c[k - 1]) for k in range(1, m)]
            for k in range(n, m):
                hi = max(b3.h[k - n + 1:k + 1]); lo = min(b3.l[k - n + 1:k + 1])
                if hi > lo: self.ci[k] = 100 * math.log10(sum(tr[k - n + 1:k + 1]) / (hi - lo)) / math.log10(n)

    def update_session(self, i):
        super().update_session(i)
        t = self.b.t[i][11:16]
        if self.cdef and self.cdef[0] == "ewer":
            if t == "21:30" or math.isnan(self.prevC): self.ewN = 0.0; self.ewP = 0.0
            elif "21:30" < t <= "23:30":
                lam = 0.5 ** (1.0 / self.cdef[1]); dc = self.b.c[i] - self.prevC
                self.ewN = lam * self.ewN + dc; self.ewP = lam * self.ewP + abs(dc)
            self.prevC = self.b.c[i]

    def chop(self, i):
        o = self.cdef; er = self.o5["chop_er"]
        if not o or er is None: return super().chop(i)
        if self.sessBars < self.warm: return False
        k = o[0]
        if k == "ewer": return self.ewP > 0 and abs(self.ewN) / self.ewP <= er
        if k == "ci":
            v = self.ci[self.sc.j[i]]; return not math.isnan(v) and v >= o[2]
        if k == "vr":
            w, q, thr = o[1], o[2], o[3]; c = self.b.c
            r1 = [c[x] - c[x - 1] for x in range(i - w + 1, i + 1)]
            rq = [c[x] - c[x - q] for x in range(i - w + q, i + 1)]
            m1 = sum(r1) / len(r1); v1 = sum((x - m1) ** 2 for x in r1) / len(r1)
            mq = sum(rq) / len(rq); vq = sum((x - mq) ** 2 for x in rq) / len(rq)
            return v1 > 0 and vq / (q * v1) < thr
        base = super().chop(i)
        if not base: return False
        if k == "accept":
            kk = o[1]; b = self.b; hi = -1e18; lo = 1e18; x = i - kk
            while x >= 0 and b.t[x][:10] == b.t[i][:10] and b.t[x][11:16] >= "21:30":
                hi = max(hi, b.h[x]); lo = min(lo, b.l[x]); x -= 1
            last = b.c[i - kk + 1:i + 1]
            if hi > -1e17 and (all(v > hi for v in last) or all(v < lo for v in last)): return False
            return True
        if k == "ci_rel":
            v = self.ci[self.sc.j[i]]
            return not (not math.isnan(v) and v <= o[2])
        return base


class Combo29V(Combo29):
    """세 판단 묶기. chop_vote = ("maj", n, w) 셋 중 둘 이상이 흔들림이면 흔들림 / ("both", n, w) 지금 판단이 흔들림이어도 CI·분산비 둘 다 추세면 해제
    세 판단: 그날 효율비 ≤ 0.15(지금), Choppiness Index(3분 n개) ≥ 61.8, 분산비(1분 w개, 5봉) < 1.0"""
    def __init__(self, b, chop_vote=None, **kw):
        self.vote = chop_vote
        super().__init__(b, chop_def=("ci", chop_vote[1], 61.8) if chop_vote else None, **kw)
    def chop(self, i):
        if not self.vote: return super().chop(i)
        if self.sessBars < self.warm or self.o5["chop_er"] is None: return False
        mode, n, w = self.vote
        a = Combo29.__mro__[1].chop(self, i)          # 지금 판단 (Combo28 → 그날 효율비)
        keep = self.cdef
        self.cdef = ("ci", n, 61.8); b = Combo29.chop(self, i)
        self.cdef = ("vr", w, 5, 1.0); c = Combo29.chop(self, i)
        self.cdef = keep
        if mode == "maj": return (a + b + c) >= 2
        return a and (b or c)
