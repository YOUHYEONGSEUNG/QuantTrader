# 통합안 재현기: 장 상태에 따라 진입 범위 바꾸기(흔들리는 장이면 눌림만), 달린 뒤 진입 건너뛰기, v6 박스 돌파(봉 마감)
import math
from combo4 import Combo4
from combo import TRADER


class Combo5(Combo4):
    def __init__(self, b, chop_er=None, chop_flip=None, flip_win=60, ext_atr=None, b1=False, chop_mode="pull", **kw):
        self.o5 = dict(chop_er=chop_er, chop_flip=chop_flip, flip_win=flip_win, ext_atr=ext_atr, chop_mode=chop_mode)
        super().__init__(b, alpha=b1, **kw)
        if b1:
            p = self.p
            p.UseEntryTrendStart = False; p.UseEntryOrb = False; p.E3StopEntry = False; p.UseEntryOpenRange = False
            p.UseBreakoutEntry = True; p.UseEntryRangeFade = False; p.RiskPerTrade = 4000
        self.dirs = []

    def chop(self, i):
        o = self.o5
        if o["chop_er"] is not None and self.sessBars >= 60 and self.sess_er(i) <= o["chop_er"]: return True
        if o["chop_flip"] is not None:
            w = [d for d in self.dirs[-o["flip_win"]:] if d != 0]
            flips = sum(1 for a, b in zip(w, w[1:]) if a != b)
            if flips >= o["chop_flip"]: return True
        return False

    def evaluate(self, i):
        sc = self.sc
        self.dirs.append(sc.R7Dir() if getattr(sc, "ready", False) else 0)
        out = super().evaluate(i); o = self.o5
        ch = self.chop(i); res = []
        for c in out:
            if c["path"] in ("T51", "T54"):
                if ch and o["chop_mode"] == "pull" and abs(sc.r7) != 3: continue
                if ch and o["chop_mode"] == "none": continue
                if o["ext_atr"] is not None:
                    a = self.ts.atr[i]; cl = self.b.c[i]
                    sw = self.ts.swL[i] if c["d"] > 0 else self.ts.swH[i]
                    dist = max(abs(cl - sw), self.tp.MinStopAtr * a)
                    if a > 0 and dist > o["ext_atr"] * a: continue
            elif c["path"] == "B1" and ch and o["chop_mode"] == "none": continue
            res.append(c)
        return res
