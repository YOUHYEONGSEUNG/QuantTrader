---
paths:
  - "Strategies/TeamQuant/**"
---

# 전략·주문 규칙 — 담당 B

2026-10-15 브랜치 `v3-integrated`에서 spec v3(v3 지시서)에 맞춰 고쳤다. v2 규칙은 `main`의 같은 파일에 있다.

## 원칙 (spec v3 2장, 4-1)
- **포지션은 4가지:** 추세 매수 TL, 추세 매도 TS, 횡보 매수 SL, 횡보 매도 SS (`enum Pos`). 역진입·역추세 진입 코드는 두지 않는다.
- **거울상:** 모든 규칙은 매수로 한 번만 정의하고 매도는 방향 부호 `dir`(+1 / −1)로 계산한다. 매수·매도 함수를 따로 쓰지 않는다. 밴드는 `Band(dir, n)`(n = 0 중심선), 비교는 `dir * (a - b) > 0` 꼴. 새 규칙을 넣을 때도 이 형태를 지킨다.
- 진입 경로는 이름(E1~E4, v4 브랜치는 B1 돌파 추가)을 붙여 로그·상태 상자·학습 데이터에 남긴다. 청산은 고정 사유 코드(spec v3 6장)로 남긴다. 새 사유를 만들면 spec과 backtest-guide 9장에 같이 적는다.

## 데이터
- Configure에서 `AddDataSeries(BarsPeriodType.Minute, 3)` → BarsArray[1](`regimeBip` = 1, v6 정리에서 10분봉을 뺐음). 추세는 `TQ_Regime(BarsArray[regimeBip], ...)`.
- CurrentBars[0], CurrentBars[regimeBip] 가드를 두고, `regime.Regime[0]`이 NaN이면 아무것도 하지 않는다.
- 현재가 청산(손절 안전장치, B1 반대 돌파)은 `OnMarketData`(Last, 실시간만) → `TickExits(px)`. 여기서 쓰는 1분봉 값은 직전 마감 때 필드에 담아 둔 것(`tk…`). 주문은 BIP 0으로. **1틱 데이터 시리즈는 쓰지 않는다:** 불러온 며칠 치 틱을 전부 처리해 Playback 이동이 멈춘 듯 느려졌음(2026-10-16).
- 과거 데이터(`State.Historical`)에서는 매 봉 로그 줄을 찍지 않는다(Output 창이 느려짐).
- 진입·청산 판단과 주문은 `BarsInProgress == 0`(진입 봉)에서만 한다(현재가 청산 예외). 추세 봉 마감 때는 로그만 남긴다(1분 지연).
- 추세 봉의 이벤트(`TrendStart`, `SuspectEvent`)는 새 추세 봉 값을 처음 본 진입 봉에서만 쓴다 (`ObserveRegime`, `trendStartNow`·`suspectEventNow`).

## 주문 (Unmanaged approach, 2026-10-22)
- `IsUnmanaged = true`, `IgnoreOverfill = true`(SetDefaults). 관리형 메서드(EnterLong, SetStopLoss, ExitLong …)는 섞어 쓸 수 없다(NT8 문서). 관리형 Set 손절·익절은 부분 체결 때 조각마다 주문이 여러 개 생기고, 한 조각을 청산하면 NinjaTrader가 다른 조각의 손절·익절을 취소해 손절 없는 계약과 남는 손절이 생겼다(10/15 22:06, 7/24 22:35).
- 주문 구조: 진입 주문 1개(조각 합계, `TL{n}.E`), 보호 손절 1개(`TL{n}.S`, 실제 보유 수량 전체), 조각별 익절 지정가(`TL{n}.1T` …, 그 조각 남은 수량), 시장가 청산(`X{k}`), 계획에 없는 포지션 정리(`Fix{k}`). 손절과 익절은 OCO로 묶지 않는다.
- **`Reconcile()`이 실제 보유 수량에 맞춘다.** 체결(OnExecutionUpdate)·포지션 변화(OnPositionUpdate)·주문 상태 변화(OnOrderUpdate)·봉 마감마다 부른다. 손절 수량 = 보유 − 아직 체결 안 된 시장가 청산(`plannedExitQty`), 익절은 단계 순서로 그 수량 안에서. 미보유면 손절·익절 취소, 방향이 다른 포지션이면 시장가 정리. 안에서 다시 불리면 끝난 뒤 한 번 더(재진입 막기).
- 조각은 기록만(`SubmitChunk`): 진입 체결은 조각 순서(1차 → 2차 → 마지막)로 나누고(`AllocateEntry`), 청산 체결은 그 주문의 조각 먼저, 나머지는 먼 조각부터(추가 → 마지막 → 2차 → 1차) 뺀다(`ReduceChunks`).
- 주문 객체는 OnOrderUpdate에서 이름으로 잡는다(제출 직후 돌려받은 참조가 아직 안 채워질 수 있음). 실시간 전환 때 `GetRealtimeOrder`로 바꾼다.
- 바꾸는 중(Submitted, ChangePending/Submitted, Cancel…)인 주문은 다시 고치지 않는다. 접수되면 OnOrderUpdate → Reconcile이 다시 맞춘다.
- 시장가 청산은 `SubmitMarketExit`: 먼저 계획 수량에 넣고 Reconcile로 손절 수량을 줄인 뒤 낸다(같이 체결돼 넘치지 않게). 취소·거부되면 계획에서 빼고 손절이 다시 덮는다.
- 익절 체결은 조각 수량을 먼저 줄인 뒤 단계 처리(손절 이동)를 한다. 거꾸로 하면 Reconcile이 익절을 다시 낸다.
- E3 스탑 진입은 같은 방향이면 걸어 둔 주문을 `ChangeOrder`로 고친다. 비관리형 주문은 취소할 때까지 살아 있어, 다시 걸지 않는 봉에는 `ForgetE3Stop`이 직접 취소한다. 시장가 진입 전에는 `CancelE3Stop`.
- 손절 이동은 `MoveStopTo()`만 쓴다(유리한 쪽으로만, `BreakevenBufferTicks` 안전 거리, 가까우면 미뤘다가 봉 마감마다 재시도). 실제 주문 반영은 Reconcile.
- 안전장치(봉 마감·현재가)는 손절 주문이 덮지 못하는 수량(`UnprotectedQty`)이 있을 때만 청산한다(정상 손절과 겹쳐 넘치지 않게).

## 그리기 (2026-10-22 사용자 규칙)
- 계산은 전부 OnBarUpdate, OnRender에는 계산을 넣지 않는다. 연속된 선은 OnRender로 차트 끝에서 끝까지, 봉에 붙는 값은 BackBrush·AddPlot.
- `Draw.*`는 쓰지 않는다(봉마다 그리면 무거움). 장전 박스 선·손절선·평단선·체결 표시·진입 횟수는 OnBarUpdate·체결 때 값만 기록하고(`boxLines`, `marks`, `renderStop`, `entryCountText`, `renderLock`으로 잠금) `RenderChartMarks`가 그린다. SharpDX 자원은 그릴 때 만들고 Dispose.

## 상태 관리
- 미보유가 되면 `ResetTradeState()`로 포지션·조각·손절·의심 상태를 지운다. 대회 구간 상태(진입 횟수, 장전 박스, E3 사용, 횡보 재진입 금지)와 추세 관찰 상태(`lastRegimeBar`, E1 재허용)는 지우지 않는다.
- 대회 구간은 진입 시간대에 들어서는 첫 봉에서 새로 시작한다(`UpdateSession`).
- 시간대 구분은 `ZoneNow()`(지표 발표 / 장전 / 개장 전후 / 장후)로 한다. 시각은 모두 설정값이고 자정을 넘는 경우도 처리한다(`RelMinutes`).

## 검증
- 설정창은 정말 조절할 값만 둔다(v6 정리, 사용자 결정). 나머지는 `#region 고정값`의 `private const`. 새 설정을 늘리기 전에 고정값으로 충분한지 먼저 본다.
- 상태 상자(`UpdateStatusPanel`)는 규칙이 바뀌면 같이 고친다. 구역(현재 상태 / 진입 조건 / 포지션 / 익절 / 손절 / 청산)별로 `sb.Section(...)` 아래에 줄을 넣고, 그리기는 `OnRender`가 한다. 줄 색은 맨 앞 기호(●○×, 두 칸 들여쓰기)로 정해진다. 막힌 진입에는 사유를 수치와 함께 적는다(`EvaluateEntries`).
- 추세 판정 식을 바꾸면 `docs/tools/tq_regime_score.py`의 `compute_regime_v3`도 같이 고친다.
