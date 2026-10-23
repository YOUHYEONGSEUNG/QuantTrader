# 조합 재현기: v6 재현기(strat.Strat, Playback과 판단 일치) 위에 트레이더 전략(score-v1-logic) 진입·청산을 경로 T51·T54·T55·T56으로 붙임.
# 묶음 4개를 고른다:
#   reg    "team" = 팀 10분봉 점수 방식으로 트레이더 진입을 고름 / "v6" = v6 7레짐(추세·눌림 방향)
#   alpha  False = 트레이더 진입만 / True = + v6 돌파 경로 E1·E3·B1·E5
#   exits  "trader" = 트레이더 밴드 익절·손절(전저점·ATR 먼 쪽)·본절 / "v6" = v6 R 익절·트레일링·이익 보호 본절·청산용 판정
#   size   "fixed" = 30계약 고정, 시간 제한 없음 / "v6" = 위험 금액 수량·줄이는 이유·지표 발표·개장 전후 진입 금지
import math
from strat import Strat, P as SP, rt, PV, hhmmss
from ind import TICK
from team import Sig as TSig, Regime as TRegime, to_n, P as TP

TRADER = ("T51", "T54", "T55", "T56")


class Combo(Strat):
    def __init__(self, b, reg="team", alpha=False, exits="trader", size="fixed", tp=None, exit_reg=None, tpaths=None, **kw):
        self.tpaths = tpaths; self.timeblk = kw.pop("timeblk", None)
        kw_exit_reg = exit_reg
        self.reg, self.alpha, self.exits_mode, self.size = reg, alpha, exits, size
        p = SP(UseEntryTrendStart=alpha, UseEntryOrb=alpha, E3StopEntry=alpha, UseEntryOpenRange=alpha, UseBreakoutEntry=alpha,
               UseEntryPullback=kw.pop("v6E2", False), UseEntrySide=kw.pop("v6E4", False), UseEntryRangeFade=kw.pop("v6E6", False), ForceMinEntries=kw.pop("force", False), UseAddOn=(exits == "v6"), **kw)
        if size == "fixed":   # 모든 진입 30계약 고정 (트레이더 전략처럼): 위험 금액·줄이는 이유 없음
            p.RiskPerTrade = 1e9; p.QualityHalfBelow = -9; p.ExtendedEntryAtr = 0; p.DayLossLimitR = 0; p.PreOpenQtyPercent = 100
        super().__init__(b, p)
        self.tp = tp or TP(); self.ts = TSig(b, self.tp)
        b10 = to_n(b, 10); rg = TRegime(b10, self.tp); rgm = TRegime(b10, TP(RegimeMode=1)); j = -1
        from ind import sma as _sma
        s18 = _sma(b10.c, 18); s36 = _sma(b10.c, 36)
        self.r10 = []; self.rma = []; self.rslope = []
        for i in range(b.n):
            while j + 1 < b10.n and b10.t[j + 1] <= b.t[i]: j += 1
            self.r10.append(rg.r[j] if j >= 0 else math.nan); self.rma.append(rgm.r[j] if j >= 0 else math.nan)
            if j >= 40:
                d18 = s18[j] - s18[j - 3]; d36 = s36[j] - s36[j - 3]
                self.rslope.append(1 if d18 > 0 and d36 > 0 else -1 if d18 < 0 and d36 < 0 else 0)
            else: self.rslope.append(math.nan)
        self.exit_reg = kw_exit_reg
        self.tm = None   # 트레이더 거래 상태 (경로가 T…이고 exits == trader일 때)

    def trader_trade(self): return self.path in TRADER and self.exits_mode == "trader"

    # ── 진입 후보 ──
    def evaluate(self, i):
        out = super().evaluate(i) if self.alpha else []
        p = self.p; ts = self.ts; z = self.zone(i)
        tb = self.size == "v6" if getattr(self, "timeblk", None) is None else self.timeblk
        if tb:
            if z in ("Out", "News", "OpenBlock"): return out
            if self.rel(self.T(i)) >= self.rel(p.NormalEntryEndTime) and not (z == "Post" and self.entryCount < p.MinEntries): return out
        r = self.reg_at(i)
        if r != r: return out
        r = int(r)
        if r == 1 and (ts.eUpLong[i] or ts.t2Bull[i]): out.append(dict(path="T51", d=1, q=100, fb=False, rr=math.nan))
        elif r == -1 and (ts.eDnShort[i] or ts.t2Bear[i]): out.append(dict(path="T54", d=-1, q=100, fb=False, rr=math.nan))
        elif r == 0:
            if not self.blockSideLong and ts.eSideLong[i]: out.append(dict(path="T55", d=1, q=100, fb=False, rr=math.nan))
            elif not self.blockSideShort and ts.eSideShort[i]: out.append(dict(path="T56", d=-1, q=100, fb=False, rr=math.nan))
        if self.tpaths is not None: out = [c for c in out if c["path"] not in TRADER or c["path"] in self.tpaths]
        return out

    RANK = dict(Strat.RANK, T51=6, T54=6, T55=7, T56=7, SB=1)

    def lab(self, name, i):
        v6 = self.sc.RegimeState() if getattr(self.sc, "ready", False) else 0
        if name == "none": return float("nan")
        if name == "team": return self.r10[i]
        if name == "ma": return self.rma[i]
        if name == "slope": return self.rslope[i]
        if name == "v6": return v6
        if name in ("v6+ma", "v6+slope"):
            slow = self.rma[i] if name == "v6+ma" else self.rslope[i]
            if slow != slow: return slow
            return v6 if v6 == slow else 0
        raise ValueError(name)

    def reg_at(self, i): return self.lab(self.reg, i)

    def _with_exit_reg(self, i, fn):
        if not self.exit_reg or self.trader_trade(): return fn(i)
        v = self.lab(self.exit_reg, i); v = 0 if v != v else int(v)
        keep = self.sc.ExitState; self.sc.ExitState = lambda: v
        try: return fn(i)
        finally: self.sc.ExitState = keep

    def route(self, i, cands):
        tcs = [c for c in cands if c["path"] in TRADER]
        others = [c for c in cands if c["path"] not in TRADER]
        ready_alpha = [c for c in others if "stop" not in c]
        if ready_alpha or not tcs:
            return super().route(i, others)
        c = tcs[0]
        self.e3 = None
        pos = ("SL" if c["d"] > 0 else "SS") if c["path"] in ("T55", "T56") else ("TL" if c["d"] > 0 else "TS")
        self.enter(i, c["d"], c["path"], 100, False, math.nan, pos=pos)

    # ── 손절: 트레이더 = 전저점·전고점과 ATR×1 중 먼 쪽 ──
    def calc_stop(self, px):
        if not self.trader_trade(): return super().calc_stop(px)
        sw, a, L = self.stopSwing, self.stopAtr, self.dir > 0
        st = sw
        k = self.tp.MinStopAtr
        if a > 0 and abs(px - st) < k * a: st = px - k * a if L else px + k * a
        return rt(st)

    def band(self, i, d, n):
        return super().band(i, d, n)

    def tband(self, i, sig):
        B = self.ts.band
        m = {"UpL1": 2, "UpL2": 3, "DnS1": -2, "DnS2": -3, "SideL1": 2, "SideS1": -2}
        return B[m[sig]][i] if sig in m else math.nan

    def enter(self, i, d, path, qpct, fb, rr, pos=None, stop_px=None):
        if path not in TRADER: return super().enter(i, d, path, qpct, fb, rr, pos=pos, stop_px=stop_px)
        if self.exits_mode == "v6" and self.size == "v6": return super().enter(i, d, path, qpct, fb, rr, pos=pos)
        if self.exits_mode == "v6": return super().enter(i, d, path, 100, fb, rr, pos=pos)
        # 트레이더 청산: 조각 계획과 밴드 익절
        self.reset_trade(); self.pos = pos; self.dir = d; self.path = path; self.fallback = fb
        self.stopAtr = self.ts.atr[i]; self.stopSwing = self.ts.swL[i] if d > 0 else self.ts.swH[i]; self.early = False
        self.initialStop = self.calc_stop(self.b.c[i]); self.stopPrice = self.initialStop
        q = self.trader_qty(i)
        self.baseQty = q
        plan = {"T51": [("UpL1", 1, 5), ("UpL2", 2, 5), ("UpL3", 0, 0)], "T54": [("DnS1", 1, 3), ("DnS2", 1, 3), ("DnS3", 0, 0)],
                "T55": [("SideL1", 1, 2), ("SideL2", 0, 0)], "T56": [("SideS1", 1, 2), ("SideS2", 0, 0)]}[path]
        used = 0; self.signalSeq += 1; self.tm = dict(tp1=False, tp2=False, strong=False, side=None, bePending=False, reached=False, risk0=math.nan)
        for k, (sig, n_, d_) in enumerate(plan):
            qq = q * n_ // d_ if d_ else q - used; used += qq
            self.add_chunk(sig, qq, k + 1 if k < 2 else 3, self.tband(i, sig))
        for c in self.chunks.values(): c.stop = self.stopPrice
        self.tr = dict(t0=self.b.t[i], i0=i, path=path, d=d, qty=q, sig_close=self.b.c[i], stop0=math.nan, pnl=0.0, mfe=0.0, mae=0.0, bars=0, exits=[], reason="", stop_entry=None)
        self.trOpen = True; self.pending.append(("entry", None, q))
        return True

    def trader_qty(self, i):
        q = 30
        if self.size == "v6":
            dist = abs(self.b.c[i] - self.initialStop)
            if dist > 0: q = max(1, min(q, int(self.p.RiskPerTrade / (dist * PV))))
            mult = self.p.PreOpenQtyPercent / 100.0 if self.zone(i) == "Pre" else 1.0
            if self.p.DayLossLimitR > 0 and self.sessPnl <= -self.p.DayLossLimitR * self.p.RiskPerTrade: mult *= 0.5
            if mult < 1: q = max(1, int(q * max(0.25, mult)))
        return q

    def on_entry_fill(self, i, price, qty, tick_px):
        if not self.trader_trade(): return super().on_entry_fill(i, price, qty, tick_px)
        for c in self.chunks.values():
            if c.left == 0: c.left = c.qty
        if self.posQty == 0: self.entryCount += 1
        self.posQty += qty; self.fillSum += price * qty; self.fillQty += qty; self.tr["avg"] = self.avg()
        if self.dir * (price - self.initialStop) <= 0: self.gap = True; self.flatten(i, "GAP"); return
        s = self.calc_stop(self.avg())
        if (tick_px <= s) if self.dir > 0 else (tick_px >= s): self.gap = True; self.flatten(i, "GAP"); return
        self.stopPrice = s; self.tr["stop0"] = s; self.tm["risk0"] = abs(self.avg() - s)
        for c in self.chunks.values(): c.stop = s

    def on_target_fill(self, i, c, price, cur_px):
        if not self.trader_trade(): return super().on_target_fill(i, c, price, cur_px)
        if c.filled_stage: return
        c.filled_stage = True
        if c.sig == "DnS2": self.tm["tp2"] = True; return
        self.tm["tp1"] = True
        if c.sig == "UpL2": self.tm["strong"] = True
        if c.sig in ("SideL1", "SideS1"): self.tm["side"] = "A"
        self.first_tp(i, price)

    def first_tp(self, i, px):
        self.tm["bePending"] = False; self.tm["risk0"] = math.nan
        if self.beDone or not self.live: return
        be = rt(self.avg())
        if not ((px > be) if self.dir > 0 else (px < be)): return
        mr = getattr(self, "BE_MIN_R", 0.0)
        if mr > 0 and not math.isnan(self.tr.get("stop0", math.nan)):
            risk = abs(self.avg() - self.tr["stop0"])
            if risk > 0 and self.dir * (px - self.avg()) < mr * risk:
                fb = getattr(self, "BE_FALLBACK_ATR", 0.0)
                if fb > 0: self.move_stop(i, px - self.dir * fb * self.ts.atr[i], "1차 뒤 ATR", px)
                return   # 1차 이익이 작으면 평단으로 옮기지 않음 (v6 BreakevenMinR)
        self.move_stop(i, be, "평단", px)

    def exit_fill(self, i, sig, px, q, kind):
        super().exit_fill(i, sig, px, q, kind)
        if self.tm and kind == "x" and self.tm.get("bePending") and self.posQty > 0: self.first_tp(i, px)

    def on_stop_fill(self, i):
        if not self.trader_trade(): return super().on_stop_fill(i)
        self.exitReason = "STOP" if not self.beDone else "BE_STOP"
        if self.beDone or self.path in ("T51", "T54"): return
        if self.dir * (self.stopPrice - self.avg()) < 0:
            if self.dir > 0: self.blockSideLong = True
            else: self.blockSideShort = True

    # ── 보유 중 봉 마감: 트레이더 청산 ──
    def protect(self, i):
        if not self.trader_trade(): return super().protect(i)
        tm = self.tm
        if self.path in ("T51", "T54") or self.beDone or math.isnan(tm["risk0"]) or self.fillQty <= 0: return
        fav = (self.b.h[i] - self.avg()) if self.dir > 0 else (self.avg() - self.b.l[i])
        if fav >= tm["risk0"]: tm["reached"] = True
        if tm["reached"]: self.move_stop(i, rt(self.avg()), "이익 보호", self.b.c[i])

    def trail(self, i):
        if not self.trader_trade(): return super().trail(i)
        for s in self.live: self.chunks[s].fixed = self.tband(i, s)

    def warn_check(self, i):
        if not self.trader_trade(): return super().warn_check(i)

    def add_on(self, i):
        if not self.trader_trade(): return super().add_on(i)

    def mbe(self):
        if not self.beDone: self.tm["bePending"] = True

    def trend_exits(self, i):
        if not self.trader_trade(): return self._with_exit_reg(i, super().trend_exits)
        ts, c, tm = self.ts, self.b.c[i], self.tm
        if self.path == "T51":
            if tm["strong"]:
                if c < ts.sma[i]: self.flatten(i, "STRONG_SMA")
                return
            up3, up2, weak = ts.xUp[3][i], ts.xUp[2][i], ts.t2Bear[i]
            if up3:
                self.mbe()
                if not tm["tp1"]: self.exit_chunk(i, "UpL1", "TP1")
                self.exit_chunk(i, "UpL2", "TP2"); tm["tp1"] = True; tm["strong"] = True; return
            if not tm["tp1"]:
                if weak: self.flatten(i, "WEAK"); return
                if up2: self.mbe(); self.exit_chunk(i, "UpL1", "TP1"); tm["tp1"] = True
            elif weak: self.flatten(i, "WEAK")
        else:
            final = self.final_check(i, c); t1 = c <= ts.band[-2][i] or ts.mUp[i] or ts.k20[i]; t2 = c <= ts.band[-3][i]
            if final: self.flatten(i, "FINAL"); return
            if not tm["tp1"] and t1: self.mbe(); self.exit_chunk(i, "DnS1", "TP1"); tm["tp1"] = True
            if t2 and not tm["tp2"]: self.exit_chunk(i, "DnS2", "TP2"); tm["tp2"] = True

    FINAL_MODE = "base"
    def final_check(self, i, c):
        ts, tm = self.ts, self.tm; above = c > ts.sma[i]; m = self.FINAL_MODE
        if m == "base": return above
        if m == "after_tp1": return above and tm["tp1"]
        if m == "atr": return c > ts.sma[i] + 0.5 * ts.atr[i]
        if m == "two": return above and self.b.c[i - 1] > ts.sma[i - 1]
        if m == "delay3": return above and self.tr.get("bars", 0) > 3
        if m == "atr_tp1": return above and (tm["tp1"] or c > ts.sma[i] + 0.5 * ts.atr[i])
        raise ValueError(m)

    def side_exits(self, i):
        if not self.trader_trade(): return self._with_exit_reg(i, super().side_exits)
        ts, b, tm = self.ts, self.b, self.tm; d = self.dir; c = b.c[i]
        if tm["side"] is None:
            aT = (c >= ts.band[2][i] or ts.mDn[i] or ts.dead[i]) if d > 0 else (c <= ts.band[-2][i] or ts.mUp[i] or ts.gold[i])
            bT = ts.k80[i] if d > 0 else ts.k20[i]
            aR = b.h[i] >= ts.band[3][i] if d > 0 else b.l[i] <= ts.band[-3][i]
            bR = ts.t2Bear[i] if d > 0 else ts.t2Bull[i]
            if aR or bR: self.flatten(i, "SIDE_REST"); return
            if aT or bT:
                tm["side"] = "A" if aT else "B"; self.mbe(); self.exit_chunk(i, "SideL1" if d > 0 else "SideS1", "TP1"); tm["tp1"] = True
        elif tm["side"] == "A":
            if (b.h[i] >= ts.band[3][i]) if d > 0 else (b.l[i] <= ts.band[-3][i]): self.flatten(i, "SIDE_A")
        else:
            if (ts.t2Bear[i] if d > 0 else ts.t2Bull[i]): self.flatten(i, "SIDE_B")

    def on_bar_close(self, i):
        # 트레이더 거래는 v6의 종가 기준 손절 안전장치를 쓰지 않음 (트레이더 규칙에 없음)
        if self.trader_trade() and self.posQty > 0 and not self.flat_time(i):
            keep = self.stopPrice; self.stopPrice = self.stopPrice   # 그대로
        super().on_bar_close(i)

    def reset_trade(self):
        super().reset_trade(); self.tm = None
