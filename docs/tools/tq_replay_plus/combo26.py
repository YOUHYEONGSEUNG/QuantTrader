# 추세 품질(약한 신호 수)로 수량·불타기 조절 실험 (10/23 밤 9)
#   약한 신호(진입 방향 기준): 3분 SMA20 5봉 기울기 < 0.22 × 3분 ATR, 느린 점수 S < 1.33, 3분 ATR이 최근 20봉 평균보다 작음,
#                             7레짐 같은 방향 '추세' 10봉 미만, 최근 3분봉 5개 고점·저점이 진입 방향으로 오른(내린) 횟수 < 5
#   q_strong  약한 신호 0개일 때 진입 수량 (None = 그대로) / q_weak (n, 수량) 약한 신호 n개 이상이면 그 수량
#   add_strong_only  True면 불타기는 진입 때 약한 신호 0개인 거래만
import math
from combo25 import Combo25

class Combo26(Combo25):
    def __init__(self, b, q_strong=None, q_weak=None, add_strong_only=False, **kw):
        self.o26 = dict(qs=q_strong, qw=q_weak, ad=add_strong_only); self.entryWeak = 0
        super().__init__(b, **kw)

    def weak_count(self, i, d):
        sc, rg, b3 = self.sc, self.rg, self.b3; j = sc.j[i]; a3 = rg.atr[j]
        if not getattr(sc, "ready", False) or a3 <= 0 or j < 25: return 0
        w = 0
        if d * (rg.sm[j] - rg.sm[j - 5]) / a3 < 0.22: w += 1
        if d * sc.S < 1.33: w += 1
        if a3 < sum(rg.atr[j - 20:j]) / 20: w += 1
        if self.runLen < 10: w += 1
        hh = sum(1 for k in range(5) if (b3.h[j - k] > b3.h[j - k - 1]) == (d > 0)) + sum(1 for k in range(5) if (b3.l[j - k] > b3.l[j - k - 1]) == (d > 0))
        if hh < 5: w += 1
        return w

    def trader_qty(self, i):
        q = super().trader_qty(i); o = self.o26
        if self.path in ("T51", "T54"):
            w = self.weak_count(i, self.dir); self.entryWeak = w
            dist = abs(self.b.c[i] - self.initialStop); cap = self.o11["cap"]
            if o["qs"] is not None and w == 0:
                q2 = o["qs"]
                if cap and dist > 0: q2 = min(q2, int(cap / (dist * 2)))
                q = max(1, min(q2, self.o11["max_q"]))
            if o["qw"] is not None and w >= o["qw"][0]: q = max(1, min(q, o["qw"][1]))
        return q

    def add_on(self, i):
        if self.o26["ad"] and self.entryWeak > 0: return
        return super().add_on(i)
