# 수량·불타기·이동 효율비 실험 (MNQ 단독, 대회 한도 마이크로 80)
import math
from combo10 import Combo10

class Combo11(Combo10):
    def __init__(self, b, qty=30, cap=4000, add_q=0, add_when="tp1", add_v=None, add_end="23:10", max_q=80,
                 rer_n=None, rer_thr=None, rer_mode="block", rer_qty=None, **kw):
        self.o11 = dict(qty=qty, cap=cap, add_q=add_q, add_when=add_when, add_v=add_v, add_end=add_end, max_q=max_q,
                        rer_n=rer_n, rer_thr=rer_thr, rer_mode=rer_mode, rer_qty=rer_qty)
        super().__init__(b, **kw)
        n = rer_n or 30; c = b.c; self.rer = [math.nan] * b.n
        path = 0.0
        for i in range(1, b.n):
            path += abs(c[i] - c[i - 1])
            if i > n: path -= abs(c[i - n] - c[i - n - 1])
            if i >= n and path > 0: self.rer[i] = abs(c[i] - c[i - n]) / path

    def trader_qty(self, i):
        o = self.o11; q = o["qty"]
        if o["rer_qty"] is not None and not math.isnan(self.rer[i]):
            lo, hi, thr = o["rer_qty"]; q = hi if self.rer[i] >= thr else lo
        dist = abs(self.b.c[i] - self.initialStop)
        if o["cap"] and dist > 0: q = max(1, min(q, int(o["cap"] / (dist * 2))))
        return max(1, min(q, o["max_q"]))

    def evaluate(self, i):
        out = super().evaluate(i); o = self.o11
        if out and o["rer_n"] and o["rer_thr"] is not None and out[0]["path"] in ("T51", "T54") and not math.isnan(self.rer[i]) and self.rer[i] < o["rer_thr"]:
            if o["rer_mode"] == "block": return []
            if o["rer_mode"] == "pull" and abs(self.sc.r7) != 3: return []
        return out

    def add_on(self, i):
        o = self.o11
        if not self.trader_trade(): return super().add_on(i)
        if o["add_q"] <= 0 or self.path not in ("T51", "T54") or self.addSig or self.posQty <= 0 or self.fillQty <= 0: return
        tm = self.tm
        if o["add_when"] == "tp1" and not tm["tp1"]: return
        if o["add_when"] == "tp2" and not (tm["tp2"] or tm["strong"]): return
        if self.b.t[i][11:16] >= o["add_end"]: return
        sc = self.sc
        if not getattr(sc, "ready", False) or sc.r7 * self.dir != 2: return
        if o["add_v"] is not None and self.dir * sc.V < o["add_v"]: return
        q = min(o["add_q"], o["max_q"] - self.posQty)
        if q <= 0: return
        # 손절은 평단(아니면 그대로 유리한 쪽): 추가분까지 이익 보호
        self.addSig = f"ADD{self.signalSeq}"
        self.add_chunk(self.addSig, q, 3, math.nan); self.chunks[self.addSig].stop = self.stopPrice
        self.pending.append(("add", self.addSig, q)); self.tr["addq"] = self.tr.get("addq", 0) + q

class Combo11H(Combo11):
    """그날 실현 손익이 −halt 이하면 신규 진입 안 함 (강제 채우기는 그대로)"""
    def __init__(self, b, halt=0, **kw):
        self.halt = halt; super().__init__(b, **kw)
    def evaluate(self, i):
        out = super().evaluate(i)
        if self.halt and self.sessPnl <= -self.halt: return []
        return out
