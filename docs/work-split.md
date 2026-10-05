# 작업 분담

## 0단계 — 같이 (반나절)
1. 저장소 세팅: `Documents\NinjaTrader 8\bin\Custom`을 git 저장소로 만들고 이 키트의 파일을 넣는다.
2. docs/interface.md를 같이 읽고 이름·의미에 합의한다.
3. A가 **TQ_Signals·TQ_Regime·TQ_ATRChannels 스켈레톤**을 먼저 커밋한다. 모든 속성이 존재하고 값은 false/NaN. B는 이걸로 컴파일하면서 작업한다.

## 담당 A — 신호 엔진 (`Indicators/TeamQuant/`)
1. TQ_ATRChannels: 밴드 플롯. TradingView 값과 같은 시점 비교.
2. TQ_Regime: 10분봉 레짐(+1/0/−1) + 10분 SMA20. 배경색으로 9/24~25 차트에서 회의 때 나눈 구간과 비교.
3. TQ_Signals 기본 이벤트: 돌파, MACD 전환, 필터된 크로스, %K 80/20, RSI, SwingHigh5/Low5.
4. TQ_Signals 복합: 하락 다이버전스, T2, T1 대기 상태, 6개 진입 신호.
5. 검증: 진입 신호 위치에 화살표(ShowDebugVisuals) 찍어서 명세와 맞는지 눈으로 확인.

## 담당 B — 전략·주문 (`Strategies/TeamQuant/`)
1. TQ_Strategy 골격: 10분봉 추가, 데이터 가드, 설정값 전체, 대회 시간 필터, 종료 청산, 진입 횟수 표시.
2. 진입 라우팅: 레짐별로 맞는 진입 신호 선택, 진입 시 레짐 저장, 분할 계획대로 시그널명 나눠 진입.
3. 손절·본절: 진입 봉 기준 손절가 계산, 1차 익절 후 평균 진입가로 이동, 포지션 없을 때 리셋.
4. 익절 상태 머신: 5.1(약한/강한 모멘텀), 5.2, 5.3, 5.4(3단계), 5.5·5.6(세트 A/B 중 먼저 충족).
5. 검증: UseTestEntry로 임시 진입을 넣고 Market Replay에서 분할 익절·본절·종료 청산이 맞는지 확인.

## 합치기
- 브랜치는 main 하나만 쓴다. 두 사람이 고치는 폴더가 겹치지 않으므로 따로 브랜치를 만들지 않는다.
- 작업을 시작하기 전과 푸시하기 전에 `git pull --rebase`를 한다.
- NinjaScript Editor에서 F5가 통과한 코드만 푸시한다. 깨진 코드가 올라가면 상대방 컴파일도 막힌다.
- docs/interface.md와 지표의 설정값은 합의한 뒤에 바꾼다. 지표 설정값이 바뀌면 전략의 호출부도 같은 커밋에서 고친다.
- A의 신호가 완성되면 B가 UseTestEntry를 끄고 실제 신호로 연결 → Strategy Analyzer 백테스트.
