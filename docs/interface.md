# 지표 ↔ 전략 인터페이스 (계약서)

담당 A(신호 엔진)는 아래 이름·타입·의미대로 값을 노출하고, 담당 B(전략)는 이것만 읽어서 주문을 낸다.
변경은 두 사람이 합의한 뒤 이 파일부터 고친다. 2026-10-12에 spec v2(대면회의 이후 규칙)에 맞춰 전체를 다시 썼고, 2026-10-15 브랜치 `v3-integrated`에서 TQ_Regime 절을 v3로 바꿨다(v3 지시서 3장).

## 원칙
- A는 "이 봉에서 이 조건이 참인가"만 계산한다. 포지션·주문·시간대와 무관한 값만 낸다.
- B는 포지션 상태에 따라 달라지는 모든 것(어느 진입 신호를 쓸지, 익절 단계, 손절, 수량, 대회 시간)을 처리한다.
- 모든 값은 진입 봉(1분봉) 마감 시점 기준이다 (TQ_Regime만 추세 봉 기준. v3 기본 3분봉).
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

## TQ_Regime (A) — 입력: 추세 봉 (전략에서 `TQ_Regime(BarsArray[regimeBip], ...)`)
v3 추세 판단 (spec v3 3장). 추세를 +1 상승 / 0 횡보 / −1 하락으로 내고, 의심은 상태가 아니라 이벤트로 낸다. 입력 시리즈는 전략이 정한다: `RegimeBarMinutes` 3이면 3분봉(기본, `BarsArray[2]`), 10이면 10분봉(`BarsArray[1]`).
모든 이동평균·MACD·RSI는 추세 봉 기준이다. MACD는 내장 `MACD(MacdFast, MacdSlow, MacdSmooth)`의 MACD선, RSI는 내장 `RSI(RsiPeriod, 3)`을 쓴다. 옛 Mode 1·2, 의심 RSI·다이버전스·스토캐스틱 판정은 없다.

| 설정값 (생성자 인자 순서) | 기본값 | 의미 |
| --- | --- | --- |
| Fast / Mid / Slow | 6 / 20 / 60 | 단기(F1) / 중기(S1, S2 방식 1) / 장기(S2) SMA |
| ShowDebugVisuals | false | 검증용 배경색: 상승 초록, 하락 빨강, 의심 중 주황(하락의심)·하늘색(상승의심) |
| MacdFast / MacdSlow / MacdSmooth | 12 / 26 / 9 | S3 MACD선 부호, F2 MACD선 기울기 |
| RsiPeriod | 14 | S4 |
| S2Mode | 1 | 1 = 중기선 > 장기선, 2 = 장기선이 SlopeBars봉 전보다 높음 |
| SlopeBars | 5 | S2 방식 2의 비교 봉 수 |
| UseRsi | true | S4 RSI 항목 사용 |
| RsiBand | 5 | S4: RSI ≥ 50 + 이 값이면 +1, ≤ 50 − 이 값이면 −1 |
| MacdSlopeBars | 2 | F2: MACD선이 이 봉 수 연속 커지면 +1, 작아지면 −1 |
| TrendStartFast / TrendStartSlow | 1 / 1 (2026-10-15에 2 / 2에서) | 추세 시작: F ≥ 앞의 값 AND S ≥ 뒤의 값 (하락은 거울상) |
| TrendEndSlow | 0 | 상승추세 끝: S ≤ 이 값 (하락추세 끝: S ≥ −이 값) |
| SuspectFast | 2 | 의심 이벤트: 상승추세 중 F ≤ −이 값이 된 첫 봉 (하락추세는 F ≥ 이 값) |
| DirectReverse | false | 추세가 끝나는 봉(S가 종료 기준에 닿음)에 F가 이미 반대로 ±2면 횡보 없이 반대 추세로 (2026-10-15 추가, 생성자 마지막 인자) |
| ShowPlots | false | 차트 표시 전용. 생성자 인자가 아니다 |

전략의 설정값 이름: `Fast/Mid/Slow` ↔ `RegimeFast/RegimeMid/RegimeSlow`, `S2Mode` ↔ `RegimeS2Mode`, `SlopeBars` ↔ `RegimeSlopeBars`, `UseRsi` ↔ `RegimeUseRsi`, `RsiBand` ↔ `RegimeRsiBand`, `MacdSlopeBars` ↔ `RegimeMacdSlopeBars`. `DirectReverse` ↔ `RegimeDirectReverse`. `TrendStartFast`·`TrendStartSlow`·`TrendEndSlow`·`SuspectFast`·MACD·RSI 기간은 같은 이름.

| 이름 | 타입 | 의미 |
| --- | --- | --- |
| Regime | 플롯 | +1 상승추세 / 0 횡보 / −1 하락추세. 데이터가 모자라면 NaN |
| SmaFast, SmaMid, SmaSlow | 플롯 | 세 SMA (표시·마지막 조각 청산·기록용) |
| MacdLine | 플롯 | 추세 봉 MACD선 |
| Rsi | 플롯 | 추세 봉 RSI |
| SlowScore, FastScore | Series&lt;double&gt; | S (−4 ~ +4, RSI를 끄면 −3 ~ +3), F (−2 ~ +2) |
| TrendStart | Series&lt;int&gt; | 이 봉에서 추세가 시작되면 ±1, 아니면 0 |
| SuspectEvent | Series&lt;int&gt; | 이 봉에서 의심이 생기면: 상승추세 중 하락의심 = −1, 하락추세 중 상승의심 = +1, 아니면 0 |
| SuspectActive | Series&lt;int&gt; | 의심 중이면 그 방향(−1 / +1), 아니면 0 |
| ScoreText | string | 상태 상자·로그용: "S1▲ S2▲ S3− S4▲ │ F1▼ F2▼" (RSI를 끄면 S4 없음) |

판정 (spec v3 3-1, 3-2, 추세 봉 마감마다):
- S1 = 부호(종가 − SMA Mid). S2 = 방식 1이면 부호(SMA Mid − SMA Slow), 방식 2면 부호(SMA Slow − SlopeBars봉 전 SMA Slow). S3 = 부호(MACD선). S4 = RSI ≥ 50 + RsiBand → +1, ≤ 50 − RsiBand → −1, 아니면 0.
- F1 = 종가 > SMA Fast AND SMA Fast > 직전 봉 → +1, 반대 → −1, 나머지 0. F2 = MACD선이 MacdSlopeBars봉 연속 커짐 → +1, 연속 작아짐 → −1.
- 상태: 추세 아님 → 시작 조건이면 그 방향 추세(TrendStart = ±1). 상승추세 → 하락 시작 조건이면 바로 하락추세, 아니고 S ≤ TrendEndSlow면 횡보. 하락은 거울상. 확정 연속 봉 수는 없다.
- 의심: 추세가 바뀌면 지운다. 추세 중 F × 추세 ≤ −SuspectFast가 된 첫 봉에 이벤트, F × 추세 ≥ 0이면 해제(이벤트 없음). 해제 뒤 다시 닿으면 다시 이벤트.
- 데이터 가드: CurrentBar < max(Slow − 1, MacdSlow + MacdSmooth, RsiPeriod, SlopeBars, MacdSlopeBars + 1)이면 Regime = NaN.
- 같은 식이 `docs/tools/tq_regime_score.py`의 `mode=3`(`compute_regime_v3`)에 있다. 한쪽을 고치면 같이 고친다.

## TQ_MacdTurn (A) — 입력: 진입 봉 또는 추세 봉
MACD 전환 판정을 한곳에서 한다. TQ_Signals가 내부에서 호출하고, 전략은 직접 호출하지 않는다 (v3의 TQ_Regime은 쓰지 않는다).
차트에 올리면 히스토그램 막대 색으로 방향을 볼 수 있다: 초록 = 연속 증가, 빨강 = 연속 감소, 회색 = 둘 다 아님. 회색·빨강에서 초록으로 바뀐 첫 봉이 상승 전환이다.

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
| T1WindowUp / T1WindowDn / T1WindowSide | 0 | 5.1~5.3 / 5.4~5.7 / 5.8·5.9의 T1 대기 봉 수. 0이면 T1Window. 전략은 2026-10-14부터 항상 0을 넘긴다(전략 설정에서 뺐음) |
| T2CountConfirmBars | true | T2에서 MACD 확인에 걸린 봉 수만큼 앞의 크로스도 인정 |
| T1SameBar | false | 켜면 밴드 조건이 충족된 봉 자체의 반전 신호도 인정 |
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

## B가 직접 계산하는 것 (v3 전략)
- TQ_Signals에서 쓰는 값: `EntryUpLong`·`EntryDnShort`(E2 추세 눌림, 생성자 `UpLongBand`·`DnShortBand` ← `TrendPullbackBand`), `EntrySideLong`·`EntrySideShort`(E4 횡보, `SideBand` ← `SideBand`), `T2Bull/T2Bear`, `MacdUp/MacdDown`, `Golden/Dead`, `SwingHigh5/SwingLow5`, `K/D/Rsi`, 화면용 `MacdRising/Falling`·`Wait…`·`T1Bars…`·`MacdHist`·`Sma`(기록용). 나머지 5개 진입 신호는 계산되지만 쓰지 않으며, 그 밴드 인자에는 main 기본값(2 / 1 / 3 / 30 / 1 / 2)을, `SmaPeriod`에는 18을, `T1WindowUp/Dn/Side`에는 0을, `T1SameBar`에는 false를 넘긴다.
- 포지션 종류(TL·TS·SL·SS)와 진입 경로(E1~E4): `TQ_Regime.Regime`·`TrendStart`·`SuspectActive`·`SlowScore`·`FastScore`와 위 진입 신호, 장전 박스(진입 봉 21:31~22:30 최고가·최저가)로 고른다 (spec v3 4-2).
- 단계별 익절: 추세 포지션은 TQ_ATRChannels 밴드(1차·2차), 횡보 1단계는 진입가 ± ATR × 배수(고정 가격). 마지막 조각 조건은 추세 봉 종가(`Closes[regimeBip][0]`)와 `TQ_Regime.SmaMid`/`SmaFast` 비교.
- 손절: 신호 봉의 `SwingLow5`/`SwingHigh5` 한 틱 바깥, ATR 범위와 최소 틱으로 자름. 신호 봉 종가로 먼저 걸고 체결되면 체결가로 다시 계산 (spec v3 4-3).
- 의심 일부청산: `TQ_Regime.SuspectEvent`(새 추세 봉 값을 처음 본 진입 봉에서) → 가격 확인(`SwingLow5[1]`/`SwingHigh5[1]`) → 조각 단위 청산 (spec v3 4-5).
- 지표는 추세·포지션·시간대를 모른 채 값만 낸다.
