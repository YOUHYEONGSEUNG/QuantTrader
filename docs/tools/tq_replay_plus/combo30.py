# 점수 속도·가속도 실험 (10/23 오후)
#   vbars      속도 계산 봉 수 (지금 6). 짧을수록 '지금 움직임'에 가까운 속도
#   acc_cont   ("ease", a)  추세 지속 진입: 속도 < 0.2이거나, 속도가 줄고 있으면(가속도 ≤ −a, 진입 방향 기준) 진입
#              ("need", a)  추세 지속 진입: 속도 < 0.2이고 가속도 ≤ a일 때만
#   acc_pull   a            눌림·T2 진입: 진입 반대 방향 가속도가 a 넘으면(d·가속도 < −a) 진입 안 함
#   가속도 = 지금 속도 − 3봉 전 속도
import math
import score
from combo19 import Combo20

class Combo30(Combo20):
    def __init__(self, b, vbars=6, acc_cont=None, acc_pull=None, **kw):
        self.o30 = dict(vb=vbars, ac=acc_cont, ap=acc_pull); self.vhist = []
        score.Score.VBARS = vbars
        try: super().__init__(b, **kw)
        finally: pass
    def run(self):
        score.Score.VBARS = self.o30["vb"]
        try: return super().run()
        finally: score.Score.VBARS = 6
    def update_session(self, i):
        super().update_session(i)
        self.vhist.append(self.sc.V if getattr(self.sc, "ready", False) else 0.0)
    def accel(self):
        return self.vhist[-1] - self.vhist[-4] if len(self.vhist) >= 4 else 0.0
    def evaluate(self, i):
        o = self.o30; sc = self.sc
        if o["ac"] and o["ac"][0] == "ease":
            keep = self.o8["vmax"]
            r7 = sc.r7 if getattr(sc, "ready", False) else 0; d = 1 if r7 > 0 else -1 if r7 < 0 else 0
            if d and d * self.accel() <= -o["ac"][1]: self.o8["vmax"] = None   # 속도가 줄고 있으면 속도 조건 없이
            try: out = super().evaluate(i)
            finally: self.o8["vmax"] = keep
        else:
            before = self.usedRun
            out = super().evaluate(i)
            if o["ac"] and o["ac"][0] == "need" and out and out[0]["path"] in ("T51", "T54") and (not before) and self.usedRun:
                if out[0]["d"] * self.accel() > o["ac"][1]: self.usedRun = False; return []
        if o["ap"] is not None and out and out[0]["path"] in ("T51", "T54"):
            if out[0]["d"] * self.accel() < -o["ap"]: return []
        return out
