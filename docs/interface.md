# 지표 ↔ 전략 인터페이스 (계약서)

담당 A(신호 엔진)는 아래 이름·타입·의미대로 값을 노출하고, 담당 B(전략)는 이것만 읽어서 주문을 낸다.
변경은 두 사람이 합의한 뒤 이 파일부터 고친다.

## 원칙
- A는 "이 봉에서 이 조건이 참인가"만 계산한다. 포지션·주문·시간대와 무관한 값만 낸다.
- B는 포지션 상태에 따라 달라지는 모든 것(레짐 고정, 익절 단계, 손절, 수량, 대회 시간)을 처리한다.
- 모든 값은 3분봉 마감 시점 기준이다 (TQ_Regime만 10분봉).

## TQ_ATRChannels (A) — 입력: 3분봉
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

## TQ_Regime (A) — 입력: 10분봉 (전략에서 `TQ_Regime(BarsArray[1])`)
| 설정값 | 기본값 |
| --- | --- |
| Fast / Mid / Slow (SMA) | 20 / 60 / 120 |
| UseRule12 | true (spec 3장 1.2·2.2) |
| ShowDebugVisuals | false (검증용 배경색) |

| 플롯 | 의미 |
| --- | --- |
| Regime | +1 상승추세, −1 하락추세, 0 횡보 (spec 3장) |
| Sma20 | 10분봉 SMA20 (spec 5.2 1차 익절용) |

## TQ_MacdTurn (A) — 입력: 3분봉
MACD 전환 판정을 한곳에서 한다. TQ_Signals가 내부에서 호출하고, 전략은 직접 호출하지 않는다.
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
| Up, Down | Series&lt;bool&gt; | 히스토그램이 ConfirmBars봉 연속 증가 / 감소했고 그 구간의 변화폭이 MinChangeAtr × ATR 이상 |

## TQ_Signals (A) — 입력: 3분봉
| 설정값 | 기본값 |
| --- | --- |
| T1Window (N) | 3 |
| CrossFilterBars | 5 |
| CrossLow / CrossHigh | 20 / 80 |
| RsiPeriod | 14 |
| RsiHigh / RsiLow | 70 / 20 |
| DivFrom / DivTo | 5 / 30 |
| SwingBars | 5 |
| BandMidPeriod / BandAtrPeriod | 26 / 14 (TQ_ATRChannels의 MidPeriod / AtrPeriod와 같은 값) |
| BandMult1 / BandMult2 / BandMult3 | 1 / 2 / 3 (TQ_ATRChannels의 Mult1~3과 같은 값) |
| MacdFast / MacdSlow / MacdSmooth | 12 / 26 / 9 |
| StochPeriodK / StochSmooth / StochPeriodD | 10 / 5 / 5 (%K 길이 / %K 스무딩 / %D 스무딩) |
| SmaPeriod | 20 (3분봉, spec 5.1 강한 모멘텀 청산) |
| MacdConfirmBars | 2 (MACD 전환 확인 봉 수. TQ_MacdTurn의 ConfirmBars로 넘김) |
| MacdMinChangeAtr | 0 (MACD 전환 최소 변화폭, ATR 배수. TQ_MacdTurn의 MinChangeAtr로 넘김) |
| ShowDebugVisuals | false (검증용 화살표) |

설정값 타입: 기간·봉 수는 int, 기준값(CrossLow/High, RsiHigh/Low)과 배수는 double, UseRule12·ShowDebugVisuals는 bool.
전략은 TQ_ATRChannels와 TQ_Signals에 같은 밴드 설정값을 넘긴다.

숫자 값 (Series&lt;double&gt;)

| 이름 | 의미 |
| --- | --- |
| K, D | Stochastics(StochPeriodD, StochPeriodK, StochSmooth)의 %K, %D. 기본값 Stochastics(5, 10, 5) |
| Rsi | RSI(RsiPeriod). 기본값 RSI(14) |
| MacdHist | MACD(MacdFast, MacdSlow, MacdSmooth).Diff. 기본값 MACD(12, 26, 9) |
| Sma20 | 3분봉 SMA(SmaPeriod). 기본값 SMA20 (spec 5.1 강한 모멘텀 청산용) |
| SwingHigh5, SwingLow5 | 신호 봉 포함 최근 5봉의 최고 고가 / 최저 저가 (spec 2장 전고점·전저점). B는 신호 봉의 값을 읽는다 |

이벤트 (Series&lt;bool&gt;) — spec 2장 정의 그대로

| 이름 | 의미 |
| --- | --- |
| CrossAboveUp1, CrossAboveUp2, CrossAboveUp3 | 종가 상방 돌파 |
| CrossBelowDn1, CrossBelowDn2, CrossBelowDn3 | 종가 하방 돌파 |
| MacdUp, MacdDown | TQ_MacdTurn의 Up / Down 그대로 (spec 2장 MACD 전환). 진입·T2·익절 조건 모두 이 값을 쓴다 |
| Golden, Dead | 필터 적용된 골든·데드크로스 |
| K80CrossUp, K20CrossDown | %K 80 상향 돌파 / 20 하방 돌파 |
| BearDiv | 현재 봉 기준 하락 다이버전스 |
| T2Bull | MacdUp AND Golden (같은 봉 또는 연속 2봉, spec T2) |
| T2Bear | MacdDown AND Dead (같은 봉 또는 연속 2봉, spec T2) |

진입 신호 (Series&lt;bool&gt;) — 레짐과 무관하게 계산, B가 레짐에 맞는 것만 사용

| 이름 | spec | 조건 요약 |
| --- | --- | --- |
| EntryUpLong | 5.1 | CrossBelowDn1 → T1: [MacdUp OR Golden] |
| EntryUpShort | 5.2 | High ≥ Up3 AND Rsi ≥ 70 AND BearDiv → T1: [MacdDown OR Dead] |
| EntryDnLong | 5.3 | CrossBelowDn3 → T1: [Rsi < 20 OR MacdUp OR Golden] |
| EntryDnShort | 5.4 | CrossAboveUp2 → T1: [MacdDown OR Dead OR (Rsi ≥ 70 AND BearDiv)] |
| EntrySideLong | 5.5 | (CrossBelowDn2 또는 CrossBelowDn3) → T1: [MacdUp OR Golden], 또는 T2Bull |
| EntrySideShort | 5.6 | (CrossAboveUp2 또는 CrossAboveUp3) → T1: [MacdDown OR Dead], 또는 T2Bear |

T1 대기 상태 (int) — 전략의 화면 표시용. 매매 판단에는 쓰지 않는다

| 이름 | 의미 |
| --- | --- |
| WaitUpLong, WaitUpShort, WaitDnLong, WaitDnShort, WaitSideLong, WaitSideShort | 진입 신호별 대기 상태. −1 = 대기 없음, 0 = 밴드 조건이 충족된 봉, 1 이상 = 그 뒤 지난 봉 수 |
| T1Bars | 실제 대기 봉 수 (T1Window + MacdConfirmBars − 1) |

T1 처리 규칙 (spec 2장 T1)
- 밴드 조건이 충족된 봉(t) 다음 봉부터 대기 봉 수 안에서만 반전 신호를 본다. 봉 t 자체의 반전 신호는 세지 않는다.
- 대기 봉 수 = T1Window + (MacdConfirmBars − 1). 기본값이면 3 + 1 = 4봉.
- 밴드 조건 1번당 진입 신호는 1번이다. 신호가 나가면 그 대기는 소멸한다. 지표는 포지션을 모르므로, B가 보유 중이라 쓰지 못한 신호도 그 밴드 조건의 기회를 쓴 것으로 본다.
- 대기 중에 같은 밴드 조건이 새로 충족되면 그 봉을 새 t로 보고 다시 센다.
- 신호가 나온 봉이 동시에 새 밴드 조건 봉이면, 먼저 기존 대기로 신호를 판정하고 그다음 그 봉을 새 t로 등록한다(다음 봉부터 센다).
- 6개 진입 신호는 각자 따로 대기 상태를 가진다.
- 한 봉에서 여러 밴드를 동시에 돌파해도 진입 신호는 한 번만 낸다 (spec T3).

## B가 직접 계산하는 것
- 익절용 밴드 도달·터치 비교 (예: `Close[0] >= Up2[0]`, `High[0] >= Up3[0]`)
- 10분봉 저가의 10분봉 SMA20 도달: `Lows[1][0] <= TQ_Regime(BarsArray[1]).Sma20[0]`, 10분봉 마감 시 확인
- 손절가: 신호 봉의 SwingLow5/SwingHigh5와 ±0.2% 중 진입가에 가까운 쪽. 신호 봉 종가로 먼저 걸고 체결되면 실제 체결가로 다시 계산. 갭으로 체결가가 이미 손절가를 넘어섰으면 즉시 전량 청산 (spec 4장)
