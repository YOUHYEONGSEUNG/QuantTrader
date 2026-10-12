# 지표 ↔ 전략 인터페이스 (계약서)

담당 A(신호 엔진)는 아래 이름·타입·의미대로 값을 노출하고, 담당 B(전략)는 이것만 읽어서 주문을 낸다.
변경은 두 사람이 합의한 뒤 이 파일부터 고친다. 2026-10-12에 spec v2(대면회의 이후 규칙)에 맞춰 전체를 다시 썼다.

## 원칙
- A는 "이 봉에서 이 조건이 참인가"만 계산한다. 포지션·주문·시간대와 무관한 값만 낸다.
- B는 포지션 상태에 따라 달라지는 모든 것(어느 진입 신호를 쓸지, 익절 단계, 손절, 수량, 대회 시간)을 처리한다.
- 모든 값은 진입 봉(1분봉) 마감 시점 기준이다 (TQ_Regime만 추세 봉 = 10분봉).
- 설정값 타입: 기간·봉 수·밴드 배수 선택은 int, 기준값(CrossLow/High, RSI 기준)과 ATR 배수는 double, 켜고 끄는 것은 bool.

## TQ_ATRChannels (A) — 입력: 진입 봉
| 설정값 | 기본값 |
| --- | --- |
| MidPeriod (EMA) | 26 |
| AtrPeriod | 14 |
| Mult1 / Mult2 / Mult3 | 1 / 2 / 3 |

| 플롯 | 의미 |
| --- | --- |
| Mid | EMA(MidPeriod) |
| Up1, Up2, Up3 | Mid + ATR × Mult1·Mult2·Mult3 |
| Dn1, Dn2, Dn3 | Mid − ATR × Mult1·Mult2·Mult3 |

## TQ_Regime (A) — 입력: 추세 봉 (전략에서 `TQ_Regime(BarsArray[1], ...)`)
추세 5단계를 낸다 (spec 3장). 입력 시리즈는 전략이 정한다: 기본은 10분봉이고, 전략의 `RegimeBarMinutes`가 3이면 3분봉이다.

| 설정값 (생성자 인자 순서) | 기본값 | 의미 |
| --- | --- | --- |
| Fast / Mid / Slow | 6 / 18 / 36 | 정배열·역배열을 보는 SMA |
| ShowDebugVisuals | false | 검증용 배경색 |
| MacdFast / MacdSlow / MacdSmooth | 12 / 26 / 9 | MACD선 부호와 히스토그램 |
| HistBars | 2 | MACD 히스토그램이 몇 봉 연속 줄어야(늘어야) 전환으로 볼지 |
| RsiPeriod | 14 | |
| RsiHigh / RsiLow | 60 / 40 | 하락 전환 의심은 RSI ≥ RsiHigh, 상승 전환 의심은 RSI ≤ RsiLow |
| DivBars | 15 | 다이버전스: 최근 몇 봉 안의 고점·저점과 비교할지 |
| PivotStrength | 2 | 고점·저점으로 인정할 좌우 봉 수 |
| StochPeriodK / StochSmooth / StochPeriodD | 10 / 5 / 5 | |
| CrossLow / CrossHigh / CrossFilterBars | 20 / 80 / 5 | 골든·데드크로스 필터 (spec 2장 정의 그대로) |
| SuspectReturn | true | 의심 중 반대 신호가 나오면 원래 추세로 돌아감 |
| RsiLookback | 3 | 의심의 RSI 조건을 최근 몇 봉(현재 포함) 안에서 볼지. 1 = 같은 봉에서만 (전략은 `RegimeRsiBars`를 넘김, 2026-10-12 추가) |
| NoSideways | false | 켜면 횡보(0)를 내지 않는다. 상승·하락 조건이 둘 다 아닌 구간은 마지막 추세의 전환 의심(±1)으로 낸다. 생성자 인자 순서로는 ScoreRsiBand 다음, 맨 끝이다 (2026-10-12 추가) |
| Mode | 1 | 추세 판단 방식. 1 = 회의 문서 정의(정배열 AND MACD선 부호), 2 = 점수 방식 (spec 3장, 2026-10-12 추가) |
| ScoreMin | 2 | 점수 방식: 추세로 인정할 최소 점수 |
| ScoreRsiBand | 5 | 점수 방식: RSI가 50에서 이만큼 벗어나야 방향으로 인정 |
| ShowPlots | false | 차트 표시 전용. 생성자 인자가 아니다 |

| 이름 | 타입 | 의미 |
| --- | --- | --- |
| Score | int | 화면 표시용. 점수 방식의 합계 (−4 ~ +4). Mode 1에서도 계산한다 |
| ScoreText | string | 화면 표시용. 지표별 방향 (예: "가격▲ MACD▼ RSI− 스토▲") |
| Regime | 플롯 | +2 상승추세, +1 하락 전환 의심, 0 횡보, −1 상승 전환 의심, −2 하락추세. 부호는 바탕 추세의 방향. 데이터가 모자라면 NaN |
| SmaFast | 플롯 | SMA(Fast) |
| MacdLine | 플롯 | 추세 봉의 MACD선 |
| Rsi | 플롯 | 추세 봉의 RSI |
| Aligned | int | 화면 표시용. +1 정배열, −1 역배열, 0 둘 다 아님 |
| SuspectReason | string | 화면 표시용. 지금 의심 상태의 근거 (의심이 아니면 빈 문자열) |

판정:
- Mode 1: 상승추세 조건 = 정배열 AND MACD선 > 0. 하락추세 조건 = 역배열 AND MACD선 < 0.
- Mode 2: 가격(종가 vs SMA Fast와 그 기울기), MACD 히스토그램(부호 AND 직전 봉 대비 같은 방향), RSI(50 ± ScoreRsiBand), 스토캐스틱(%K vs %D AND %K가 50의 같은 쪽)이 각각 +1 / −1 / 0. 가격이 +1이고 합계 ≥ ScoreMin이면 상승추세 시작, 합계가 1 아래로 내려가면 끝. 하락은 거울상.
- 두 방식 모두, 상승·하락 조건이 둘 다 아니면 횡보이고 의심 상태도 끝난다.
- 상승추세 조건 중: RSI ≥ RsiHigh(최근 RsiLookback봉 안에 한 번이라도) AND [하락 다이버전스 OR 히스토그램 HistBars봉 연속 감소 OR 데드크로스]면 의심을 켠다. 의심은 유지되고, SuspectReturn이 켜져 있으면 히스토그램 연속 증가 또는 골든크로스에서 푼다. 하락 쪽은 거울상.
- 다이버전스: DivBars봉 안에서 좌우 PivotStrength봉보다 고가가 높은 봉(고점) 중 가장 높은 것과 지금 봉을 비교. 지금 고가가 더 높은데 RSI는 낮으면 하락 다이버전스. 저점으로 거울상이 상승 다이버전스.
- MACD 전환은 TQ_MacdTurn(…, HistBars, 14, 0)의 TurnUp/TurnDown을 쓴다 (히스토그램 HistBars봉 연속 증가·감소가 처음 확인된 봉).
- 스토캐스틱은 TQ_Signals·TQ_Stochastic과 같은 식으로 직접 계산한다.

## TQ_MacdTurn (A) — 입력: 진입 봉 또는 추세 봉
MACD 전환 판정을 한곳에서 한다. TQ_Signals와 TQ_Regime이 내부에서 호출하고, 전략은 직접 호출하지 않는다.
차트에 올리면 히스토그램 막대 색으로 판정을 볼 수 있다: 초록 = 상승 전환, 빨강 = 하락 전환, 회색 = 둘 다 아님.

| 설정값 | 기본값 |
| --- | --- |
| Fast / Slow / Smooth | 12 / 26 / 9 |
| ConfirmBars | 2 (연속 증가·감소 봉 수) |
| AtrPeriod | 14 (TQ_Signals는 BandAtrPeriod를 넘김) |
| MinChangeAtr | 0 (최소 변화폭, ATR 배수. 0 = 사용 안 함) |

| 이름 | 타입 | 의미 |
| --- | --- | --- |
| Hist | 플롯 | MACD(Fast, Slow, Smooth).Diff |
| MacdLine, SignalLine | 플롯 | MACD선(파랑)과 시그널선(주황) |
| Up, Down | Series&lt;bool&gt; | 방향. 히스토그램이 ConfirmBars봉 연속 증가 / 감소했고 그 구간의 변화폭이 MinChangeAtr × ATR 이상. 같은 방향이 이어지는 동안 계속 참. 막대 색과 화면 표시에만 쓴다 |
| TurnUp, TurnDown | Series&lt;bool&gt; | 전환 (spec 2장). Up / Down이 거짓에서 참으로 바뀐 봉 하나에서만 참. 진입·청산·추세 판단은 이 값을 쓴다 (2026-10-12 추가) |

## TQ_Stochastic — 입력: 진입 봉 (차트 표시 전용)
TQ_Signals의 스토캐스틱과 같은 식으로 %K·%D를 계산해 독립 패널에 그린다. 눈으로 검증하는 용도이고 전략과 TQ_Signals는 이 지표를 호출하지 않는다.

| 설정값 | 기본값 |
| --- | --- |
| PeriodK / SmoothK / PeriodD | 10 / 5 / 5 |

| 플롯 | 의미 |
| --- | --- |
| K, D | %K(파랑), %D(주황). 기준선 80 / 50 / 20 |

## TQ_Signals (A) — 입력: 진입 봉
| 설정값 (생성자 인자 순서) | 기본값 | 의미 |
| --- | --- | --- |
| T1Window | 5 | T1 대기 봉 수 N |
| CrossFilterBars | 5 | |
| CrossLow / CrossHigh | 20 / 80 | |
| RsiPeriod | 14 | |
| SwingBars | 5 | 전고점·전저점 구간 |
| BandMidPeriod / BandAtrPeriod | 26 / 14 | TQ_ATRChannels와 같은 값 |
| BandMult1 / BandMult2 / BandMult3 | 1 / 2 / 3 | TQ_ATRChannels와 같은 값 |
| MacdFast / MacdSlow / MacdSmooth | 12 / 26 / 9 | |
| StochPeriodK / StochSmooth / StochPeriodD | 10 / 5 / 5 | |
| SmaPeriod | 18 | 마지막 조각 청산용 이동평균 (전략은 `ExitSmaPeriod`를 넘김) |
| MacdConfirmBars | 2 | TQ_MacdTurn의 ConfirmBars |
| MacdMinChangeAtr | 0 | TQ_MacdTurn의 MinChangeAtr |
| ShowDebugVisuals | false | 검증용 화살표 |
| UpLongBand | 1 | 5.1: 저가가 몇 배 밴드에 터치. 밴드 설정은 모두 0~3이고 0 = 중심선 |
| UpSusLongBand | 2 | 5.2: 종가가 몇 배 밴드 아래 |
| UpSusShortBand | 1 | 5.3: 고가가 몇 배 밴드에 터치 |
| DnShortBand | 1 | 5.4: 고가가 몇 배 밴드에 터치 |
| DnLongBand | 3 | 5.5: 저가가 몇 배 밴드에 터치 |
| DnLongRsi | 30 | 5.5: 그 봉의 RSI가 이 값 미만 |
| DnSusLongBand | 1 | 5.6: 저가가 몇 배 밴드에 터치 |
| DnSusShortBand | 2 | 5.7: 종가가 몇 배 밴드 위 |
| SideBand | 2 | 5.8·5.9: 저가(고가)가 몇 배 밴드에 터치 |
| T1WindowUp / T1WindowDn / T1WindowSide | 0 | 5.1~5.3 / 5.4~5.7 / 5.8·5.9의 T1 대기 봉 수. 0이면 T1Window |
| T2CountConfirmBars | true | T2에서 MACD 확인에 걸린 봉 수만큼 앞의 크로스도 인정 |
| T1SameBar | true | 밴드 조건이 충족된 봉 자체의 반전 신호도 인정 |
| ShowPlots | false | 차트 표시 전용. 생성자 인자가 아니다 |

차트에 올리면 가격 패널에 얹힌다(IsOverlay = true, IsAutoScale = false). 플롯 값을 눈으로 보려면 ShowPlots를 켠다.

숫자 값 (Series&lt;double&gt;, 플롯)

| 이름 | 의미 |
| --- | --- |
| K, D | 스토캐스틱 %K, %D. 원시 %K = 100 × (종가 − 최저 저가) / (최고 고가 − 최저 저가), 구간 StochPeriodK → %K = SMA(원시 %K, StochSmooth) → %D = SMA(%K, StochPeriodD) |
| Rsi | RSI(RsiPeriod) |
| MacdHist | MACD(MacdFast, MacdSlow, MacdSmooth).Diff |
| Sma | 진입 봉 SMA(SmaPeriod). 기본 18선 (spec 5장 마지막 조각 청산용) |
| SwingHigh5, SwingLow5 | 신호 봉 포함 최근 SwingBars봉의 최고 고가 / 최저 저가 (spec 2장 전고점·전저점) |

이벤트 (Series&lt;bool&gt;) — spec 2장 정의 그대로

| 이름 | 의미 |
| --- | --- |
| MacdUp, MacdDown | TQ_MacdTurn의 TurnUp / TurnDown 그대로 (전환이 처음 확인된 봉 하나에서만 참). 진입·T2·청산 조건 모두 이 값을 쓴다 |
| MacdRising, MacdFalling | TQ_MacdTurn의 Up / Down 그대로 (방향이 이어지는 동안 계속 참). 화면 표시용이고 매매 판단에는 쓰지 않는다 |
| Golden, Dead | 필터 적용된 골든·데드크로스 |
| T2Bull | MacdUp AND Golden (같은 봉 또는 연속 2봉, spec T2). T2CountConfirmBars가 켜져 있으면 MacdUp이 처음 참이 된 봉에서 2~MacdConfirmBars봉 전의 Golden도 인정 |
| T2Bear | MacdDown AND Dead (같은 방식) |

진입 신호 (Series&lt;bool&gt;) — 추세와 무관하게 계산, B가 추세 상태에 맞는 것만 사용

| 이름 | spec | 추세 상태 | 조건 요약 |
| --- | --- | --- | --- |
| EntryUpLong | 5.1 | 상승추세 | Low ≤ Dn(UpLongBand) → T1: [MacdUp OR Golden] |
| EntryUpSusLong | 5.2 | 하락 전환 의심 | Close < Dn(UpSusLongBand) → T1: [MacdUp OR Golden] |
| EntryUpSusShort | 5.3 | 하락 전환 의심 | High ≥ Up(UpSusShortBand) → T1: [MacdDown OR Dead] |
| EntryDnShort | 5.4 | 하락추세 | High ≥ Up(DnShortBand) → T1: [MacdDown OR Dead] |
| EntryDnLong | 5.5 | 하락추세 | Rsi < DnLongRsi AND Low ≤ Dn(DnLongBand) → T1: [MacdUp OR Golden] |
| EntryDnSusLong | 5.6 | 상승 전환 의심 | Low ≤ Dn(DnSusLongBand) → T1: [MacdUp OR Golden] |
| EntryDnSusShort | 5.7 | 상승 전환 의심 | Close > Up(DnSusShortBand) → T1: [MacdDown OR Dead] |
| EntrySideLong | 5.8 | 횡보 | Low ≤ Dn(SideBand) → T1: [MacdUp OR Golden], 또는 T2Bull |
| EntrySideShort | 5.9 | 횡보 | High ≥ Up(SideBand) → T1: [MacdDown OR Dead], 또는 T2Bear |

T1 대기 상태 (int) — 전략의 화면 표시용. 매매 판단에는 쓰지 않는다

| 이름 | 의미 |
| --- | --- |
| WaitUpLong, WaitUpSusLong, WaitUpSusShort, WaitDnShort, WaitDnLong, WaitDnSusLong, WaitDnSusShort, WaitSideLong, WaitSideShort | 진입 신호별 대기 상태. −1 = 대기 없음, 0 = 밴드 조건이 충족된 봉, 1 이상 = 그 뒤 지난 봉 수 |
| T1BarsUp, T1BarsDn, T1BarsSide | 실제 대기 봉 수 = (T1WindowUp/Dn/Side 또는 T1Window) + MacdConfirmBars − 1 |

T1 처리 규칙 (spec 2장 T1)
- 한 봉에서 먼저 기존 대기로 신호를 판정하고, 그다음 그 봉이 밴드 조건 봉이면 새 t로 등록한다.
- T1SameBar가 켜져 있으면, 밴드 조건 봉에 반전 신호가 같이 있을 때 그 봉에서 바로 신호를 내고 대기는 만들지 않는다. 꺼져 있으면 t 다음 봉부터 센다.
- 대기 봉 수 안에 반전 신호가 없으면 대기는 소멸한다. 기본이면 5 + 1 = 6봉.
- 밴드 조건 1번당 진입 신호는 1번이다. 지표는 포지션을 모르므로, B가 보유 중이라 쓰지 못한 신호도 그 밴드 조건의 기회를 쓴 것으로 본다.
- 9개 진입 신호는 각자 따로 대기 상태를 가진다.

## B가 직접 계산하는 것
- 어느 진입 신호를 쓸지: 신호 봉의 `TQ_Regime.Regime` 값으로 고른다 (spec 5장 표).
- 단계별 익절 밴드와 수량 비율: 전략의 규칙 표 (`Plan`). 밴드 값은 TQ_ATRChannels에서 읽는다.
- 마지막 조각 조건: `Close[0]`과 `TQ_Signals.Sma[0]` 비교.
- 전량 청산 조건: MacdDown / Dead / T2Bull / T2Bear, 그리고 추세가 전환 의심으로 바뀌는지.
- 손절가: 신호 봉의 SwingLow5/SwingHigh5와 ±StopPercent% 중 한쪽(StopWider). 신호 봉 종가로 먼저 걸고 체결되면 실제 체결가로 다시 계산 (spec 4장).
