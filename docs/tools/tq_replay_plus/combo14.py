import math
from combo13 import Combo13
class Combo14(Combo13):
    """불타기 조건을 '2차 익절' 대신 '미실현 이익 ≥ add_r × 처음 손절 거리'로 (7레짐 같은 방향 추세·여유 조건은 그대로)"""
    def __init__(self, b, add_r=None, **kw):
        self.add_r = add_r; super().__init__(b, **kw)
    def add_on(self, i):
        if self.add_r is None: return super().add_on(i)
        tm = self.tm
        if not self.trader_trade() or tm is None or self.fillQty <= 0: return super().add_on(i)
        r0 = abs(self.avg() - self.tr["stop0"]) if not math.isnan(self.tr.get("stop0", math.nan)) else math.nan
        if math.isnan(r0) or r0 <= 0 or self.dir * (self.b.c[i] - self.avg()) < self.add_r * r0: return
        keep = self.o11["add_when"]; self.o11["add_when"] = "any"
        try: return super().add_on(i)
        finally: self.o11["add_when"] = keep
