---
paths:
  - "Strategies/TeamQuant/**"
---

# 전략·주문 규칙 — 담당 B

## 데이터
- Configure에서 `AddDataSeries(BarsPeriodType.Minute, 10)` → BarsArray[1]. 레짐은 `TQ_Regime(BarsArray[1])`로 읽는다.
- CurrentBars[0], CurrentBars[1] 가드를 둔다. 10분봉 SMA120 때문에 10분봉이 120개 이상 쌓이기 전에는 거래하지 않는다.
- 진입·청산 판단은 `BarsInProgress == 0`(3분봉)에서만 한다.
  - 예외: spec 5.2의 10분봉 익절 조건(10분봉 저가가 10분봉 SMA20에 도달)은 `BarsInProgress == 1`에서 10분봉 마감 즉시 판단한다. 다음 3분봉 마감을 기다리지 않는다.
  - 이때 주문은 BIP 0 대상으로 낸다. `ExitShort(0, qty, "TP1", "S1")`처럼 barsInProgressIndex를 0으로 지정한다.

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
- 청산 공통 규칙 (spec 5장, 5.1~5.6 모두 적용):
  - 1차 익절 전에 '나머지 청산' 조건이 먼저 충족되면 전량 청산한다.
  - 1차 익절 조건과 나머지 청산 조건이 같은 봉에서 동시에 충족되면 전량 청산한다.
  - 횡보(5.5·5.6)에서 1차 익절 전에 세트 A 또는 B의 나머지 청산 조건이 먼저 충족되면 전량 청산한다.
- 본절 예외 (spec 4장): 첫 부분 익절이 체결된 시점의 가격이 평균 진입가보다 불리하면(손실 중) 본절로 옮기지 않고 원래 손절가를 유지한다.
- 신규 진입은 신호 봉이 22:30~00:15일 때만 한다. 종료 청산은 00:27 봉이 마감되면 전량 시장가로 낸다. 두 시각 모두 설정값으로 둔다 (spec 7장).
- 종목별 진입 횟수를 세서 화면에 표시한다. 분할 진입 여러 건은 신호 1회로 센다 (spec 7장).

## 신호 엔진이 완성되기 전
- 담당 A가 먼저 올리는 TQ_Signals 스켈레톤(모든 값 false/NaN)으로 컴파일한다.
- 주문 흐름 테스트용 임시 진입 조건은 설정값 `UseTestEntry`로 분리하고, 기본값은 false.
