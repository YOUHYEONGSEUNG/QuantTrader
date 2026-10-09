# 대회(실거래) 세팅 체크리스트

작성일 2026-10-09. 백테스트·Playback과 실거래에서 달라지는 것, 대회 당일에 맞춰야 하는 것을 모았다. 전략 설정값의 뜻은 `docs/backtest-guide.md` 5장, 운용 기본값은 `docs/spec.md` 8장에 있다.

NinjaTrader 동작에 관한 내용은 공식 도움말(문서 끝의 출처)에서 확인한 것이다. "확인 필요"라고 적은 것은 문서로 확정하지 못했거나 우리 계정에서 직접 해 봐야 하는 것이다.

## 1. 백테스트·Playback과 실거래의 차이

| 항목 | Strategy Analyzer | Playback | 실거래(대회) |
| --- | --- | --- | --- |
| 연결 | Playback 연결을 끊어야 Run이 눌림 | Playback Connection | 대회 계정의 실시간 연결 |
| Account | 없음 | Playback101 | **대회 계정** |
| Enabled 체크 | 없음 (Run 버튼) | 체크해야 동작 | 체크해야 동작. **주문이 실제로 나간다** |
| 체결 | 봉 단위 가상 체결 | 틱 재생 | 실제 체결. 미끄러짐과 부분 체결이 있음 |
| Order fill resolution, Slippage, 수수료 | 여기서만 쓰임 | 쓰이지 않음 | 쓰이지 않음 |
| 로그 파일 (`LogToFile`) | 저장 안 함 | 저장 | 저장 |
| 거래 요약 파일 (`TradeLogToFile`) | 저장 | 저장 | 저장 |

체결 통보가 주문 도중에 오는 것은 실시간(Playback·실거래)에서만 일어난다. 10/8에 고친 버그 두 개가 여기서 나왔다. **새 규칙은 백테스트만 보지 말고 Playback으로도 한 번 돌려 본다.**

## 2. 전략 창(Strategies)의 NinjaTrader 공통 설정

차트 우클릭 → Strategies에서 `TQ_Strategy`를 고르면, 우리 설정값 아래에 NinjaTrader 공통 항목이 있다.

| 항목 | 넣을 값 | 이유 |
| --- | --- | --- |
| Account | 대회 계정 | Sim101이나 Playback101이면 실제 주문이 안 나간다 |
| Calculate | On bar close | 코드가 정한 값. 바꾸지 않는다 (명세 1장: 봉 마감 후 판단) |
| Start behavior | **Wait until flat** (기본값) | 아래 3장 참고 |
| Enabled | 체크 | 체크해야 전략이 돈다 |
| Entries per direction / Entry handling | 1 / Unique entries | 코드가 정한 값. 분할 진입에 필요하다. 바꾸지 않는다 |
| Stop & target submission | Per entry execution (기본값) | 분할 진입마다 손절이 따로 걸린다 |
| Set order quantity | Strategy | 수량은 우리 설정값 "진입 계약 수"로 정한다 |
| Time in force | GTC | 손절 주문이 중간에 만료되지 않게 한다 |
| Exit on session close | 켬 (기본값) | 대회 시간에는 세션 마감이 없어 영향 없다. 종료 청산은 우리 설정값 "전량 청산 시각"이 한다 |
| Bars required to trade | 20 (기본값) | 전략이 따로 더 긴 데이터 조건을 갖고 있다 (4장) |
| Maximum bars look back | 256 (기본값) | 그대로 둔다 |
| Order fill resolution, Slippage | 무관 | 백테스트 전용 |

## 3. Start behavior와 전략 상태 색

전략을 켜면 NinjaTrader는 먼저 과거 봉으로 전략을 돌려 "지금 전략이 포지션을 들고 있어야 하는가"(전략 포지션)를 계산한다. 이것과 실제 계좌 포지션이 다를 수 있다.

- **Wait until flat (기본값, 이것을 쓴다):** 전략 포지션이 없으면 바로 실제 주문을 낸다. 전략 포지션이 있으면, 가상으로만 거래하다가 그 포지션이 끝난 뒤부터 실제 주문을 낸다.
- **Synchronize account가 붙은 것:** 계좌와 전략을 맞추려고 NinjaTrader가 **시장가 주문을 알아서 낸다.** 쓰지 않는다.
- **Immediately submit:** 계좌와 전략이 이미 같다고 가정하고 바로 주문을 낸다. 쓰지 않는다.

Control Center의 Strategies 탭에서 전략 줄의 색으로 상태를 본다.

- **초록:** 정상 실행 중.
- **주황:** 전략 포지션이 끝나기를 기다리는 중. 이 동안에는 실제 주문이 나가지 않는다.

우리 전략은 대회 시간(22:30~00:27)에만 포지션을 갖는다. 그래서 **22:30보다 충분히 먼저 켜면 전략 포지션이 없어 바로 초록이 된다.** 대회 도중(포지션이 있을 시각)에 켜거나 다시 켜면 주황이 될 수 있고, 그 가상 포지션이 끝날 때까지 진입하지 않는다.

## 4. 차트와 데이터

| 항목 | 넣을 값 | 이유 |
| --- | --- | --- |
| 종목 | 그 시점의 주력 월물 (10월 말이면 `MNQ 12-26`, 골드는 `MGC 12-26`) | 월물이 다르면 데이터가 끊긴다. 대회 측 지정 월물을 확인한다 |
| 봉 종류 | 팀이 정한 것 (운용 기본값은 1분봉) | 전략은 차트의 봉을 그대로 쓴다 |
| Days to load | 3 이상 | 10분봉이 "이동평균 장기" 봉 수만큼 있어야 전략이 시작한다. 36이면 6시간, 120이면 20시간치 |
| Trading hours | 종목 기본값 (ETH) | 전체 세션이어야 봉이 이어진다 (명세 2장 T3) |
| 시간대 | Tools → Options → General의 Time zone이 한국시간 | 대회 시각 설정값이 차트 시간 기준이다 |

- 종목마다 차트를 하나씩 열고 전략을 하나씩 올린다 (MNQ 차트 하나, MGC 차트 하나). 같은 종목에 전략을 두 개 올리지 않는다.
- 전략을 켠 뒤 왼쪽 위 상태 상자와 배경색이 나오는지, Output 창에 `레짐=` 줄이 찍히는지 확인한다. 안 찍히면 데이터가 모자란 것이다.

## 5. 우리 설정값 중 대회 날 꼭 볼 것

| 설정값 | 값 | 비고 |
| --- | --- | --- |
| 주문 테스트용 가짜 진입 (`UseTestEntry`) | **끔** | 켜져 있으면 신호와 무관하게 계속 매수한다 |
| 진입 계약 수 (`EntryQuantity`) | 대회 한도 안 | 한도가 정해지면 넣는다 (명세 9장 미정). 종목별로 따로 |
| 대회 한도 계약 수 (`MaxQuantity`) | 대회 한도 | 진입 계약 수가 더 크면 시작할 때 경고가 찍힌다 |
| 진입 시작 / 진입 마감 / 전량 청산 시각 | 223000 / 1500 / 2700 | 대회 시간이 바뀌면 이 셋만 고친다. 검증하느라 넓혀 놨으면 되돌린다 |
| 최소 진입 목표 횟수 (`MinEntries`) | 종목별 최소 횟수 | 화면 표시용 |
| 차트에 상태 표시, 로그 저장, 거래 요약 저장 | 켬 | 대회 뒤 복기에 쓴다 |
| 나머지 | 팀이 확정한 값 | 설정창에서 하나씩 대조한다. 로그 파일 첫 줄의 설정 요약으로도 확인할 수 있다 |

## 6. 대회 중 하면 안 되는 것

- **전략이 낸 주문을 손으로 취소하지 않는다.** 전략이 멈출 수 있다. 멈추려면 전략을 끈다.
- **차트나 주문 창의 Close 버튼으로 포지션을 닫지 않는다.** 그 종목의 전략이 꺼진다. 정말 급하면 닫되, 전략이 꺼진다는 것을 알고 한다.
- **같은 계정·같은 종목에서 손으로 주문하지 않는다.** 전략 포지션과 계좌 포지션이 어긋난다.
- **NinjaScript를 컴파일하거나, 차트를 새로고침하거나, 차트의 종목·봉 종류를 바꾸지 않는다.** 전략이 꺼지거나 처음부터 다시 계산될 수 있다.
- 전략을 올린 차트와 워크스페이스를 닫지 않는다. NinjaTrader를 다시 켜면 전략은 꺼진 상태로 뜬다. Enabled를 다시 체크해야 한다.

## 7. 연결이 끊기거나 전략이 꺼졌을 때

- Tools → Options → Strategies에 "On connection loss" 설정이 있다 (Keep running / Recalculate / Stop strategy). **확인 필요:** 지금 값이 무엇인지 보고, 어느 쪽으로 둘지 정한 뒤 Sim101에서 연결을 끊어 시험한다. Recalculate는 다시 연결될 때 전략을 처음부터 계산하므로 3장의 주황 상태가 될 수 있다.
- 같은 화면의 "Cancel exit orders when a strategy is disabled"가 켜져 있으면, **전략이 꺼질 때 손절 주문도 같이 취소된다.** 포지션이 있는 채로 전략이 꺼지면 손절 없는 포지션이 남는다. 이 설정을 확인하고, 전략이 꺼지면 바로 계좌 포지션과 걸린 주문을 본다.
- **주문 거부.** NinjaTrader 기본 동작은 주문이 거부되면 걸린 주문을 취소하고 포지션을 닫으려 한 뒤 전략을 끄는 것이다. 10/9 Playback에서 손절을 현재가 위로 옮기려다 거부돼 실제로 꺼졌다. 지금은 설정값 "주문 오류를 전략이 직접 처리 (`HandleOrderErrors`)"가 기본으로 켜져 있어 전략이 꺼지지 않고 대처한다(손절 가격 변경 거부 → 기존 손절 유지, 손절 주문 거부 → 즉시 전량 청산). 다만 이 경로는 실제 거부가 나야 실행돼 충분히 시험되지 않았다. 대회 중 Control Center의 Log 탭에 빨간 줄이 뜨거나 로그 파일에 "주문 오류"가 찍히면, 바로 Strategies 탭에서 전략이 초록인지와 계좌의 포지션·손절 주문을 본다.
- 전략이 꺼졌는데 포지션이 남아 있으면: 손으로 정리한 뒤, 계좌에 포지션과 주문이 없는 것을 확인하고 전략을 다시 켠다.

## 8. 당일 순서

1. (미리) 확정된 코드를 pull 받고 F5로 컴파일한다. 대회 중에는 컴파일하지 않는다.
2. (미리) 절전·자동 업데이트·자동 재시작을 끈다. 유선 인터넷을 쓴다.
3. 21:30쯤 NinjaTrader를 켜고 대회 계정에 연결한다. 계좌에 포지션과 걸린 주문이 없는지 본다.
4. 시간대가 한국시간인지 본다.
5. 종목별 차트를 열고 4장대로 맞춘다.
6. 전략을 올리고 2장·5장대로 맞춘 뒤 Enabled를 체크한다.
7. Strategies 탭에서 **초록**인지, 차트에 상태 상자와 배경색이 나오는지, Output 창에 `레짐=` 줄이 찍히는지 본다.
8. 22:30 전까지 상태 상자가 "× 진입 시간 아님"인 것이 정상이다.
9. 00:27 봉이 마감되면 전량 청산된다. 00:30에 계좌에 포지션과 주문이 없는지 본다.
10. 전략을 끄고, `내 문서\NinjaTrader 8\`의 `TQ_log_종목.txt`와 `TQ_trades_종목.csv`를 따로 보관한다.

## 9. 대회 전에 해 볼 것

- **Sim101로 실시간 리허설.** 대회와 같은 시간대(22:30~00:30)에 실시간 데이터와 Sim101 계정으로 위 순서를 그대로 해 본다. Playback과 달리 실제 시각으로 돌아서 시간대 설정과 데이터 로딩이 맞는지 확인된다.
- 골드(MGC)는 아직 한 번도 돌리지 않았다. 같은 순서로 먼저 본다.
- 7장의 두 설정을 확인하고 연결 끊김을 시험한다.

## 출처

- Running a NinjaScript Strategy from a Chart: https://ninjatrader.com/support/helpGuides/nt8/running_a_ninjascript_strategy.htm
- Syncing Account Positions (Start behavior): https://ninjatrader.com/support/helpGuides/nt8/syncing_account_positions.htm
- Strategies Tab (전략 상태 색): https://ninjatrader.com/support/helpguides/nt8/strategies_tab2.htm
- Options → Strategies: https://ninjatrader.com/support/helpGuides/nt8/options_strategies.htm
- StartBehavior: https://ninjatrader.com/support/helpGuides/nt8/startbehavior.htm
