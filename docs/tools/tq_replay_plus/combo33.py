# 하루 손실 막기 실험 (10/23 오후, 8월 13일 검증 뒤): 그날 새 진입을 멈추는 규칙
#   day_stop ("usd", X)  그날 확정 손익(비용 전) ≤ −X달러면 그날 새 진입 안 함
#            ("n", k)    그날 손실 거래가 k번이면 그날 새 진입 안 함
#   (흔들리는 장 판단 변형은 Combo29·Combo21 인자를 그대로 씀)
from combo29 import Combo29

class Combo33(Combo29):
    def __init__(self, b, day_stop=None, **kw):
        self.dstop = day_stop; super().__init__(b, **kw)
    def day_blocked(self, i):
        o = self.dstop
        if not o: return False
        d = self.b.t[i][:10]; ts = [t for t in self.trades if t["t0"][:10] == d and "21:30" <= t["t0"][11:16]]
        if o[0] == "usd": return sum(t["usd"] for t in ts) <= -o[1]
        return sum(t["usd"] < 0 for t in ts) >= o[1]
    def evaluate(self, i):
        out = super().evaluate(i)
        if out and self.day_blocked(i): return []
        return out
