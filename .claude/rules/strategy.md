---
paths:
  - "Strategies/TeamQuant/**"
---

# 전략·주문 규칙 — 담당 B

2026-10-15 브랜치 `v3-integrated`에서 spec v3(v3 지시서)에 맞춰 고쳤다. v2 규칙은 `main`의 같은 파일에 있다.

## 원칙 (spec v3 2장, 4-1)
- **포지션은 4가지:** 추세 매수 TL, 추세 매도 TS, 횡보 매수 SL, 횡보 매도 SS (`enum Pos`). 역진입·역추세 진입 코드는 두지 않는다.
- **거울상:** 모든 규칙은 매수로 한 번만 정의하고 매도는 방향 부호 `dir`(+1 / −1)로 계산한다. 매수·매도 함수를 따로 쓰지 않는다. 밴드는 `Band(dir, n)`(n = 0 중심선), 비교는 `dir * (a - b) > 0` 꼴. 새 규칙을 넣을 때도 이 형태를 지킨다.
- 진입 경로는 이름(E1~E4)을 붙여 로그·상태 상자·학습 데이터에 남긴다. 청산은 고정 사유 코드(spec v3 6장)로 남긴다. 새 사유를 만들면 spec과 backtest-guide 9장에 같이 적는다.

## 데이터
- Configure에서 `AddDataSeries(BarsPeriodType.Minute, 10)` → BarsArray[1], `AddDataSeries(BarsPeriodType.Minute, 3)` → BarsArray[2]. 추세는 `TQ_Regime(BarsArray[regimeBip], ...)` (`RegimeBarMinutes` 10이면 1, 아니면 2).
- CurrentBars[0], CurrentBars[regimeBip] 가드를 두고, `regime.Regime[0]`이 NaN이면 아무것도 하지 않는다.
- 진입·청산 판단과 주문은 `BarsInProgress == 0`(진입 봉)에서만 한다. 추세 봉 마감 때는 로그만 남긴다(1분 지연).
- 추세 봉의 이벤트(`TrendStart`, `SuspectEvent`)는 새 추세 봉 값을 처음 본 진입 봉에서만 쓴다 (`ObserveRegime`, `trendStartNow`·`suspectEventNow`).

## 주문 (Managed approach)
- 분할 진입은 조각마다 시그널명이 하나다: 추세 `TL1`(1차) / `TL2`(2차) / `TL3`(마지막 조각), 횡보 `SL1`(1단계) / `SL2`(나머지). 매도는 TS·SS. `chunkStage`(1 / 2 / 3)로 역할을 구분한다.
- `EntryHandling = UniqueEntries`, `EntriesPerDirection = 1`.
- 손절은 조각별 `SetStopLoss(signal, CalculationMode.Price, price, false)`, 진입 주문보다 먼저 건다.
- 지정가 익절은 `SetProfitTarget`(`IntrabarTakeProfit`): 추세 조각은 밴드 값을 봉마다 옮기고, 횡보 1단계는 고정 가격(진입가 ± ATR × 배수, 체결되면 체결가 기준으로 다시 검). `ExitLongLimit` 같은 지정가 Exit 주문은 쓰지 않는다.
- **조각의 일부만 청산하지 않는다.** 일부 청산 때 그 조각의 Set 익절·손절 수량이 어떻게 되는지 공식 문서에서 확인되지 않았다. 의심 일부청산은 먼 조각부터 조각 단위로 뺀다(`ExitFarChunks`).
- 봉 마감 조건의 청산은 시장가 `ExitLong/ExitShort`. 주문은 BIP 0으로만.
- 체결 콜백이 주문 호출 도중에 올 수 있다. 콜백이 읽는 상태(`liveSignals`, `pendingBandExit`)는 주문 전에 바꿔 둔다.
- 손절 이동은 `MoveStopTo()`만 쓴다(유리한 쪽으로만, `BreakevenBufferTicks` 안전 거리, 가까우면 미뤘다가 재시도).

## 상태 관리
- 미보유가 되면 `ResetTradeState()`로 포지션·조각·손절·의심 상태를 지운다. 대회 구간 상태(진입 횟수, 장전 박스, E3 사용, 횡보 재진입 금지)와 추세 관찰 상태(`lastRegimeBar`, E1 재허용)는 지우지 않는다.
- 대회 구간은 진입 시간대에 들어서는 첫 봉에서 새로 시작한다(`UpdateSession`).
- 시간대 구분은 `ZoneNow()`(지표 발표 / 장전 / 개장 전후 / 장후)로 한다. 시각은 모두 설정값이고 자정을 넘는 경우도 처리한다(`RelMinutes`).

## 검증
- 주문 흐름 테스트용 임시 진입은 `UseTestEntry`(기본 false).
- 상태 상자(`UpdateStatusPanel`)는 규칙이 바뀌면 같이 고친다. 구역(현재 상태 / 진입 조건 / 포지션 / 익절 / 손절 / 청산)별로 `sb.Section(...)` 아래에 줄을 넣고, 그리기는 `OnRender`가 한다. 줄 색은 맨 앞 기호(●○×, 두 칸 들여쓰기)로 정해진다. 막힌 진입에는 사유를 수치와 함께 적는다(`EvaluateEntries`).
- 추세 판정 식을 바꾸면 `docs/tools/tq_regime_score.py`의 `compute_regime_v3`도 같이 고친다.
