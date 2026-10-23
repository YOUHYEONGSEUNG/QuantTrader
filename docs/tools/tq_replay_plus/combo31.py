# 점수 C를 묶음 평균으로 만드는 실험 (10/23 오후): cmode = (가중치 3개, 배율) 또는 None(지금)
import score
from combo19 import Combo20

class Combo31(Combo20):
    def __init__(self, b, cmode=None, **kw):
        self.cmode = cmode
        self._set(); 
        try: super().__init__(b, **kw)
        finally: self._reset()
    def _set(self):
        if self.cmode: score.Score.CMODE = "group"; score.Score.GW = tuple(self.cmode[0]); score.Score.GSCALE = self.cmode[1]
    def _reset(self):
        score.Score.CMODE = "equal"; score.Score.GW = (1.0, 1.0, 1.0); score.Score.GSCALE = 1.0
    def run(self):
        self._set()
        try: return super().run()
        finally: self._reset()
