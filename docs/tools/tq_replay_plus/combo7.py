# 추세 지속 진입: 7레짐이 같은 방향 '추세'(±2)로 K봉 이상 이어지는데 진입이 없을 때 추세 방향으로 들어감 (5.1·5.4 경로, 청산은 같은 규칙)
#   cont_k    None = 끔 / 같은 방향 추세(|r7|=2)가 이어진 봉 수 (보유 중에도 매 봉 셈. r7 부호가 바뀌면 0)
#   cont_mid  True면 종가가 중심선(EMA26)의 추세 쪽일 때만
#   cont_once True면 같은 추세 구간에서 지속 진입은 한 번만
#   cont_chop True면 흔들리는 장(ChopDay)에서는 안 함
import math
from combo6 import Combo6


class Combo7(Combo6):
    def __init__(self, b, cont_k=None, cont_mid=True, cont_once=True, cont_chop=True, **kw):
        self.o7 = dict(k=cont_k, mid=cont_mid, once=cont_once, chop=cont_chop)
        self.runDir = 0; self.runLen = 0; self.usedRun = False
        super().__init__(b, **kw)

    def update_session(self, i):
        super().update_session(i)
        sc = self.sc; r7 = sc.r7 if getattr(sc, "ready", False) else 0
        d7 = 1 if r7 > 0 else -1 if r7 < 0 else 0
        if d7 != self.runDir: self.runDir = d7; self.runLen = 0; self.usedRun = False
        if abs(r7) == 2: self.runLen += 1

    def evaluate(self, i):
        out = super().evaluate(i); o = self.o7
        if o["k"] is None or out or self.posQty > 0: return out
        sc = self.sc; r7 = sc.r7 if getattr(sc, "ready", False) else 0; d7 = self.runDir
        if d7 == 0 or abs(r7) != 2 or self.runLen < o["k"]: return out
        if o["once"] and self.usedRun: return out
        if not self.inwin(i): return out
        if o["chop"] and self.chop(i): return out
        if o["mid"] and d7 * (self.b.c[i] - self.ts.mid[i]) <= 0: return out
        self.usedRun = True
        out.append(dict(path="T51" if d7 > 0 else "T54", d=d7, q=100, fb=False, rr=math.nan))
        return out
