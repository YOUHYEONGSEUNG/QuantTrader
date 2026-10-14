#region Using declarations
using System;
using System.Collections.Generic;	// 보유 시그널·수량 추적
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;					// Brush, Brushes (화면 표시)
using System.Xml.Serialization;				// XmlIgnore (설정창의 버튼·표시 전용 항목)
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;						// DashStyleHelper
using NinjaTrader.Gui.Chart;				// ChartControl, ChartScale (OnRender)
using NinjaTrader.Gui.Tools;				// SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;	// Draw.TextFixed, TextPosition
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.TeamQuant;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.TeamQuant
{
	/// <summary>
	/// 대회 전략 (docs/spec.md v2). 1분봉 차트에 올리고 추세 봉(10분봉)은 내부에서 추가한다.
	/// 진입 라우팅, 손절·본절, 단계별 익절, 대회 시간 처리를 담당한다.
	/// </summary>
	// 설정창에서 다른 설정 때문에 쓰이지 않는 항목을 잠근다 (TQ_StrategyConverter, 파일 맨 아래). NT8 공식 문서 "Using a TypeConverter to Customize Property Grid Behavior"
	[TypeConverter("NinjaTrader.NinjaScript.Strategies.TeamQuant.TQ_StrategyConverter")]
	public class TQ_Strategy : Strategy
	{
		private TQ_ATRChannels	channels;	// 진입 봉 밴드 (spec 2장)
		private TQ_Regime		regime;		// 10분봉 추세 5단계 (spec 3장)
		private TQ_Signals		signals;	// 진입 봉 신호 (spec 2장·5장)
		private TQ_Regime		regimeFast;	// 두 단계 진입용: 진입 봉(1분봉)으로 같은 방식의 추세를 판단 (OrganicTwoStage, spec 5-2). 안 쓰면 null

		// spec 7장: 종목별 진입 횟수 (분할 진입 여러 건은 신호 1회). 미보유→보유 전환 때 1 증가
		private int				entryCount	= 0;
		private MarketPosition	lastPosition = MarketPosition.Flat;

		// 진입할 때의 추세 상태와, 어느 세부 규칙(spec 5.1~5.9)으로 진입했는지. 청산이 끝날 때까지 유지
		private int				entryRegime	= 0;
		private Rule			active		= Rule.None;
		private enum Rule { None, UpLong, UpSusLong, UpSusShort, DnShort, DnLong, DnSusLong, DnSusShort, SideLong, SideShort }
		private const int		NoBand		= int.MinValue;	// 밴드 익절이 없는 조각 (마지막 조각)

		// spec 4장 손절·본절 상태
		private bool			isLongPos;						// 현재 포지션 방향 (롱/숏)
		private double			stopSwing		= double.NaN;	// 신호 봉의 전저점(롱)/전고점(숏)
		private double			stopPrice		= double.NaN;	// 현재 걸어둔 손절가 (로그·갭 판정용)
		private double			initialStop		= double.NaN;	// 신호 봉 종가 기준 최초 손절가 (갭 판정용)
		private double			entryFillSum;					// 진입 체결가 × 수량 합 (평균 체결가 계산용)
		private int				entryFillQty;					// 진입 체결 수량 합
		private bool			gapFlattened;					// 갭으로 손절가를 넘어 체결돼 즉시 청산 중
		private bool			wasInEntryWindow;				// 진입 횟수 리셋용: 직전 봉이 진입 시간대였는지
		private bool			blockLong;						// 이번 대회 구간에서 롱이 손실 손절로 끝남 → 롱 재진입 금지
		private bool			blockShort;						// 이번 대회 구간에서 숏이 손실 손절로 끝남 → 숏 재진입 금지
		private double			initialRisk		= double.NaN;	// 체결 직후 손절 거리 (1R)
		private bool			reachedR;						// 최대 유리폭이 이익 보호 기준에 닿았음
		private int				regimeBip		= 1;			// 추세를 판단하는 시리즈 번호: 0 = 진입 봉, 1 = 10분봉, 2 = 3분봉
		private int				minBars0;						// 진입 봉 데이터 부족 가드
		private bool			breakevenDone;					// 손절이 평균 진입가 이상(롱)·이하(숏)으로 옮겨짐
		private string			stopNote		= "";			// 화면 표시용: 손절이 어디로 옮겨졌는지 (본절 / 1차 익절가)
		private double			deferredStop	= double.NaN;	// 현재가가 가까워 미뤄 둔 손절 이동 목표가 (봉 마감마다 재시도)
		private double			lastStopTarget	= double.NaN;	// 마지막으로 옮기려 한 손절가 (주문 거부 때 재시도용)
		private string			lastStopLabel	= "";
		private string			logPath;						// LogToFile용 로그 파일 경로
		// 거래 1건의 기록 (학습 데이터 파일의 결과 칸에 쓴다)
		private bool			trOpen;							// 기록 중인 거래가 있음
		private DateTime		trExitTime;
		private double			trStop0			= double.NaN;	// 체결 직후 손절가
		private double			trPnlPts;						// 실현 손익 합 (포인트 × 수량)
		private double			trMfe;							// 최대 유리폭 (포인트)
		private double			trMae;							// 최대 불리폭 (포인트)
		private int				trBars;
		private string			trExits			= "";
		private int				tradeQty;						// 이번 진입의 총 수량 (역추세는 CounterQtyPercent 적용)
		private bool			tradeMain;						// 이번 거래가 주력 진입인지 (재진입 제한·이익 보호 본절 예외). IsMainAt으로 정한다
		private string			mlPath;							// MlLogToFile용 학습 데이터 파일 경로
		private string			mlRunId			= "";			// 학습 데이터: 이번 실행 ID (같은 백테스트를 여러 번 돌린 중복 제거용)
		private string			mlSettingsId	= "";			// 학습 데이터: 설정 요약의 해시 (설정별로 나눠 보기용)
		private bool			mlHeaderWritten;				// 학습 데이터: 이번 실행의 설정 줄을 썼음
		private string			mlFeatures		= "";			// 학습 데이터: 신호 봉에서 잰 특징값 (CSV 조각)
		private double			mlPrevR			= double.NaN;	// 학습 데이터: 직전 거래의 손익(R)
		private DateTime		mlPrevExit		= DateTime.MinValue;	// 학습 데이터: 직전 거래의 청산 시각
		private ATR				atr;							// 손절 최소 거리(MinStopAtr)용 진입 봉 ATR
		private double			stopAtr			= double.NaN;	// 신호 봉의 ATR (손절 거리 하한 계산용)
		private Brush			upBackBrush;					// 화면 표시용: 상승추세 배경색
		private Brush			downBackBrush;					// 화면 표시용: 하락추세 배경색
		private Brush			upSuspectBrush;					// 화면 표시용: 하락 전환 의심 배경색
		private Brush			downSuspectBrush;				// 화면 표시용: 상승 전환 의심 배경색
		private SimpleFont		statusFont;						// 화면 표시용: 왼쪽 위 상자 글꼴
		private SimpleFont		markFont;						// 화면 표시용: 체결 라벨 글꼴
		private string			markTag			= "";			// 화면 표시용: 마지막 체결 라벨 태그 (같은 봉 수량 합산)
		private int				markQty;						// 화면 표시용: 그 라벨의 합산 수량

		// 분할 조각 (spec 5장). 조각마다 진입 시그널명이 하나씩 있다
		private readonly List<string>				liveSignals		= new List<string>();				// 아직 보유 중인 진입 시그널명
		private readonly List<string>				chunkOrder		= new List<string>();				// 이번 진입의 모든 조각 (단계 순서)
		private readonly Dictionary<string, int>	chunkQty		= new Dictionary<string, int>();	// 조각별 수량
		private readonly Dictionary<string, int>	chunkBand		= new Dictionary<string, int>();	// 조각별 익절 밴드 (+N = 위 N배, −N = 아래 N배, 0 = 중심선, NoBand = 없음)
		private readonly HashSet<string>			filledBand		= new HashSet<string>();			// 밴드 익절이 체결된 조각
		private readonly HashSet<string>			pendingBandExit	= new HashSet<string>();			// 봉 마감 시장가로 낸 밴드 익절 주문 이름 (체결 대기)
		private readonly HashSet<int>				filledLevels	= new HashSet<int>();				// 익절이 체결된 밴드 단계 (같은 밴드의 조각이 여러 개여도 한 단계)
		private int				tpCount;						// 체결된 밴드 익절 단계 수 (= filledLevels.Count)
		private double			firstTpPrice	= double.NaN;	// 1차 익절 체결가 (2차 익절 때 손절을 여기로)
		private bool			adopted;						// 의심 진입이 추세 규칙의 청산으로 갈아탔음 (AdoptTrendExits)

		// 추세가 바뀔 때 반전 신호 대기를 이어 가기 위한 상태 (KeepWaitOnTrendChange, TrackWaitCarry)
		private int				prevState		= int.MinValue;	// 직전 진입 봉에서의 추세 상태
		private int				prevLongWait	= -1;			// 직전 봉에서 그 추세의 매수 규칙 대기 상태 (−1 = 대기 없음)
		private int				prevLongBars;					// 그 규칙의 대기 봉 수
		private int				prevShortWait	= -1;
		private int				prevShortBars;
		private int				carryLongLeft;					// 이어받은 매수 대기의 남은 봉 수
		private int				carryShortLeft;
		private bool			carryLongFire;					// 이 봉에서 이어받은 대기의 반전 신호가 나옴
		private bool			carryShortFire;

		// 유기 모드 (OrganicMode)
		private int				stateChangedFrom	= int.MinValue;	// 이 봉에서 추세가 바뀌었으면 바뀌기 전 상태, 아니면 int.MinValue
		private int				entryQtyPercent		= 100;			// 이번 진입의 수량 비율 (전환 의심 때의 역진입은 줄인다)
		private bool			viaConfirm;							// 이번 진입이 추세가 확정된 봉에서 바로 들어간 것인지
		private bool			viaStage1;							// 이번 진입이 두 단계 진입의 1단계인지 (로그용)
		private bool			viaTrigger;							// 이번 진입이 의심 뒤 가격 확인 역진입인지 (로그용)
		private bool			suspectTriggerUsed;					// 지금의 전환 의심 구간에서 가격 확인 역진입을 이미 했음 (OrganicSuspectTrigger)

		// 반전 신호 큐 (OrganicSignalQueue, spec 5-2): 추세와 상관없이 진입 봉의 반전 신호를 방향별로 모아 두고,
		// 전환 의심이 뜨면 이미 나와 있던 신호로 바로 역진입한다
		private class QueuedSignal
		{
			public int		Bar;		// 신호가 나온 진입 봉 번호 (CurrentBars[0])
			public double	Price;		// 신호 봉 종가
			public double	Extreme;	// 신호 봉 기준 전고점(매도 신호)·전저점(매수 신호). 손절 기준이자 무효 기준
			public string	Name;		// 어느 규칙의 신호인지 (로그용)
		}
		private readonly List<QueuedSignal>	shortQueue	= new List<QueuedSignal>();	// 매도 쪽 반전 신호
		private readonly List<QueuedSignal>	longQueue	= new List<QueuedSignal>();	// 매수 쪽 반전 신호
		private bool			viaQueue;							// 이번 진입이 큐 신호로 들어간 것인지
		private QueuedSignal	queueUsed;							// 그 진입에 쓴 신호
		private bool			tradeQueueStop;						// 이번 거래의 손절을 큐 신호의 고점·저점 기준으로 잡음
		private bool			queueEntryUsed;						// 지금의 전환 의심 구간에서 큐 역진입을 이미 했음
		private bool			windowJustOpened;					// 이 봉이 진입 시간대의 첫 봉인지
		private bool			wasWindowBefore;					// 직전 봉이 진입 시간대였는지 (windowJustOpened 계산용)
		private int				stage;								// 두 단계 진입: 0 = 해당 없음, 1 = 1단계만 들어가 추세 봉 확정 대기, 2 = 확정됨(추가 진입했거나 건너뜀)
		private double			trailExtreme		= double.NaN;	// 이익 따라가기 청산: 진입 뒤 최고가(매수)·최저가(매도)

		// 검증용 임시 진입 (UseTestEntry). 보유 봉 수로 분할 익절·본절·청산을 결정적으로 재현
		private int				testHoldBars;					// 테스트 진입 후 경과한 보유 봉 수
		private const int		TestTp1Bar	= 2;				// 이 봉에 1차 익절 + 본절
		private const int		TestTp2Bar	= 3;				// 이 봉에 2차 익절
		private const int		TestExitBar	= 4;				// 이 봉에 나머지 전량 청산

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 (docs/spec.md)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				EntriesPerDirection	= 1;
				EntryHandling		= EntryHandling.UniqueEntries;	// 시그널명별 분할 진입

				// 기본값 = spec 8장 (2026-10-12 대면회의 이후 규칙)

				// 지표 계산값
				BandMidPeriod		= 26;
				BandAtrPeriod		= 14;
				BandMult1			= 1;
				BandMult2			= 2;
				BandMult3			= 3;
				MacdFast			= 12;
				MacdSlow			= 26;
				MacdSmooth			= 9;
				MacdConfirmBars		= 2;
				MacdMinChangeAtr	= 0;
				StochPeriodK		= 10;
				StochSmooth			= 5;
				StochPeriodD		= 5;
				CrossLow			= 20;
				CrossHigh			= 80;
				CrossFilterBars		= 5;
				RsiPeriod			= 14;

				// 진입 — 추세별 밴드 배수와 반전 신호 대기 봉 수 (0 = 중심선)
				UpLongBand			= 1;		// 5.1 상승추세 매수: 저가 −1배 터치
				UpSusLongBand		= 2;		// 5.2 하락 전환 의심 매수: 종가 −2배 아래
				UpSusShortBand		= 1;		// 5.3 하락 전환 의심 매도: 고가 +1배 터치
				DnShortBand			= 1;		// 5.4 하락추세 매도: 고가 +1배 터치
				DnLongBand			= 3;		// 5.5 하락추세 매수: 저가 −3배 터치
				DnLongRsi			= 30;		// 5.5 하락추세 매수: RSI 30 미만
				DnSusLongBand		= 1;		// 5.6 상승 전환 의심 매수: 저가 −1배 터치
				DnSusShortBand		= 2;		// 5.7 상승 전환 의심 매도: 종가 +2배 위
				SideBand			= 2;		// 5.8·5.9 횡보: ±2배 터치
				T1Window			= 5;

				// 진입 — 공통
				T1SameBar			= false;		// 밴드 조건 봉 자체의 반전 신호도 인정
				T2CountConfirmBars	= true;
				UseCounterTrend		= true;		// 5.5 하락추세 매수
				// 규칙별 사용 여부. 명세는 모두 켬. 끄면 그 규칙의 진입만 하지 않는다 (5.5는 위의 UseCounterTrend)
				UseUpLong			= true;		// 5.1
				UseUpSusLong		= true;		// 5.2
				UseUpSusShort		= true;		// 5.3
				UseDnShort			= true;		// 5.4
				UseDnSusLong		= true;		// 5.6
				UseDnSusShort		= true;		// 5.7
				UseSideLong			= true;		// 5.8
				UseSideShort		= true;		// 5.9
				CounterQtyPercent	= 100;
				BlockReentryAfterStop	= true;
				SkipEntryPastTarget	= false;		// 신호 봉 종가가 이미 1차 익절 밴드를 넘었으면 진입하지 않음

				// 추세 판단 (10분봉)
				RegimeBarMinutes	= 10;		// 3이면 3분봉, 1이면 진입 봉으로 추세 판단
				RegimeMode			= 1;		// 1 = 회의 문서 정의(정배열 AND MACD선 부호), 2 = 점수 방식(가격·MACD·RSI·스토캐스틱)
				RegimeNoSideways	= false;	// 켜면 횡보 없이 상승·하락추세와 각각의 전환 의심만 쓴다
				RegimeScoreMin		= 2;		// 점수 방식: 추세로 인정할 최소 점수 (같은 방향 지표 수 − 반대 방향 지표 수)
				KeepWaitOnTrendChange	= false;	// 추세가 바뀌어도 같은 방향의 반전 신호 대기를 새 추세의 규칙으로 이어 간다
				RegimeScoreRsiBand	= 5;		// 점수 방식: RSI 55 이상 / 45 이하를 방향으로 인정
				RegimeScoreEventBars	= 3;		// 점수 방식: 최근 몇 봉 안의 사건(MACD 전환·크로스·다이버전스)을 점수에 넣을지. 0 = 안 넣음
				RegimeRsiRelative	= false;	// 의심의 RSI 조건을 고정 기준(60 / 40) 대신 최근 평균에서 벗어난 폭으로
				RegimeRsiCenterBars	= 20;		// RSI 중심을 구하는 추세 봉 수
				RegimeRsiOffset		= 8;		// 중심에서 이만큼 벗어나야 의심의 RSI 조건 충족
				RegimeScoreExit		= 0;		// 점수 방식: 상승추세는 점수가 이 값 이하로 내려가면 끝남 (하락추세는 부호 반대)
				RegimeSuspectNeedsPrice	= false;	// 켜면 가격이 아직 추세 방향이면 전환 의심을 띄우지 않음
				RegimeSuspectMaxScore	= 5;		// 의심은 추세 점수가 이 값 이하(하락 쪽은 −값 이상)일 때만. 5 = 제한 없음
				RegimeSuspectMinEvidence	= 1;	// 의심에 필요한 근거 종류 수. 1 = 하나로 충분
				RegimeSuspectEvidenceBars	= 2;	// 근거를 모으는 추세 봉 수
				RegimeConfirmBars		= 1;		// 추세 시작 조건이 몇 봉 연속이어야 추세로 인정할지
				RegimeExitBars			= 1;		// 추세 종료 조건이 몇 봉 연속이어야 추세를 끝낼지
				RegimeFast			= 6;
				RegimeMid			= 18;
				RegimeSlow			= 36;
				RegimeRsiHigh		= 60;
				RegimeRsiLow		= 40;
				RegimeDivBars		= 15;
				RegimePivotStrength	= 2;
				RegimeHistBars		= 2;
				RegimeRsiBars		= 1;		// 의심의 RSI 조건을 최근 몇 봉 안에서 볼지 (1 = 같은 봉에서만)
				SuspectReturn		= true;		// 의심 중 반대 신호가 나오면 원래 추세로 돌아감

				// 손절·본절
				StopPercent			= 0.2;
				UseSwingStop		= true;
				StopWider			= true;		// 전저점(전고점)과 % 중 진입가에서 먼 쪽 (spec 4장)
				SwingBars			= 5;
				MinStopAtr			= 1;
				BreakevenAtR		= 0;		// 이익 보호 본절. 0 = 사용 안 함
				TrailStopAtTp2		= false;		// 2차 익절이 체결되면 손절을 1차 익절가로
				BreakevenBufferTicks	= 8;	// 손절을 옮길 때 현재가와 띄울 최소 거리 (주문 거부 방지)
				HandleOrderErrors	= true;		// 주문 오류를 전략이 직접 처리 (끄면 NinjaTrader가 전략을 끈다)

				// 청산
				IntrabarTakeProfit	= true;		// 밴드 익절은 가격이 닿는 즉시 (끄면 봉 마감 후 시장가)
				ExitSmaPeriod		= 18;		// 마지막 조각 청산용 이동평균 (진입 봉)
				LastChunkEarly		= false;	// 켜면 마지막 조각의 이동평균 조건을 진입 직후부터 본다
				AdoptTrendExits		= false;	// 의심 진입이 추세로 확정되면 추세 규칙의 익절점으로 옮긴다
				ExitOnTrendChange	= false;		// 추세가 포지션과 맞지 않게 바뀌면 전량 청산

				// 유기 모드 (회의 문서에 없는 실험, 기본 끔)
				OrganicMode				= false;
				OrganicSuspectQtyPercent	= 50;	// 전환 의심 때의 역진입 수량 %
				OrganicEnterOnConfirm	= true;		// 추세가 확정되는 봉에 바로 진입
				OrganicStopAtr			= 2;		// 손절 거리, ATR 배수
				OrganicBeMinR			= 0.5;		// 1차 익절 폭이 손절 거리의 이 배수 이상일 때만 평단으로
				OrganicSideBlockScore	= 2;		// 횡보에서 추세 봉 점수가 이만큼 반대쪽이면 그 방향 횡보 진입을 막음
				OrganicEnterAtOpen		= false;	// 진입 시간이 시작되는 봉에 이미 추세면 그 봉에서 진입
				OrganicHoldWhileTrend	= false;	// 추세가 포지션 방향 그대로면 진입 봉(1분봉)의 전량 청산 조건을 무시
				OrganicTwoStage			= false;	// 두 단계 진입: 1분봉 추세가 먼저 바뀌면 일부, 추세 봉이 확정하면 나머지
				OrganicStage1Percent	= 50;		// 두 단계 진입의 1단계 수량 %
				OrganicStopSwing		= false;	// 손절을 직전 저점·고점(구조) 기준으로, 거리는 아래 ATR 범위로 제한
				OrganicStopSwingBars	= 5;		// 구조 손절: 추세 봉 몇 개 안의 최저가·최고가
				OrganicStopMinAtr		= 1.5;		// 구조 손절: 최소 거리, 진입 봉 ATR 배수
				OrganicStopMaxAtr		= 3;		// 구조 손절: 최대 거리, 진입 봉 ATR 배수
				OrganicTp1Percent		= 0;		// 추세 규칙(5.1·5.4)의 1차 익절 수량 %. 0과 0이면 규칙 표 비율
				OrganicTp2Percent		= 0;		// 추세 규칙의 2차 익절 수량 %
				OrganicTrailAtr			= 0;		// 이익 따라가기 청산: 1차 익절 뒤 최고가(최저가)에서 ATR × 이 값. 0 = 사용 안 함
				OrganicSuspectTrigger	= false;	// 역진입: 의심이 뜨면 대기하고, 1분봉이 직전 저점(고점)을 깨면 진입
				OrganicSignalQueue		= false;	// 역진입: 의심 전에 나온 반전 신호를 큐에 모아 두고 의심이 뜨면 바로 진입
				OrganicQueueBars		= 10;		// 큐 신호의 유효 봉 수 (진입 봉)
				OrganicQueueMaxAtr		= 1.5;		// 신호 가격에서 진입 방향으로 ATR × 이 값 넘게 가 버렸으면 무효

				// 수량·대회 시간 (spec 6·7장)
				EntryQuantity		= 30;
				MaxQuantity			= 40;
				MinEntries			= 2;		// 종목당 최소 진입 (나스닥 2, 골드 2)
				// spec 7장: 2026-10-14 대회 시간을 21:30~23:30 KST로 확정 (그 전에는 22:30~00:30)
				ContestStartTime	= 213000;	// 21:30:00 KST
				EntryEndTime		= 231500;	// 23:15:00 KST
				FlattenTime			= 232700;	// 23:27:00 KST 봉 마감

				// 화면·기록·테스트
				ShowStatus			= true;
				StatusPanelScale	= 130;		// 상태 상자 크기 %
				LogToFile			= true;
				MlLogToFile			= true;
				UseTestEntry		= false;
				PresetOrganicBase	= false;	// 설정 세트: 유기 기준 세트 (ApplyPreset)

				// 여기까지가 회의 문서 규칙의 기본값이다. "기본값으로 되돌리기" 버튼이 쓰도록 담아 둔다
				RememberSpecDefaults();
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
				// 설정 세트가 켜져 있으면 그 세트의 값을 다시 넣는다. 저장된 설정을 불러올 때 개별 값이 세트와 다르게 들어와 있을 수 있다
				if (PresetOrganicBase)
					ApplyPreset();

				if (HandleOrderErrors)
					RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
			}
			else if (State == State.DataLoaded)
			{
				// false = ShowDebugVisuals. 밴드·MACD·스토캐스틱·RSI 설정값은 지표들에 같은 값을 넘긴다 (docs/interface.md)
				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				regimeBip	= RegimeBarMinutes == 1 ? 0 : RegimeBarMinutes == 3 ? 2 : 1;	// 추세를 판단하는 시리즈: 진입 봉(0), 10분봉(1), 3분봉(2)
				regime		= TQ_Regime(BarsArray[regimeBip], RegimeFast, RegimeMid, RegimeSlow, false,
								MacdFast, MacdSlow, MacdSmooth, RegimeHistBars,
								RsiPeriod, RegimeRsiHigh, RegimeRsiLow, RegimeDivBars, RegimePivotStrength,
								StochPeriodK, StochSmooth, StochPeriodD, CrossLow, CrossHigh, CrossFilterBars, SuspectReturn, RegimeRsiBars,
								RegimeMode, RegimeScoreMin, RegimeScoreRsiBand, RegimeNoSideways, RegimeScoreEventBars,
								RegimeRsiRelative, RegimeRsiCenterBars, RegimeRsiOffset,
				RegimeScoreExit, RegimeSuspectNeedsPrice,
				RegimeSuspectMaxScore, RegimeSuspectMinEvidence, RegimeSuspectEvidenceBars, RegimeConfirmBars, RegimeExitBars);
				// 두 단계 진입(spec 5-2): 같은 설정의 추세 판단을 진입 봉에도 돌린다. 추세 봉이 이미 진입 봉이면(RegimeBarMinutes 1) 쓰지 않는다
				regimeFast	= null;
				if (TwoStageOn())
					regimeFast	= TQ_Regime(BarsArray[0], RegimeFast, RegimeMid, RegimeSlow, false,
									MacdFast, MacdSlow, MacdSmooth, RegimeHistBars,
									RsiPeriod, RegimeRsiHigh, RegimeRsiLow, RegimeDivBars, RegimePivotStrength,
									StochPeriodK, StochSmooth, StochPeriodD, CrossLow, CrossHigh, CrossFilterBars, SuspectReturn, RegimeRsiBars,
									RegimeMode, RegimeScoreMin, RegimeScoreRsiBand, RegimeNoSideways, RegimeScoreEventBars,
									RegimeRsiRelative, RegimeRsiCenterBars, RegimeRsiOffset,
									RegimeScoreExit, RegimeSuspectNeedsPrice,
								RegimeSuspectMaxScore, RegimeSuspectMinEvidence, RegimeSuspectEvidenceBars, RegimeConfirmBars, RegimeExitBars);
				// 추세별 대기 봉 수(T1WindowUp/Dn/Side)는 2026-10-14에 전략 설정에서 뺐다. 0 = 모두 T1Window
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, SwingBars,
								BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3,
								MacdFast, MacdSlow, MacdSmooth,
								StochPeriodK, StochSmooth, StochPeriodD,
								ExitSmaPeriod, MacdConfirmBars, MacdMinChangeAtr, false,
								UpLongBand, UpSusLongBand, UpSusShortBand, DnShortBand, DnLongBand, DnLongRsi,
								DnSusLongBand, DnSusShortBand, SideBand,
								0, 0, 0, T2CountConfirmBars, T1SameBar);

				// 진입 봉 지표(밴드 EMA, MACD, 이동평균)가 자리 잡을 만큼의 봉 수
				minBars0	= Math.Max(Math.Max(BandMidPeriod, MacdSlow + MacdSmooth), ExitSmaPeriod) + 5;

				// 로그 파일: 내 문서\NinjaTrader 8\TQ_log_종목.txt (LogToFile)
				logPath		= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_log_" + Instrument.MasterInstrument.Name + ".txt");
				mlPath		= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_ml_" + Instrument.MasterInstrument.Name + ".csv");
				mlRunId		= DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
				// 설정 ID는 종목 이름([MNQ DEC26])을 빼고 만든다. 월물이 달라도 같은 설정이면 같은 ID
				string summary	= SettingsSummary();
				int cut			= summary.IndexOf("] ");
				mlSettingsId	= HashText(cut >= 0 ? summary.Substring(cut + 2) : summary);

				atr			= ATR(BandAtrPeriod);

				// 화면 표시: 밴드를 차트에 같이 올리고, 배경색 브러시와 글꼴을 한 번만 만든다 (직접 만든 브러시는 Freeze)
				if (ShowStatus)
					AddChartIndicator(channels);
				upBackBrush			= new SolidColorBrush(Color.FromArgb(30, 0, 200, 0));
				downBackBrush		= new SolidColorBrush(Color.FromArgb(30, 230, 0, 0));
				upSuspectBrush		= new SolidColorBrush(Color.FromArgb(30, 255, 165, 0));
				downSuspectBrush	= new SolidColorBrush(Color.FromArgb(30, 0, 160, 255));
				upBackBrush.Freeze();
				downBackBrush.Freeze();
				upSuspectBrush.Freeze();
				downSuspectBrush.Freeze();
				statusFont		= new SimpleFont("Malgun Gothic", 12);
				markFont		= new SimpleFont("Malgun Gothic", 11);

				// spec 6장: 분할 진입 합계는 EntryQuantity이고 불타기가 없으므로 보유 수량은 EntryQuantity를 넘지 않는다.
				// MaxQuantity(= 대회 한도)보다 크게 설정하면 한도를 넘을 수 있어 경고한다.
				if (EntryQuantity > MaxQuantity)
					Out(string.Format("[{0}][TQ_Strategy] 경고: 기본 진입 수량({1}) > 최대 보유 수량({2}, 대회 한도). 설정 확인 필요 (spec 6장)",
						Instrument.FullName, EntryQuantity, MaxQuantity));
			}
			else if (State == State.Realtime)
			{
				// 로그 파일 머리말: 어떤 설정으로 돌린 기록인지 남긴다
				Out("==== " + SettingsSummary());
			}
		}

		// 차트 왼쪽 위에 붙는 전략 이름표. 기본값은 설정값 91개를 전부 나열해 화면을 가리므로 이름만 보여 준다.
		// 설정값은 로그 파일 첫 줄(SettingsSummary)과 설정창에서 본다
		public override string DisplayName
		{
			get { return "TQ_Strategy"; }
		}

		// 어떤 설정으로 돌린 기록인지 한 줄로. 로그 파일과 학습 데이터 파일에 쓴다
		private string SettingsSummary()
		{
			return string.Format("[{0}] {1}분봉 | 밴드 상승매수={2} 하락의심 매수/매도={3}/{4} 하락매도={5} 하락매수={6}(RSI<{7}) 상승의심 매수/매도={8}/{9} 횡보={10} | 대기={11} 같은봉={12} T2앞봉인정={13} | 역추세={14} 역추세수량%={15} 재진입금지={16} | 추세봉={17}분 MA={18}/{19}/{20} RSI={21}/{22} 다이버전스={23}봉(고점폭{24}) 히스토그램={25}봉 의심복귀={26} | 손절%={27} 전저점={28} 넓은쪽={29} 최소ATR={30} 본절R={31} 2차익절손절이동={32} | 봉중익절={33} 청산MA={34} 마지막조각처음부터={35} 추세청산갈아타기={36} MACD확인={37} | 의심RSI봉수={38} 익절밴드넘은진입차단={39} 추세바뀌면청산={40} | 추세방식={41}(1문서/2점수) 최소점수={42} RSI폭={43} 횡보없음={44} 추세바뀌어도대기유지={45} | 규칙사용(상승매수/하락의심매수/하락의심매도/하락매도/하락매수/상승의심매수/상승의심매도/횡보매수/횡보매도)={46} | 유기모드={47} 역진입%={48} 확정봉진입={49} 손절ATR={50} 본절최소폭={51} 횡보차단점수={52} | 점수사건봉수={53} RSI상대기준={54} 중심봉수={55} 폭={56} 추세종료점수={57} 의심가격확인={58} 시작봉진입={59} 추세유지중청산무시={60} 유기기준세트={61} | 두단계={62} 1단계%={63} 구조손절={64} 구조봉수={65} 구조ATR={66}~{67} 익절%={68}/{69} 따라가기ATR={70} | 의심점수상한={71} 의심근거수={72}({73}봉) 추세확정봉={74} 추세종료봉={75} 의심가격확인진입={76} | 큐={77} 큐봉수={78} 큐멀어짐ATR={79}",
				Instrument.FullName, BarsPeriod.Value,
				UpLongBand, UpSusLongBand, UpSusShortBand, DnShortBand, DnLongBand, DnLongRsi, DnSusLongBand, DnSusShortBand, SideBand,
				T1Window, T1SameBar, T2CountConfirmBars,
				UseCounterTrend, CounterQtyPercent, BlockReentryAfterStop,
				TrendMinutes(), RegimeFast, RegimeMid, RegimeSlow, RegimeRsiHigh, RegimeRsiLow,
				RegimeDivBars, RegimePivotStrength, RegimeHistBars, SuspectReturn,
				StopPercent, UseSwingStop, StopWider, MinStopAtr, BreakevenAtR, TrailStopAtTp2,
				IntrabarTakeProfit, ExitSmaPeriod, LastChunkEarly, AdoptTrendExits, MacdConfirmBars,
				RegimeRsiBars, SkipEntryPastTarget, ExitOnTrendChange,
				RegimeMode, RegimeScoreMin, RegimeScoreRsiBand, RegimeNoSideways, KeepWaitOnTrendChange,
				// 위 괄호 안의 순서대로 켬 1 / 끔 0
				(UseUpLong ? "1" : "0") + (UseUpSusLong ? "1" : "0") + (UseUpSusShort ? "1" : "0") + (UseDnShort ? "1" : "0")
				+ (UseCounterTrend ? "1" : "0") + (UseDnSusLong ? "1" : "0") + (UseDnSusShort ? "1" : "0")
				+ (UseSideLong ? "1" : "0") + (UseSideShort ? "1" : "0"),
				OrganicMode, OrganicSuspectQtyPercent, OrganicEnterOnConfirm, OrganicStopAtr, OrganicBeMinR, OrganicSideBlockScore, RegimeScoreEventBars,
				RegimeRsiRelative, RegimeRsiCenterBars, RegimeRsiOffset,
				RegimeScoreExit, RegimeSuspectNeedsPrice, OrganicEnterAtOpen, OrganicHoldWhileTrend, PresetOrganicBase,
				OrganicTwoStage, OrganicStage1Percent, OrganicStopSwing, OrganicStopSwingBars, OrganicStopMinAtr, OrganicStopMaxAtr,
				OrganicTp1Percent, OrganicTp2Percent, OrganicTrailAtr,
				RegimeSuspectMaxScore, RegimeSuspectMinEvidence, RegimeSuspectEvidenceBars, RegimeConfirmBars, RegimeExitBars, OrganicSuspectTrigger,
				OrganicSignalQueue, OrganicQueueBars, OrganicQueueMaxAtr);
		}

		#region 거래 기록 — 매매 판단에는 영향 없음
		// 거래 1건의 손익·최대 유리폭·불리폭·청산 순서를 모아 학습 데이터 파일(MlLogToFile)의 결과 칸에 쓴다.
		// 거래 요약 파일(TQ_trades_종목.csv, TradeLogToFile)은 같은 내용이 학습 데이터 파일에 있어서 2026-10-14에 뺐다
		private void TradeBegin()
		{
			trOpen		= true;
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
		private void TradeExitFill(string name, double price, int quantity, DateTime time)
		{
			if (!trOpen || entryFillQty <= 0)
				return;
			double avg = entryFillSum / entryFillQty;
			trPnlPts	+= (isLongPos ? price - avg : avg - price) * quantity;
			trExitTime	= time;
			if (!trExits.EndsWith(name))
				trExits += (trExits.Length > 0 ? " > " : "") + name;
		}

		// 보유 → 미보유가 되면 학습 데이터 파일에 그 거래를 한 줄 쓴다
		private void TradeEnd()
		{
			if (!trOpen)
				return;
			trOpen = false;
			MlEnd();
		}
		#endregion

		#region 학습 데이터 파일 (MlLogToFile) — 매매 판단에는 영향 없음
		// 거래 1건마다 한 줄을 내 문서\NinjaTrader 8\TQ_ml_종목.csv에 쓴다.
		// 진입 신호 봉에서 잰 특징값(지표 상태)과 그 거래의 결과를 한 줄에 같이 둬서, 어떤 신호가 손절로 끝나는지 학습·분석하는 데 쓴다.
		// 열 이름은 Python(pandas)에서 바로 쓰도록 영어로 둔다. '#'으로 시작하는 줄은 실행별 설정 요약이다 (read_csv(comment='#'))
		// 2026-10-12부터 strategy는 9종 규칙 이름, regime은 +2 ~ −2(5단계), sma20_dist_atr은 청산 이동평균(18선)까지의 거리다
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
		private void MlBegin(Rule act)
		{
			mlFeatures = "";
			if (!MlLogToFile)
				return;

			double a	= atr[0];
			double c	= Close[0];
			bool okA	= a > 0;
			int r		= RegimeState();

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

			double htfDist = double.IsNaN(regime.SmaFast[0]) ? double.NaN : Closes[regimeBip][0] - regime.SmaFast[0];

			mlFeatures = string.Join(",", new string[]
			{
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
				((int)Time[0].DayOfWeek).ToString(),
				(ToTime(Time[0]) / 100).ToString(),
				act.ToString(),
				r.ToString(),
				isLongPos ? "1" : "-1",
				"0",	// via_t2: TrendT2Entry를 2026-10-14에 뺐다. 열 순서를 지키려고 칸만 남긴다
				Ml(c),
				Ml(a),
				Ml(c > 0 ? a / c * 100 : double.NaN),
				Ml(okA ? Math.Abs(c - initialStop) / a : double.NaN),
				Ml(okA ? (c - channels.Mid[0]) / a : double.NaN),
				Ml(okA ? (c - signals.Sma[0]) / a : double.NaN),
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
			double pnlPts	= trPnlPts / entryFillQty;	// 계약당 평균 손익 (포인트)
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

		protected override void OnBarUpdate()
		{
			// 데이터 부족 가드: 진입 봉 지표와 추세 봉 이동평균이 채워지기 전에는 아무것도 하지 않는다
			if (CurrentBars[0] < minBars0 || CurrentBars[regimeBip] < RegimeSlow || double.IsNaN(regime.Regime[0]))
				return;

			if (BarsInProgress == regimeBip)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] {2}분봉 마감 추세={3}{4} 배열={5} MACD선={6:F2} RSI={7:F1} 종가={8} 점수={9} ({10})",
					Time[0], Instrument.FullName, TrendMinutes(), StateText(RegimeState()),
					regime.SuspectReason.Length > 0 ? " [" + regime.SuspectReason + "]" : "",
					regime.Aligned, regime.MacdLine[0], regime.Rsi[0], Closes[regimeBip][0], regime.Score, regime.ScoreText));
				// 추세 봉이 따로 있으면(10분·3분) 여기서 끝낸다. 진입 봉으로 추세를 보는 경우(RegimeBarMinutes 1)는 같은 봉에서 매매 판단까지 이어 간다
				if (regimeBip != 0)
					return;
			}

			if (BarsInProgress != 0)
				return;

			Out(string.Format("[{0}][{1}][TQ_Strategy] 추세={2} 신호 상승매수={3} 하락의심매수={4} 하락의심매도={5} 하락매도={6} 하락매수={7} 상승의심매수={8} 상승의심매도={9} 횡보매수={10} 횡보매도={11} 대기(같은 순서)={12}/{13}/{14}/{15}/{16}/{17}/{18}/{19}/{20} 시고저종={21}/{22}/{23}/{24}",
				Time[0], Instrument.FullName, RegimeState(),
				signals.EntryUpLong[0], signals.EntryUpSusLong[0], signals.EntryUpSusShort[0],
				signals.EntryDnShort[0], signals.EntryDnLong[0], signals.EntryDnSusLong[0], signals.EntryDnSusShort[0],
				signals.EntrySideLong[0], signals.EntrySideShort[0],
				signals.WaitUpLong, signals.WaitUpSusLong, signals.WaitUpSusShort,
				signals.WaitDnShort, signals.WaitDnLong, signals.WaitDnSusLong, signals.WaitDnSusShort,
				signals.WaitSideLong, signals.WaitSideShort, Open[0], High[0], Low[0], Close[0]));	// 봉 가격은 나중에 로그만으로 분석하기 위함

			// 화면 표시: 추세 배경색과 밴드 조건 표시 (매매 판단과 무관)
			PaintBar();
			TradeTrack();
			TrackWaitCarry();	// 추세가 바뀔 때 반전 신호 대기를 이어 간다 (KeepWaitOnTrendChange)
			UpdateSignalQueue();	// 반전 신호 큐: 이 봉의 신호를 넣고 오래되거나 무효가 된 신호를 뺀다 (OrganicSignalQueue)

			// spec 7장: 진입 횟수는 대회 구간마다 0부터 센다. 진입 시간대에 들어서는 첫 봉에서 리셋한다
			bool inEntryWindow = InEntryWindow();
			if (inEntryWindow && !wasInEntryWindow)
			{
				entryCount	= 0;
				blockLong	= false;	// 재진입 제한도 대회 구간마다 푼다
				blockShort	= false;
			}
			wasInEntryWindow = inEntryWindow;
			windowJustOpened = inEntryWindow && !wasWindowBefore;	// 진입 시간대의 첫 봉인지 (유기 모드의 시작 봉 진입용)
			wasWindowBefore  = inEntryWindow;

			// spec 7장: 종목별 진입 횟수와 최소 진입 목표를 화면에 표시. 분할 진입 여러 건은 OnPositionUpdate에서 신호 1회로 센다.
			string entryStatus = entryCount >= MinEntries ? "" : string.Format(" (최소 {0} 미달)", MinEntries);
			Draw.TextFixed(this, "TQ_EntryCount",
				string.Format("{0} 진입: {1}/{2}회{3}", Instrument.MasterInstrument.Name, entryCount, MinEntries, entryStatus),
				TextPosition.TopRight);

			// spec 7장: 종료 청산 — FlattenTime(23:27) 봉이 마감되면 남은 포지션을 전량 시장가로 청산한다
			if (IsFlattenTime())
			{
				if (Position.MarketPosition == MarketPosition.Long)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 종료 청산(롱) 수량={2}",
						Time[0], Instrument.FullName, Position.Quantity));
					ExitLong();	// 전량 시장가. 걸린 손절 스탑은 포지션 청산 시 자동 취소됨
				}
				else if (Position.MarketPosition == MarketPosition.Short)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 종료 청산(숏) 수량={2}",
						Time[0], Instrument.FullName, Position.Quantity));
					ExitShort();
				}
				UpdateStatusPanel();
				return;	// 종료 시각 이후에는 신규 진입·익절 판단을 하지 않는다
			}

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				// 미보유: 진입·익절·손절 상태 전체 리셋
				ResetTradeState();

				// spec 1장 2 + 7장: 미보유 + 진입 허용 시간대(신호 봉 21:30~23:15)일 때만 신규 진입
				if (InEntryWindow())
				{
					if (UseTestEntry)	RouteTestEntry();	// 주문 흐름 검증용 임시 진입
					else				RouteEntry();
				}
			}
			else
			{
				// 보유 중: 새 진입 신호는 무시 (spec 1장 2).
				// 손절 재계산·갭 청산·본절 이동은 체결 시점에 OnExecutionUpdate에서 처리한다 (spec 4장)
				if (gapFlattened || active == Rule.None)
				{
					// 전량 청산 주문을 낸 뒤에도(갭 청산 포함) 포지션이 남아 있으면 다시 전량 청산한다.
					// 청산 주문이 거부됐거나 아직 체결되지 않은 경우다
					if (Position.MarketPosition == MarketPosition.Long)			ExitLong();
					else if (Position.MarketPosition == MarketPosition.Short)	ExitShort();
					UpdateStatusPanel();
					return;
				}

				// 현재가가 너무 가까워 미뤄 둔 손절 이동(본절, 1차 익절가)을 다시 시도한다
				RetryDeferredStop();

				if (UseTestEntry)
				{
					// 보유 봉 수 기반으로 분할 익절·본절·나머지 청산을 결정적으로 재현
					testHoldBars++;
					ManageTestExits();
				}
				else
				{
					// spec 4장 이익 보호 본절: 1차 익절 전이라도 유리폭이 기준에 닿으면 본절로 옮긴다
					CheckBreakevenAtR();

					// spec 5-2 이익 따라가기 청산: 1차 익절 뒤 추세 포지션의 손절을 최고가(최저가)를 따라 올린다 (OrganicTrailAtr)
					UpdateTrailStop();

					// 밴드 익절 지정가를 방금 마감된 봉의 밴드 값으로 옮긴다 (IntrabarTakeProfit)
					UpdateTargets();

					// spec 5장: 진입한 규칙의 청산
					ManageExits();

					// spec 5-2 두 단계 진입: 1단계 포지션의 추세 봉 확정(→ 추가 진입) 또는 1분봉 추세 소멸(→ 청산)
					if (active != Rule.None)
						ManageTwoStage();
				}
			}

			// 화면 표시: 이번 봉의 판단이 끝난 뒤 상태를 갱신한다
			UpdateStatusPanel();
		}

		// spec 7장: 분할 진입 여러 건은 신호 1회. 미보유(Flat)→보유 전환 시점에만 1 증가시킨다
		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (lastPosition == MarketPosition.Flat && marketPosition != MarketPosition.Flat)
			{
				entryCount++;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 신규 진입 #{2} 방향={3} 평균가={4} 수량={5}",
					Time[0], Instrument.FullName, entryCount, marketPosition, averagePrice, quantity));
			}

			// 거래 요약: 보유 → 미보유로 바뀌면 한 줄 쓴다
			if (lastPosition != MarketPosition.Flat && marketPosition == MarketPosition.Flat)
				TradeEnd();

			lastPosition = marketPosition;
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

		// 가장 최근에 마감된 추세 봉의 상태: +2 상승추세, +1 하락 전환 의심, 0 횡보, −1 상승 전환 의심, −2 하락추세 (spec 3장)
		private int RegimeState()
		{
			double v = regime.Regime[0];
			return double.IsNaN(v) ? 0 : (int)v;
		}

		// 추세를 판단하는 봉이 몇 분봉인지 (화면·로그용). RegimeBarMinutes 1이면 진입 봉(차트의 봉)
		private int TrendMinutes()
		{
			return RegimeBarMinutes == 1 ? BarsPeriod.Value : RegimeBarMinutes == 3 ? 3 : 10;
		}

		// 추세 값의 이름과 화면용 기호
		private string StateText(int st)
		{
			return st == 2 ? "▲ 상승추세" : st == 1 ? "△ 하락 전환 의심" : st == -1 ? "▽ 상승 전환 의심" : st == -2 ? "▼ 하락추세" : "◆ 횡보";
		}

		#region 규칙 표 (spec 5장)
		// 규칙별 분할 익절 계획: 단계별 익절 밴드(+N = 위 N배, −N = 아래 N배, 0 = 중심선)와 수량 비율(nums[i] / den).
		// 계획에 없는 나머지 수량이 마지막 조각이다
		private void Plan(Rule r, out string prefix, out int[] bands, out int[] nums, out int den)
		{
			switch (r)
			{
				case Rule.UpLong:		prefix = "UpL"; bands = new[] { 2, 3 };			nums = new[] { 1, 2 };			den = 5; break;	// 5.1
				case Rule.UpSusLong:	prefix = "UsL"; bands = new[] { 0, 1, 2, 3 };	nums = new[] { 1, 1, 1, 1 };	den = 5; break;	// 5.2
				case Rule.UpSusShort:	prefix = "UsS"; bands = new[] { -1, -2, -3 };	nums = new[] { 1, 1, 1 };		den = 4; break;	// 5.3
				case Rule.DnShort:		prefix = "DnS"; bands = new[] { -2, -3 };		nums = new[] { 1, 1 };			den = 3; break;	// 5.4
				case Rule.DnLong:		prefix = "DnL"; bands = new[] { 0, 1 };			nums = new[] { 1, 2 };			den = 4; break;	// 5.5
				case Rule.DnSusLong:	prefix = "DsL"; bands = new[] { 0, 1, 2, 3 };	nums = new[] { 1, 1, 1, 1 };	den = 5; break;	// 5.6
				case Rule.DnSusShort:	prefix = "DsS"; bands = new[] { 0, -1, -2, -3 };	nums = new[] { 1, 2, 1, 1 };	den = 6; break;	// 5.7
				case Rule.SideLong:		prefix = "SdL"; bands = new[] { 1, 2, 3 };		nums = new[] { 1, 1, 2 };		den = 5; break;	// 5.8
				case Rule.SideShort:	prefix = "SdS"; bands = new[] { -1, -2, -3 };	nums = new[] { 1, 1, 2 };		den = 5; break;	// 5.9
				default:				prefix = "X";   bands = new int[0];				nums = new int[0];				den = 1; break;
			}

			// spec 5-2 유기 모드의 익절 비중 (OrganicTp1Percent / OrganicTp2Percent): 추세 규칙(5.1, 5.4)의 두 밴드 단계 수량을 %로 정한다.
			// 밴드는 규칙 표 그대로이고, 남는 %가 마지막 조각이다. 둘 다 0이거나 합이 100을 넘으면 규칙 표의 비율을 쓴다
			if (OrganicMode && (r == Rule.UpLong || r == Rule.DnShort) && TpPercentOn())
			{
				nums	= new[] { OrganicTp1Percent, OrganicTp2Percent };
				den		= 100;
			}
		}

		// 유기 모드의 익절 비중 설정이 쓰이는지
		private bool TpPercentOn()
		{
			return (OrganicTp1Percent > 0 || OrganicTp2Percent > 0) && OrganicTp1Percent + OrganicTp2Percent <= 100;
		}

		private bool IsLongRule(Rule r)
		{
			return r == Rule.UpLong || r == Rule.UpSusLong || r == Rule.DnLong || r == Rule.DnSusLong || r == Rule.SideLong;
		}

		// 규칙별 사용 여부 (설정값 Use…). 명세는 모두 켬
		private bool RuleEnabled(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return UseUpLong;
				case Rule.UpSusLong:	return UseUpSusLong;
				case Rule.UpSusShort:	return UseUpSusShort;
				case Rule.DnShort:		return UseDnShort;
				case Rule.DnLong:		return UseCounterTrend;
				case Rule.DnSusLong:	return UseDnSusLong;
				case Rule.DnSusShort:	return UseDnSusShort;
				case Rule.SideLong:		return UseSideLong;
				case Rule.SideShort:	return UseSideShort;
				default:				return false;
			}
		}

		// 추세 방향 주력 진입 (5.1 상승추세 매수, 5.4 하락추세 매도)
		private bool IsMain(Rule r)
		{
			return r == Rule.UpLong || r == Rule.DnShort;
		}

		// 그 추세 상태에서 이 규칙으로 들어가는 것이 주력 진입인지. 재진입 제한과 이익 보호 본절의 예외를 정한다 (spec 4장).
		// 유기 모드는 전환 의심의 역진입과 두 단계 진입의 1단계에도 5.1·5.4 규칙을 빌려 쓰므로, 규칙 이름이 아니라
		// 실제로 그 방향 추세(상승추세 매수, 하락추세 매도)일 때만 주력으로 본다 (2026-10-14 수정. 그 전에는 역진입도 제한을 받지 않았음)
		private bool IsMainAt(Rule r, int st)
		{
			if (!IsMain(r))
				return false;
			if (!OrganicMode)
				return true;
			return r == Rule.UpLong ? st == 2 : st == -2;
		}

		private string RuleName(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return "상승추세 매수";
				case Rule.UpSusLong:	return "하락 전환 의심 매수";
				case Rule.UpSusShort:	return "하락 전환 의심 매도";
				case Rule.DnShort:		return "하락추세 매도";
				case Rule.DnLong:		return "하락추세 매수";
				case Rule.DnSusLong:	return "상승 전환 의심 매수";
				case Rule.DnSusShort:	return "상승 전환 의심 매도";
				case Rule.SideLong:		return "횡보 매수";
				case Rule.SideShort:	return "횡보 매도";
				default:				return "청산 주문 냄";
			}
		}

		// 규칙별 전량 청산 조건 (spec 5장). 단계와 상관없이 본다. 없으면 false
		private bool FullExitSignal(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return signals.T2Bear[0];								// MACD 하락 전환 AND 데드크로스
				case Rule.UpSusLong:	return signals.MacdDown[0] || signals.Dead[0];			// MACD 하락 전환 OR 데드크로스
				case Rule.UpSusShort:	return signals.T2Bull[0];								// MACD 상승 전환 AND 골든크로스
				case Rule.DnLong:		return signals.MacdDown[0] || signals.Dead[0];			// 단계 전이든 뒤든 MACD 하락 전환 OR 데드크로스
				case Rule.DnSusLong:	return signals.T2Bear[0];
				case Rule.DnSusShort:	return tpCount < 3 && signals.T2Bull[0];				// 3단계 체결 전에만
				default:				return false;											// 5.4, 5.8, 5.9는 전량 청산 조건이 없다
			}
		}

		private string FullExitText(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return "MACD▼ + 데드";
				case Rule.UpSusLong:	return "MACD▼ 또는 데드";
				case Rule.UpSusShort:	return "MACD▲ + 골든";
				case Rule.DnLong:		return "MACD▼ 또는 데드";
				case Rule.DnSusLong:	return "MACD▼ + 데드";
				case Rule.DnSusShort:	return tpCount < 3 ? "MACD▲ + 골든 (3단계 전까지)" : null;
				default:				return null;
			}
		}

		// 추세가 바뀌어 전량 청산해야 하는지 (spec 1장 원칙 3). 청산 사유를 돌려주고, 해당 없으면 null.
		//  · 추세 방향 포지션(5.1, 5.4): 추세가 전환 의심으로 바뀌면 항상. ExitOnTrendChange가 켜져 있으면 그 추세가 아니게 되는 모든 경우(횡보, 반대 추세 포함)
		//  · 그 밖의 포지션: ExitOnTrendChange가 켜져 있으면, 포지션과 반대 방향의 추세가 확정될 때 (매수 보유 중 하락추세, 매도 보유 중 상승추세).
		//    하락추세 매수(5.5)는 하락추세 안에서 들어가는 역추세 진입이라 제외한다
		private string TrendExitReason(Rule r, int st)
		{
			// 유기 모드: 규칙이 아니라 포지션 방향으로 본다. 매수는 하락 쪽 신호(하락 전환 의심, 하락추세)가 나오면,
			// 매도는 상승 쪽 신호(상승 전환 의심, 상승추세)가 나오면 전량 청산. 횡보로 바뀐 것만으로는 청산하지 않는다
			if (OrganicMode)
			{
				if (r == Rule.None)
					return null;
				if (isLongPos && st == 1)	return "매수 보유 중 하락 전환 의심";
				if (isLongPos && st == -2)	return "매수 보유 중 하락추세";
				if (!isLongPos && st == -1)	return "매도 보유 중 상승 전환 의심";
				if (!isLongPos && st == 2)	return "매도 보유 중 상승추세";
				return null;
			}

			if (r == Rule.UpLong)
			{
				if (st == 1)						return "추세가 하락 전환 의심으로 바뀜";
				if (ExitOnTrendChange && st != 2)	return "추세가 상승추세에서 " + (st == 0 ? "횡보" : "하락 쪽으") + "로 바뀜";
				return null;
			}
			if (r == Rule.DnShort)
			{
				if (st == -1)						return "추세가 상승 전환 의심으로 바뀜";
				if (ExitOnTrendChange && st != -2)	return "추세가 하락추세에서 " + (st == 0 ? "횡보" : "상승 쪽으") + "로 바뀜";
				return null;
			}
			if (!ExitOnTrendChange || r == Rule.None || r == Rule.DnLong)
				return null;
			if (isLongPos && st == -2)	return "매수 보유 중 하락추세 확정";
			if (!isLongPos && st == 2)	return "매도 보유 중 상승추세 확정";
			return null;
		}

		// 화면 표시용: 위 조건을 글로
		private string TrendExitText(Rule r)
		{
			if (OrganicMode)
				return r == Rule.None ? null : isLongPos ? "하락 전환 의심이나 하락추세가 되면" : "상승 전환 의심이나 상승추세가 되면";
			if (r == Rule.UpLong)	return ExitOnTrendChange ? "추세가 상승추세가 아니게 되면" : "추세가 하락 전환 의심으로 바뀌면";
			if (r == Rule.DnShort)	return ExitOnTrendChange ? "추세가 하락추세가 아니게 되면" : "추세가 상승 전환 의심으로 바뀌면";
			if (!ExitOnTrendChange || r == Rule.None || r == Rule.DnLong)
				return null;
			return isLongPos ? "하락추세가 확정되면" : "상승추세가 확정되면";
		}

		// 마지막 조각의 청산 조건 (spec 5장): 매도는 종가가 이동평균 위, 횡보 매수는 종가가 이동평균 아래.
		// 0 = 조건 없음(전량 청산 조건·손절·종료 청산으로만 나감), +1 = 종가 > 이동평균, −1 = 종가 < 이동평균
		private int LastChunkSide(Rule r)
		{
			switch (r)
			{
				case Rule.UpSusShort:
				case Rule.DnShort:
				case Rule.DnSusShort:
				case Rule.SideShort:	return 1;
				case Rule.SideLong:		return -1;
				default:				return 0;
			}
		}

		// 밴드 값 (+N = 위 N배, −N = 아래 N배, 0 = 중심선)
		private double BandPrice(int level)
		{
			switch (level)
			{
				case 0:		return channels.Mid[0];
				case 1:		return channels.Up1[0];
				case 2:		return channels.Up2[0];
				case 3:		return channels.Up3[0];
				case -1:	return channels.Dn1[0];
				case -2:	return channels.Dn2[0];
				default:	return level > 0 ? channels.Up3[0] : channels.Dn3[0];
			}
		}

		private string BandText(int level)
		{
			return level == 0 ? "중심선" : (level > 0 ? "+" : "−") + Math.Abs(level) + "배";
		}
		#endregion

		#region 진입 라우팅 (spec 3장·5장·6장)
		// 신호 봉의 추세 상태로 세부 규칙을 고르고, 그 규칙의 신호가 있으면 분할 진입한다 (spec 3장).
		// 같은 봉에 매수·매도 신호가 동시에 나면 추세 방향(의심 상태에서는 전환되는 방향)을 우선한다
		private void RouteEntry()
		{
			int st = RegimeState();

			// spec 4장 재진입 제한: 손실 손절로 끝난 방향은 이번 대회 구간에서 다시 진입하지 않는다.
			// 주력 진입(5.1 상승추세 매수, 5.4 하락추세 매도)은 제한을 받지 않는다
			bool canLong	= !(BlockReentryAfterStop && blockLong);
			bool canShort	= !(BlockReentryAfterStop && blockShort);

			// 지금 추세에서 쓰는 매수·매도 규칙 (spec 5장 표). 상승추세는 매수(5.1)만, 하락추세의 매수는 역추세(5.5)
			Rule longRule, shortRule;
			Candidates(st, out longRule, out shortRule);

			bool longOk		= RuleEnabled(longRule) && (IsMainAt(longRule, st) || canLong) && !SideBlocked(st, true);
			bool shortOk	= RuleEnabled(shortRule) && (IsMainAt(shortRule, st) || canShort) && !SideBlocked(st, false);

			// 진입 신호: 그 규칙의 신호, 또는 추세가 바뀌기 전 같은 방향 규칙에서 이어받은 대기의 반전 신호 (KeepWaitOnTrendChange)
			bool longSig	= longOk && (EntryOf(longRule) || carryLongFire);
			bool shortSig	= shortOk && (EntryOf(shortRule) || carryShortFire);

			// spec 5-2 의심 뒤 가격 확인 역진입 (OrganicSuspectTrigger): 전환 의심 동안 밴드 조건 신호 대신,
			// 진입 봉 종가가 직전 SwingBars봉의 저점을 깨면(하락 전환 의심) 매도, 고점을 넘으면(상승 전환 의심) 매수. 의심 구간 하나에 한 번
			bool suspectTrig = SuspectTriggerOn(st);
			if (suspectTrig)
			{
				longSig		= longOk && st == -1 && !suspectTriggerUsed && Close[0] > signals.SwingHigh5[1];
				shortSig	= shortOk && st == 1 && !suspectTriggerUsed && Close[0] < signals.SwingLow5[1];
			}

			// spec 5-2 반전 신호 큐 (OrganicSignalQueue): 전환 의심 동안, 큐에 유효한 반대쪽 신호가 있으면 새 신호를 기다리지 않고 역진입. 의심 구간 하나에 한 번
			QueuedSignal qLong = null, qShort = null;
			if (QueueEntryOn(st) && !queueEntryUsed)
			{
				if (st == -1 && longOk)	qLong	= LatestQueued(true);
				if (st == 1 && shortOk)	qShort	= LatestQueued(false);
			}

			// 같은 봉에 둘 다 나오면: 하락 전환 의심과 하락추세는 매도 우선, 나머지는 매수 우선
			// 유기 모드: 전환 의심 상태의 진입은 역진입이라 수량을 줄인다. 추세가 확정된 것을 처음 본 봉에서는
			// 밴드 조건과 반전 신호를 기다리지 않고 그 방향으로 들어간다 (추세를 만든 반전 신호는 확정 전에 이미 지나갔다)
			entryQtyPercent	= OrganicMode && (st == 1 || st == -1) ? OrganicSuspectQtyPercent : 100;
			// 역진입 수량을 0으로 두면 전환 의심 구간에서는 진입하지 않는다
			if (entryQtyPercent <= 0)
			{
				longOk			= false;
				shortOk			= false;
				longSig			= false;
				shortSig		= false;
				entryQtyPercent	= 100;
			}
			// 추세 확정 봉: 추세가 바뀐 것을 처음 본 봉. OrganicEnterAtOpen이 켜져 있으면 진입 시간대의 첫 봉도 같이 취급한다
			// (진입 시간이 열리기 전에 이미 확정된 추세에는 그렇지 않으면 들어갈 기회가 없다)
			bool confirmed	= OrganicMode && OrganicEnterOnConfirm
								&& (stateChangedFrom != int.MinValue || (OrganicEnterAtOpen && windowJustOpened));
			bool longConfirm	= confirmed && st == 2 && longOk;
			bool shortConfirm	= confirmed && st == -2 && shortOk;

			viaConfirm	= false;
			viaTrigger	= suspectTrig;
			bool shortFirst = st == 1 || st == -2;
			if (shortFirst)
			{
				if      (shortSig)		Enter(shortRule, st);
				else if (qShort != null)	{ viaQueue = true; queueUsed = qShort; Enter(shortRule, st); }
				else if (shortConfirm)	{ viaConfirm = true; Enter(shortRule, st); }
				else if (longSig)		Enter(longRule, st);
			}
			else
			{
				if      (longSig)		Enter(longRule, st);
				else if (qLong != null)	{ viaQueue = true; queueUsed = qLong; Enter(longRule, st); }
				else if (longConfirm)	{ viaConfirm = true; Enter(longRule, st); }
				else if (shortSig)		Enter(shortRule, st);
			}
			if (suspectTrig && active != Rule.None)
				suspectTriggerUsed = true;
			if (viaQueue && active != Rule.None)
				queueEntryUsed = true;
			viaTrigger	= false;
			viaQueue	= false;
			queueUsed	= null;

			// 위에서 들어가지 않았으면 두 단계 진입의 1단계를 본다
			if (active == Rule.None)
				TryStage1(st, canLong, canShort);
		}

		// ── 두 단계 진입 (OrganicTwoStage, spec 5-2) ──
		// 추세 봉(3분봉)의 판정은 큰 움직임이 시작되고 평균 12~13분 뒤에 나온다. 같은 방식의 추세를 진입 봉(1분봉)에서도 계산해서,
		// 1분봉이 먼저 상승추세(하락추세)가 되면 일부만 들어가고(1단계), 추세 봉이 같은 방향으로 확정되면 나머지를 채운다(2단계).
		// 확정 전에 1분봉 추세가 꺼지면 1단계 수량을 청산한다.
		private bool TwoStageOn()
		{
			return OrganicMode && OrganicTwoStage && RegimeBarMinutes != 1;
		}

		// 진입 봉의 추세 값. 쓸 수 없으면 0 (횡보)
		private int FastState(int barsAgo)
		{
			if (regimeFast == null || CurrentBars[0] < barsAgo + 1)
				return 0;
			double v = regimeFast.Regime[barsAgo];
			return double.IsNaN(v) ? 0 : (int)v;
		}

		// 1단계: 진입 봉의 추세가 이 봉에서 상승추세(하락추세)로 바뀌었고, 추세 봉은 아직 확정 전이며 반대 방향 추세도 그 의심도 아닐 때.
		//  · 매수: 추세 봉이 횡보(0) 또는 상승 전환 의심(−1). 하락 전환 의심(+1)이면 들어가자마자 추세 청산 조건이 되므로 제외
		//  · 매도: 추세 봉이 횡보(0) 또는 하락 전환 의심(+1)
		// 규칙은 5.1(매수)·5.4(매도)의 익절·청산을 그대로 쓰고, 수량은 OrganicStage1Percent. 주력 진입이 아니므로 재진입 제한을 받는다
		private void TryStage1(int st, bool canLong, bool canShort)
		{
			if (!TwoStageOn() || regimeFast == null)
				return;
			int f0 = FastState(0), f1 = FastState(1);
			bool longGo		= f0 == 2 && f1 != 2 && (st == 0 || st == -1) && RuleEnabled(Rule.UpLong) && canLong && !SideBlocked(st, true);
			bool shortGo	= f0 == -2 && f1 != -2 && (st == 0 || st == 1) && RuleEnabled(Rule.DnShort) && canShort && !SideBlocked(st, false);
			if (!longGo && !shortGo)
				return;

			entryQtyPercent = Math.Max(1, Math.Min(100, OrganicStage1Percent));
			viaStage1		= true;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 두 단계 진입 1단계: 1분봉 추세 {2} → {3}, 추세 봉은 {4}",
				Time[0], Instrument.FullName, f1, f0, StateText(st)));
			Enter(longGo ? Rule.UpLong : Rule.DnShort, st);
			if (active != Rule.None)
				stage = 1;	// Enter 안의 상태 리셋 뒤에 정한다
			viaStage1		= false;
			entryQtyPercent = 100;
		}

		// 보유 중 봉마다: 1단계만 들어간 포지션을 관리한다.
		//  · 추세 봉이 같은 방향 추세로 확정(매수 +2, 매도 −2): 진입 시간 안이고 아직 밴드 익절이 하나도 체결되지 않았고
		//    종가가 1차 익절 밴드를 넘지 않았으면 나머지 수량을 추가 진입한다. 어느 경우든 이 거래는 그때부터 주력 진입으로 본다
		//  · 확정 전에 진입 봉의 추세가 그 방향을 벗어나면(매수는 +2·+1이 아니게, 매도는 −2·−1이 아니게) 전량 청산
		private void ManageTwoStage()
		{
			if (stage != 1)
				return;
			int st = RegimeState();
			if (isLongPos ? st == 2 : st == -2)
			{
				stage		= 2;
				tradeMain	= true;
				AddStage2();
				return;
			}
			int f0 = FastState(0);
			if (isLongPos ? f0 <= 0 : f0 >= 0)
				FlattenRemaining(RuleName(active) + " 두 단계 진입 1단계 청산(추세 봉 확정 전에 1분봉 추세가 꺼짐: " + f0 + ")");
		}

		// 2단계 추가 진입. 조각은 1단계와 같은 익절 단계로 나누고 시그널명 뒤에 "b"를 붙인다 (예: UpL1b, UpL2b, UpL3b)
		private void AddStage2()
		{
			int addQty = EntryQuantity - tradeQty;
			string skip = null;
			if (addQty <= 0)					skip = "추가할 수량 없음";
			else if (!InEntryWindow())			skip = "진입 시간이 아님";
			else if (tpCount > 0)				skip = "이미 밴드 익절이 체결됨";
			else if (liveSignals.Count == 0)	skip = "남은 조각 없음";

			string prefix;
			int[] bands, nums;
			int den;
			Plan(active, out prefix, out bands, out nums, out den);
			if (skip == null && bands.Length > 0)
			{
				double first = BandPrice(bands[0]);
				if (isLongPos ? Close[0] >= first : Close[0] <= first)
					skip = string.Format("종가 {0}가 이미 1차 익절 밴드({1} {2:F2})를 넘음", Close[0], BandText(bands[0]), first);
			}
			if (skip != null)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 두 단계 진입 2단계: 추세 봉 확정, 추가 진입 안 함 ({2})",
					Time[0], Instrument.FullName, skip));
				return;
			}

			// 손절 기준을 이 봉으로 다시 잡는다. 체결되면 OnEntryFill이 새 평균 진입가로 손절을 다시 계산한다 (지금 손절보다 불리하게는 옮기지 않음)
			stopAtr		= atr[0];
			stopSwing	= SwingForStop(isLongPos);

			int used = 0;
			string split = "";
			for (int i = 0; i < bands.Length; i++)
			{
				int q = (addQty * nums[i]) / den;
				SubmitChunk(prefix + (i + 1) + "b", q, bands[i]);
				used	+= q;
				split	+= q + "/";
			}
			SubmitChunk(prefix + (bands.Length + 1) + "b", addQty - used, NoBand);
			tradeQty += addQty;

			Out(string.Format("[{0}][{1}][TQ_Strategy] 두 단계 진입 2단계: 추세 봉 확정 → 추가 진입 {2} 수량={3}{4} 손절={5}",
				Time[0], Instrument.FullName, RuleName(active), split, addQty - used, stopPrice));
		}

		// ── 반전 신호 큐 (OrganicSignalQueue, spec 5-2) ──
		// 추세 봉의 전환 의심은 3분봉 지표라 늦고, 그 전에 진입 봉에서 반대쪽 진입 신호가 이미 나와 있는 경우가 많다
		// (10/5~10/9 로그: 의심 27건 중 21건이 10분 안에 반대 신호가 먼저 있었음, 보통 1~8분 전).
		// 그런데 유기 모드의 상승추세에는 매도 규칙이 없어서 그 신호는 버려진다. 그래서 신호를 추세와 상관없이 모아 둔다.
		// 큐에 넣는 신호: TQ_Signals의 진입 신호 중 그 방향의 것 전부(밴드 조건 → MACD 전환·크로스, 횡보의 T2 포함).
		// 매 봉 빼는 신호 (무효 처리):
		//  · 유효 봉 수(OrganicQueueBars)가 지남
		//  · 신호의 고점(매도 신호)을 넘는 고가가 나옴 / 저점(매수 신호)을 깨는 저가가 나옴 — 꼭지가 깨졌으므로. 큐 진입의 손절도 이 고점·저점 바깥이라 같은 기준이다
		//  · 종가가 신호 가격에서 진입 방향으로 ATR × OrganicQueueMaxAtr 넘게 가 버림 — 지금 들어가면 늦어서 손절은 멀고 익절은 가깝다
		private void UpdateSignalQueue()
		{
			if (!OrganicMode || !OrganicSignalQueue)
			{
				shortQueue.Clear();
				longQueue.Clear();
				return;
			}
			double a = atr[0];
			PruneQueue(shortQueue, false, a);
			PruneQueue(longQueue, true, a);

			// 이 봉의 신호를 넣는다. 같은 봉에 여러 규칙의 신호가 나와도 하나만 넣는다 (같은 가격·같은 고점이라)
			string s = signals.EntryDnShort[0] ? "하락추세 매도" : signals.EntryUpSusShort[0] ? "하락 전환 의심 매도"
				: signals.EntryDnSusShort[0] ? "상승 전환 의심 매도" : signals.EntrySideShort[0] ? "횡보 매도" : null;
			if (s != null)
				shortQueue.Add(new QueuedSignal { Bar = CurrentBars[0], Price = Close[0], Extreme = signals.SwingHigh5[0], Name = s });
			string l = signals.EntryUpLong[0] ? "상승추세 매수" : signals.EntryDnSusLong[0] ? "상승 전환 의심 매수"
				: signals.EntryUpSusLong[0] ? "하락 전환 의심 매수" : signals.EntryDnLong[0] ? "하락추세 매수" : signals.EntrySideLong[0] ? "횡보 매수" : null;
			if (l != null)
				longQueue.Add(new QueuedSignal { Bar = CurrentBars[0], Price = Close[0], Extreme = signals.SwingLow5[0], Name = l });
		}

		// 오래되었거나 무효가 된 신호를 뺀다. isLong = 매수 쪽 큐인지. 이 봉에 새로 넣기 전에 부르므로 신호 봉 자신은 보지 않는다
		private void PruneQueue(List<QueuedSignal> queue, bool isLong, double a)
		{
			for (int i = queue.Count - 1; i >= 0; i--)
			{
				QueuedSignal q = queue[i];
				bool expired	= CurrentBars[0] - q.Bar > OrganicQueueBars;
				bool broken		= isLong ? Low[0] < q.Extreme : High[0] > q.Extreme;
				bool ranAway	= OrganicQueueMaxAtr > 0 && !double.IsNaN(a) && a > 0
									&& (isLong ? Close[0] - q.Price : q.Price - Close[0]) > OrganicQueueMaxAtr * a;
				if (expired || broken || ranAway)
					queue.RemoveAt(i);
			}
		}

		// 큐에서 지금 쓸 수 있는 가장 최근 신호. 없으면 null
		private QueuedSignal LatestQueued(bool isLong)
		{
			List<QueuedSignal> queue = isLong ? longQueue : shortQueue;
			return queue.Count > 0 ? queue[queue.Count - 1] : null;
		}

		// 큐 역진입을 쓰는 상태인지 (유기 모드 + OrganicSignalQueue + 전환 의심)
		private bool QueueEntryOn(int st)
		{
			return OrganicMode && OrganicSignalQueue && (st == 1 || st == -1);
		}

		// 의심 뒤 가격 확인 역진입을 쓰는 상태인지 (유기 모드 + OrganicSuspectTrigger + 전환 의심)
		private bool SuspectTriggerOn(int st)
		{
			return OrganicMode && OrganicSuspectTrigger && (st == 1 || st == -1);
		}

		// 유기 모드: 횡보로 판정된 구간이라도 추세 봉의 지표 점수(가격·MACD·RSI·스토캐스틱, −4 ~ +4)가 한쪽으로 쏠려 있으면
		// 그 반대 방향의 횡보 진입은 하지 않는다. 점수가 +2 이상이면 횡보 매도를, −2 이하면 횡보 매수를 막는다 (OrganicSideBlockScore).
		// 2026-10-12 Playback: 횡보 판정 + 점수 +4인 구간에서 횡보 매도가 나가 22초 만에 손절됐다
		private bool SideBlocked(int st, bool isLong)
		{
			if (!OrganicMode || st != 0 || OrganicSideBlockScore <= 0)
				return false;
			int sc = regime.Score;
			return isLong ? sc <= -OrganicSideBlockScore : sc >= OrganicSideBlockScore;
		}

		// 규칙의 진입 신호 (TQ_Signals)
		private bool EntryOf(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return signals.EntryUpLong[0];
				case Rule.UpSusLong:	return signals.EntryUpSusLong[0];
				case Rule.UpSusShort:	return signals.EntryUpSusShort[0];
				case Rule.DnShort:		return signals.EntryDnShort[0];
				case Rule.DnLong:		return signals.EntryDnLong[0];
				case Rule.DnSusLong:	return signals.EntryDnSusLong[0];
				case Rule.DnSusShort:	return signals.EntryDnSusShort[0];
				case Rule.SideLong:		return signals.EntrySideLong[0];
				case Rule.SideShort:	return signals.EntrySideShort[0];
				default:				return false;
			}
		}

		// KeepWaitOnTrendChange: 추세가 바뀌면 진입 규칙도 바뀌는데, 바뀌기 전 규칙에서 밴드 조건이 충족돼 반전 신호를 기다리던 중이었으면
		// 그 대기를 새 추세의 같은 방향 규칙으로 이어 간다. 남은 봉 수 안에 반전 신호가 나오면 새 추세의 규칙으로 진입한다.
		// 진입 봉마다 한 번 부른다 (보유 중에도 센다. 진입은 미보유일 때만 한다).
		// 하락추세 매수(5.5)로는 이어 가지 않는다. 역추세 진입은 RSI·−3배 조건이 핵심이라 얕은 밴드의 대기로 대신하지 않는다
		private void TrackWaitCarry()
		{
			carryLongFire	= false;
			carryShortFire	= false;

			int st = RegimeState();
			Rule longRule, shortRule;
			Candidates(st, out longRule, out shortRule);

			// 이 봉에서 추세가 바뀌었으면 바뀌기 전 상태, 아니면 int.MinValue (유기 모드의 추세 확정 진입용)
			stateChangedFrom = prevState != int.MinValue && st != prevState ? prevState : int.MinValue;
			// 가격 확인 역진입(OrganicSuspectTrigger)은 전환 의심 구간 하나에 한 번. 추세 값이 바뀌면 새 구간으로 본다 (보유 중에도 센다)
			if (stateChangedFrom != int.MinValue)
			{
				suspectTriggerUsed	= false;
				queueEntryUsed		= false;
			}

			if (KeepWaitOnTrendChange)
			{
				bool revUp = signals.MacdUp[0] || signals.Golden[0];
				bool revDn = signals.MacdDown[0] || signals.Dead[0];

				if (prevState != int.MinValue && st != prevState)
				{
					// 추세가 바뀐 봉: 바뀌기 전 규칙의 대기에서 남은 봉 수를 이어받는다 (이미 이어받은 것이 더 길면 그것을 유지)
					if (prevLongWait >= 0)	carryLongLeft	= Math.Max(carryLongLeft, prevLongBars - prevLongWait);
					if (prevShortWait >= 0)	carryShortLeft	= Math.Max(carryShortLeft, prevShortBars - prevShortWait);
				}

				// 새 추세에 그 방향 규칙이 없으면(상승추세의 매도) 버리고, 하락추세 매수로는 이어 가지 않는다
				if (longRule == Rule.None || longRule == Rule.DnLong)	carryLongLeft = 0;
				if (shortRule == Rule.None)								carryShortLeft = 0;

				// 로그는 실제로 이어 가는 것만 남긴다 (위에서 버린 것은 찍지 않는다)
				if (prevState != int.MinValue && st != prevState && (carryLongLeft > 0 || carryShortLeft > 0))
					Out(string.Format("[{0}][{1}][TQ_Strategy] 추세 전환 {2} → {3}: 반전 신호 대기를 이어받음 (매수 {4}봉, 매도 {5}봉 남음)",
						Time[0], Instrument.FullName, prevState, st, carryLongLeft, carryShortLeft));

				// 이어받은 대기의 판정: 이 봉에 반전 신호가 있으면 진입 신호, 없으면 남은 봉 수를 하나 줄인다
				if (carryLongLeft > 0)
				{
					if (revUp)	{ carryLongFire = true; carryLongLeft = 0; }
					else		carryLongLeft--;
				}
				if (carryShortLeft > 0)
				{
					if (revDn)	{ carryShortFire = true; carryShortLeft = 0; }
					else		carryShortLeft--;
				}
			}
			else
			{
				carryLongLeft	= 0;
				carryShortLeft	= 0;
			}

			// 다음 봉에서 추세가 바뀌었을 때 쓸 값: 지금 추세의 규칙이 대기 중인지와 그 규칙의 대기 봉 수
			prevState		= st;
			prevLongWait	= longRule == Rule.None ? -1 : WaitOf(longRule);
			prevLongBars	= WaitBarsOf(longRule);
			prevShortWait	= shortRule == Rule.None ? -1 : WaitOf(shortRule);
			prevShortBars	= WaitBarsOf(shortRule);
		}

		// 규칙의 분할 계획대로 조각을 나눠 진입한다 (spec 5·6장). 단계 수량은 내림하고 마지막 조각이 나머지를 갖는다
		private void Enter(Rule r, int state)
		{
			string prefix;
			int[] bands, nums;
			int den;
			Plan(r, out prefix, out bands, out nums, out den);

			// SkipEntryPastTarget: 신호 봉 종가가 이미 1차 익절 밴드에 닿았거나 넘었으면 진입하지 않는다.
			// 반전 신호가 늦게 확인돼 가격이 이미 가 버린 경우로, 들어가면 첫 조각들이 진입가 그대로 익절돼 나가고 나머지만 남는다
			if ((SkipEntryPastTarget || OrganicMode) && !UseTestEntry && bands.Length > 0)
			{
				double first = BandPrice(bands[0]);
				if (IsLongRule(r) ? Close[0] >= first : Close[0] <= first)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 보류 {2}: 종가 {3}가 이미 1차 익절 밴드({4} {5:F2})를 넘음",
						Time[0], Instrument.FullName, RuleName(r), Close[0], BandText(bands[0]), first));
					return;
				}
			}

			BeginTrade(IsLongRule(r), state, r);

			int used = 0;
			string split = "";
			for (int i = 0; i < bands.Length; i++)
			{
				int q = Split(nums[i], den);
				SubmitChunk(prefix + (i + 1), q, bands[i]);
				used	+= q;
				split	+= q + "/";
			}
			SubmitChunk(prefix + (bands.Length + 1), tradeQty - used, NoBand);	// 마지막 조각

			string how = viaStage1 ? " (두 단계 진입 1단계 " + entryQtyPercent + "%)"
				: (viaConfirm ? " (추세 확정 봉)" : "") + (viaTrigger && !viaQueue ? " (의심 뒤 가격 확인)" : "")
					+ (viaQueue && queueUsed != null ? string.Format(" (큐: {0}분 전 {1} 신호, 가격 {2}, 기준 {3} {4})",
						CurrentBars[0] - queueUsed.Bar, queueUsed.Name, queueUsed.Price, IsLongRule(r) ? "저점" : "고점", queueUsed.Extreme) : "")
					+ (entryQtyPercent != 100 ? " (역진입 " + entryQtyPercent + "%)" : "");
			Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 {2}{3} 수량={4}{5} (추세={6}) 손절={7}",
				Time[0], Instrument.FullName, RuleName(r), how, split, tradeQty - used, state, stopPrice));
		}

		// 진입 공통: 상태 리셋 + 방향·추세·규칙 기록 + 신호 봉 종가 기준 손절가 계산 (spec 4장)
		private void BeginTrade(bool isLong, int regimeAtEntry, Rule act)
		{
			ResetTradeState();
			isLongPos	= isLong;
			entryRegime	= regimeAtEntry;
			active		= act;
			tradeMain	= IsMainAt(act, regimeAtEntry);
			// CounterQtyPercent: 역추세 진입(5.5 하락추세 매수)은 수량을 줄여서 들어갈 수 있다. 100이면 그대로
			tradeQty	= act == Rule.DnLong ? Math.Max(1, EntryQuantity * CounterQtyPercent / 100) : EntryQuantity;
			// 유기 모드의 전환 의심 진입(역진입)은 수량을 줄인다. 그 밖에는 100
			if (entryQtyPercent != 100)
				tradeQty = Math.Max(1, tradeQty * entryQtyPercent / 100);
			TradeBegin();
			stopAtr		= atr[0];
			// 전고점·전저점은 신호 봉 기준 (spec 4장). 신호 봉 = 현재 마감된 봉([0]). 유기 모드의 구조 손절은 추세 봉까지 본다.
			// 큐 역진입은 그 신호의 전고점(매도)·전저점(매수)을 기준으로 한다 (CalcStop의 구조 손절 거리 제한을 받음)
			tradeQueueStop	= viaQueue && queueUsed != null;
			stopSwing		= tradeQueueStop ? queueUsed.Extreme : SwingForStop(isLong);
			initialStop	= CalcStop(Close[0]);	// 신호 봉 종가를 진입가로 보고 계산 (spec 4장)
			stopPrice	= initialStop;
			MlBegin(act);	// 학습 데이터: 신호 봉의 특징값 (매매 판단과 무관)
		}

		// spec 4·6장: 시그널명별 분할 진입. 진입 주문 제출 전에 손절을 먼저 걸어 체결 즉시 손절이 서도록 한다
		private void SubmitChunk(string sig, int qty, int band)
		{
			if (qty <= 0)
				return;
			chunkQty[sig]	= qty;
			chunkBand[sig]	= band;
			chunkOrder.Add(sig);
			liveSignals.Add(sig);
			// SetStopLoss는 진입 전에 호출해야 초기 손절가가 보장된다 (NT8 공식 문서)
			// 걸어두는 가격은 신호 봉 종가에서 최소 1틱 띄운다. 체결되면 OnExecutionUpdate에서 체결가 기준으로 다시 건다
			SetStopLoss(sig, CalculationMode.Price, OrderSafeStop(stopPrice, Close[0]), false);
			SetTarget(sig);	// 밴드 익절 지정가도 진입 전에 건다 (IntrabarTakeProfit)

			if (isLongPos)	EnterLong(qty, sig);
			else			EnterShort(qty, sig);
		}

		// 조각의 익절 밴드 값. 밴드 익절이 없는 조각(마지막 조각)은 NaN
		private double TargetPrice(string sig)
		{
			int band;
			if (!chunkBand.TryGetValue(sig, out band) || band == NoBand)
				return double.NaN;
			return BandPrice(band);
		}

		// IntrabarTakeProfit: 밴드 익절을 봉 마감을 기다리지 않고 지정가 주문으로 걸어 둔다 (spec 1장 4).
		// 가격은 방금 마감된 봉의 밴드 값이고, 봉이 마감될 때마다 새 밴드 값으로 옮긴다.
		// 손절(SetStopLoss)과 같이 쓸 수 있는 것은 SetProfitTarget뿐이다 (ExitLongLimit을 쓰면 손절이 무시된다, NT8 Managed 규칙)
		private void SetTarget(string sig)
		{
			if (!IntrabarTakeProfit || UseTestEntry)
				return;
			double band = TargetPrice(sig);
			if (double.IsNaN(band))
				return;
			SetProfitTarget(sig, CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(band));
		}

		// 보유 중 봉마다: 남아 있는 조각의 익절 지정가를 새 밴드 값으로 옮긴다.
		// 가격을 옮기다 바로 체결되면 콜백이 liveSignals를 바꾸므로 복사본으로 돈다
		private void UpdateTargets()
		{
			if (!IntrabarTakeProfit || UseTestEntry)
				return;
			foreach (string sig in liveSignals.ToArray())
				SetTarget(sig);
		}

		// 익절 지정가(SetProfitTarget) 체결
		private void OnTargetFill(string sig, double price, DateTime time)
		{
			// 부분 체결이 여러 번 올 수 있다. 상태 전환은 조각당 한 번만 한다
			if (sig == null || !liveSignals.Remove(sig))
				return;

			Out(string.Format("[{0}][{1}][TQ_Strategy] 밴드 익절(지정가) 체결 시그널={2} 가격={3}",
				time, Instrument.FullName, sig, price));
			OnBandFill(sig, price, time);
		}

		// 밴드 익절 한 단계가 체결됐을 때의 후속 처리 (지정가·봉 마감 시장가 공통).
		// 1번째 체결: 손절을 평균 진입가로 (spec 4장 본절). 2번째 체결: 손절을 1차 익절가로 (TrailStopAtTp2)
		private void OnBandFill(string sig, double price, DateTime time)
		{
			filledBand.Add(sig);
			// 단계는 밴드 기준으로 센다. 같은 밴드에 조각이 여럿이면(두 단계 진입의 1·2단계 조각) 처음 체결된 조각에서만 단계가 넘어간다
			int level;
			if (chunkBand.TryGetValue(sig, out level) && !filledLevels.Add(level))
				return;
			tpCount = filledLevels.Count;

			if (tpCount == 1)
			{
				firstTpPrice = price;
				OnFirstTakeProfitFill(price, time);
			}
			else if (tpCount == 2 && (TrailStopAtTp2 || OrganicMode) && !double.IsNaN(firstTpPrice))
			{
				// 유기 모드: 1차 때 평단 이동을 보류했을 수 있으므로 평단부터 옮기고, 1차 익절가가 더 유리하면 거기로 한 번 더 옮긴다
				// (MoveStopTo는 지금 손절보다 유리한 쪽으로만 옮긴다)
				if (OrganicMode && entryFillQty > 0)
					MoveStopTo(entryFillSum / entryFillQty, "본절", time);
				MoveStopTo(firstTpPrice, "1차 익절가", time);
			}
		}

		// spec 6장: 진입 수량 × (num/den)을 내림. 양수 정수 나눗셈이 곧 내림이다 (두 단계 진입의 2단계는 AddStage2에서 따로 나눈다)
		private int Split(int num, int den)
		{
			return (tradeQty * num) / den;
		}
		#endregion

		#region 손절·본절 (spec 4장)
		// spec 4장 손절가: 전저점(롱)/전고점(숏)과 진입가 ±StopPercent% 중 한쪽.
		// StopWider가 켜져 있으면 진입가에서 먼 쪽(롱은 둘 중 낮은 값, 숏은 높은 값), 꺼져 있으면 가까운 쪽.
		// 전고점·전저점이 없으면(NaN) %만 쓴다(방어).
		private double CalcStop(double entryPrice)
		{
			// 유기 모드: 손절을 익절 밴드와 같은 단위(ATR)로 잡는다. 진입가에서 신호 봉 ATR × OrganicStopAtr
			if (OrganicMode && !double.IsNaN(stopAtr) && stopAtr > 0)
			{
				double dist = OrganicStopAtr * stopAtr;
				// 구조 손절 (OrganicStopSwing, spec 5-2): 직전 저점(매수)·고점(매도) 한 틱 바깥. 거리는 ATR × 최소~최대 배수 안으로 자른다
				// 큐 역진입(tradeQueueStop)은 구조 손절을 꺼 두어도 큐 신호의 고점·저점으로 같은 계산을 한다
				if ((OrganicStopSwing || tradeQueueStop) && !double.IsNaN(stopSwing))
				{
					double swingDist	= (isLongPos ? entryPrice - stopSwing : stopSwing - entryPrice) + TickSize;
					double lo			= OrganicStopMinAtr * stopAtr;
					double hi			= Math.Max(lo, OrganicStopMaxAtr * stopAtr);
					dist				= Math.Max(lo, Math.Min(hi, swingDist));
				}
				return Instrument.MasterInstrument.RoundToTickSize(isLongPos ? entryPrice - dist : entryPrice + dist);
			}

			double pct = isLongPos
				? entryPrice * (1 - StopPercent / 100.0)
				: entryPrice * (1 + StopPercent / 100.0);
			if (double.IsNaN(stopSwing))	// 지표 미완성·데이터 부족 방어
				return FloorStop(entryPrice, pct);
			if (!UseSwingStop)				// 전고점·전저점을 쓰지 않고 %만 쓴다
				return FloorStop(entryPrice, pct);

			double lower	= Math.Min(stopSwing, pct);
			double higher	= Math.Max(stopSwing, pct);
			// 롱: 먼 쪽 = 낮은 값. 숏: 먼 쪽 = 높은 값
			double stop		= isLongPos ? (StopWider ? lower : higher) : (StopWider ? higher : lower);
			return FloorStop(entryPrice, stop);
		}

		// 손절의 기준이 되는 직전 저점(매수)·고점(매도). 기본은 신호 봉 포함 최근 SwingBars봉(진입 봉)의 전저점·전고점 (spec 4장).
		// 유기 모드의 구조 손절(OrganicStopSwing)은 거기에 추세 봉 최근 OrganicStopSwingBars개의 최저가·최고가까지 넣어 더 바깥쪽을 쓴다
		private double SwingForStop(bool isLong)
		{
			double s = isLong ? signals.SwingLow5[0] : signals.SwingHigh5[0];
			if (!OrganicMode || !OrganicStopSwing)
				return s;
			int n = Math.Min(OrganicStopSwingBars, CurrentBars[regimeBip] + 1);
			for (int i = 0; i < n; i++)
			{
				double v = isLong ? Lows[regimeBip][i] : Highs[regimeBip][i];
				if (double.IsNaN(s) || (isLong ? v < s : v > s))
					s = v;
			}
			return s;
		}

		// MinStopAtr: 손절 거리가 신호 봉 ATR × MinStopAtr보다 짧으면 그 거리까지 벌린다. 0이면 그대로 둔다.
		// 신호 봉이 최근 5봉의 저점(롱)·고점(숏) 근처에서 마감하면 손절 거리가 0에 가까워지는 것을 막는다
		private double FloorStop(double entryPrice, double stop)
		{
			// 전저점이 진입가 위(롱)·전고점이 진입가 아래(숏)에 놓이는 경우도 최소 거리로 바로잡는다
			bool wrongSide = isLongPos ? stop >= entryPrice : stop <= entryPrice;
			if (MinStopAtr > 0 && !double.IsNaN(stopAtr) && stopAtr > 0)
			{
				double minDist = MinStopAtr * stopAtr;
				if (wrongSide || Math.Abs(entryPrice - stop) < minDist)
					stop = isLongPos ? entryPrice - minDist : entryPrice + minDist;
			}
			return Instrument.MasterInstrument.RoundToTickSize(stop);
		}

		// 아직 보유 중인 모든 진입 시그널의 손절가를 price로 (재)설정
		private void SetStopForLive(double price)
		{
			foreach (string sig in liveSignals)
				SetStopLoss(sig, CalculationMode.Price, price, false);
		}

		// 주문으로 걸어둘 스탑 가격: 기준가(신호 봉 종가)와 같거나 넘어서면 주문이 거부될 수 있어 최소 1틱 띄운다.
		// 손절 규칙(spec 4장)의 값은 CalcStop이 정하고, 이 함수는 체결 전 임시 주문 가격에만 쓴다
		private double OrderSafeStop(double stop, double refPrice)
		{
			return isLongPos ? Math.Min(stop, refPrice - TickSize) : Math.Max(stop, refPrice + TickSize);
		}

		// 주문 오류 처리 (HandleOrderErrors). 켜져 있으면 NinjaTrader가 전략을 끄지 않으므로 여기서 포지션을 지킨다.
		//  · 손절 가격 변경만 거부됨: 주문은 이전 가격에 살아 있다 → 내부 상태를 실제 가격으로 되돌리고, 옮기려던 가격은 나중에 다시 시도
		//  · 손절 주문 자체가 거부됨: 손절 없는 포지션이 된다 → 즉시 전량 청산
		//  · 진입 주문 거부: 그 조각은 없는 것으로 한다
		//  · 익절 지정가·시장가 청산 오류: 기록만 한다. 전량 청산이 안 됐으면 다음 봉 마감에서 다시 낸다 (OnBarUpdate)
		protected override void OnOrderUpdate(Order order, double limitPrice, double orderStopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (order == null)
				return;
			bool rejected = orderState == OrderState.Rejected;
			if (!rejected && error == ErrorCode.NoError)
				return;

			string name = order.Name;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 주문 오류 이름={2} 상태={3} 오류={4} 내용={5} 스탑가={6} 수량={7}",
				time, Instrument.FullName, name, orderState, error, comment, order.StopPrice, quantity));

			if (!HandleOrderErrors)
				return;	// NinjaTrader 기본 동작에 맡긴다 (포지션을 닫고 전략 종료)

			if (name == "Stop loss")
			{
				if (rejected)
				{
					if (Position.MarketPosition != MarketPosition.Flat)
					{
						gapFlattened = true;
						Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 주문 거부 → 손절 없이 둘 수 없어 즉시 전량 청산",
							time, Instrument.FullName));
						FlattenRemaining("손절 주문 거부");
					}
				}
				else if (error == ErrorCode.UnableToChangeOrder)
				{
					stopPrice		= order.StopPrice;	// 실제로 걸려 있는 가격
					breakevenDone	= AtOrBeyondAverage(stopPrice);
					if (!breakevenDone)
						stopNote = "";
					if (!double.IsNaN(lastStopTarget))
						deferredStop = lastStopTarget;	// 봉 마감마다 RetryDeferredStop이 다시 시도한다
					Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 가격 변경 거부 → 기존 손절 {2} 유지",
						time, Instrument.FullName, stopPrice));
				}
			}
			else if (rejected && chunkQty.ContainsKey(name))
			{
				liveSignals.Remove(name);
				Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 주문 거부 → 시그널 {2} 제외",
					time, Instrument.FullName, name));
			}
		}

		// 체결 시점 처리 (spec 4장): 진입 체결 → 갭 판정과 손절 재계산, 밴드 익절 체결 → 손절 이동
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;

			string name = execution.Order.Name;

			MarkExecution(name, price, quantity, marketPosition, time);

			// 청산 체결은 모두 로그에 남긴다 (익절·손절·전량 청산이 얼마에 체결됐는지 나중에 확인하기 위함)
			if (!chunkQty.ContainsKey(name))
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 청산 체결 {2} 가격={3} 수량={4}",
					time, Instrument.FullName, name, price, quantity));
				TradeExitFill(name, price, quantity, time);
			}

			if (chunkQty.ContainsKey(name))
				OnEntryFill(price, quantity, time);
			else if (name == "Profit target")					// SetProfitTarget이 내는 주문 이름
				OnTargetFill(execution.Order.FromEntrySignal, price, time);
			else if (pendingBandExit.Remove(name))				// 봉 마감 시장가로 낸 밴드 익절. 주문 이름은 "x" + 진입 시그널명
				OnBandFill(name.Substring(1), price, time);
			else if (name == "Stop loss")						// SetStopLoss가 내는 주문 이름
				OnStopLossFill(price, time);

			UpdateStatusPanel();
		}

		// spec 4장 재진입 제한: 손절이 손실로 체결되면 이번 대회 구간에서 같은 방향 진입을 막는다.
		// 본절 이후의 손절(손실 없음)은 막지 않는다
		private void OnStopLossFill(double exitPrice, DateTime time)
		{
			if (entryFillQty <= 0)
				return;

			// 본절 이상으로 옮긴 뒤의 손절은 체결가가 미끄러져 평균가보다 조금 불리해도 손실 손절로 보지 않는다
			if (breakevenDone)
				return;

			double avg	= entryFillSum / entryFillQty;
			bool loss	= isLongPos ? exitPrice < avg : exitPrice > avg;
			if (!loss)
				return;

			// 주력 진입(5.1, 5.4)의 손절은 재진입 제한을 걸지 않는다. 추세 방향 진입은 여러 번 시도하는 것이 전략의 취지다.
			// 유기 모드의 역진입·두 단계 진입 1단계는 5.1·5.4 규칙을 빌려 쓰지만 주력이 아니다 (IsMainAt)
			if (tradeMain)
				return;

			bool already = isLongPos ? blockLong : blockShort;
			if (isLongPos)	blockLong = true;
			else			blockShort = true;

			if (!already && BlockReentryAfterStop)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 손실 손절 → 이번 대회 구간 {2} 재진입 금지 (주력 진입 제외)",
					time, Instrument.FullName, isLongPos ? "매수" : "매도"));
		}

		// spec 4장 이익 보호 본절 (BreakevenAtR, 기본 0 = 사용 안 함): 보유 중 최대 유리폭이 처음 손절 거리(1R)의
		// BreakevenAtR배에 닿으면 1차 익절 전이라도 손절을 평균 진입가로 옮긴다. 봉 마감 시 그 봉의 고가(롱)·저가(숏)로 판단한다
		private void CheckBreakevenAtR()
		{
			// 주력 진입(5.1, 5.4)에는 적용하지 않는다. 추세를 길게 끌고 가는 거래라 진입가를 되짚는 움직임을 버텨야 한다
			if (tradeMain)
				return;

			if (BreakevenAtR <= 0 || breakevenDone || liveSignals.Count == 0 || entryFillQty <= 0
				|| double.IsNaN(initialRisk) || initialRisk <= 0)
				return;

			double avg			= entryFillSum / entryFillQty;
			double favorable	= isLongPos ? High[0] - avg : avg - Low[0];
			if (favorable >= BreakevenAtR * initialRisk)
				reachedR = true;
			if (reachedR)
				MoveStopTo(avg, "본절(이익 보호)", Time[0]);
		}

		// spec 4장: 체결되면 실제 체결가(평균)로 손절을 다시 계산한다.
		// 갭으로 체결가가 이미 걸어둔 손절가(신호 봉 종가 기준)를 넘어섰으면 즉시 전량 청산한다
		private void OnEntryFill(double fillPrice, int fillQty, DateTime time)
		{
			entryFillSum += fillPrice * fillQty;
			entryFillQty += fillQty;

			bool breached = !double.IsNaN(initialStop)
				&& (isLongPos ? fillPrice <= initialStop : fillPrice >= initialStop);
			if (breached || gapFlattened)
			{
				gapFlattened = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 갭 손절 초과 → 즉시 전량 청산 체결가={2} 손절={3}",
					time, Instrument.FullName, fillPrice, initialStop));
				FlattenRemaining("spec 4장 갭 손절 초과");
				return;
			}

			double avg	= entryFillSum / entryFillQty;

			// 두 단계 진입의 2단계 체결: 평균 진입가가 바뀌었으므로 손절을 다시 계산하되, 지금 손절보다 불리하게는 옮기지 않는다
			if (stage == 2)
			{
				double s2 = CalcStop(avg);
				if (!double.IsNaN(stopPrice) && (isLongPos ? s2 < stopPrice : s2 > stopPrice))
					s2 = stopPrice;
				if (isLongPos ? GetCurrentBid(0) <= s2 : GetCurrentAsk(0) >= s2)
				{
					gapFlattened = true;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 2단계 체결 직후 현재가가 손절가를 넘어섬 → 즉시 전량 청산 손절={2}",
						time, Instrument.FullName, s2));
					FlattenRemaining("spec 4장 손절가 초과");
					return;
				}
				stopPrice		= s2;
				initialRisk		= Math.Abs(avg - s2);
				breakevenDone	= AtOrBeyondAverage(s2);
				SetStopForLive(s2);
				Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 재계산(2단계 체결) 평균 체결가={2} 손절={3}",
					time, Instrument.FullName, avg, s2));
				return;
			}

			// 손절이 이미 본절 이상으로 옮겨졌으면 체결가 기준으로 되돌리지 않는다
			if (breakevenDone)
				return;

			double s	= CalcStop(avg);

			// 다시 계산한 손절가에 현재가가 이미 닿았으면 스탑을 그쪽으로 옮길 수 없다(주문 거부).
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
			Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 재계산(체결가 기준) 평균 체결가={2} 손절={3}",
				time, Instrument.FullName, avg, s));
		}

		// spec 4장 본절: 첫 밴드 익절이 체결되면 남은 수량의 손절을 평균 진입가로 옮긴다.
		// 체결 시점 가격이 평균 진입가보다 유리하지 않으면(손실 중이거나 같으면) 원래 손절가를 유지한다
		private void OnFirstTakeProfitFill(double exitPrice, DateTime time)
		{
			double risk		= initialRisk;	// 처음 손절 거리 (유기 모드의 본절 판단용)
			initialRisk		= double.NaN;
			reachedR		= false;
			if (breakevenDone || liveSignals.Count == 0)
				return;

			double avg	= entryFillQty > 0 ? entryFillSum / entryFillQty : Position.AveragePrice;
			double be	= Instrument.MasterInstrument.RoundToTickSize(avg);

			// 유기 모드: 1차 익절의 이익이 손절 거리의 OrganicBeMinR배에 못 미치면 평단으로 옮기지 않는다.
			// 익절이 진입가 바로 옆인데 평단으로 옮기면 잡음에 바로 청산된다. 2차 익절 때 다시 본다 (OnBandFill)
			if (OrganicMode && OrganicBeMinR > 0 && !double.IsNaN(risk) && risk > 0)
			{
				double gain = isLongPos ? exitPrice - avg : avg - exitPrice;
				if (gain < OrganicBeMinR * risk)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 보류(1차 익절 폭 {2:F2}가 손절 거리 {3:F2}의 {4}배 미만) 손절 유지={5}",
						time, Instrument.FullName, gain, risk, OrganicBeMinR, stopPrice));
					return;
				}
			}

			bool favorable = isLongPos ? exitPrice > be : exitPrice < be;
			if (!favorable)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 보류(손실 중) 평균가={2} 익절 체결가={3} 손절 유지={4}",
					time, Instrument.FullName, be, exitPrice, stopPrice));
				return;
			}

			MoveStopTo(be, "본절", time);
		}

		// 손절을 target으로 옮긴다 (본절, 1차 익절가). 지금 손절보다 유리한 쪽으로만 옮긴다.
		// 스탑을 현재가 바로 옆이나 반대편으로 옮기면 주문이 거부되므로, 현재 호가가 충분히 떨어져 있지 않으면
		// 지금은 옮기지 않고 봉 마감마다 다시 본다 (RetryDeferredStop)
		private void MoveStopTo(double target, string label, DateTime time)
		{
			if (liveSignals.Count == 0)
				return;
			target = Instrument.MasterInstrument.RoundToTickSize(target);

			bool improves = double.IsNaN(stopPrice) || (isLongPos ? target > stopPrice : target < stopPrice);
			if (!improves)
				return;

			lastStopTarget	= target;
			lastStopLabel	= label;

			if (!SafeToMoveStop(target))
			{
				if (deferredStop != target)
					Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 이동 대기(현재가가 너무 가까움) 목표={2} ({3}) 손절 유지={4}",
						time, Instrument.FullName, target, label, stopPrice));
				deferredStop = target;
				return;
			}

			ApplyStop(target, label, time);
		}

		private void ApplyStop(double target, string label, DateTime time)
		{
			stopPrice		= target;
			deferredStop	= double.NaN;
			breakevenDone	= AtOrBeyondAverage(target);
			stopNote		= label;
			SetStopForLive(target);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 이동 → {2} ({3})",
				time, Instrument.FullName, target, label));
		}

		// 손절가가 평균 진입가와 같거나 그보다 유리한 쪽인지 (그 손절이 체결돼도 손실이 아님)
		private bool AtOrBeyondAverage(double stop)
		{
			if (entryFillQty <= 0 || double.IsNaN(stop))
				return false;
			double be = Instrument.MasterInstrument.RoundToTickSize(entryFillSum / entryFillQty);
			return isLongPos ? stop >= be : stop <= be;
		}

		// 스탑을 newStop으로 옮겨도 거부되지 않을 만큼 현재 호가가 떨어져 있는지.
		// 롱은 매수 호가가 newStop보다 BreakevenBufferTicks틱 이상 위, 숏은 매도 호가가 그만큼 아래여야 한다.
		// 과거 봉에서는 GetCurrentBid/Ask가 마지막 종가를 준다
		private bool SafeToMoveStop(double newStop)
		{
			double buffer = Math.Max(1, BreakevenBufferTicks) * TickSize;
			return isLongPos ? GetCurrentBid(0) - newStop >= buffer : newStop - GetCurrentAsk(0) >= buffer;
		}

		// 대기 중인 손절 이동을 봉 마감마다 다시 시도한다
		private void RetryDeferredStop()
		{
			if (double.IsNaN(deferredStop) || liveSignals.Count == 0 || entryFillQty <= 0)
				return;

			bool improves = double.IsNaN(stopPrice) || (isLongPos ? deferredStop > stopPrice : deferredStop < stopPrice);
			if (!improves)
			{
				deferredStop = double.NaN;
				return;
			}
			if (!SafeToMoveStop(deferredStop))
				return;
			ApplyStop(deferredStop, lastStopLabel, Time[0]);
		}
		#endregion

		#region 익절·청산 (spec 5장)
		// 보유 중 봉마다: 추세 전환 정리 → 전량 청산 조건 → (봉 마감 방식일 때) 밴드 익절 → 마지막 조각
		private void ManageExits()
		{
			int st = RegimeState();

			// spec 1장 원칙 3: 추세가 포지션과 맞지 않게 바뀌면 전량 정리한다 (TrendExitReason).
			// 추세 값은 가장 최근에 마감된 추세 봉 기준이라, 추세 봉 마감 뒤 첫 진입 봉 마감에서 실행된다
			string trendExit = TrendExitReason(active, st);
			if (trendExit != null)
			{
				FlattenRemaining(RuleName(active) + " 추세 청산(" + trendExit + ")");
				return;
			}

			// 옵션 AdoptTrendExits: 의심 상태에서 들어간 포지션이 같은 방향 추세로 확정되면 그 추세 규칙의 청산으로 갈아탄다
			if (AdoptTrendExits && !adopted
				&& (active == Rule.UpSusLong || active == Rule.UpSusShort || active == Rule.DnSusLong || active == Rule.DnSusShort))
			{
				if (isLongPos && st == 2)			AdoptTrendRule(Rule.UpLong);
				else if (!isLongPos && st == -2)	AdoptTrendRule(Rule.DnShort);
			}

			// 전량 청산 조건: 단계와 상관없이 본다 (spec 5장).
			// 유기 모드의 OrganicHoldWhileTrend: 추세가 포지션 방향 그대로면 진입 봉의 전량 청산 조건을 무시한다 (HeldByTrend)
			if (FullExitSignal(active) && !HeldByTrend(st))
			{
				FlattenRemaining(RuleName(active) + " 전량 청산 조건(" + FullExitText(active) + ")");
				return;
			}

			// IntrabarTakeProfit이 꺼져 있으면 밴드 익절을 봉 마감 종가로 판단해 시장가로 낸다
			if (!IntrabarTakeProfit)
			{
				foreach (string sig in liveSignals.ToArray())
				{
					double band = TargetPrice(sig);
					if (double.IsNaN(band))
						continue;
					if (isLongPos ? Close[0] >= band : Close[0] <= band)
						ExitChunk(sig, "밴드 익절 " + BandText(chunkBand[sig]), true);
				}
			}

			// 마지막 조각: 종가와 이동평균 비교 (spec 5장). 기본은 앞 단계 밴드 익절이 모두 체결된 뒤부터 본다 (LastChunkEarly로 처음부터)
			int side = LastChunkSide(active);
			List<string> lastChunks = LastChunks();
			if (side != 0 && lastChunks.Count > 0 && (LastChunkEarly || BandStagesDone()))
			{
				double ma = signals.Sma[0];
				if (side > 0 ? Close[0] > ma : Close[0] < ma)
					foreach (string sig in lastChunks)
						ExitChunk(sig, string.Format("마지막 조각(종가 {0} {1}선)", side > 0 ? ">" : "<", ExitSmaPeriod), false);
			}
		}

		// 아직 보유 중인 마지막 조각(밴드 익절이 없는 조각). 보통 1개, 두 단계 진입으로 추가 진입했으면 2개
		private List<string> LastChunks()
		{
			List<string> list = new List<string>();
			foreach (string sig in liveSignals)
				if (chunkBand.ContainsKey(sig) && chunkBand[sig] == NoBand)
					list.Add(sig);
			return list;
		}

		// 유기 모드의 OrganicHoldWhileTrend: 추세 판정이 포지션 방향 쪽인지. 참이면 진입 봉(1분봉)의 전량 청산 조건을 무시한다.
		// 추세 봉의 판정이 살아 있는데 1분봉의 작은 눌림에 통째로 청산되는 것을 막는다.
		// 2026-10-12 Playback: 상승추세 매수의 청산 37번 중 30번이 이 1분봉 조건이었고(추세가 바뀌어 청산된 것은 7번), 평균 5분 만에 나갔다.
		//  · 매수: 상승추세(+2), 또는 상승 전환 의심(−1, 역진입 매수가 기다리는 상태). 매도: 하락추세(−2), 또는 하락 전환 의심(+1)
		//  · 2026-10-14: 전환 의심의 역진입도 넣었다. 그 전에는 역진입 매수만 5.1의 1분봉 전량 청산 조건을 받고 역진입 매도(5.4 규칙)는 그런 조건이 없어
		//    매수·매도가 다르게 청산됐다. 추세 청산(매수 보유 중 +1·−2, 매도 보유 중 −1·+2)은 그대로 먼저 본다
		private bool HeldByTrend(int st)
		{
			if (!OrganicMode || !OrganicHoldWhileTrend || active == Rule.None)
				return false;
			return isLongPos ? st == 2 || st == -1 : st == -2 || st == 1;
		}

		// 이익 따라가기 청산 (OrganicTrailAtr, spec 5-2): 유기 모드의 추세 규칙(5.1, 5.4) 포지션은 1차 익절이 체결된 뒤
		// 손절을 "진입 뒤 최고가(매수) − 진입 봉 ATR × OrganicTrailAtr"로 따라 올린다(매도는 최저가 + ATR × 배수). 손절은 유리한 쪽으로만 움직인다.
		// 추세 봉 판정은 큰 움직임이 끝나고도 몇 분 늦게 바뀌어서, 추세 청산만 기다리면 그동안 이익을 돌려준다
		private void UpdateTrailStop()
		{
			if (active == Rule.None || entryFillQty <= 0)
				return;
			trailExtreme = double.IsNaN(trailExtreme) ? (isLongPos ? High[0] : Low[0])
				: isLongPos ? Math.Max(trailExtreme, High[0]) : Math.Min(trailExtreme, Low[0]);

			if (!OrganicMode || OrganicTrailAtr <= 0 || tpCount < 1 || !IsMain(active))
				return;
			double a = atr[0];
			if (double.IsNaN(a) || a <= 0)
				return;
			double target = isLongPos ? trailExtreme - OrganicTrailAtr * a : trailExtreme + OrganicTrailAtr * a;
			MoveStopTo(target, "따라가기", Time[0]);
		}

		// 밴드 익절 단계가 모두 체결됐는지 (남은 조각 중 밴드 익절이 걸린 것이 없음)
		private bool BandStagesDone()
		{
			foreach (string sig in liveSignals)
				if (chunkBand.ContainsKey(sig) && chunkBand[sig] != NoBand)
					return false;
			return true;
		}

		// AdoptTrendExits: 남은 밴드 조각의 익절점을 추세 규칙(5.1 또는 5.4)의 단계로 옮기고, 이후 청산 조건도 그 규칙을 따른다.
		// 조각 수량은 진입 때 나눈 그대로다. 남은 조각을 추세 규칙의 단계에 바깥쪽부터 맞추고, 남는 안쪽 조각은 첫 단계에 둔다
		// (예: −1 / −2 / −3배 조각 → 하락추세 매도의 −2 / −2 / −3배)
		private void AdoptTrendRule(Rule to)
		{
			string prefix;
			int[] bands, nums;
			int den;
			Plan(to, out prefix, out bands, out nums, out den);

			List<string> bandSigs = new List<string>();
			foreach (string sig in chunkOrder)
				if (liveSignals.Contains(sig) && chunkBand[sig] != NoBand)
					bandSigs.Add(sig);

			string moved = "";
			for (int i = 0; i < bandSigs.Count; i++)
			{
				int idx = Math.Max(0, bands.Length - (bandSigs.Count - i));
				moved += (moved.Length > 0 ? ", " : "") + BandText(chunkBand[bandSigs[i]]) + "→" + BandText(bands[idx]);
				chunkBand[bandSigs[i]] = bands[idx];
			}

			Out(string.Format("[{0}][{1}][TQ_Strategy] 추세 확정 → {2}의 청산을 {3} 규칙으로 갈아탐. 익절점 {4}",
				Time[0], Instrument.FullName, RuleName(active), RuleName(to), moved.Length > 0 ? moved : "변경 없음"));

			active		= to;
			adopted		= true;
			tradeMain	= true;	// 갈아탄 뒤에는 추세 규칙의 거래로 본다 (재진입 제한·이익 보호 본절 예외)
			UpdateTargets();
		}

		// 특정 진입 시그널 수량만 시장가 청산 (주문은 BIP 0 대상).
		// isBand = 밴드 익절 단계인지. 체결되면 손절 이동 등 후속 처리를 한다
		private void ExitChunk(string sig, string reason, bool isBand)
		{
			if (!liveSignals.Contains(sig))
				return;
			int q = chunkQty[sig];
			liveSignals.Remove(sig);	// 체결 콜백이 주문 호출 도중에 올 수 있어 먼저 뺀다
			if (isBand)
				pendingBandExit.Add("x" + sig);
			if (isLongPos)	ExitLong(0, q, "x" + sig, sig);
			else			ExitShort(0, q, "x" + sig, sig);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 익절 {2} 시그널={3} 수량={4}",
				Time[0], Instrument.FullName, reason, sig, q));
		}

		// 남은 전량 시장가 청산 (손절 스탑은 포지션 청산 시 자동 취소). 청산 후 중복 주문 방지
		private void FlattenRemaining(string reason)
		{
			if (isLongPos)	ExitLong();
			else			ExitShort();
			liveSignals.Clear();
			pendingBandExit.Clear();
			active = Rule.None;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 전량 청산 {2}",
				Time[0], Instrument.FullName, reason));
		}

		// 미보유 전환 시 호출: 진입·익절·손절 상태 전체 초기화.
		// 손절값은 다음 진입의 SubmitChunk에서 시그널별로 SetStopLoss를 다시 하므로 직전 값이 남지 않는다.
		private void ResetTradeState()
		{
			active			= Rule.None;
			entryRegime		= 0;
			isLongPos		= false;
			stopSwing		= double.NaN;
			stopPrice		= double.NaN;
			initialStop		= double.NaN;
			entryFillSum	= 0;
			entryFillQty	= 0;
			gapFlattened	= false;
			breakevenDone	= false;
			stopNote		= "";
			deferredStop	= double.NaN;
			lastStopTarget	= double.NaN;
			lastStopLabel	= "";
			tpCount			= 0;
			firstTpPrice	= double.NaN;
			adopted			= false;
			tradeMain		= false;
			tradeQueueStop	= false;
			stage			= 0;
			trailExtreme	= double.NaN;
			testHoldBars	= 0;
			stopAtr			= double.NaN;
			initialRisk		= double.NaN;
			reachedR		= false;	// 직전 거래의 이익 보호 본절 판정이 다음 거래로 넘어가지 않게 한다
			liveSignals.Clear();
			chunkOrder.Clear();
			chunkQty.Clear();
			chunkBand.Clear();
			filledBand.Clear();
			filledLevels.Clear();
			pendingBandExit.Clear();
		}
		#endregion

		#region 검증 — 임시 진입 (UseTestEntry)
		// UseTestEntry가 켜지면 실제 신호(지표) 대신 결정적 흐름으로 주문 흐름을 검증한다.
		// 진입·손절·분할 익절·손절 이동·청산 모두 운영과 같은 헬퍼를 써서 Playback에서 그대로 확인한다.

		// 테스트 진입: 5.1 상승추세 매수의 분할(1/5·2/5·나머지)로 들어간다. 밴드 익절 지정가는 걸지 않는다
		private void RouteTestEntry()
		{
			viaConfirm		= false;
			viaStage1		= false;
			entryQtyPercent	= 100;
			Enter(Rule.UpLong, RegimeState());
		}

		// 보유 봉 수에 따라 1차 익절(→ 본절) → 2차 익절(→ 손절을 1차 익절가로) → 나머지 전량 청산을 순서대로 낸다
		private void ManageTestExits()
		{
			if (testHoldBars == TestTp1Bar)
				ExitChunk("UpL1", "TEST 1차 익절", true);
			else if (testHoldBars == TestTp2Bar)
				ExitChunk("UpL2", "TEST 2차 익절", true);
			else if (testHoldBars >= TestExitBar)
				FlattenRemaining("TEST 최종 청산");
		}
		#endregion

		#region 화면 표시 (ShowStatus) — 매매 판단에는 영향 없음
		// 차트에서 전략 상태를 눈으로 보게 한다. 글은 줄이고 색과 도형을 우선 쓴다.
		//  · 배경색: 추세 (초록 = 상승추세, 주황 = 하락 전환 의심, 빨강 = 하락추세, 하늘색 = 상승 전환 의심, 없음 = 횡보)
		//  · 밴드: TQ_ATRChannels를 차트에 같이 올린다 (DataLoaded의 AddChartIndicator)
		//  · 마름모: 밴드 조건이 충족된 봉 t (하늘색 = 매수 쪽, 주황 = 매도 쪽)
		//  · 라벨: 체결 (매수·매도·익절·본절·손절·청산 + 수량)
		//  · 가로선: 손절가(자홍 실선), 평균 진입가(회색 점선) — 보유 중에만
		//  · 왼쪽 위 상자: 추세와 그 근거, 기다리는 조건을 단계별로 (○ 대기, ● 충족·완료, × 막힘, ■□ T1 남은 봉)

		// 지금 추세에서 쓰는 매수·매도 규칙 (없으면 None). 화면 표시용
		private void Candidates(int st, out Rule longRule, out Rule shortRule)
		{
			// 유기 모드: 추세가 방향을 정한다. 상승 쪽은 매수만, 하락 쪽은 매도만.
			// 전환 의심에서는 바뀔 것으로 보는 쪽의 추세 규칙으로 들어간다 (하락 전환 의심 → 하락추세 매도 규칙, 수량은 줄여서)
			if (OrganicMode)
			{
				switch (st)
				{
					case 2:		longRule = Rule.UpLong;		shortRule = Rule.None;			break;
					case 1:		longRule = Rule.None;		shortRule = Rule.DnShort;		break;
					case -2:	longRule = Rule.None;		shortRule = Rule.DnShort;		break;
					case -1:	longRule = Rule.UpLong;		shortRule = Rule.None;			break;
					default:	longRule = Rule.SideLong;	shortRule = Rule.SideShort;		break;
				}
				return;
			}

			switch (st)
			{
				case 2:		longRule = Rule.UpLong;		shortRule = Rule.None;			break;
				case 1:		longRule = Rule.UpSusLong;	shortRule = Rule.UpSusShort;	break;
				case -2:	longRule = Rule.DnLong;		shortRule = Rule.DnShort;		break;
				case -1:	longRule = Rule.DnSusLong;	shortRule = Rule.DnSusShort;	break;
				default:	longRule = Rule.SideLong;	shortRule = Rule.SideShort;		break;
			}
		}

		private int WaitOf(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return signals.WaitUpLong;
				case Rule.UpSusLong:	return signals.WaitUpSusLong;
				case Rule.UpSusShort:	return signals.WaitUpSusShort;
				case Rule.DnShort:		return signals.WaitDnShort;
				case Rule.DnLong:		return signals.WaitDnLong;
				case Rule.DnSusLong:	return signals.WaitDnSusLong;
				case Rule.DnSusShort:	return signals.WaitDnSusShort;
				case Rule.SideLong:		return signals.WaitSideLong;
				case Rule.SideShort:	return signals.WaitSideShort;
				default:				return -1;
			}
		}

		private int WaitBarsOf(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:
				case Rule.UpSusLong:
				case Rule.UpSusShort:	return signals.T1BarsUp;
				case Rule.SideLong:
				case Rule.SideShort:	return signals.T1BarsSide;
				default:				return signals.T1BarsDn;
			}
		}

		// 진입 후보의 밴드 조건을 글로
		private string BandConditionText(Rule r)
		{
			switch (r)
			{
				case Rule.UpLong:		return "저가 " + BandText(-UpLongBand) + " 터치";
				case Rule.UpSusLong:	return "종가 " + BandText(-UpSusLongBand) + " 아래";
				case Rule.UpSusShort:	return "고가 " + BandText(UpSusShortBand) + " 터치";
				case Rule.DnShort:		return "고가 " + BandText(DnShortBand) + " 터치";
				case Rule.DnLong:		return "RSI " + DnLongRsi + " 아래 + 저가 " + BandText(-DnLongBand) + " 터치";
				case Rule.DnSusLong:	return "저가 " + BandText(-DnSusLongBand) + " 터치";
				case Rule.DnSusShort:	return "종가 " + BandText(DnSusShortBand) + " 위";
				case Rule.SideLong:		return "저가 " + BandText(-SideBand) + " 터치 (또는 MACD▲ + 골든)";
				case Rule.SideShort:	return "고가 " + BandText(SideBand) + " 터치 (또는 MACD▼ + 데드)";
				default:				return "";
			}
		}

		// 진입 봉마다 호출: 추세 배경색과 밴드 조건 마름모. 차트가 없으면(Strategy Analyzer) 그리지 않는다
		private void PaintBar()
		{
			if (!ShowStatus || ChartControl == null)
				return;

			int st = RegimeState();
			BackBrush = st == 2 ? upBackBrush : st == 1 ? upSuspectBrush : st == -1 ? downSuspectBrush : st == -2 ? downBackBrush : null;

			// 지금 추세에서 쓰는 진입 규칙의 밴드 조건만 표시한다 (spec 3장: 진입 규칙은 추세로 고른다)
			Rule longRule, shortRule;
			Candidates(st, out longRule, out shortRule);
			bool longSetup	= RuleEnabled(longRule) && WaitOf(longRule) == 0;
			bool shortSetup	= RuleEnabled(shortRule) && WaitOf(shortRule) == 0;

			double gap = (channels.Up1[0] - channels.Mid[0]) * 0.4;	// 봉에서 띄우는 거리. 밴드 한 칸의 0.4배
			if (longSetup)
				Draw.Diamond(this, "TQ_SetupL_" + CurrentBars[0], false, 0, Low[0] - gap, Brushes.DeepSkyBlue);
			if (shortSetup)
				Draw.Diamond(this, "TQ_SetupS_" + CurrentBars[0], false, 0, High[0] + gap, Brushes.Orange);
		}

		// ── 왼쪽 위 상태 상자 (구역별 표시, 2026-10-14) ──
		// 내용은 OnBarUpdate·체결 때 UpdateStatusPanel이 만들고(계산은 여기서 끝냄), 그리기는 OnRender가 한다.
		// 구역: 현재 상태 / 진입 조건(미보유) 또는 포지션·익절·손절·청산(보유 중). 구역마다 색 제목줄과 색조 배경, 구역 사이 간격.
		// 줄 색은 맨 앞 기호로 정한다: ● 끝남·충족(초록), ○ 기다리는 조건(흰색), × 막힘(회색), 두 칸 들여쓴 줄 = 참고(옅은 회색)
		private class PanelSection
		{
			public string		Title;
			public Color		Tone;			// 제목줄 색. 배경은 이 색을 어둡게 섞어 칠한다
			public bool			Headline;		// 첫 줄을 크게·제목 색으로 (현재 상태의 추세 이름)
			public List<string>	Lines = new List<string>();
		}

		// 상태 상자 내용을 쌓는 도우미. 예전 글 상자와 같은 문자열을 받아 줄 단위로 지금 구역에 넣는다
		private class StatusPanelBuilder
		{
			public readonly List<PanelSection> Sections = new List<PanelSection>();

			public void Section(string title, Color tone, bool headline = false)
			{
				Sections.Add(new PanelSection { Title = title, Tone = tone, Headline = headline });
			}

			public void Append(string text)
			{
				if (Sections.Count == 0)
					Section("", Colors.Gray);
				foreach (string line in text.Split('\n'))
					if (line.Length > 0)
						Sections[Sections.Count - 1].Lines.Add(line);
			}

			public void AppendFormat(string format, params object[] args)
			{
				Append(string.Format(format, args));
			}
		}

		// OnRender가 읽는 지금의 상자 내용. 통째로 새 목록으로 바꿔 넣으므로 그리는 도중에 내용이 섞이지 않는다
		private List<PanelSection> panelSections;

		// 구역 색
		private static readonly Color ToneUp		= Color.FromRgb(0, 150, 60);		// 상승추세
		private static readonly Color ToneUpSus		= Color.FromRgb(210, 130, 0);		// 하락 전환 의심
		private static readonly Color ToneDown		= Color.FromRgb(190, 30, 30);		// 하락추세
		private static readonly Color ToneDownSus	= Color.FromRgb(0, 130, 210);		// 상승 전환 의심
		private static readonly Color ToneSide		= Color.FromRgb(110, 110, 110);		// 횡보
		private static readonly Color ToneEntry		= Color.FromRgb(70, 90, 170);		// 진입 조건
		private static readonly Color TonePosLong	= Color.FromRgb(0, 120, 90);		// 포지션(매수)
		private static readonly Color TonePosShort	= Color.FromRgb(160, 50, 70);		// 포지션(매도)
		private static readonly Color ToneTp		= Color.FromRgb(180, 140, 0);		// 익절
		private static readonly Color ToneStop		= Color.FromRgb(170, 0, 140);		// 손절
		private static readonly Color ToneExit		= Color.FromRgb(40, 110, 180);		// 청산

		private Color StateTone(int st)
		{
			return st == 2 ? ToneUp : st == 1 ? ToneUpSus : st == -2 ? ToneDown : st == -1 ? ToneDownSus : ToneSide;
		}

		// 왼쪽 위 상자의 내용과 손절선·평단선. 실시간·Playback에서만 갱신한다.
		// 같은 태그를 다시 써서 그리기 개체는 가로선 2개로 고정된다. 상자는 OnRender가 그린다
		private void UpdateStatusPanel()
		{
			if (!ShowStatus || State != State.Realtime || CurrentBars[0] < minBars0 || CurrentBars[regimeBip] < RegimeSlow
				|| double.IsNaN(regime.Regime[0]))
				return;

			StatusPanelBuilder sb = new StatusPanelBuilder();

			int st			= RegimeState();
			double close	= Closes[0][0];
			int trendMin	= TrendMinutes();

			// ── 현재 상태: 추세, 추세의 근거(추세 봉), 진입 봉 지표 ──
			sb.Section("현재 상태", StateTone(st), true);
			sb.AppendFormat("{0}  {1:HH:mm}\n", StateText(st), Times[0][0]);
			if (RegimeMode == 2)
			{
				// 점수 방식: 합계와 지표별 방향 (▲ 상승 +1, ▼ 하락 −1, − 중립)
				int sc = regime.Score;
				sb.AppendFormat("{0}분봉: 점수 {1}{2} ({3}) · RSI {4:F0}{5}\n", trendMin, sc > 0 ? "+" : "", sc, regime.ScoreText, regime.Rsi[0], RsiCenterText());
			}
			else
			{
				int al = regime.Aligned;
				sb.AppendFormat("{0}분봉: {1} · MACD선 {2}{3:F1} · RSI {4:F0}{5}\n", trendMin,
					al > 0 ? "정배열" : al < 0 ? "역배열" : "배열 없음",
					regime.MacdLine[0] >= 0 ? "+" : "", regime.MacdLine[0], regime.Rsi[0], RsiCenterText());
			}
			if (regime.SuspectReason.Length > 0)
				sb.AppendFormat("  의심 근거: {0}\n", regime.SuspectReason);
			sb.AppendFormat("{0}분봉: MACD {1}  %K {2:F0} / %D {3:F0}  RSI {4:F0}  {5}선 {6}\n", BarsPeriod.Value,
				// 방향(오르는 중 ▲, 내리는 중 ▼)을 보여 주고, 이 봉이 전환이 확인된 봉이면 "전환"을 붙인다
				signals.MacdUp[0] ? "▲ 전환" : signals.MacdDown[0] ? "▼ 전환" : signals.MacdRising[0] ? "▲" : signals.MacdFalling[0] ? "▼" : "−",
				signals.K[0], signals.D[0], signals.Rsi[0], ExitSmaPeriod, close > signals.Sma[0] ? "위" : "아래");
			if (TwoStageOn() && regimeFast != null)
				sb.AppendFormat("{0}분봉 추세(두 단계 진입용): {1}\n", BarsPeriod.Value, StateText(FastState(0)));

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				RemoveDrawObject("TQ_StopLine");
				RemoveDrawObject("TQ_AvgLine");

				// ── 진입 조건 ──
				sb.Section("진입 조건", ToneEntry);

				if (IsFlattenTime())
					sb.Append("× 대회 시간 밖\n");
				else if (!InEntryWindow())
					sb.Append("× 진입 시간 아님\n");
				else if (active != Rule.None)
					sb.Append("● 진입 주문 냄, 체결 대기\n");
				else if (UseTestEntry)
					sb.Append("TEST: 다음 봉에 검증용 진입\n");
				else
				{
					Rule longRule, shortRule;
					Candidates(st, out longRule, out shortRule);

					string longBlock	= null;
					string shortBlock	= null;
					if (BlockReentryAfterStop && blockLong && !IsMainAt(longRule, st))		longBlock = "손절 뒤 재진입 금지";
					if (BlockReentryAfterStop && blockShort && !IsMainAt(shortRule, st))	shortBlock = "손절 뒤 재진입 금지";
					if (longRule != Rule.None && !RuleEnabled(longRule))			longBlock = "이 규칙 꺼짐";
					if (shortRule != Rule.None && !RuleEnabled(shortRule))			shortBlock = "이 규칙 꺼짐";
					if (OrganicMode && (st == 1 || st == -1) && OrganicSuspectQtyPercent <= 0)
					{
						longBlock	= "전환 의심 때 역진입 꺼짐";
						shortBlock	= "전환 의심 때 역진입 꺼짐";
					}
					if (SideBlocked(st, true))										longBlock = "추세 봉 지표가 하락 쪽 (점수 " + regime.Score + ")";
					if (SideBlocked(st, false))										shortBlock = "추세 봉 지표가 상승 쪽 (점수 +" + regime.Score + ")";

					// 그 추세에서 우선하는 방향을 먼저 보여 준다 (상승 쪽은 매수, 하락 쪽은 매도. 하락 전환 의심은 매도, 상승 전환 의심은 매수)
					bool shortFirst = st == 1 || st == -2;
					// 그 규칙 자체의 대기가 없어도, 추세가 바뀌기 전 규칙에서 이어받은 대기가 남아 있으면 그것을 보여 준다
					int longWait = WaitOf(longRule), longBars = WaitBarsOf(longRule);
					string longRev = "MACD▲ / 골든";
					if (longWait < 0 && carryLongLeft > 0)		{ longWait = 0; longBars = carryLongLeft; longRev += " (이전 추세에서 이어받음)"; }
					int shortWait = WaitOf(shortRule), shortBars = WaitBarsOf(shortRule);
					string shortRev = "MACD▼ / 데드";
					if (shortWait < 0 && carryShortLeft > 0)	{ shortWait = 0; shortBars = carryShortLeft; shortRev += " (이전 추세에서 이어받음)"; }

					// 유기 모드: 전환 의심 상태의 진입은 수량을 줄인 역진입임을 이름에 보여 준다
					string sizeNote		= OrganicMode && (st == 1 || st == -1) ? " (역진입 " + OrganicSuspectQtyPercent + "%)" : "";
					if (OrganicMode)
						sb.Append(OrganicEnterOnConfirm && (st == 0 || st == 1 || st == -1) ? "유기 모드: 추세가 확정되는 봉에 바로 진입\n" : "유기 모드\n");
					if (TwoStageOn() && regimeFast != null)
						sb.AppendFormat("두 단계 진입: 1분봉 {0} → 1분봉이 {1}가 되면 {2}% 먼저\n", StateText(FastState(0)),
							st == 0 ? "상승추세·하락추세" : st == -1 ? "상승추세" : st == 1 ? "하락추세" : "(추세 봉 확정 중이라 해당 없음)", OrganicStage1Percent);

					string longLine		= longRule == Rule.None ? (OrganicMode ? "× 매수: 이 추세에서는 하지 않음\n" : "× 매수: 없음\n")
						: CandidateLine("매수" + sizeNote, longWait, BandConditionText(longRule), longRev, longBlock, longBars);
					string shortLine	= shortRule == Rule.None ? "× 매도: 이 추세에서는 하지 않음\n"
						: CandidateLine("매도" + sizeNote, shortWait, BandConditionText(shortRule), shortRev, shortBlock, shortBars);
					// 의심 뒤 가격 확인 역진입: 밴드 조건 대신 직전 저점·고점 돌파를 기다린다
					if (SuspectTriggerOn(st))
					{
						string trig = st == 1
							? string.Format("종가 < 직전 {0}봉 저점 {1:N2}", SwingBars, signals.SwingLow5[0])
							: string.Format("종가 > 직전 {0}봉 고점 {1:N2}", SwingBars, signals.SwingHigh5[0]);
						string block = st == 1 ? shortBlock : longBlock;
						if (block == null && suspectTriggerUsed)
							block = "이번 의심 구간에서 이미 진입함";
						string line = block != null ? string.Format("× {0}{1}: {2}\n", st == 1 ? "매도" : "매수", sizeNote, block)
							: string.Format("○ {0}{1}: 의심 뒤 가격 확인 — {2}\n", st == 1 ? "매도" : "매수", sizeNote, trig);
						if (st == 1)	shortLine = line;
						else			longLine = line;
					}
					sb.Append(shortFirst ? shortLine + longLine : longLine + shortLine);

					// 반전 신호 큐: 지금 쓸 수 있는 신호 (전환 의심이 아니면 모아 두기만 한다)
					if (OrganicMode && OrganicSignalQueue)
					{
						QueuedSignal qs = LatestQueued(false), ql = LatestQueued(true);
						sb.AppendFormat("큐{0}: 매도 {1} / 매수 {2}\n", QueueEntryOn(st) ? (queueEntryUsed ? " (이번 의심 구간에서 사용함)" : " → 의심 중이라 바로 진입") : "",
							qs == null ? "없음" : string.Format("{0}분 전 고점 {1:N2}", CurrentBars[0] - qs.Bar, qs.Extreme),
							ql == null ? "없음" : string.Format("{0}분 전 저점 {1:N2}", CurrentBars[0] - ql.Bar, ql.Extreme));
					}
				}
			}
			else
			{
				bool isLong	= Position.MarketPosition == MarketPosition.Long;
				double avg	= entryFillQty > 0 ? entryFillSum / entryFillQty : Position.AveragePrice;
				double pts	= isLong ? close - avg : avg - close;

				// ── 포지션 ──
				sb.Section("포지션", isLong ? TonePosLong : TonePosShort);
				sb.AppendFormat("{0} {1} @ {2:N2}  {3}{4:N2}  ({5})\n", isLong ? "매수" : "매도", Position.Quantity, avg,
					pts >= 0 ? "+" : "", pts, UseTestEntry ? "TEST" : RuleName(active) + (adopted ? ", 갈아탐" : ""));
				if (!double.IsNaN(stopPrice))
					Draw.HorizontalLine(this, "TQ_StopLine", false, stopPrice, Brushes.Magenta, DashStyleHelper.Solid, 2);
				Draw.HorizontalLine(this, "TQ_AvgLine", false, avg, Brushes.Gray, DashStyleHelper.Dash, 1);

				AppendExitSteps(sb);
			}

			panelSections = sb.Sections;
		}

		// 상태 상자 그리기. 계산은 하지 않고 UpdateStatusPanel이 만든 내용(panelSections)만 그린다 (.claude/rules/ninjascript.md).
		// SharpDX 자원(글꼴 형식, 글 배치, 붓)은 그릴 때마다 만들고 끝에서 모두 Dispose한다 (공식 샘플 @SampleCustomRender.cs와 같은 방식)
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			List<PanelSection> sections = panelSections;
			if (!ShowStatus || sections == null || sections.Count == 0 || RenderTarget == null || ChartPanel == null)
				return;

			// 크기: 글자·여백·폭을 모두 StatusPanelScale(%)만큼 키운다. 100이면 본문 12 / 추세 이름 15 / 폭 560px
			float k100		= Math.Max(50, StatusPanelScale) / 100f;
			float margin	= 8f * k100, pad = 6f * k100, gap = 6f * k100, lineGap = 1f * k100;
			float maxW = Math.Max(200f, Math.Min(560f * k100, ChartPanel.W - 2 * margin));	// 너무 긴 줄은 이 폭에서 줄바꿈

			List<IDisposable> trash = new List<IDisposable>();
			try
			{
				SharpDX.DirectWrite.TextFormat bodyFmt	= new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic", 12f * k100);
				SharpDX.DirectWrite.TextFormat titleFmt	= new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic",
															SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 12f * k100);
				SharpDX.DirectWrite.TextFormat headFmt	= new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic",
															SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 15f * k100);
				trash.Add(bodyFmt);
				trash.Add(titleFmt);
				trash.Add(headFmt);

				// 1단계: 모든 줄의 글 배치를 만들고 상자 폭(가장 긴 줄)을 정한다. 구역 폭을 같게 해서 세로로 가지런히 쌓는다
				List<SharpDX.DirectWrite.TextLayout> titles = new List<SharpDX.DirectWrite.TextLayout>();
				List<List<SharpDX.DirectWrite.TextLayout>> bodies = new List<List<SharpDX.DirectWrite.TextLayout>>();
				float boxW = 0;
				foreach (PanelSection s in sections)
				{
					SharpDX.DirectWrite.TextLayout t = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s.Title, titleFmt, maxW, 100f * k100);
					trash.Add(t);
					titles.Add(t);
					boxW = Math.Max(boxW, t.Metrics.Width);
					List<SharpDX.DirectWrite.TextLayout> lines = new List<SharpDX.DirectWrite.TextLayout>();
					for (int i = 0; i < s.Lines.Count; i++)
					{
						SharpDX.DirectWrite.TextLayout l = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s.Lines[i],
							s.Headline && i == 0 ? headFmt : bodyFmt, maxW, 400f * k100);
						trash.Add(l);
						lines.Add(l);
						boxW = Math.Max(boxW, l.Metrics.Width);
					}
					bodies.Add(lines);
				}
				boxW = Math.Min(maxW, boxW) + 2 * pad;

				// 줄 색 (맨 앞 기호로)
				SharpDX.Direct2D1.SolidColorBrush white	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)240, (byte)240, (byte)240, (byte)255));
				SharpDX.Direct2D1.SolidColorBrush done	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)120, (byte)225, (byte)120, (byte)255));
				SharpDX.Direct2D1.SolidColorBrush block	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)150, (byte)150, (byte)150, (byte)255));
				SharpDX.Direct2D1.SolidColorBrush note	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)180, (byte)180, (byte)190, (byte)255));
				trash.Add(white);
				trash.Add(done);
				trash.Add(block);
				trash.Add(note);

				// 2단계: 구역마다 제목줄(구역 색) → 본문 배경(구역 색을 어둡게) → 줄을 그리고, 구역 사이를 띄운다
				float y = ChartPanel.Y + margin;
				float x = ChartPanel.X + margin;
				for (int k = 0; k < sections.Count; k++)
				{
					PanelSection s = sections[k];
					Color c = s.Tone;
					SharpDX.Direct2D1.SolidColorBrush headBg	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(c.R, c.G, c.B, (byte)235));
					SharpDX.Direct2D1.SolidColorBrush bodyBg	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
						new SharpDX.Color((byte)(c.R / 5 + 12), (byte)(c.G / 5 + 12), (byte)(c.B / 5 + 12), (byte)220));
					SharpDX.Direct2D1.SolidColorBrush headText	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
						new SharpDX.Color((byte)((c.R + 255 * 2) / 3), (byte)((c.G + 255 * 2) / 3), (byte)((c.B + 255 * 2) / 3), (byte)255));
					trash.Add(headBg);
					trash.Add(bodyBg);
					trash.Add(headText);

					float titleH = titles[k].Metrics.Height + 4 * k100;
					float bodyH = pad;
					foreach (SharpDX.DirectWrite.TextLayout l in bodies[k])
						bodyH += l.Metrics.Height + lineGap;
					bodyH += pad - lineGap;

					RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, boxW, titleH), headBg);
					RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + pad, y + 2 * k100), titles[k], white, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
					y += titleH;

					if (bodies[k].Count > 0)
					{
						RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, boxW, bodyH), bodyBg);
						float ly = y + pad;
						for (int i = 0; i < bodies[k].Count; i++)
						{
							string text = s.Lines[i];
							SharpDX.Direct2D1.SolidColorBrush b = s.Headline && i == 0 ? headText
								: text.StartsWith("●") ? done : text.StartsWith("×") ? block : text.StartsWith("  ") ? note : white;
							RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + pad, ly), bodies[k][i], b, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
							ly += bodies[k][i].Metrics.Height + lineGap;
						}
						y += bodyH;
					}
					y += gap;
				}
			}
			finally
			{
				foreach (IDisposable d in trash)
					d.Dispose();
			}
		}

		// 화면 표시용: 의심의 RSI 조건을 상대 기준으로 볼 때(RegimeRsiRelative), 지금의 RSI 중심과 위아래 기준선을 RSI 옆에 보여 준다
		private string RsiCenterText()
		{
			if (!RegimeRsiRelative || double.IsNaN(regime.RsiCenter))
				return "";
			return string.Format(" (중심 {0:F0}, 기준 {1:F0} / {2:F0})", regime.RsiCenter, regime.RsiCenter + RegimeRsiOffset, regime.RsiCenter - RegimeRsiOffset);
		}

		// 진입 후보 한 줄. ○ = 밴드 조건 대기, ● = 밴드 조건 충족 뒤 반전 신호 대기(■ 지난 봉, □ 남은 봉), × = 막힘
		private string CandidateLine(string name, int wait, string bandText, string reversalText, string blockedReason, int total)
		{
			if (blockedReason != null)
				return string.Format("× {0}: {1}\n", name, blockedReason);
			if (wait < 0)
				return string.Format("○ {0}: {1} → {2}\n", name, bandText, reversalText);

			int done	= Math.Max(0, Math.Min(wait, total));
			return string.Format("● {0}: {1} 대기 {2}{3}\n", name, reversalText, new string('■', done), new string('□', total - done));
		}

		// 지금 손절가와 그 출처(본절·1차 익절가·따라가기), 미뤄 둔 이동 (손절 구역의 첫 줄)
		private void AppendStopLine(StatusPanelBuilder sb)
		{
			if (double.IsNaN(stopPrice))
				return;
			sb.AppendFormat("손절 {0:N2}{1}{2}\n", stopPrice, stopNote.Length > 0 ? " (" + stopNote + ")" : "",
				double.IsNaN(deferredStop) ? "" : string.Format(" → {0:N2} 이동 대기", deferredStop));
		}

		// 보유 중 익절·손절·청산 (spec 5장). ● = 끝난 단계, ○ = 기다리는 단계
		private void AppendExitSteps(StatusPanelBuilder sb)
		{
			if (active == Rule.None || UseTestEntry)
			{
				sb.Section("손절", ToneStop);
				AppendStopLine(sb);
				sb.Section("청산", ToneExit);
				sb.Append(active == Rule.None ? "● 청산 주문 냄, 체결 대기\n" : "TEST: 2봉째 1차 익절, 3봉째 2차 익절, 4봉째 전량 청산\n");
				return;
			}

			// ── 익절: 밴드 익절 단계(체결된 단계는 ●, 남은 단계는 ○와 지금 걸려 있는 가격)와 마지막 조각 ──
			sb.Section("익절", ToneTp);
			foreach (string sig in chunkOrder)
			{
				int band = chunkBand[sig];
				if (band == NoBand)
					continue;
				if (filledBand.Contains(sig))
					sb.AppendFormat("● {0} → {1}계약 익절\n", BandText(band), chunkQty[sig]);
				else if (liveSignals.Contains(sig))
					sb.AppendFormat("○ {0} {1:N2} → {2}계약 익절\n", BandText(band), BandPrice(band), chunkQty[sig]);
			}

			// 마지막 조각 (두 단계 진입으로 추가했으면 두 조각을 합쳐 보여 준다)
			int lastQty = 0;
			foreach (string sig in LastChunks())
				lastQty += chunkQty[sig];
			if (lastQty > 0)
			{
				int side = LastChunkSide(active);
				if (side == 0)
					sb.AppendFormat("○ 마지막 {0}계약: 아래 청산 조건으로\n", lastQty);
				else
					sb.AppendFormat("○ 마지막 {0}계약: 종가 {1} {2}선 {3:N2}{4}\n", lastQty, side > 0 ? ">" : "<",
						ExitSmaPeriod, signals.Sma[0], LastChunkEarly || BandStagesDone() ? "" : " (앞 단계가 끝난 뒤부터)");
			}
			// ── 손절: 지금 손절가, 따라가기, 다음 익절 때의 손절 이동 ──
			sb.Section("손절", ToneStop);
			AppendStopLine(sb);
			if (OrganicMode && OrganicTrailAtr > 0 && IsMain(active))
			{
				string from = string.Format("{0} {1:N2} {2} ATR×{3}", isLongPos ? "최고가" : "최저가", trailExtreme, isLongPos ? "−" : "+", OrganicTrailAtr);
				sb.Append(tpCount >= 1 ? "○ 따라가기: 손절을 " + from + "로\n" : "  따라가기: 1차 익절 뒤부터 (" + from + ")\n");
			}
			if (TrailStopAtTp2 && tpCount == 1 && !double.IsNaN(firstTpPrice))
				sb.AppendFormat("  다음 익절 때 손절 → {0:N2} (1차 익절가)\n", firstTpPrice);

			// ── 청산: 두 단계 진입 1단계, 전량 청산 조건, 추세 청산 ──
			sb.Section("청산", ToneExit);
			if (stage == 1)
				sb.AppendFormat("○ 두 단계 진입 1단계: 추세 봉이 {0}가 되면 나머지 추가, 그 전에 1분봉 추세가 꺼지면 청산 (지금 1분봉 {1})\n",
					isLongPos ? "상승추세" : "하락추세", StateText(FastState(0)));

			string full = FullExitText(active);
			if (full != null && HeldByTrend(RegimeState()))
				sb.AppendFormat("  (전량 청산 조건 {0} — 추세가 유지되는 동안 무시)\n", full);
			else if (full != null)
				sb.AppendFormat("○ 전량 청산: {0}\n", full);
			string trend = TrendExitText(active);
			if (trend != null)
				sb.AppendFormat("○ 추세 청산: {0}\n", trend);
			sb.AppendFormat("  종료 청산: {0:00}:{1:00} 봉 마감\n", FlattenTime / 10000, FlattenTime / 100 % 100);
		}

		// 체결을 차트에 라벨로 표시한다. 매수 체결은 가격 아래, 매도 체결은 가격 위.
		// 같은 봉에서 같은 종류로 여러 건 체결되면(분할 진입 여러 건 등) 수량을 합쳐 라벨 하나로 보여 준다
		private void MarkExecution(string name, double price, int quantity, MarketPosition side, DateTime time)
		{
			if (!ShowStatus || ChartControl == null)
				return;

			bool isBuy = side == MarketPosition.Long;
			string kind;
			Brush brush;

			if (chunkQty.ContainsKey(name))
			{
				kind	= isBuy ? "매수" : "매도";
				brush	= isBuy ? Brushes.LimeGreen : Brushes.Tomato;
			}
			else if (name.StartsWith("x") || name == "Profit target")
			{
				kind	= "익절";
				brush	= Brushes.Goldenrod;
			}
			else if (name == "Stop loss")
			{
				// 손절이 본절이나 1차 익절가로 옮겨진 뒤의 체결은 손실 손절과 구분한다
				kind	= !breakevenDone ? "손절" : stopNote == "1차 익절가" || stopNote == "따라가기" ? "익절 스탑" : "본절";
				brush	= Brushes.Magenta;
			}
			else
			{
				kind	= "청산";
				brush	= Brushes.DodgerBlue;
			}

			string tag	= "TQ_Ex_" + kind + "_" + CurrentBars[0];
			markQty		= tag == markTag ? markQty + quantity : quantity;
			markTag		= tag;

			Draw.Text(this, tag, false, kind + " " + markQty, time, price, isBuy ? -20 : 20, brush, markFont,
				System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
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

		#region Properties
		// 설정창 배치 원칙 (2026-10-12 재정리)
		//  · 묶음은 판단 순서대로다: 추세 판단 → 추세 상태별 진입 → 진입 공통 → 손절 → 손절 이동 → 익절·청산 → 수량·시간 → 지표 → 화면.
		//  · 이름 앞의 태그: [명세] = 회의 문서의 규칙과 그 값, [추가] = 회의 문서에 없는 옵션(기본값은 명세와 같은 동작), [안전] = 주문 거부 대비, [표시] = 화면·기록.
		//  · 한 묶음 안에서는 [명세]가 위, [추가]가 아래다.
		//  · 설명(Description)은 "무엇인지 / 명세 값 / 올리거나 켜면 어떻게 되는지" 순서로 쓴다.
		//  · 괄호 안의 코드 이름은 docs/spec.md 8장과 docs/backtest-guide.md에서 쓰는 이름이다

		// ── 01. 추세 판단 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 이동평균 단기 (RegimeFast)", Description = "추세 봉의 단기 이동평균. 명세 6. 정배열은 단기 > 중기 > 장기, 역배열은 반대. 줄이면 추세를 빨리 인정하지만 자주 바뀜", GroupName = "01. 추세 판단", Order = 0)]
		public int RegimeFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 이동평균 중기 (RegimeMid)", Description = "추세 봉의 중기 이동평균. 명세 18", GroupName = "01. 추세 판단", Order = 1)]
		public int RegimeMid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 이동평균 장기 (RegimeSlow)", Description = "추세 봉의 장기 이동평균. 명세 36. 이 봉 수만큼 데이터가 쌓여야 전략이 시작됨(10분봉 36개 = 6시간)", GroupName = "01. 추세 판단", Order = 2)]
		public int RegimeSlow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[명세] 하락 전환 의심: RSI가 이 값 이상 (RegimeRsiHigh)", Description = "상승추세 중 추세 봉 RSI가 이 값 이상이고 [RSI 다이버전스 / MACD 하락 전환 / 데드크로스] 중 하나가 나오면 하락 전환 의심. 명세 60. 올리면 의심이 드물어짐", GroupName = "01. 추세 판단", Order = 3)]
		public double RegimeRsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[명세] 상승 전환 의심: RSI가 이 값 이하 (RegimeRsiLow)", Description = "하락추세 중 추세 봉 RSI가 이 값 이하이고 [RSI 다이버전스 / MACD 상승 전환 / 골든크로스] 중 하나가 나오면 상승 전환 의심. 명세 40. 내리면 의심이 드물어짐", GroupName = "01. 추세 판단", Order = 4)]
		public double RegimeRsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 다이버전스: 최근 몇 봉 안의 고점·저점과 비교 (RegimeDivBars)", Description = "이 봉 수 안에 생긴 확연한 고점(저점)과 지금 봉을 비교. 지금이 더 높은 고점인데 RSI는 낮으면 하락 다이버전스. 명세 15", GroupName = "01. 추세 판단", Order = 5)]
		public int RegimeDivBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] MACD 전환: 히스토그램 연속 봉 수 (RegimeHistBars)", Description = "추세 봉의 MACD 히스토그램이 몇 봉 연속 줄어야(늘어야) 전환으로 볼지. 명세 2", GroupName = "01. 추세 판단", Order = 6)]
		public int RegimeHistBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다 (1이면 두 단계 진입을 잠금)
		[Display(Name = "[추가] 추세 봉: 10분, 3분, 1(진입 봉) (RegimeBarMinutes)", Description = "추세를 판단하는 봉. 명세 10(10분봉). 3이면 3분봉. 1이면 따로 추세 봉을 두지 않고 진입 봉(차트의 봉, 보통 1분봉)으로 추세까지 판단함. 짧을수록 추세 인정이 빠르지만 자주 바뀜. 1, 3, 10만 쓸 수 있고 다른 값은 10으로 봄. 이 묶음의 봉 수(이동평균 기간 등)는 모두 이 봉 기준이라, 1분봉이면 이동평균 6/18/36이 6/18/36분이 됨", GroupName = "01. 추세 판단", Order = 10)]
		public int RegimeBarMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 의심의 RSI 조건을 최근 몇 봉 안에서 볼지 (RegimeRsiBars)", Description = "명세 1(RSI 기준과 다이버전스·MACD 전환·크로스가 같은 봉에서 만나야 의심). 3이면 RSI가 최근 3봉 안에 기준을 넘은 적이 있으면 충족. MACD 전환과 크로스는 늦게 확인되므로 1이면 급하게 꺾일 때 의심이 뜨지 않을 수 있음", GroupName = "01. 추세 판단", Order = 11)]
		public int RegimeRsiBars { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[추가] 의심의 RSI 기준을 그때의 RSI 중심 대비로 (RegimeRsiRelative)", Description = "명세 끔(고정 기준: 60 이상이면 하락 전환 의심 쪽, 40 이하면 상승 전환 의심 쪽). 켜면 고정 기준 대신 추세 봉 RSI의 최근 평균(중심)에서 아래 폭만큼 위에 있으면 하락 전환 의심 쪽, 아래에 있으면 상승 전환 의심 쪽으로 봄. RSI는 오르는 날에는 60 위에 오래 머물고 내려와도 40까지는 잘 안 가서, 고정 기준으로는 하락 전환 의심만 자주 뜸. 켜면 위아래가 같은 빈도로 뜸. 켜면 위의 고정 기준 두 값은 쓰이지 않음", GroupName = "01. 추세 판단", Order = 19)]
		public bool RegimeRsiRelative { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] RSI 중심을 구하는 봉 수 (RegimeRsiCenterBars)", Description = "위 옵션을 켰을 때만 쓰임. 추세 봉 RSI의 최근 몇 봉 평균을 중심으로 볼지. 기본 20. 줄이면 중심이 RSI를 바짝 따라가 의심이 드물어지고, 늘리면 고정 기준에 가까워짐", GroupName = "01. 추세 판단", Order = 20)]
		public int RegimeRsiCenterBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "[추가] RSI가 중심에서 벗어나야 하는 폭 (RegimeRsiOffset)", Description = "위 옵션을 켰을 때만 쓰임. RSI가 중심보다 이만큼 높으면 하락 전환 의심의 RSI 조건 충족, 이만큼 낮으면 상승 전환 의심의 RSI 조건 충족. 기본 8. 키우면 의심이 드물어짐", GroupName = "01. 추세 판단", Order = 21)]
		public double RegimeRsiOffset { get; set; }

		[NinjaScriptProperty]
		[Range(-5, 5)]
		[Display(Name = "[추가] 점수 방식: 추세가 끝나는 점수 (RegimeScoreExit)", Description = "추세 판단 방식이 2일 때만 쓰임. 상승추세는 점수가 이 값 이하로 내려가면 끝남(하락추세는 부호를 바꾼 값 이상으로 올라가면 끝남). 기본 0 = 지표들이 중립이 되면 끝남(음봉 하나에도 끝날 수 있음). -1이면 지표들이 실제로 반대쪽을 가리킬 때 끝나서 추세가 덜 끊김. 추세로 인정할 최소 점수보다 작아야 함", GroupName = "01. 추세 판단", Order = 22)]
		public int RegimeScoreExit { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 가격이 아직 추세 방향이면 전환 의심을 띄우지 않음 (RegimeSuspectNeedsPrice)", Description = "명세 끔. 켜면 추세 봉의 종가가 아직 단기선 위에 있고 단기선이 오르는 중일 때는 다른 조건(RSI + 다이버전스·MACD 전환·데드크로스)이 맞아도 하락 전환 의심을 띄우지 않음. 상승 전환 의심은 반대(종가가 단기선 아래 + 단기선 하락 중이면 띄우지 않음). 급등·급락 도중 잠깐 쉬는 지점에서 의심이 뜨는 것을 줄임. 두 추세 판단 방식 모두에 적용", GroupName = "01. 추세 판단", Order = 23)]
		public bool RegimeSuspectNeedsPrice { get; set; }

		[NinjaScriptProperty]
		[Range(0, 5)]
		[Display(Name = "[추가] 의심은 추세의 힘이 빠진 뒤에만: 점수 상한 (RegimeSuspectMaxScore)", Description = "기본 5(제한 없음). 하락 전환 의심은 추세 봉 점수가 이 값 이하일 때만, 상승 전환 의심은 −이 값 이상일 때만 띄움. 1이면 점수 +2 이상(지표 대부분이 아직 상승)인 동안에는 하락 전환 의심을 띄우지 않음. 10/14 로그 의심 17건: 점수 ±1 이하에서 뜬 7건은 모두 맞았고 ±2 이상에서 뜬 10건은 3건만 맞음(표본 작음). 두 추세 판단 방식 모두에 적용", GroupName = "01. 추세 판단", Order = 24)]
		public int RegimeSuspectMaxScore { get; set; }

		[NinjaScriptProperty]
		[Range(1, 4)]
		[Display(Name = "[추가] 의심에 필요한 근거 종류 수 (RegimeSuspectMinEvidence)", Description = "기본 1(명세: 근거 하나면 충분). 근거 = 다이버전스, MACD 전환, 크로스, 가격 항목이 더는 추세 방향이 아님(종가가 단기선 아래거나 단기선이 안 오름). 아래 봉 수 안에 이 수 이상의 종류가 나와야 의심을 띄움. 2면 스토캐스틱 데드크로스 하나만으로는 뜨지 않음. 그 봉에 다이버전스·MACD 전환·크로스 중 하나가 있어야 하는 것과 RSI 조건은 그대로", GroupName = "01. 추세 판단", Order = 25)]
		public int RegimeSuspectMinEvidence { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 의심 근거를 모으는 봉 수 (RegimeSuspectEvidenceBars)", Description = "기본 2. 위의 근거를 최근 몇 개의 추세 봉(현재 포함) 안에서 셀지. 근거 종류 수가 1이면 쓰이지 않음", GroupName = "01. 추세 판단", Order = 26)]
		public int RegimeSuspectEvidenceBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 추세 확정에 필요한 연속 봉 수 (RegimeConfirmBars)", Description = "기본 1(그 봉에서 바로). 추세 시작 조건(점수 방식: 가격 조건 + 최소 점수, 명세: 정배열 + MACD선 부호)이 이 봉 수만큼 연속이어야 추세로 인정. 2면 한 봉짜리 흔들림을 거르는 대신 추세 봉 한 개만큼 늦어짐(3분봉이면 3분)", GroupName = "01. 추세 판단", Order = 27)]
		public int RegimeConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 추세 종료에 필요한 연속 봉 수 (RegimeExitBars)", Description = "기본 1. 추세 종료 조건(점수 방식: 점수가 종료 점수 이하, 명세: 시작 조건이 깨짐)이 이 봉 수만큼 연속이어야 추세를 끝냄. 2면 추세 중 한 봉 쉬어 가는 것으로는 횡보가 되지 않음. 반대 방향 추세가 확정되면 그때는 바로 바뀜", GroupName = "01. 추세 판단", Order = 28)]
		public int RegimeExitBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 의심 중 반대 신호가 나오면 원래 추세로 복귀 (SuspectReturn)", Description = "회의 문서에서 미정인 부분. 켜면(기본) 하락 전환 의심 중 MACD 상승 전환이나 골든크로스가 나오면 상승추세로 돌아감(상승 전환 의심은 반대). 끄면 이동평균 배열이나 MACD선 부호가 깨질 때까지 의심 상태로 남음", GroupName = "01. 추세 판단", Order = 12)]
		public bool SuspectReturn { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 다이버전스: 고점·저점으로 인정할 좌우 봉 수 (RegimePivotStrength)", Description = "좌우로 이 봉 수만큼의 봉보다 고가가 높으면 확연한 고점(저가가 낮으면 저점). 기본 2. 키우면 큰 고점만 인정", GroupName = "01. 추세 판단", Order = 13)]
		public int RegimePivotStrength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 횡보 없이 추세와 전환 의심만 (RegimeNoSideways)", Description = "명세 끔. 켜면 횡보 상태를 쓰지 않고, 상승·하락추세 조건이 둘 다 아닌 구간을 마지막 추세의 전환 의심으로 봄. 횡보 매수·매도는 나오지 않게 됨", GroupName = "01. 추세 판단", Order = 14)]
		public bool RegimeNoSideways { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[추가] 추세 판단 방식: 1 명세, 2 점수 (RegimeMode)", Description = "1(기본) = 명세: 정배열 + MACD선 양수면 상승추세(하락은 반대). 2 = 점수 방식: 추세 봉에서 [가격이 단기선 위 + 단기선 상승], [MACD 히스토그램 양수 + 줄지 않음], [RSI 55 이상], [스토캐스틱 %K가 %D 위 + 50 위], [최근 사건이 상승 쪽: MACD 상승 전환·골든크로스·상승 다이버전스]를 각각 +1(반대면 −1)로 세어, 가격 조건이 맞고 합이 최소 점수 이상이면 상승추세, 합이 0 이하로 내려가면 끝. 전환 의심 조건은 두 방식 모두 같음", GroupName = "01. 추세 판단", Order = 15)]
		public int RegimeMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "[추가] 점수 방식: 추세로 인정할 최소 점수 (RegimeScoreMin)", Description = "추세 판단 방식이 2일 때만 쓰임. 기본 2. 3~4면 늦지만 확실", GroupName = "01. 추세 판단", Order = 16)]
		public int RegimeScoreMin { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "[추가] 점수 방식: RSI가 50에서 벗어나야 하는 폭 (RegimeScoreRsiBand)", Description = "추세 판단 방식이 2일 때만 쓰임. 5면 RSI 55 이상을 상승, 45 이하를 하락으로 셈", GroupName = "01. 추세 판단", Order = 17)]
		public double RegimeScoreRsiBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "[추가] 점수 방식: 사건을 점수에 넣는 봉 수 (RegimeScoreEventBars)", Description = "추세 판단 방식이 2일 때만 쓰임. 추세 봉에서 나온 사건(MACD 상승·하락 전환, 골든·데드크로스, 상승·하락 다이버전스) 중 가장 최근 것이 이 봉 수 안에 있으면, 상승 쪽 사건은 +1, 하락 쪽 사건은 −1로 점수에 더함. 기본 3. 0이면 사건을 쓰지 않음(지표의 상태만 봄). 키우면 사건의 영향이 오래 남음", GroupName = "01. 추세 판단", Order = 18)]
		public int RegimeScoreEventBars { get; set; }

		// ── 02. 진입 — 상승추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매수: 저가가 몇 배 밴드에 닿으면 (UpLongBand)", Description = "상승추세 매수의 밴드 조건. 명세 1(−1배). 0 = 중심선(얕은 눌림에도 진입), 2~3이면 깊은 눌림만", GroupName = "02. 진입 — 상승추세", Order = 0)]
		public int UpLongBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매수 사용 (UseUpLong)", Description = "명세 켬. 끄면 상승추세 매수를 하지 않음. 상승추세에는 매도 규칙이 없음", GroupName = "02. 진입 — 상승추세", Order = 10)]
		public bool UseUpLong { get; set; }

		// ── 03. 진입 — 하락 전환 의심 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매도: 고가가 몇 배 밴드에 닿으면 (UpSusShortBand)", Description = "하락 전환 의심 매도의 밴드 조건. 명세 1(+1배). 0 = 중심선", GroupName = "03. 진입 — 하락 전환 의심", Order = 0)]
		public int UpSusShortBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매수: 종가가 몇 배 밴드 아래면 (UpSusLongBand)", Description = "하락 전환 의심 매수의 밴드 조건. 명세 2(종가가 −2배 아래). 3이면 더 깊을 때만", GroupName = "03. 진입 — 하락 전환 의심", Order = 1)]
		public int UpSusLongBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매도 사용 (UseUpSusShort)", Description = "명세 켬. 끄면 하락 전환 의심 매도를 하지 않음", GroupName = "03. 진입 — 하락 전환 의심", Order = 10)]
		public bool UseUpSusShort { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매수 사용 (UseUpSusLong)", Description = "명세 켬. 끄면 하락 전환 의심 매수를 하지 않음. 꺾이는 중에 사는 진입", GroupName = "03. 진입 — 하락 전환 의심", Order = 11)]
		public bool UseUpSusLong { get; set; }

		// ── 04. 진입 — 하락추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매도: 고가가 몇 배 밴드에 닿으면 (DnShortBand)", Description = "하락추세 매도의 밴드 조건. 명세 1(+1배). 0 = 중심선(얕은 반등에도 진입)", GroupName = "04. 진입 — 하락추세", Order = 0)]
		public int DnShortBand { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[명세] 역추세 매수 사용 (UseCounterTrend)", Description = "하락추세 매수를 할지. 명세 켬. 끄면 하락추세에서는 매도만", GroupName = "04. 진입 — 하락추세", Order = 1)]
		public bool UseCounterTrend { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 역추세 매수: 저가가 몇 배 밴드에 닿으면 (DnLongBand)", Description = "하락추세 매수의 밴드 조건. 명세 3(−3배). 아래 RSI 조건도 같이 필요", GroupName = "04. 진입 — 하락추세", Order = 2)]
		public int DnLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[명세] 역추세 매수: RSI가 이 값 아래일 때만 (DnLongRsi)", Description = "밴드에 닿은 봉의 RSI가 이 값 미만이어야 함. 명세 30. 내리면 더 과매도일 때만", GroupName = "04. 진입 — 하락추세", Order = 3)]
		public double DnLongRsi { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매도 사용 (UseDnShort)", Description = "명세 켬. 끄면 하락추세 매도를 하지 않음", GroupName = "04. 진입 — 하락추세", Order = 10)]
		public bool UseDnShort { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "[추가] 역추세 매수 수량 % (CounterQtyPercent)", Description = "하락추세 매수는 진입 계약 수의 이 %만 들어감. 명세 100(같은 수량)", GroupName = "04. 진입 — 하락추세", Order = 11)]
		public int CounterQtyPercent { get; set; }

		// ── 05. 진입 — 상승 전환 의심 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매수: 저가가 몇 배 밴드에 닿으면 (DnSusLongBand)", Description = "상승 전환 의심 매수의 밴드 조건. 명세 1(−1배). 0 = 중심선", GroupName = "05. 진입 — 상승 전환 의심", Order = 0)]
		public int DnSusLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매도: 종가가 몇 배 밴드 위면 (DnSusShortBand)", Description = "상승 전환 의심 매도의 밴드 조건. 명세 2(종가가 +2배 위). 3이면 더 높을 때만", GroupName = "05. 진입 — 상승 전환 의심", Order = 1)]
		public int DnSusShortBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매수 사용 (UseDnSusLong)", Description = "명세 켬. 끄면 상승 전환 의심 매수를 하지 않음", GroupName = "05. 진입 — 상승 전환 의심", Order = 10)]
		public bool UseDnSusLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매도 사용 (UseDnSusShort)", Description = "명세 켬. 끄면 상승 전환 의심 매도를 하지 않음. 반등하는 중에 파는 진입", GroupName = "05. 진입 — 상승 전환 의심", Order = 11)]
		public bool UseDnSusShort { get; set; }

		// ── 06. 진입 — 횡보 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[명세] 매수·매도: 저가(고가)가 몇 배 밴드에 닿으면 (SideBand)", Description = "횡보 매수·매도의 밴드 조건. 명세 2(±2배). 밴드 조건 없이 MACD 전환 + 크로스가 같이 나와도 진입함(명세)", GroupName = "06. 진입 — 횡보", Order = 0)]
		public int SideBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매수 사용 (UseSideLong)", Description = "명세 켬. 끄면 횡보 매수를 하지 않음", GroupName = "06. 진입 — 횡보", Order = 10)]
		public bool UseSideLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 매도 사용 (UseSideShort)", Description = "명세 켬. 끄면 횡보 매도를 하지 않음", GroupName = "06. 진입 — 횡보", Order = 11)]
		public bool UseSideShort { get; set; }

		// ── 07. 진입 — 공통 (반전 신호와 대기) ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 반전 신호를 기다리는 봉 수 (T1Window)", Description = "밴드 조건 뒤 반전 신호(MACD 전환 또는 크로스)를 기다리는 봉 수. 기본 5. 실제로는 여기에 (MACD 확인 봉 수 − 1)을 더함(기본이면 6봉). 늘리면 진입이 늘지만 밴드에서 멀어진 신호도 받음", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 0)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] MACD 전환 확인 봉 수 (MacdConfirmBars)", Description = "진입 봉의 MACD 히스토그램이 몇 봉 연속 커져야(작아져야) 전환으로 볼지. 명세 2. 전환은 그것이 처음 확인된 봉 하나에서만 참", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 1)]
		public int MacdConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 밴드에 닿은 그 봉의 반전 신호도 인정 (T1SameBar)", Description = "명세 끔(다음 봉부터의 반전 신호만 셈). 켜면 밴드에 닿은 봉에서 반전 신호가 같이 나올 때 바로 진입", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 13)]
		public bool T1SameBar { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 추세가 바뀌어도 같은 방향의 대기는 유지 (KeepWaitOnTrendChange)", Description = "명세 끔. 켜면 반전 신호를 기다리는 중에 추세가 바뀔 때, 그 대기를 새 추세의 같은 방향 규칙으로 이어 감. 새 규칙의 밴드 조건이 더 깊어도 이어받으므로 그만큼 느슨해짐. 하락추세 역추세 매수로는 이어 가지 않음", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 14)]
		public bool KeepWaitOnTrendChange { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 종가가 이미 1차 익절 밴드를 넘었으면 진입 안 함 (SkipEntryPastTarget)", Description = "명세 끔. 반전 신호가 늦게 확인돼 신호 봉 종가가 첫 익절 밴드를 이미 넘은 경우 진입하지 않음. 끄면 그대로 진입하고 첫 조각이 진입가 근처에서 바로 익절돼 나감", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 15)]
		public bool SkipEntryPastTarget { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] MACD + 크로스 동시 조건: MACD가 꺾인 봉의 크로스도 인정 (T2CountConfirmBars)", Description = "회의 전(10/9)부터 켜 둔 것. MACD 전환은 2봉 연속이어야 확인되므로 실제로 꺾인 봉은 확인 봉보다 앞임. 켜면 그 앞 봉들에서 나온 크로스도 같이 나온 것으로 인정. 진입과 청산 모두에 적용", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 17)]
		public bool T2CountConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 손실 손절 뒤 같은 방향 재진입 금지 (BlockReentryAfterStop)", Description = "회의 전부터 켜 둔 것. 주력 진입(상승추세 매수, 하락추세 매도)이 아닌 진입이 손절 주문으로 손실 청산되면 그 대회 구간에서는 같은 방향의 주력 아닌 진입을 다시 하지 않음. 조건 청산(전량 청산 조건)으로 진 것은 해당하지 않음", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 18)]
		public bool BlockReentryAfterStop { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] MACD 전환 최소 변화폭, ATR 배수 (MacdMinChangeAtr)", Description = "전환으로 인정할 최소 변화폭. 0(기본) = 사용 안 함. 0.02~0.05면 미세한 흔들림을 거름", GroupName = "07. 진입 — 공통 (반전 신호와 대기)", Order = 19)]
		public double MacdMinChangeAtr { get; set; }

		// ── 08. 손절 ──
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[명세] 손절 % (StopPercent)", Description = "진입가에서 이 %만큼 불리한 가격. 명세 0.2(MNQ 31,000이면 62포인트). 전저점·전고점과 이 값 중 한쪽이 손절가", GroupName = "08. 손절", Order = 0)]
		public double StopPercent { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[명세] 전저점·전고점 손절 사용 (UseSwingStop)", Description = "명세 켬. 끄면 손절 %만 사용", GroupName = "08. 손절", Order = 1)]
		public bool UseSwingStop { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 전저점·전고점을 찾는 봉 수 (SwingBars)", Description = "신호 봉을 포함한 최근 몇 봉의 최저가·최고가를 쓸지. 명세 5", GroupName = "08. 손절", Order = 2)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[명세] 손절은 둘 중 먼 쪽으로 (StopWider)", Description = "명세 켬: 전저점·전고점과 손절 % 중 진입가에서 먼 쪽(여유 있게). 끄면 가까운 쪽(타이트하게). 1분봉에서는 0.2%가 거의 항상 더 멀어서, 켜면 손절이 사실상 0.2%로 고정됨", GroupName = "08. 손절", Order = 3)]
		public bool StopWider { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] 손절 최소 거리, ATR 배수 (MinStopAtr)", Description = "회의 전부터 1로 써 온 것. 손절이 이보다 가까우면 이 거리까지 벌림. 0 = 사용 안 함. 손절 %를 0으로 두고 이 값만 넣으면 ATR 배수만으로 손절함", GroupName = "08. 손절", Order = 10)]
		public double MinStopAtr { get; set; }

		// ── 09. 손절 이동 (본절) ──
		// 1차 익절이 체결되면 손절을 평단으로 옮기는 것은 명세이고 항상 적용된다 (설정값 없음)
		[NinjaScriptProperty]
		[Display(Name = "[추가] 2차 익절 때 손절을 1차 익절가로 (TrailStopAtTp2)", Description = "명세 끔(회의 문서에 '고민'으로만 있음). 켜면 두 번째 밴드 익절이 체결될 때 남은 수량의 손절을 첫 번째 익절이 체결된 가격으로 옮김. 1차 익절 때 손절을 평단으로 옮기는 것은 명세라 항상 적용됨", GroupName = "09. 손절 이동 (본절)", Order = 0)]
		public bool TrailStopAtTp2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] 이익 보호 본절: 손절 거리의 몇 배 이익에서 (BreakevenAtR)", Description = "명세 0(사용 안 함). 1이면 1차 익절 전이라도 이익이 처음 손절 거리만큼 나면 손절을 평단으로 옮김. 주력 진입(상승추세 매수, 하락추세 매도)은 제외", GroupName = "09. 손절 이동 (본절)", Order = 1)]
		public double BreakevenAtR { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[안전] 손절을 옮길 때 현재가와 띄울 최소 틱 수 (BreakevenBufferTicks)", Description = "손절을 평단이나 1차 익절가로 옮길 때, 현재 호가가 그 가격에서 이 틱 수 이상 떨어져 있어야 옮김. 가까우면 미뤘다가 봉 마감마다 다시 시도. 스탑 주문 거부를 막는 거리. MNQ 8틱 = 2포인트. 키우면 본절 이동이 늦어지는 대신 옮기자마자 손절되는 일이 줄어듦", GroupName = "09. 손절 이동 (본절)", Order = 10)]
		public int BreakevenBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[안전] 주문 오류를 전략이 직접 처리 (HandleOrderErrors)", Description = "켜 둘 것. 주문이 거부돼도 전략이 꺼지지 않고 직접 대처: 손절 가격 변경이 거부되면 기존 손절을 유지하고 다시 시도, 손절 주문 자체가 거부되면 즉시 전량 청산. 끄면 NinjaTrader 기본 동작(포지션을 닫고 전략 종료)", GroupName = "09. 손절 이동 (본절)", Order = 11)]
		public bool HandleOrderErrors { get; set; }

		// ── 10. 익절·청산 ──
		// 단계별 익절 밴드와 수량 비율은 명세 5장 표대로 코드(Plan)에 있다
		[NinjaScriptProperty]
		[Display(Name = "[명세] 밴드 익절을 봉 진행 중에 바로 (IntrabarTakeProfit)", Description = "명세 켬. 단계별 밴드 익절을 지정가로 걸어 두어 가격이 밴드에 닿는 즉시 체결. 밴드 값은 직전에 마감된 봉 기준이고 봉마다 옮김. 끄면 봉 마감 종가로 판단해 시장가", GroupName = "10. 익절·청산", Order = 0)]
		public bool IntrabarTakeProfit { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 마지막 조각 청산 이동평균 (ExitSmaPeriod)", Description = "진입 봉의 이동평균. 명세 18. 매도 포지션의 마지막 조각은 종가가 이 선 위에서, 횡보 매수의 마지막 조각은 아래에서 마감하면 청산", GroupName = "10. 익절·청산", Order = 1)]
		public int ExitSmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 추세가 포지션과 맞지 않게 바뀌면 전량 청산 (ExitOnTrendChange)", Description = "명세 끔(추세 방향 포지션이 전환 의심으로 바뀔 때만 청산). 켜면 (1) 상승추세 매수는 추세가 상승추세가 아니게 되면, 하락추세 매도는 하락추세가 아니게 되면 청산. (2) 그 밖의 포지션은 반대 방향 추세가 확정되면 청산. 하락추세 역추세 매수는 제외", GroupName = "10. 익절·청산", Order = 10)]
		public bool ExitOnTrendChange { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 의심 진입이 추세로 확정되면 추세 규칙의 익절점으로 (AdoptTrendExits)", Description = "명세 끔. 켜면 전환 의심에서 들어간 포지션이 같은 방향 추세로 확정될 때(매도 → 하락추세, 매수 → 상승추세) 남은 조각의 익절 밴드와 청산 조건을 그 추세 규칙으로 바꿈", GroupName = "10. 익절·청산", Order = 11)]
		public bool AdoptTrendExits { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 마지막 조각의 이동평균 조건을 진입 직후부터 (LastChunkEarly)", Description = "명세 끔(앞 단계의 밴드 익절이 모두 체결된 뒤부터 봄). 켜면 진입 직후부터 보고, 조건이 맞으면 마지막 조각만 먼저 청산. 진입 직후 손실로 나가는 일이 생김", GroupName = "10. 익절·청산", Order = 12)]
		public bool LastChunkEarly { get; set; }

		// ── 11. 수량·대회 시간 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 진입 계약 수 (EntryQuantity)", Description = "한 번 진입할 때의 마이크로 계약 수. 기본 30. 분할 비율(1/3·1/4·1/5·1/6)은 내림하고 나머지는 마지막 조각에 붙음. 60이면 모두 나눠떨어짐. 대회 한도가 나오면 맞춤", GroupName = "11. 수량·대회 시간", Order = 0)]
		public int EntryQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 대회 한도 계약 수 (MaxQuantity)", Description = "진입 계약 수가 이보다 크면 시작할 때 경고만 찍음. 매매에는 쓰이지 않음", GroupName = "11. 수량·대회 시간", Order = 1)]
		public int MaxQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[명세] 진입 시작 시각 (ContestStartTime)", Description = "HHmmss, 한국시간. 213000 = 21:30. 이 시각부터의 신호로 진입", GroupName = "11. 수량·대회 시간", Order = 2)]
		public int ContestStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[명세] 진입 마감 시각 (EntryEndTime)", Description = "HHmmss, 한국시간. 231500 = 23:15 (자정 뒤면 1500 = 00:15처럼 씀). 이 시각 뒤의 신호로는 진입하지 않음", GroupName = "11. 수량·대회 시간", Order = 3)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[명세] 전량 청산 시각 (FlattenTime)", Description = "HHmmss, 한국시간. 232700 = 23:27 (자정 뒤면 2700 = 00:27처럼 씀). 이 시각의 봉이 마감되면 남은 수량을 전량 청산", GroupName = "11. 수량·대회 시간", Order = 4)]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "[표시] 최소 진입 목표 횟수 (MinEntries)", Description = "화면 오른쪽 위의 진입 횟수 표시에만 쓰임", GroupName = "11. 수량·대회 시간", Order = 5)]
		public int MinEntries { get; set; }

		// ── 12. 지표 계산값 (보통 그대로 둠). 진입 봉과 추세 봉에 같은 값을 쓴다 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 밴드 중심선 EMA 기간 (BandMidPeriod)", Description = "ATR 채널 중심선. 명세 26", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 0)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 밴드 ATR 기간 (BandAtrPeriod)", Description = "명세 14", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 1)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[명세] 1배 밴드의 ATR 배수 (BandMult1)", Description = "명세 1", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 2)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[명세] 2배 밴드의 ATR 배수 (BandMult2)", Description = "명세 2", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 3)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[명세] 3배 밴드의 ATR 배수 (BandMult3)", Description = "명세 3", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 4)]
		public double BandMult3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] MACD 단기 (MacdFast)", Description = "명세 12", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 5)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] MACD 장기 (MacdSlow)", Description = "명세 26", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 6)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] MACD 시그널 (MacdSmooth)", Description = "명세 9", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 7)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 스토캐스틱 %K 길이 (StochPeriodK)", Description = "명세 10", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 8)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 스토캐스틱 %K 스무딩 (StochSmooth)", Description = "명세 5", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 9)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 스토캐스틱 %D 스무딩 (StochPeriodD)", Description = "명세 5", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 10)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[명세] 스토캐스틱 과매도 기준 (CrossLow)", Description = "골든크로스는 %K가 이 값 아래였던 뒤에만 인정. 명세 20. 올리면 골든크로스가 더 자주 인정됨", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 11)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[명세] 스토캐스틱 과매수 기준 (CrossHigh)", Description = "데드크로스는 %K가 이 값 위였던 뒤에만 인정. 명세 80. 내리면 데드크로스가 더 자주 인정됨", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 12)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] 크로스 인정 기한 봉 수 (CrossFilterBars)", Description = "위의 '아래(위)였던 적'을 현재 봉 포함 최근 몇 봉에서 찾을지. 명세 5", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 13)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[명세] RSI 기간 (RsiPeriod)", Description = "명세 14", GroupName = "12. 지표 계산값 (보통 그대로 둠)", Order = 14)]
		public int RsiPeriod { get; set; }

		// ── 13. 화면·기록·테스트 (매매 판단과 무관) ──
		[NinjaScriptProperty]
		[Display(Name = "[표시] 차트에 상태 표시 (ShowStatus)", Description = "추세 배경색, 밴드, 밴드 조건 마름모, 체결 라벨, 손절선, 왼쪽 위 상태 상자를 차트에 표시", GroupName = "13. 화면·기록·테스트", Order = 0)]
		public bool ShowStatus { get; set; }

		[NinjaScriptProperty]
		[Range(50, 300)]
		[Display(Name = "[표시] 상태 상자 크기 % (StatusPanelScale)", Description = "왼쪽 위 상태 상자의 글자·여백·폭 크기. 기본 130. 100이면 본문 12 / 추세 이름 15 / 폭 560px. 화면에 따라 키우거나 줄임", GroupName = "13. 화면·기록·테스트", Order = 5)]
		public int StatusPanelScale { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 로그를 파일로 저장 (LogToFile)", Description = "실시간·Playback 로그를 내 문서\\NinjaTrader 8\\TQ_log_종목.txt에 이어서 저장. 백테스트에서는 저장하지 않음", GroupName = "13. 화면·기록·테스트", Order = 1)]
		public bool LogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 학습 데이터를 파일로 저장 (MlLogToFile)", Description = "거래 1건마다 진입 신호 봉의 지표 상태와 그 거래의 결과(손익, 최대 유리폭·불리폭, 보유 봉 수, 청산 순서)를 TQ_ml_종목.csv에 저장. 백테스트에서도 저장. 최적화(Optimize)를 돌릴 때는 끔", GroupName = "13. 화면·기록·테스트", Order = 3)]
		public bool MlLogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[테스트] 주문 테스트용 가짜 진입 (UseTestEntry)", Description = "실제 신호를 무시하고 진입 시간대에 포지션이 없을 때마다 매수해 주문 흐름만 확인. 평소와 대회 때는 반드시 끔", GroupName = "13. 화면·기록·테스트", Order = 4)]
		public bool UseTestEntry { get; set; }

		// ── 14. 유기 모드 (회의 문서에 없는 실험. 켜면 진입 방향·손절·본절을 추세에 맞물리게 바꾼다) ──
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[추가] 유기 모드 사용 (OrganicMode)", Description = "명세 끔. 켜면 추세가 진입 방향을 정함: 상승추세는 매수만, 하락추세는 매도만(역추세 매수 없음). 하락 전환 의심에서는 하락추세 매도 규칙으로 매도만, 상승 전환 의심에서는 상승추세 매수 규칙으로 매수만 하고 수량은 아래 %로 줄임(역진입). 횡보는 명세 그대로. 손절은 0.2% 대신 아래 ATR 배수, 본절 이동은 1차 익절이 충분히 멀 때만, 2차 익절 때 손절을 1차 익절가로. 매수 보유 중 하락 전환 의심·하락추세가 되면(매도는 반대) 전량 청산. 이미 1차 익절 밴드를 넘은 가격에는 진입하지 않음. 켜면 무시되는 설정: 03·05 묶음의 의심 진입 밴드, 하락추세 역추세 매수, 08 손절 묶음, 2차 익절 손절 이동, 추세 바뀌면 전량 청산, 익절점 갈아타기", GroupName = "14. 유기 모드 (실험)", Order = 0)]
		public bool OrganicMode { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[추가] 유기 모드: 전환 의심 때 역진입 수량 % (OrganicSuspectQtyPercent)", Description = "유기 모드에서 전환 의심 상태의 진입(하락 전환 의심의 매도, 상승 전환 의심의 매수)은 진입 계약 수의 이 %만 들어감. 기본 50. 0이면 전환 의심 상태에서는 진입하지 않음", GroupName = "14. 유기 모드 (실험)", Order = 1)]
		public int OrganicSuspectQtyPercent { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[추가] 유기 모드: 추세가 확정되는 봉에 바로 진입 (OrganicEnterOnConfirm)", Description = "기본 켬. 추세가 상승추세(하락추세)로 바뀐 것을 처음 본 봉에서, 밴드 조건과 반전 신호를 기다리지 않고 그 방향으로 진입. 추세를 만든 반전 신호는 추세가 확정되기 전에 이미 지나가 있어서, 기다리면 강한 추세에서는 진입이 나오지 않기 때문. 종가가 이미 1차 익절 밴드를 넘었으면 진입하지 않음. 끄면 추세 안에서 밴드 조건 + 반전 신호가 나올 때만 진입", GroupName = "14. 유기 모드 (실험)", Order = 2)]
		public bool OrganicEnterOnConfirm { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 손절 거리, ATR 배수 (OrganicStopAtr)", Description = "유기 모드의 손절가 = 진입가에서 신호 봉 ATR × 이 값만큼 불리한 가격. 기본 2. 익절 밴드(ATR 1·2·3배)와 같은 단위라 손절과 익절의 비율이 일정해짐. 키우면 손절이 드물지만 한 번의 손실이 커짐", GroupName = "14. 유기 모드 (실험)", Order = 3)]
		public double OrganicStopAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 본절 이동에 필요한 1차 익절 폭 (OrganicBeMinR)", Description = "1차 익절의 이익이 손절 거리의 이 배수 이상일 때만 손절을 평단으로 옮김. 기본 0.5. 1차 익절이 진입가 바로 옆인데 평단으로 옮겨 곧바로 청산되는 것을 막음. 0이면 명세처럼 1차 익절 때 항상 옮김", GroupName = "14. 유기 모드 (실험)", Order = 4)]
		public double OrganicBeMinR { get; set; }

		[NinjaScriptProperty]
		[Range(0, 5)]
		[Display(Name = "[추가] 유기 모드: 횡보 진입을 막는 반대 방향 점수 (OrganicSideBlockScore)", Description = "횡보로 판정된 구간이라도 추세 봉의 지표 점수(가격·MACD·RSI·스토캐스틱·사건을 각각 +1/−1로 센 합, −5~+5. 사건 봉 수가 0이면 −4~+4)가 이 값 이상 상승 쪽이면 횡보 매도를, 이 값 이상 하락 쪽이면 횡보 매수를 하지 않음. 기본 2. 0이면 막지 않음. 점수는 화면 둘째 줄과 로그의 추세 봉 마감 줄에 나옴", GroupName = "14. 유기 모드 (실험)", Order = 5)]
		public int OrganicSideBlockScore { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 유기 모드: 진입 시간이 시작될 때 이미 추세면 그 봉에서 진입 (OrganicEnterAtOpen)", Description = "기본 끔. 추세 확정 봉 진입은 추세가 바뀐 그 봉에서만 나가므로, 진입 시작 시각 전에 이미 확정된 추세에는 들어갈 기회가 없음. 켜면 진입 시간대의 첫 봉에서 상승추세면 매수, 하락추세면 매도로 바로 진입(추세가 확정되는 봉에 바로 진입 옵션이 켜져 있을 때만). 종가가 이미 1차 익절 밴드를 넘었으면 진입하지 않음", GroupName = "14. 유기 모드 (실험)", Order = 6)]
		public bool OrganicEnterAtOpen { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 유기 모드: 추세가 유지되는 동안은 1분봉 전량 청산 조건을 무시 (OrganicHoldWhileTrend)", Description = "기본 끔. 상승추세 매수에는 명세의 전량 청산 조건(진입 봉에서 MACD 하락 전환 + 데드크로스)이 있어서, 추세 봉의 판정이 여전히 상승추세인데도 1분봉의 작은 눌림에 포지션이 통째로 청산됨. 하락추세 매도에는 이런 조건이 없어 매도만 길게 감. 켜면 매수 보유 중 판정이 상승추세이거나 상승 전환 의심인 동안(매도는 하락추세이거나 하락 전환 의심인 동안)은 이 조건을 무시하고, 추세가 바뀌면 그때 청산. 전환 의심 때의 역진입도 같이 다룸(10/14부터). 손절과 단계별 익절, 마지막 조각 조건은 그대로", GroupName = "14. 유기 모드 (실험)", Order = 7)]
		public bool OrganicHoldWhileTrend { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[추가] 유기 모드: 두 단계 진입 (OrganicTwoStage)", Description = "기본 끔. 추세 봉과 같은 방식의 추세를 진입 봉(1분봉)에서도 계산해서, 1분봉이 먼저 상승추세가 되면(추세 봉은 횡보나 상승 전환 의심) 아래 %만 매수하고, 추세 봉이 상승추세로 확정되면 나머지를 추가 매수(매도는 거울상). 추가는 진입 시간 안이고 밴드 익절이 아직 하나도 체결되지 않았고 종가가 1차 익절 밴드를 넘지 않았을 때만. 확정 전에 1분봉 추세가 꺼지면(매수는 1분봉이 상승추세·하락 전환 의심이 아니게 되면) 1단계 수량을 청산. 익절·청산은 상승추세 매수(5.1)·하락추세 매도(5.4) 규칙. 1단계는 주력 진입이 아니라서 손실 손절 뒤 재진입 제한을 받음. 추세 봉이 1(진입 봉)이면 쓰지 않음", GroupName = "14. 유기 모드 (실험)", Order = 8)]
		public bool OrganicTwoStage { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "[추가] 유기 모드: 두 단계 진입의 1단계 수량 % (OrganicStage1Percent)", Description = "1단계에서 진입 계약 수의 이 %만 들어감. 기본 50. 나머지는 추세 봉이 확정될 때 추가. 1단계 조각도 익절 단계 비율대로 나누므로 작으면 단계가 0계약이 될 수 있음", GroupName = "14. 유기 모드 (실험)", Order = 9)]
		public int OrganicStage1Percent { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[추가] 유기 모드: 손절을 직전 저점·고점 기준으로 (OrganicStopSwing)", Description = "기본 끔(진입가에서 ATR × 손절 배수). 켜면 손절가 = 직전 저점 한 틱 아래(매도는 고점 한 틱 위). 직전 저점은 진입 봉 최근 SwingBars봉(08 묶음, 기본 5)과 추세 봉 최근 아래 봉 수를 합쳐 가장 낮은 값. 거리는 진입 봉 ATR × 최소~최대 배수 안으로 자름. 켜면 위의 '손절 거리, ATR 배수'는 쓰지 않음", GroupName = "14. 유기 모드 (실험)", Order = 10)]
		public bool OrganicStopSwing { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 구조 손절 — 추세 봉 몇 개 안의 저점·고점 (OrganicStopSwingBars)", Description = "기본 5(3분봉이면 15분). 늘리면 더 바깥 저점을 쓰므로 손절이 멀어짐(최대 배수에서 잘림)", GroupName = "14. 유기 모드 (실험)", Order = 11)]
		public int OrganicStopSwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 구조 손절 — 최소 거리, ATR 배수 (OrganicStopMinAtr)", Description = "기본 1.5. 직전 저점이 진입가에 너무 가까우면 이 거리까지 벌림", GroupName = "14. 유기 모드 (실험)", Order = 12)]
		public double OrganicStopMinAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 구조 손절 — 최대 거리, ATR 배수 (OrganicStopMaxAtr)", Description = "기본 3. 직전 저점이 진입가에서 너무 멀면 이 거리까지만. 한 번의 손실을 제한함", GroupName = "14. 유기 모드 (실험)", Order = 13)]
		public double OrganicStopMaxAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[추가] 유기 모드: 추세 규칙 1차 익절 수량 % (OrganicTp1Percent)", Description = "기본 0. 상승추세 매수(+2배)·하락추세 매도(−2배)의 1차 익절 수량을 진입 계약 수의 %로. 0과 0이면 규칙 표 비율(매수 20/40/나머지 40, 매도 33/33/33). 2차 익절에 비중을 두려면 예: 1차 20, 2차 50 → 마지막 조각 30. 합이 100을 넘으면 규칙 표 비율. 횡보 규칙에는 적용하지 않음", GroupName = "14. 유기 모드 (실험)", Order = 14)]
		public int OrganicTp1Percent { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[추가] 유기 모드: 추세 규칙 2차 익절 수량 % (OrganicTp2Percent)", Description = "기본 0. 상승추세 매수(+3배)·하락추세 매도(−3배)의 2차 익절 수량 %. 1차와 2차를 빼고 남은 %가 마지막 조각", GroupName = "14. 유기 모드 (실험)", Order = 15)]
		public int OrganicTp2Percent { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 이익 따라가기 청산, ATR 배수 (OrganicTrailAtr)", Description = "기본 0(사용 안 함). 상승추세 매수·하락추세 매도 규칙의 포지션(역진입·두 단계 진입 포함)은 1차 익절이 체결된 뒤 손절을 '진입 뒤 최고가 − 진입 봉 ATR × 이 값'으로 따라 올림(매도는 최저가 + ATR × 값). 손절은 유리한 쪽으로만 움직임. 추세 봉 판정이 늦게 바뀌는 동안 이익을 돌려주는 것을 줄임. 3 안팎부터 시험", GroupName = "14. 유기 모드 (실험)", Order = 16)]
		public double OrganicTrailAtr { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[추가] 유기 모드: 역진입은 의심 뒤 가격 확인으로 (OrganicSuspectTrigger)", Description = "기본 끔(의심 상태에서 밴드 조건 + 반전 신호로 역진입). 켜면 전환 의심이 뜬 동안에만 기다렸다가, 진입 봉 종가가 직전 SwingBars봉(기본 5)의 저점 아래로 내려가면 매도(하락 전환 의심), 고점 위로 올라가면 매수(상승 전환 의심). 의심 한 번에 한 번만. 밴드 조건 역진입은 하지 않음. 수량은 역진입 수량 %. 맞는 의심도 먼저 반대로 20~40포인트 갔다가 맞는 경우가 많아서(10/8 22:27), 가격이 꺾인 것을 보고 들어가기 위함", GroupName = "14. 유기 모드 (실험)", Order = 17)]
		public bool OrganicSuspectTrigger { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창의 잠금 상태를 다시 그린다
		[Display(Name = "[추가] 유기 모드: 반전 신호 큐 (OrganicSignalQueue)", Description = "기본 끔. 켜면 추세와 상관없이 진입 봉의 반전 신호(어느 규칙이든 매도 쪽·매수 쪽 진입 신호)를 큐에 모아 두고, 전환 의심이 뜨면 큐에 유효한 반대쪽 신호가 있을 때 새 신호를 기다리지 않고 바로 역진입(하락 전환 의심 → 매도, 상승 전환 의심 → 매수). 의심 구간 하나에 한 번. 손절은 그 신호의 전고점(매도)·전저점(매수) 한 틱 바깥, 거리는 구조 손절의 최소~최대 ATR 배수 안. 큐 신호는 아래 유효 봉 수가 지나거나, 그 고점을 넘는 고가(매수 신호는 저점을 깨는 저가)가 나오거나, 아래 멀어짐 한도를 넘으면 무효. 상승추세 중 꼭지에서 나온 매도 신호가 3분봉 의심보다 1~8분 먼저 나오는 경우가 많아서(10/5~10/9 의심 27건 중 21건)", GroupName = "14. 유기 모드 (실험)", Order = 18)]
		public bool OrganicSignalQueue { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 큐 신호의 유효 봉 수 (OrganicQueueBars)", Description = "기본 10(1분봉이면 10분). 신호가 나온 뒤 이 봉 수가 지나면 큐에서 뺌", GroupName = "14. 유기 모드 (실험)", Order = 19)]
		public int OrganicQueueBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[추가] 유기 모드: 큐 신호의 멀어짐 한도, ATR 배수 (OrganicQueueMaxAtr)", Description = "기본 1.5. 종가가 신호 가격에서 진입 방향으로(매도 신호면 아래로) 진입 봉 ATR × 이 값보다 멀리 가 버리면 그 신호는 무효. 이미 움직인 뒤에 들어가 손절은 멀고 익절은 가까운 진입을 막음. 0이면 이 조건을 쓰지 않음", GroupName = "14. 유기 모드 (실험)", Order = 20)]
		public double OrganicQueueMaxAtr { get; set; }

		// ── 00. 설정 세트 ──
		// 켜면 그 세트의 값들을 한 번에 넣고 설정창에서 잠근다. 전략을 다시 올릴 때마다 여러 값을 손으로 넣다가 빠뜨리는 것을 막는다
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]	// 바꾸면 설정창을 다시 그려 잠금 상태와 값을 갱신한다
		[Display(Name = "[세트] 유기 기준 세트 적용 (PresetOrganicBase)", Description = "기본 끔(회의 문서 규칙 그대로). 켜면 10/14 마지막 Playback 설정을 한 번에 넣고 잠금: 추세 봉 3분, 추세 판단 방식 2(점수), 의심의 RSI 기준을 RSI 중심 대비로 켬(중심 14봉, 폭 6), 의심 점수 상한 2, 유기 모드 켬, 추세가 유지되는 동안 1분봉 전량 청산 조건 무시 켬, 두 단계 진입 켬, 구조 손절 켬, 추세 규칙 익절 30% / 40%, 이익 따라가기 ATR 2, 의심 뒤 가격 확인 역진입 켬, 반전 신호 큐 켬. 나머지 설정(1단계 50%, 구조 손절 5봉·1.5~3 ATR, 큐 10봉·1.5 ATR 등)은 기본값 그대로이고 건드리지 않음. 끄면 값은 그대로 남고 잠금만 풀림", GroupName = "00. 설정 세트", Order = 0)]
		public bool PresetOrganicBase
		{
			get { return presetOrganicBase; }
			set
			{
				presetOrganicBase = value;
				if (value)
					ApplyPreset();
			}
		}
		private bool presetOrganicBase;

		// 아래는 "버튼"이다. 설정창에는 버튼이 없어서 체크 상자로 만든다: 체크하면 그 동작을 한 번 하고 스스로 체크가 풀린다.
		// 값을 저장하지 않으므로 [NinjaScriptProperty]를 붙이지 않고 XML에도 쓰지 않는다 (전략을 복제·저장할 때 동작이 다시 실행되지 않게)
		[XmlIgnore]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[버튼] 회의 문서 기본값으로 되돌리기 — 체크하면 실행", Description = "체크하면 매매 규칙 설정을 모두 회의 문서 규칙의 기본값으로 되돌리고, 유기 기준 세트도 끔. 실행한 뒤 체크는 스스로 풀림. 수량·대회 시간(11번 묶음)과 화면·기록·테스트(13번 묶음)는 건드리지 않음. 결과는 아래 '마지막 동작'에 나옴", GroupName = "00. 설정 세트", Order = 1)]
		public bool PresetApplySpec
		{
			get { return ButtonRead("spec"); }
			set { ButtonWrite("spec", value); }
		}

		// 지금 설정을 한 벌 저장·불러오는 버튼은 2026-10-14에 뺐다. 설정창 아래의 template 저장·불러오기(NinjaTrader 기능)를 쓴다
		[XmlIgnore]
		[Display(Name = "[표시] 마지막 동작", Description = "위 버튼이 마지막으로 한 일과 그 결과. 직접 바꿀 수 없음", GroupName = "00. 설정 세트", Order = 4)]
		public string PresetStatus
		{
			get { return presetStatus; }
		}
		private string presetStatus = "";

		// 버튼의 값. 설정창은 값을 넣은 뒤 다시 읽어서 "값이 바뀌었을 때만" 화면을 다시 그린다.
		// 그래서 누른 직후 처음 한 번은 true로 읽히게 하고(화면 갱신), 그다음부터는 false로 읽히게 한다(체크가 풀림).
		// 2초가 지나면 무조건 false: 전략이 복제될 때 true가 넘어가 동작이 다시 실행되는 일을 막는다
		private string		buttonPressed = "";
		private DateTime	buttonPressedAt;

		private bool ButtonRead(string name)
		{
			if (buttonPressed != name)
				return false;
			buttonPressed = "";
			return (DateTime.Now - buttonPressedAt).TotalSeconds < 2;
		}

		private void ButtonWrite(string name, bool value)
		{
			if (!value)
			{
				if (buttonPressed == name)
					buttonPressed = "";
				return;
			}
			if (name == "spec")
				ApplySpecDefaults();
			buttonPressed	= name;
			buttonPressedAt	= DateTime.Now;
			LogPresetAction(name);
		}

		// 버튼이 실제로 실행됐는지 확인하는 기록 (내 문서\NinjaTrader 8\TQ_settings_log.txt). 설정창 동작은 Output 창에 남지 않아서 파일에 적는다
		private void LogPresetAction(string name)
		{
			try
			{
				System.IO.File.AppendAllText(
					System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "TQ_settings_log.txt"),
					string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} 버튼={1} 결과=[{2}] 세트={3} 유기모드={4} 추세봉={5} 추세방식={6} 인스턴스={7}{8}",
						DateTime.Now, name, presetStatus, presetOrganicBase, OrganicMode, RegimeBarMinutes, RegimeMode, GetHashCode(), Environment.NewLine),
					new System.Text.UTF8Encoding(false));
			}
			catch (Exception) { }
		}
		#endregion

		#region 설정 세트와 설정창 잠금
		// 유기 기준 세트의 값 (docs/status-1012.md의 "시험 중인 설정 세트"). 세트에 들어가는 설정은 PresetSettings와 같이 고친다
		// 2026-10-14 저녁 마지막 Playback 설정으로 바꿨다 (그 전: 추세 봉 3, 점수 방식, RSI 상대 기준 26/6, 유기 모드, 추세 유지 중 청산 무시)
		private void ApplyPreset()
		{
			RegimeBarMinutes		= 3;
			RegimeMode				= 2;
			RegimeRsiRelative		= true;
			RegimeRsiCenterBars		= 14;
			RegimeRsiOffset			= 6;
			RegimeSuspectMaxScore	= 2;
			OrganicMode				= true;
			OrganicHoldWhileTrend	= true;
			OrganicTwoStage			= true;
			OrganicStopSwing		= true;
			OrganicTp1Percent		= 30;
			OrganicTp2Percent		= 40;
			OrganicTrailAtr			= 2;
			OrganicSuspectTrigger	= true;
			OrganicSignalQueue		= true;
		}

		private static readonly string[] PresetSettings =
		{
			"RegimeBarMinutes", "RegimeMode", "RegimeRsiRelative", "RegimeRsiCenterBars", "RegimeRsiOffset", "RegimeSuspectMaxScore",
			"OrganicMode", "OrganicHoldWhileTrend", "OrganicTwoStage", "OrganicStopSwing", "OrganicTp1Percent", "OrganicTp2Percent",
			"OrganicTrailAtr", "OrganicSuspectTrigger", "OrganicSignalQueue"
		};

		// SetDefaults가 끝난 직후의 설정값 = 회의 문서 규칙의 기본값. "기본값으로 되돌리기" 버튼이 쓴다 (모든 인스턴스가 같으므로 한 번만 담는다)
		private static Dictionary<string, object> specDefaults;

		// 설정값 속성 목록. [NinjaScriptProperty]가 붙은 int·double·bool만 고른다.
		// rulesOnly면 수량·대회 시간(11번 묶음)과 화면·기록·테스트(13번 묶음)를 뺀다
		private List<System.Reflection.PropertyInfo> SettingProperties(bool rulesOnly)
		{
			List<System.Reflection.PropertyInfo> list = new List<System.Reflection.PropertyInfo>();
			foreach (System.Reflection.PropertyInfo p in typeof(TQ_Strategy).GetProperties(
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
			{
				if (!p.CanRead || !p.CanWrite)
					continue;
				if (p.GetCustomAttributes(typeof(NinjaScriptPropertyAttribute), false).Length == 0)
					continue;
				if (p.PropertyType != typeof(int) && p.PropertyType != typeof(double) && p.PropertyType != typeof(bool))
					continue;
				if (rulesOnly)
				{
					object[] display = p.GetCustomAttributes(typeof(DisplayAttribute), false);
					string group = display.Length > 0 ? ((DisplayAttribute)display[0]).GroupName ?? "" : "";
					if (group.StartsWith("11.") || group.StartsWith("13."))
						continue;
				}
				list.Add(p);
			}
			return list;
		}

		// SetDefaults 끝에서 부른다: 기본값을 담아 둔다
		private void RememberSpecDefaults()
		{
			if (specDefaults != null)
				return;
			Dictionary<string, object> values = new Dictionary<string, object>();
			foreach (System.Reflection.PropertyInfo p in SettingProperties(false))
				values[p.Name] = p.GetValue(this, null);
			specDefaults = values;
		}

		// [버튼] 회의 문서 기본값으로 되돌리기
		private void ApplySpecDefaults()
		{
			try
			{
				if (specDefaults == null)
				{
					presetStatus = "기본값으로 되돌리기 실패: 기본값을 아직 읽지 못함";
					return;
				}
				presetOrganicBase = false;
				int count = 0;
				foreach (System.Reflection.PropertyInfo p in SettingProperties(true))
				{
					object value;
					if (p.Name == "PresetOrganicBase" || !specDefaults.TryGetValue(p.Name, out value))
						continue;
					p.SetValue(this, value, null);
					count++;
				}
				presetStatus = string.Format("{0:HH:mm:ss} 회의 문서 기본값으로 되돌림 ({1}개. 수량·시간·화면 설정은 그대로)", DateTime.Now, count);
			}
			catch (Exception e)
			{
				presetStatus = "기본값으로 되돌리기 실패: " + e.Message;
			}
		}

		// 지금 설정에서 쓰이지 않거나 세트가 정해 둔 설정의 이름. 설정창에서 이 항목들은 회색으로 잠긴다 (TQ_StrategyConverter).
		// 매매 동작에는 영향이 없다. 값이 무시되는 조건을 코드의 실제 분기와 맞춰 둔다
		internal HashSet<string> LockedSettings()
		{
			HashSet<string> locked = new HashSet<string>();

			// 세트가 켜져 있으면 세트가 정한 값은 바꿀 수 없다
			if (PresetOrganicBase)
				foreach (string name in PresetSettings)
					locked.Add(name);

			if (OrganicMode)
			{
				// 유기 모드가 쓰지 않는 것: 의심 전용 규칙과 역추세 매수, 08 손절 묶음, 유기 모드가 자체 규칙으로 대신하는 옵션
				foreach (string name in new[] { "UpSusShortBand", "UpSusLongBand", "UseUpSusShort", "UseUpSusLong",
					"DnSusLongBand", "DnSusShortBand", "UseDnSusLong", "UseDnSusShort",
					"UseCounterTrend", "DnLongBand", "DnLongRsi", "CounterQtyPercent",
					"StopPercent", "UseSwingStop", "SwingBars", "StopWider", "MinStopAtr",
					"TrailStopAtTp2", "ExitOnTrendChange", "AdoptTrendExits", "SkipEntryPastTarget" })
					locked.Add(name);
				if (!OrganicEnterOnConfirm)
					locked.Add("OrganicEnterAtOpen");
				// 두 단계 진입: 추세 봉이 진입 봉이면(RegimeBarMinutes 1) 쓰지 않는다
				if (RegimeBarMinutes == 1)
					locked.Add("OrganicTwoStage");
				if (!TwoStageOn())
					locked.Add("OrganicStage1Percent");
				// 구조 손절: 켜면 ATR 배수 손절 대신 쓰고, 진입 봉 전저점 봉 수(SwingBars)를 같이 쓴다
				if (OrganicStopSwing)
				{
					locked.Add("OrganicStopAtr");
					locked.Remove("SwingBars");
				}
				else
				{
					locked.Add("OrganicStopSwingBars");
					// 최소·최대 거리는 큐 역진입의 손절에도 쓴다
					if (!OrganicSignalQueue)
					{
						locked.Add("OrganicStopMinAtr");
						locked.Add("OrganicStopMaxAtr");
					}
				}
				if (!OrganicSignalQueue)
				{
					locked.Add("OrganicQueueBars");
					locked.Add("OrganicQueueMaxAtr");
				}
			}
			else
			{
				// 유기 모드가 꺼져 있으면 그 세부 설정은 쓰이지 않는다
				foreach (string name in new[] { "OrganicSuspectQtyPercent", "OrganicEnterOnConfirm", "OrganicEnterAtOpen", "OrganicStopAtr",
					"OrganicBeMinR", "OrganicSideBlockScore", "OrganicHoldWhileTrend", "OrganicTwoStage", "OrganicStage1Percent",
					"OrganicStopSwing", "OrganicStopSwingBars", "OrganicStopMinAtr", "OrganicStopMaxAtr",
					"OrganicTp1Percent", "OrganicTp2Percent", "OrganicTrailAtr", "OrganicSuspectTrigger",
					"OrganicSignalQueue", "OrganicQueueBars", "OrganicQueueMaxAtr" })
					locked.Add(name);
				if (!UseCounterTrend)
					foreach (string name in new[] { "DnLongBand", "DnLongRsi", "CounterQtyPercent" })
						locked.Add(name);
				if (!UseSwingStop)
					foreach (string name in new[] { "SwingBars", "StopWider" })
						locked.Add(name);
			}

			// 점수 방식 전용 설정. 점수 자체는 유기 모드의 횡보 진입 필터도 쓰므로, RSI 폭과 사건 봉 수는 유기 모드가 켜져 있으면 열어 둔다
			if (RegimeMode != 2)
			{
				locked.Add("RegimeScoreMin");
				locked.Add("RegimeScoreExit");
				if (!OrganicMode)
				{
					locked.Add("RegimeScoreRsiBand");
					locked.Add("RegimeScoreEventBars");
				}
			}

			// 의심의 RSI 기준: 고정 기준과 상대 기준 중 쓰는 쪽만 연다
			if (RegimeRsiRelative)
			{
				locked.Add("RegimeRsiHigh");
				locked.Add("RegimeRsiLow");
			}
			else
			{
				locked.Add("RegimeRsiCenterBars");
				locked.Add("RegimeRsiOffset");
			}

			return locked;
		}
		#endregion
	}

	/// <summary>
	/// 설정창(속성 표)에서 쓰이지 않는 설정을 잠근다. TQ_Strategy.LockedSettings()가 돌려준 이름의 항목을 읽기 전용으로 바꿔 보여 준다.
	/// 전략용 변환기는 StrategyBaseConverter를 상속해야 한다 (NT8 공식 문서 "Using a TypeConverter to Customize Property Grid Behavior").
	/// 설정값을 바꿨을 때 표를 다시 그리려면 그 설정에 [RefreshProperties(RefreshProperties.All)]이 있어야 한다.
	/// </summary>
	public class TQ_StrategyConverter : StrategyBaseConverter
	{
		public override bool GetPropertiesSupported(ITypeDescriptorContext context) { return true; }

		public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attrs)
		{
			// 내장 지표(@Pivots.cs 등)와 같은 방식으로 기본 목록을 얻는다
			PropertyDescriptorCollection props = base.GetPropertiesSupported(context)
				? base.GetProperties(context, component, attrs)
				: TypeDescriptor.GetProperties(component, attrs);

			TQ_Strategy strategy = component as TQ_Strategy;
			if (strategy == null || props == null)
				return props;

			HashSet<string> locked;
			try
			{
				locked = strategy.LockedSettings();
			}
			catch (Exception)
			{
				return props;	// 잠금 계산에 실패해도 설정창은 원래대로 보여 준다
			}
			if (locked.Count == 0)
				return props;

			PropertyDescriptorCollection adjusted = new PropertyDescriptorCollection(null);
			foreach (PropertyDescriptor descriptor in props)
				adjusted.Add(locked.Contains(descriptor.Name) ? new TQ_ReadOnlyDescriptor(descriptor) : descriptor);
			return adjusted;
		}
	}

	/// <summary>
	/// 원래 속성 설명자를 감싸서 읽기 전용으로만 바꾼다. 값·이름·설명·묶음은 원래 것을 그대로 쓴다.
	/// </summary>
	public class TQ_ReadOnlyDescriptor : PropertyDescriptor
	{
		private readonly PropertyDescriptor inner;

		public TQ_ReadOnlyDescriptor(PropertyDescriptor descriptor) : base(descriptor.Name, CopyAttributes(descriptor))
		{
			inner = descriptor;
		}

		private static Attribute[] CopyAttributes(PropertyDescriptor descriptor)
		{
			Attribute[] attributes = new Attribute[descriptor.Attributes.Count];
			descriptor.Attributes.CopyTo(attributes, 0);
			return attributes;
		}

		public override bool IsReadOnly					{ get { return true; } }
		public override Type ComponentType				{ get { return inner.ComponentType; } }
		public override Type PropertyType				{ get { return inner.PropertyType; } }
		public override bool CanResetValue(object component)			{ return false; }
		public override object GetValue(object component)				{ return inner.GetValue(component); }
		public override void ResetValue(object component)				{ }
		public override void SetValue(object component, object value)	{ inner.SetValue(component, value); }	// 저장된 설정을 불러올 때는 값이 들어가야 한다
		public override bool ShouldSerializeValue(object component)		{ return inner.ShouldSerializeValue(component); }
	}
}
