---
paths:
  - "Strategies/TeamQuant/**"
---

# 전략·주문 규칙 — 담당 B

## 데이터
- Configure에서 `AddDataSeries(BarsPeriodType.Minute, 10)` → BarsArray[1]. 레짐은 `TQ_Regime(BarsArray[1])`로 읽는다.
- CurrentBars[0], CurrentBars[1] 가드를 둔다. 10분봉 SMA120 때문에 10분봉이 120개 이상 쌓이기 전에는 거래하지 않는다.
- 진입·청산 판단은 `BarsInProgress == 0`(3분봉)에서만 한다.

## 주문 (Managed approach)
- 분할 익절을 위해 진입을 시그널명으로 나눠 낸다. 진입 시점 레짐의 분할 계획을 따른다.
  - 예: 상승추세 롱 30계약 → "L1" 6, "L2" 12, "L3" 12. 청산은 `ExitLong(qty, "TP1", "L1")`처럼 fromEntrySignal을 지정한다.
  - `EntryHandling = UniqueEntries`, `EntriesPerDirection = 1`로 시그널명마다 한 번씩 진입한다.
- 손절은 진입 시그널별로 `SetStopLoss(signal, CalculationMode.Price, price, false)`. 본절 이동도 같은 방식이고 기준은 `Position.AveragePrice`.
- 포지션이 없을 때 SetStopLoss 값을 반드시 리셋한다. 안 하면 직전 값이 다음 진입에 적용된다.
- Set() 계열을 쓰므로 주문은 첫 번째 시리즈(BIP 0)로만 낸다.
- 손절 주문이 걸려 있으면 반대 방향 진입이 무시된다. spec상 보유 중 새 신호는 무시하므로 반전 로직은 만들지 않는다.
- 손절 주문이 걸려 있으면 지정가·스탑 형태의 Exit 주문이 무시된다. 익절은 시장가 ExitLong/ExitShort로만 낸다.

## 상태 관리
- 진입할 때 레짐을 저장하고 청산이 끝날 때까지 그 레짐의 규칙을 쓴다 (spec 1장 3).
- 추적할 상태: 1차 익절 여부, 강한/약한 모멘텀(5.1), 횡보 익절 세트 A/B 선택(5.5, 5.6), 남은 수량.
- 대회 시간 밖에서는 신규 진입하지 않고, 종료 시각에 전량 청산한다 (spec 7장).
- 종목별 진입 횟수를 세서 화면에 표시한다.

## 신호 엔진이 완성되기 전
- 담당 A가 먼저 올리는 TQ_Signals 스켈레톤(모든 값 false/NaN)으로 컴파일한다.
- 주문 흐름 테스트용 임시 진입 조건은 설정값 `UseTestEntry`로 분리하고, 기본값은 false.
