# 청산을 반대 진입 신호로 쓰기 실험 (10/23 밤 9)
#   rev = (이유들, 손실만, 수량 배율, 대기 봉)
#     추세 진입(5.1·5.4) 거래가 이유들 중 하나로 끝나면(손실만=True면 손해일 때만), 대기 봉 안에 미보유면 반대 방향으로 진입(5.1·5.4와 같은 분할·손절·청산)
#     이유: "STOP" 처음 손절, "FINAL" 5.4 최종 청산(SMA20), "WEAK" 5.1 약한 모멘텀, "TREND_END"·"OPP_TREND" 청산용 판정
import math
from combo24 import Combo24

class Combo25(Combo24):
    def __init__(self, b, rev=None, **kw):
        self.rev = rev; self.revSig = None; self.revQtyMul = 1.0
        super().__init__(b, **kw)

    def trade_end(self, i):
        tr = self.tr
        super().trade_end(i)
        if not self.rev or tr is None or tr.get("path") not in ("T51", "T54") or tr.get("rev"): return
        reasons, loss_only, mul, wait = self.rev
        if tr.get("reason") in reasons and (not loss_only or tr.get("usd", 0) < 0):
            self.revSig = dict(d=-tr["d"], until=i + wait)

    def evaluate(self, i):
        out = super().evaluate(i)
        r = self.revSig
        if r and i <= r["until"] and self.posQty == 0 and self.inwin(i):
            self.revSig = None
            return [dict(path="T51" if r["d"] > 0 else "T54", d=r["d"], q=100, fb=False, rr=math.nan, rev=True)]
        if r and i > r["until"]: self.revSig = None
        return out

    def route(self, i, cands):
        self.revNext = bool(cands and cands[0].get("rev"))
        return super().route(i, cands)

    def trader_qty(self, i):
        q = super().trader_qty(i)
        if getattr(self, "revNext", False): q = max(1, int(q * self.rev[2]))
        return q

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        if getattr(self, "revNext", False) and self.tr is not None and self.tr.get("i0") == i: self.tr["rev"] = True
        self.revNext = False
        return r
