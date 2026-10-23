# 3분봉 길이가 늘거나 줄어드는 것(봉 가속도) 실험 (10/23 오후). 마감된 3분봉만 씀(1분봉마다 마지막 마감 3분봉을 봄)
#   bar3 = (규칙, k)
#     "pull_big"   눌림·T2 진입: 마지막 3분봉이 진입 반대 방향이고 길이가 최근 10개 평균의 k배 이상이면 진입 안 함(아직 힘 있는 되돌림)
#     "pull_shrink" 눌림·T2 진입: 마지막 3분봉 길이 < 그 전 봉 길이(되돌림이 힘을 잃는 중)일 때만
#     "cont_go"    추세 지속 진입: 마지막 3분봉이 진입 방향이고 길이가 평균의 k배 이상일 때만
import math
from combo19 import Combo20

class Combo32(Combo20):
    def __init__(self, b, bar3=None, **kw):
        self.bar3 = bar3; super().__init__(b, **kw)
    def last3(self, i):
        b3 = self.b3; j = self.sc.j[i]
        if j < 12: return None
        rng = [b3.h[k] - b3.l[k] for k in range(j - 10, j)]
        return dict(r0=b3.h[j] - b3.l[j], r1=b3.h[j - 1] - b3.l[j - 1], body=b3.c[j] - b3.o[j], avg=sum(rng) / len(rng))
    def evaluate(self, i):
        before = self.usedRun
        out = super().evaluate(i)
        o = self.bar3
        if not o or not out or out[0]["path"] not in ("T51", "T54") or out[0].get("rev"): return out
        x = self.last3(i)
        if x is None or x["avg"] <= 0: return out
        d = out[0]["d"]; isCont = (not before) and self.usedRun
        rule, k = o
        if rule == "pull_big" and not isCont and d * x["body"] < 0 and x["r0"] >= k * x["avg"]: return []
        if rule == "pull_shrink" and not isCont and not (x["r0"] < x["r1"]): return []
        if rule == "cont_go" and isCont and not (d * x["body"] > 0 and x["r0"] >= k * x["avg"]):
            self.usedRun = False; return []
        return out
