# 백테스트 가이드와 설정값 설명 (v3)

`TQ_Strategy` v3(브랜치 `v3-integrated`)를 Strategy Analyzer로 백테스트하고 Playback으로 확인하는 절차와, 설정창에 보이는 값의 뜻을 정리한 문서입니다. 규칙 자체는 `docs/spec.md`(v3), 바뀐 이력은 `docs/changelog.md`에 있습니다. v2 가이드는 `main` 브랜치의 같은 파일에 있습니다.

## 1. 돌리기 전에 확인할 것

| 확인 | 이유 |
| --- | --- |
| Custom 폴더에 `obj` 폴더가 없다 | 있으면 컴파일이 CS0579로 깨진다. Visual Studio로 이 폴더를 열면 생긴다 |
| NinjaScript Editor에서 F5를 눌러 에러가 없다 | 에러가 있으면 예전 코드로 백테스트가 돈다 |
| 차트에 올려 둔 전략·지표를 지웠다가 다시 올린다 | v3는 설정값이 전부 바뀌어 예전 인스턴스·템플릿을 그대로 쓸 수 없다 |
| Connections에서 Playback 연결을 끊는다 (백테스트 때) | Playback에 연결된 동안에는 Run 버튼이 눌리지 않는다 |

## 2. 백테스트 절차

1. Control Center → New → **Strategy Analyzer**, 종류 **Backtest**, 전략 **TQ_Strategy**.
2. **Instrument**: `MNQ DEC26` (골드는 `MGC DEC26`). 직접 입력하고 Enter.
3. **Data Series**: Minute, Value **1** (진입 봉. 4장).
4. **기간**: 장기선 60봉(3분봉이면 3시간)이 채워지도록 보고 싶은 첫날보다 며칠 앞부터. To는 마지막으로 볼 저녁의 다음 날.
5. 설정값을 확인하고 **Run**. 결과는 **Summary**와 **Trades**.
6. 거래 단위 분석은 `TQ_ml_v3_종목.csv`(9장)가 편하다.

**지켜야 할 것:** 한 번에 하나만 바꾼다. 바꾸기 전에 기본값으로 기준을 잡는다. 설정을 고를 때 쓴 날로 확인하지 않는다(9월로 고르고 10월로 확인).

## 3. 결과 읽는 법

분할 진입 한 덩어리를 NinjaTrader는 조각마다 1건으로 센다. 같은 시각에 들어간 건들을 묶어 "진입 1번"으로 읽는다.

| Entry name | 뜻 |
| --- | --- |
| TL1, TL2, TL3 | 추세 매수: 1차(+2배) / 2차(+3배) / 마지막 조각. 30계약 33/33이면 9 / 9 / 12 |
| TS1, TS2, TS3 | 추세 매도 (거울상) |
| SL1, SL2 | 횡보 매수: 1단계(진입가 + 2 ATR) / 나머지. 30계약 33%면 9 / 21 |
| SS1, SS2 | 횡보 매도 (거울상) |

| Exit name | 뜻 |
| --- | --- |
| Profit target | 밴드·ATR 지정가 익절 |
| x로 시작 (예: xTL3) | 조각 단위 청산(의심 일부청산, 봉 마감 익절) |
| Stop loss | 손절. 로그·학습 데이터의 사유 코드로 손절(`STOP`) / 본절(`BE_STOP`) / 트레일링(`TRAIL`)을 구분 |
| Sell / Buy to cover | 나머지 전량 청산 (사유 코드는 로그·`exit_reason`) |

MNQ는 1포인트가 계약당 2달러다. 30계약이면 1포인트에 60달러.

## 4. 진입 봉과 추세 봉

전략은 Data Series에서 고른 봉을 진입 봉으로 쓴다. v3는 **1분봉**이다. 추세 봉은 코드가 붙이는 3분봉(기본)이나 10분봉이다(`RegimeBarMinutes`).

| | 진입 봉 1분 | 추세 봉 3분 (기본) |
| --- | --- | --- |
| 밴드 EMA 26 / ATR 14 | 26분 / 14분 | – |
| 구조 손절 5봉, 의심 가격 확인 5봉 | 5분 | – |
| 반전 대기 6봉 | 6분 | – |
| 이동평균 6 / 20 / 60 | – | 18분 / 60분 / 180분 |

## 5. 설정값

기본값만으로 돌리면 설계안(v3 지시서) 그대로다. 설정값 전체와 기본값 표는 spec 8장, 고민 → 스위치 대응은 spec 9장.

설정창은 판단 순서대로 13개 묶음(00~12)이고 각 항목은 "[태그] 한국어 설명 (코드 이름)"이다. 태그: [설계] 설계안의 동작과 값, [선택] 고민사항 스위치, [안전] 주문 거부 대비, [표시] 화면·기록. 한 묶음 안에서 [설계]가 위, [선택]이 아래. 쓰이지 않는 설정은 회색으로 잠긴다. 항목에 마우스를 올리면 기본값과 뜻, 올리거나 켜면 어떻게 되는지가 나온다.

**전략을 차트에 다시 올리면 설정이 모두 기본값으로 돌아간다.** 같은 설정으로 여러 번 돌릴 때는 설정창의 template 저장 기능을 쓴다. "설계안 기본값으로 되돌리기" 버튼(00 묶음)은 10·12 묶음을 뺀 매매 규칙 설정을 기본값으로 되돌린다. 돌린 설정은 로그 첫 줄(`====`)과 학습 데이터 파일의 `#` 줄에 남는다.

### 5-1. 01 추세 판단 (추세 봉)

| 설정값 | 기본 | 뜻 | 비교할 것 |
| --- | --- | --- | --- |
| RegimeBarMinutes | 3 | 추세 봉. 3 또는 10 | 10 (이때 이평 6/18/36) |
| RegimeFast / RegimeMid / RegimeSlow | 6 / 20 / 60 | F1 단기선, S1·S2 중기선, S2 장기선 | Fast 8 |
| TrendStartFast / TrendStartSlow | 2 / 2 | 추세 시작: F ≥ 2 AND S ≥ 2 | |
| TrendEndSlow | 0 | 추세 끝: S ≤ 0 → 횡보 | |
| RegimeMacdSlopeBars | 2 | F2: MACD선 연속 증가·감소 봉 수 | |
| RegimeS2Mode / RegimeSlopeBars | 1 / 5 | S2: 1 중기 > 장기, 2 장기선 기울기(5봉 전 대비) | 2 |
| RegimeUseRsi / RegimeRsiBand | 켬 / 5 | S4: RSI 55 이상 +1, 45 이하 −1 | 끔 |

### 5-2. 02 의심 이벤트

| 설정값 | 기본 | 뜻 | 비교할 것 |
| --- | --- | --- | --- |
| SuspectFast | 2 | 상승추세 중 F ≤ −2가 된 첫 봉에 하락의심 이벤트 | |
| SuspectNeedPrice / SuspectConfirmBars | 켬 / 5 | 이벤트 뒤 5봉 안에 종가가 직전 5봉 저점 아래로 마감해야 일부청산 | 끔 |
| SuspectExitPercent | 50 | 남은 수량의 %. 먼 조각부터 조각 단위 | 33 / 100 |
| SuspectStrongSlow / SuspectStrongExitPercent | 1 / 100 | 확인 때 S ≤ 1이면 전량 | |
| BlockEntryWhileSuspect | 켬 | 의심 중 추세 방향 신규 진입 금지 | |
| SuspectBlockNeedPrice | 켬 | 그 금지는 의심 뒤 가격 확인(직전 5봉 저점 이탈)이 있었을 때만 | 끔 |

### 5-3. 03 진입 — 추세, 04 진입 — 횡보, 05 진입 — 공통

| 설정값 | 기본 | 뜻 | 비교할 것 |
| --- | --- | --- | --- |
| UseEntryTrendStart | 켬 | E1: 추세 시작을 처음 본 진입 봉에 진입. 반대 추세 시작으로 청산한 뒤에는 2봉 안에도 | |
| EntryMinRR | 1.0 | E1·E2: 2차 익절까지 남은 폭 ≥ 예상 손절 × 이 값 | 0 / 1.5 |
| E1Reaccel | 켬 | E1R: 추세 중 F가 0 이하로 갔다가 다시 시작 조건을 채운 추세 봉에서 E1 | 끔 |
| E1AtSessionStart | 켬 | E1S: 구간 첫 진입 가능 봉(21:35)에 이미 있는 추세로 E1 | 끔 |
| UseEntryPullback / TrendPullbackBand | 켬 / 1 | E2: −1배 터치 → [MACD 상승 전환 OR 골든] | |
| UseEntryOrb / OrbEndTime / OrbMinSlow / OrbMinFast | 켬 / 230000 / 1 / 1 | E3: 장후 23:00 전, 종가가 장전 박스 밖 + S·F ≥ 1. 방향별 OrbMaxEntries번 | 끔 |
| OrbMaxEntries | 2 | E3를 쓴 뒤 종가가 박스 안으로 돌아오면 다시 열림, 방향별 최대 횟수 | 1 |
| UseEntrySide / SideBand / SideT2Entry | 켬 / 2 / 켬 | E4: 횡보 중 −2배 터치 → 반전, 또는 MACD 전환 + 크로스 | |
| SideBlockScore | 3 | S+F ≥ 3이면 횡보 매도, ≤ −3이면 횡보 매수 금지 | |
| BlockSideAfterStop | 켬 | 횡보 손실 손절 뒤 같은 방향 횡보 재진입 금지 | |
| T1Window / MacdConfirmBars / T2CountConfirmBars | 5 / 2 / 켬 | 반전 대기 5+1봉, MACD 전환 2봉 연속, T2에서 MACD 꺾인 봉의 크로스 인정 | |

### 5-4. 06 손절

| 설정값 | 기본 | 뜻 |
| --- | --- | --- |
| SwingBars | 5 | 구조 손절: 최근 5봉 저가 − 1틱 |
| TrendStopMinAtr / TrendStopMaxAtr | 1.5 / 3.0 | 추세 포지션 손절 거리 범위(신호 봉 1분 ATR 배수) |
| SideStopMinAtr / SideStopMaxAtr | 1.0 / 2.0 | 횡보 포지션 손절 거리 범위 |
| StopMinTicks | 32 | 최소 손절 거리(MNQ 8pt). 장전 ATR이 작을 때 |
| BreakevenMinR / BreakevenFallbackAtr | 0.5 / 1.5 | 1차 이익 ≥ 손절 거리 × 0.5면 평단, 아니면 1차 체결가 ∓ 1.5 ATR | 0 (항상 평단) |
| BreakevenBufferTicks / HandleOrderErrors | 8 / 켬 | 손절 이동 안전 거리, 주문 오류 직접 처리 |

### 5-5. 07 청산 — 추세, 08 청산 — 횡보

| 설정값 | 기본 | 뜻 | 비교할 것 |
| --- | --- | --- | --- |
| TrendTp1Band / TrendTp2Band | 2 / 3 | 1차·2차 익절 밴드 | |
| TrendTp1Percent / TrendTp2Percent | 33 / 33 | 1차·2차 수량 %, 나머지가 마지막 조각 | |
| TrailAtr | 2.5 | 2차 뒤 트레일링: 최고가 − 2.5 × 1분 ATR | 2 / 3 |
| TrendLastExitMa | 1 | 2차 뒤 추세 봉 종가 < 중기선이면 나머지 전량. 2 단기선, 0 트레일링만 | 0 / 2 |
| TrendExitOnEnd | 켬 | 추세가 횡보로 바뀌면 전량 | 끔 |
| TrendExitOnEntryT2 | 끔 | 추세 유지 중에도 1분 T2 반대 신호로 전량 | 켬 |
| TrendTpMinR | 0.5 | 진입 때 익절 밴드까지 남은 거리 < 예상 손절 × 0.5면 지난 밴드로 봄 | 0 |
| SideTp1Atr / SideTp1Percent | 2 / 33 | 횡보 1단계: 진입가 + 2 ATR(진입 때 고정)에서 33% | |
| SideRestBand | 3 | 종가가 +3배에 닿으면 나머지 전량 | |
| SideOscExitAfterTp1 | 켬 | MACD·크로스 청산은 1단계 뒤에만 | 끔 |
| SideExitOnOppTrend | 켬 | 반대 추세 시작 시 전량 | |

### 5-6. 09 시간대

| 설정값 | 기본 | 뜻 | 비교할 것 |
| --- | --- | --- | --- |
| OpenTime | 223000 | 미국 정규장 개장(KST). 골드는 차트마다 따로 | |
| NewsBlockMinutes | 5 | 21:30부터 5분 진입 금지(지표 발표) | 대회 날 일정대로 |
| OpenBlockBefore / OpenBlockAfter | 5 / 3 | 22:25~22:32 봉 진입 금지 | |
| PreOpenQtyPercent | 50 | 장전 수량 % | 100 |
| PreOpenTrendMinSlow / PreOpenAllowSide | 2 / 켬 | 장전 E1·E2는 S ≥ 2일 때만(10/15에 3에서 내림), 장전 E4 허용 | 3 |
| ProtectBeforeOpen / ProtectAtr | 끔 / 1 | 22:29 봉 마감에 이익 ≥ 1 ATR이면 손절 평단 | 켬 |
| MinEntryFallback / MinEntryFallbackTime / MinEntryQtyPercent | 켬 / 230000 / 30 | 23:00 이후 진입 횟수가 최소 목표보다 적으면 다음 유효 신호를 30%로 | |

### 5-7. 10 수량·대회 시간, 11 지표 계산값, 12 화면·기록

| 설정값 | 기본 | 뜻 |
| --- | --- | --- |
| EntryQuantity / MaxQuantity / MinEntries | 30 / 40 / 2 | 진입 계약 수, 대회 한도(경고만), 종목당 최소 진입 목표 |
| ContestStartTime / EntryEndTime / FlattenTime | 213000 / 231500 / 232700 | 진입 시작 / 진입 마감 / 23:27 봉 마감 전량 청산 |
| BandMidPeriod / BandAtrPeriod / BandMult1~3 | 26 / 14 / 1·2·3 | ATR 채널. ATR 14는 손절·익절·트레일링 거리에도 씀 |
| MacdFast / MacdSlow / MacdSmooth | 12 / 26 / 9 | 진입 봉과 추세 봉에 같이 |
| StochPeriodK / StochSmooth / StochPeriodD | 10 / 5 / 5 | 진입 봉 스토캐스틱 |
| CrossLow / CrossHigh / CrossFilterBars | 20 / 80 / 5 | 골든은 최근 5봉 안에 %K < 20, 데드는 > 80 |
| RsiPeriod | 14 | 진입 봉과 추세 봉(S4) |
| ShowStatus / StatusPanelScale | 켬 / 130 | 차트 표시, 상태 상자 크기 % |
| LogToFile / MlLogToFile | 켬 / 켬 | 로그(`TQ_log_v3_종목.txt`), 학습 데이터(`TQ_ml_v3_종목.csv`) |
| UseTestEntry | 끔 | 주문 흐름 검증용 가짜 매수(2봉째 1차, 3봉째 2차, 4봉째 전량). **평소에는 반드시 끔** |
| IntrabarTakeProfit | 켬 | 익절을 봉 진행 중 지정가로. 끄면 봉 마감 종가로 시장가 |

**왼쪽 위 상태 상자:** 구역마다 색 제목줄과 배경이 있다. 현재 상태(추세 색, 큰 글씨 추세 · 의심, S·F와 항목별 방향, 의심 가격 확인 대기, 구간·장전 박스, 1분봉 지표) / 진입 조건(미보유일 때 방향별 E1~E4 상태와 막힌 사유, 진입 횟수) / 포지션 / 익절 / 손절 / 청산. 줄 색: ● 초록(충족·완료), ○ 흰색(기다리는 조건), × 회색(막힘), 들여쓴 줄 옅은 회색(참고).

**차트:** 배경 = 추세(초록 상승, 빨강 하락, 의심 중 주황·하늘색), 진입 화살표(매수 초록 ↑, 매도 빨강 ↓), 청산 화살표(파랑), 체결 라벨, 손절선(자홍)·평단선(회색 점선), 장전 박스 고가·저가(금색 점선, 22:30 이후). 영어 체결 글자는 차트 우클릭 → Data Series → Plot executions → Do not plot으로 끈다.

## 6. 확인 순서 (v3 지시서 8장)

| 단계 | 할 것 | 완료 기준 |
| --- | --- | --- |
| 1 | 채점기 `--v3` 비교 (`docs/tools/README.md`) | 9월로 고르고 10월로 확인. 기본값(6/20/60, S2 1, RSI 켬)의 지연이 mode=2 3분보다 짧거나 같은지 |
| 2 | TQ_Regime을 3분봉 차트에 올리고 `ShowDebugVisuals` 켬 | 배경색, Output 창의 추세·의심 줄. Playback 로그로 `--validate` |
| 3 | `UseTestEntry` 켜고 Playback | 진입 → 2봉째 1차(손절 평단) → 3봉째 2차 → 4봉째 전량 |
| 4 | 기본값으로 Playback 하루 | 경로별 진입이 로그(`진입 … 경로=E1~E4`)에 남고, 막힌 신호의 사유가 상태 상자·로그(`후보 …`)에 나옴 |
| 5 | 지시서 8장 시나리오 14개 | 로그의 사유 코드와 손절 이동 줄로 하나씩 |
| 8 | Strategy Analyzer 9~10월 MNQ·MGC, spec 9장 스위치 하나씩 | 날짜별 손익·최악의 날을 `docs/status-v3.md`에 |

## 7. 더 긴 데이터 받기

지금은 Connections에 Playback뿐이라 과거 데이터를 자동으로 받지 못한다. 시도해 볼 방법(확인 전): Playback Connection에 연결 → New → Chart에서 종목·Minute 1·기간을 직접 지정해 열기 → 봉이 그려지면 받아진 것. 채점기용 1분봉은 Control Center → Tools → Historical Data → Export(`docs/tools/README.md`).

## 8. 자주 겪는 문제

| 증상 | 원인과 해결 |
| --- | --- |
| Run 버튼이 눌리지 않는다 | Playback에 연결돼 있거나 종목이 비어 있다 |
| 거래가 0건이다 | 데이터가 없거나, 장기선 60봉(3분봉 3시간)이 채워지기 전이다. 기간을 앞으로 늘린다 |
| `TargetFrameworkAttribute` 중복 에러 | Visual Studio가 `obj` 폴더를 만들었다. 지우고 다시 F5 |
| 설정을 바꿨는데 차트 지표가 그대로다 | 차트에 올려 둔 지표는 예전 설정을 기억한다. 지웠다가 다시 올린다 |

## 9. 학습 데이터 (`MlLogToFile`)

거래가 끝날 때마다 `내 문서\NinjaTrader 8\TQ_ml_v3_종목.csv`에 한 줄이 쌓인다(백테스트 포함). v2 파일(`TQ_ml_종목.csv`)과 섞이지 않게 이름을 따로 둔다. `#` 줄은 실행별 설정 요약이고 pandas에서는 `pd.read_csv(path, comment='#')`. 같은 구간을 여러 번 돌리면 같은 거래가 중복으로 쌓이므로 `signal_time`·`instrument`·`settings_id`가 같은 줄은 하나만 남긴다. 최적화 때는 끈다.

main과 같은 열 (뜻은 같고 v3에서 바뀐 것만 적음)

| 열 | 뜻 |
| --- | --- |
| run_id, settings_id, mode, instrument, bar_min | 실행 ID, 설정 해시, `hist`/`live`, 종목, 진입 봉 분 |
| signal_time, weekday, hhmm | 신호 봉 시각, 요일, 시분 |
| strategy, regime, dir | 포지션 종류(TL / TS / SL / SS), 추세(+1 / 0 / −1), 방향 |
| via_t2 | v3에는 없음. 항상 0 (열 순서 유지) |
| close, atr, atr_pct, stop_dist_atr, mid_dist_atr, sma20_dist_atr | 신호 봉 종가, ATR(14), ATR/종가 %, 손절 거리/ATR, 밴드 중심선·진입 봉 18선과의 거리/ATR |
| macd_hist_atr, macd_slope_atr, k, d, k_minus_d, rsi, macd_up, macd_down, golden, dead | 진입 봉 지표 |
| bar_range_atr, swing_range_atr, vol_ratio | 봉 고저폭/ATR, 최근 5봉 고저폭/ATR, 거래량 비 |
| htf_sma20_dist_atr, regime_age, regime_changes6 | 추세 봉 종가와 단기선 거리/ATR, 지금 추세가 이어진 추세 봉 수, 최근 6봉 추세 변경 횟수 |
| session_entry_no, prev_r, min_since_prev_exit | 구간 내 진입 순번, 직전 거래 R, 직전 청산 뒤 분 |
| exit_time, hold_bars, qty, avg_price, risk_pts, pnl_pts, pnl_usd, pnl_r, mfe_pts, mae_pts, mfe_r, mae_r, win, exits | 결과 |

v3 추가 열 (7장)

| 열 | 뜻 |
| --- | --- |
| path | 진입 경로 E1(추세 시작) / E1X(반대 추세 청산 뒤) / E1R(재가속) / E1S(구간 첫 봉 추세) / E2(추세 눌림) / E3(장전 박스 돌파) / E4(횡보) / TEST |
| session | pre(장전) / post(장후) |
| s1, s2, s3, s4, f1, f2, slow, fast | 신호 봉 시점 추세 봉 점수 항목(+1 / 0 / −1, RSI를 끄면 s4 빈칸)과 S, F |
| pre_box_high, pre_box_low | 장전 박스 (진입 시점까지 기록된 값) |
| atr_entry | 신호 봉 1분 ATR (= atr) |
| rr_expected | E1·E2의 손익비 (2차 익절까지 남은 폭 ÷ 예상 손절 거리). 다른 경로는 빈칸 |
| fallback_entry | 최소 진입 보장으로 들어갔으면 1 |
| suspect_events | 보유 중 본 반대 방향 의심 이벤트 수 |
| suspect_action | 마지막 의심 처리: SUSPECT / SUSPECT_STRONG / CANCEL(해제·확인 없음) |
| tp_stages_hit | 체결된 익절 단계 수 |
| trail_used | 트레일링으로 손절이 한 번이라도 옮겨졌으면 1 |
| exit_reason | 마지막 청산 사유 코드: TP1, TP2, LAST_MA, TRAIL, STOP, BE_STOP, SUSPECT, SUSPECT_STRONG, TREND_END, OPP_TREND, ENTRY_T2, SIDE_REST_BAND, SIDE_MACD, SIDE_CROSS, FLATTEN_TIME, GAP |
