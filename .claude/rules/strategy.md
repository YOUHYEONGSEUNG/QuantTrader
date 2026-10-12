---
paths:
  - "Strategies/TeamQuant/**"
---

# 전략·주문 규칙 — 담당 B

2026-10-12에 spec v2(대면회의 이후 규칙)에 맞춰 고쳤다.

## 데이터
- Configure에서 `AddDataSeries(BarsPeriodType.Minute, 10)` → BarsArray[1], `AddDataSeries(BarsPeriodType.Minute, 3)` → BarsArray[2]. 추세는 `TQ_Regime(BarsArray[regimeBip], ...)`로 읽는다 (`RegimeBarMinutes`가 3이면 2, 아니면 1).
- CurrentBars[0], CurrentBars[regimeBip] 가드를 두고, `regime.Regime[0]`이 NaN이면 아무것도 하지 않는다.
- 진입·청산 판단과 주문은 `BarsInProgress == 0`(진입 봉)에서만 한다. 추세 봉 마감 때는 로그만 남긴다. 추세가 바뀐 것은 그 뒤 첫 진입 봉 마감에서 반영된다(최대 1봉 지연).

## 주문 (Managed approach)
- 분할 익절을 위해 진입을 시그널명으로 나눠 낸다. 규칙별 분할 계획은 `Plan()`에 있다 (spec 5장 표).
  - 시그널명은 규칙 접두어 + 단계 번호다 (예: 상승추세 매수 30계약 → "UpL1" 6, "UpL2" 12, "UpL3" 12). 마지막 번호가 마지막 조각이다.
  - `EntryHandling = UniqueEntries`, `EntriesPerDirection = 1`로 시그널명마다 한 번씩 진입한다.
- 손절은 진입 시그널별로 `SetStopLoss(signal, CalculationMode.Price, price, false)`. 진입 주문보다 먼저 건다.
- 단계별 밴드 익절은 `SetProfitTarget(signal, CalculationMode.Price, price)`로 건다 (`IntrabarTakeProfit`). 손절(SetStopLoss)과 같이 쓸 수 있는 지정가는 이것뿐이다. `ExitLongLimit` 같은 지정가·스탑 Exit 주문은 쓰지 않는다.
  - Set 계열의 값은 시그널명에 남는다. 마지막 조각의 시그널명에는 SetProfitTarget을 걸지 않으므로, 한 규칙 안에서 시그널명과 역할(밴드 단계인지 마지막 조각인지)을 바꾸지 않는다.
- 봉 마감 조건의 청산(마지막 조각, 전량 청산)은 시장가 `ExitLong/ExitShort`로 낸다.
- Set 계열을 쓰므로 주문은 첫 번째 시리즈(BIP 0)로만 낸다.
- 실시간·Playback에서는 체결 콜백(OnExecutionUpdate)이 주문 호출 도중에 올 수 있다. 콜백이 읽는 상태(`liveSignals`, `pendingBandExit` 등)는 주문을 내기 전에 바꿔 둔다.
- 손절 가격을 옮길 때는 `MoveStopTo()`만 쓴다. 현재 호가와의 거리(`BreakevenBufferTicks`)를 확인하고, 가까우면 미뤘다가 봉 마감마다 다시 시도한다. 스탑을 현재가 옆이나 반대편에 두면 주문이 거부된다.

## 상태 관리
- 진입할 때의 규칙(`active`)을 저장하고 청산이 끝날 때까지 그 규칙을 쓴다. 예외는 spec 1장 원칙 3의 둘이다(추세 방향 포지션의 의심 전환 정리, `AdoptTrendExits`).
- 추적할 상태: 조각별 수량과 익절 밴드, 체결된 밴드 단계 수(`tpCount`), 1차 익절 체결가, 손절가와 미뤄 둔 손절 이동.
- 첫 밴드 익절 체결 → 손절을 평균 진입가로. 그 체결가가 평균 진입가보다 유리하지 않으면 원래 손절가를 유지한다 (spec 4장).
- 두 번째 밴드 익절 체결 → 손절을 1차 익절 체결가로 (`TrailStopAtTp2`).
- 신규 진입은 신호 봉이 22:30~00:15일 때만 한다. 종료 청산은 00:27 봉이 마감되면 전량 시장가로 낸다. 두 시각 모두 설정값으로 둔다 (spec 7장).
- 종목별 진입 횟수를 세서 화면에 표시한다. 분할 진입 여러 건은 신호 1회로 센다 (spec 7장).

## 검증
- 주문 흐름 테스트용 임시 진입은 설정값 `UseTestEntry`로 분리하고, 기본값은 false.
- 화면의 왼쪽 위 상태 상자(`UpdateStatusPanel`)는 규칙이 바뀌면 같이 고친다. 추세와 그 근거, 진입 후보, 단계별 익절·마지막 조각·전량 청산 조건을 보여 준다.
