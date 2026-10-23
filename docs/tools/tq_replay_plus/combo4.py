# 강한 돌파 SB: 직전 봉 마감에 조건이 맞으면 15봉 고가(매도 저가) 1틱 밖에 스탑 진입 → 닿는 순간 체결(틱 기준).
# 손절 = 진입가 ∓ sb_stop (pt 또는 ATR 배수), 익절 = 진입가 ± tp1·tp2 × ATR, 1차 뒤 손절 평단·트레일링
import math
from combo3 import Combo3
from strat import Strat, rt
from ind import TICK


class Combo4(Combo3):
    def __init__(self, b, sb=False, sb_state=(1, 2), sb_f=0.9, sb_v=0.25, sb_er=0.25, sb_near=1.0, sb_stop=("pt", 2.0), sb_tp=(1.0, 3.0), sb_end=230000, **kw):
        self.sbo = dict(on=sb, state=sb_state, f=sb_f, v=sb_v, er=sb_er, near=sb_near, stop=sb_stop, tp=sb_tp, end=sb_end)
        super().__init__(b, **kw)

    def evaluate(self, i):
        out = super().evaluate(i); o = self.sbo; sc = self.sc
        if not o["on"] or not getattr(sc, "ready", False) or not self.inwin(i): return out
        if self.zone(i) in ("Out", "News", "OpenBlock") or self.T(i) >= o["end"]: return out
        if o["er"] < 1 and self.box_er(i) > o["er"]: return out
        a = self.sg.atr[i]; r7 = sc.r7; d7 = 1 if r7 > 0 else -1 if r7 < 0 else 0
        for d in (1, -1):
            if d7 != d or abs(r7) not in o["state"] or sc.r7Warn == -d: continue
            if d * sc.F < o["f"] or d * sc.V < o["v"]: continue
            lvl = self.rHi[i] if d > 0 else self.rLo[i]
            if d * (lvl - self.b.c[i]) > o["near"] * a or d * (lvl - self.b.c[i]) < 0: continue
            out.append(dict(path="SB", d=d, q=100, fb=False, rr=math.nan, stop=rt(lvl + d * TICK)))
        return out

    def calc_stop(self, px):
        if self.path == "SB":
            k, v = self.sbo["stop"]; dist = v if k == "pt" else v * self.stopAtr
            return rt(px - self.dir * max(dist, TICK))
        return super().calc_stop(px)

    def on_entry_fill(self, i, price, qty, tick_px):
        super().on_entry_fill(i, price, qty, tick_px)
        if self.path == "SB" and self.posQty > 0:
            a = self.avg(); t1, t2 = self.sbo["tp"]
            for c in self.chunks.values():
                c.fixed = a + self.dir * (t1 if c.stage == 1 else t2) * self.stopAtr if c.stage in (1, 2) else math.nan

    def protect(self, i):
        if self.path == "SB": return   # 손절이 진입가 바로 옆이라 이익 보호 본절은 쓰지 않음 (1차 뒤 평단·트레일링)
        return super().protect(i)

    # SB 청산 (C#과 같게 단순하게): 손절·1차·2차 지정가, 1차 뒤 평단 + 3 ATR 트레일링, 2차 뒤 2.5 ATR,
    # 청산용 판정이 끝(그 방향이었던 적 있을 때)·반대면 전량, 전환의심 50%(warn_check 공통). 의심·기준선·T2 청산 없음
    def trend_exits(self, i):
        if self.path != "SB": return super().trend_exits(i)
        xs = self.sc.ExitState()
        if xs == self.dir: self.sawOwn = True
        if xs == -self.dir: self.flatten(i, "OPP_TREND"); return
        if xs == 0 and self.sawOwn: self.flatten(i, "TREND_END")

    def after_tp1(self, i, tp1, cur_px):
        if self.path != "SB": return super().after_tp1(i, tp1, cur_px)
        if self.fillQty > 0: self.move_stop(i, self.avg(), "평단", cur_px)

    def warn_check(self, i):
        if self.path == "SB": return Strat.warn_check(self, i)
        return super().warn_check(i)

    # 최소 진입 강제 채우기 (개선): 남은 봉 수로 시작 시각을 거꾸로 정함. 필요한 횟수 × 2봉(진입·청산) + 보유 중이면 정리 1봉 + 여유 force_margin봉.
    # 보유 포지션은 이미 1회로 세졌으므로 최대한 들고 감. force_mode="fixed"면 예전 방식(23:20부터)
    force_mode = "late"; force_margin = 2
    def force_needed(self, i):
        p = self.p
        if not p.ForceMinEntries or self.entryCount >= p.MinEntries: return False
        now, end = self.rel(self.T(i)), self.rel(p.FlattenTime)
        if now >= end: return False
        if self.force_mode == "fixed": return now >= self.rel(p.ForceStartTime)
        need = p.MinEntries - self.entryCount; hold = 1 if self.posQty > 0 and self.path != "FORCE" else 0
        return end - now <= 2 * need + hold + self.force_margin
