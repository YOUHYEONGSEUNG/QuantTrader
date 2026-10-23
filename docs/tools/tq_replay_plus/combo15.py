# 러너 청산을 3분봉 기준으로 (10/23 밤 실험)
#   run3 = (mode, 값, 단계, 1분 SMA 청산도 바꿀지)
#     mode "atr3"   진입 뒤 최고가(매도 최저가) − 값 × 3분 ATR(14)
#          "swing3" 마감된 3분봉 최근 값개의 최저 저가(매도 최고 고가) ∓ 1틱
#          "sma3"   마감된 3분봉 종가가 3분 SMA(값) 반대쪽이면 나머지 전량
#     단계 "tp1"(1차 뒤) / "tp2"(2차·강한 모멘텀 뒤). 그 단계부터 1분 ATR 트레일링 대신 씀
#     True면 5.1 강한 모멘텀 청산(1분 종가 < SMA20)·5.4 최종 청산(1분 종가 > SMA20)도 그 단계부터는 안 씀
import math
from combo13 import Combo13
from ind import sma, TICK

class Combo15(Combo13):
    def __init__(self, b, run3=None, **kw):
        self.run3 = run3
        super().__init__(b, **kw)
        if run3 and run3[0] == "sma3": self.sma3 = sma(self.b3.c, run3[1])
        self.j3seen = -1

    def stage3(self):
        if not self.run3 or self.path not in ("T51", "T54") or not self.trader_trade() or self.tm is None: return False
        return self.tm["tp1"] if self.run3[2] == "tp1" else (self.tm["tp2"] or self.tm["strong"])

    def trail(self, i):
        if not self.stage3(): return super().trail(i)
        for s in self.live: self.chunks[s].fixed = self.tband(i, s)   # 밴드 익절 가격 옮기기는 그대로
        if self.fillQty <= 0: return
        b = self.b; d = self.dir; j = self.sc.j[i]; mode, v = self.run3[0], self.run3[1]
        self.trailExt = (b.h[i] if d > 0 else b.l[i]) if math.isnan(self.trailExt) else (max(self.trailExt, b.h[i]) if d > 0 else min(self.trailExt, b.l[i]))
        if mode == "atr3":
            tgt = self.trailExt - d * v * self.rg.atr[j]
        elif mode == "swing3":
            lo = min(self.b3.l[j - v + 1:j + 1]); hi = max(self.b3.h[j - v + 1:j + 1])
            tgt = lo - TICK if d > 0 else hi + TICK
        else:
            return
        keep = self.deferred
        self.move_stop(i, tgt, "3분 트레일링", b.c[i])
        if not math.isnan(keep) and (math.isnan(self.stopPrice) or d * (keep - self.stopPrice) > 0): self.deferred = keep

    def trend_exits(self, i):
        if not self.stage3(): return super().trend_exits(i)
        if self.run3[0] == "sma3":
            j = self.sc.j[i]
            if self.dir * (self.b3.c[j] - self.sma3[j]) < 0: self.flatten(i, "SMA3"); return
        if not self.run3[3]: return super().trend_exits(i)
        # 1분 SMA 청산은 쓰지 않고, 남은 밴드 익절(5.4 2차 −3배)만 봄. 추세 변화 청산은 그대로
        if self.o6["rx6"] and self._rx(i): return
        ts, c, tm = self.ts, self.b.c[i], self.tm
        if self.path == "T51":
            if not tm["strong"]:
                if ts.xUp[3][i]: self.exit_chunk(i, "UpL2", "TP2"); tm["strong"] = True
                elif ts.t2Bear[i]: self.flatten(i, "WEAK")
        else:
            if c <= ts.band[-3][i] and not tm["tp2"]: self.exit_chunk(i, "DnS2", "TP2"); tm["tp2"] = True
