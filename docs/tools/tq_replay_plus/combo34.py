# 매수(5.1)에 매도(5.4)의 SMA20 규칙을 거울로 넣는 실험 (10/23 오후, 8월·골드에서 매수만 크게 잃음)
#   long_block  N: 5.1 매수 신호 봉 종가 < 1분 SMA20이면 보류, N봉 안에 종가 > SMA20이고 7레짐 상승(추세·눌림)이면 매수. None = 끔
#   long_exit   "always": 매수 보유 중 종가 < SMA20이면 전량 청산(5.4 최종 청산의 거울, 강한 모멘텀 청산과 같은 조건)
#               "pre_tp1": 1차 익절 전에만 / None = 끔
import math
from combo33 import Combo33

class Combo34(Combo33):
    def __init__(self, b, long_block=None, long_exit=None, **kw):
        self.lb = long_block; self.lx = long_exit; self.lWaitUntil = -1; super().__init__(b, **kw)
    def evaluate(self, i):
        out = super().evaluate(i)
        if not self.lb: return out
        c, s = self.b.c[i], self.ts.sma[i]
        if any(x["path"] == "T51" for x in out) and c < s:
            out = [x for x in out if x["path"] != "T51"]; self.lWaitUntil = i + self.lb
        if not out and i <= self.lWaitUntil and self.posQty == 0 and c > s and not self.day_blocked(i):
            sc = self.sc; r7 = sc.r7 if getattr(sc, "ready", False) else 0
            if r7 in (2, 3) and self.inwin(i):
                self.lWaitUntil = -1; out = [dict(path="T51", d=1, q=100, fb=False, rr=math.nan)]
        return out
    def trend_exits(self, i):
        if self.lx and self.trader_trade() and self.path == "T51" and self.posQty > 0 and not self.tm["strong"]:
            if self.b.c[i] < self.ts.sma[i] and (self.lx == "always" or not self.tm["tp1"]):
                self.flatten(i, "L_SMA"); return
        return super().trend_exits(i)
