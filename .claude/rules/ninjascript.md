# NinjaScript 공통 규칙

## 생명주기 (OnStateChange)
- SetDefaults: 이름, 기본값, Calculate 설정만. 데이터 접근 금지.
- Configure: AddDataSeries 등 데이터 요청. AddDataSeries는 여기서만 호출하고 인자는 하드코딩한다(변수 사용 금지).
- DataLoaded: 지표 인스턴스와 Series<T>를 여기서 만든다. 여기서부터 데이터 사용 가능.
- Historical: 과거 데이터 처리(백테스트). Realtime: 실시간 전환.
- Terminated: 정리 작업.

## OnBarUpdate
- 계산 로직은 전부 OnBarUpdate에 둔다.
- 이 프로젝트는 `Calculate.OnBarClose`를 쓴다 (spec 1장: 봉 마감 후 종가로 판단).
- 인덱스: [0] = 현재 봉(OnBarClose에서는 방금 마감된 봉), [1] = 그 전 봉. 봉이 완성될 때마다 기존 봉의 인덱스가 1씩 늘어난다.
- 데이터 부족 가드 필수: 참조하는 가장 먼 인덱스보다 CurrentBar가 작으면 return한다. 멀티 시리즈면 CurrentBars[i]를 모두 확인한다. 안 하면 불친절한 에러와 함께 스크립트가 멈춘다.
- 멀티 시리즈: BarsInProgress로 어느 시리즈가 업데이트됐는지 확인한다. 3분봉과 10분봉이 같은 시각에 닫히면 primary(3분)가 먼저 처리되고 10분 값은 그 뒤에 갱신된다. 그래서 3분 로직은 직전에 마감된 10분 값을 본다 (spec 3장과 일치).

## 내장 지표
- 스토캐스틱 인자 순서는 Stochastics(periodD, periodK, smooth). spec 설정 = `Stochastics(5, 10, 5)`. 값은 `.K`, `.D`.
- MACD 히스토그램 = `MACD(12, 26, 9).Diff`.
- RSI 값 = `RSI(14, 3)[0]` (두 번째 인자는 평균선용 smooth로 RSI 값에는 영향 없음).
- ATR Channels는 내장 지표가 없다. KeltnerChannel로 대체하지 말고 spec 2장대로 직접 구현한다.

## 시간
- `ToTime(Time[0])`은 HHmmss 형태의 정수를 준다 (예: 22:30:00 → 223000).
- 차트 시간은 NinjaTrader 시간대 설정을 따른다. 시작 전에 KST로 설정됐는지 확인한다.
- 대회 시간(22:30~00:30 KST)은 자정을 넘는다. 시간 비교 로직에서 날짜가 바뀌는 경우를 처리한다.
- 대회일은 미국 서머타임 기간이다. 시간 값은 하드코딩하지 말고 설정값으로 둔다.

## 디버깅
- `Print()` 출력은 NinjaScript Output 창에 나온다. 런타임 예외는 Control Center의 Log 탭에서 확인한다.
- NinjaTrader 디버깅은 까다롭다. 상태 전환, 신호, 주문마다 로그를 남기고 문제 구간을 로그로 좁힌다.

## 그리기 (필요할 때만)
- Draw.* 메서드는 OnBarUpdate에서 매 봉 호출돼 무겁다. 검증용으로만 쓰고 설정값으로 끌 수 있게 한다.
- 연속된 선과 배경색은 AddPlot과 BackBrush를 우선 쓴다.
- OnRender를 쓰는 경우:
  - OnRender 안에서 계산하지 않는다. 계산은 OnBarUpdate에서 끝낸다.
  - 브러시가 WPF(System.Windows.Media.Brush)와 SharpDX 두 종류다. 섞어 쓰면 타입 불일치 에러가 난다.
  - SharpDX 리소스는 다 쓴 뒤 Dispose한다. 안 하면 메모리 누수가 생긴다.
  - 직접 만든 WPF 브러시는 Freeze()한다.
  - 예전 렌더링 API(예: RawRectangleF 관련) 에러가 나면 추측하지 말고 공식 문서에서 현재 API를 확인한다.
