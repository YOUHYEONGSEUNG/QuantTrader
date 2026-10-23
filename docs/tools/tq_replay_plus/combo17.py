import math
from combo16 import Combo16
class Combo17(Combo16):
    """early_q: (끝 시각 'HH:MM', 수량) 그 시각 전 진입 수량 / lock: 그날 실현 ≥ lock이면 신규 진입 안 함(강제 채우기는 그대로)"""
    def __init__(self, b, early_q=None, lock=None, **kw):
        self.early_q = early_q; self.lock = lock; super().__init__(b, **kw)
    def trader_qty(self, i):
        q = super().trader_qty(i)
        if self.early_q and self.b.t[i][11:16] < self.early_q[0]: q = min(q, self.early_q[1])
        return q
    def evaluate(self, i):
        out = super().evaluate(i)
        if self.lock and self.sessPnl >= self.lock: return []
        return out
