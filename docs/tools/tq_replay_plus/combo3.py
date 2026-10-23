# 상태별 동작 재현기 (설계 A·B). Combo2(청산 부품) 위에 7레짐 상태로 트레이더 진입을 고르고, v6 위험 관리를 따로 켬
#   tstate  추세 진입(5.1·5.4) 허용 상태: "any" / "trendpull" (7레짐 추세·눌림 같은 방향) / "pull" (눌림만)
#   sstate  횡보 진입(5.5·5.6): "any" / "flat" (7레짐 횡보) / "flat10" (횡보 10분 넘게) / "off"
#   sdir    횡보 방향 필터: 연속 S+F가 반대로 ±3(환산) 이상이면 그 방향 횡보 진입 안 함
#   warn    전환의심 중 원래 방향 트레이더 진입 금지 + 보유 중이면 50% 청산(v6 WARN)
#   news    21:30~21:34 트레이더 진입 금지 / pre: 장전 트레이더 진입 수량 50% / risk: 1회 위험 $2,000 수량(트레이더·돌파 모두)
import math
from combo2 import Combo2
from combo import TRADER
from strat import Strat, PV


class Combo3(Combo2):
    def __init__(self, b, tstate="any", sstate="any", sdir=False, warn=False, news=False, pre=False, risk=False, exits_v6=False, **kw):
        self.exits_v6 = exits_v6
        self.o = dict(tstate=tstate, sstate=sstate, sdir=sdir, warn=warn, news=news, pre=pre, risk=risk)
        kw.setdefault("reg", "v6")
        super().__init__(b, size=("v6" if risk else "fixed"), timeblk=False, **kw)
        self.flatSince = -1
        if exits_v6: self.exits_mode = "v6"

    def evaluate(self, i):
        out = super().evaluate(i); o = self.o; sc = self.sc
        r7 = sc.r7 if getattr(sc, "ready", False) else 0
        if r7 == 0:
            if self.flatSince < 0: self.flatSince = i
        else: self.flatSince = -1
        res = []
        for c in out:
            if c["path"] not in TRADER: res.append(c); continue
            d = c["d"]
            if o["news"] and self.zone(i) == "News": continue
            if o["warn"] and sc.r7Warn == -d: continue
            if c["path"] in ("T51", "T54"):
                d7 = 1 if r7 > 0 else -1 if r7 < 0 else 0
                if o["tstate"] == "trendpull" and not (d7 == d and abs(r7) in (2, 3)): continue
                if o["tstate"] == "pull" and not (d7 == d and abs(r7) == 3): continue
            else:
                if o["sstate"] == "off": continue
                if o["sstate"] in ("flat", "flat10") and r7 != 0: continue
                if o["sstate"] == "flat10" and (self.flatSince < 0 or i - self.flatSince < 10): continue
                if o["sdir"] and d * (sc.SlowNow() + sc.FastNow()) <= -3: continue
            res.append(c)
        return res

    def trader_qty(self, i):
        q = 30
        if self.o["risk"]:
            dist = abs(self.b.c[i] - self.initialStop)
            if dist > 0: q = max(1, min(q, int(2000 / (dist * PV))))
        if self.o["pre"] and self.zone(i) == "Pre": q = max(1, q // 2)
        return q

    def warn_check(self, i):
        if self.trader_trade() and self.o["warn"]: return Strat.warn_check(self, i)
        return super().warn_check(i)
