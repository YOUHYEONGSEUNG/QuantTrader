# 추세 품질 Q를 진입에 엮기 + 짧은 익절 반대 진입 (10/23 밤 10)
#   Q = 다섯 값(중앙값으로 나눔)의 기하평균 = (a·b·c·d·e)^(1/5), 각 값은 0.05~3으로 자름
#     a 3분 SMA20 5봉 기울기(진입 방향, 3분 ATR) ÷ 0.4   b 느린 점수 S(진입 방향) ÷ 1.9   c 3분 ATR ÷ 최근 20봉 평균
#     d 7레짐 같은 방향 '추세' 봉 수 ÷ 20               e 최근 3분봉 5개 고점·저점이 진입 방향으로 간 횟수 ÷ 6
#   cont_q θ   추세 지속 진입은 Q ≥ θ일 때만
#   pull_q θ   눌림(7레짐 ±3) 진입은 '눌리기 직전 추세'의 Q ≥ θ일 때만 (추세였던 마지막 봉의 Q)
#   trend_q θ  7레짐 '추세'(±2)라도 Q < θ면 진입에서는 추세로 안 봄 (눌림 진입은 pull_q 방식으로 직전 추세 Q)
#   rev_tp (k, n) 반대 진입(combo25 rev)의 앞 n조각을 진입가 ± k × 손절 거리에 익절, 그 체결 뒤 손절은 무조건 평단, 마지막 조각은 기존 청산
import math
import combo
from combo26 import Combo26

def _clip(x): return min(3.0, max(0.05, x))

class Combo27(Combo26):
    def __init__(self, b, cont_q=None, pull_q=None, trend_q=None, rev_tp=None, **kw):
        self.o27 = dict(cq=cont_q, pq=pull_q, tq=trend_q, rtp=rev_tp); self.qLast = {1: math.nan, -1: math.nan}; self.revScalp = False; self.scalpPx = math.nan
        super().__init__(b, **kw)

    def quality(self, i, d):
        sc, rg, b3 = self.sc, self.rg, self.b3; j = sc.j[i]; a3 = rg.atr[j]
        if not getattr(sc, "ready", False) or a3 <= 0 or j < 25: return math.nan
        a = _clip(d * (rg.sm[j] - rg.sm[j - 5]) / a3 / 0.4)
        bb = _clip(d * sc.S / 1.9)
        c = _clip(a3 / (sum(rg.atr[j - 20:j]) / 20))
        dd = _clip((self.runLen if self.runDir == d else 0) / 20.0)
        hh = sum(1 for k in range(5) if (b3.h[j - k] > b3.h[j - k - 1]) == (d > 0)) + sum(1 for k in range(5) if (b3.l[j - k] > b3.l[j - k - 1]) == (d > 0))
        e = _clip(hh / 6.0)
        return (a * bb * c * dd * e) ** 0.2

    def update_session(self, i):
        super().update_session(i)
        sc = self.sc
        if getattr(sc, "ready", False) and abs(sc.r7) == 2:
            d = 1 if sc.r7 > 0 else -1; self.qLast[d] = self.quality(i, d)

    def evaluate(self, i):
        before = self.usedRun
        out = super().evaluate(i); o = self.o27
        if not out or out[0]["path"] not in ("T51", "T54") or out[0].get("rev"): return out
        x = out[0]; d = x["d"]; r7 = self.sc.r7 if getattr(self.sc, "ready", False) else 0
        isCont = (not before) and self.usedRun
        qNow = self.quality(i, d); qPrev = self.qLast.get(d, math.nan)
        def ok(q, th): return th is None or (not math.isnan(q) and q >= th)
        if isCont and not ok(qNow, o["cq"]): return []
        if abs(r7) == 3 and not ok(qPrev, o["pq"]): return []
        if o["tq"] is not None:
            q = qNow if abs(r7) == 2 else qPrev
            if not ok(q, o["tq"]): return []
        return out

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        r = super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        self.revScalp = False
        rt = self.o27["rtp"]
        if rt and self.tr is not None and self.tr.get("i0") == i and self.tr.get("rev") and path in ("T51", "T54"):
            k, n = rt; R = abs(self.b.c[i] - self.initialStop)
            self.revScalp = True; self.scalpPx = self.b.c[i] + d * k * R
            sigs = ["UpL1", "UpL2"] if d > 0 else ["DnS1", "DnS2"]
            for s in sigs[:n]:
                if s in self.chunks: self.chunks[s].fixed = self.scalpPx
        return r

    def tband(self, i, sig):
        if self.revScalp and sig in (["UpL1", "UpL2"] if self.dir > 0 else ["DnS1", "DnS2"])[: self.o27["rtp"][1]]: return self.scalpPx
        return super().tband(i, sig)

    def first_tp(self, i, px):
        if self.revScalp:   # 짧은 익절이 체결되면 손절은 무조건 평단 (이익 크기 조건 없음)
            keep = combo.Combo.BE_MIN_R; combo.Combo.BE_MIN_R = 0.0
            try: return combo.Combo.first_tp(self, i, px)
            finally: combo.Combo.BE_MIN_R = keep
        return super().first_tp(i, px)

    def reset_trade(self):
        super().reset_trade(); self.revScalp = False
