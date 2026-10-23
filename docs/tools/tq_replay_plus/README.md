# 재현기 (`tq_replay_plus`)

`Strategies/TeamQuant/TQ_Strategy.cs`(브랜치 `test/score-v1-plus`)의 판단과 주문 흐름을 Python으로 옮긴 1분봉 백테스트다. NinjaTrader 없이 몇 초 만에 45일을 돌려서, 설정을 여러 개 비교할 때 쓴다. 10/22~10/23 `docs/changelog.md`의 '재현기' 숫자는 모두 이것으로 냈다.

**NinjaTrader 결과를 대신하지 않는다.** 체결을 1분봉 시가·고가·저가·종가 네 점으로만 따라가서, 틱 흔들림·미끄러짐·부분 체결은 없다. 수수료도 없다. 본선 제출용 백테스트는 NinjaTrader Strategy Analyzer로 낸다.

## 실행

Python 3, 표준 라이브러리만 쓴다. 이 폴더에서:

```
py -X utf8 replay.py                 # 지금 기본값과 비교 기준(10/23 오전 30계약, 지속 진입 끔), 구간별 손익
py -X utf8 replay.py days            # 날짜별 손익(10/23 오전 30계약 vs 지금 기본값)과 그날 대회 구간 움직임
py -X utf8 replay.py trades 2026-09-21,2026-09-23   # 그날 거래 내역(진입·청산·최대 유리/불리)
py -X utf8 replay.py exits           # 청산 늦추기 180가지 (10/23 시험, 몇 분 걸림)
py -X utf8 replay.py cont            # 추세 지속 진입 봉 수·조건 (10/23 시험)
```

`-X utf8`이 없으면 Windows 콘솔에서 한글이 깨진다. 다른 설정을 보려면 `common.py`의 `trades(...)`에 인자를 넘긴다(예: `trades(cont_k=None, chop_er=None)`).

## 데이터 (`data/`)

MNQ 1분봉, 시각은 KST, `[시각, 시가, 고가, 저가, 종가]` 목록. NinjaTrader에서 내보낸 1분봉과 전략 로그의 `시고저종=`를 합쳐 만들었다.

| 파일 | 구간 | 월물 |
| --- | --- | --- |
| `bars_sep.json` | 7/17~7/31 | MNQ 09-26 |
| `bars_sepm.json` | 9/1~9/17 | MNQ 09-26 |
| `bars_oos.json` | 9/21~10/2 | MNQ 12-26 |
| `bars_oct_nt.json` | 10/5~10/21 | MNQ 12-26 |
| `bars_aug.json` | 7/16~9/18 (검증에는 8/4~8/31 13일만, 나머지는 위와 겹침) | MNQ 09-26, 10/23 받음. 설정 고를 때 안 쓴 날 |

각 파일은 첫날 며칠 앞부터 들어 있어 지표가 채워진 뒤 시작한다. **45일 모두 지금까지 설정을 고르는 데 썼으므로 독립 검증용이 아니다.** 새 구간(예: 5/1~7/16)을 넣으려면 같은 형식의 파일을 만들어 `common.py`의 `SETS`에 추가한다. 골드(MGC)는 계약당 가치·틱이 달라(`strat.PV`, `ind.TICK`이 MNQ 값) 그대로는 못 돌린다.

## 파일

| 파일 | 내용 |
| --- | --- |
| `ind.py` | 지표(EMA·SMA·ATR·MACD·RSI·스토캐스틱 등), 1분 → 3분·10분봉. NinjaTrader 내장 지표와 같은 식 |
| `score.py` | v6 연속 점수·7레짐·청산용 판정 |
| `strat.py` | v6-box 전략 재현기(주문 흐름·체결 모형). 아래 파일들의 바탕 |
| `team.py` | 팀 전략(score-v1-logic) 신호(`Sig`)·10분봉 레짐·설정값(`P`) |
| `combo.py` | v6 재현기 위에 팀 진입·청산(경로 T51·T54·T55·T56)을 붙임 |
| `combo2.py` | 청산 부품(손절·본절·트레일링·익절 방식·추세 변화 청산) |
| `combo3.py` | 7레짐으로 진입 고르기(추세·눌림), 횡보 진입, 전환의심, 수량 |
| `combo4.py` | 강한 돌파(꺼 둠), 최소 진입 강제 채우기 |
| `combo5.py` | 흔들리는 장이면 눌림만 (`ChopMaxEr`) |
| `combo6.py` | 청산 늦추기 실험 (넣지 않음, 10/23) |
| `combo7.py` | 추세 지속 진입 (`TrendContBars`) |
| `combo8.py` | 지속 진입 속도 조건(`TrendContMaxVel`), 2차 뒤 트레일링 폭·추세 나이·21:30 리셋 실험 |
| `combo9.py` | 레짐으로 본절·트레일링 늦추기 실험 (넣지 않음) |
| `combo10.py` | 매도 청산을 매수 구조로 실험 (넣지 않음) |
| `combo11.py` | 수량·위험 상한·불타기(`AddOnQty`)·이동 효율비·그날 진입 중단(`Combo11H`) |
| `combo12.py` | 진입 방식(스탑 돌파·지정가 되돌림, 봉 안 체결), 변동성 수량, 불타기 여유(`AddOnMinRoomAtr`) |
| `combo13.py` | 추세일 모드(재진입·반대 방향 막기·트레일링·본절) 실험 (넣지 않음) |
| `combo14.py` | 미실현 이익 기준 불타기 실험 (넣지 않음) |
| `combo15.py` | 러너 청산을 3분봉 기준으로(3분 ATR·직전 3분봉 저점·3분 SMA) 실험 (넣지 않음) |
| `combo16.py` | 진입·청산 충돌 고치기(SMA 반대쪽 진입 막기, 1차가 이미 지난 진입) 실험 (넣지 않음) |
| `combo17.py` | 21:45 전 수량 줄이기, 그날 이익 잠금 실험 (넣지 않음) |
| `combo18.py` | 5.4 매도 SMA20 충돌 보류·대기 진입(`DnShortSmaBlock`·`DnShortSmaWaitBars`), 1차 밴드를 이미 지난 진입의 1차 가운데(넣었다가 뺌) |
| `combo19.py` | `Combo19`: 매수 T2 충돌 보류·진입가 기준 1·2차(넣지 않음), 21:45 전 수량(`EarlyQty`). `Combo20`: 이미 지난 익절 조각은 사지 않음(`SkipPassedChunks`). 지금 기본값 클래스 |
| `combo21.py` | 흔들리는 장 판단(대회 시작 n봉 / 대회 전 포함 최근 w봉)·대응(눌림만·추세 진입 안 함·수량 절반·횡보 진입) 실험 (넣지 않음) |
| `combo22.py` | 7개 선 위의 최근 위치(최근 N봉 평균 (종가−중심선)/ATR)로 눌림 기준선을 ±1배·∓1배로 바꾸는 실험 (넣지 않음) |
| `combo23.py` | 범위가 넓은 흔들림 날 ±2·3배 되돌림(중심선 익절), 좁은 흔들림 날 수량 절반·추세 진입 안 함 실험 (넣지 않음) |
| `combo24.py` | 7레짐 반대 전환 청산, 30봉 극값에서 애매하게 물러난 자리 진입 안 함 실험 (넣지 않음) |
| `combo25.py` | 청산(손절·5.4 최종 등) 뒤 반대 방향 진입 실험 (넣지 않음) |
| `combo26.py` | 추세 품질(약한 신호 5개 수)로 수량·불타기 조절 실험 (넣지 않음) |
| `combo27.py` | 추세 품질 Q(다섯 값 기하평균)로 지속 진입·눌림 진입(직전 추세 Q)·7레짐 추세 인정 거르기, 짧은 익절 반대 진입 실험 (넣지 않음) |
| `tune_cv.py` | 촘촘한 값 × 45일 날짜별 손익 표(`grid`, `tune_grid.json`) → 주 단위 5겹 교차 검증 300번(`cv`): 학습 주에서 고른 값이 남긴 주에서 지금 값보다 나았는지·자주 고른 값 |
| `tune.py` | 설정값 고르기: 앞 22일(7월·9월 초)로 한 값씩 바꿔 짝지은 부트스트랩(2,000번)으로 고르고 `check`로 뒤 23일(9/21~10/2·10월)에서 확인 |
| `combo28.py` | 흔들리는 장 판단 예외(강한 점수 / 추세 시작부터 k ATR / 최근 w봉 효율비 / 점수 + 대회 구간 고저 돌파) 실험 (넣지 않음) |
| `combo29.py` | 흔들리는 장 판단 고도화: 지수 감쇠 효율비, Choppiness Index, 분산비, 범위 밖 받아들여짐, CI 해제, 셋 묶기(`Combo29V`) 실험 (넣지 않음) |
| `combo30.py` | 점수 속도 계산 봉 수(`score.Score.VBARS`, 기본 6), 가속도(속도 − 3봉 전 속도)로 지속 진입·눌림 진입 거르기 실험 (넣지 않음) |
| `combo31.py` | 점수 C를 묶음 평균으로(가격 위치·빠른 모멘텀·느린 구조, `score.Score.CMODE`) 실험 (넣지 않음) |
| `combo32.py` | 3분봉 길이가 늘고 주는 것(봉 가속도)으로 눌림·지속 진입 거르기 실험 (넣지 않음) |
| `combo33.py` | 하루 손실 막기(그날 −X달러·손실 k번이면 새 진입 안 함) 실험 (넣지 않음) |
| `combo34.py` | 매수에 매도의 SMA20 보류·청산을 거울로 넣기 실험 (넣지 않음) |
| `ladder.py` | 10/22 밤 설정에서 10/23에 바꾼 것을 하나씩 쌓아 가며 8월 13일·45일 비교 |
| `loo.py` | 지금 로직에서 하나씩 끄기 (8월 13일·45일) |
| `all58.py`, `all58b.py`, `all58c.py` | 58일(45 + 8월 13)로 기능·수량, 흔들림·하루 멈춤, 매수 SMA20 대칭 비교와 주 단위 교차 검증 (결과 `tune_all58*.json`) |
| `all_hours.py`, `hours_report.py` | 하루 모든 2시간 구간에서 시험: 봉 시각을 밀어 다른 시간대를 21:30~23:30 자리에 놓음(데이터 7/16~10/21, 구간 649개). 결과 `tune_hours.json` |
| `monthly.py` | 팀 규칙(8a4036b) vs 지금 규칙 월별 성적 (7/16~10/21, 결과 `tune_monthly.json`). 정리는 `docs/rules-team-vs-now.md` 4장 |
| `vel_test.py` | 속도 계산 방식 비교(`score.Score.VMODE`: 6봉 전 한 점 / 비교 기준 3봉 평균 / 양쪽 평균 / 회귀 기울기), 모든 시간대. 결과 `tune_vel.json` |
| `tune_vel2.py` | 속도 방식 × 속도 기준값(ScoreVelMin·지속 진입 상한) 격자, 모든 시간대 + 주 단위 교차 검증. 결과 `tune_vel2.json` → ref3·0.25·0.15 채택 |
| `tune_vel3.py` | 현재 쪽도 평균(최근 2·3봉 평균 − 5~8봉 전 평균) × 기준값 격자. 결과 `tune_vel3.json` (넣지 않음) |
| `chop_test.py` | 실제 45일 + 모의 흔들리는 날 90일을 같이 돌려 설정 비교, 연 환산 샤프 |
| `common.py` | 데이터 구간, 지금 C# 기본값에 맞춘 설정, 요약 |
| `replay.py` | 실행 |

## C# 설정과 재현기 인자

| C# (`TQ_Strategy`) | 기본 | 재현기 (`common.DEFAULT` 등) |
| --- | --- | --- |
| `UseR7Regime`, `TrendEntryPullbackOnly` | 켬, 끔 | `reg="v6"`, `tstate="trendpull"` |
| `UseSideEntry` | 끔 | `sstate="off"` |
| `UseWarnExit`, `NewsBlockMinutes` | 끔, 0 | `warn=False`, `news=False` |
| `UseTrailAfterTp1`, `UseTrendChangeExit` | 켬, 켬 | `trail=True`, `rx=True` |
| `ChopMaxEr` | 0.15 | `chop_er=0.15`, `chop_mode="pull"` |
| `TrendContBars`, `TrendContMaxVel` | 8, 0.2 | `cont_k=8` (`cont_once=True`, `cont_chop=True`, `cont_mid=False`), `cont_vmax=0.2` |
| `EntryQuantity`, `MaxQuantity` | 50, 80 | `qty=50`, `max_q=80` |
| `AddOnQty`, `AddOnEndTime`, `AddOnMinRoomAtr` | 30, 231000, 1 | `add_q=30`, `add_when="tp2"`, `add_end="23:10"`, `add_room=1` |
| `DnShortSmaBlock`, `DnShortSmaWaitBars` | 켬, 10 | `sma_block="short"`, `sma_wait=10` |
| `SkipPassedChunks` | 켬 | `ext_skip=True` (`tp1_mid=False`) |
| `EarlyQtyEndTime`, `EarlyQty` | 214500, 25 | `early_q=("21:45", 25)` |
| `RiskCapUsd` | 4000 | `cap=4000` (`Combo11.trader_qty`) |
| `T1Window` | 3 | `team.P.T1Window` 기본 3 (10/23 밤 12부터, 전에는 5) |
| `MinStopAtr` | 1.5 | `team.P.MinStopAtr` 기본 1.5 (10/23 밤 11부터, 전에는 1.0 — 그 전 changelog 숫자는 1.0 기준) |
| `BreakevenMinR` | 0.25 | `combo.Combo.BE_MIN_R` |
| `ForceMinEntries`, `MinEntries` | 켬, 4 | `force=True`, `min_entries=4` |
| `UseStrongBreakout` | 끔 | `Combo4` 기본 `sb=False` |

C#을 고치면 여기도 같이 고치고, `py -X utf8 replay.py`로 합계가 changelog와 맞는지 본다. 지금 기본값(10/23 밤 12, T1 3봉·손절 최소 1.5 ATR): 7월 +$88,154 / 9월 초 +$14,406 / 9/21~10/2 +$58,128 / 10월 +$26,971, 합계 +$187,659(비용 전), 최악의 날 −$4,876.

`strat.py`의 `walk`에 트레이더 진입 실험용 봉 안 체결(`e3`의 `kind` = "stop"·"limit")을 더했다. `kind`가 없으면(v6 E3) 예전과 같다.

## 맞춰 본 것과 한계

- 10/23 밤 Playback(9/30~10/21, 16일, 진입 50 + 불타기 30)과 날짜별 거래 수가 모두 같고, 손익은 대부분 몇백 달러 안에서 맞았다. 차이가 큰 날은 10/14(−$1.7k)와 실시간이었던 10/21(−$4.1k, 불타기 직후 틱 손절)이다.

- v6 부분(`strat.py`)은 10/22 Playback 33건과 진입·청산 판단이 같았다(커밋 `7561638`, 브랜치 `v6-box`).
- 팀 진입·청산 부분은 Playback 로그와 거래 단위로 대조해 고쳐 왔다(10/22 본절 버그 등, changelog). 체결 가격은 1분봉 근사라 실시간과 다르다.
- 대회 구간 밖 거래는 세지 않는다(진입 21:30~23:29).
