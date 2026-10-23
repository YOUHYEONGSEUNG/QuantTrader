#region Using declarations
using System;
using System.Collections.Generic;	// 보유 시그널·수량 추적 (task3·4)
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;					// Brush, Brushes (화면 표시)
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;						// DashStyleHelper
using NinjaTrader.Gui.Chart;				// ChartControl, ChartScale (OnRender)
using NinjaTrader.Gui.Tools;				// SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;	// (그리기는 OnRender로 옮김. 기본 스크립트와 같은 using 유지)
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.TeamQuant;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.TeamQuant
{
	/// <summary>
	/// 대회 전략 (docs/spec.md). 3분봉 차트에 올리고 10분봉은 내부에서 추가한다.
	/// 진입 라우팅, 손절·본절, 익절 상태 머신, 대회 시간 처리를 담당한다.
	/// </summary>
	public class TQ_Strategy : Strategy
	{
		private TQ_ATRChannels	channels;	// 3분봉 밴드 (spec 2장)
		private TQ_Regime		regime;		// 10분봉 레짐 (spec 3장)
		private TQ_Signals		signals;	// 3분봉 신호 (spec 2장·5장)

		// spec 7장: 종목별 진입 횟수 (분할 진입 여러 건은 신호 1회). 미보유→보유 전환 때 1 증가
		private int				entryCount	= 0;
		private MarketPosition	lastPosition = MarketPosition.Flat;

		// spec 1장 3: 진입 시점 레짐 고정 + 어느 세부 전략(5.x)으로 진입했는지. 청산이 끝날 때까지 유지
		private int				entryRegime	= 0;
		private ActiveStrategy	active		= ActiveStrategy.None;
		private enum ActiveStrategy { None, UpLong, UpShort, DnLong, DnShort, SideLong, SideShort, Breakout, Force }

		// spec 4장 손절·본절 상태 (task3)
		private bool			isLongPos;						// 현재 포지션 방향 (롱/숏)
		private double			stopSwing		= double.NaN;	// 신호 봉의 전저점(롱)/전고점(숏)
		private double			stopPrice		= double.NaN;	// 현재 걸어둔 손절가 (로그·갭 판정용)
		private double			initialStop		= double.NaN;	// 신호 봉 종가 기준 최초 손절가 (갭 판정용)
		private double			entryFillSum;					// 진입 체결가 × 수량 합 (평균 체결가 계산용)
		private int				entryFillQty;					// 진입 체결 수량 합
		private bool			gapFlattened;					// 갭으로 손절가를 넘어 체결돼 즉시 청산 중
		private bool			breakevenPending;				// 첫 부분 익절 주문을 냈고 체결을 기다리는 중
		private bool			wasInEntryWindow;				// 진입 횟수 리셋용: 직전 봉이 진입 시간대였는지
		private bool			blockLong;						// 이번 대회 구간에서 롱이 손실 손절로 끝남 → 롱 재진입 금지
		private bool			blockShort;						// 이번 대회 구간에서 숏이 손실 손절로 끝남 → 숏 재진입 금지
		private double			initialRisk		= double.NaN;	// 체결 직후 손절 거리 (1R)
		private bool			reachedR;						// 최대 유리폭이 이익 보호 기준에 닿았음
		private int				regimeBip		= 1;			// 추세를 판단하는 시리즈 번호: 1 = 10분봉, 2 = 3분봉
		private bool			breakevenDeferred;			// 본절 이동을 현재가가 가까워 미뤄 둠 (봉 마감마다 재시도)
		private string			logPath;					// LogToFile용 로그 파일 경로
		private string			tradePath;						// TradeLogToFile용 거래 요약 파일 경로
		private bool			trOpen;							// 거래 요약: 기록 중인 거래가 있음
		private bool			trHeaderWritten;				// 거래 요약: 이번 실행의 설정 줄을 썼음
		private DateTime		trEntryTime;
		private DateTime		trExitTime;
		private string			trName			= "";
		private int				trRegime;
		private double			trStop0			= double.NaN;	// 체결 직후 손절가
		private double			trPnlPts;						// 실현 손익 합 (포인트 × 수량)
		private double			trMfe;							// 최대 유리폭 (포인트)
		private double			trMae;							// 최대 불리폭 (포인트)
		private int				trBars;
		private string			trExits			= "";
		private int				tradeQty;						// 이번 진입의 총 수량 (역추세는 CounterQtyPercent 적용)
		private string			mlPath;							// MlLogToFile용 학습 데이터 파일 경로
		private string			mlRunId			= "";			// 학습 데이터: 이번 실행 ID (같은 백테스트를 여러 번 돌린 중복 제거용)
		private string			mlSettingsId	= "";			// 학습 데이터: 설정 요약의 해시 (설정별로 나눠 보기용)
		private bool			mlHeaderWritten;				// 학습 데이터: 이번 실행의 설정 줄을 썼음
		private string			mlFeatures		= "";			// 학습 데이터: 신호 봉에서 잰 특징값 (CSV 조각)
		private double			mlPrevR			= double.NaN;	// 학습 데이터: 직전 거래의 손익(R)
		private DateTime		mlPrevExit		= DateTime.MinValue;	// 학습 데이터: 직전 거래의 청산 시각
		private ATR				atr;						// 실험 옵션 MinStopAtr용 3분봉 ATR
		private double			stopAtr			= double.NaN;	// 신호 봉의 ATR (손절 거리 하한 계산용)
		private Brush			upBackBrush;				// 화면 표시용: 상승추세 배경색
		private Brush			downBackBrush;					// 화면 표시용: 하락추세 배경색
		private Brush			upLightBrush, downLightBrush;	// 테스트 확장: 7레짐 눌림·시작의심 (옅게)
		private Brush			warnDownBrush, warnUpBrush;		// 테스트 확장: 전환의심 (상승 중 하락 전환의심 주황, 하락 중 상승 전환의심 하늘색)
		private bool			breakevenDone;					// 본절 이동 완료
		private readonly List<string>			liveSignals	= new List<string>();		// 아직 보유 중인 진입 시그널명
		private readonly Dictionary<string, int>	chunkQty	= new Dictionary<string, int>();	// 진입 시그널별 계획 수량
		// 비관리형 주문 상태 (v6-box에서 Playback으로 검증한 방식). 조각 = 진입 시그널명(UpL1 등), 주문 이름 = 거래 앞부분(P12.) + 종류
		private readonly List<string>				chunkOrder		= new List<string>();				// 이번 진입의 모든 조각 (단계 순서)
		private readonly Dictionary<string, int>	chunkLeft		= new Dictionary<string, int>();	// 조각별 실제 보유 수량 (진입 체결로 늘고 청산 체결로 줄어듦)
		private readonly Dictionary<string, int>	chunkFilled		= new Dictionary<string, int>();	// 조각별 진입 체결 수량
		private string			addSig			= "";		// 불타기 조각 이름 (UpL4·DnS4). 빈 문자열이면 이번 거래에 불타기 없음
		private Order			addOrder;					// 불타기 시장가 주문
		private double			addFillSum;					// 불타기 체결가 × 수량 합 (불타기 조각 손익은 이 평균가 기준)
		private int				addFillQty;					// 불타기 체결 수량
		private readonly HashSet<string>			stageFilled		= new HashSet<string>();			// 익절 단계 처리를 이미 한 조각 (부분 체결이 여러 번 와도 한 번만)
		private readonly HashSet<string>			exitSent		= new HashSet<string>();			// 직접 청산 주문을 낸 조각
		private Order								entryOrder;										// 진입 주문 (시장가 또는 강한 돌파 스탑)
		private Order								stopOrder;										// 보호 손절 (보유 수량 전체)
		private readonly Dictionary<string, Order>	tpOrders		= new Dictionary<string, Order>();	// 조각 → 익절 지정가
		private readonly Dictionary<string, List<string>>	exitOrderChunks	= new Dictionary<string, List<string>>();	// 시장가 청산 주문 이름 → 조각
		private int									plannedExitQty;									// 아직 체결 안 된 시장가 청산 수량 (손절 수량에서 뺌)
		private int									exitSeq;
		private int									signalSeq;
		private string								tradePrefix		= "";							// 이번 거래 주문 이름 앞부분 (예: P12.)
		private string								sbPrefix		= "";							// 걸어 둔 강한 돌파 스탑 주문의 앞부분 (같은 방향이면 이름을 그대로 써서 주문을 고침)
		private bool								inReconcile, reconcileAgain;
		private bool								stopRejectHandled;								// 손절 거부 → 전량 청산을 이번 거래에 이미 함

		// spec 5장 익절 상태 머신 (task4)
		private bool			tp1Done;						// 1차 익절 완료
		private bool			tp2Done;						// 5.4 2차 익절 완료
		private bool			strongMomentum;					// 5.1 강한 모멘텀 전환
		private SideSet			sideSet		= SideSet.None;		// 5.5·5.6 먼저 충족된 세트
		private enum SideSet { None, A, B }

		// 검증용 임시 진입 (task5, UseTestEntry). 보유 봉 수로 분할 익절·본절·청산을 결정적으로 재현
		private int				testHoldBars;					// 테스트 진입 후 경과한 보유 봉 수
		private const int		TestTp1Bar	= 2;				// 이 봉에 1차 익절 + 본절
		private const int		TestTp2Bar	= 3;				// 이 봉에 2차(강한 모멘텀) 익절
		private const int		TestExitBar	= 4;				// 이 봉에 나머지 전량 청산

		// ── 테스트 확장 (브랜치 test/score-v1-plus, docs/plus-design.md): v6의 7레짐·강한 돌파·강제 채우기를 트레이더 전략에 붙임 ──
		// v6 연속 점수 (v6 spec 3-3): 3분봉 SMA 6/20/60·MACD 12/26/9·RSI 14·ATR 14와 지금 1분봉 종가로 항목 11개를 −1~+1로 만들어 평균
		private const double	ScoreTrendOn	= 0.2;		// 추세 시작 점수 (v6 ScoreTrendOn)
		private const double	ScoreVelMin		= 0.25;		// 빠른 출발 속도 (v6 ScoreVelMin)
		private const double	ScoreExitOff	= -0.1;		// 청산용 판정 종료 점수 (v6 ScoreExitOff)
		private const int		ScoreVelBars	= 6;		// 속도 = 점수 − 6개 1분봉 전 점수
		private const double	R7EndSuspectC	= 0.1;		// 추세 → 눌림 점수 (v6 R7EndSuspectC)
		private const int		ScoreMinBars3	= 70;		// 3분봉이 이만큼 쌓여야 점수를 냄 (SMA60 + 기울기 5봉 + 여유)
		private const double	TrailTp1Atr		= 3.0;		// 1차 익절 뒤 트레일링: 진입 뒤 최고가 − 3 × 1분 ATR
		private const double	TrailTp2Atr		= 2.5;		// 2차 익절(또는 5.1 강한 모멘텀) 뒤: 2.5 × 1분 ATR
		private const int		OpenTime		= 223000;	// 미국 개장 (강한 돌파는 개장 전후 22:25~22:32 봉에 걸지 않음)
		private const int		OpenBlockBefore	= 5;
		private const int		OpenBlockAfter	= 3;
		private const int		SbRangeBars		= 15;		// 강한 돌파 기준: 지금 봉 포함 최근 15봉 최고가·최저가
		private const int		SbBoxBars		= 30;		// 강한 돌파 박스 판단: 지금 봉을 뺀 직전 30봉 종가 효율비
		private SMA				r3Fast, r3Mid, r3Slow;		// 3분봉 SMA 6 / 20 / 60
		private MACD			r3Macd;
		private RSI				r3Rsi;
		private ATR				r3Atr;
		private MAX				rangeHigh;
		private MIN				rangeLow;
		private Series<double>	contHist;					// 1분봉마다 점수 (속도 계산용)
		private bool			contReady;
		private double			contScore, contVel, contSlow, contFast;
		private string			contDiag		= "";		// 점수 진단 (3분봉 시각·지표 원값·항목)
		private int				dnShortWaitUntil	= -1;	// SMA20 위라 막은 5.4 매도를 이 봉(CurrentBar)까지 기다림
		private int				trendRunDir;		// 추세 지속 진입: 지금 7레짐 방향
		private int				trendRunLen;		// 같은 방향 '추세'(±2)였던 1분봉 수
		private bool			trendRunUsed;		// 이 추세 구간에서 지속 진입을 했는지
		private double			sessStart		= double.NaN;	// 그날 대회 구간 첫 봉 종가 (흔들리는 장 판단)
		private double			sessPrev		= double.NaN;
		private double			sessPath;
		private int				sessBars;
		private const int		ChopMinBars		= 60;		// 대회 구간 시작 뒤 이 봉 수가 지나야 흔들리는 장을 판단
		private int				contExitState;				// 청산용 판정 +1 / 0 / −1 (v6 spec 3-3)
		private int				r7;							// 7레짐: 0 횡보, ±1 시작의심, ±2 추세, ±3 눌림 (v6 spec 3-4·3-5)
		private int				r7Warn;						// 전환의심: 추세·눌림 중 반대 방향 빠른 출발이면 그 반대 방향(±1)
		private bool			sawOwnTrend;				// 보유 중 청산용 판정이 포지션 방향이었던 적이 있음 (TREND_END 조건)
		private bool			warnDone;					// 포지션당 전환의심 청산은 한 번
		private double			trailExtreme	= double.NaN;	// 보유 중 최고가(매수)·최저가(매도)
		private int				sbDir;						// 강한 돌파 스탑 주문이 걸려 있는 방향 (0 = 없음)
		private readonly Dictionary<string, double>	fixedTarget	= new Dictionary<string, double>();	// 밴드가 아닌 고정 익절 가격 (강한 돌파)
		private string			lastCountText	= "";		// 진입 횟수 표시: 바뀔 때만 다시 그림

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 (docs/spec.md)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				// 비관리형 주문 (테스트 확장, 2026-10-22): 진입 1개, 보호 손절 1개(실제 보유 수량 전체), 조각별 익절 지정가, 시장가 청산.
				// 관리형은 진입 시그널별로 수량을 따로 세서 실시간 부분 체결 + 조각 청산이 겹치면 실제 포지션과 어긋났음
				// (10/12 22:21 Playback: 18계약 보유에 전량 청산이 19계약을 팔아 포지션이 뒤집힌 뒤 매 봉 사고팔기를 되풀이)
				IsUnmanaged			= true;
				// 취소 중인 주문이 체결돼도(초과 체결) 전략을 멈추지 않고 Reconcile()이 계획에 없는 포지션을 시장가로 정리 (NT8 IgnoreOverfill 문서)
				IgnoreOverfill		= true;

				// 기본값 = 운용 기본값 (spec 8장의 "운용 기본값" 열, 2026-10-09 기준).
				// 뒤에 "명세 N"이라고 적힌 줄은 원래 명세 값과 다르게 둔 것이다. 팀 확정 전까지의 임시 값이다

				// 지표 계산값 (명세와 같음)
				BandMidPeriod		= 26;
				BandAtrPeriod		= 14;
				MacdFast			= 12;
				MacdSlow			= 26;
				MacdSmooth			= 9;
				MacdConfirmBars		= 2;
				StochPeriodK		= 10;
				StochSmooth			= 5;	// 팀 운용 기본값 그대로 (2026-10-22 교차 검증: 바꾼 값은 처음 보는 9/21~10/2에서 효과 없음 = 과적합)
				StochPeriodD		= 5;
				CrossLow			= 20;	// 팀 운용 기본값 그대로 (2026-10-22 교차 검증: 바꾼 값은 처음 보는 9/21~10/2에서 효과 없음 = 과적합)
				CrossHigh			= 80;	// 팀 운용 기본값 그대로 (2026-10-22 교차 검증: 바꾼 값은 처음 보는 9/21~10/2에서 효과 없음 = 과적합)
				CrossFilterBars		= 5;
				RsiPeriod			= 14;
				RsiHigh				= 70;
				RsiLow				= 20;
				DivFrom				= 5;
				DivTo				= 30;

				// 진입 — 추세별 밴드 배수와 반전 신호 대기 봉 수
				UpLongBand			= 0;		// 명세 1. 0 = 중심선
				DnShortBand			= 0;		// 명세 2. 0 = 중심선
				T1Window			= 3;		// 명세 3 (2026-10-23 주 단위 교차 검증: 5 → 3, 밴드 조건 뒤 5~6봉째 늦은 반전 26건 −$12,120을 뺌)

				// 진입 — 공통
				TrendT2Entry		= true;		// 명세에 없음 (끔과 같음)
				T2CountConfirmBars	= true;		// 명세에 없음 (끔과 같음)
				BandTouchEntry		= true;		// 명세에 없음 (끔 = 종가 돌파)

				// 추세 판단 (10분봉)
				// 2026-10-14 테스트: 추세 판단만 점수 방식으로 (spec 3-1). 1이면 위의 이동평균·고저점 규칙

				// 손절·본절
				SwingBars			= 5;
				MinStopAtr			= 1.5;	// 2026-10-23 앞 22일로 고르고 뒤 23일로 확인: 1 → 1.5 학습 +$9,350(부트스트랩 84%) / 검증 +$2,387(76%). 2.0도 둘 다 +

				// 청산
				MomentumSmaPeriod	= 20;	// 팀 운용 기본값 그대로 (2026-10-22 교차 검증: 바꾼 값은 처음 보는 9/21~10/2에서 효과 없음 = 과적합)

				// 수량·대회 시간 (spec 6·7장)
				EntryQuantity		= 50;		// MNQ 단독 운용(2026-10-23): 진입 50 + 불타기 30 = 대회 한도 80(미니 8계약). 50은 1/5·2/5로 나눠떨어짐
				MaxQuantity			= 80;		// 대회 한도: 합산 미니 8계약 = MNQ 80. MNQ만 거래하므로 전부 MNQ에 씀. 불타기 상한
				MinEntries			= 4;		// 종목당 최소 진입. 대회 규정은 두 기초자산군 합산 4회 → 한 종목이 멈춰도 채우게 종목당 4 (2026-10-23)
				// 2026-10-14 대회 시간을 21:30~23:30 KST로 확정 (그 전에는 22:30~00:30)
				ContestStartTime	= 213000;	// 21:30:00 KST
				EntryEndTime		= 231500;	// 23:15:00 KST
				FlattenTime			= 232700;	// 23:27:00 KST 봉 마감

				// 화면·기록·테스트
				ShowStatus			= true;
				StatusPanelScale	= 130;		// 상태 상자·체결 표시 글자 크기 %
				LogToFile			= true;
				TradeLogToFile		= true;

				// 테스트 확장 (docs/plus-design.md). 모두 끄면 위의 트레이더 전략 그대로
				RiskCapUsd			= 4000;		// 1회 위험 상한 ($). 0 = 끔
				BreakevenMinR		= 0.25;		// 1차 익절 이익이 손절 거리 × 이 값보다 작으면 본절로 옮기지 않음. 0 = 트레이더 원래 규칙
				ChopMaxEr			= 0.15;		// 흔들리는 장이면 추세 진입은 눌림에서만. 0 = 끔
				TrendContBars		= 8;		// 추세 지속 진입: 7레짐 같은 방향 추세가 이 봉 수 이어지면 한 번 진입. 0 = 끔
				TrendContMaxVel		= 0.2;		// 추세 지속 진입은 점수 속도가 추세 방향으로 이 값 미만일 때만 (속도 추격 금지). 0 = 조건 없음
				AddOnQty			= 30;		// 2차 익절 뒤 추세가 이어지면 한 번 더하는 계약 수 (합계 MaxQuantity 이하). 0 = 끔
				AddOnEndTime		= 231000;	// 불타기는 이 시각(HHmmss) 전까지만
				AddOnMinRoomAtr		= 1;		// 불타기는 현재가와 손절 사이가 이 × 1분 ATR 이상일 때만. 0 = 조건 없음
				DnShortSmaWaitBars	= 10;		// 그 뒤 이 봉 수 안에 종가가 SMA20 아래로 오면 매도
				EarlyQtyEndTime		= 214500;	// 이 시각 전 신호 봉의 진입은 EarlyQty 이하 (21:30 지표 발표 구간)
				EarlyQty			= 25;
				ForceMinEntries		= true;		// 최소 진입 강제 채우기
			}
			else if (State == State.Configure)
			{
				// 10분봉 → BarsArray[1]. 인자는 하드코딩한다
				AddDataSeries(BarsPeriodType.Minute, 10);
				// 3분봉 → BarsArray[2]. RegimeBarMinutes가 3일 때 추세 판단에 쓴다.
				// AddDataSeries의 인자는 변수로 줄 수 없어서 두 시리즈를 항상 추가하고 어느 것을 쓸지만 고른다
				AddDataSeries(BarsPeriodType.Minute, 3);

				// 주문 오류를 NinjaTrader 기본 동작(포지션을 닫고 전략 종료)에 맡기지 않고 OnOrderUpdate에서 직접 처리한다.
				// 이 값은 SetDefaults나 Configure에서만 정할 수 있다 (NT8 공식 문서)
				if (HandleOrderErrors)
					RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
			}
			else if (State == State.DataLoaded)
			{
				// 마지막 인자 false = ShowDebugVisuals. 밴드 설정값은 두 지표에 같은 값을 넘긴다 (docs/interface.md)
				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				regimeBip	= RegimeBarMinutes == 3 ? 2 : 1;	// 추세를 판단하는 시리즈: 10분봉(1) 또는 3분봉(2)
				regime		= TQ_Regime(BarsArray[regimeBip], RegimeFast, RegimeMid, RegimeSlow, UseRule12, false,
								Rule12Bars, Rule12PriceFilter, FastTrendBars, FastTrendSlope,
								RegimeMode, RegimeScoreMin, RegimeScoreRsiBand, RegimeScoreEventBars, RegimeScoreExit,
								MacdFast, MacdSlow, MacdSmooth, RegimeHistBars, RsiPeriod,
								StochPeriodK, StochSmooth, StochPeriodD, CrossLow, CrossHigh, CrossFilterBars,
								RegimeDivBars, RegimePivotStrength);
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, RsiHigh, RsiLow,
								DivFrom, DivTo, SwingBars,
								BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3,
								MacdFast, MacdSlow, MacdSmooth,
								StochPeriodK, StochSmooth, StochPeriodD,
								MomentumSmaPeriod, MacdConfirmBars, MacdMinChangeAtr, false,
								UpLongBand, UpShortBand, DnLongBand, DnShortBand, SideBand,
								T1WindowUp, T1WindowDn, T1WindowSide, T2CountConfirmBars, BandTouchEntry);

				// 로그 파일: 내 문서\NinjaTrader 8\TQ_log_종목.txt (LogToFile)
				logPath		= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_log_plus_" + Instrument.MasterInstrument.Name + ".txt");
				tradePath	= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_trades_plus_" + Instrument.MasterInstrument.Name + ".csv");
				mlPath		= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_ml_plus_" + Instrument.MasterInstrument.Name + ".csv");
				mlRunId		= DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
				// 설정 ID는 종목 이름([MNQ DEC26])을 빼고 만든다. 월물이 달라도 같은 설정이면 같은 ID
				string summary	= SettingsSummary();
				int cut			= summary.IndexOf("] ");
				mlSettingsId	= HashText(cut >= 0 ? summary.Substring(cut + 2) : summary);

				atr			= ATR(BandAtrPeriod);

				// 테스트 확장: 7레짐 점수용 3분봉 지표 (BarsArray[2]), 강한 돌파용 1분봉 15봉 최고·최저
				r3Fast		= SMA(BarsArray[2], 6);
				r3Mid		= SMA(BarsArray[2], 20);
				r3Slow		= SMA(BarsArray[2], 60);
				r3Macd		= MACD(BarsArray[2], 12, 26, 9);
				r3Rsi		= RSI(BarsArray[2], 14, 3);
				r3Atr		= ATR(BarsArray[2], 14);
				rangeHigh	= MAX(High, SbRangeBars);
				rangeLow	= MIN(Low, SbRangeBars);
				contHist	= new Series<double>(this, MaximumBarsLookBack.Infinite);

				// 화면 표시: 밴드를 차트에 같이 올리고, 배경색 브러시와 글꼴을 한 번만 만든다 (직접 만든 브러시는 Freeze)
				if (ShowStatus)
					AddChartIndicator(channels);
				upBackBrush		= new SolidColorBrush(Color.FromArgb(30, 0, 200, 0));
				downBackBrush	= new SolidColorBrush(Color.FromArgb(30, 230, 0, 0));
				upBackBrush.Freeze();
				downBackBrush.Freeze();
				upLightBrush	= new SolidColorBrush(Color.FromArgb(14, 0, 200, 0));
				downLightBrush	= new SolidColorBrush(Color.FromArgb(14, 230, 0, 0));
				warnDownBrush	= new SolidColorBrush(Color.FromArgb(40, 255, 140, 0));
				warnUpBrush		= new SolidColorBrush(Color.FromArgb(40, 0, 170, 255));
				upLightBrush.Freeze();
				downLightBrush.Freeze();
				warnDownBrush.Freeze();
				warnUpBrush.Freeze();

				// spec 6장: 분할 진입 합계는 EntryQuantity이고 불타기가 없으므로 보유 수량은 EntryQuantity를 넘지 않는다.
				// MaxQuantity(= 종목당 대회 한도 40, 두 종목 합쳐 미니 8계약 상당)보다 크게 설정하면 한도를 넘을 수 있어 경고한다.
				if (EntryQuantity > MaxQuantity)
					Out(string.Format("[{0}][TQ_Strategy] 경고: 기본 진입 수량({1}) > 최대 보유 수량({2}, 대회 한도). 설정 확인 필요 (spec 6장)",
						Instrument.FullName, EntryQuantity, MaxQuantity));
			}
			else if (State == State.Historical)
			{
				SetZOrder(int.MaxValue);	// 화면 표시: 상태 상자·선·체결 표시를 봉·지표 위에 그림 (NT8 SetZOrder 도움말: 가장 위 = int.MaxValue, State.Historical 이후)
			}
			else if (State == State.Realtime)
			{
				// 로그 파일 머리말: 어떤 설정으로 돌린 기록인지 남긴다 (기본값에서 자주 바꾸는 것만)
				Out("==== " + SettingsSummary());
			}
		}

		// 어떤 설정으로 돌린 기록인지 한 줄로 (기본값에서 자주 바꾸는 것만). 로그 파일과 거래 요약 파일에 쓴다
		private string SettingsSummary()
		{
			return string.Format("[{0}] {1}분봉 | 밴드 상승 매수/매도={2}/{3} 하락 매도/매수={4}/{5} 횡보={6} | 대기 공통/상승/하락/횡보={7}/{8}/{9}/{10} | 역추세={11} 재진입금지={12} 매도둘다={13} | 추세 MA={14}/{15}/{16} 고저규칙={17} 구간={18} 20선조건={19} 빠른전환={20} 기울기={28} | 손절%={21} 전저점={22} 최소ATR={23} 본절R={24} | 횡보청산밴드={25} MACD확인={26} 추세T2진입={27} | 역추세매수 다이버전스={29} 역추세수량%={30} T2앞봉인정={31} 봉중익절={32} 추세봉={33}분 | 추세방식={34}(1이평/2점수) 최소점수={35} RSI폭={36} 사건봉수={37} 종료점수={38} | 손절상한 pt/ATR={39}/{40} | 밴드터치진입={41} | 손절먼쪽={42} 하락매도청산SMA={43}",
				Instrument.FullName, BarsPeriod.Value, UpLongBand, UpShortBand, DnShortBand, DnLongBand, SideBand,
				T1Window, T1WindowUp, T1WindowDn, T1WindowSide, UseCounterTrend, BlockReentryAfterStop, DnShortNeedBoth,
				RegimeFast, RegimeMid, RegimeSlow, UseRule12, Rule12Bars, Rule12PriceFilter, FastTrendBars,
				StopPercent, UseSwingStop, MinStopAtr, BreakevenAtR, SideRestBand, MacdConfirmBars, TrendT2Entry,
				FastTrendSlope, DnLongNeedDiv, CounterQtyPercent, T2CountConfirmBars, IntrabarTakeProfit, RegimeBarMinutes == 3 ? 3 : 10,
				RegimeMode, RegimeScoreMin, RegimeScoreRsiBand, RegimeScoreEventBars, RegimeScoreExit,
				MaxStopPoints, MaxStopAtr, BandTouchEntry, StopUseFarthest, DnShortExitSma)
				+ string.Format(" | 확장 7레짐={0} 눌림만={1} 횡보진입={2} 전환의심={3} 지표발표분={4} 트레일링={5} 추세청산={6} | 강한돌파={7} 손절pt={8} 익절ATR={9}/{10} F={11} 속도={12} 효율비={13} 거리ATR={14} 마감={15} 시작의심={18} | 강제채우기={16} 여유봉={17}",
					UseR7Regime, TrendEntryPullbackOnly, UseSideEntry, UseWarnExit, NewsBlockMinutes, UseTrailAfterTp1, UseTrendChangeExit,
					UseStrongBreakout, SbStopPoints, SbTp1Atr, SbTp2Atr, SbMinFast, SbMinVel, SbMaxEr, SbNearAtr, SbEndTime, ForceMinEntries, ForceMarginBars, SbAllowStartSuspect)
				+ string.Format(" | 위험상한=${0} 본절최소R={1} 흔들림효율비={2} 지속진입={3}봉/속도<{4} 불타기={5} 마감={6} 여유ATR={8} 한도={7} 주문=비관리형", RiskCapUsd, BreakevenMinR, ChopMaxEr, TrendContBars, TrendContMaxVel, AddOnQty, AddOnEndTime, MaxQuantity, AddOnMinRoomAtr);
		}

		#region 거래 요약 파일 (TradeLogToFile) — 매매 판단에는 영향 없음
		// 거래 1건이 끝날 때마다 한 줄을 내 문서\NinjaTrader 8\TQ_trades_종목.csv에 쓴다.
		// 백테스트(Strategy Analyzer)에서도 쓰므로, 여러 날을 한 번에 돌린 결과를 거래 단위로 볼 수 있다
		private void TradeBegin(ActiveStrategy act)
		{
			trOpen		= true;
			trEntryTime	= Time[0];
			trName		= act.ToString();
			trRegime	= entryRegime;
			trStop0		= double.NaN;
			trPnlPts	= 0;
			trMfe		= 0;
			trMae		= 0;
			trBars		= 0;
			trExits		= "";
		}

		// 보유 중 봉마다: 평단 대비 최대 유리폭·불리폭(포인트)과 보유 봉 수
		private void TradeTrack()
		{
			if (!trOpen || entryFillQty <= 0 || Position.MarketPosition == MarketPosition.Flat)
				return;
			double avg = entryFillSum / entryFillQty;
			trBars++;
			trMfe = Math.Max(trMfe, isLongPos ? High[0] - avg : avg - Low[0]);
			trMae = Math.Max(trMae, isLongPos ? avg - Low[0] : High[0] - avg);
		}

		// 청산 체결마다: 실현 손익(포인트 × 수량)과 청산 종류
		// 불타기 조각 청산이면 avgOverride = 불타기 평균가 (진입 조각과 따로 셈)
		private void TradeExitFill(string name, double price, int quantity, DateTime time, double avgOverride = double.NaN)
		{
			if (!trOpen || entryFillQty <= 0)
				return;
			double avg = double.IsNaN(avgOverride) ? entryFillSum / entryFillQty : avgOverride;
			trPnlPts	+= (isLongPos ? price - avg : avg - price) * quantity;
			trExitTime	= time;
			if (!trExits.EndsWith(name))
				trExits += (trExits.Length > 0 ? " > " : "") + name;
		}

		private void TradeEnd()
		{
			if (!trOpen)
				return;
			trOpen = false;
			MlEnd();
			if (!TradeLogToFile || tradePath == null || entryFillQty <= 0)
				return;

			System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
			double avg = entryFillSum / entryFillQty;
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			try
			{
				if (!System.IO.File.Exists(tradePath))
					sb.AppendLine("모드,진입시각,청산시각,종류,추세,방향,수량,평단,처음손절,손절거리pt,손익pt(계약당 평균),손익$,최대유리pt,최대불리pt,보유봉수,청산순서");
				if (!trHeaderWritten)
				{
					sb.AppendLine("# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + SettingsSummary().Replace(",", ";"));
					trHeaderWritten = true;
				}
				sb.AppendLine(string.Join(",", new string[]
				{
					State == State.Realtime ? "실시간" : "과거",
					trEntryTime.ToString("yyyy-MM-dd HH:mm:ss"),
					trExitTime.ToString("yyyy-MM-dd HH:mm:ss"),
					trName,
					trRegime.ToString(inv),
					isLongPos ? "매수" : "매도",
					(entryFillQty + addFillQty).ToString(inv),	// 불타기 포함 수량
					avg.ToString("0.##", inv),
					double.IsNaN(trStop0) ? "" : trStop0.ToString("0.##", inv),
					double.IsNaN(trStop0) ? "" : Math.Abs(avg - trStop0).ToString("0.##", inv),
					(trPnlPts / (entryFillQty + addFillQty)).ToString("0.##", inv),
					(trPnlPts * Instrument.MasterInstrument.PointValue).ToString("0", inv),
					trMfe.ToString("0.##", inv),
					trMae.ToString("0.##", inv),
					trBars.ToString(inv),
					trExits
				}));
				System.IO.File.AppendAllText(tradePath, sb.ToString(), new System.Text.UTF8Encoding(true));
			}
			catch (Exception)
			{
				// 파일을 못 써도 매매에는 영향이 없게 한다
			}
		}
		#endregion

		#region 학습 데이터 파일 (MlLogToFile) — 매매 판단에는 영향 없음
		// 거래 1건마다 한 줄을 내 문서\NinjaTrader 8\TQ_ml_종목.csv에 쓴다.
		// 진입 신호 봉에서 잰 특징값(지표 상태)과 그 거래의 결과를 한 줄에 같이 둬서, 어떤 신호가 손절로 끝나는지 학습·분석하는 데 쓴다.
		// 열 이름은 Python(pandas)에서 바로 쓰도록 영어로 둔다. '#'으로 시작하는 줄은 실행별 설정 요약이다 (read_csv(comment='#'))
		private const string MlHeader =
			"run_id,settings_id,mode,instrument,bar_min,"
			+ "signal_time,weekday,hhmm,strategy,regime,dir,via_t2,"
			+ "close,atr,atr_pct,stop_dist_atr,mid_dist_atr,sma20_dist_atr,"
			+ "macd_hist_atr,macd_slope_atr,k,d,k_minus_d,rsi,"
			+ "macd_up,macd_down,golden,dead,bar_range_atr,swing_range_atr,vol_ratio,"
			+ "htf_sma20_dist_atr,regime_age,regime_changes6,session_entry_no,prev_r,min_since_prev_exit,"
			+ "exit_time,hold_bars,qty,avg_price,risk_pts,pnl_pts,pnl_usd,pnl_r,mfe_pts,mae_pts,mfe_r,mae_r,win,exits";

		// 신호 봉(BIP 0, 진입 주문을 내는 봉)에서 특징값을 잰다. BeginTrade 끝에서 부른다.
		// ATR로 나눈 값은 종목·시기에 따라 가격 단위가 달라도 비교할 수 있게 하기 위함이다
		private void MlBegin(ActiveStrategy act)
		{
			mlFeatures = "";
			if (!MlLogToFile)
				return;

			double a	= atr[0];
			double c	= Close[0];
			bool okA	= a > 0;
			int r		= (int)regime.Regime[0];

			// 밴드 조건 없이 T2로만 들어간 추세 방향 진입인지 (TrendT2Entry)
			int viaT2 = 0;
			if (act == ActiveStrategy.UpLong && !signals.EntryUpLong[0])	viaT2 = 1;
			if (act == ActiveStrategy.DnShort && !DnShortSignal())			viaT2 = 1;

			// 거래량: 직전 20봉 평균 대비
			int volBars = Math.Min(20, CurrentBar);
			double volSum = 0;
			for (int i = 1; i <= volBars; i++)
				volSum += Volume[i];
			double volRatio = volSum > 0 ? Volume[0] / (volSum / volBars) : double.NaN;

			// 추세 봉 기준: 지금 추세가 몇 봉째 이어지는지, 최근 6봉 동안 추세가 몇 번 바뀌었는지
			int maxBack = Math.Min(200, CurrentBars[regimeBip]);
			int age = 1;
			while (age < maxBack && (int)regime.Regime[age] == r)
				age++;
			int changes = 0;
			for (int i = 0; i < 6 && i + 1 <= CurrentBars[regimeBip]; i++)
				if ((int)regime.Regime[i] != (int)regime.Regime[i + 1])
					changes++;

			double htfDist = double.IsNaN(regime.Sma20[0]) ? double.NaN : Closes[regimeBip][0] - regime.Sma20[0];

			mlFeatures = string.Join(",", new string[]
			{
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
				((int)Time[0].DayOfWeek).ToString(),
				(ToTime(Time[0]) / 100).ToString(),
				act.ToString(),
				r.ToString(),
				isLongPos ? "1" : "-1",
				viaT2.ToString(),
				Ml(c),
				Ml(a),
				Ml(c > 0 ? a / c * 100 : double.NaN),
				Ml(okA ? Math.Abs(c - initialStop) / a : double.NaN),
				Ml(okA ? (c - channels.Mid[0]) / a : double.NaN),
				Ml(okA ? (c - signals.Sma20[0]) / a : double.NaN),
				Ml(okA ? signals.MacdHist[0] / a : double.NaN),
				Ml(okA ? (signals.MacdHist[0] - signals.MacdHist[1]) / a : double.NaN),
				Ml(signals.K[0]),
				Ml(signals.D[0]),
				Ml(signals.K[0] - signals.D[0]),
				Ml(signals.Rsi[0]),
				signals.MacdUp[0] ? "1" : "0",
				signals.MacdDown[0] ? "1" : "0",
				signals.Golden[0] ? "1" : "0",
				signals.Dead[0] ? "1" : "0",
				Ml(okA ? (High[0] - Low[0]) / a : double.NaN),
				Ml(okA ? (signals.SwingHigh5[0] - signals.SwingLow5[0]) / a : double.NaN),
				Ml(volRatio),
				Ml(okA ? htfDist / a : double.NaN),
				age.ToString(),
				changes.ToString(),
				(entryCount + 1).ToString(),	// 이번 대회 구간의 몇 번째 진입인지 (entryCount는 체결 후 증가)
				Ml(mlPrevR),
				mlPrevExit == DateTime.MinValue ? "" : Ml((Time[0] - mlPrevExit).TotalMinutes)
			});
		}

		// 거래가 끝나면(보유 → 미보유) 특징값 뒤에 결과를 붙여 한 줄을 쓴다. TradeEnd에서 부른다
		private void MlEnd()
		{
			if (entryFillQty <= 0)
				return;

			double avg		= entryFillSum / entryFillQty;
			double pnlPts	= trPnlPts / (entryFillQty + addFillQty);	// 계약당 평균 손익 (포인트, 불타기 포함)
			double risk		= double.IsNaN(trStop0) ? double.NaN : Math.Abs(avg - trStop0);
			bool okR		= !double.IsNaN(risk) && risk > 0;
			double pnlR		= okR ? pnlPts / risk : double.NaN;

			// 다음 거래의 특징값(prev_r, min_since_prev_exit)용. 파일 기록을 꺼도 갱신한다
			mlPrevR		= pnlR;
			mlPrevExit	= trExitTime;

			if (!MlLogToFile || mlPath == null || mlFeatures.Length == 0)
				return;

			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			try
			{
				if (!System.IO.File.Exists(mlPath))
					sb.AppendLine(MlHeader);
				if (!mlHeaderWritten)
				{
					sb.AppendLine("# " + mlRunId + " settings_id=" + mlSettingsId + " " + SettingsSummary().Replace(",", ";"));
					mlHeaderWritten = true;
				}
				sb.AppendLine(string.Join(",", new string[]
				{
					mlRunId,
					mlSettingsId,
					State == State.Realtime ? "live" : "hist",
					Instrument.FullName,
					BarsPeriod.Value.ToString(),
					mlFeatures,
					trExitTime.ToString("yyyy-MM-dd HH:mm:ss"),
					trBars.ToString(),
					entryFillQty.ToString(),
					Ml(avg),
					Ml(risk),
					Ml(pnlPts),
					Ml(trPnlPts * Instrument.MasterInstrument.PointValue),
					Ml(pnlR),
					Ml(trMfe),
					Ml(trMae),
					Ml(okR ? trMfe / risk : double.NaN),
					Ml(okR ? trMae / risk : double.NaN),
					pnlPts > 0 ? "1" : "0",
					trExits
				}));
				System.IO.File.AppendAllText(mlPath, sb.ToString(), new System.Text.UTF8Encoding(true));
			}
			catch (Exception)
			{
				// 파일을 못 써도 매매에는 영향이 없게 한다
			}
			mlFeatures = "";
		}

		// 숫자를 CSV 칸으로: 소수점은 항상 '.', 값이 없으면 빈칸
		private string Ml(double v)
		{
			if (double.IsNaN(v) || double.IsInfinity(v))
				return "";
			return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
		}

		// 설정 요약 문자열의 짧은 해시 (FNV-1a 32비트). 실행이 달라도 같은 설정이면 같은 값이 나온다
		private string HashText(string text)
		{
			uint h = 2166136261;
			foreach (char ch in text)
			{
				h ^= ch;
				h *= 16777619;
			}
			return h.ToString("x8");
		}
		#endregion

		// 차트 왼쪽 위 이름표는 이름만 (설정값은 로그 첫 줄과 설정창에서 본다). 설정값을 다 쓰면 세 줄이 되어 상태 상자를 가림 (v6-box와 같음)
		public override string DisplayName
		{
			get { return "TQ_Strategy"; }
		}

		protected override void OnBarUpdate()
		{
			// 테스트 확장: 3분봉 점수 지표를 3분봉 처리 때 읽어 그 시리즈에서 갱신되게 한다 (v6는 3분봉마다 읽었음.
			// 1분봉 처리 때만 읽으면 10/22 Playback에서 점수가 v6·재현기와 달랐음 — 원인 확인용 진단 로그도 같이 남김)
			if (BarsInProgress == 2 && CurrentBars[2] >= 0)
			{
				double touch = r3Fast[0] + r3Mid[0] + r3Slow[0] + r3Macd[0] + r3Rsi[0] + r3Atr[0];
				if (double.IsNaN(touch) && CurrentBars[2] > ScoreMinBars3)
					Out(string.Format("[{0}][{1}][TQ_Strategy] 3분봉 지표 값 없음", Time[0], Instrument.FullName));
			}

			// 데이터 부족 가드: 10분봉 SMA120이 채워지기 전에는 아무것도 하지 않는다
			if (CurrentBars[0] < DivTo || CurrentBars[regimeBip] < RegimeSlow)
				return;
			// 점수 방식은 다이버전스 구간까지 채워져야 추세를 낸다. 그 전(NaN)에는 아무것도 하지 않는다
			if (double.IsNaN(regime.Regime[0]))
				return;

			if (BarsInProgress == regimeBip)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] {5}분봉 마감 레짐={2} 종가={3} 20선={4:F2}",
					Time[0], Instrument.FullName, regime.Regime[0], Closes[regimeBip][0], regime.Sma20[0], RegimeBarMinutes == 3 ? 3 : 10));

				// spec 5.2 1차 익절: 상승추세 숏 보유 중 10분봉 저가 <= 10분봉 SMA20이면 즉시 익절.
				// 다음 3분봉 마감을 기다리지 않고 10분봉 마감 즉시 처리하며, 주문은 BIP 0 대상으로 낸다.
				if (active == ActiveStrategy.UpShort && !tp1Done
					&& Position.MarketPosition == MarketPosition.Short
					&& !double.IsNaN(regime.Sma20[0]) && Lows[regimeBip][0] <= regime.Sma20[0])
				{
					MoveBreakeven();	// 체결 콜백이 주문 호출 도중에 올 수 있어 익절 주문보다 먼저 예약한다
					ExitChunk("UpS1", "5.2 1차 익절(10분봉 저가<=SMA20)");
					tp1Done = true;
				}
				return;
			}

			if (BarsInProgress != 0)
				return;

			// 테스트 확장: 1분봉마다 v6 연속 점수·7레짐·청산용 판정을 갱신 (진입·청산 판단보다 먼저)
			UpdateContScore();
			Observe7Regime();
			CountTrendRun();

			Out(string.Format("[{0}][{1}][TQ_Strategy] 레짐={2} UpLong={3} UpShort={4} DnLong={5} DnShort={6} SideLong={7} SideShort={8} 대기(같은 순서)={9}/{10}/{11}/{12}/{13}/{14} 점수C={15:F6} 속도={16:F6} 연속S={17:F6} 연속F={18:F6} 레짐7={19} 전환의심={20} 청산판정={21} 시고저종={22}/{23}/{24}/{25}",
				Time[0], Instrument.FullName, regime.Regime[0],
				signals.EntryUpLong[0], signals.EntryUpShort[0],
				signals.EntryDnLong[0], signals.EntryDnShort[0],
				signals.EntrySideLong[0], signals.EntrySideShort[0],
				signals.WaitUpLong, signals.WaitUpShort, signals.WaitDnLong, DnShortWait(), signals.WaitSideLong, signals.WaitSideShort,
				contScore, contVel, contSlow, contFast, r7, r7Warn, contExitState, Open[0], High[0], Low[0], Close[0]) + " " + contDiag);

			// 화면 표시: 레짐 배경색과 밴드 조건 표시 (매매 판단과 무관)
			PaintBar();
			TradeTrack();

			// spec 7장: 진입 횟수는 대회 구간마다 0부터 센다. 진입 시간대에 들어서는 첫 봉에서 리셋한다
			bool inEntryWindow = InEntryWindow();
			if (inEntryWindow && !wasInEntryWindow)
			{
				entryCount	= 0;
				blockLong	= false;	// 재진입 제한도 대회 구간마다 푼다
				blockShort	= false;
			}
			if (inEntryWindow && !wasInEntryWindow)
			{
				sessStart	= double.NaN;	// 테스트 확장: 그날 효율비 (흔들리는 장 판단)
				sessPath	= 0;
				sessBars	= 0;
			}
			wasInEntryWindow = inEntryWindow;
			// 테스트 확장: 대회 구간 첫 봉 종가부터 지금까지 효율비 = |지금 종가 − 첫 종가| ÷ 봉마다 종가 변화의 합 (v6 spec 4-11 박스 장 감지와 같은 식)
			if (inEntryWindow)
			{
				if (double.IsNaN(sessStart))
					sessStart = Close[0];
				else
				{
					sessPath += Math.Abs(Close[0] - sessPrev);
					sessBars++;
				}
				sessPrev = Close[0];
			}

			// spec 7장: 종목별 진입 횟수와 최소 진입 목표를 화면에 표시. 분할 진입 여러 건은 OnPositionUpdate에서 신호 1회로 센다.
			// 최소 진입 미달이면 경고를 덧붙인다 (미달 대책 자체는 spec 9장 미정 → 표시만 한다)
			string entryStatus = entryCount >= MinEntries ? "" : string.Format(" (최소 {0} 미달)", MinEntries);
			string countText = string.Format("{0} 진입: {1}/{2}회{3}", Instrument.MasterInstrument.Name, entryCount, MinEntries, entryStatus);
			if (countText != lastCountText)	// 글만 만들고 그리기는 OnRender (계산은 OnBarUpdate, 그리기는 OnRender)
			{
				entryCountText	= countText;
				lastCountText	= countText;
			}

			// spec 7장: 종료 청산 — FlattenTime(23:27) 봉이 마감되면 남은 포지션을 전량 시장가로 청산한다
			if (IsFlattenTime())
			{
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 종료 청산({2}) 수량={3}",
						Time[0], Instrument.FullName, Position.MarketPosition, Position.Quantity));
					if (active != ActiveStrategy.None)
						FlattenRemaining("FLATTEN_TIME 종료 청산 시각");	// 전량 시장가. 손절·익절은 Reconcile이 거둠
					else
						FlattenLeftover();
				}
				CancelStrongBreakout("종료 청산 시각");
				Reconcile();
				UpdateStatusPanel();
				return;	// 종료 시각 이후에는 신규 진입·익절 판단을 하지 않는다
			}

			bool force = ForceNeeded();
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				// 미보유: 진입·익절·손절 상태 전체 리셋 (spec 1장 3, strategy.md)
				ResetTradeState();

				// 테스트 확장: 최소 진입 강제 채우기가 필요하면 다른 진입 대신 1계약
				if (force)
					EnterForce();
				// spec 1장 2 + 7장: 미보유 + 진입 허용 시간대(신호 봉 21:30~23:15)일 때만 신규 진입
				else if (InEntryWindow())
				{
					if (UseTestEntry)	RouteTestEntry();	// task5: 주문 흐름 검증용 임시 진입
					else				RouteEntry();
				}
				else
					CancelStrongBreakout("진입 시간 밖");
			}
			else
			{
				// 보유 중: 새 진입 신호는 무시 (spec 1장 2).
				// 손절 재계산·갭 청산·본절 이동은 체결 시점에 OnExecutionUpdate에서 처리한다 (spec 4장)
				if (gapFlattened || active == ActiveStrategy.None)
				{
					// 전량 청산 주문을 낸 뒤에도(갭 청산 포함) 포지션이 남아 있으면 다시 전량 청산한다.
					// 청산 주문이 거부됐거나 아직 체결되지 않은 경우다
					FlattenLeftover();
					Reconcile();
					UpdateStatusPanel();
					return;
				}

				// 테스트 확장: 최소 진입을 채우려고 지금 포지션을 정리 (강제 진입이면 다음 봉에 바로 청산하고 다시 진입)
				if (force)
				{
					FlattenRemaining(active == ActiveStrategy.Force
						? string.Format("FORCE_EXIT 최소 진입 {0}/{1}회 채우기", entryCount, MinEntries)
						: string.Format("FORCE_PREP 최소 진입 {0}/{1}회 채우기 (남은 봉 {2})", entryCount, MinEntries, MinutesToFlatten()));
					UpdateStatusPanel();
					return;
				}

				if (UseTestEntry)
				{
					// task5: 보유 봉 수 기반으로 분할 익절·본절·나머지 청산을 결정적으로 재현
					testHoldBars++;
					ManageTestExits();
				}
				else
				{
					// spec 4장 이익 보호 본절: 1차 익절 전이라도 유리폭이 기준에 닿으면 본절로 옮긴다
					CheckBreakevenAtR();

					// 현재가가 너무 가까워 미뤄 둔 본절 이동을 다시 시도한다
					RetryBreakeven();

					// 밴드 익절 지정가를 방금 마감된 봉의 밴드 값으로 옮긴다 (IntrabarTakeProfit)
					UpdateTargets();

					// 테스트 확장: 1차 익절 뒤 트레일링 손절
					UpdateTrailStop();

					// 테스트 확장: 청산용 판정이 끝·반대면 전량. 청산했으면 아래 상태 머신은 보지 않음
					if (!TrendChangeExit())
					{
						// spec 5장: 진입 시점 레짐의 익절·청산 상태 머신
						ManageExits();

						// 테스트 확장: 전환의심이면 50% 청산 (포지션당 한 번)
						WarnCheck();

						// 테스트 확장: 2차 익절 뒤 추세가 이어지면 한 번 불타기
						TryAddOn();
					}
				}

			}

			// 이번 봉에 바뀐 손절·익절·청산을 실제 주문에 반영
			Reconcile();

			// 화면 표시: 이번 봉의 판단이 끝난 뒤 상태를 갱신한다
			UpdateStatusPanel();
		}

		// spec 7장: 분할 진입 여러 건은 신호 1회. 미보유(Flat)→보유 전환 시점에만 1 증가시킨다
		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (lastPosition == MarketPosition.Flat && marketPosition != MarketPosition.Flat)
			{
				if (sbDir != 0)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 강한 돌파 스탑 주문 체결 {2}", Time[0], Instrument.FullName, sbDir > 0 ? "매수" : "매도"));
					sbDir = 0;	// 체결됨: 남은 조각 주문은 다시 걸지 않으니 봉 끝에 취소됨
				}
				entryCount++;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 신규 진입 #{2} 방향={3} 평균가={4} 수량={5}",
					Time[0], Instrument.FullName, entryCount, marketPosition, averagePrice, quantity));
			}

			// 거래 요약: 보유 → 미보유로 바뀌면 한 줄 쓴다
			if (lastPosition != MarketPosition.Flat && marketPosition == MarketPosition.Flat)
				TradeEnd();

			lastPosition = marketPosition;
			Reconcile();	// 포지션 수량이 바뀐 뒤 보호 주문을 다시 맞춤 (체결 콜백 안에서 포지션이 아직 안 바뀌었을 수 있음)
		}

		#region 진입 라우팅 (spec 3장·5장·6장)
		// 진입 시점 레짐으로 세부 전략을 고르고, 그 방향 신호가 있으면 분할 진입한다 (spec 3장).
		// 같은 봉에 롱·숏 신호가 동시에 나는 극단적 경우는 각 레짐의 주력 방향을 우선한다 (spec 미정, 사람 확인 필요).
		private void RouteEntry()
		{
			if (UseR7Regime)
			{
				RouteEntryR7();
				return;
			}
			int r = (int)regime.Regime[0];

			// spec 4장 재진입 제한: 손실 손절로 끝난 방향은 이번 대회 구간에서 다시 진입하지 않는다.
			// 주력 진입(5.1 상승추세 롱, 5.4 하락추세 숏)은 제한을 받지 않는다
			bool canLong	= !(BlockReentryAfterStop && blockLong);
			bool canShort	= !(BlockReentryAfterStop && blockShort);

			if (r == 1)			// 상승추세: 주력 롱(5.1) 우선, 역추세 숏(5.2)
			{
				// 실험 옵션 TrendT2Entry: 밴드 조건 없이 T2(MACD 전환 AND 크로스)만으로도 추세 방향 주력 진입
				if      (signals.EntryUpLong[0] || (TrendT2Entry && signals.T2Bull[0]))	EnterUpLong();
				else if (canShort && UseCounterTrend && signals.EntryUpShort[0])	EnterUpShort();
			}
			else if (r == -1)	// 하락추세: 주력 숏(5.4) 우선, 역추세 롱(5.3)
			{
				if      (DnShortSignal() || (TrendT2Entry && signals.T2Bear[0]))		EnterDnShort();
				// 실험 옵션 DnLongNeedDiv: 5.2와 대칭으로 조인 조건(저가 터치 + RSI 과매도 + 상승 다이버전스)일 때만 역추세 매수
				else if (canLong && UseCounterTrend && (DnLongNeedDiv ? signals.EntryDnLongDiv[0] : signals.EntryDnLong[0]))	EnterDnLong();
			}
			else				// 횡보(0): 롱(5.5)·숏(5.6)
			{
				if      (canLong && signals.EntrySideLong[0])	EnterSideLong();
				else if (canShort && signals.EntrySideShort[0])	EnterSideShort();
			}
		}

		// spec 5.4 진입 신호. 실험 옵션 DnShortNeedBoth를 켜면 반전 신호로 MACD 하락 전환과 데드크로스가 둘 다(T2) 있어야 한다
		private bool DnShortSignal()
		{
			return DnShortNeedBoth ? signals.EntryDnShortBoth[0] : signals.EntryDnShort[0];
		}

		// 화면 표시용: 위 신호의 T1 대기 상태
		private int DnShortWait()
		{
			return DnShortNeedBoth ? signals.WaitDnShortBoth : signals.WaitDnShort;
		}

		// 실시간·Playback 로그를 파일에도 남긴다 (LogToFile). 과거 봉과 백테스트에서는 파일에 쓰지 않는다
		private void Out(string text)
		{
			Print(text);
			if (!LogToFile || State != State.Realtime || logPath == null)
				return;
			try
			{
				System.IO.File.AppendAllText(logPath, text + Environment.NewLine);
			}
			catch (Exception)
			{
				// 파일을 못 써도 매매에는 영향이 없게 한다
			}
		}

		// spec 5.1 상승추세 롱(주력): 1/5 + 2/5 + 나머지
		private void EnterUpLong()
		{
			if (StopTooWide(true, "상승추세 롱(5.1)"))
				return;
			BeginTrade(true, 1, ActiveStrategy.UpLong);
			int q1 = Split(1, 5), q2 = Split(2, 5), q3 = tradeQty - q1 - q2;
			// 테스트 확장 SkipPassedChunks: 종가가 이미 +2배(1차) 위면 1차 조각을, +3배(2차)도 위면 2차 조각도 사지 않음.
			// 사면 그 익절 지정가가 진입하자마자 체결돼 왕복 비용만 듦(재현기 45일 38건). 그 단계는 한 것으로 봄(본절은 안 옮김)
			if (SkipPassedChunks && Close[0] >= channels.Up2[0])
			{
				bool past3 = Close[0] >= channels.Up3[0];
				Out(string.Format("[{0}][{1}][TQ_Strategy] 진입가가 이미 +2배{2} 위 → {3} 조각은 사지 않음",
					Time[0], Instrument.FullName, past3 ? "·+3배" : "", past3 ? "1차·2차" : "1차"));
				q1 = 0;
				tp1Done = true;
				if (past3) { q2 = 0; strongMomentum = true; }
			}
			SubmitChunk("UpL1", q1);
			SubmitChunk("UpL2", q2);
			SubmitChunk("UpL3", q3);
			LogEntry("상승추세 롱(5.1)", q1, q2, q3);
		}

		// spec 5.2 상승추세 숏(역추세): 1/2 + 나머지
		private void EnterUpShort()
		{
			if (StopTooWide(false, "상승추세 숏(5.2)"))
				return;
			BeginTrade(false, 1, ActiveStrategy.UpShort);
			int q1 = Split(1, 2), q2 = tradeQty - q1;
			SubmitChunk("UpS1", q1);
			SubmitChunk("UpS2", q2);
			LogEntry("상승추세 숏(5.2)", q1, q2, 0);
		}

		// spec 5.3 하락추세 롱(역추세): 1/3 + 나머지
		private void EnterDnLong()
		{
			if (StopTooWide(true, "하락추세 롱(5.3)"))
				return;
			BeginTrade(true, -1, ActiveStrategy.DnLong);
			int q1 = Split(1, 3), q2 = tradeQty - q1;
			SubmitChunk("DnL1", q1);
			SubmitChunk("DnL2", q2);
			LogEntry("하락추세 롱(5.3)", q1, q2, 0);
		}

		// spec 5.4 하락추세 숏(주력): 1/3 + 1/3 + 나머지
		private void EnterDnShort()
		{
			if (StopTooWide(false, "하락추세 숏(5.4)"))
				return;
			BeginTrade(false, -1, ActiveStrategy.DnShort);
			int q1 = Split(1, 3), q2 = Split(1, 3), q3 = tradeQty - q1 - q2;
			// 테스트 확장 SkipPassedChunks: 종가가 이미 −2배(1차) 아래면 1차 조각을, −3배(2차)도 아래면 2차 조각도 사지 않음 (EnterUpLong과 같은 이유)
			if (SkipPassedChunks && Close[0] <= channels.Dn2[0])
			{
				bool past3 = Close[0] <= channels.Dn3[0];
				Out(string.Format("[{0}][{1}][TQ_Strategy] 진입가가 이미 −2배{2} 아래 → {3} 조각은 사지 않음",
					Time[0], Instrument.FullName, past3 ? "·−3배" : "", past3 ? "1차·2차" : "1차"));
				q1 = 0;
				tp1Done = true;
				if (past3) { q2 = 0; tp2Done = true; }
			}
			SubmitChunk("DnS1", q1);
			SubmitChunk("DnS2", q2);
			SubmitChunk("DnS3", q3);
			LogEntry("하락추세 숏(5.4)", q1, q2, q3);
		}

		// spec 5.5 횡보 롱: 1/2 + 나머지
		private void EnterSideLong()
		{
			if (StopTooWide(true, "횡보 롱(5.5)"))
				return;
			BeginTrade(true, 0, ActiveStrategy.SideLong);
			int q1 = Split(1, 2), q2 = tradeQty - q1;
			SubmitChunk("SideL1", q1);
			SubmitChunk("SideL2", q2);
			LogEntry("횡보 롱(5.5)", q1, q2, 0);
		}

		// spec 5.6 횡보 숏: 1/2 + 나머지
		private void EnterSideShort()
		{
			if (StopTooWide(false, "횡보 숏(5.6)"))
				return;
			BeginTrade(false, 0, ActiveStrategy.SideShort);
			int q1 = Split(1, 2), q2 = tradeQty - q1;
			SubmitChunk("SideS1", q1);
			SubmitChunk("SideS2", q2);
			LogEntry("횡보 숏(5.6)", q1, q2, 0);
		}

		// 진입 공통: 상태 리셋 + 방향·레짐·전략 기록 + 신호 봉 종가 기준 손절가 계산 (spec 1장 3, 4장)
		private void BeginTrade(bool isLong, int regimeAtEntry, ActiveStrategy act)
		{
			ResetTradeState();
			isLongPos	= isLong;
			entryRegime	= regimeAtEntry;
			active		= act;
			// 실험 옵션 CounterQtyPercent: 역추세 진입(5.2, 5.3)은 수량을 줄여서 들어간다. 100이면 그대로
			tradeQty	= (act == ActiveStrategy.UpShort || act == ActiveStrategy.DnLong)
							? Math.Max(1, EntryQuantity * CounterQtyPercent / 100) : EntryQuantity;
			// 테스트 확장: 21:30 지표 발표 직후(EarlyQtyEndTime 전) 신호 봉의 진입 수량 상한
			if (EarlyQty > 0 && act != ActiveStrategy.Force && RelMinutes(ToTime(Time[0])) < RelMinutes(EarlyQtyEndTime) && tradeQty > EarlyQty)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 초반 수량: {2} → {3}계약 ({4} 전)", Time[0], Instrument.FullName, tradeQty, EarlyQty, EarlyQtyEndTime));
				tradeQty = EarlyQty;
			}
			TradeBegin(act);
			stopAtr		= atr[0];
			// 전고점·전저점은 신호 봉 기준 (spec 4장). 신호 봉 = 현재 마감된 봉([0])
			stopSwing	= isLong ? signals.SwingLow5[0] : signals.SwingHigh5[0];
			initialStop	= CalcStop(Close[0]);	// 신호 봉 종가를 진입가로 보고 계산 (spec 4장)
			stopPrice	= initialStop;
			// 테스트 확장: 1회 위험 상한. 신호 봉 종가 기준 손절 거리 × 계약당 가치 × 수량이 RiskCapUsd를 넘으면 수량을 줄임
			// (10/15 21:37 Playback: 손절 95pt × 30계약 = 위험 $5,700). 35일 재현: 상한 없음 $60,089 / $4,000 $56,872 / $2,000 $36,124
			double riskPts = Math.Abs(Close[0] - initialStop);
			double pv = Instrument.MasterInstrument.PointValue;
			if (RiskCapUsd > 0 && act != ActiveStrategy.Force && riskPts > 0 && pv > 0 && riskPts * pv * tradeQty > RiskCapUsd)
			{
				int before = tradeQty;
				tradeQty = Math.Max(1, (int)Math.Floor(RiskCapUsd / (riskPts * pv)));
				Out(string.Format("[{0}][{1}][TQ_Strategy] 위험 상한: 손절 {2:F2}pt × {3}계약 = ${4:N0} > ${5:N0} → {6}계약",
					Time[0], Instrument.FullName, riskPts, before, riskPts * pv * before, RiskCapUsd, tradeQty));
			}
			MlBegin(act);	// 학습 데이터: 신호 봉의 특징값 (매매 판단과 무관)
		}

		// spec 4·6장: 분할 조각을 기록한다. 주문은 SendEntryOrder가 조각 합계로 한 번 내고, 손절·익절은 체결 뒤 Reconcile이 건다
		private void SubmitChunk(string sig, int qty)
		{
			if (qty <= 0)
				return;
			chunkQty[sig] = qty;
			chunkOrder.Add(sig);
			liveSignals.Add(sig);
		}

		// 진입 주문 하나 (조각 합계). stopPx가 있으면 스탑 진입: 같은 이름 주문이 걸려 있으면 가격·수량만 고침
		private void SendEntryOrder(double stopPx = double.NaN, string reusePrefix = "")
		{
			int total = 0;
			foreach (string sig in chunkOrder)
				total += chunkQty[sig];
			if (total <= 0)
				return;
			if (reusePrefix.Length > 0)
				tradePrefix = reusePrefix;
			else
			{
				signalSeq++;
				tradePrefix = "P" + signalSeq + ".";
			}
			string name = tradePrefix + "E";
			OrderAction act = isLongPos ? OrderAction.Buy : OrderAction.SellShort;
			if (!double.IsNaN(stopPx))
			{
				double px = Instrument.MasterInstrument.RoundToTickSize(stopPx);
				if (IsWorking(entryOrder) && entryOrder.Name == name)
				{
					if (!IsChanging(entryOrder) && (entryOrder.StopPrice != px || entryOrder.Quantity != total))
						ChangeOrder(entryOrder, total, 0, px);
					return;
				}
				entryOrder = SubmitOrderUnmanaged(0, act, OrderType.StopMarket, total, 0, px, "", name);
				return;
			}
			entryOrder = SubmitOrderUnmanaged(0, act, OrderType.Market, total, 0, 0, "", name);
		}

		// spec 5장의 분할 익절 중 "밴드 가격" 조건이 있는 조각과 그 밴드. 가격 조건이 없는 조각은 NaN
		private double TargetBand(string sig)
		{
			switch (sig)
			{
				case "UpL1":	return channels.Up2[0];	// 5.1 1차 익절: +2배
				case "UpL2":	return channels.Up3[0];	// 5.1 강한 모멘텀 익절: +3배
				case "UpS1":	return channels.Dn1[0];	// 5.2 1차 익절: −1배
				case "DnL1":	return channels.Up1[0];	// 5.3 1차 익절: +1배
				case "DnS1":	return channels.Dn2[0];	// 5.4 1차 익절: −2배
				case "DnS2":	return channels.Dn3[0];	// 5.4 2차 익절: −3배
				case "SideL1":	return channels.Up2[0];	// 5.5 세트 A 1차 익절: +2배
				case "SideS1":	return channels.Dn2[0];	// 5.6 세트 A 1차 익절: −2배
				default:		return double.NaN;
			}
		}

		// IntrabarTakeProfit: 밴드 익절을 봉 마감을 기다리지 않고 지정가 주문으로 걸어 둔다.
		// 가격은 방금 마감된 봉의 밴드 값이고, 봉이 마감될 때마다 새 밴드 값으로 옮긴다 (Reconcile이 조각 남은 수량으로 건다).
		// 강한 돌파는 진입 때 정한 고정 가격(체결되면 체결가 기준으로 다시)
		private double TargetPrice(string sig)
		{
			double fixedPrice;
			if (fixedTarget.TryGetValue(sig, out fixedPrice))
				return fixedPrice;
			if (!IntrabarTakeProfit || UseTestEntry)
				return double.NaN;
			return TargetBand(sig);
		}

		private void SetTarget(string sig)
		{
			Reconcile();
		}

		// 보유 중 봉마다: 남아 있는 조각의 익절 지정가를 새 밴드 값으로 옮긴다
		private void UpdateTargets()
		{
			Reconcile();
		}

		// 익절 지정가 체결: 봉 마감 로직이 하던 상태 전환을 체결 시점에 한다
		private void OnTargetFill(string sig, double price, DateTime time)
		{
			// 부분 체결이 여러 번 올 수 있다. 상태 전환은 조각당 한 번만 한다 (조각을 끝내는 것은 체결 수량, ReduceChunks)
			if (sig == null || !chunkQty.ContainsKey(sig) || !stageFilled.Add(sig))
				return;

			Out(string.Format("[{0}][{1}][TQ_Strategy] 밴드 익절(지정가) 체결 시그널={2} 가격={3}",
				time, Instrument.FullName, sig, price));

			if (sig == "DnS2" || sig == "SB2")
			{
				tp2Done = true;			// spec 5.4 2차 익절 (강한 돌파 2차도 같음). 본절 이동은 1차에서 한다
				return;
			}

			tp1Done = true;
			if (sig == "UpL2")
				strongMomentum = true;	// spec 5.1: +3배 익절 뒤에는 약한 모멘텀 청산을 적용하지 않는다
			else if (sig == "SideL1" || sig == "SideS1")
				sideSet = SideSet.A;	// spec 5.5·5.6: 밴드 가격 조건은 세트 A의 1차 익절이다

			// spec 4장 본절: 첫 부분 익절이 체결되면 남은 수량의 손절을 평균 진입가로
			breakevenPending = true;
			OnFirstTakeProfitFill(price, time);
		}

		// spec 6장: 진입 수량 × (num/den)을 내림. 양수 정수 나눗셈이 곧 내림이다
		private int Split(int num, int den)
		{
			return (tradeQty * num) / den;
		}

		// 진입 주문을 내고 기록한다 (Enter* 공통 마지막 단계)
		private void LogEntry(string name, int q1, int q2, int q3)
		{
			SendEntryOrder();
			Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 {2} 수량={3}/{4}/{5} (레짐={6}) 손절={7} 주문={8}E",
				Time[0], Instrument.FullName, name, q1, q2, q3, entryRegime, stopPrice, tradePrefix));
		}
		#endregion

		#region 손절·본절 (spec 4장 — task3)
		// spec 4장 손절가: 전저점(롱)/전고점(숏)과 진입가 ±StopPercent% 중 진입가에 가까운 쪽.
		// 롱은 둘 중 높은 값, 숏은 둘 중 낮은 값. 전고점·전저점이 없으면(NaN) %만 쓴다(방어).
		private double CalcStop(double entryPrice)
		{
			// 테스트 확장: 강한 돌파는 진입가 바로 옆 (돌파 가격 아래로 돌아오면 실패한 돌파)
			if (active == ActiveStrategy.Breakout)
				return Instrument.MasterInstrument.RoundToTickSize(isLongPos ? entryPrice - SbStopPoints : entryPrice + SbStopPoints);
			return CalcStop(entryPrice, isLongPos, stopSwing, stopAtr);
		}

		// 위 계산을 상태 필드 없이 한다. 진입 전 손절 거리 상한 검사(MaxStopPoints·MaxStopAtr)에서도 쓴다
		private double CalcStop(double entryPrice, bool isLong, double swing, double atrValue)
		{
			double pct = isLong
				? entryPrice * (1 - StopPercent / 100.0)
				: entryPrice * (1 + StopPercent / 100.0);
			if (double.IsNaN(swing))		// 지표 미완성·데이터 부족 방어
				return FloorStop(entryPrice, pct, isLong, atrValue);
			if (!UseSwingStop)				// 실험 옵션: 전고점·전저점을 쓰지 않고 %만 쓴다
				return FloorStop(entryPrice, pct, isLong, atrValue);
			// 실험 옵션 StopUseFarthest (명세에 없음): %로 손절을 당기지 않고, 전저점·전고점과 ATR × MinStopAtr 중 진입가에서 먼 쪽을 쓴다.
			// 손절 범위를 넓혀 추세를 더 길게 가져가기 위함 (2026-10-22). ATR 쪽은 FloorStop이 맞춘다
			if (StopUseFarthest)
				return FloorStop(entryPrice, swing, isLong, atrValue);
			return FloorStop(entryPrice, isLong ? Math.Max(swing, pct) : Math.Min(swing, pct), isLong, atrValue);
		}

		// 실험 옵션 MinStopAtr: 손절 거리가 신호 봉 ATR × MinStopAtr보다 짧으면 그 거리까지 벌린다. 0이면 그대로 둔다.
		// 신호 봉이 최근 5봉의 저점(롱)·고점(숏) 근처에서 마감하면 손절 거리가 0에 가까워지는 것을 막는다
		private double FloorStop(double entryPrice, double stop, bool isLong, double atrValue)
		{
			if (MinStopAtr > 0 && !double.IsNaN(atrValue) && atrValue > 0)
			{
				double minDist = MinStopAtr * atrValue;
				if (Math.Abs(entryPrice - stop) < minDist)
					stop = isLong ? entryPrice - minDist : entryPrice + minDist;
			}
			return Instrument.MasterInstrument.RoundToTickSize(stop);
		}

		// 실험 옵션 MaxStopPoints·MaxStopAtr (명세에 없음): 신호 봉 종가 기준으로 계산한 손절 거리가 상한을 넘으면 진입하지 않는다.
		// 손절 계산은 진입 때와 같다(전저점·전고점, %, MinStopAtr 하한 포함). 둘 다 0이면 검사하지 않는다.
		// 건너뛴 신호도 그 밴드 조건의 진입 기회를 쓴 것으로 본다 (spec 2장 T1: 진입하지 못한 경우도 포함)
		private bool StopTooWide(bool isLong, string name)
		{
			if (MaxStopPoints <= 0 && MaxStopAtr <= 0)
				return false;

			double a	= atr[0];
			double s	= CalcStop(Close[0], isLong, isLong ? signals.SwingLow5[0] : signals.SwingHigh5[0], a);
			double dist	= Math.Abs(Close[0] - s);
			bool overPt		= MaxStopPoints > 0 && dist > MaxStopPoints;
			bool overAtr	= MaxStopAtr > 0 && !double.IsNaN(a) && a > 0 && dist > MaxStopAtr * a;
			if (!overPt && !overAtr)
				return false;

			Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 건너뜀 {2}: 손절 거리 {3:N2}pt (ATR {4:N2}의 {5:N2}배) > 상한 (pt {6}, ATR 배수 {7})",
				Time[0], Instrument.FullName, name, dist, a, a > 0 ? dist / a : double.NaN, MaxStopPoints, MaxStopAtr));
			return true;
		}

		// 실험 옵션 SideRestBand: 횡보 세트 A의 나머지 청산 밴드 (spec 5.5·5.6은 3배).
		// SideRestNarrowTime부터 종료 청산 시각까지는 SideRestBand배(0 = 중심선)를 쓰고, 그 전에는 3배를 쓴다
		private int SideRestLevel()
		{
			if (SideRestBand >= 3)
				return 3;
			return IsTimeInWindow(ToTime(Time[0]), SideRestNarrowTime, FlattenTime) ? SideRestBand : 3;
		}

		private double BandUp(int level)
		{
			switch (level)
			{
				case 0:		return channels.Mid[0];
				case 1:		return channels.Up1[0];
				case 2:		return channels.Up2[0];
				default:	return channels.Up3[0];
			}
		}

		private double BandDn(int level)
		{
			switch (level)
			{
				case 0:		return channels.Mid[0];
				case 1:		return channels.Dn1[0];
				case 2:		return channels.Dn2[0];
				default:	return channels.Dn3[0];
			}
		}

		private string BandText(int level, bool up)
		{
			return level == 0 ? "중심선" : (up ? "+" : "−") + level + "배";
		}

		// 진입 밴드 조건을 화면에 적는 말: BandTouchEntry면 고가·저가 터치, 아니면 종가 돌파·이탈
		private string BandHow(bool up)
		{
			return BandTouchEntry ? (up ? " 고가 터치" : " 저가 터치") : (up ? " 돌파" : " 이탈");
		}

		// 손절가를 price로: 보호 손절 주문 하나(보유 수량 전체)를 Reconcile이 맞춘다
		private void SetStopForLive(double price)
		{
			stopPrice = price;
			Reconcile();
		}

		// 주문으로 걸어둘 스탑 가격: 기준가(신호 봉 종가)와 같거나 넘어서면 주문이 거부될 수 있어 최소 1틱 띄운다.
		// 손절 규칙(spec 4장)의 값은 CalcStop이 정하고, 이 함수는 체결 전 임시 주문 가격에만 쓴다
		private double OrderSafeStop(double stop, double refPrice)
		{
			return isLongPos ? Math.Min(stop, refPrice - TickSize) : Math.Max(stop, refPrice + TickSize);
		}

		// 주문 오류 처리 (HandleOrderErrors). 켜져 있으면 NinjaTrader가 전략을 끄지 않으므로 여기서 포지션을 지킨다.
		//  · 손절 가격 변경만 거부됨: 주문은 이전 가격에 살아 있다 → 내부 상태를 실제 가격으로 되돌리고, 본절 이동이었으면 나중에 다시 시도
		//  · 손절 주문 자체가 거부됨: 손절 없는 포지션이 된다 → 즉시 전량 청산
		//  · 진입 주문 거부: 그 조각은 없는 것으로 한다
		//  · 익절 지정가·시장가 청산 오류: 기록만 한다. 전량 청산이 안 됐으면 다음 봉 마감에서 다시 낸다 (OnBarUpdate)
		protected override void OnOrderUpdate(Order order, double limitPrice, double orderStopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (order == null)
				return;
			ToRealtimeOrders();
			string name = order.Name;
			// 주문 객체는 이름으로 잡는다: 제출 직후에는 돌려받은 참조가 아직 안 채워졌을 수 있음 (NT8 Unmanaged 문서)
			if (tradePrefix.Length > 0 && name.StartsWith(tradePrefix))
			{
				string rest = name.Substring(tradePrefix.Length);
				if (rest == "E" && (entryOrder == null || entryOrder == order || !IsWorking(entryOrder)))	entryOrder = order;
				else if (rest == "S")										stopOrder = order;
				else if (rest.EndsWith("T") && chunkQty.ContainsKey(rest.Substring(0, rest.Length - 1)))
					tpOrders[rest.Substring(0, rest.Length - 1)] = order;
			}
			bool rejected	= orderState == OrderState.Rejected;
			bool gone		= orderState == OrderState.Cancelled || rejected;
			if (gone && addSig.Length > 0 && tradePrefix.Length > 0 && name == tradePrefix + "A" && order.Filled == 0 && ChunkLeft(addSig) == 0)
			{
				// 불타기 주문이 체결 없이 거부·취소됨: 그 조각을 지움 (보유 수량·손절은 그대로)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 불타기 주문 {2} → 불타기 없음", time, Instrument.FullName, orderState));
				liveSignals.Remove(addSig);
				chunkOrder.Remove(addSig);
				chunkQty.Remove(addSig);
			}
			if (gone && exitOrderChunks.ContainsKey(name))
				plannedExitQty = Math.Max(0, plannedExitQty - Math.Max(0, order.Quantity - order.Filled));	// 체결 안 된 청산 수량은 계획에서 뺌

			if (rejected || error != ErrorCode.NoError)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 주문 오류 이름={2} 상태={3} 오류={4} 내용={5} 스탑가={6} 수량={7}",
					time, Instrument.FullName, name, orderState, error, comment, order.StopPrice, quantity));

			if (tradePrefix.Length > 0 && name == tradePrefix + "S")
			{
				if (rejected && PosQty() > 0)
				{
					// 손절 없이 둘 수 없음 → 한 번만 전량 청산 (이전: 조각·부분 체결마다 거부 → 청산이 되풀이됨, 10/15 22:55)
					if (!stopRejectHandled)
					{
						stopRejectHandled	= true;
						gapFlattened		= true;
						Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 주문 거부 → 손절 없이 둘 수 없어 즉시 전량 청산", time, Instrument.FullName));
						FlattenRemaining("STOP 손절 주문 거부");
					}
				}
				else if (error == ErrorCode.UnableToChangeOrder)
				{
					stopPrice = order.StopPrice;	// 실제로 걸려 있는 가격
					if (breakevenDone)
					{
						breakevenDone		= false;
						breakevenDeferred	= true;	// 봉 마감마다 RetryBreakeven이 다시 시도한다
					}
					Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 가격 변경 거부 → 기존 손절 {2} 유지", time, Instrument.FullName, stopPrice));
				}
			}
			else if (rejected && tradePrefix.Length > 0 && name == tradePrefix + "E" && PosQty() == 0)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 주문 거부 → 이번 진입 없음", time, Instrument.FullName));
				sbDir = 0;
				ResetTradeState();
			}
			if (gone || orderState == OrderState.Accepted || orderState == OrderState.Working)
				Reconcile();
		}

		#region 비관리형 주문 맞추기 (v6-box에서 옮김)
		// 실시간으로 넘어올 때 과거 처리 중 낸 주문 참조를 실시간 주문으로 바꾼다 (NT8 GetRealtimeOrder 문서)
		private void ToRealtimeOrders()
		{
			if (State != State.Realtime)
				return;
			if (entryOrder != null && entryOrder.IsBacktestOrder)	entryOrder = GetRealtimeOrder(entryOrder);
			if (stopOrder != null && stopOrder.IsBacktestOrder)		stopOrder = GetRealtimeOrder(stopOrder);
			foreach (string sig in new List<string>(tpOrders.Keys))
				if (tpOrders[sig] != null && tpOrders[sig].IsBacktestOrder)
					tpOrders[sig] = GetRealtimeOrder(tpOrders[sig]);
		}

		private static bool IsWorking(Order o)
		{
			if (o == null)
				return false;
			OrderState st = o.OrderState;
			return st == OrderState.Initialized || st == OrderState.Submitted || st == OrderState.Accepted || st == OrderState.TriggerPending
				|| st == OrderState.Working || st == OrderState.ChangePending || st == OrderState.ChangeSubmitted || st == OrderState.PartFilled;
		}

		private static bool IsGoingAway(Order o)
		{
			return o != null && (o.OrderState == OrderState.CancelPending || o.OrderState == OrderState.CancelSubmitted);
		}

		// 바꾸는 중이거나 아직 접수 전인 주문은 다시 고치지 않는다 (접수되면 OnOrderUpdate → Reconcile이 다시 맞춤)
		private static bool IsChanging(Order o)
		{
			return o.OrderState == OrderState.Initialized || o.OrderState == OrderState.Submitted || o.OrderState == OrderState.ChangePending
				|| o.OrderState == OrderState.ChangeSubmitted || IsGoingAway(o);
		}

		private void CancelIfWorking(Order o)
		{
			if (IsWorking(o))
				CancelOrder(o);
		}

		private int PosQty()	{ return Position.MarketPosition == MarketPosition.Flat ? 0 : Position.Quantity; }
		private int PosSide()	{ return Position.MarketPosition == MarketPosition.Long ? 1 : Position.MarketPosition == MarketPosition.Short ? -1 : 0; }
		private int TradeDir()	{ return chunkOrder.Count == 0 && entryFillQty == 0 ? 0 : (isLongPos ? 1 : -1); }

		private int ChunkLeft(string sig)
		{
			int q;
			return chunkLeft.TryGetValue(sig, out q) ? q : 0;
		}

		// 실제 보유 수량에 맞춰 보호 주문을 맞춘다. 체결·주문 상태 변화·봉 마감마다 부른다 (안에서 다시 불리면 끝난 뒤 한 번 더)
		//  · 미보유 → 손절·익절 취소
		//  · 계획에 없는 포지션(방향이 다르거나 거래 정보 없음) → 손절·익절을 거두고 시장가로 정리
		//  · 그 외 → 손절 1개 = 보유 − 아직 체결 안 된 시장가 청산, 익절 = 조각 순서로 그 수량 안에서 조각 남은 수량
		private void Reconcile()
		{
			if (inReconcile)
			{
				reconcileAgain = true;
				return;
			}
			inReconcile = true;
			try
			{
				for (int n = 0; n < 4; n++)
				{
					reconcileAgain = false;
					ReconcileOnce();
					if (!reconcileAgain)
						break;
				}
			}
			finally
			{
				inReconcile = false;
			}
		}

		private void ReconcileOnce()
		{
			int P = PosQty(), side = PosSide(), dir = TradeDir();
			if (P == 0)
			{
				CancelIfWorking(stopOrder);
				foreach (Order t in new List<Order>(tpOrders.Values))
					CancelIfWorking(t);
				return;
			}
			if (dir == 0 || side != dir)
			{
				CancelIfWorking(stopOrder);
				foreach (Order t in new List<Order>(tpOrders.Values))
					CancelIfWorking(t);
				if (plannedExitQty <= 0)
				{
					string name = "Fix" + (++exitSeq);
					exitOrderChunks[name] = new List<string>();
					plannedExitQty += P;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 계획에 없는 포지션 {2} {3}계약 → 시장가 정리", Time[0], Instrument.FullName, Position.MarketPosition, P));
					SubmitOrderUnmanaged(0, side > 0 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Market, P, 0, 0, "", name);
				}
				return;
			}
			int need = Math.Max(0, P - plannedExitQty);
			OrderAction exitAct = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;

			// ── 보호 손절: 보유 수량 전체 하나 ──
			if (need > 0 && !double.IsNaN(stopPrice))
			{
				double px = Instrument.MasterInstrument.RoundToTickSize(stopPrice);
				bool beyond = dir > 0 ? px >= GetCurrentBid(0) : px <= GetCurrentAsk(0);
				if (beyond && !IsWorking(stopOrder))
				{
					// 걸려는 손절가를 현재가가 이미 지남: 걸 수 없으니 바로 청산 (로그는 한 번)
					if (!gapFlattened)
					{
						gapFlattened = true;
						Out(string.Format("[{0}][{1}][TQ_Strategy] 손절가 {2}를 현재가가 이미 지남 → 전량 청산", Time[0], Instrument.FullName, px));
					}
					FlattenRemaining(breakevenDone ? "BE_STOP 손절가를 이미 지남" : "STOP 손절가를 이미 지남");
					return;
				}
				if (IsGoingAway(stopOrder))
				{
					// 취소 중인 손절이 끝나면 다시 건다 (두 개가 겹치지 않게)
				}
				else if (!IsWorking(stopOrder))
					stopOrder = SubmitOrderUnmanaged(0, exitAct, OrderType.StopMarket, need, 0, px, "", tradePrefix + "S");
				else if (!IsChanging(stopOrder) && !beyond && (stopOrder.StopPrice != px || stopOrder.Quantity - stopOrder.Filled != need))
					ChangeOrder(stopOrder, stopOrder.Filled + need, 0, px);
			}
			else
				CancelIfWorking(stopOrder);

			// ── 익절 지정가: 조각 순서로, 손절이 덮는 수량 안에서 ──
			// 목록 복사본으로 돈다: 주문을 내고 취소하면 Playback은 그 자리에서 OnOrderUpdate·OnExecutionUpdate를 불러 목록을 고칠 수 있음
			int room = need;
			foreach (string sig in new List<string>(chunkOrder))
			{
				double tp = TargetPrice(sig);
				Order t;
				tpOrders.TryGetValue(sig, out t);
				int want = 0;
				if (!double.IsNaN(tp) && entryFillQty > 0 && liveSignals.Contains(sig) && !exitSent.Contains(sig))
					want = Math.Min(Math.Max(0, ChunkLeft(sig)), room);
				room -= want;
				if (want <= 0)
				{
					CancelIfWorking(t);
					continue;
				}
				double lp = Instrument.MasterInstrument.RoundToTickSize(tp);
				if (IsGoingAway(t))
					continue;
				if (!IsWorking(t))
					tpOrders[sig] = SubmitOrderUnmanaged(0, exitAct, OrderType.Limit, want, lp, 0, "", tradePrefix + sig + "T");
				else if (!IsChanging(t) && (t.LimitPrice != lp || t.Quantity - t.Filled != want))
				{
					if (t.Filled > 0 && t.Quantity - t.Filled < want)
						continue;	// 일부 체결된 지정가는 늘리지 않음
					ChangeOrder(t, t.Filled + want, lp, 0);
				}
			}
		}

		// 진입 체결을 조각 순서로 나눈다. 계획보다 많이 체결되면(겹친 진입) 마지막 조각에
		private void AllocateEntry(int q)
		{
			string last = null;
			foreach (string sig in chunkOrder)
			{
				last = sig;
				int f;
				chunkFilled.TryGetValue(sig, out f);
				int take = Math.Min(q, Math.Max(0, chunkQty[sig] - f));
				if (take <= 0)
					continue;
				chunkFilled[sig]	= f + take;
				chunkLeft[sig]		= ChunkLeft(sig) + take;
				q -= take;
				if (active != ActiveStrategy.None && !exitSent.Contains(sig) && !liveSignals.Contains(sig))
					liveSignals.Add(sig);
				if (q <= 0)
					break;
			}
			if (q > 0 && last != null)
				chunkLeft[last] = ChunkLeft(last) + q;
		}

		private int ReduceChunk(string sig, int q, double price, DateTime time, string name)
		{
			int left = ChunkLeft(sig);
			int take = Math.Min(q, Math.Max(0, left));
			if (take <= 0)
				return 0;
			chunkLeft[sig] = left - take;
			TradeExitFill(name, price, take, time, sig == addSig && addFillQty > 0 ? addFillSum / addFillQty : double.NaN);
			if (chunkLeft[sig] <= 0)
				liveSignals.Remove(sig);
			return take;
		}

		// 청산 체결 수량을 조각에서 뺀다: 지정한 조각 먼저, 그다음 먼 조각부터 (조각 번호 3 → 2 → 1)
		private void ReduceChunks(int q, double price, DateTime time, string name, List<string> first)
		{
			if (first != null)
				foreach (string sig in first)
				{
					if (q <= 0)
						break;
					q -= ReduceChunk(sig, q, price, time, name);
				}
			List<string> order = new List<string>(chunkOrder);
			order.Sort((x, y) => ChunkStage(y).CompareTo(ChunkStage(x)));
			foreach (string sig in order)
			{
				if (q <= 0)
					break;
				q -= ReduceChunk(sig, q, price, time, name);
			}
			if (q > 0)
				TradeExitFill(name, price, q, time);	// 조각 기록 밖 수량 (계획에 없는 포지션 정리)
		}

		// 시장가 청산 주문. 먼저 계획 수량에 넣고 Reconcile로 손절 수량을 줄인 뒤 낸다 (손절과 같이 체결돼 넘치지 않게)
		private void SubmitMarketExit(int q, List<string> chunks)
		{
			if (q <= 0 || PosQty() == 0)
				return;
			int side = PosSide();
			string name = "X" + (++exitSeq);
			exitOrderChunks[name] = chunks ?? new List<string>();
			plannedExitQty += q;
			Reconcile();
			SubmitOrderUnmanaged(0, side > 0 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Market, q, 0, 0, "", name);
		}

		// 청산을 결정했는데 포지션이 남아 있으면 (청산 주문 거부·미체결·늦은 진입 체결) 남은 수량을 다시 시장가로. 낸 청산 주문이 아직 살아 있으면 기다린다
		private void FlattenLeftover()
		{
			if (PosQty() == 0 || plannedExitQty > 0)
				return;
			int q = PosQty();
			SubmitMarketExit(q, null);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 남은 포지션 청산 {2} {3}계약", Time[0], Instrument.FullName, Position.MarketPosition, q));
		}
		#endregion

		// 체결 시점 처리 (spec 4장): 진입 체결 → 갭 판정과 손절 재계산, 첫 부분 익절 체결 → 본절 이동
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;
			Order o			= execution.Order;
			string name		= o.Name;
			bool isEntry	= o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.SellShort;
			int ed			= o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover ? 1 : -1;	// 이 체결의 매수(+1)·매도(−1)
			int dir			= TradeDir();
			bool isStop		= tradePrefix.Length > 0 && name == tradePrefix + "S";
			string tsig		= tradePrefix.Length > 0 && name.StartsWith(tradePrefix) && name.EndsWith("T")
								? name.Substring(tradePrefix.Length, name.Length - tradePrefix.Length - 1) : "";

			// 화면 체결 라벨 (MarkExecution은 이름으로 종류를 고름)
			MarkExecution(isEntry && chunkOrder.Count > 0 ? chunkOrder[0] : isStop ? "Stop loss" : tsig.Length > 0 ? "Profit target" : name,
				price, quantity, ed > 0 ? MarketPosition.Long : MarketPosition.Short, time);

			if (isEntry)
			{
				if (addSig.Length > 0 && tradePrefix.Length > 0 && name == tradePrefix + "A" && dir != 0 && ed == dir)
				{
					// 불타기 체결: 불타기 조각에만 넣고, 손절은 다시 계산하지 않음 (지금 손절·트레일링이 추가분까지 덮음)
					int f;
					chunkFilled.TryGetValue(addSig, out f);
					chunkFilled[addSig]	= f + quantity;
					chunkLeft[addSig]	= ChunkLeft(addSig) + quantity;
					addFillSum			+= price * quantity;
					addFillQty			+= quantity;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 불타기 체결 가격={2} 수량={3} 보유={4} 손절={5}",
						time, Instrument.FullName, price, quantity, PosQty(), stopPrice));
					if (gapFlattened || active == ActiveStrategy.None || !liveSignals.Contains(addSig))
						FlattenRemaining("늦게 온 불타기 체결분 청산");	// 이미 청산하기로 한 뒤 체결됨
				}
				else if (dir != 0 && ed == dir && (name == tradePrefix + "E" || active != ActiveStrategy.None))
				{
					AllocateEntry(quantity);
					OnEntryFill(price, quantity, time);
				}
				else
					Out(string.Format("[{0}][{1}][TQ_Strategy] 계획에 없는 진입 체결 {2} 가격={3} 수량={4} → 정리", time, Instrument.FullName, name, price, quantity));
			}
			else
			{
				// 청산 체결은 모두 로그에 남긴다 (익절·손절·전량 청산이 얼마에 체결됐는지 나중에 확인하기 위함)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 청산 체결 {2} 가격={3} 수량={4}", time, Instrument.FullName, name, price, quantity));
				List<string> chunks;
				// 조각 수량을 먼저 줄이고 단계 처리(손절 이동 → Reconcile)를 한다. 거꾸로 하면 Reconcile이 아직 남은 줄 알고 익절을 다시 냄
				if (isStop)
				{
					ReduceChunks(quantity, price, time, "Stop loss", null);
					OnStopLossFill(price, time);
				}
				else if (tsig.Length > 0 && chunkQty.ContainsKey(tsig))
				{
					ReduceChunks(quantity, price, time, "Profit target", new List<string> { tsig });
					OnTargetFill(tsig, price, time);
				}
				else if (exitOrderChunks.TryGetValue(name, out chunks))
				{
					plannedExitQty = Math.Max(0, plannedExitQty - quantity);
					ReduceChunks(quantity, price, time, "x" + name, chunks);
					if (breakevenPending)
						OnFirstTakeProfitFill(price, time);	// 봉 마감 분할 익절(시장가) 체결 → 본절 이동
				}
				else
					ReduceChunks(quantity, price, time, name, null);
			}
			Reconcile();
			UpdateStatusPanel();
		}

		// spec 4장 재진입 제한: 손절이 손실로 체결되면 이번 대회 구간에서 같은 방향 진입을 막는다.
		// 본절 손절(손실 없음)은 막지 않는다
		private void OnStopLossFill(double exitPrice, DateTime time)
		{
			if (entryFillQty <= 0)
				return;

			// 본절로 옮긴 뒤의 손절은 체결가가 미끄러져 평균가보다 조금 불리해도 본절 손절로 본다 (spec 4장: 본절 손절은 제외)
			if (breakevenDone)
				return;

			double avg	= entryFillSum / entryFillQty;
			bool loss	= isLongPos ? exitPrice < avg : exitPrice > avg;
			if (!loss)
				return;

			// 주력 진입(5.1, 5.4)의 손절은 재진입 제한을 걸지 않는다. 추세 방향 진입은 여러 번 시도하는 것이 전략의 취지다
			if (active == ActiveStrategy.UpLong || active == ActiveStrategy.DnShort)
				return;
			// 테스트 확장: 강한 돌파·강제 채우기 손절도 횡보 재진입 제한을 걸지 않음
			if (active == ActiveStrategy.Breakout || active == ActiveStrategy.Force)
				return;

			bool already = isLongPos ? blockLong : blockShort;
			if (isLongPos)	blockLong = true;
			else			blockShort = true;

			if (!already && BlockReentryAfterStop)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 손실 손절 → 이번 대회 구간 {2} 재진입 금지",
					time, Instrument.FullName, isLongPos ? "롱" : "숏"));
		}

		// spec 4장 이익 보호 본절: 보유 중 최대 유리폭이 처음 손절 거리(1R)의 BreakevenAtR배에 닿으면
		// 1차 익절 전이라도 손절을 평균 진입가로 옮긴다. 봉 마감 시 그 봉의 고가(롱)·저가(숏)로 판단한다
		private void CheckBreakevenAtR()
		{
			// 주력 진입(5.1, 5.4)에는 적용하지 않는다. 추세를 길게 끌고 가는 거래라 진입가를 되짚는 움직임을 버텨야 한다
			if (active == ActiveStrategy.UpLong || active == ActiveStrategy.DnShort)
				return;

			// 테스트 확장: 강한 돌파는 손절이 진입가 바로 옆이라 이익 보호 본절을 쓰지 않음 (1차 뒤 평단·트레일링). 강제 채우기도 안 씀
			if (active == ActiveStrategy.Breakout || active == ActiveStrategy.Force)
				return;

			if (BreakevenAtR <= 0 || breakevenDone || liveSignals.Count == 0 || entryFillQty <= 0
				|| double.IsNaN(initialRisk) || initialRisk <= 0)
				return;

			double avg	= entryFillSum / entryFillQty;
			double be	= Instrument.MasterInstrument.RoundToTickSize(avg);

			double favorable = isLongPos ? High[0] - avg : avg - Low[0];
			if (favorable >= BreakevenAtR * initialRisk)
				reachedR = true;
			if (!reachedR)
				return;

			// 스탑 가격이 현재가와 같거나 넘어서면 주문이 거부될 수 있어, 종가가 평균 진입가보다 유리할 때만 옮긴다
			if (!SafeToMoveStop(be))
				return;

			stopPrice		= be;
			breakevenDone	= true;
			SetStopForLive(be);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 이익 보호 본절 이동 손절→평균가={2} (유리폭 기준 {3})",
				Time[0], Instrument.FullName, be, BreakevenAtR * initialRisk));
		}

		// spec 4장: 체결되면 실제 체결가(평균)로 손절을 다시 계산한다.
		// 갭으로 체결가가 이미 걸어둔 손절가(신호 봉 종가 기준)를 넘어섰으면 즉시 전량 청산한다
		private void OnEntryFill(double fillPrice, int fillQty, DateTime time)
		{
			entryFillSum += fillPrice * fillQty;
			entryFillQty += fillQty;

			if (gapFlattened || active == ActiveStrategy.None)
			{
				// 이미 전량 청산하기로 한 뒤 늦게 온 진입 체결: 그 수량만 더 청산
				FlattenRemaining("늦게 온 진입 체결분 청산");
				return;
			}
			bool breached = !double.IsNaN(initialStop)
				&& (isLongPos ? fillPrice <= initialStop : fillPrice >= initialStop);
			if (breached)
			{
				gapFlattened = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 갭 손절 초과 → 즉시 전량 청산 체결가={2} 손절={3}",
					time, Instrument.FullName, fillPrice, initialStop));
				FlattenRemaining("GAP 갭 손절 초과");
				return;
			}

			// 본절이 이미 이동했으면 체결가 기준으로 되돌리지 않는다
			if (breakevenDone)
				return;

			double avg	= entryFillSum / entryFillQty;
			double s	= CalcStop(avg);

			// 다시 계산한 손절가에 현재가가 이미 닿았으면 스탑을 그쪽으로 옮길 수 없다(주문 거부 → 전략 종료).
			// 손절가를 넘어선 것으로 보고 갭 규칙과 같이 즉시 전량 청산한다 (spec 4장)
			if (isLongPos ? GetCurrentBid(0) <= s : GetCurrentAsk(0) >= s)
			{
				gapFlattened = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 체결 직후 현재가가 손절가를 넘어섬 → 즉시 전량 청산 손절={2}",
					time, Instrument.FullName, s));
				FlattenRemaining("spec 4장 손절가 초과");
				return;
			}

			stopPrice	= s;
			initialRisk	= Math.Abs(avg - s);	// 이익 보호 본절의 기준 거리 (1R)
			SetStopForLive(s);
			trStop0		= s;
			if (active == ActiveStrategy.Breakout)
				ResetBreakoutTargets(avg);	// 테스트 확장: 강한 돌파 익절도 체결가 기준으로
			Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 재계산(체결가 기준) 평균 체결가={2} 손절={3}",
				time, Instrument.FullName, avg, s));
		}

		// spec 4장 본절: 첫 부분 익절이 체결되면 남은 수량의 손절을 평균 진입가로 옮긴다.
		// 익절 주문을 낼 때 이 함수로 예약만 하고, 실제 이동은 체결 시 OnFirstTakeProfitFill에서 한다
		private void MoveBreakeven()
		{
			if (breakevenDone)
				return;
			breakevenPending = true;
		}

		// 체결 시점 가격이 평균 진입가보다 유리하지 않으면(손실 중이거나 같으면) 원래 손절가를 유지한다 (spec 4장).
		// 같을 때도 유지하는 것은 스탑 가격이 현재가와 같으면 주문이 거부될 수 있기 때문이다
		private void OnFirstTakeProfitFill(double exitPrice, DateTime time)
		{
			breakevenPending = false;
			initialRisk		= double.NaN;
			reachedR		= false;
			if (breakevenDone || liveSignals.Count == 0)
				return;

			double avg	= entryFillQty > 0 ? entryFillSum / entryFillQty : Position.AveragePrice;
			double be	= Instrument.MasterInstrument.RoundToTickSize(avg);

			bool favorable = isLongPos ? exitPrice > be : exitPrice < be;
			if (!favorable)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 보류(손실 중) 평균가={2} 익절 체결가={3} 손절 유지={4}",
					time, Instrument.FullName, be, exitPrice, stopPrice));
				return;
			}

			// 테스트 확장 BreakevenMinR: 1차 이익이 처음 손절 거리 × 이 값보다 작으면 평단으로 옮기지 않고 손절 유지 (트레일링은 그대로).
			// 지표 조건(MACD·%K)으로 몇 pt 이익에 1차 익절한 뒤 바로 본절로 옮기면 남은 조각이 작은 흔들림에 다 털림
			// (10/15 22:14 Playback: 1차 이익 2pt → 22:17 본절 → 22:19 본절 청산 +$29, 같은 자리에서 버틴 경우 +$2,220)
			double risk0 = double.IsNaN(trStop0) ? double.NaN : Math.Abs(avg - trStop0);
			if (BreakevenMinR > 0 && !double.IsNaN(risk0) && risk0 > 0 && Math.Abs(exitPrice - avg) < BreakevenMinR * risk0)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 보류(1차 이익 {2:F2}pt < 손절 거리 {3:F2}pt × {4}) 손절 유지={5}",
					time, Instrument.FullName, Math.Abs(exitPrice - avg), risk0, BreakevenMinR, stopPrice));
				return;
			}

			// 스탑을 현재가 바로 옆이나 반대편으로 옮기면 주문이 거부되고, 거부되면 NinjaTrader가 전략을 끈다.
			// 현재 호가가 본절가에서 충분히 떨어져 있지 않으면 지금은 옮기지 않고 봉 마감마다 다시 본다
			if (!SafeToMoveStop(be))
			{
				breakevenDeferred = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 이동 대기(현재가가 평균가에 너무 가까움) 평균가={2} 익절 체결가={3} 손절 유지={4}",
					time, Instrument.FullName, be, exitPrice, stopPrice));
				return;
			}

			stopPrice		= be;
			breakevenDone	= true;
			breakevenDeferred = false;
			SetStopForLive(be);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 이동 손절→평균가={2}",
				time, Instrument.FullName, be));
		}

		// 스탑을 newStop으로 옮겨도 거부되지 않을 만큼 현재 호가가 떨어져 있는지.
		// 롱은 매수 호가가 newStop보다 BreakevenBufferTicks틱 이상 위, 숏은 매도 호가가 그만큼 아래여야 한다.
		// 과거 봉에서는 GetCurrentBid/Ask가 마지막 종가를 준다
		private bool SafeToMoveStop(double newStop)
		{
			double buffer = Math.Max(1, BreakevenBufferTicks) * TickSize;
			return isLongPos ? GetCurrentBid(0) - newStop >= buffer : newStop - GetCurrentAsk(0) >= buffer;
		}

		// 대기 중인 본절 이동을 봉 마감마다 다시 시도한다
		private void RetryBreakeven()
		{
			if (!breakevenDeferred || breakevenDone || liveSignals.Count == 0 || entryFillQty <= 0)
				return;
			double be = Instrument.MasterInstrument.RoundToTickSize(entryFillSum / entryFillQty);
			if (!SafeToMoveStop(be))
				return;
			stopPrice		= be;
			breakevenDone	= true;
			breakevenDeferred = false;
			SetStopForLive(be);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 이동(대기 후) 손절→평균가={2}",
				Time[0], Instrument.FullName, be));
		}
		#endregion

		#region 익절·청산 상태 머신 (spec 5장 — task4)
		// 진입 시점 레짐·전략에 맞는 청산 로직으로 분기 (spec 1장 3: 레짐 고정)
		private void ManageExits()
		{
			switch (active)
			{
				case ActiveStrategy.UpLong:		ManageUpLong();		break;
				case ActiveStrategy.UpShort:	ManageUpShort();	break;
				case ActiveStrategy.DnLong:		ManageDnLong();		break;
				case ActiveStrategy.DnShort:	ManageDnShort();	break;
				case ActiveStrategy.SideLong:	ManageSideLong();	break;
				case ActiveStrategy.SideShort:	ManageSideShort();	break;
			}
		}

		// 특정 진입 시그널 수량만 시장가 청산 (주문은 BIP 0 대상). 분할 익절용
		private void ExitChunk(string sig, string reason)
		{
			if (!liveSignals.Contains(sig))
				return;
			int q = ChunkLeft(sig);
			liveSignals.Remove(sig);	// 체결 콜백이 주문 호출 도중에 올 수 있어 먼저 뺀다
			if (q <= 0)
				return;
			exitSent.Add(sig);
			SubmitMarketExit(q, new List<string> { sig });
			Out(string.Format("[{0}][{1}][TQ_Strategy] 익절 {2} 시그널={3} 수량={4}",
				Time[0], Instrument.FullName, reason, sig, q));
		}

		// 남은 전량 시장가 청산 (손절 스탑은 포지션 청산 시 자동 취소). 청산 후 중복 주문 방지
		// 전량 청산: 실제 보유 수량에서 이미 낸 시장가 청산을 뺀 만큼 시장가로 (조각 기록은 체결 때 먼 조각부터 줄임)
		private void FlattenRemaining(string reason)
		{
			List<string> chunks = new List<string>();
			foreach (string sig in liveSignals.ToArray())
				if (ChunkLeft(sig) > 0)
				{
					exitSent.Add(sig);
					chunks.Add(sig);
				}
			liveSignals.Clear();
			active = ActiveStrategy.None;
			if (IsWorking(entryOrder) && PosQty() == 0)
				CancelOrder(entryOrder);	// 아직 체결 안 된 진입은 취소
			CancelIfWorking(addOrder);		// 아직 체결 안 된 불타기도 취소
			int q = PosQty() - plannedExitQty;
			if (q > 0)
				SubmitMarketExit(q, chunks);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 전량 청산 {2} 수량={3}",
				Time[0], Instrument.FullName, reason, Math.Max(0, q)));
			Reconcile();
		}

		// spec 5.1 상승추세 롱(주력)
		private void ManageUpLong()
		{
			if (strongMomentum)
			{
				// 강한 모멘텀 청산: 종가 < 3분봉 SMA20 → 나머지 전량
				if (Close[0] < signals.Sma20[0])
					FlattenRemaining("5.1 강한 모멘텀 청산(종가<SMA20)");
				return;
			}

			bool up3  = signals.CrossAboveUp3[0];	// 강한 모멘텀 전환(+3배 상방 돌파)
			bool up2  = signals.CrossAboveUp2[0];	// 1차 익절(+2배 상방 돌파)
			bool weak = signals.T2Bear[0];			// 약한 모멘텀 청산(MACD 하락 전환 AND 데드크로스)

			// 강한 모멘텀 전환: 보유 중 언제든. 1/5를 아직 안 했으면 1/5+2/5 같이, 했으면 2/5만
			if (up3)
			{
				MoveBreakeven();	// 익절 주문보다 먼저 예약한다
				if (!tp1Done) ExitChunk("UpL1", "5.1 강한모멘텀 1/5");
				ExitChunk("UpL2", "5.1 강한모멘텀 2/5");
				tp1Done			= true;
				strongMomentum	= true;		// 이후 약한 모멘텀 청산 적용 안 함
				return;
			}

			if (!tp1Done)
			{
				// 공통 규칙: 1차 익절 전/동시에 나머지 청산 조건이 나오면 전량 청산
				if (up2 && weak)	{ FlattenRemaining("5.1 1차+약한모멘텀 동시 → 전량"); return; }
				if (weak)			{ FlattenRemaining("5.1 약한모멘텀 청산(1차 전) → 전량"); return; }
				if (up2)			{ MoveBreakeven(); ExitChunk("UpL1", "5.1 1차 익절 1/5"); tp1Done = true; }
			}
			else if (weak)			{ FlattenRemaining("5.1 약한모멘텀 청산 → 나머지 전량"); }
		}

		// spec 5.2 상승추세 숏(역추세). 10분봉 저가 조건은 BIP 1에서 별도 처리
		private void ManageUpShort()
		{
			bool tp1  = signals.K20CrossDown[0] || Close[0] <= channels.Dn1[0];
			bool rest = signals.T2Bull[0];			// 골든크로스 AND MACD 상승 전환

			if (!tp1Done)
			{
				if (tp1 && rest)	{ FlattenRemaining("5.2 1차+나머지 동시 → 전량"); return; }
				if (rest)			{ FlattenRemaining("5.2 나머지 청산(1차 전) → 전량"); return; }
				if (tp1)			{ MoveBreakeven(); ExitChunk("UpS1", "5.2 1차 익절 1/2"); tp1Done = true; }
			}
			else if (rest)			{ FlattenRemaining("5.2 나머지 전량 청산"); }
		}

		// spec 5.3 하락추세 롱(역추세)
		private void ManageDnLong()
		{
			bool tp1  = Close[0] >= channels.Up1[0] || signals.MacdDown[0] || signals.K[0] >= CrossHigh;
			bool rest = signals.T2Bear[0];			// MACD 하락 전환 AND 데드크로스

			if (!tp1Done)
			{
				if (tp1 && rest)	{ FlattenRemaining("5.3 1차+나머지 동시 → 전량"); return; }
				if (rest)			{ FlattenRemaining("5.3 나머지 청산(1차 전) → 전량"); return; }
				if (tp1)			{ MoveBreakeven(); ExitChunk("DnL1", "5.3 1차 익절 1/3"); tp1Done = true; }
			}
			else if (rest)			{ FlattenRemaining("5.3 나머지 전량 청산"); }
		}

		// spec 5.4 하락추세 숏(주력): 3단계 (1차·2차·최종)
		private void ManageDnShort()
		{
			// 최종 청산. 명세는 RSI < 20. 실험 옵션 DnShortExitSma를 켜면 5.1 강한 모멘텀 청산(종가 < SMA20)을 뒤집어
			// 종가 > 차트 봉 SMA(MomentumSmaPeriod)일 때 청산한다 (2026-10-22)
			bool final = DnShortExitSma
				? Close[0] > signals.Sma20[0]
				: signals.Rsi[0] < RsiLow;
			bool tp1   = Close[0] <= channels.Dn2[0] || signals.MacdUp[0] || signals.K20CrossDown[0];
			bool tp2   = Close[0] <= channels.Dn3[0];	// 2차 익절(-3배 도달)

			if (!tp1Done)
			{
				// 공통 규칙: 1차 익절 전/동시에 최종 청산 조건이 나오면 전량 청산
				if (final)	{ FlattenRemaining("5.4 최종 청산(1차 전/동시) → 전량"); return; }
				if (tp1)	{ MoveBreakeven(); ExitChunk("DnS1", "5.4 1차 익절 1/3"); tp1Done = true; }
				// -2·-3 동시 돌파면 1차·2차를 같은 봉에서 함께 실행 (spec T3)
				if (tp2 && !tp2Done) { ExitChunk("DnS2", "5.4 2차 익절 1/3"); tp2Done = true; }
			}
			else
			{
				if (final)	{ FlattenRemaining("5.4 최종 청산 → 나머지 전량"); return; }
				if (tp2 && !tp2Done) { ExitChunk("DnS2", "5.4 2차 익절 1/3"); tp2Done = true; }
			}
		}

		// spec 5.5 횡보 롱: 1차 익절이 먼저 충족된 세트만 끝까지 사용
		private void ManageSideLong()
		{
			if (sideSet == SideSet.None)
			{
				bool aTp1  = Close[0] >= channels.Up2[0] || signals.MacdDown[0] || signals.Dead[0];
				bool bTp1  = signals.K80CrossUp[0];
				bool aRest = High[0] >= BandUp(SideRestLevel());	// A 나머지 청산: 고가 +3배 터치
				bool bRest = signals.T2Bear[0];				// B 나머지 청산: MACD 하락 전환 AND 데드크로스
				bool anyTp1  = aTp1 || bTp1;
				bool anyRest = aRest || bRest;

				// 공통 규칙: 1차 익절 전/동시에 A·B 어느 세트든 나머지 청산 조건이 나오면 전량 청산
				if (anyTp1 && anyRest)	{ FlattenRemaining("5.5 1차+나머지 동시 → 전량"); return; }
				if (anyRest)			{ FlattenRemaining("5.5 나머지 청산(1차 전) → 전량"); return; }
				if (anyTp1)
				{
					sideSet = aTp1 ? SideSet.A : SideSet.B;	// 동시 충족이면 A (기본값)
					MoveBreakeven();	// 익절 주문보다 먼저 예약한다
					ExitChunk("SideL1", "5.5 1차 익절 1/2 (세트 " + sideSet + ")");
					tp1Done = true;
				}
			}
			else if (sideSet == SideSet.A)
			{
				if (High[0] >= BandUp(SideRestLevel()))	FlattenRemaining("5.5 A 나머지 청산(고가 " + BandText(SideRestLevel(), true) + " 터치)");
			}
			else	// 세트 B
			{
				if (signals.T2Bear[0])			FlattenRemaining("5.5 B 나머지 청산(T2 하락)");
			}
		}

		// spec 5.6 횡보 숏: 1차 익절이 먼저 충족된 세트만 끝까지 사용
		private void ManageSideShort()
		{
			if (sideSet == SideSet.None)
			{
				bool aTp1  = Close[0] <= channels.Dn2[0] || signals.MacdUp[0] || signals.Golden[0];
				bool bTp1  = signals.K20CrossDown[0];
				bool aRest = Low[0] <= BandDn(SideRestLevel());		// A 나머지 청산: 저가 -3배 터치
				bool bRest = signals.T2Bull[0];				// B 나머지 청산: MACD 상승 전환 AND 골든크로스
				bool anyTp1  = aTp1 || bTp1;
				bool anyRest = aRest || bRest;

				if (anyTp1 && anyRest)	{ FlattenRemaining("5.6 1차+나머지 동시 → 전량"); return; }
				if (anyRest)			{ FlattenRemaining("5.6 나머지 청산(1차 전) → 전량"); return; }
				if (anyTp1)
				{
					sideSet = aTp1 ? SideSet.A : SideSet.B;	// 동시 충족이면 A (기본값)
					MoveBreakeven();	// 익절 주문보다 먼저 예약한다
					ExitChunk("SideS1", "5.6 1차 익절 1/2 (세트 " + sideSet + ")");
					tp1Done = true;
				}
			}
			else if (sideSet == SideSet.A)
			{
				if (Low[0] <= BandDn(SideRestLevel()))	FlattenRemaining("5.6 A 나머지 청산(저가 " + BandText(SideRestLevel(), false) + " 터치)");
			}
			else	// 세트 B
			{
				if (signals.T2Bull[0])			FlattenRemaining("5.6 B 나머지 청산(T2 상승)");
			}
		}

		// 미보유 전환 시 호출: 진입·익절·손절 상태 전체 초기화 (strategy.md).
		// 미보유면 남은 손절·익절 주문을 취소한다. 걸어 둔 강한 돌파 스탑 진입(entryOrder)은 RouteEntry·CancelStrongBreakout이 다룬다
		private void ResetTradeState()
		{
			chunkOrder.Clear();
			chunkLeft.Clear();
			chunkFilled.Clear();
			stageFilled.Clear();
			exitSent.Clear();
			exitOrderChunks.Clear();
			plannedExitQty		= 0;
			stopRejectHandled	= false;
			if (PosQty() == 0)
			{
				CancelIfWorking(stopOrder);
				foreach (Order t in new List<Order>(tpOrders.Values))
					CancelIfWorking(t);
			}
			stopOrder = null;
			tpOrders.Clear();
			active			= ActiveStrategy.None;
			entryRegime		= 0;
			isLongPos		= false;
			stopSwing		= double.NaN;
			stopPrice		= double.NaN;
			initialStop		= double.NaN;
			entryFillSum	= 0;
			entryFillQty	= 0;
			gapFlattened	= false;
			breakevenPending	= false;
			breakevenDone	= false;
			tp1Done			= false;
			tp2Done			= false;
			strongMomentum	= false;
			sideSet			= SideSet.None;
			testHoldBars	= 0;
			stopAtr = double.NaN;
			breakevenDeferred = false;
			initialRisk = double.NaN;
			reachedR = false;	// 직전 거래의 이익 보호 본절 판정이 다음 거래로 넘어가지 않게 한다
			liveSignals.Clear();
			chunkQty.Clear();
			// 테스트 확장 (sbDir은 걸린 주문을 따라가므로 여기서 지우지 않음)
			sawOwnTrend		= false;
			warnDone		= false;
			trailExtreme	= double.NaN;
			fixedTarget.Clear();
			addSig			= "";
			addOrder		= null;
			addFillSum		= 0;
			addFillQty		= 0;
		}
		#endregion

		#region 테스트 확장 — v6 7레짐·강한 돌파·트레일링·전환의심·강제 채우기 (docs/plus-design.md)
		private static double R6(double x)	{ return Math.Round(x, 6); }

		// v6 spec 3-3 연속 점수: 직전에 마감된 3분봉의 SMA·MACD·RSI·ATR과 지금 1분봉 종가로 항목 11개를 −1~+1로 만들어 평균.
		// 가격 항목은 1분마다, 이평 항목은 3분봉마다 바뀜. v6 TQ_Strategy.UpdateContScore와 같은 식 (재현기 score.py)
		private void UpdateContScore()
		{
			contReady = CurrentBars[2] >= ScoreMinBars3 && CurrentBars[0] > ScoreVelBars;
			if (!contReady)
			{
				contHist[0] = 0;
				return;
			}
			double a	= Math.Max(r3Atr[0], TickSize);
			double c	= Close[0];
			double sm	= r3Mid[0], sf = r3Fast[0], ss = r3Slow[0], m = r3Macd[0];
			// 가격 항목 6개
			double pxMid	= R6(Math.Tanh((c - sm) / (1.5 * a)));
			double pxFast	= R6(Math.Tanh((c - sf) / (0.75 * a)));
			double roc5		= R6(Math.Tanh((c - Closes[2][5]) / (2.0 * a)));
			double hh = c, ll = c;
			for (int k = 0; k < 20; k++)
			{
				hh = Math.Max(hh, Highs[2][k]);
				ll = Math.Min(ll, Lows[2][k]);
			}
			double don20	= R6(hh > ll ? 2 * (c - ll) / (hh - ll) - 1 : 0);
			const int n = 10;
			double[] ys = new double[n];
			for (int k = 0; k < n - 1; k++)
				ys[k] = Closes[2][n - 1 - k];			// 3분봉 [9]..[1]
			ys[n - 1] = c;								// 마지막 점 = 지금 가격
			double xm = (n - 1) / 2.0, ym = 0, num = 0, den = 0, path = Math.Abs(ys[0] - Closes[2][n]);
			for (int k = 0; k < n; k++)
				ym += ys[k];
			ym /= n;
			for (int k = 0; k < n; k++)
			{
				num += (k - xm) * (ys[k] - ym);
				den += (k - xm) * (k - xm);
				if (k > 0)
					path += Math.Abs(ys[k] - ys[k - 1]);
			}
			double lrs10	= R6(Math.Tanh(num / den / (0.25 * a)));
			double er10		= R6(path > 0 ? (c - Closes[2][n]) / path : 0);
			// 구조 2개, 모멘텀 3개
			double structS	= R6(Math.Tanh((sm - ss) / (2.0 * a)));
			double slope60	= R6(Math.Tanh((ss - r3Slow[5]) / (0.5 * a)));
			double macd		= R6(Math.Tanh(m / a));
			double macdSl	= R6(Math.Tanh((m - r3Macd[2]) / (0.3 * a)));
			double rsi		= R6(Math.Max(-1.0, Math.Min(1.0, (r3Rsi[0] - 50) / 20)));
			contScore	= R6((pxMid + pxFast + roc5 + don20 + lrs10 + er10 + structS + slope60 + macd + macdSl + rsi) / 11.0);
			contSlow	= R6(pxMid + structS + macd + rsi);	// 종가−중기선, 중기−장기선, MACD선, RSI (−4~+4)
			contFast	= R6(pxFast + macdSl);				// 종가−단기선, MACD선 기울기 (−2~+2)
			contHist[0]	= contScore;
			contVel		= R6(contScore - contHist[ScoreVelBars]);
			// 진단 (10/22 Playback에서 점수가 v6와 달랐음): 3분봉 시각과 지표 원값을 로그에 남겨 재현기와 대조
			contDiag	= string.Format("3분봉={0:HH:mm} 종가3={1} ATR3={2:F4} SMA6={3:F4} SMA20={4:F4} SMA60={5:F4} SMA60[5]={6:F4} MACD={7:F4} MACD[2]={8:F4} RSI={9:F4} 항목={10}/{11}/{12}/{13}/{14}/{15}/{16}/{17}/{18}/{19}/{20}",
				Times[2][0], Closes[2][0], r3Atr[0], sf, sm, ss, r3Slow[5], m, r3Macd[2], r3Rsi[0],
				pxMid, pxFast, roc5, don20, lrs10, er10, structS, slope60, macd, macdSl, rsi);
		}

		// 1분봉마다: 청산용 판정(v6 spec 3-3)과 7레짐(v6 spec 3-4·3-5). 상승 기준, 하락은 거울상
		//  청산용 판정: 시작 = C ≥ 0.2, 끝 = C ≤ −0.1, 반대 C ≤ −0.2면 바로 반대
		//  7레짐: 횡보 → C ≥ 0.2 추세 / 빠른 출발(C ≥ 0, 속도 ≥ 0.25) 시작의심.
		//         시작의심 → 반대 빠른 출발·C ≤ −0.2면 반대 시작의심, C ≥ 0.2면 추세, C < 0이면 횡보.
		//         추세 → C ≤ −0.2 반대 추세, C < 0.1 또는 속도 ≤ −0.25면 눌림.
		//         눌림 → C ≤ −0.2 반대 추세, C ≤ −0.1 횡보, C ≥ 0.2이고 속도 ≥ 0이면 추세.
		//  전환의심: 추세·눌림 중 C가 반대쪽(0 넘음)이고 속도가 반대로 0.25 이상
		private void Observe7Regime()
		{
			if (!contReady)
				return;
			double C = contScore, V = contVel;
			int prevX = contExitState;
			if (contExitState == 1)			contExitState = C <= -ScoreTrendOn ? -1 : C <= ScoreExitOff ? 0 : 1;
			else if (contExitState == -1)	contExitState = C >= ScoreTrendOn ? 1 : C >= -ScoreExitOff ? 0 : -1;
			else							contExitState = C >= ScoreTrendOn ? 1 : C <= -ScoreTrendOn ? -1 : 0;
			if (contExitState != prevX)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 청산용 판정 {2} → {3}  C={4:F3}", Time[0], Instrument.FullName, prevX, contExitState, C));

			int prev = r7;
			int d = r7 > 0 ? 1 : r7 < 0 ? -1 : 0;
			int a = Math.Abs(r7);
			bool fastUp = C >= 0 && V >= ScoreVelMin, fastDn = C <= 0 && V <= -ScoreVelMin;
			if (a == 0)
				r7 = C >= ScoreTrendOn ? 2 : C <= -ScoreTrendOn ? -2 : fastUp ? 1 : fastDn ? -1 : 0;
			else if (a == 1)
			{
				if ((d > 0 ? fastDn : fastUp) || d * C <= -ScoreTrendOn)	r7 = -d;
				else if (d * C >= ScoreTrendOn)							r7 = 2 * d;
				else if (d * C < 0)										r7 = 0;
			}
			else if (a == 2)
			{
				if (d * C <= -ScoreTrendOn)									r7 = -2 * d;
				else if (d * C < R7EndSuspectC || d * V <= -ScoreVelMin)	r7 = 3 * d;
			}
			else
			{
				if (d * C <= -ScoreTrendOn)						r7 = -2 * d;
				else if (d * C <= ScoreExitOff)					r7 = 0;
				else if (d * C >= ScoreTrendOn && d * V >= 0)	r7 = 2 * d;
			}
			if (r7 != prev)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 7레짐 {2} → {3}  C={4:F3} 속도={5:F3}", Time[0], Instrument.FullName, R7Text(prev), R7Text(r7), C, V));
			int d7 = R7TrendDir(), prevWarn = r7Warn;
			r7Warn = d7 != 0 && d7 * C <= 0 && d7 * V <= -ScoreVelMin ? -d7 : 0;
			if (r7Warn != prevWarn && r7Warn != 0)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 전환의심: {2}  C={3:F3} 속도={4:F3}", Time[0], Instrument.FullName, R7Display(), C, V));
		}

		// 7레짐 방향: 추세·눌림일 때만 ±1, 시작의심·횡보는 0
		private int R7TrendDir()	{ return contReady && (Math.Abs(r7) == 2 || Math.Abs(r7) == 3) ? (r7 > 0 ? 1 : -1) : 0; }

		private static string R7Text(int v)
		{
			string dir = v > 0 ? "상승" : "하락";
			switch (Math.Abs(v))
			{
				case 1:		return dir + " 시작의심";
				case 2:		return dir + "추세";
				case 3:		return dir + " 눌림";
				default:	return "횡보";
			}
		}

		private string R7Display()
		{
			if (r7Warn != 0)
				return (r7Warn > 0 ? "상승" : "하락") + " 전환의심 (" + R7Text(r7) + " 중)";
			return R7Text(r7);
		}

		// 대회 시작(ContestStartTime)부터 지난 분. 자정을 넘는 설정도 처리
		private int RelMinutes(int hhmmss)
		{
			int t = hhmmss / 10000 * 60 + hhmmss / 100 % 100;
			int s = ContestStartTime / 10000 * 60 + ContestStartTime / 100 % 100;
			return (t - s + 1440) % 1440;
		}

		private int MinutesToFlatten()	{ return RelMinutes(FlattenTime) - RelMinutes(ToTime(Time[0])); }

		// 21:30부터 NewsBlockMinutes분 안의 신호 봉 (재현기 zone "News")
		private bool InNewsBlock()	{ return NewsBlockMinutes > 0 && InEntryWindow() && RelMinutes(ToTime(Time[0])) < NewsBlockMinutes; }

		// 개장 전후 22:25~22:32 신호 봉 (재현기 zone "OpenBlock")
		private bool InOpenBlock()
		{
			int r = RelMinutes(ToTime(Time[0])), o = RelMinutes(OpenTime);
			return r >= o - OpenBlockBefore && r < o + OpenBlockAfter;
		}

		// 7레짐으로 트레이더 진입을 고른다. 추세 진입(5.1·5.4)은 추세·눌림 방향(TrendEntryPullbackOnly면 눌림만),
		// 횡보 진입(5.5·5.6)은 UseSideEntry일 때 추세·눌림이 아닐 때. 트레이더 진입이 없으면 강한 돌파를 본다
		private void RouteEntryR7()
		{
			int r = R7TrendDir();
			bool news = InNewsBlock();
			bool canLong	= !(BlockReentryAfterStop && blockLong);
			bool canShort	= !(BlockReentryAfterStop && blockShort);
			if (!news && contReady)
			{
				// 흔들리는 장(그날 효율비 ≤ ChopMaxEr)이면 추세 진입은 눌림에서만
				bool pullOnly = TrendEntryPullbackOnly || ChopDay();
				if (r == 1 && (!pullOnly || r7 == 3) && !(UseWarnExit && r7Warn == -1)
					&& (signals.EntryUpLong[0] || (TrendT2Entry && signals.T2Bull[0])))
				{
					CancelStrongBreakout("트레이더 진입");
					EnterUpLong();
					return;
				}
				if (r == -1 && (!pullOnly || r7 == -3) && !(UseWarnExit && r7Warn == 1)
					&& (DnShortSignal() || (TrendT2Entry && signals.T2Bear[0])))
				{
					if (!DnShortSmaBlocked("5.4 신호"))
					{
						CancelStrongBreakout("트레이더 진입");
						EnterDnShort();
						return;
					}
				}
				if (UseSideEntry && r == 0)
				{
					if (canLong && signals.EntrySideLong[0])
					{
						CancelStrongBreakout("트레이더 진입");
						EnterSideLong();
						return;
					}
					if (canShort && signals.EntrySideShort[0])
					{
						CancelStrongBreakout("트레이더 진입");
						EnterSideShort();
						return;
					}
				}
				// 추세 지속 진입: 눌림 신호 없이 추세가 이어지면 그 방향으로 한 번 들어감 (5.1·5.4와 같은 분할·손절·청산)
				if (TrendContinuationEntry())
					return;
				// SMA20 위라 막았던 5.4 매도: 대기 봉 수 안에 종가가 SMA20 아래로 오고 7레짐이 아직 하락(추세·눌림)이면 매도
				if (DnShortSmaBlock && DnShortSmaWaitBars > 0 && CurrentBar <= dnShortWaitUntil
					&& Close[0] < signals.Sma20[0] && (r7 == -2 || r7 == -3))
				{
					dnShortWaitUntil = -1;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 5.4 매도: 막았던 신호 뒤 종가가 SMA20 아래로 → 진입 (종가={2} SMA20={3:F2})",
						Time[0], Instrument.FullName, Close[0], signals.Sma20[0]));
					CancelStrongBreakout("트레이더 진입");
					EnterDnShort();
					return;
				}
			}
			TryStrongBreakout();
		}

		// 5.4 매도 진입이 '종가 > SMA20'에서 나오면 다음 봉 최종 청산 조건(종가 > SMA20)이 이미 참이라 들어가자마자 나옴(로직 충돌).
		// 이때는 진입하지 않고 DnShortSmaWaitBars봉 동안 종가가 SMA20 아래로 오기를 기다림.
		// 재현기 45일: 1봉 안 최종 청산 25건 중 21건이 이 경우. 막고 10봉 기다림 +$168,871 → +$161,542, 장중 최저 −$7,830 → −$6,905
		private bool DnShortSmaBlocked(string what)
		{
			if (!DnShortSmaBlock || !DnShortExitSma || Close[0] <= signals.Sma20[0])
				return false;
			if (DnShortSmaWaitBars > 0)
				dnShortWaitUntil = CurrentBar + DnShortSmaWaitBars;
			Out(string.Format("[{0}][{1}][TQ_Strategy] {2} 매도 보류: 종가 {3} > SMA20 {4:F2} (최종 청산 조건이 이미 참). {5}봉 동안 SMA20 아래로 오면 진입",
				Time[0], Instrument.FullName, what, Close[0], signals.Sma20[0], DnShortSmaWaitBars));
			return true;
		}

		// 테스트 확장 TrendContBars: 7레짐이 같은 방향 '추세'(±2)로 TrendContBars봉 이상 이어졌는데 미보유이면 추세 방향으로 진입.
		// 같은 추세 구간(7레짐 방향이 바뀌기 전)에서 한 번만, 흔들리는 장(ChopDay)에서는 하지 않음.
		// 왜: 트레이더 진입은 '중심선까지 눌렸다가 반전'이라 눌림 없이 계속 가는 큰 추세에는 들어가지 못했음
		//     (재현기 9/21 +277pt 상승 내내 7레짐 상승추세였는데 매수 0건, 9/23 −295pt 하락도 매도 0건).
		// 근거(재현기 45일): 끔 7월 +$22,100 / 9월 초 +$9,400 / 9/21~10/2 +$23,413 / 10월 +$283 → 8봉 +$30,251 / +$7,012 / +$32,603 / +$5,382.
		//     6~12봉 모두 네 구간 +, 큰 추세일 15일 +$36,769 → +$53,139
		private bool TrendContinuationEntry()
		{
			if (TrendContBars <= 0 || trendRunUsed || Math.Abs(r7) != 2 || trendRunLen < TrendContBars || ChopDay())
				return false;
			// 속도를 쫓지 않음: 점수 속도가 추세 방향으로 TrendContMaxVel 이상이면 이번 봉은 건너뜀(이 추세 구간의 기회는 남김).
			// 재현기 45일: 지속 진입 중 속도 ≥ 0.1에서 들어간 43건 −$3,740, 나머지 40건 +$22,661
			int dir = r7 > 0 ? 1 : -1;
			if (TrendContMaxVel > 0 && dir * contVel >= TrendContMaxVel)
				return false;
			trendRunUsed = true;
			if (dir < 0 && DnShortSmaBlocked("추세 지속"))
				return false;	// 이 추세 구간의 지속 진입 기회는 쓴 것으로 봄 (재현기와 같음)
			CancelStrongBreakout("추세 지속 진입");
			Out(string.Format("[{0}][{1}][TQ_Strategy] 추세 지속 진입: 7레짐 {2} {3}봉 이어짐 (눌림 신호 없음)",
				Time[0], Instrument.FullName, R7Text(r7), trendRunLen));
			if (r7 > 0)	EnterUpLong();
			else		EnterDnShort();
			return true;
		}

		// 테스트 확장 AddOnQty: 추세 진입(5.1·5.4, 지속 진입 포함)이 2차 익절(5.1은 +3배 강한 모멘텀, 5.4는 −3배)까지 갔고
		// 7레짐이 아직 같은 방향 '추세'(±2)면 AddOnQty계약을 시장가로 한 번 더함(보유 합계는 MaxQuantity 이하). 거래당 한 번, AddOnEndTime 전까지.
		// 손절은 다시 계산하지 않고 지금 손절(2차 뒤 트레일링)이 추가분까지 덮음. 대회 규정상 기존 포지션 추가는 진입 횟수에 안 셈.
		// 왜: 수익이 2차까지 간 소수 거래에서 나옴(재현기 45일, 2차 뒤 트레일링 청산 37건 +$77k). 검증된 거래에만 수량을 늘림.
		// 근거(재현기 45일, MNQ, 위험 상한 $4,000): 80계약 고정 +$144,544, 장중 최저 −$10,359 → 진입 50 + 불타기 30 +$166,156, 장중 최저 −$7,830 (네 구간 모두 +)
		private void TryAddOn()
		{
			if (AddOnQty <= 0 || addSig.Length > 0 || UseTestEntry || tradePrefix.Length == 0)
				return;
			if (active != ActiveStrategy.UpLong && active != ActiveStrategy.DnShort)
				return;
			if (!(tp2Done || strongMomentum) || gapFlattened || plannedExitQty > 0 || liveSignals.Count == 0)
				return;
			int dir = isLongPos ? 1 : -1;
			if (!contReady || r7 != 2 * dir)
				return;
			if (RelMinutes(ToTime(Time[0])) >= RelMinutes(AddOnEndTime))
				return;
			// 손절이 현재가에 너무 붙어 있으면 더하지 않음: 10/21 22:26 Playback에서 트레일링 손절 6pt 위에 30계약을 더했다가 5초 만에 손절.
			// 재현기 45일: 조건 없음 +$166,156 → 1 ATR +$168,871 (네 구간 모두 같거나 나음), 2 ATR +$166,876, 3 ATR +$110,790
			if (AddOnMinRoomAtr > 0 && !double.IsNaN(stopPrice) && Math.Abs(Close[0] - stopPrice) < AddOnMinRoomAtr * atr[0])
				return;
			int q = Math.Min(AddOnQty, MaxQuantity - PosQty());
			if (q <= 0)
				return;
			addSig = isLongPos ? "UpL4" : "DnS4";
			chunkQty[addSig] = q;
			chunkOrder.Add(addSig);
			liveSignals.Add(addSig);
			addOrder = SubmitOrderUnmanaged(0, isLongPos ? OrderAction.Buy : OrderAction.SellShort, OrderType.Market, q, 0, 0, "", tradePrefix + "A");
			Out(string.Format("[{0}][{1}][TQ_Strategy] 불타기 {2} {3}계약 (보유 {4}, 7레짐 {5}, 손절 {6})",
				Time[0], Instrument.FullName, isLongPos ? "매수" : "매도", q, PosQty(), R7Text(r7), stopPrice));
		}

		// 7레짐 방향이 바뀌면 0부터, 같은 방향 '추세'(±2)인 봉마다 1 늘림. 보유 중에도 1분봉마다 셈
		private void CountTrendRun()
		{
			int v = contReady ? r7 : 0;
			int d = v > 0 ? 1 : v < 0 ? -1 : 0;
			if (d != trendRunDir)
			{
				trendRunDir		= d;
				trendRunLen		= 0;
				trendRunUsed	= false;
			}
			if (Math.Abs(v) == 2)
				trendRunLen++;
		}

		// 흔들리는 장: 대회 구간이 ChopMinBars봉 넘게 지났고 그날 효율비가 ChopMaxEr 이하 (v6 박스 장 감지 0.15·60봉 그대로).
		// 근거(재현기 45일, 이 값은 이번 데이터로 고르지 않음): 지금 기본 7월 +$26,078 / 9월 초 +$10,855 / 9/21~10/2 +$33,493 / 10월 −$11,753
		//  → 흔들리는 장이면 눌림만: +$22,100 / +$9,400 / +$23,413 / +$283 (네 구간 모두 +, 최악의 날 −$5,110 → −$4,831)
		private bool ChopDay()
		{
			if (ChopMaxEr <= 0 || sessBars < ChopMinBars || sessPath <= 0 || double.IsNaN(sessStart))
				return false;
			return Math.Abs(Close[0] - sessStart) / sessPath <= ChopMaxEr;
		}

		// 직전 SbBoxBars봉 종가 효율비 (지금 봉 제외): |순변화| ÷ 봉마다 변화의 합. 0이면 박스, 1이면 한 방향
		private double BoxEr()
		{
			if (CurrentBar <= SbBoxBars)
				return 1;
			double net = Math.Abs(Close[1] - Close[SbBoxBars]), path = 0;
			for (int k = 1; k < SbBoxBars; k++)
				path += Math.Abs(Close[k] - Close[k + 1]);
			return path > 0 ? net / path : 1;
		}

		// 강한 돌파: 7레짐이 추세이고(SbAllowStartSuspect면 시작의심도, 전환의심 아님), 빠른 점수·속도가 그 방향으로 강하고, 직전 30봉이 박스이고,
		// 종가가 15봉 고가(매도는 저가)에서 SbNearAtr × ATR 안이면 그 가격 1틱 밖에 스탑 진입. 다음 봉에 다시 보고 다시 걸거나 취소
		private void TryStrongBreakout()
		{
			if (!UseStrongBreakout || !contReady)
			{
				CancelStrongBreakout(null);
				return;
			}
			string why = null;
			if (InNewsBlock())											why = "지표 발표 시간";
			else if (InOpenBlock())										why = "개장 전후";
			else if (RelMinutes(ToTime(Time[0])) >= RelMinutes(SbEndTime))	why = "마감 시각 뒤";
			else if (SbMaxEr < 1 && BoxEr() > SbMaxEr)					why = string.Format("박스 아님(효율비 {0:F2} > {1:F2})", BoxEr(), SbMaxEr);
			int d = r7 > 0 ? 1 : r7 < 0 ? -1 : 0;
			double a = atr[0];
			double px = double.NaN;
			if (why == null)
			{
				int s = Math.Abs(r7);
				double lvl = d > 0 ? rangeHigh[0] : rangeLow[0];
				if (d == 0 || (s != 2 && !(s == 1 && SbAllowStartSuspect)))	why = "7레짐 " + R7Text(r7);
				else if (r7Warn == -d)								why = "전환의심";
				else if (d * contFast < SbMinFast)					why = string.Format("빠른 점수 {0:F2} < {1:F2}", d * contFast, SbMinFast);
				else if (d * contVel < SbMinVel)					why = string.Format("속도 {0:F2} < {1:F2}", d * contVel, SbMinVel);
				else if (d * (lvl - Close[0]) < 0 || d * (lvl - Close[0]) > SbNearAtr * a)	why = string.Format("돌파 가격 {0:F2}이 멂", lvl);
				else
				{
					px = Instrument.MasterInstrument.RoundToTickSize(lvl + d * TickSize);
					bool beyond = d > 0 ? px <= GetCurrentAsk(0) : px >= GetCurrentBid(0);
					if (beyond)										why = "현재가가 이미 돌파 가격을 넘음";
				}
			}
			if (why != null)
			{
				CancelStrongBreakout(sbDir != 0 ? why : null);
				return;
			}
			if (sbDir != 0 && sbDir != d)
			{
				// 관리형 주문: 미보유 때 한 방향 진입 주문이 살아 있으면 반대 진입은 무시됨 → 이번 봉은 취소만
				CancelStrongBreakout("반대 방향 조건");
				return;
			}
			PlaceStrongBreakout(d, px);
		}

		// 스탑 진입 3조각 (1/3·1/3·나머지). 같은 시그널명으로 다시 걸면 NinjaTrader가 걸린 주문의 가격만 고침
		private void PlaceStrongBreakout(int d, double px)
		{
			bool renew = sbDir == d;
			ResetTradeState();
			isLongPos	= d > 0;
			entryRegime	= r7;
			active		= ActiveStrategy.Breakout;
			tradeQty	= EntryQuantity;
			TradeBegin(active);
			stopAtr		= atr[0];
			initialStop	= Instrument.MasterInstrument.RoundToTickSize(px - d * SbStopPoints);
			stopPrice	= initialStop;
			MlBegin(active);
			int q1 = tradeQty * 33 / 100, q2 = tradeQty * 33 / 100, q3 = tradeQty - q1 - q2;
			fixedTarget["SB1"] = px + d * SbTp1Atr * stopAtr;
			fixedTarget["SB2"] = px + d * SbTp2Atr * stopAtr;
			SubmitChunk("SB1", q1);
			SubmitChunk("SB2", q2);
			SubmitChunk("SB3", q3);
			// 같은 방향으로 다시 걸면 같은 주문 이름 → 걸린 주문의 가격·수량만 고침 (비관리형 주문은 취소할 때까지 살아 있음)
			bool live = renew && sbPrefix.Length > 0 && IsWorking(entryOrder) && entryOrder.Name == sbPrefix + "E";
			SendEntryOrder(px, live ? sbPrefix : "");
			sbPrefix = tradePrefix;
			sbDir = d;
			if (!renew)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 강한 돌파 스탑 주문 {2} {3:F2} 수량={4}/{5}/{6} 손절={7:F2} 익절={8:F2}/{9:F2} 7레짐={10} 빠른점수={11:F2} 속도={12:F2} 효율비={13:F2}",
					Time[0], Instrument.FullName, d > 0 ? "매수" : "매도", px, q1, q2, q3, stopPrice, fixedTarget["SB1"], fixedTarget["SB2"], R7Text(r7), contFast, contVel, BoxEr()));
		}

		// 걸어 둔 강한 돌파 스탑 진입을 취소 (비관리형 주문은 봉이 끝나도 살아 있으므로 다시 걸지 않을 때마다 직접 취소)
		private void CancelStrongBreakout(string why)
		{
			if (sbDir == 0)
				return;
			if (IsWorking(entryOrder) && entryOrder.OrderType == OrderType.StopMarket && PosQty() == 0)
				CancelOrder(entryOrder);
			if (why != null)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 강한 돌파 스탑 주문 취소: {2}", Time[0], Instrument.FullName, why));
			sbDir		= 0;
			sbPrefix	= "";
			if (Position.MarketPosition == MarketPosition.Flat && active == ActiveStrategy.Breakout)
				ResetTradeState();
		}

		// 체결가 기준으로 강한 돌파 익절 가격을 다시 건다
		private void ResetBreakoutTargets(double avg)
		{
			int d = isLongPos ? 1 : -1;
			fixedTarget["SB1"] = avg + d * SbTp1Atr * stopAtr;
			fixedTarget["SB2"] = avg + d * SbTp2Atr * stopAtr;
			foreach (string sig in liveSignals.ToArray())
				SetTarget(sig);
		}

		// 1차 익절 뒤 트레일링: 진입 뒤 최고가(매도는 최저가) − 3 ATR, 2차 익절·강한 모멘텀 뒤 2.5 ATR. 유리한 쪽으로만, 안전 거리 지킴
		private void UpdateTrailStop()
		{
			if (active == ActiveStrategy.None || active == ActiveStrategy.Force || entryFillQty <= 0 || Position.MarketPosition == MarketPosition.Flat)
				return;
			int d = isLongPos ? 1 : -1;
			trailExtreme = double.IsNaN(trailExtreme) ? (d > 0 ? High[0] : Low[0]) : (d > 0 ? Math.Max(trailExtreme, High[0]) : Math.Min(trailExtreme, Low[0]));
			if (!UseTrailAfterTp1 && active != ActiveStrategy.Breakout)
				return;
			double k = (tp2Done || strongMomentum) ? TrailTp2Atr : tp1Done ? TrailTp1Atr : 0;
			if (k <= 0 || double.IsNaN(atr[0]) || atr[0] <= 0 || liveSignals.Count == 0)
				return;
			double target = Instrument.MasterInstrument.RoundToTickSize(trailExtreme - d * k * atr[0]);
			if (!double.IsNaN(stopPrice) && d * (target - stopPrice) <= 0)
				return;
			if (!SafeToMoveStop(target))
				return;	// 현재가가 너무 가까움: 다음 봉에 다시
			stopPrice = target;
			SetStopForLive(target);
			double avg = entryFillSum / entryFillQty;
			if (d * (target - Instrument.MasterInstrument.RoundToTickSize(avg)) >= 0)
				breakevenDone = true;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 트레일링 손절 → {2:F2} (최고·최저 {3:F2} ∓ {4} ATR)", Time[0], Instrument.FullName, target, trailExtreme, k));
		}

		// 청산용 판정이 포지션 반대면 전량(OPP_TREND), 포지션 방향이었다가 횡보면 전량(TREND_END). 청산했으면 true
		private bool TrendChangeExit()
		{
			if (!contReady || active == ActiveStrategy.None || active == ActiveStrategy.Force)
				return false;
			if (!UseTrendChangeExit && active != ActiveStrategy.Breakout)
				return false;
			int d = isLongPos ? 1 : -1;
			if (contExitState == d)
				sawOwnTrend = true;
			if (contExitState == -d)
			{
				FlattenRemaining(string.Format("OPP_TREND 청산용 판정 반대 (C={0:F3})", contScore));
				return true;
			}
			if (contExitState == 0 && sawOwnTrend)
			{
				FlattenRemaining(string.Format("TREND_END 청산용 판정 횡보 (C={0:F3})", contScore));
				return true;
			}
			return false;
		}

		// 조각 번호(시그널명 끝 숫자)가 클수록 먼 조각: 3 → 2 → 1 순서로 뺀다
		private static int ChunkStage(string sig)
		{
			char ch = sig[sig.Length - 1];
			return ch >= '0' && ch <= '9' ? ch - '0' : 0;
		}

		// 전환의심 (포지션당 한 번): 보유 방향 반대로 전환의심이면 남은 수량의 50%에 가장 가까워지도록 먼 조각부터 청산, 이익 중이면 손절을 평단으로
		private void WarnCheck()
		{
			if (!UseWarnExit && active != ActiveStrategy.Breakout)
				return;
			if (warnDone || active == ActiveStrategy.None || active == ActiveStrategy.Force || !contReady || Position.MarketPosition == MarketPosition.Flat)
				return;
			int d = isLongPos ? 1 : -1;
			if (r7Warn != -d)
				return;
			warnDone = true;
			List<string> order = new List<string>(liveSignals);
			order.Sort((x, y) => ChunkStage(y).CompareTo(ChunkStage(x)));
			int rem = 0;
			foreach (string s in order)
				rem += ChunkLeft(s);
			int want = rem / 2;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 전환의심 청산: {2} 남은 {3}계약 중 {4}계약", Time[0], Instrument.FullName, R7Display(), rem, want));
			if (want <= 0)
				return;
			if (want >= rem)
			{
				FlattenRemaining("WARN 전환의심");
				return;
			}
			int taken = 0;
			List<string> pick = new List<string>();
			foreach (string s in order)
			{
				int q = ChunkLeft(s);
				if (pick.Count > 0 && Math.Abs(taken + q - want) >= Math.Abs(taken - want))
					break;
				pick.Add(s);
				taken += q;
			}
			foreach (string s in pick)
				ExitChunk(s, "WARN 전환의심");
			double avg = entryFillQty > 0 ? Instrument.MasterInstrument.RoundToTickSize(entryFillSum / entryFillQty) : double.NaN;
			if (!double.IsNaN(avg) && d * (Close[0] - avg) > 0 && (double.IsNaN(stopPrice) || d * (avg - stopPrice) > 0) && SafeToMoveStop(avg) && liveSignals.Count > 0)
			{
				stopPrice		= avg;
				breakevenDone	= true;
				SetStopForLive(avg);
				Out(string.Format("[{0}][{1}][TQ_Strategy] 전환의심 뒤 손절 → 평단 {2}", Time[0], Instrument.FullName, avg));
			}
		}

		// 최소 진입 강제 채우기: 진입 횟수 < 목표이고, 종료 청산까지 남은 봉 ≤ 필요한 횟수 × 2(진입·청산) + 보유 중이면 정리 1 + 여유
		private bool ForceNeeded()
		{
			if (!ForceMinEntries || UseTestEntry || entryCount >= MinEntries)
				return false;
			int left = MinutesToFlatten();
			if (left <= 0 || RelMinutes(ToTime(Time[0])) > RelMinutes(FlattenTime))
				return false;
			int need = MinEntries - entryCount;
			int hold = Position.MarketPosition != MarketPosition.Flat && active != ActiveStrategy.Force ? 1 : 0;
			return left <= 2 * need + hold + ForceMarginBars;
		}

		// 강제 채우기 1계약 시장가. 방향은 점수 부호(0이면 매수). 손절은 트레이더 식, 익절 없음, 다음 봉에 아직 모자라면 청산
		private void EnterForce()
		{
			CancelStrongBreakout("최소 진입 강제 채우기");
			bool isLong = contScore >= 0;
			BeginTrade(isLong, r7, ActiveStrategy.Force);
			tradeQty = 1;
			SubmitChunk("F1", 1);
			SendEntryOrder();
			Out(string.Format("[{0}][{1}][TQ_Strategy] 최소 진입 강제 채우기 {2}/{3}회 → {4} 1계약 (남은 봉 {5}) 손절={6}",
				Time[0], Instrument.FullName, entryCount, MinEntries, isLong ? "매수" : "매도", MinutesToFlatten(), stopPrice));
		}
		#endregion

		#region 검증 — 임시 진입 (task5, UseTestEntry)
		// UseTestEntry가 켜지면 실제 신호(지표) 대신 결정적 흐름으로 주문 플럼빙을 검증한다.
		// 진입·손절·분할 익절·본절·청산 모두 운영과 같은 헬퍼를 써서 Market Replay에서 그대로 확인한다.
		// A의 신호가 완성되면 UseTestEntry를 끄고 실제 신호로 연결한다 (strategy.md).

		// 테스트 진입: 가장 복잡한 5.1 상승추세 롱 분할(1/5·2/5·나머지)로 들어가 2단계 분할 익절을 재현한다
		private void RouteTestEntry()
		{
			BeginTrade(true, 1, ActiveStrategy.UpLong);
			int q1 = Split(1, 5), q2 = Split(2, 5), q3 = tradeQty - q1 - q2;
			SubmitChunk("UpL1", q1);
			SubmitChunk("UpL2", q2);
			SubmitChunk("UpL3", q3);
			SendEntryOrder();
			Out(string.Format("[{0}][{1}][TQ_Strategy] TEST 진입(롱, 5.1 분할) 수량={2}/{3}/{4} 손절={5}",
				Time[0], Instrument.FullName, q1, q2, q3, stopPrice));
		}

		// 보유 봉 수에 따라 1차 익절(+본절) → 2차 익절 → 나머지 전량 청산을 순서대로 낸다.
		// 체결 기반 손절 재계산·본절 이동은 OnExecutionUpdate가 운영과 동일하게 처리한다.
		private void ManageTestExits()
		{
			if (testHoldBars == TestTp1Bar && !tp1Done)
			{
				MoveBreakeven();	// 체결되면 OnFirstTakeProfitFill에서 본절 이동. 익절 주문보다 먼저 예약한다
				ExitChunk("UpL1", "TEST 1차 익절");
				tp1Done = true;
			}
			else if (testHoldBars == TestTp2Bar && !tp2Done)
			{
				ExitChunk("UpL2", "TEST 2차 익절");
				tp2Done = true;
			}
			else if (testHoldBars >= TestExitBar)
			{
				FlattenRemaining("TEST 최종 청산");
			}
		}
		#endregion

		#region 화면 표시 (ShowStatus) — 매매 판단에는 영향 없음
		// 차트에서 전략 상태를 눈으로 보게 한다. 글은 줄이고 색과 도형을 우선 쓴다.
		//  · 배경색: 레짐 (초록 = 상승추세, 빨강 = 하락추세, 없음 = 횡보)
		//  · 밴드: TQ_ATRChannels를 차트에 같이 올린다 (DataLoaded의 AddChartIndicator)
		//  · 마름모: 밴드 조건이 충족된 봉 t (하늘색 = 매수 쪽, 주황 = 매도 쪽)
		//  · 라벨: 체결 (매수·매도·익절·본절·손절·청산 + 수량)
		//  · 가로선: 손절가(자홍 실선), 평균 진입가(회색 점선) — 보유 중에만
		//  · 왼쪽 위 상자: 기다리는 조건을 단계별로 (○ 대기, ● 충족·완료, × 막힘, ■□ T1 남은 봉)

		// 3분봉마다 호출: 레짐 배경색과 밴드 조건 마름모. 차트가 없으면(Strategy Analyzer) 그리지 않는다
		private void PaintBar()
		{
			if (!ShowStatus || ChartControl == null)
				return;

			double rv = UseR7Regime ? R7TrendDir() : regime.Regime[0];
			if (UseR7Regime)
			{
				// 7레짐: 추세 진하게, 눌림·시작의심 옅게, 전환의심 주황(상승 중 하락 전환의심)·하늘색
				int a7 = Math.Abs(r7);
				BackBrush = r7Warn < 0 ? warnDownBrush : r7Warn > 0 ? warnUpBrush
					: a7 == 2 ? (r7 > 0 ? upBackBrush : downBackBrush)
					: a7 == 1 || a7 == 3 ? (r7 > 0 ? upLightBrush : downLightBrush) : null;
			}
			else
				BackBrush = rv > 0 ? upBackBrush : rv < 0 ? downBackBrush : null;

			// 지금 레짐에서 쓰는 진입 규칙의 밴드 조건만 표시한다 (spec 3장: 진입 규칙은 레짐으로 고른다)
			bool longSetup, shortSetup;
			if (rv > 0)
			{
				longSetup	= signals.WaitUpLong == 0;
				shortSetup	= UseCounterTrend && signals.WaitUpShort == 0;
			}
			else if (rv < 0)
			{
				longSetup	= UseCounterTrend && (DnLongNeedDiv ? signals.WaitDnLongDiv : signals.WaitDnLong) == 0;
				shortSetup	= DnShortWait() == 0;
			}
			else
			{
				longSetup	= signals.WaitSideLong == 0;
				shortSetup	= signals.WaitSideShort == 0;
			}

			double gap = (channels.Up1[0] - channels.Mid[0]) * 0.4;	// 봉에서 띄우는 거리. 밴드 한 칸의 0.4배
			// 봉마다 Draw.Diamond로 그리기 개체를 만들지 않고 위치만 기록, 그리기는 OnRender
			if (longSetup || shortSetup)
				lock (renderLock)
				{
					if (longSetup)	setupMarks.Add(new SetupMark { Time = Time[0], Price = Low[0] - gap, Long = true });
					if (shortSetup)	setupMarks.Add(new SetupMark { Time = Time[0], Price = High[0] + gap, Long = false });
					while (setupMarks.Count > 600)
						setupMarks.RemoveAt(0);
				}
		}

		// 왼쪽 위 상자: 실제 매매 판단에 쓰는 값으로 구역을 나눠 보여 준다 (v6-box 방식). 실시간·Playback에서만 갱신.
		// 계산·글 만들기는 여기(OnBarUpdate), 그리기는 OnRender. 줄 앞 기호: ● 충족·완료, ○ 기다리는 조건, × 막힘, 두 칸 들여쓰기 = 참고
		private class PanelSection
		{
			public string		Title;
			public Color		Tone;
			public bool			Headline;
			public List<string>	Lines = new List<string>();
		}

		private List<PanelSection> statusSections;

		private static readonly Color ToneUp		= Color.FromRgb(0, 150, 60);
		private static readonly Color ToneDown		= Color.FromRgb(190, 30, 30);
		private static readonly Color ToneSide		= Color.FromRgb(110, 110, 110);
		private static readonly Color ToneEntry		= Color.FromRgb(70, 90, 170);
		private static readonly Color TonePosLong	= Color.FromRgb(0, 120, 90);
		private static readonly Color TonePosShort	= Color.FromRgb(160, 50, 70);
		private static readonly Color ToneExit		= Color.FromRgb(180, 140, 0);

		private static PanelSection NewSection(List<PanelSection> list, string title, Color tone, bool headline = false)
		{
			PanelSection s = new PanelSection { Title = title, Tone = tone, Headline = headline };
			list.Add(s);
			return s;
		}

		private static void AddLines(PanelSection s, string text)
		{
			foreach (string line in text.Split('\n'))
				if (line.Length > 0)
					s.Lines.Add(line);
		}

		// 그날 대회 구간 효율비 (흔들리는 장 판단과 같은 값). 아직 없으면 NaN
		private double SessionEr()
		{
			if (sessBars <= 0 || sessPath <= 0 || double.IsNaN(sessStart))
				return double.NaN;
			return Math.Abs(Close[0] - sessStart) / sessPath;
		}

		private void UpdateStatusPanel()
		{
			if (!ShowStatus || State != State.Realtime || CurrentBars[0] < DivTo || CurrentBars[regimeBip] < RegimeSlow)
				return;

			List<PanelSection> list = new List<PanelSection>();
			int dir7 = UseR7Regime ? R7TrendDir() : (int)regime.Regime[0];

			// ── 현재 상태: 7레짐(진입 방향을 정함)과 그 근거 값 ──
			PanelSection st = NewSection(list, "현재 상태", dir7 > 0 ? ToneUp : dir7 < 0 ? ToneDown : ToneSide, true);
			if (UseR7Regime)
			{
				st.Lines.Add(string.Format("{0}   {1:HH:mm}", R7Text(r7), Times[0][0]));
				st.Lines.Add(string.Format("진입 방향: {0}", dir7 > 0 ? "매수 (7레짐 상승 추세·눌림)" : dir7 < 0 ? "매도 (7레짐 하락 추세·눌림)" : "없음 (7레짐 횡보·시작의심이면 추세 진입 안 함)"));
				st.Lines.Add(string.Format("점수 C {0:+0.00;-0.00} · 속도 {1:+0.00;-0.00} · 같은 방향 '추세' {2}봉", contScore, contVel, trendRunLen));
				if (r7Warn != 0)
					st.Lines.Add(string.Format("  {0} 전환의심{1}", r7Warn > 0 ? "상승" : "하락", UseWarnExit ? " → 그 방향 진입 금지·50% 청산" : " (참고만, 매매에는 안 씀)"));
			}
			else
				st.Lines.Add(string.Format("{0}   {1:HH:mm}", dir7 > 0 ? "▲ 상승추세" : dir7 < 0 ? "▼ 하락추세" : "◆ 횡보", Times[0][0]));
			double er = SessionEr();
			if (ChopMaxEr > 0 && InEntryWindow())
				st.Lines.Add(double.IsNaN(er) ? "  그날 효율비: 계산 전"
					: ChopDay() ? string.Format("× 흔들리는 장 (그날 효율비 {0:F2} ≤ {1}) → 추세 진입은 눌림에서만, 지속 진입 안 함", er, ChopMaxEr)
					: string.Format("  그날 효율비 {0:F2} (≤ {1}이면 흔들리는 장, {2}/{3}봉 뒤부터 판단)", er, ChopMaxEr, Math.Min(sessBars, ChopMinBars), ChopMinBars));
			st.Lines.Add(string.Format("  MACD {0}  %K {1:F0} / %D {2:F0}  RSI {3:F0}  SMA20 {4:F2}",
				signals.MacdUp[0] ? "▲" : signals.MacdDown[0] ? "▼" : "−", signals.K[0], signals.D[0], signals.Rsi[0], signals.Sma20[0]));

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				renderStop	= double.NaN;	// 손절선·평단선은 OnRender가 그림
				renderAvg	= double.NaN;
				PanelSection en = NewSection(list, string.Format("진입 조건  ({0}/{1}회)", entryCount, MinEntries), ToneEntry);
				if (IsFlattenTime())
					en.Lines.Add("× 대회 시간 밖");
				else if (ForceNeeded())
					en.Lines.Add(string.Format("● 최소 진입 채우기: 1계약 진입 → 다음 봉 청산 (남은 봉 {0})", MinutesToFlatten()));
				else if (!InEntryWindow())
					en.Lines.Add(string.Format("× 진입 시간 아님 ({0} ~ {1})", HhmmText(ContestStartTime), HhmmText(EntryEndTime)));
				else if (active != ActiveStrategy.None)
					en.Lines.Add("● 진입 주문 냄, 체결 대기");
				else if (UseTestEntry)
					en.Lines.Add("TEST: 다음 봉에 검증용 진입");
				else if (dir7 == 0)
					en.Lines.Add("× 7레짐 방향 없음 → 추세 진입 안 함");
				else
					AppendEntryLines(en, dir7);
				if (!IsFlattenTime())
				{
					bool early = EarlyQty > 0 && RelMinutes(ToTime(Time[0])) < RelMinutes(EarlyQtyEndTime);
					en.Lines.Add(string.Format("  수량 {0}계약{1} · 손절 거리 × $2 × 수량 > ${2:N0}이면 줄임", early ? Math.Min(EntryQuantity, EarlyQty) : EntryQuantity,
						early ? string.Format(" ({0} 전)", HhmmText(EarlyQtyEndTime)) : "", RiskCapUsd));
				}
			}
			else
			{
				bool isLong	= Position.MarketPosition == MarketPosition.Long;
				double avg	= entryFillQty > 0 ? entryFillSum / entryFillQty : Position.AveragePrice;
				double pts	= isLong ? Closes[0][0] - avg : avg - Closes[0][0];
				PanelSection ps = NewSection(list, "포지션", isLong ? TonePosLong : TonePosShort);
				ps.Lines.Add(string.Format("{0} {1}계약 @ {2:N2}  {3}{4:N2}pt  ({5})", isLong ? "매수" : "매도", Position.Quantity, avg, pts >= 0 ? "+" : "", pts, ActiveName()));
				if (!double.IsNaN(stopPrice))
					ps.Lines.Add(string.Format("손절 {0:N2}{1}", stopPrice, breakevenDone ? " (본절·트레일링)" : ""));
				renderStop	= stopPrice;
				renderAvg	= avg;

				PanelSection ex = NewSection(list, "익절 · 청산", ToneExit);
				System.Text.StringBuilder sb = new System.Text.StringBuilder();
				AppendExitSteps(sb);
				AddLines(ex, sb.ToString());
				if (AddOnQty > 0 && (active == ActiveStrategy.UpLong || active == ActiveStrategy.DnShort))
				{
					if (addSig.Length > 0)
						ex.Lines.Add(string.Format("● 불타기 {0}계약{1}", addFillQty > 0 ? addFillQty : chunkQty.ContainsKey(addSig) ? chunkQty[addSig] : 0,
							addFillQty > 0 ? string.Format(" @ {0:N2}", addFillSum / addFillQty) : " 주문 냄"));
					else
						ex.Lines.Add(string.Format("{0} 불타기 {1}계약: 2차 익절 뒤{2} · 7레짐 같은 방향 '추세' · 손절까지 {3} ATR · {4} 전",
							Mark(tp2Done || strongMomentum), AddOnQty, tp2Done || strongMomentum ? "(완료)" : "", AddOnMinRoomAtr, HhmmText(AddOnEndTime)));
				}
				if (UseTrendChangeExit)
					ex.Lines.Add("○ 청산용 판정이 반대·추세 끝 → 전량");
				ex.Lines.Add(string.Format("○ {0} 봉 마감 → 전량 청산", HhmmText(FlattenTime)));
			}
			statusSections = list;
		}

		// 진입 조건 줄 (7레짐 방향이 있을 때). 실제 판단 순서: 눌림 신호(5.1·5.4) → T2 → 추세 지속 진입 → 5.4 보류 뒤 대기 진입
		private void AppendEntryLines(PanelSection en, int d)
		{
			bool pullOnly = TrendEntryPullbackOnly || ChopDay();
			string name = d > 0 ? "매수" : "매도";
			string stateBlock = !pullOnly || Math.Abs(r7) == 3 ? null : (ChopDay() ? "흔들리는 장: 7레짐 '눌림'일 때만" : "7레짐 '눌림'일 때만");
			if (d > 0)
				en.Lines.Add(CandidateLine(name + " 눌림(5.1)", signals.WaitUpLong, BandText(UpLongBand, false) + BandHow(false), "MACD▲ / 골든", stateBlock, signals.T1BarsUp).TrimEnd('\n'));
			else
				en.Lines.Add(CandidateLine(name + " 눌림(5.4)", DnShortWait(), BandText(DnShortBand, true) + BandHow(true),
					DnShortNeedBoth ? "MACD▼ + 데드" : "MACD▼ / 데드 / RSI 70 + 다이버전스", stateBlock, signals.T1BarsDn).TrimEnd('\n'));
			if (TrendT2Entry)
				en.Lines.Add(stateBlock != null ? "× T2: " + stateBlock : string.Format("○ T2: MACD{0} + {1} 같이 나오면", d > 0 ? "▲" : "▼", d > 0 ? "골든" : "데드"));
			if (TrendContBars > 0)
			{
				if (trendRunUsed)
					en.Lines.Add("● 추세 지속 진입: 이 추세 구간에서 이미 씀");
				else if (ChopDay())
					en.Lines.Add("× 추세 지속 진입: 흔들리는 장");
				else if (Math.Abs(r7) != 2 || trendRunLen < TrendContBars)
					en.Lines.Add(string.Format("○ 추세 지속 진입: 7레짐 '추세' {0}/{1}봉", trendRunLen, TrendContBars));
				else if (TrendContMaxVel > 0 && d * contVel >= TrendContMaxVel)
					en.Lines.Add(string.Format("○ 추세 지속 진입: 속도 {0:+0.00;-0.00} ≥ {1} (이미 달리는 중) → 줄면 진입", contVel, TrendContMaxVel));
				else
					en.Lines.Add("● 추세 지속 진입: 조건 충족");
			}
			if (d < 0 && DnShortSmaBlock && DnShortExitSma)
			{
				if (DnShortSmaWaitBars > 0 && CurrentBar <= dnShortWaitUntil)
					en.Lines.Add(string.Format("● 5.4 보류 중: 종가가 SMA20 {0:N2} 아래로 오면 매도 (남은 {1}봉)", signals.Sma20[0], dnShortWaitUntil - CurrentBar));
				else if (Close[0] > signals.Sma20[0])
					en.Lines.Add(string.Format("  매도 신호가 나와도 종가 > SMA20 {0:N2}이면 보류 (최종 청산 조건이 이미 참)", signals.Sma20[0]));
			}
		}

		private static string HhmmText(int hhmmss)
		{
			return string.Format("{0:00}:{1:00}", hhmmss / 10000, hhmmss / 100 % 100);
		}

		// 진입 후보 한 줄. ○ = 밴드 조건 대기, ● = 밴드 조건 충족 뒤 반전 신호 대기(■ 지난 봉, □ 남은 봉), × = 막힘
		private string CandidateLine(string name, int wait, string bandText, string reversalText, string blockedReason, int total)
		{
			if (blockedReason != null)
				return string.Format("× {0}: {1}\n", name, blockedReason);
			if (wait < 0)
				return string.Format("○ {0}: {1}\n", name, bandText);

			int done	= Math.Max(0, Math.Min(wait, total));
			return string.Format("● {0}: {1} 대기 {2}{3}\n", name, reversalText, new string('■', done), new string('□', total - done));
		}

		private string ActiveName()
		{
			if (UseTestEntry)	return "TEST";
			switch (active)
			{
				case ActiveStrategy.UpLong:		return "상승추세 주력";
				case ActiveStrategy.UpShort:	return "상승추세 역추세";
				case ActiveStrategy.DnLong:		return "하락추세 역추세";
				case ActiveStrategy.DnShort:	return "하락추세 주력";
				case ActiveStrategy.SideLong:	return "횡보";
				case ActiveStrategy.SideShort:	return "횡보";
				case ActiveStrategy.Breakout:	return "강한 돌파";
				case ActiveStrategy.Force:		return "강제 채우기";
				default:						return "청산 주문 냄";
			}
		}

		private string Mark(bool done)
		{
			return done ? "●" : "○";
		}

		// 보유 중 청산 단계 (spec 5장). ● = 끝난 단계, ○ = 기다리는 단계
		private void AppendExitSteps(System.Text.StringBuilder sb)
		{
			if (UseTestEntry)
			{
				sb.Append("TEST: 2봉째 1차 익절, 3봉째 2차 익절, 4봉째 전량 청산\n");
				return;
			}

			switch (active)
			{
				case ActiveStrategy.UpLong:
					sb.AppendFormat("{0} +2배 돌파 → 1/5 익절\n", Mark(tp1Done));
					sb.AppendFormat("{0} +3배 돌파 → 2/5 익절\n", Mark(strongMomentum));
					sb.Append(strongMomentum ? "○ 종가 < 20선 → 전량 청산\n" : "○ MACD▼ + 데드 → 전량 청산\n");
					break;
				case ActiveStrategy.UpShort:
					sb.AppendFormat("{0} %K 20 이탈 / −1배 / 10분봉 20선 → 1/2 익절\n", Mark(tp1Done));
					sb.Append("○ MACD▲ + 골든 → 전량 청산\n");
					break;
				case ActiveStrategy.DnLong:
					sb.AppendFormat("{0} +1배 / MACD▼ / %K 80 → 1/3 익절\n", Mark(tp1Done));
					sb.Append("○ MACD▼ + 데드 → 전량 청산\n");
					break;
				case ActiveStrategy.DnShort:
					sb.AppendFormat("{0} −2배 / MACD▲ / %K 20 이탈 → 1/3 익절\n", Mark(tp1Done));
					sb.AppendFormat("{0} −3배 → 1/3 익절\n", Mark(tp2Done));
					sb.Append(DnShortExitSma
						? string.Format("○ 종가 > SMA{0} → 전량 청산\n", MomentumSmaPeriod)
						: "○ RSI 20 아래 → 전량 청산\n");
					break;
				case ActiveStrategy.SideLong:
					if (sideSet == SideSet.None)
					{
						sb.Append("○ A: +2배 / MACD▼ / 데드 → 1/2 익절\n");
						sb.Append("○ B: %K 80 돌파 → 1/2 익절\n");
						sb.AppendFormat("○ 고가 {0} 또는 MACD▼ + 데드 → 전량 청산\n", BandText(SideRestLevel(), true));
					}
					else
					{
						sb.AppendFormat("● 1/2 익절 (세트 {0})\n", sideSet);
						sb.Append(sideSet == SideSet.A ? "○ 고가 " + BandText(SideRestLevel(), true) + " 터치 → 전량 청산\n" : "○ MACD▼ + 데드 → 전량 청산\n");
					}
					break;
				case ActiveStrategy.SideShort:
					if (sideSet == SideSet.None)
					{
						sb.Append("○ A: −2배 / MACD▲ / 골든 → 1/2 익절\n");
						sb.Append("○ B: %K 20 이탈 → 1/2 익절\n");
						sb.AppendFormat("○ 저가 {0} 또는 MACD▲ + 골든 → 전량 청산\n", BandText(SideRestLevel(), false));
					}
					else
					{
						sb.AppendFormat("● 1/2 익절 (세트 {0})\n", sideSet);
						sb.Append(sideSet == SideSet.A ? "○ 저가 " + BandText(SideRestLevel(), false) + " 터치 → 전량 청산\n" : "○ MACD▲ + 골든 → 전량 청산\n");
					}
					break;
				case ActiveStrategy.Breakout:
					sb.AppendFormat("{0} {1:F1} ATR → 1/3 익절, 손절 평단\n", Mark(tp1Done), SbTp1Atr);
					sb.AppendFormat("{0} {1:F1} ATR → 1/3 익절\n", Mark(tp2Done), SbTp2Atr);
					sb.Append("○ 트레일링 / 추세 끝·반대 / 전환의심 50%\n");
					break;
				case ActiveStrategy.Force:
					sb.Append("○ 최소 진입 채우기 1계약: 다음 봉 청산 또는 종료 청산\n");
					break;
				default:
					sb.Append("● 청산 주문 냄, 체결 대기\n");
					break;
			}
			if (UseTrailAfterTp1 && tp1Done && !double.IsNaN(trailExtreme))
				sb.AppendFormat("트레일링 기준 {0:N2}\n", trailExtreme);
		}

		// 체결을 차트에 라벨로 표시한다. 매수 체결은 가격 아래, 매도 체결은 가격 위.
		// 같은 봉에서 같은 종류로 여러 건 체결되면(분할 진입 3건 등) 수량을 합쳐 라벨 하나로 보여 준다
		private void MarkExecution(string name, double price, int quantity, MarketPosition side, DateTime time)
		{
			if (!ShowStatus)
				return;
			bool isBuy = side == MarketPosition.Long;
			int kindCode;	// 0 진입, 1 익절, 2 손절·본절, 3 청산
			string kind;
			if (chunkQty.ContainsKey(name))								{ kind = isBuy ? "매수" : "매도"; kindCode = 0; }
			else if (name.StartsWith("x") || name == "Profit target")	{ kind = "익절"; kindCode = 1; }
			else if (name == "Stop loss")								{ kind = breakevenDone ? "본절" : "손절"; kindCode = 2; }
			else														{ kind = "청산"; kindCode = 3; }
			// 같은 분 같은 종류는 수량을 합친다 (분할 진입·부분 체결). 그리기는 OnRender
			DateTime minute = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
			lock (renderLock)
			{
				ExecMark last = execMarks.Count > 0 ? execMarks[execMarks.Count - 1] : null;
				if (last != null && last.Minute == minute && last.Kind == kind)
				{
					last.Qty += quantity;
					return;
				}
				execMarks.Add(new ExecMark { Time = time, Minute = minute, Price = price, Buy = isBuy, Kind = kind, KindCode = kindCode, Qty = quantity });
				while (execMarks.Count > 400)
					execMarks.RemoveAt(0);
			}
		}

		// ── 그리기 (OnRender, v6-box와 같은 방식) ──
		// 계산은 OnBarUpdate·체결 콜백에서 끝내고 여기서는 기록해 둔 값만 그린다. 브러시는 SharpDX(그릴 때 만들고 Dispose).
		// WPF 브러시(BackBrush용)는 DataLoaded에서 만들고 Freeze. 두 종류를 섞지 않는다
		private class ExecMark
		{
			public DateTime	Time, Minute;
			public double	Price;
			public bool		Buy;
			public string	Kind;
			public int		KindCode, Qty;
		}

		private class SetupMark
		{
			public DateTime	Time;
			public double	Price;
			public bool		Long;
		}

		private readonly object				renderLock		= new object();
		private readonly List<ExecMark>		execMarks		= new List<ExecMark>();
		private readonly List<SetupMark>	setupMarks		= new List<SetupMark>();
		private double						renderStop		= double.NaN;
		private double						renderAvg		= double.NaN;
		private string						entryCountText	= "";

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (!ShowStatus || RenderTarget == null || ChartPanel == null || ChartBars == null)
				return;
			List<IDisposable> trash = new List<IDisposable>();
			try
			{
				RenderChartMarks(chartControl, chartScale, trash);
				RenderStatusBox(trash);
			}
			finally
			{
				foreach (IDisposable d in trash)
					d.Dispose();
			}
		}

		private SharpDX.Direct2D1.SolidColorBrush DxBrush(List<IDisposable> trash, byte r, byte g, byte b, byte a = 255)
		{
			SharpDX.Direct2D1.SolidColorBrush br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(r, g, b, a));
			trash.Add(br);
			return br;
		}

		// 손절선·평단선(차트 끝에서 끝까지), 밴드 조건 마름모, 체결 표시, 진입 횟수
		private void RenderChartMarks(ChartControl chartControl, ChartScale chartScale, List<IDisposable> trash)
		{
			List<ExecMark> ms;
			List<SetupMark> ss;
			lock (renderLock)
			{
				ms = new List<ExecMark>(execMarks);
				ss = new List<SetupMark>(setupMarks);
			}
			float k = Math.Max(50, StatusPanelScale) / 100f;
			float x0 = ChartPanel.X, x1 = ChartPanel.X + ChartPanel.W;
			DateTime tFrom	= ChartBars.GetTimeByBarIdx(chartControl, ChartBars.FromIndex);
			DateTime tTo	= ChartBars.GetTimeByBarIdx(chartControl, ChartBars.ToIndex);

			SharpDX.Direct2D1.StrokeStyle dash = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
				new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
			trash.Add(dash);
			SharpDX.Direct2D1.SolidColorBrush mag		= DxBrush(trash, 255, 0, 255);
			SharpDX.Direct2D1.SolidColorBrush gray		= DxBrush(trash, 150, 150, 150);
			SharpDX.Direct2D1.SolidColorBrush green		= DxBrush(trash, 50, 205, 50);
			SharpDX.Direct2D1.SolidColorBrush red		= DxBrush(trash, 255, 99, 71);
			SharpDX.Direct2D1.SolidColorBrush gold		= DxBrush(trash, 218, 165, 32);
			SharpDX.Direct2D1.SolidColorBrush blue		= DxBrush(trash, 30, 144, 255);
			SharpDX.Direct2D1.SolidColorBrush sky		= DxBrush(trash, 0, 191, 255);
			SharpDX.Direct2D1.SolidColorBrush orange	= DxBrush(trash, 255, 165, 0);
			SharpDX.Direct2D1.SolidColorBrush white		= DxBrush(trash, 235, 235, 235);
			SharpDX.DirectWrite.TextFormat fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic", 11f * k);
			SharpDX.DirectWrite.TextFormat big = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic",
				SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 13f * k);
			trash.Add(fmt);
			trash.Add(big);

			// 손절선(자홍 실선)·평단선(회색 점선): 보유 중일 때 차트 끝에서 끝까지
			double sp = renderStop, ap = renderAvg;
			if (!double.IsNaN(sp))
			{
				float y = chartScale.GetYByValue(sp);
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), mag, 2f);
			}
			if (!double.IsNaN(ap))
			{
				float y = chartScale.GetYByValue(ap);
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), gray, 1f, dash);
			}

			// 밴드 조건 마름모 (하늘색 = 매수 쪽, 주황 = 매도 쪽). 화면에 보이는 구간만
			foreach (SetupMark m in ss)
			{
				if (m.Time < tFrom || m.Time > tTo)
					continue;
				float x = chartControl.GetXByTime(m.Time), y = chartScale.GetYByValue(m.Price);
				SharpDX.DirectWrite.TextLayout l = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, "◆", fmt, 30f, 20f);
				trash.Add(l);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - l.Metrics.Width / 2, y - l.Metrics.Height / 2), l, m.Long ? sky : orange, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}

			// 체결 표시: 진입 ▲ 초록(매수)·▼ 빨강(매도), 익절 금색, 손절·본절 자홍, 청산 파랑 + 종류·수량
			foreach (ExecMark m in ms)
			{
				if (m.Time < tFrom || m.Time > tTo.AddMinutes(1))
					continue;
				float x = chartControl.GetXByTime(m.Minute), y = chartScale.GetYByValue(m.Price);
				SharpDX.Direct2D1.SolidColorBrush b = m.KindCode == 0 ? (m.Buy ? green : red) : m.KindCode == 1 ? gold : m.KindCode == 2 ? mag : blue;
				float dy = m.Buy ? 6f * k : -18f * k;
				SharpDX.DirectWrite.TextLayout la = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, m.Buy ? "▲" : "▼", big, 40f, 30f);
				SharpDX.DirectWrite.TextLayout lt = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, m.Kind + " " + m.Qty, fmt, 200f, 30f);
				trash.Add(la);
				trash.Add(lt);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - la.Metrics.Width / 2, y + dy), la, b, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - lt.Metrics.Width / 2, y + dy + (m.Buy ? 16f : -14f) * k), lt, b, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}

			// 오른쪽 위 진입 횟수
			string ec = entryCountText;
			if (!string.IsNullOrEmpty(ec))
			{
				SharpDX.DirectWrite.TextLayout l = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, ec, big, 600f, 60f);
				trash.Add(l);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x1 - l.Metrics.Width - 8f * k, ChartPanel.Y + 6f * k), l, white, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}
		}

		// 왼쪽 위 상태 상자: 구역마다 제목 줄(구역 색)과 본문. 줄 색은 맨 앞 기호: ● 초록, × 회색, 두 칸 들여쓰기 옅은 회색, 그 밖 흰색
		private void RenderStatusBox(List<IDisposable> trash)
		{
			List<PanelSection> sections = statusSections;
			if (sections == null || sections.Count == 0)
				return;
			float k = Math.Max(50, StatusPanelScale) / 100f;
			float margin = 8f * k, pad = 6f * k, gap = 5f * k, lineGap = 1f * k;
			float maxW = Math.Max(240f, Math.Min(620f * k, ChartPanel.W - 2 * margin));
			SharpDX.DirectWrite.TextFormat bodyFmt	= new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic", 12f * k);
			SharpDX.DirectWrite.TextFormat titleFmt	= new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic",
				SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 12f * k);
			SharpDX.DirectWrite.TextFormat headFmt	= new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic",
				SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 15f * k);
			trash.Add(bodyFmt);
			trash.Add(titleFmt);
			trash.Add(headFmt);

			// 글 배치를 먼저 다 만들어 상자 폭을 정함
			List<SharpDX.DirectWrite.TextLayout> titles = new List<SharpDX.DirectWrite.TextLayout>();
			List<List<SharpDX.DirectWrite.TextLayout>> bodies = new List<List<SharpDX.DirectWrite.TextLayout>>();
			float boxW = 0;
			foreach (PanelSection s in sections)
			{
				SharpDX.DirectWrite.TextLayout t = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s.Title, titleFmt, maxW, 100f * k);
				trash.Add(t);
				titles.Add(t);
				boxW = Math.Max(boxW, t.Metrics.WidthIncludingTrailingWhitespace);
				List<SharpDX.DirectWrite.TextLayout> lines = new List<SharpDX.DirectWrite.TextLayout>();
				for (int i = 0; i < s.Lines.Count; i++)
				{
					SharpDX.DirectWrite.TextLayout l = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s.Lines[i],
						s.Headline && i == 0 ? headFmt : bodyFmt, maxW, 400f * k);
					trash.Add(l);
					lines.Add(l);
					boxW = Math.Max(boxW, l.Metrics.WidthIncludingTrailingWhitespace);
				}
				bodies.Add(lines);
			}
			boxW = Math.Min(maxW, boxW) + 2 * pad;

			SharpDX.Direct2D1.SolidColorBrush white	= DxBrush(trash, 240, 240, 240);
			SharpDX.Direct2D1.SolidColorBrush done	= DxBrush(trash, 120, 225, 120);
			SharpDX.Direct2D1.SolidColorBrush block	= DxBrush(trash, 150, 150, 150);
			SharpDX.Direct2D1.SolidColorBrush note	= DxBrush(trash, 180, 180, 190);

			// 차트 왼쪽 위에는 NinjaTrader 이름표(DisplayName)가 한 줄 그려지므로 그 아래에 둔다
			float x = ChartPanel.X + margin, y = ChartPanel.Y + margin + 20f;
			for (int n = 0; n < sections.Count; n++)
			{
				PanelSection s = sections[n];
				Color c = s.Tone;
				SharpDX.Direct2D1.SolidColorBrush headBg	= DxBrush(trash, c.R, c.G, c.B, 240);
				SharpDX.Direct2D1.SolidColorBrush bodyBg	= DxBrush(trash, (byte)(c.R / 5 + 14), (byte)(c.G / 5 + 14), (byte)(c.B / 5 + 14), 235);
				SharpDX.Direct2D1.SolidColorBrush headText	= DxBrush(trash, (byte)((c.R + 255 * 2) / 3), (byte)((c.G + 255 * 2) / 3), (byte)((c.B + 255 * 2) / 3));

				float titleH = titles[n].Metrics.Height + 4f * k;
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, boxW, titleH), headBg);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + pad, y + 2f * k), titles[n], white, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
				y += titleH;

				if (bodies[n].Count > 0)
				{
					float bodyH = 2 * pad - lineGap;
					foreach (SharpDX.DirectWrite.TextLayout l in bodies[n])
						bodyH += l.Metrics.Height + lineGap;
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, boxW, bodyH), bodyBg);
					float ly = y + pad;
					for (int i = 0; i < bodies[n].Count; i++)
					{
						string t = s.Lines[i];
						SharpDX.Direct2D1.SolidColorBrush b = s.Headline && i == 0 ? headText
							: t.StartsWith("●") ? done : t.StartsWith("×") ? block : t.StartsWith("  ") ? note : white;
						RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + pad, ly), bodies[n][i], b, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
						ly += bodies[n][i].Metrics.Height + lineGap;
					}
					y += bodyH;
				}
				y += gap;
			}
		}
		#endregion

		#region 헬퍼 (대회 시간 — spec 7장)
		// 신규 진입 허용 시간대: 신호 봉이 ContestStartTime(21:30)~EntryEndTime(23:15). 자정을 넘는 설정도 처리한다
		private bool InEntryWindow()
		{
			return IsTimeInWindow(ToTime(Time[0]), ContestStartTime, EntryEndTime);
		}

		// 자정을 넘는 구간(start > end)도 처리하는 HHmmss 시간 비교
		private bool IsTimeInWindow(int t, int start, int end)
		{
			if (start <= end)
				return t >= start && t <= end;		// 같은 날 구간
			return t >= start || t <= end;			// 자정을 넘는 구간
		}

		// 종료 청산 시각: FlattenTime(23:27)부터 다음 대회 시작(ContestStartTime) 전까지.
		// 이 구간에서 포지션이 있으면 청산하고, 첫 청산 뒤에는 미보유라 추가 주문이 나가지 않는다
		private bool IsFlattenTime()
		{
			int t = ToTime(Time[0]);
			if (FlattenTime < ContestStartTime)
				return t >= FlattenTime && t < ContestStartTime;	// 청산 시각이 자정 뒤 (예: 00:27 ~ 다음 날 22:30)
			return t >= FlattenTime || t < ContestStartTime;		// 청산 시각이 같은 날 (예: 23:27 ~ 자정 ~ 다음 날 21:30)
		}
		#endregion

		#region 고정값 (설정창에서 뺀 것, 2026-10-23 정리)
		// 지금 운용에서 바꾸지 않는 값·꺼 둔 실험 기능. 값은 정리 전 기본값 그대로라 동작은 같다.
		// 10분봉 팀 레짐(7레짐으로 대체), 강한 돌파·전환의심·지표 발표 금지(꺼 둠), 역추세·횡보 진입(꺼 둠), 항상 켜는 규칙들
		private const bool MlLogToFile = true;
		private const bool UseTestEntry = false;
		private const int UpShortBand = 3;
		private const int T1WindowUp = 0;
		private const bool DnShortNeedBoth = false;
		private const int DnLongBand = 3;
		private const bool DnLongNeedDiv = false;
		private const int T1WindowDn = 0;
		private const int SideBand = 1;
		private const int T1WindowSide = 0;
		private const bool UseCounterTrend = false;
		private const int CounterQtyPercent = 100;
		private const bool BlockReentryAfterStop = true;
		private const double MacdMinChangeAtr = 0;
		private const int RegimeFast = 6;
		private const int RegimeMid = 18;
		private const int RegimeSlow = 36;
		private const bool UseRule12 = true;
		private const int Rule12Bars = 3;
		private const bool Rule12PriceFilter = true;
		private const int FastTrendBars = 2;
		private const bool FastTrendSlope = false;
		private const int RegimeBarMinutes = 10;
		private const int RegimeMode = 2;
		private const int RegimeScoreMin = 2;
		private const double RegimeScoreRsiBand = 5;
		private const int RegimeScoreEventBars = 3;
		private const int RegimeScoreExit = 0;
		private const int RegimeHistBars = 2;
		private const int RegimeDivBars = 15;
		private const int RegimePivotStrength = 2;
		private const double StopPercent = 0.2;
		private const bool UseSwingStop = true;
		private const bool StopUseFarthest = true;
		private const double MaxStopPoints = 0;
		private const double MaxStopAtr = 0;
		private const double BreakevenAtR = 1;
		private const int BreakevenBufferTicks = 8;
		private const bool HandleOrderErrors = true;
		private const bool IntrabarTakeProfit = true;
		private const int SideRestBand = 3;
		private const int SideRestNarrowTime = 213000;
		private const bool DnShortExitSma = true;
		private const double BandMult1 = 1;
		private const double BandMult2 = 2;
		private const double BandMult3 = 3;
		private const bool UseR7Regime = true;
		private const bool TrendEntryPullbackOnly = false;
		private const bool UseSideEntry = false;
		private const bool UseWarnExit = false;
		private const int NewsBlockMinutes = 0;
		private const bool UseTrailAfterTp1 = true;
		private const bool UseTrendChangeExit = true;
		private const bool UseStrongBreakout = false;
		private const bool SbAllowStartSuspect = false;
		private const double SbStopPoints = 4;
		private const double SbTp1Atr = 1.5;
		private const double SbTp2Atr = 3.0;
		private const double SbMinFast = 1.2;
		private const double SbMinVel = 0.25;
		private const double SbMaxEr = 1.0;
		private const double SbNearAtr = 1.0;
		private const int SbEndTime = 230000;
		private const int ForceMarginBars = 2;
		private const bool DnShortSmaBlock = true;
		private const bool SkipPassedChunks = true;
		#endregion

		#region Properties
		// 설정창에는 Display의 Name(한국어 설명 + 괄호 안 코드 이름)이 보인다. 묶음은 기능별이고 앞의 숫자 순서로 나온다.
		// 코드 이름은 docs/backtest-guide.md와 docs/spec.md 8장에서 쓰는 이름이다

		// ── 0. 화면·기록·테스트 ──
		[NinjaScriptProperty]
		[Display(Name = "차트에 상태 표시 (ShowStatus)", Description = "추세 배경색, 밴드, 밴드 조건 마름모, 체결 라벨, 손절선, 상태 상자를 차트에 표시. 매매 판단과 무관", GroupName = "0. 화면·기록", Order = 0)]
		public bool ShowStatus { get; set; }

		[NinjaScriptProperty]
		[Range(50, 300)]
		[Display(Name = "상태 상자 크기 % (StatusPanelScale)", Description = "기본 130. 100이면 본문 12 / 첫 줄 15 / 폭 620px. 체결 표시·진입 횟수 글자도 같은 배율", GroupName = "0. 화면·기록", Order = 3)]
		public int StatusPanelScale { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "로그를 파일로 저장 (LogToFile)", Description = "실시간·Playback 로그를 내 문서\\NinjaTrader 8\\TQ_log_종목.txt에 이어서 저장. 백테스트에서는 저장하지 않음", GroupName = "0. 화면·기록", Order = 1)]
		public bool LogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "거래 요약을 파일로 저장 (TradeLogToFile)", Description = "거래 1건이 끝날 때마다 한 줄을 내 문서\\NinjaTrader 8\\TQ_trades_종목.csv에 저장. 백테스트에서도 저장하므로 여러 날 결과를 거래 단위로 볼 수 있음. 최적화(Optimize)를 돌릴 때는 끔", GroupName = "0. 화면·기록", Order = 2)]
		public bool TradeLogToFile { get; set; }

		// ── 1. 진입 — 상승추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "주력 매수: 몇 배 밴드 아래로 이탈하면 (UpLongBand)", Description = "상승추세 매수(5.1)의 밴드 조건. 명세는 1 (−1배 이탈). 0 = 중심선 이탈", GroupName = "2. 진입", Order = 0)]
		public int UpLongBand { get; set; }

		// ── 2. 진입 — 하락추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "주력 매도: 몇 배 밴드 위로 돌파하면 (DnShortBand)", Description = "하락추세 매도(5.4)의 밴드 조건. 명세는 2 (+2배 돌파). 1로 낮추면 얕은 반등에서도 매도. 0 = 중심선 돌파", GroupName = "2. 진입", Order = 1)]
		public int DnShortBand { get; set; }

		// ── 3. 진입 — 횡보 ──
		// ── 4. 진입 — 공통 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "반전 신호를 기다리는 봉 수, 공통 (T1Window)", Description = "밴드 조건 뒤 반전 신호를 기다리는 봉 수. 실제로는 여기에 (MACD 확인 봉 수 − 1)을 더한다. 명세는 3 (미정)", GroupName = "2. 진입", Order = 3)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "추세 방향 진입: 밴드 조건 없이 MACD 전환 + 크로스로도 (TrendT2Entry)", Description = "켜면 상승추세에서 MACD 상승 전환 + 골든크로스가 같이 나오면 주력 매수, 하락추세에서 MACD 하락 전환 + 데드크로스면 주력 매도. 밴드까지 눌리지(반등하지) 않는 추세에서 진입하기 위함. 명세에는 횡보에만 있는 조건", GroupName = "2. 진입", Order = 5)]
		public bool TrendT2Entry { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MACD + 크로스 동시 조건: MACD가 꺾인 봉의 크로스도 인정 (T2CountConfirmBars)", Description = "MACD 전환은 2봉 연속이어야 확인되므로 실제로 꺾인 봉은 확인 봉보다 앞이다. 켜면 전환이 처음 확인된 봉에서, 확인에 걸린 봉 수만큼 앞에서 나온 골든·데드크로스도 같이 나온 것으로 인정한다. 진입과 청산의 'MACD 전환 + 크로스' 조건 모두에 적용", GroupName = "2. 진입", Order = 6)]
		public bool T2CountConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "밴드 조건: 종가 대신 고가·저가 터치 (BandTouchEntry)", Description = "켜면 진입 밴드 조건(주력 매수·매도, 역추세 매수, 횡보 매수·매도)을 종가 돌파 대신 저가(매수)·고가(매도)가 밴드에 처음 닿은 봉으로 판단한다. 익절의 밴드 터치와 같은 방식. 끄면 명세(종가 돌파)", GroupName = "2. 진입", Order = 2)]
		public bool BandTouchEntry { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 전환 확인 봉 수 (MacdConfirmBars)", Description = "MACD 히스토그램이 몇 봉 연속 커져야(작아져야) 전환으로 볼지. 1 = 직전 봉 대비, 2 = 2봉 연속", GroupName = "2. 진입", Order = 4)]
		public int MacdConfirmBars { get; set; }

		// ── 5. 추세 판단 (10분봉) ──
		// ── 6. 손절·본절 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "전저점·전고점을 찾는 봉 수 (SwingBars)", Description = "신호 봉을 포함한 최근 몇 봉의 최저가·최고가를 쓸지. 명세는 5", GroupName = "3. 손절·청산", Order = 0)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "손절 최소 거리, ATR 배수 (MinStopAtr)", Description = "손절이 이보다 가까우면 이 거리까지 벌림. 0 = 사용 안 함. 손절이 진입가 바로 옆에 놓이는 것을 막음", GroupName = "3. 손절·청산", Order = 1)]
		public double MinStopAtr { get; set; }

		// ── 7. 청산 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "상승추세 매수: 강한 모멘텀 청산 이동평균 (MomentumSmaPeriod)", Description = "+3배 돌파 뒤 종가가 이 3분봉 이동평균 아래로 내려가면 나머지 전량 청산. 명세는 20", GroupName = "3. 손절·청산", Order = 3)]
		public int MomentumSmaPeriod { get; set; }

		// ── 8. 수량·대회 시간 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "진입 계약 수 (EntryQuantity)", Description = "한 번 진입할 때의 마이크로 계약 수. 30이면 1/2·1/3·1/5로 나눠떨어짐", GroupName = "1. 대회·수량", Order = 5)]
		public int EntryQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "대회 한도 계약 수 (MaxQuantity)", Description = "보유 합계 한도(마이크로). 대회 한도는 모든 종목 합산 미니 8계약 = MNQ 80. 불타기는 이 한도 안에서만 하고, 진입 계약 수가 더 크면 시작할 때 경고", GroupName = "1. 대회·수량", Order = 6)]
		public int MaxQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "진입 시작 시각 (ContestStartTime)", Description = "HHmmss, 한국시간. 213000 = 21:30. 이 시각부터의 신호로 진입", GroupName = "1. 대회·수량", Order = 0)]
		public int ContestStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "진입 마감 시각 (EntryEndTime)", Description = "HHmmss, 한국시간. 231500 = 23:15 (자정 뒤면 1500 = 00:15처럼 씀). 이 시각 뒤의 신호로는 진입하지 않음", GroupName = "1. 대회·수량", Order = 1)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "전량 청산 시각 (FlattenTime)", Description = "HHmmss, 한국시간. 232700 = 23:27 (자정 뒤면 2700 = 00:27처럼 씀). 이 시각의 봉이 마감되면 남은 수량을 전량 청산", GroupName = "1. 대회·수량", Order = 2)]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "최소 진입 목표 횟수 (MinEntries)", Description = "종목당 최소 진입 횟수. 화면 표시와 강제 채우기(ForceMinEntries)에 쓰임. 대회 규정은 NQ·GC 기초자산군 합산 라운드당 4회", GroupName = "1. 대회·수량", Order = 3)]
		public int MinEntries { get; set; }

		// ── 9. 지표 계산값 (보통 그대로 둠) ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "밴드 중심선 EMA 기간 (BandMidPeriod)", Description = "ATR 채널 중심선. 명세는 26", GroupName = "4. 지표 계산값", Order = 0)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "밴드 ATR 기간 (BandAtrPeriod)", Description = "명세는 14", GroupName = "4. 지표 계산값", Order = 1)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 단기 (MacdFast)", Description = "명세는 12", GroupName = "4. 지표 계산값", Order = 2)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 장기 (MacdSlow)", Description = "명세는 26", GroupName = "4. 지표 계산값", Order = 3)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 시그널 (MacdSmooth)", Description = "명세는 9", GroupName = "4. 지표 계산값", Order = 4)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "스토캐스틱 %K 길이 (StochPeriodK)", Description = "명세는 10", GroupName = "4. 지표 계산값", Order = 5)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "스토캐스틱 %K 스무딩 (StochSmooth)", Description = "명세는 5", GroupName = "4. 지표 계산값", Order = 6)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "스토캐스틱 %D 스무딩 (StochPeriodD)", Description = "명세는 5", GroupName = "4. 지표 계산값", Order = 7)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "스토캐스틱 과매도 기준 (CrossLow)", Description = "골든크로스는 %K가 이 값 아래였던 뒤에만 인정. %K 하방 돌파 기준이기도 함. 명세는 20", GroupName = "4. 지표 계산값", Order = 8)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "스토캐스틱 과매수 기준 (CrossHigh)", Description = "데드크로스는 %K가 이 값 위였던 뒤에만 인정. %K 상향 돌파 기준이기도 함. 명세는 80", GroupName = "4. 지표 계산값", Order = 9)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "크로스 인정 기한 봉 수 (CrossFilterBars)", Description = "위의 '아래(위)였던 적'을 현재 봉 포함 최근 몇 봉에서 찾을지. 명세는 5", GroupName = "4. 지표 계산값", Order = 10)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RSI 기간 (RsiPeriod)", Description = "명세는 14", GroupName = "4. 지표 계산값", Order = 11)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RSI 과매수 기준 (RsiHigh)", Description = "이 값 이상이면 과매수. 명세는 70", GroupName = "4. 지표 계산값", Order = 12)]
		public double RsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RSI 과매도 기준 (RsiLow)", Description = "이 값 미만이면 과매도. 명세는 20", GroupName = "4. 지표 계산값", Order = 13)]
		public double RsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "다이버전스 비교 구간 시작, 몇 봉 전 (DivFrom)", Description = "명세는 5", GroupName = "4. 지표 계산값", Order = 14)]
		public int DivFrom { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "다이버전스 비교 구간 끝, 몇 봉 전 (DivTo)", Description = "명세는 30", GroupName = "4. 지표 계산값", Order = 15)]
		public int DivTo { get; set; }

		// ── 10. 테스트 확장 (브랜치 test/score-v1-plus, docs/plus-design.md). 모두 끄면 위의 트레이더 전략 그대로 ──
		[NinjaScriptProperty]
		[Display(Name = "최소 진입 강제 채우기 (ForceMinEntries)", Description = "종료 청산 전에 진입 횟수가 최소 진입 목표보다 적으면 1계약 시장가 진입 → 다음 봉 청산을 되풀이. 시작 시각은 남은 봉 수로 거꾸로 정함(필요한 횟수 × 2 + 보유 중이면 정리 1 + 여유). 보유 포지션은 이미 1회로 세지므로 끝까지 들고 감", GroupName = "1. 대회·수량", Order = 4)]
		public bool ForceMinEntries { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000000)]
		[Display(Name = "1회 위험 상한 $ (RiskCapUsd)", Description = "트레이더 진입의 신호 봉 종가 기준 손절 거리 × 계약당 가치 × 수량이 이 값을 넘으면 수량을 줄임. 0 = 끔. 35일 재현: 끔 $60,089 / $4,000 $56,872 / $3,000 $48,905 / $2,000 $36,124. 10/15 21:37 Playback 위험 $5,700 거래 −$3,192", GroupName = "1. 대회·수량", Order = 9)]
		public double RiskCapUsd { get; set; }

		[NinjaScriptProperty]
		[Range(0, 5)]
		[Display(Name = "1차 뒤 본절에 필요한 이익 R (BreakevenMinR)", Description = "1차 익절 체결가의 이익이 처음 손절 거리 × 이 값보다 작으면 손절을 평단으로 옮기지 않고 그대로 둠(트레일링은 그대로). 0 = 트레이더 원래 규칙(항상 평단). 35일 재현: 0 $54,678 / 0.25 $62,978 / 0.5 $59,763", GroupName = "3. 손절·청산", Order = 2)]
		public double BreakevenMinR { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1)]
		[Display(Name = "흔들리는 장 효율비 (ChopMaxEr)", Description = "대회 구간 시작 60봉 뒤부터, 첫 봉 종가 기준 그날 효율비(|순변화| ÷ 변화의 합)가 이 값 이하면 흔들리는 장으로 보고 추세 진입(5.1·5.4)을 7레짐 '눌림'에서만 함. 0 = 끔. 0.15는 v6 박스 장 감지 값 그대로. 재현기 45일: 끔 10월 −$11,753 → 켬 +$283, 네 구간 모두 +", GroupName = "2. 진입", Order = 9)]
		public double ChopMaxEr { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "추세 지속 진입 봉 수 (TrendContBars)", Description = "7레짐이 같은 방향 '추세'로 이 봉 수만큼 이어졌는데 미보유면, 눌림 신호 없이 추세 방향으로 한 번 진입(5.1·5.4와 같은 분할·손절·청산). 같은 추세 구간에서 한 번, 흔들리는 장에서는 안 함. 0 = 끔. 재현기 45일: 끔 +$55,196 → 8봉 +$75,248 (6~12봉 모두 네 구간 +)", GroupName = "2. 진입", Order = 7)]
		public int TrendContBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "추세 지속 진입 최대 속도 (TrendContMaxVel)", Description = "추세 지속 진입은 점수 속도(6봉 전 대비 점수 변화)가 추세 방향으로 이 값 미만일 때만 함. 이미 빠르게 달리는 중이면 건너뛰고 같은 추세 구간 안에서 속도가 줄 때까지 기다림. 0 = 조건 없음. 재현기 45일: 없음 +$74,875 → 0.2 +$80,818 (네 구간 모두 같거나 나음, 최악의 날 −$5,698 → −$4,506)", GroupName = "2. 진입", Order = 8)]
		public double TrendContMaxVel { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "불타기 계약 수 (AddOnQty)", Description = "추세 진입(5.1·5.4)이 2차 익절까지 가고 7레짐이 아직 같은 방향 추세면 이 수량을 시장가로 한 번 더함. 보유 합계는 대회 한도(MaxQuantity) 이하. 손절은 지금 손절(트레일링)이 추가분까지 덮음. 0 = 끔. 재현기 45일(MNQ): 80계약 고정 +$144,544 / 장중 최저 −$10,359 → 진입 50 + 불타기 30 +$166,156 / −$7,830", GroupName = "1. 대회·수량", Order = 10)]
		public int AddOnQty { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "불타기 마감 시각 (AddOnEndTime)", Description = "HHmmss, 한국시간. 이 시각 뒤에는 불타기를 하지 않음", GroupName = "1. 대회·수량", Order = 11)]
		public int AddOnEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "불타기 최소 여유 ATR (AddOnMinRoomAtr)", Description = "불타기는 현재가와 손절 사이가 이 값 × 1분 ATR 이상일 때만 함(손절이 바로 옆이면 더하자마자 털림). 0 = 조건 없음. 재현기 45일: 없음 +$166,156 / 1 +$168,871 / 2 +$166,876 / 3 +$110,790", GroupName = "1. 대회·수량", Order = 12)]
		public double AddOnMinRoomAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "5.4 보류 뒤 기다릴 봉 수 (DnShortSmaWaitBars)", Description = "보류한 매도 신호 뒤 이 봉 수 안에 종가가 SMA20 아래로 오고 7레짐이 하락(추세·눌림)이면 매도 진입. 0 = 기다리지 않음. 재현기 45일: 막기만 +$155,702 / 3·5봉 +$159,424 / 10봉 +$161,542 / 15봉 +$157,649 (막지 않음 +$168,871)", GroupName = "2. 진입", Order = 10)]
		public int DnShortSmaWaitBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "초반 수량 마감 시각 (EarlyQtyEndTime)", Description = "HHmmss. 대회 시작부터 이 시각 전 신호 봉의 진입은 EarlyQty 이하. 21:30 지표 발표 직후 구간", GroupName = "1. 대회·수량", Order = 7)]
		public int EarlyQtyEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "초반 진입 수량 (EarlyQty)", Description = "EarlyQtyEndTime 전 신호 봉의 진입 수량 상한. 재현기 45일: 50 그대로 +$161,032 / 25 +$162,559(최악의 날 −$6,408 → −$6,008)", GroupName = "1. 대회·수량", Order = 8)]
		public int EarlyQty { get; set; }
		#endregion
	}
}
