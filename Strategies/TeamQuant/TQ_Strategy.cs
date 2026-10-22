#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;					// Brush, Brushes, Color (화면 표시)
using System.Xml.Serialization;				// XmlIgnore (설정창의 버튼·표시 전용 항목)
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;						// DashStyleHelper
using NinjaTrader.Gui.Chart;				// ChartControl, ChartScale (OnRender)
using NinjaTrader.Gui.Tools;				// SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.TeamQuant;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.TeamQuant
{
	/// <summary>
	/// 대회 전략 v3 (v3 지시서, docs/spec.md v3). 1분봉 차트에 올리고 추세 봉(3분·10분)은 내부에서 추가한다.
	/// 추세 판단은 TQ_Regime v3(느린 점수 S + 빠른 점수 F), 포지션은 4가지(추세 매수 TL·추세 매도 TS·횡보 매수 SL·횡보 매도 SS),
	/// 의심은 진입이 아니라 일부청산 이벤트, 장전·장후를 나눈다.
	/// 모든 규칙은 매수로 한 번만 정의하고 매도는 거울상이다 (4-0): 방향 부호 dir(+1 / −1) 하나로 한 경로만 쓴다.
	/// </summary>
	// 설정창에서 쓰이지 않는 항목을 잠근다 (TQ_StrategyConverter, 파일 맨 아래). NT8 공식 문서 "Using a TypeConverter to Customize Property Grid Behavior"
	[TypeConverter("NinjaTrader.NinjaScript.Strategies.TeamQuant.TQ_StrategyConverter")]
	public class TQ_Strategy : Strategy
	{
		private TQ_ATRChannels	channels;	// 진입 봉 밴드
		private TQ_Regime		regime;		// 추세 봉 추세 판단 (v3)
		private TQ_Signals		signals;	// 진입 봉 신호
		private ATR				atr;		// 진입 봉 ATR (손절·익절·트레일링 거리)
		private int				regimeBip	= 1;	// 추세를 판단하는 시리즈 번호: 3분봉 (BarsArray[1])
		private double			tkLastPx	= double.NaN;	// 현재가 청산: 마지막으로 본 체결가 (같은 가격이면 다시 계산 안 함)
		private double			tkSmaFast	= double.NaN, tkAtr = double.NaN, tkMacdSl = double.NaN;	// 현재가 F 계산용: 직전 1분봉 마감 때 값
		private double			tkEdgeLow	= double.NaN, tkEdgeHigh = double.NaN;	// 현재가 반대 돌파 기준: 마감된 직전 BreakoutBars봉 최저·최고
		private int				tickOppExitBar	= -10;	// 현재가 반대 돌파로 청산한 때의 CurrentBars[0] (그 봉이 마감될 때 새 진입 안 함)
		private int				minBars0;			// 진입 봉 데이터 부족 가드

		// ── 포지션 ──
		private enum Pos { None, TL, TS, SL, SS }	// 추세 매수 / 추세 매도 / 횡보 매수 / 횡보 매도 (4-1 원칙 3)
		private Pos				pos			= Pos.None;
		private int				dir;				// +1 매수, −1 매도
		private string			path		= "";	// 진입 경로 E1 ~ E4
		private string			session		= "";	// pre / post
		private bool			fallbackEntry;		// 최소 진입 보장으로 들어간 진입
		private bool			sawOwnTrend;		// 보유 중 추세가 포지션 방향이었던 적이 있음 (추세 종료 판정용)
		private int				entryCount	= 0;	// 이번 대회 구간의 진입 횟수 (분할 진입 여러 건은 1회)
		private MarketPosition	lastPosition = MarketPosition.Flat;
		private bool			wasInEntryWindow;

		// ── 분할 조각 ── 조각마다 진입 시그널명이 하나씩 있다 (예: TL1, TL2, TL3)
		private readonly List<string>				liveSignals		= new List<string>();				// 아직 보유 중인 조각
		private readonly List<string>				chunkOrder		= new List<string>();				// 이번 진입의 모든 조각 (단계 순서)
		private readonly Dictionary<string, int>	chunkQty		= new Dictionary<string, int>();
		private readonly Dictionary<string, int>	chunkStage		= new Dictionary<string, int>();	// 1 = 1차(1단계), 2 = 2차, 3 = 마지막 조각(횡보는 나머지)
		private readonly Dictionary<string, double>	chunkFixed		= new Dictionary<string, double>();	// 고정 가격 익절 (횡보 1단계, NaN = 없음)
		// 조각별 실제 보유 수량: 진입 체결로 늘고, 그 조각에서 나간 청산 체결(FromEntrySignal)로 준다. 0이 되면 조각이 끝난 것.
		// 진입·청산이 여러 번에 나눠 체결되면 NinjaTrader가 조각별로 기억하는 수량이 실제와 어긋날 수 있어(10/6 Playback: 9계약 보유에 11계약 청산) 청산 수량은 이 값으로 직접 준다
		private readonly Dictionary<string, int>	chunkLeft		= new Dictionary<string, int>();
		private readonly HashSet<string>			stageFilled		= new HashSet<string>();			// 익절 단계 처리를 이미 한 조각 (부분 체결이 여러 번 와도 한 번만)
		private readonly HashSet<string>			exitSent		= new HashSet<string>();			// 직접 청산 주문을 낸 조각 (늦게 온 진입 체결로 다시 살리지 않음)
		// 비관리형 주문 (2026-10-22): 진입 1개, 보호 손절 1개(실제 보유 수량 전체), 조각별 익절 지정가, 시장가 청산. Reconcile()이 보유 수량에 맞춤
		private Order							entryOrder;									// 진입 주문 (시장가 또는 E3 스탑)
		private Order							addOrder;									// 추가 진입 주문
		private Order							stopOrder;									// 보호 손절 (보유 수량 전체)
		private readonly Dictionary<string, Order>	tpOrders		= new Dictionary<string, Order>();	// 조각 → 익절 지정가
		private readonly Dictionary<string, List<string>>	exitOrderChunks	= new Dictionary<string, List<string>>();	// 시장가 청산 주문 이름 → 조각
		private readonly Dictionary<string, int>	chunkFilled		= new Dictionary<string, int>();	// 조각별 진입 체결 수량 (진입 체결을 조각 순서로 나눔)
		private int								plannedExitQty;								// 아직 체결 안 된 시장가 청산 수량 (손절 수량에서 뺌)
		private int								exitSeq;
		private string							tradePrefix		= "";						// 이번 거래 주문 이름 앞부분 (예: TL7.)
		private bool							inReconcile, reconcileAgain;
		private bool			tp1Done;
		private bool			tp2Done;
		private double			tp1FillPrice	= double.NaN;	// 1차 익절 체결가 (1차 뒤 손절 계산용)
		private int				signalSeq;						// 진입마다 하나씩 늘려 시그널명에 붙인다 (지난 거래의 Set 익절 값이 남지 않게)
		// 추가 진입 (spec 4-4-1, 사용자 결정 2026-10-15): 1차 익절 뒤 점수 속도가 포지션 방향으로 AddOnMinVel 이상이면 한 번
		private string			addSig			= "";			// 추가 진입 조각 시그널명 (없으면 "")
		private int				baseQty;						// 처음 진입 계약 수 (추가 수량 계산용)
		// E3 스탑 주문 진입 (spec 4-2 v6): 봉 마감에 박스 고가 + 1틱(매도는 저가 − 1틱)에 스탑 진입 주문을 걸고, 닿는 순간 체결
		private double			e3StopPx		= double.NaN;	// Enter가 이번에 거는 스탑 진입 가격 (NaN = 시장가 진입)
		private int				e3StopDir;						// 걸어 둔 E3 스탑 주문 방향 (0 = 없음)
		private string			e3StopPrefix	= "";			// 걸어 둔 E3 스탑 주문 시그널명 앞부분 (같은 방향이면 이름을 그대로 써서 주문을 고침)
		private bool			e3StopPlacedNow;				// 이번 봉에 다시 걸었음 (안 걸면 NinjaTrader가 봉 끝에 취소)

		private bool			earlyEntry;
		// 박스 장 감지와 하루 손실 (spec 4-11)
		private double			sessStartPx		= double.NaN;	// 대회 구간 첫 봉 종가
		private double			sessPrevPx;
		private double			sessPath;						// 봉마다 종가 변화의 합
		private int				sessBars;
		private double			sessPnlUsd;						// 이번 구간 끝난 거래 손익 합 ($)
		private bool			dayCutLogged;
		private MAX				fadeHigh;						// 진입 봉 고가의 RangeFadeBars봉 최고
		private MIN				fadeLow;						// 개장 뒤 EarlyWindowMinutes 안에 들어간 추세 진입 (E5 제외, spec 4-4-2)
		private double			addFillSum;						// 추가분 체결가 합 (손익은 추가분 체결가 기준으로 따로 셈)
		private int				addFillQty;
		private int				tpStagesHit;

		// ── 손절 ──
		private double			stopSwing		= double.NaN;	// 신호 봉 구조 저점(매수)·고점(매도)
		private double			stopAtr			= double.NaN;	// 신호 봉 ATR
		private double			stopPrice		= double.NaN;	// 지금 걸어 둔 손절가
		private double			initialStop		= double.NaN;	// 신호 봉 종가 기준 처음 손절가 (갭 판정용)
		private double			entryFillSum;
		private int				entryFillQty;
		private bool			gapFlattened;
		private bool			breakevenDone;
		private string			stopNote		= "";
		private double			deferredStop	= double.NaN;
		private double			lastStopTarget	= double.NaN;
		private string			lastStopLabel	= "";
		private double			trailExtreme	= double.NaN;	// 진입 뒤 최고가(매수)·최저가(매도)
		private bool			trailUsed;

		// ── 의심 이벤트 처리 (4-5) ──
		private int				suspectWaitLeft;				// 가격 확인 대기 남은 봉 수 (0 = 대기 없음)
		private int				suspectEventBar	= -1;			// 지금 다루는 의심 이벤트가 나온 추세 봉 번호
		private int				suspectDoneBar	= -1;			// 실행(또는 취소)이 끝난 의심 이벤트의 추세 봉 번호
		private DateTime		suspectEventTime;
		private int				suspectEventsSeen;				// 보유 중 본 반대 방향 의심 이벤트 수 (기록용)
		private string			suspectAction	= "";			// 기록용: SUSPECT / SUSPECT_STRONG / CANCEL

		// ── 추세 봉 관찰 ── 진입 봉에서 새 추세 봉 값을 처음 볼 때를 잡는다
		private int				lastRegimeBar	= -1;			// 의심 이벤트 번호: 이벤트를 본 진입 봉 번호 (의심 하나당 한 번 실행, 4-5)
		private int				trendStartNow;					// 이 진입 봉에서 처음 본 추세 시작 (±1, 없으면 0)
		private int				suspectEventNow;				// 이 진입 봉에서 처음 본 의심 이벤트
		private DateTime		lastTrendStartTime;
		private int				lastTrendStartDir;
		private int				e1AfterExitDir;					// 반대 추세 시작으로 청산한 뒤 다음 봉에 허용하는 E1 방향 (사용자 결정 2026-10-15)
		private int				e1AfterExitBar	= -1;
		private const int		E1AfterExitBars	= 2;			// 청산 체결을 기다리는 1봉 + 여유 1봉
		private int				reaccelNow;						// 이 진입 봉에서 처음 본 추세 재가속 (±1, E1Reaccel, 사용자 결정 2026-10-15)
		private bool			reaccelDipUp;					// 상승추세 중 F가 0 이하로 내려간 적이 있음 (재가속 대기)
		private bool			reaccelDipDn;
		// 재가속 기준은 추세 시작 기준과 따로 고정: 시작 기준을 1로 내려도(2026-10-15) 재가속은 처음 정한 대로 "F가 다시 ±2, S ±2"
		private const int		ReaccelFast		= 2;
		private const int		ReaccelSlow		= 2;

		// ── 연속 점수 추세 판정 (RegimeMode 2, spec 3-3) ── 항목마다 −1~+1 연속값(소수 6자리), 1분봉마다 현재가로 다시 계산
		private ATR				regimeAtr;						// 추세 봉 ATR(14): 항목을 ATR 단위로 나눌 때
		private Series<double>	contHist;						// 1분봉마다 점수 C (속도 = C − ScoreVelBars봉 전 C)
		private double			contScore;						// C = 항목 11개 평균 (−1~+1)
		private double			contVel;						// 속도
		private double			contSlow;						// 연속 S = S1~S4 연속값 합 (−4~+4)
		private double			contFast;						// 연속 F = F1·F2 연속값 합 (−2~+2)
		private int				contState;						// +1 / 0 / −1 (진입용 판정)
		private int				contExitState;
		private int				r7;								// 7레짐: 0 횡보, ±1 시작의심, ±2 추세, ±3 종료의심 (부호 = 방향)
		private int				r7Since			= -1;			// 추세(±2)가 된 진입 봉 번호 (종료의심 동안에도 이어 셈)
		private int				r7Age			= -1;
		private int				r7Warn;							// 전환의심: 추세·눌림 중 반대 방향 빠른 출발이면 그 반대 방향(±1), 아니면 0 (spec 3-4)
		private bool			warnDone;						// 이 포지션에서 전환의심 일부청산을 했음			// 추세 확정 뒤 지난 진입 봉 수 (추세·종료의심일 때만, 아니면 −1)					// 청산용 판정: 속도 시작 없음, 끝 = C ≤ ScoreExitOff (TREND_END·OPP_TREND)
		private int				contSuspect;					// 의심 중 방향 (−1 하락의심, +1 상승의심)
		private int				contFPrev;						// 직전 1분봉의 F(정수 환산)
		private bool			contReady;

		// ── v4 돌파 구조 (docs/spec.md 4-9) ── 진입 방향은 가격 돌파 + 빠른 점수, 느린 점수는 강한 반대만 거른다
		private MAX				rangeHigh;						// 진입 봉 고가의 BreakoutBars봉 최고 ([1] = 직전 봉까지, 지금 봉 제외)
		private MIN				rangeLow;
		private int				fastCrossNow;					// 이 진입 봉에서 처음 본 추세 봉에서 F가 −SuspectFast 아래로(−1) / 위로(+1) 넘어감
		private int				lastBreakoutDir;				// 표시용: 마지막 돌파 신호 방향과 시각
		private DateTime		lastBreakoutTime;

		// ── 대회 구간 ──
		private double			boxHigh			= double.NaN;	// 장전 박스 (21:30~22:30 진입 봉 최고가·최저가)
		private double			boxLow			= double.NaN;
		private DateTime		boxStartTime;
		private int				orbCountLong;					// 이번 구간 E3 매수 횟수
		private int				orbCountShort;
		private bool			orbArmedLong	= true;			// E3 매수가 열려 있음: 구간 시작 때, 그리고 E3 뒤 종가가 박스 안으로 돌아오면 (OrbMaxEntries, 사용자 결정 2026-10-15)
		private bool			orbArmedShort	= true;
		// E5 개장 범위 돌파 (spec 4-10): 개장 시각 봉부터 E5RangeMinutes개 진입 봉의 최고가·최저가
		private double			orHigh			= double.NaN;
		private double			orLow			= double.NaN;
		private bool			orLongUsed;						// 이번 구간 E5 매수를 썼음 (방향별 하루 한 번)
		private bool			orShortUsed;
		private bool			e5Trail;						// E5 보유: E5BreakevenR에 닿아 본전으로 옮겼고 이제 트레일링
		private bool			blockSideLong;					// 횡보 매수 손실 손절 뒤 이번 구간 횡보 매수 금지
		private bool			blockSideShort;
		private string			sessionTag		= "";

		// ── 기록 ──
		private string			logPath;
		private string			mlPath;
		private string			mlRunId			= "";
		private string			mlSettingsId	= "";
		private bool			mlHeaderWritten;
		private string			mlFeatures		= "";
		private double			mlPrevR			= double.NaN;
		private DateTime		mlPrevExit		= DateTime.MinValue;
		private bool			trOpen;
		private DateTime		trExitTime;
		private double			trStop0			= double.NaN;
		private double			trPnlPts;
		private double			trMfe;
		private double			trMae;
		private int				trBars;
		private string			trExits			= "";
		private string			exitReason		= "";			// 마지막 청산 사유 코드 (7장)
		private double			rrExpected		= double.NaN;	// 진입 때 계산한 손익비 (E1)

		// ── 화면 ──
		private Brush			upBackBrush;
		private Brush			downBackBrush;
		private Brush			upSuspectBrush;
		private Brush			upStartBrush;					// 7레짐 시작의심 (옅은 초록·빨강)
		private Brush			downStartBrush;
		private Brush			downSuspectBrush;

		#region 고정값 (v6 박스 장 대책, spec 4-11)
		private const double RangeFadeMaxEr		= 0.15;	// 박스 장 감지: 대회 구간 시작부터 지금까지 종가 효율비 ≤ 이 값
		private const int RangeFadeMinBars		= 60;	// 감지는 대회 구간 시작 뒤 이 봉 수부터 (22:30)
		private const int RangeFadeBars			= 30;	// 가짜 돌파 기준 범위 봉 수
		private const int RangeFadeMaxLag		= 3;	// 범위를 깬 봉이 지금 봉 기준 1~3봉 전
		private const double RangeFadeTargetR	= 1.0;	// 익절 R
		private const double RangeFadeStopBuf	= 0.25;	// 손절 = 최근 4봉 극값 ∓ 이 값 × ATR
		private const double RangeFadeStopMinAtr	= 1.0;
		private const double RangeFadeStopMaxAtr	= 2.5;
		private const int RangeFadeMaxHold		= 20;	// 최대 보유 봉 수
		private const int DayLossQtyPercent		= 50;	// 하루 손실 한도를 넘으면 그 뒤 수량 %
		private const int E5EndTime				= 225000;	// spec 4-10: E5는 이 시각 봉 전까지 (28일: 22:35~44 26건 +10.2R, 22:45 뒤 12건 +0.3R)
		private const int NormalEntryEndTime	= 230000;	// spec 4-7: 이 시각 봉부터는 최소 진입 보장 진입만 (18일: 23:00~23:15 B1·E6 6건 −5.6R)
		// spec 3-4 7레짐 (2026-10-16 사용자 제안): 횡보 / 시작의심 / 추세 / 종료의심 (위·아래)
		private const double R7EndSuspectC		= 0.1;	// 추세 중 C가 이 값 아래로(또는 속도가 반대로 ScoreVelMin) → 종료의심
		private const int E1DelayBars			= 3;	// E1 = 추세 확정 뒤 이 봉 수째 진입 봉 (18일: 0분(지금) $41,253, 1분 $46,290, 2분 $43,960, 3분 $55,839, 5분 $41,606)
		// spec 4-8-1 진입 품질 점수 (2026-10-16 사용자 결정): 7·10월 18일 후보 352개에서 두 달 모두 같은 방향이던 정보만. 구간 경계와 값은 두 달 합쳐 만든 것
		private const double QualityHalfBelow	= -0.05;	// 점수가 이 값보다 낮으면 수량 50% (다른 달로 만든 점수로 18일: 7월 $44,966 → $39,466, 10월 $10,808 → $14,526)
		private const double SizeFloorPercent	= 25;		// spec 4-8: 수량을 줄이는 이유(장전·달린 뒤·하루 손실·품질)는 곱하되 최대 계약의 이 % 아래로는 안 줄임
		private static readonly double[] QF		= { 0.7056, 1.2511, 0.0143, 0.0738, -0.0861 };	// 빠른 F: 경계 2개, 낮은⅓·중간·높은⅓ 값
		private static readonly double[] QExt	= { 0.8550, 1.8225, 0.0915, 0.0813, -0.1714 };	// 중심선 거리 ATR
		private static readonly double[] QR10	= { 3.0649, 3.9154, -0.1736, 0.0797, 0.0932 };	// 최근 10봉 고저폭 ÷ ATR
		private static readonly double[] QPre	= { 1.1770, 2.6459, -0.0443, 0.1236, -0.0800 };	// 진입 전 15분 이동 ÷ ATR
		private static readonly double[] QRoom	= { -0.0269, 0.8333, -0.2536, 0.1931, 0.0610 };	// 30봉 고·저점까지 남은 거리 ÷ 예상 손절 거리
		private const int ForceStartTime		= 232000;	// spec 5 최소 진입 강제 채우기: 이 시각 봉부터 종료 청산 전까지
		private const double Tp1TrailAtr		= 3.0;	// spec 4-4: 1차 익절 뒤 트레일링 = 진입 뒤 최고가 − 이 값 × ATR (본전보다 나을 때부터, 2차 뒤는 TrailAtr)
		#endregion

		#region 고정값 (v6 정리, 2026-10-15 사용자 결정: 설정창에는 정말 조절할 값만. 값은 정리 전 기본값 그대로, 근거는 spec)
		private const int RegimeBarMinutes = 3;
		private const double ScoreScale = 0.9;
		private const double ScoreTrendOff = 0.0;
		private const int ScoreVelBars = 6;
		private const int RegimeFast = 6;
		private const int RegimeMid = 20;
		private const int RegimeSlow = 60;
		private const int TrendStartFast = 1;
		private const int TrendStartSlow = 1;
		private const int TrendEndSlow = 0;
		private const int RegimeMacdSlopeBars = 2;
		private const int RegimeS2Mode = 1;
		private const int RegimeSlopeBars = 5;
		private const bool RegimeUseRsi = true;
		private const double RegimeRsiBand = 5;
		private const int SuspectFast = 2;
		private const bool SuspectNeedPrice = true;
		private const int SuspectConfirmBars = 5;
		private const int SuspectExitPercent = 50;
		private const int SuspectStrongSlow = 1;
		private const int SuspectStrongExitPercent = 100;
		private const bool BlockEntryWhileSuspect = true;
		private const bool TrendEntryNeedFast = true;
		private const int TrendPullbackBand = 0;
		private const int PullbackMinSlow = 2;
		private const int OrbEndTime = 230000;
		private const int OrbMinSlow = 1;
		private const int OrbMinFast = 1;
		private const int OrbMaxEntries = 2;
		private const double E5BreakevenR = 1.0;
		private const double E5StopMinAtr = 1.0;
		private const double E5StopMaxAtr = 4.0;
		private const int BreakoutBars = 15;
		private const int BreakoutMinFast = 1;
		private const int BreakoutBoxBars = 30;
		private const double BreakoutMaxEr = 0.25;
		private const bool BreakoutExitOnOpposite = true;
		private const double EntryMinRR = 1.0;
		private const bool E1Reaccel = true;
		private const int SideBand = 2;
		private const bool SideT2Entry = true;
		private const int SideBlockScore = 3;
		private const bool BlockSideAfterStop = true;
		private const int T1Window = 5;
		private const int MacdConfirmBars = 2;
		private const bool T2CountConfirmBars = true;
		private const int SwingBars = 5;
		private const double TrendStopMinAtr = 1.5;
		private const double TrendStopMaxAtr = 3.0;
		private const double SideStopMinAtr = 1.0;
		private const double SideStopMaxAtr = 2.0;
		private const int BreakevenBufferTicks = 8;
		private const bool HandleOrderErrors = true;
		private const double BreakevenMinR = 0.5;
		private const double BreakevenFallbackAtr = 1.5;
		private const double BreakevenAtR = 0.8;
		private const int TrendTp1Band = 2;
		private const int TrendTp2Band = 3;
		private const int TrendTp1Percent = 33;
		private const int TrendTp2Percent = 33;
		private const int TrendLastExitMa = 1;
		private const bool TrendExitOnEnd = true;
		private const bool TrendExitOnEntryT2 = false;
		private const double AddOnMinVel = 0.3;
		private const int AddOnPercent = 50;
		private const int EarlyWindowMinutes = 5;
		private const double ExtendedEntryAtr = 2.0;
		private const int ExtendedQtyPercent = 50;
		private const int AddOnEndTime = 231000;
		private const double SideTp1Atr = 2;
		private const int SideTp1Percent = 33;
		private const int SideRestBand = 3;
		private const bool SideOscExitAfterTp1 = true;
		private const bool SideExitOnOppTrend = true;
		private const int OpenBlockBefore = 5;
		private const int OpenBlockAfter = 3;
		private const int PreOpenQtyPercent = 50;
		private const int PreOpenTrendMinSlow = 2;
		private const bool PreOpenAllowSide = true;
		private const bool MinEntryFallback = true;
		private const int MinEntryFallbackTime = 230000;
		private const int MinEntryQtyPercent = 30;
		private const bool UseRiskSizing = true;
		private const int MaxQuantity = 40;
		private const int BandMidPeriod = 26;
		private const int BandAtrPeriod = 14;
		private const double BandMult1 = 1;
		private const double BandMult2 = 2;
		private const double BandMult3 = 3;
		private const int MacdFast = 12;
		private const int MacdSlow = 26;
		private const int MacdSmooth = 9;
		private const int StochPeriodK = 10;
		private const int StochSmooth = 5;
		private const int StochPeriodD = 5;
		private const double CrossLow = 20;
		private const double CrossHigh = 80;
		private const int CrossFilterBars = 5;
		private const int RsiPeriod = 14;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 v5: v3 규칙 + 위험 금액 수량 + 설정 정리 (docs/spec.md, 브랜치 v5-clean)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;			// 4-1 원칙 1: 봉 마감 뒤 판단
				// 주문은 비관리형 (2026-10-22 사용자 결정: 확실한 방법으로). 관리형 Set 손절·익절은 부분 체결 때 조각마다 주문이 여러 개 생기고,
				// 한 조각을 청산하면 NinjaTrader가 다른 조각의 손절·익절을 취소해 손절 없는 계약과 남는 손절(반대 포지션 위험)이 생겼음 (10/15 22:06, 7/24 22:35)
				IsUnmanaged			= true;
				// 취소 중인 주문이 체결돼도(초과 체결) 전략을 멈추지 않고 Reconcile()이 계획에 없는 포지션을 시장가로 정리 (NT8 IgnoreOverfill 문서)
				IgnoreOverfill		= true;


				// 기본값 = v3 지시서 5-2 (설계안)
				// 01 추세 판단
				// spec 3-3 연속 점수 + 속도 판정 (v6 기본, 사용자 제안 2026-10-15). 1 = v3 TQ_Regime 판정
				ScoreTrendOn			= 0.2;
				ScoreVelMin				= 0.25;
				ScoreExitOff			= -0.1;
				// 02 의심 이벤트
				// 03 진입 — 추세
				// v6: 진입 경로 E1·E2·E3·E4 + B1 박스 돌파 켬 (진입 횟수를 줄이지 않고 B1 조건만 '박스에서 나오는 돌파'로 바꿈, spec 4-9)
				UseEntryTrendStart		= true;
				UseEntryPullback		= true;
				UseEntryOrb				= true;
				E3StopEntry				= true;
				// E5 개장 범위 돌파 (spec 4-10, 사용자 결정 2026-10-15): 28일 시뮬레이션 38건 +10.5R, 7월·10월 모두 이익
				UseEntryOpenRange		= true;
				UseEntryRangeFade		= true;
				DayLossLimitR			= 1.5;
				ForceMinEntries			= true;
				E5RangeMinutes			= 5;
				E5TargetR				= 2.0;
				// B1 박스 돌파 (4-9) — v6 기본 켬. 직전 BreakoutBoxBars봉 효율비 ≤ BreakoutMaxEr(박스)일 때 돌파 진입, 느린 점수는 안 봄(4)
				UseBreakoutEntry		= true;
				// 04 진입 — 횡보
				UseEntrySide			= true;
				// 05 진입 — 공통
				// 06 손절
				StopMinTicks			= 32;
				// 07 청산 — 추세
				TrailAtr				= 2.5;
				// spec 4-4 v6: 추세 익절을 손절 거리(R) 기준으로 (1 = 밴드 +2/+3배, 2 = R). 18일 시뮬레이션 두 달 모두 나음
				TrendTp1R				= 0.5;	// 2026-10-15 사용자 결정 1 → 0.5 (18일 시뮬레이션 합계 +23.0 → +24.5R, 밴드 +13.6R)
				TrendTp2R				= 2.5;
				UseAddOn				= true;
				B1OppTickExit			= true;
				// spec 4-4-2 개장 직후 진입 옵션 (기본 끔, 2026-10-15 사용자 결정: 옵션으로)
				EarlyFullExitAtTp1		= false;
				EarlyStopMult			= 1.0;
				// 08 청산 — 횡보
				// 09 시간대
				OpenTime				= 223000;
				NewsBlockMinutes		= 5;
				// 10 수량·대회 시간
				EntryQuantity			= 30;
				RiskPerTrade			= 2000;
				MinEntries				= 2;
				ContestStartTime		= 213000;
				EntryEndTime			= 231500;
				FlattenTime				= 232700;
				// 11 지표 계산값 (main 12번 묶음 그대로)
				// 12 화면·기록
				ShowStatus				= true;
				StatusPanelScale		= 130;
				LogToFile				= true;
				MlLogToFile				= true;

				// 여기까지가 설계안 기본값이다. "설계안 기본값으로 되돌리기" 버튼이 쓰도록 담아 둔다
				RememberDesignDefaults();
			}
			else if (State == State.Configure)
			{
				// 추세 봉 = 3분봉 → BarsArray[1] (v6 정리에서 10분봉 선택을 뺐음)
				AddDataSeries(BarsPeriodType.Minute, 3);

				// 주문 오류를 NinjaTrader 기본 동작(포지션을 닫고 전략 종료)에 맡기지 않고 OnOrderUpdate에서 직접 처리한다 (SetDefaults·Configure에서만 정할 수 있음)
				if (HandleOrderErrors)
					RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
			}
			else if (State == State.DataLoaded)
			{
				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				regime		= TQ_Regime(BarsArray[regimeBip], RegimeFast, RegimeMid, RegimeSlow, false, MacdFast, MacdSlow, MacdSmooth, RsiPeriod,
								RegimeS2Mode, RegimeSlopeBars, RegimeUseRsi, RegimeRsiBand, RegimeMacdSlopeBars,
								TrendStartFast, TrendStartSlow, TrendEndSlow, SuspectFast, false);	// DirectReverse: v5에서 설정을 뺐고 항상 끔
				regimeAtr	= ATR(BarsArray[regimeBip], 14);
				contHist	= new Series<double>(this);
				// v3는 EntryUpLong / EntryDnShort(추세 눌림, 밴드 = TrendPullbackBand), EntrySideLong / EntrySideShort(횡보, 밴드 = SideBand)만 쓴다.
				// 쓰지 않는 밴드 인자(의심 진입 4개, 역추세 매수)에는 main 기본값을 넘긴다: 2 / 1 / 3 / 30 / 1 / 2. 마지막 조각 이동평균은 쓰지 않아 main 기본 18
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, SwingBars,
								BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3,
								MacdFast, MacdSlow, MacdSmooth, StochPeriodK, StochSmooth, StochPeriodD,
								18, MacdConfirmBars, 0, false,
								TrendPullbackBand, 2, 1, TrendPullbackBand, 3, 30, 1, 2, SideBand,
								0, 0, 0, T2CountConfirmBars, false);
				atr			= ATR(BandAtrPeriod);

				minBars0	= Math.Max(Math.Max(Math.Max(BandMidPeriod, MacdSlow + MacdSmooth), Math.Max(BandAtrPeriod, SwingBars)), Math.Max(BreakoutBars, BreakoutBoxBars) + 1) + 5;
				rangeHigh	= MAX(Highs[0], BreakoutBars);
				rangeLow	= MIN(Lows[0], BreakoutBars);
				fadeHigh	= MAX(Highs[0], RangeFadeBars);
				fadeLow		= MIN(Lows[0], RangeFadeBars);

				// v6 브랜치는 TQ_log_v6_종목.txt, TQ_ml_v6_종목.csv로 따로 쓴다 (v3~v5 기록과 섞이지 않게)
				// 기록 파일: 내 문서\NinjaTrader 8\TQ_log_v3_종목.txt, TQ_ml_v3_종목.csv (v2 기록과 섞이지 않게 따로, 사용자 결정 2026-10-15)
				string docs	= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8");
				logPath		= System.IO.Path.Combine(docs, "TQ_log_v6_" + Instrument.MasterInstrument.Name + ".txt");
				mlPath		= System.IO.Path.Combine(docs, "TQ_ml_v6_" + Instrument.MasterInstrument.Name + ".csv");
				mlRunId		= DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
				string summary	= SettingsSummary();
				int cut			= summary.IndexOf("] ");
				mlSettingsId	= HashText(cut >= 0 ? summary.Substring(cut + 2) : summary);	// 종목 이름을 빼고 만든다 (월물이 달라도 같은 설정이면 같은 ID)

				if (ShowStatus)
					AddChartIndicator(channels);
				upBackBrush			= new SolidColorBrush(Color.FromArgb(30, 0, 200, 0));
				downBackBrush		= new SolidColorBrush(Color.FromArgb(30, 230, 0, 0));
				upSuspectBrush		= new SolidColorBrush(Color.FromArgb(30, 255, 165, 0));
				downSuspectBrush	= new SolidColorBrush(Color.FromArgb(30, 0, 160, 255));
				upBackBrush.Freeze();
				downBackBrush.Freeze();
				upSuspectBrush.Freeze();
				upStartBrush		= new SolidColorBrush(Color.FromArgb(12, 0, 200, 0));
				downStartBrush		= new SolidColorBrush(Color.FromArgb(12, 230, 0, 0));
				upStartBrush.Freeze();
				downStartBrush.Freeze();
				downSuspectBrush.Freeze();

				// 4-8: 보유 수량은 EntryQuantity를 넘지 않는다. 한도(MaxQuantity)보다 크면 경고만
				if (EntryQuantity > MaxQuantity)
					Out(string.Format("[{0}][TQ_Strategy] 경고: 진입 계약 수({1}) > 최대 보유 수량({2}, 대회 한도). 설정 확인 필요",
						Instrument.FullName, EntryQuantity, MaxQuantity));
			}
			else if (State == State.Realtime)
			{
				Out("==== " + SettingsSummary());
				ToRealtimeOrders();
			}
		}

		// 차트 왼쪽 위 이름표는 이름만 (설정값은 로그 첫 줄과 설정창에서 본다)
		public override string DisplayName
		{
			get { return "TQ_Strategy"; }
		}

		// 실행·설정 구분용 한 줄. 채점기(docs/tools/tq_regime_score.py --validate)가 "버전=v3"~"버전=v6"와 추세 판단 값을 읽는다
		private string SettingsSummary()
		{
			System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
			return "[" + Instrument.FullName + "] " + BarsPeriod.Value + "분봉 | 버전=v6"
				+ " | 판정=" + ("점수(기준x" + ScoreScale.ToString(inv) + " 켬" + ScoreTrendOn.ToString(inv) + " 끔" + ScoreTrendOff.ToString(inv)
					+ " 속도" + ScoreVelBars + "봉≥" + ScoreVelMin.ToString(inv) + " 청산끔" + ScoreExitOff.ToString(inv) + ")")
				+ " | 추세봉=" + RegimeBarMinutes + "분 MA=" + RegimeFast + "/" + RegimeMid + "/" + RegimeSlow
				+ " S2방식=" + RegimeS2Mode + " 기울기봉=" + RegimeSlopeBars + " RSI항목=" + RegimeUseRsi + " RSI폭=" + RegimeRsiBand.ToString(inv)
				+ " MACD기울기봉=" + RegimeMacdSlopeBars + " 시작F=" + TrendStartFast + " 시작S=" + TrendStartSlow + " 종료S=" + TrendEndSlow
				+ " | 의심F=" + SuspectFast + " 가격확인=" + SuspectNeedPrice + "(" + SuspectConfirmBars + "봉) 청산%=" + SuspectExitPercent
				+ " 강한의심S=" + SuspectStrongSlow + "(" + SuspectStrongExitPercent + "%) 의심중진입금지=" + BlockEntryWhileSuspect + " 추세진입F필요=" + TrendEntryNeedFast
				+ " | E1=" + UseEntryTrendStart + "(재가속=" + E1Reaccel + ") E2=" + UseEntryPullback + "(밴드" + TrendPullbackBand + " S" + PullbackMinSlow + ") E3=" + UseEntryOrb
				+ "(스탑주문=" + E3StopEntry + " ~" + OrbEndTime + " S" + OrbMinSlow + " F" + OrbMinFast + " 최대" + OrbMaxEntries + "번) 손익비=" + EntryMinRR.ToString(inv)
				+ " | 추세익절=" + TrendTp1R.ToString(inv) + "R/" + TrendTp2R.ToString(inv) + "R" + " 추가진입=" + UseAddOn
					+ "(속도≥" + AddOnMinVel.ToString(inv) + " " + AddOnPercent + "% ~" + AddOnEndTime + ")"
				+ " 개장직후(" + EarlyWindowMinutes + "분 1차전량=" + EarlyFullExitAtTp1 + " 손절x" + EarlyStopMult.ToString(inv) + ")"
				+ " 달린진입=" + ExtendedEntryAtr.ToString(inv) + "ATR→" + ExtendedQtyPercent + "%"
				+ " | E6박스되돌림=" + UseEntryRangeFade + "(효율비≤" + RangeFadeMaxEr.ToString(inv) + " " + RangeFadeBars + "봉 " + RangeFadeTargetR.ToString(inv) + "R " + RangeFadeMaxHold + "분)"
				+ " 하루손실한도=" + DayLossLimitR.ToString(inv) + "R→" + DayLossQtyPercent + "%"
				+ " 일반진입마감=" + NormalEntryEndTime + " 7레짐(종료의심C<" + R7EndSuspectC.ToString(inv) + " E1=확정" + E1DelayBars + "봉째)"
				+ " | E5개장범위=" + UseEntryOpenRange + "(~" + E5EndTime + " " + E5RangeMinutes + "분 익절" + E5TargetR.ToString(inv) + "R 본전" + E5BreakevenR.ToString(inv)
					+ "R 손절ATR" + E5StopMinAtr.ToString(inv) + "~" + E5StopMaxAtr.ToString(inv) + ")"
				+ " | B1박스돌파=" + UseBreakoutEntry + "(" + BreakoutBars + "봉 F" + BreakoutMinFast
				+ " 효율비≤" + BreakoutMaxEr.ToString(inv) + "(" + BreakoutBoxBars + "봉) 반대청산=" + BreakoutExitOnOpposite + " 현재가=" + B1OppTickExit + ")"
				+ " | E4=" + UseEntrySide + "(밴드" + SideBand + " T2=" + SideT2Entry + ") 횡보필터=" + SideBlockScore + " 횡보재진입금지=" + BlockSideAfterStop
				+ " | 대기=" + T1Window + " MACD확인=" + MacdConfirmBars + " T2앞봉인정=" + T2CountConfirmBars
				+ " | 손절봉=" + SwingBars + " 추세손절ATR=" + TrendStopMinAtr.ToString(inv) + "~" + TrendStopMaxAtr.ToString(inv)
				+ " 횡보손절ATR=" + SideStopMinAtr.ToString(inv) + "~" + SideStopMaxAtr.ToString(inv) + " 최소손절틱=" + StopMinTicks
				+ " 본절최소R=" + BreakevenMinR.ToString(inv) + " 모자라면ATR=" + BreakevenFallbackAtr.ToString(inv) + " 이익보호R=" + BreakevenAtR.ToString(inv)
				+ " | 추세조각=" + TrendTp1Percent + "/" + TrendTp2Percent + "% 트레일링ATR=" + TrailAtr.ToString(inv)
				+ " 마지막조각=" + TrendLastExitMa + " 추세종료청산=" + TrendExitOnEnd + " 1분T2청산=" + TrendExitOnEntryT2
				+ " | 횡보1단계=" + SideTp1Atr.ToString(inv) + "ATR " + SideTp1Percent + "% 전량밴드=" + SideRestBand
				+ " 오실레이터는1단계뒤=" + SideOscExitAfterTp1 + " 반대추세청산=" + SideExitOnOppTrend
				+ " | 개장=" + OpenTime + " 지표금지=" + NewsBlockMinutes + "분 개장전후금지=" + OpenBlockBefore + "/" + OpenBlockAfter
				+ " 장전수량%=" + PreOpenQtyPercent + " 장전추세S=" + PreOpenTrendMinSlow + " 장전횡보=" + PreOpenAllowSide
				+ " 최소진입보장=" + MinEntryFallback + "(" + MinEntryFallbackTime + " " + MinEntryQtyPercent + "%)"
				+ " | 수량=" + EntryQuantity + (UseRiskSizing ? "(위험 $" + RiskPerTrade.ToString(inv) + ")" : "(고정)") + " 시간=" + ContestStartTime + "/" + EntryEndTime + "/" + FlattenTime;
		}

		protected override void OnBarUpdate()
		{
			// 데이터 부족 가드: 진입 봉 지표와 추세 봉 판정이 채워지기 전에는 아무것도 하지 않는다
			if (CurrentBars[0] < minBars0 || CurrentBars[regimeBip] < 1 || double.IsNaN(regime.Regime[0]))
				return;

			// 추세 봉 마감: 기록만 한다. 판단은 다음 진입 봉 마감에서 (3분봉과 1분봉이 같은 시각에 닫히면 1분봉이 먼저 처리돼 1분 지연, 4-1 원칙 5)
			if (BarsInProgress == regimeBip)
			{
				LogRegimeBar();
				return;
			}
			if (BarsInProgress != 0)
				return;

			ObserveRegime();
			PaintBar();
			TradeTrack();
			UpdateSession();
			tkEdgeLow	= rangeLow[0];	// 다음 봉 진행 중 현재가 반대 돌파 기준 = 지금까지 마감된 BreakoutBars봉 (4-9의 '지금 봉 제외'와 같음)
			tkEdgeHigh	= rangeHigh[0];

			// 종료 청산: FlattenTime 봉이 마감되면 남은 포지션을 전량 시장가로
			if (IsFlattenTime())
			{
				if (Position.MarketPosition != MarketPosition.Flat && pos != Pos.None)
					FlattenRemaining("FLATTEN_TIME", "종료 청산 시각");
				else
					FlattenLeftover();
				LogEntryBar(null);
				UpdateStatusPanel(null);
				return;
			}

			List<Candidate> cands = null;
			bool force = ForceNeeded();
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				ResetTradeState();
				cands = EvaluateEntries();
				if (force)
					ForceEnter();
				else if (tickOppExitBar == CurrentBars[0] - 1)
				{
					ForgetE3Stop();
					Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 안 함: 이 봉 진행 중에 현재가 반대 돌파로 청산 (4-9: 청산한 봉에는 반대 진입 안 함)", Time[0], Instrument.FullName));
				}
				else if (InEntryWindow())
					RouteEntry(cands);
				else
					ForgetE3Stop();	// 진입 시간 밖: 걸어 둔 E3 스탑은 다시 걸지 않으니 봉 끝에 취소됨
			}
			else
			{
				if (force && !gapFlattened && pos != Pos.None)
				{
					// spec 5: 최소 진입을 채우려고 지금 포지션을 정리 (강제 진입이면 다음 봉에 바로 청산)
					FlattenRemaining(path == "FORCE" ? "FORCE_EXIT" : "FORCE_PREP",
						string.Format("최소 진입 {0}/{1}회 채우기", entryCount, MinEntries));
				}
				else if (gapFlattened || pos == Pos.None)
				{
					// 전량 청산 주문을 낸 뒤에도 포지션이 남아 있으면 다시 낸다 (거부·미체결·수량 어긋남)
					FlattenLeftover();
				}
				else if (!double.IsNaN(stopPrice) && Position.MarketPosition != MarketPosition.Flat && dir * (Close[0] - stopPrice) < 0 && UnprotectedQty() > 0)
				{
					// spec 4-3 안전장치: 종가가 이미 손절가를 넘었는데 포지션이 남음 = 손절 주문 수량이 보유 수량보다 적었음.
					// 7/24 22:35 E5 16계약이 4 + 12로 나뉘어 체결되고 손절은 12계약만 나가, 4계약이 손절 없이 23:27까지 남았음 (−1.57R)
					FlattenRemaining(breakevenDone ? "BE_STOP" : "STOP", string.Format("종가 {0}가 손절가 {1}를 넘었는데 {2}계약 남음", Close[0], stopPrice, Position.Quantity));
				}
				else
				{
					RetryDeferredStop();
					ProtectProfitCheck();
					UpdateTrailStop();
					UpdateTargets();
					if (IsTrendPos())	ManageTrendExits();
					else				ManageSideExits();
					WarnCheck();
					TryAddOn();
				}
			}

			Reconcile();	// 이번 봉에 바뀐 손절·익절·청산을 실제 주문에 반영
			LogEntryBar(cands);
			UpdateStatusPanel(cands);

			// 다음 봉의 SideSignal이 쓰는 직전 봉 횡보 대기 상태
			prevWaitSideLong	= signals.WaitSideLong;
			prevWaitSideShort	= signals.WaitSideShort;
		}

		// 4-1 원칙 2: 분할 진입 여러 건은 1회. 미보유 → 보유 전환 때만 센다. 보유 → 미보유가 되면 거래 기록을 쓴다
		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (lastPosition == MarketPosition.Flat && marketPosition != MarketPosition.Flat)
			{
				// E3 스탑 주문이 체결됐으면 그때 E3 횟수를 센다 (걸어 두기만 한 봉은 세지 않음)
				if (path == "E3" && e3StopDir != 0)
				{
					if (dir > 0)	{ orbCountLong++;	orbArmedLong = false; }
					else			{ orbCountShort++;	orbArmedShort = false; }
					Out(string.Format("[{0}][{1}][TQ_Strategy] E3 스탑 주문 체결 {2}", Time[0], Instrument.FullName, DirText(dir)));
					ForgetE3Stop();
				}
				entryCount++;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 신규 진입 #{2} 방향={3} 평균가={4} 수량={5}",
					Time[0], Instrument.FullName, entryCount, marketPosition, averagePrice, quantity));
			}
			if (lastPosition != MarketPosition.Flat && marketPosition == MarketPosition.Flat)
				TradeEnd();
			lastPosition = marketPosition;
			Reconcile();	// 포지션 수량이 바뀐 뒤 보호 주문을 다시 맞춤 (체결 콜백 안에서 포지션이 아직 안 바뀌었을 수 있음)
		}

		// 실시간·Playback 로그를 파일에도 남긴다 (LogToFile). 백테스트에서는 Output 창에만
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

		#region 추세 봉 관찰과 공통 헬퍼
		// 직전에 마감된 추세 봉의 추세: +1 상승, 0 횡보, −1 하락
		// spec 3-4: 진입 판정 = 7레짐의 추세·눌림 방향 (그 전에는 점수+속도 빠른 판정 contState). 시작의심·전환의심·횡보는 0
		private int RegimeState()	{ return contReady ? R7TrendDir() : 0; }

		// TQ_Regime(v3)의 판정 그대로 (추세 봉 마감 줄과 채점기 --validate는 항상 이 값)
		private int RawRegimeState()
		{
			double v = regime.Regime[0];
			return double.IsNaN(v) ? 0 : (int)v;
		}

		// 진입·청산 기준이 보는 S·F: 연속 S·F를 정수로 환산 "S ≥ k" ⇔ "연속 S ≥ ScoreScale × k" (spec 3-3)
		private int SlowNow()	{ return ScoreLevel(contSlow, RegimeUseRsi ? 4 : 3); }
		private int FastNow()	{ return ScoreLevel(contFast, 2); }

		private int ScoreLevel(double x, int top)
		{
			if (!contReady || ScoreScale <= 0)
				return 0;
			int n = Math.Min(top, (int)Math.Floor(Math.Abs(x) / ScoreScale + 1e-9));
			return x > 0 ? n : -n;
		}

		// 보유 포지션의 추세 청산(TREND_END·OPP_TREND)이 보는 흔들림에 둔한 청산용 판정 (spec 3-3)
		private int ExitState()	{ return contReady ? contExitState : 0; }

		private int SuspectActiveNow()	{ return contSuspect; }

		private static double R6(double x)	{ return Math.Round(x, 6); }

		// spec 3-3 연속 점수: 직전에 마감된 추세 봉의 이평·MACD·RSI·ATR과 지금 1분봉 종가로 항목 11개를 −1~+1로 만든다.
		// 가격 항목은 1분마다 바뀌고 이평 항목은 추세 봉마다 바뀜. 연구 스크립트(spec 3-3 근거)와 같은 식
		private void UpdateContScore()
		{
			int rb = regimeBip;
			contReady = CurrentBars[rb] >= 70 && CurrentBars[0] > ScoreVelBars;
			if (!contReady)
			{
				contHist[0] = 0;
				return;
			}
			double a	= Math.Max(regimeAtr[0], TickSize);
			double c	= Close[0];
			double sm	= regime.SmaMid[0], sf = regime.SmaFast[0], ss = regime.SmaSlow[0], m = regime.MacdLine[0];
			// 가격 항목 6개
			double pxMid	= R6(Math.Tanh((c - sm) / (1.5 * a)));
			double pxFast	= R6(Math.Tanh((c - sf) / (0.75 * a)));
			double roc5		= R6(Math.Tanh((c - Closes[rb][5]) / (2.0 * a)));
			double hh = c, ll = c;
			for (int k = 0; k < 20; k++)
			{
				hh = Math.Max(hh, Highs[rb][k]);
				ll = Math.Min(ll, Lows[rb][k]);
			}
			double don20	= R6(hh > ll ? 2 * (c - ll) / (hh - ll) - 1 : 0);
			const int n = 10;
			double[] ys = new double[n];
			for (int k = 0; k < n - 1; k++)
				ys[k] = Closes[rb][n - 1 - k];			// 추세 봉 [9]..[1]
			ys[n - 1] = c;								// 마지막 점 = 지금 가격
			double xm = (n - 1) / 2.0, ym = 0, num = 0, den = 0, path = Math.Abs(ys[0] - Closes[rb][n]);
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
			double er10		= R6(path > 0 ? (c - Closes[rb][n]) / path : 0);
			// 구조 2개, 모멘텀 3개
			double structS	= R6(Math.Tanh((sm - ss) / (2.0 * a)));
			double slope60	= R6(Math.Tanh((ss - regime.SmaSlow[5]) / (0.5 * a)));
			double macd		= R6(Math.Tanh(m / a));
			double macdSl	= R6(Math.Tanh((m - regime.MacdLine[2]) / (0.3 * a)));
			double rsi		= R6(Math.Max(-1.0, Math.Min(1.0, (regime.Rsi[0] - 50) / 20)));
			contScore	= R6((pxMid + pxFast + roc5 + don20 + lrs10 + er10 + structS + slope60 + macd + macdSl + rsi) / 11.0);
			contSlow	= R6(pxMid + structS + macd + (RegimeUseRsi ? rsi : 0));	// S1 종가−중기선, S2 중기−장기선, S3 MACD선, S4 RSI
			contFast	= R6(pxFast + macdSl);										// F1 종가−단기선, F2 MACD선 기울기
			tkSmaFast	= sf;
			tkAtr		= a;
			tkMacdSl	= macdSl;
			contHist[0]	= contScore;
			contVel		= R6(contScore - contHist[ScoreVelBars]);
		}

		// spec 3-3 판정 (1분봉마다): 상승 시작 = C ≥ ScoreTrendOn, 또는 C ≥ 0이면서 속도 ≥ ScoreVelMin. 상승 끝 = C ≤ ScoreTrendOff.
		// 상승 중 하락 시작 조건이면 바로 하락. 하락은 거울상. 의심 = F(정수 환산)가 추세 반대로 SuspectFast에 처음 닿은 1분봉
		private void ObserveCont()
		{
			int prev = contState;
			if (contReady)
			{
				bool upGo = contScore >= ScoreTrendOn || (contScore >= 0 && contVel >= ScoreVelMin);
				bool dnGo = contScore <= -ScoreTrendOn || (contScore <= 0 && contVel <= -ScoreVelMin);
				if (contState == 1)			contState = dnGo ? -1 : contScore <= ScoreTrendOff ? 0 : 1;
				else if (contState == -1)	contState = upGo ? 1 : contScore >= -ScoreTrendOff ? 0 : -1;
				else						contState = upGo ? 1 : dnGo ? -1 : 0;
			}
			// 청산용 판정 (spec 3-3): 시작 = C ≥ ScoreTrendOn만(속도 시작 없음), 끝 = C ≤ ScoreExitOff, 반대 시작이면 바로 반대
			int prevX = contExitState;
			if (contReady)
			{
				if (contExitState == 1)			contExitState = contScore <= -ScoreTrendOn ? -1 : contScore <= ScoreExitOff ? 0 : 1;
				else if (contExitState == -1)	contExitState = contScore >= ScoreTrendOn ? 1 : contScore >= -ScoreExitOff ? 0 : -1;
				else							contExitState = contScore >= ScoreTrendOn ? 1 : contScore <= -ScoreTrendOn ? -1 : 0;
			}
			if (contExitState != prevX)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 청산용 판정(점수) {2} → {3}  C={4:F3}", Time[0], Instrument.FullName, StateText(prevX), StateText(contExitState), contScore));
			if (contState != prev)
				contSuspect = 0;
			if (contState != 0 && contState != prev)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 추세 판정(점수) {2} → {3}  C={4:F3} 속도={5:F3} 연속S={6:F2} 연속F={7:F2}",
					Time[0], Instrument.FullName, StateText(prev), StateText(contState), contScore, contVel, contSlow, contFast));
			}
			else if (contState == 0 && prev != 0)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 추세 판정(점수) {2} → 횡보  C={3:F3} 속도={4:F3}", Time[0], Instrument.FullName, StateText(prev), contScore, contVel));
			int f = FastNow();
			if (f <= -SuspectFast && contFPrev > -SuspectFast)		fastCrossNow = -1;
			else if (f >= SuspectFast && contFPrev < SuspectFast)	fastCrossNow = 1;
			int stR = R7TrendDir();	// spec 3-4: 의심 이벤트도 7레짐 방향 기준
			if (stR != 0 && fastCrossNow == -stR)
			{
				suspectEventNow	= -stR;
				contSuspect		= -stR;
			}
			else if (contSuspect != 0 && stR * f >= 0)
				contSuspect = 0;
			contFPrev = f;
			Observe7Regime();
		}

		// spec 3-4 7레짐 (1분봉마다). 상승 기준, 하락은 거울상:
		//  횡보 → C ≥ ScoreTrendOn이면 상승추세, 아니고 빠른 시작(C ≥ 0 AND 속도 ≥ ScoreVelMin)이면 상승시작의심
		//  시작의심 → 반대 빠른 시작 또는 C ≤ −ScoreTrendOn이면 하락시작의심, C ≥ ScoreTrendOn이면 상승추세, C < 0이면 횡보
		//  추세 → C ≤ −ScoreTrendOn이면 하락추세, C < R7EndSuspectC 또는 속도 ≤ −ScoreVelMin이면 상승종료의심
		//  종료의심 → C ≤ −ScoreTrendOn이면 하락추세, C ≤ ScoreExitOff면 횡보, C ≥ ScoreTrendOn이고 속도 ≥ 0이면 상승추세(확정 시간 새로)
		// E1은 추세가 된 뒤 E1DelayBars봉째 (확정 순간은 가격이 한 번에 움직인 직후라 바로 따라가면 되돌림에 맞음:
		// v6 Playback 확정 0~5분 진입 21건 8승 −6.4R, 5~15분 10건 7승 +2.4R)
		private void Observe7Regime()
		{
			if (!contReady)
				return;
			int prev = r7;
			double C = contScore, V = contVel;
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
			int na = Math.Abs(r7);
			if (na == 2 && prev != r7)
				r7Since = CurrentBars[0];
			else if (na != 2 && na != 3)
				r7Since = -1;
			r7Age = r7Since >= 0 ? CurrentBars[0] - r7Since : -1;
			if (r7 != prev)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 7레짐 {2} → {3}  C={4:F3} 속도={5:F3}", Time[0], Instrument.FullName, R7Text(prev), R7Text(r7), C, V));
			// (전환의심은 아래에서 r7 방향 기준으로 다시 계산)
			// spec 3-4 전환의심: 추세·눌림 중 C가 반대쪽(0 넘음)이고 속도가 반대로 ScoreVelMin 이상 = 반대 방향 빠른 출발.
			// 18일: 이때 30분 뒤 원래 방향 −1.44 ATR(반대 2 ATR 먼저 56%), 그냥 약해진 눌림은 +0.94 ATR(원래 방향 먼저 61%)
			int d7 = R7TrendDir(), prevWarn = r7Warn;
			r7Warn = d7 != 0 && d7 * C <= 0 && d7 * V <= -ScoreVelMin ? -d7 : 0;
			if (r7Warn != prevWarn && r7Warn != 0)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 전환의심: {2}  C={3:F3} 속도={4:F3}", Time[0], Instrument.FullName, R7Display(), C, V));
			if (r7Age == E1DelayBars)
			{
				int dir7 = r7 > 0 ? 1 : -1;
				trendStartNow		= dir7;
				lastTrendStartTime	= Time[0];
				lastTrendStartDir	= dir7;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 추세 확정 {2}봉째 → E1 {3} 열림", Time[0], Instrument.FullName, E1DelayBars, dir7 > 0 ? "매수" : "매도"));
			}
		}

		private static string R7Text(int v)
		{
			string dir = v > 0 ? "상승" : "하락";
			switch (Math.Abs(v))
			{
				case 1:	return dir + " 시작의심";
				case 2:	return dir + "추세";
				case 3:	return dir + " 눌림";
				default:	return "횡보";
			}
		}

		// 7레짐 방향: 추세·눌림일 때만 ±1 (spec 3-4: 진입 판정 RegimeState와 E1이 이 값을 씀)
		private int R7TrendDir()	{ return Math.Abs(r7) == 2 || Math.Abs(r7) == 3 ? (r7 > 0 ? 1 : -1) : 0; }

		// 화면·로그용 7레짐 이름: 추세·눌림 중 전환의심이면 '하락 전환의심'(상승 중) / '상승 전환의심'(하락 중)
		private string R7Display()
		{
			if (r7Warn != 0)
				return (r7Warn > 0 ? "상승" : "하락") + " 전환의심 (" + R7Text(r7) + " 중)";
			return R7Text(r7);
		}

		// 진입 봉에서 새 추세 봉 값을 처음 볼 때 그 봉의 추세 시작·의심 이벤트를 꺼낸다 (그 진입 봉에서만 0이 아님)
		private void ObserveRegime()
		{
			trendStartNow	= 0;
			suspectEventNow	= 0;
			reaccelNow		= 0;
			fastCrossNow	= 0;
			// 의심 이벤트 번호 = 이 진입 봉 (판정이 1분마다라 이벤트도 1분봉 단위). v6 처음에는 이 값을 갱신하지 않아
			// 한 실행에서 첫 의심이 끝나면 그 뒤 의심을 다시 처리하지 못했음 (2026-10-15 정리 중 발견)
			lastRegimeBar = CurrentBars[0];
			UpdateContScore();
			ObserveCont();
			ObserveReaccel();		// 판정이 1분마다 바뀌므로 재가속도 1분마다 본다
		}

		// 추세 재가속 (E1Reaccel): 추세가 이어지는 중에 F가 0 이하(매도는 0 이상)로 내려갔다가 다시 추세 시작 조건(F, S)을 채운 추세 봉.
		// 추세가 시작되거나 횡보가 되면 처음부터 다시 센다. 대회 시간 밖에서도 센다 (대회 전에 시작된 추세용)
		private void ObserveReaccel()
		{
			int st	= RegimeState();
			int f	= FastNow();
			int s	= SlowNow();
			if (trendStartNow != 0 || st == 0)
			{
				reaccelDipUp = false;
				reaccelDipDn = false;
				return;
			}
			bool dip = st > 0 ? reaccelDipUp : reaccelDipDn;
			if (st * f <= 0)
				dip = true;
			else if (dip && st * f >= ReaccelFast && st * s >= ReaccelSlow)
			{
				reaccelNow	= st;
				dip			= false;
			}
			reaccelDipUp = st > 0 && dip;
			reaccelDipDn = st < 0 && dip;
		}

		// 밴드 값: n = 0 중심선, 1~3 = dir 쪽 n배 (매수 +n배 = 위, 매도 = 아래). 4-0 거울상
		private double Band(int d, int n)
		{
			if (n <= 0)
				return channels.Mid[0];
			if (d > 0)
				return n == 1 ? channels.Up1[0] : n == 2 ? channels.Up2[0] : channels.Up3[0];
			return n == 1 ? channels.Dn1[0] : n == 2 ? channels.Dn2[0] : channels.Dn3[0];
		}

		private string BandText(int d, int n)
		{
			return n <= 0 ? "중심선" : (d > 0 ? "+" : "−") + n + "배";
		}

		private bool IsTrendPos()	{ return pos == Pos.TL || pos == Pos.TS; }
		private string DirText(int d)	{ return d > 0 ? "매수" : "매도"; }

		private string PosName(Pos p)
		{
			return p == Pos.TL ? "추세 매수" : p == Pos.TS ? "추세 매도" : p == Pos.SL ? "횡보 매수" : p == Pos.SS ? "횡보 매도" : "없음";
		}

		private string PathName(string p)
		{
			return p == "E1" ? "E1 추세 시작" : p == "E1X" ? "E1 반대 추세 청산 뒤" : p == "E1R" ? "E1 추세 재가속" : p == "E1S" ? "E1 구간 첫 봉 추세" : p == "B1" ? "B1 돌파" : p == "E2" ? "E2 추세 눌림" : p == "E3" ? "E3 장전 박스 돌파" : p == "E4" ? "E4 횡보" : p == "E5" ? "E5 개장 범위 돌파" : p == "E6" ? "E6 박스 장 되돌림" : p;
		}

		private string StateText(int st)
		{
			return st == 1 ? "▲ 상승추세" : st == -1 ? "▼ 하락추세" : "◆ 횡보";
		}

		private static string Signed(int v)
		{
			return v > 0 ? "+" + v : v.ToString();
		}
		#endregion

		#region 시간대 (4-7) — 장전 / 장후
		private enum Zone { Out, News, Pre, OpenBlock, Post }

		private static int MinOfDay(int hhmmss)
		{
			return (hhmmss / 10000) * 60 + (hhmmss / 100) % 100;
		}

		// 대회 시작 시각에서 몇 분 지났는지 (자정을 넘어도 이어서 셈)
		private int RelMinutes(int hhmmss)
		{
			return (MinOfDay(hhmmss) - MinOfDay(ContestStartTime) + 1440) % 1440;
		}

		// 신호 봉(진입 봉 마감 시각) 기준 구간.
		//  · 지표 발표: 대회 시작 ~ +NewsBlockMinutes 전 (21:30~21:34 봉)
		//  · 장전: 그 뒤 ~ 개장 − OpenBlockBefore 전 (21:35~22:24 봉)
		//  · 개장 전후: 개장 − OpenBlockBefore ~ 개장 + OpenBlockAfter 전 (22:25~22:32 봉)
		//  · 장후: 그 뒤 (22:33~)
		private Zone ZoneNow()
		{
			if (!InEntryWindow())
				return Zone.Out;
			int rel		= RelMinutes(ToTime(Time[0]));
			int open	= RelMinutes(OpenTime);
			if (rel < NewsBlockMinutes)					return Zone.News;
			if (rel < open - OpenBlockBefore)			return Zone.Pre;
			if (rel < open + OpenBlockAfter)			return Zone.OpenBlock;
			return Zone.Post;
		}

		private string ZoneText(Zone z)
		{
			return z == Zone.News ? "지표 발표 진입 금지" : z == Zone.Pre ? "장전" : z == Zone.OpenBlock ? "개장 전후 진입 금지"
				: z == Zone.Post ? "장후" : "진입 시간 아님";
		}

		// 진입 시간대에 들어서는 첫 봉에서 대회 구간을 새로 시작한다. 장전 박스는 대회 시작 다음 봉 ~ 개장 시각 봉의 최고가·최저가
		private void UpdateSession()
		{
			bool inWin = InEntryWindow();
			if (inWin && !wasInEntryWindow)
			{
				entryCount		= 0;
				blockSideLong	= false;
				blockSideShort	= false;
				orbCountLong	= 0;
				orbCountShort	= 0;
				orbArmedLong	= true;
				orbArmedShort	= true;
				boxHigh			= double.NaN;
				boxLow			= double.NaN;
				orHigh			= double.NaN;
				orLow			= double.NaN;
				orLongUsed		= false;
				orShortUsed		= false;
				sessStartPx		= double.NaN;
				sessPath		= 0;
				sessBars		= 0;
				sessPnlUsd		= 0;
				dayCutLogged	= false;
				boxStartTime	= Time[0];
				sessionTag		= Time[0].ToString("yyyyMMdd");
			}
			wasInEntryWindow = inWin;

			if (inWin)
			{
				int rel = RelMinutes(ToTime(Time[0]));
				if (rel >= 1 && rel <= RelMinutes(OpenTime))
				{
					boxHigh	= double.IsNaN(boxHigh) ? High[0] : Math.Max(boxHigh, High[0]);
					boxLow	= double.IsNaN(boxLow) ? Low[0] : Math.Min(boxLow, Low[0]);
				}
				// spec 4-11 박스 장 감지용: 대회 구간 첫 봉부터 종가 변화의 합
				if (double.IsNaN(sessStartPx))
					sessStartPx = Close[0];
				else
				{
					sessPath += Math.Abs(Close[0] - sessPrevPx);
					sessBars++;
				}
				sessPrevPx = Close[0];
				// E5 개장 범위: 개장 시각 봉 ~ 그 뒤 E5RangeMinutes − 1분 봉 (기본 22:30~22:34 봉)
				int openRel = RelMinutes(OpenTime);
				if (rel >= openRel && rel <= openRel + E5RangeMinutes - 1)
				{
					orHigh	= double.IsNaN(orHigh) ? High[0] : Math.Max(orHigh, High[0]);
					orLow	= double.IsNaN(orLow) ? Low[0] : Math.Min(orLow, Low[0]);
				}
				// E3 다시 열기: 박스가 완성된 뒤 종가가 박스 안(매수는 고가 이하, 매도는 저가 이상)으로 마감하면 (보유 중에도 본다)
				if (BoxReady())
				{
					if (Close[0] <= boxHigh)	orbArmedLong = true;
					if (Close[0] >= boxLow)		orbArmedShort = true;
				}
			}

			// 우상단 진입 횟수 (최소 진입 목표): 글자만 만들고 그리기는 OnRender
			entryCountText = string.Format("{0} 진입: {1}/{2}회{3}", Instrument.MasterInstrument.Name, entryCount, MinEntries, entryCount >= MinEntries ? "" : " (최소 미달)");
		}

		// spec 4-11 박스 장: 대회 구간 시작 뒤 RangeFadeMinBars봉이 지났고, 시작부터 지금까지 효율비 ≤ RangeFadeMaxEr
		private double SessionEr()	{ return sessPath > 0 && !double.IsNaN(sessStartPx) ? Math.Abs(Close[0] - sessStartPx) / sessPath : 1.0; }
		private bool RangeDay()		{ return sessBars >= RangeFadeMinBars && SessionEr() <= RangeFadeMaxEr; }

		// spec 4-11 가짜 돌파: 1~3봉 전 봉이 그 앞 RangeFadeBars봉 최저가를 깼는데, 직전 봉 종가는 그 아래이고 지금 봉 종가가 다시 위 → 매수 (매도는 거울상)
		private int RangeFadeSignal()
		{
			for (int k = 0; k < 2; k++)
			{
				int d = k == 0 ? 1 : -1;
				for (int lag = 1; lag <= RangeFadeMaxLag; lag++)
				{
					if (CurrentBars[0] < lag + RangeFadeBars + 1)
						return 0;
					double edge	= d > 0 ? fadeLow[lag + 1] : fadeHigh[lag + 1];
					bool broke	= d > 0 ? Lows[0][lag] < edge : Highs[0][lag] > edge;
					if (broke && d * (Close[0] - edge) > 0 && d * (Close[1] - edge) <= 0)
						return d;
				}
			}
			return 0;
		}

		// E5 개장 범위가 완성됐는지 (범위 마지막 봉이 지났는지)
		private bool OpenRangeReady()
		{
			return !double.IsNaN(orHigh) && InEntryWindow() && RelMinutes(ToTime(Time[0])) > RelMinutes(OpenTime) + E5RangeMinutes - 1;
		}

		private double OpenRangeMid()	{ return (orHigh + orLow) / 2; }

		// 장전 박스가 완성됐는지 (개장 시각 봉이 지났는지)
		private bool BoxReady()
		{
			return !double.IsNaN(boxHigh) && InEntryWindow() && RelMinutes(ToTime(Time[0])) > RelMinutes(OpenTime);
		}

		// 진입 허용 시간: 신호 봉이 ContestStartTime ~ EntryEndTime (자정을 넘는 설정도 처리)
		private bool InEntryWindow()
		{
			return IsTimeInWindow(ToTime(Time[0]), ContestStartTime, EntryEndTime);
		}

		private bool IsTimeInWindow(int t, int start, int end)
		{
			if (start <= end)
				return t >= start && t <= end;
			return t >= start || t <= end;
		}

		// 종료 청산 시각: FlattenTime 봉부터 다음 대회 시작 전까지 (자정을 넘지 않는 대회 시간도 처리, 10/14 수정본)
		private bool IsFlattenTime()
		{
			int t = ToTime(Time[0]);
			if (FlattenTime < ContestStartTime)
				return t >= FlattenTime && t < ContestStartTime;
			return t >= FlattenTime || t < ContestStartTime;
		}
		#endregion

		#region 진입 (4-2)
		// 진입 후보 하나. 상태 상자·로그에 그대로 쓴다
		private class Candidate
		{
			public string	Path;		// E1 ~ E4
			public int		Dir;
			public bool		Ready;		// 지금 들어갈 수 있음
			public string	Line;		// 상태 상자 줄 (●○× 규칙)
			public string	Code;		// 로그용 짧은 상태
			public double	QtyPercent	= 100;
			public bool		Fallback;
			public double	RR			= double.NaN;
			public string	Note		= "";
			public double	StopPrice	= double.NaN;	// E3 스탑 주문 진입 가격 (조건은 맞고 종가만 아직 박스 안)	// E1이 열린 이유 (추세 시작 / 반대 추세 청산 뒤 / 재가속 / 구간 첫 봉 추세)
		}

		// 경로 우선순위: E1 > E3 > E2 > E4 (4-2)
		private static int PathRank(string p)
		{
			return p == "E5" ? -1 : p == "E1" ? 0 : p == "E3" ? 1 : p == "B1" ? 2 : p == "E2" ? 3 : p == "E6" ? 5 : 4;	// E5(v6, 4-10)가 가장 먼저	// B1(v4)은 E3 다음
		}

		// 진입 경로 4개 × 방향 2개의 후보와 막힌 사유를 만든다. 미보유일 때 매 진입 봉 마감마다
		// spec 4-9 B1 박스 판단: 지금 봉을 뺀 직전 BreakoutBoxBars봉 종가의 효율비 = |처음→끝 순변화| ÷ 봉마다 변화의 합.
		// 0에 가까우면 왔다갔다(박스), 1이면 한 방향. 7·10월 1분 15봉 돌파 112건에서 효율비 낮은 ⅓의 돌파만 두 달 모두 30분 뒤까지 이어졌음
		private double BoxEfficiency()
		{
			int n = BreakoutBoxBars;
			if (CurrentBars[0] <= n) return 1.0;
			double net	= Math.Abs(Closes[0][1] - Closes[0][n]);
			double path	= 0;
			for (int k = 1; k < n; k++)
				path += Math.Abs(Closes[0][k] - Closes[0][k + 1]);
			return path > 0 ? net / path : 1.0;
		}

		private static double QBin(double[] t, double x)	{ return x < t[0] ? t[2] : x < t[1] ? t[3] : t[4]; }

		// spec 4-8-1 진입 품질 점수: 진입 방향 기준 정보 5개 + 7레짐 관계. 높을수록 유리 (0 근처가 평균)
		private double EntryQuality(int d, double px, out string detail)
		{
			double a	= Math.Max(atr[0], TickSize);
			double F	= d * contFast;
			double ext	= d * (px - channels.Mid[0]) / a;
			double r10	= (MAX(Highs[0], 10)[0] - MIN(Lows[0], 10)[0]) / a;
			double pre	= CurrentBars[0] > 15 ? d * (px - Closes[0][15]) / a : 0;
			double hh	= MAX(Highs[0], 30)[1], ll = MIN(Lows[0], 30)[1];
			double swing	= d > 0 ? signals.SwingLow5[0] : signals.SwingHigh5[0];
			double stopD	= StopDistance(d, true, px, swing, atr[0]);
			double room	= stopD > 0 ? (d > 0 ? hh - px : px - ll) / stopD : 0;
			int a7 = Math.Abs(r7), d7 = r7 > 0 ? 1 : r7 < 0 ? -1 : 0;
			string reg; double rv;
			if (r7Warn == -d)							{ reg = "전환의심";		rv = -0.0325; }
			else if (a7 == 3 && d7 == d)				{ reg = "같은 눌림";	rv = 0.2566; }
			else if (a7 == 2 && d7 == d)				{ reg = "같은 추세";	rv = -0.0646; }
			else if ((a7 == 2 || a7 == 3) && d7 == -d)	{ reg = "반대 추세";	rv = 0.2174; }
			else if (a7 == 1 && d7 == -d)				{ reg = "반대 시작의심";	rv = -0.1086; }
			else if (a7 == 0)							{ reg = "횡보";		rv = -0.1444; }
			else										{ reg = "같은 시작의심";	rv = 0; }
			double sc = QBin(QF, F) + QBin(QExt, ext) + QBin(QR10, r10) + QBin(QPre, pre) + QBin(QRoom, room) + rv;
			detail = string.Format("품질 {0:+0.000;-0.000} (F {1:F2}, 중심선 {2:F1}ATR, 10봉폭 {3:F1}ATR, 15분 {4:+0.0;-0.0}ATR, 남은거리 {5:F2}손절, {6})",
				sc, F, ext, r10, pre, room, reg);
			return sc;
		}

		private List<Candidate> EvaluateEntries()
		{
			List<Candidate> list	= new List<Candidate>();
			int st		= RegimeState();
			int S		= SlowNow();
			int F		= FastNow();
			Zone z		= ZoneNow();
			bool fallback = MinEntryFallback && z == Zone.Post && entryCount < MinEntries
							&& RelMinutes(ToTime(Time[0])) >= RelMinutes(MinEntryFallbackTime);
			int suspectActive = SuspectActiveNow();

			for (int k = 0; k < 2; k++)
			{
				int d = k == 0 ? 1 : -1;

				// 시간대 공통 사유
				string timeBlock = z == Zone.Out ? "진입 시간 아님" : z == Zone.News ? "지표 발표 진입 금지 (" + NewsBlockMinutes + "분)"
					: z == Zone.OpenBlock ? "개장 전후 진입 금지" : null;
				// spec 3-4: 전환의심 중에는 원래 추세 방향 새 진입 안 함 (모든 경로)
				if (timeBlock == null && r7Warn == -d)
					timeBlock = "전환의심 중 (" + R7Display() + ")";
				// spec 4-7 v6: 23:00 봉부터는 최소 진입 보장(진입 횟수 < MinEntries)일 때만 새 진입 (개장 직후 움직임이 끝난 뒤의 돌파·되돌림은 졌음)
				if (timeBlock == null && !fallback && RelMinutes(ToTime(Time[0])) >= RelMinutes(NormalEntryEndTime))
					timeBlock = TimeText(NormalEntryEndTime) + " 뒤에는 최소 진입 보장만";
				// 의심 중에는 그 추세 방향 신규 진입 안 함 (가격 확인 뒤에만 막는 완화는 10/15에 넣었다가 손실로 지웠다: 의심 중 E2 8건 −$9,997)
				string suspectBlock = BlockEntryWhileSuspect && st == d && suspectActive == -d ? "의심 중이라 추세 방향 신규 진입 안 함" : null;
				// 추세 진입(E1·E2·E3)은 빠른 점수가 반대면 안 함 (TrendEntryNeedFast, 사용자 결정 2026-10-15):
				// 추세 판정은 늦어서, 반전 초입에는 판정은 옛 방향인데 F는 이미 반대다 (21일: 반대 의심 중 E2 8건 −$9,997)
				string fastBlock = TrendEntryNeedFast && d * F < 0 ? "빠른 점수 F가 반대 (" + Signed(F) + ")" : null;
				if (suspectBlock == null)
					suspectBlock = fastBlock;
				if (suspectBlock == null && r7Warn == -d)
					suspectBlock = "전환의심 중 (" + R7Display() + ")";
				// E1이 열리는 경우: 추세 시작 / 반대 추세 청산 뒤 / 재가속 (구간 첫 봉 추세 E1S는 21일 10건 3승 −$2,792로 지웠다)
				string e1Kind		= trendStartNow == d ? "추세 시작"
					: e1AfterExitDir == d && CurrentBars[0] - e1AfterExitBar <= E1AfterExitBars ? "반대 추세 청산 뒤"
					: E1Reaccel && reaccelNow == d ? "재가속"
					: null;
				double qtyPct = z == Zone.Pre && !fallback ? PreOpenQtyPercent : 100;
				if (fallback)
					qtyPct = MinEntryQtyPercent;

				// ── E1 추세 시작 ──
				{
					Candidate c = new Candidate { Path = "E1", Dir = d, QtyPercent = qtyPct, Fallback = fallback };
					// spec 3-4: '추세 시작'은 7레짐 추세 확정 E1DelayBars봉째. 이때는 밴드·손익비·장전 S 조건을 보지 않음(18일 시뮬레이션과 같은 조건).
					// 반대 추세 청산 뒤(E1X)·재가속(E1R)은 지금까지처럼 진입 판정과 그 조건들로
					bool confirmKind = e1Kind == "추세 시작";
					int e1Dir = confirmKind ? R7TrendDir() : st;
					bool armed = e1Dir == d && e1Kind != null;
					c.Note = e1Kind ?? "";
					string block = null;
					if (!UseEntryTrendStart)				block = "꺼짐";
					else if (e1Dir != d)					block = null;
					else if (timeBlock != null)				block = timeBlock;
					else if (suspectBlock != null)			block = suspectBlock;
					else if (!confirmKind && z == Zone.Pre && !fallback && d * S < PreOpenTrendMinSlow)	block = "장전에는 S가 " + Signed(d * PreOpenTrendMinSlow) + " 이상이어야 함 (지금 " + Signed(S) + ")";
					if (block == null && armed && !confirmKind)
					{
						double tp1	= Band(d, TrendTp1Band);
						double tp2	= Band(d, TrendTp2Band);
						double dist	= ExpectedStopDist(d, true);
						c.RR		= dist > 0 ? d * (tp2 - Close[0]) / dist : double.NaN;
						if (d * (Close[0] - tp1) >= 0)
							block = string.Format("종가 {0:N2}가 이미 1차 익절 밴드({1} {2:N2})를 넘음", Close[0], BandText(d, TrendTp1Band), tp1);
						else if (EntryMinRR > 0 && !(c.RR >= EntryMinRR))
							block = string.Format("손익비 부족: 2차 익절까지 {0:F1}pt ÷ 예상 손절 {1:F1}pt = {2:F2} < {3}", d * (tp2 - Close[0]), dist, c.RR, EntryMinRR);
					}
					if (!UseEntryTrendStart)		{ c.Line = "× E1 추세 시작: 꺼짐"; c.Code = "off"; }
					else if (e1Dir != d)			{ c.Line = "  E1 추세 시작: " + (d > 0 ? "상승" : "하락") + "추세가 아님 (" + R7Text(r7) + ")"; c.Code = "-"; }
					else if (armed && block == null)	{ c.Ready = true; c.Line = "● E1 추세 시작: 지금 진입 (" + e1Kind + ")"; c.Code = "go"; }
					else if (armed)					{ c.Line = "× E1 추세 시작 (" + e1Kind + "): " + block; c.Code = "blk"; }
					else if (r7Age >= 0 && r7Age < E1DelayBars)	{ c.Line = string.Format("○ E1 추세 시작: 확정 {0}/{1}봉 대기", r7Age, E1DelayBars); c.Code = "wait"; }
					else							{ c.Line = "  E1 추세 시작: 이미 지남" + (lastTrendStartDir == d ? " (" + lastTrendStartTime.ToString("HH:mm") + ")" : ""); c.Code = "past"; }
					list.Add(c);
				}

				// ── E2 추세 눌림 ── 저가 ≤ Band(−, TrendPullbackBand) → T1 [MACD 상승 전환 OR 골든] (TQ_Signals EntryUpLong / EntryDnShort)
				{
					Candidate c = new Candidate { Path = "E2", Dir = d, QtyPercent = qtyPct, Fallback = fallback };
					bool sig	= d > 0 ? signals.EntryUpLong[0] : signals.EntryDnShort[0];
					int wait	= d > 0 ? signals.WaitUpLong : signals.WaitDnShort;
					int bars	= d > 0 ? signals.T1BarsUp : signals.T1BarsDn;
					string block = !UseEntryPullback ? "꺼짐" : timeBlock ?? suspectBlock;
					if (block == null && z == Zone.Pre && !fallback && d * S < PreOpenTrendMinSlow)
						block = "장전에는 S가 " + Signed(d * PreOpenTrendMinSlow) + " 이상이어야 함 (지금 " + Signed(S) + ")";
					// 추세 끝물 거르기 (PullbackMinSlow, 사용자 결정 2026-10-15): S가 ±2 미만이면 눌림을 받지 않음 (21일 |S| ≤ 1 E2 4건 1승, −$6,867)
					if (block == null && PullbackMinSlow > 0 && d * S < PullbackMinSlow)
						block = "S가 " + Signed(d * PullbackMinSlow) + " 이상이어야 함 (지금 " + Signed(S) + ", 추세 끝물)";
					// 손익비 필터 (E1과 같은 EntryMinRR, 사용자 결정 2026-10-15): 신호가 나온 봉에서만 본다
					if (block == null && sig && st == d)
					{
						double tp2	= Band(d, TrendTp2Band);
						double dist	= ExpectedStopDist(d, true);
						c.RR		= dist > 0 ? d * (tp2 - Close[0]) / dist : double.NaN;
						if (EntryMinRR > 0 && !(c.RR >= EntryMinRR))
							block = string.Format("손익비 부족: 2차 익절까지 {0:F1}pt ÷ 예상 손절 {1:F1}pt = {2:F2} < {3}", d * (tp2 - Close[0]), dist, c.RR, EntryMinRR);
					}
					string bandTxt = BandText(-d, TrendPullbackBand) + " " + Band(-d, TrendPullbackBand).ToString("N2");
					if (!UseEntryPullback)			{ c.Line = "× E2 눌림: 꺼짐"; c.Code = "off"; }
					else if (st != d)				{ c.Line = "  E2 눌림: " + (d > 0 ? "상승" : "하락") + "추세가 아님"; c.Code = "-"; }
					else if (block != null)			{ c.Line = "× E2 눌림: " + block; c.Code = "blk"; }
					else if (sig)					{ c.Ready = true; c.Line = "● E2 눌림: 지금 진입"; c.Code = "go"; }
					else if (wait >= 0)				{ c.Line = "● E2 눌림: " + (d > 0 ? "MACD▲ / 골든" : "MACD▼ / 데드") + " 대기 " + Boxes(wait, bars); c.Code = "wait" + wait; }
					else							{ c.Line = "○ E2 눌림: " + (d > 0 ? "저가" : "고가") + " " + bandTxt + " 터치 대기"; c.Code = "band"; }
					list.Add(c);
				}

				// ── E3 장전 박스 돌파 ── 장후 AND OrbEndTime 전 AND 종가가 박스 밖 AND S·F가 그 방향.
				// 방향별 OrbMaxEntries번까지. 한 번 쓰면 종가가 박스 안으로 돌아와야 다시 열림 (사용자 결정 2026-10-15, 그 전에는 방향별 한 번)
				{
					Candidate c = new Candidate { Path = "E3", Dir = d, QtyPercent = 100, Fallback = fallback };
					if (fallback)
						c.QtyPercent = MinEntryQtyPercent;
					int count	= d > 0 ? orbCountLong : orbCountShort;
					bool armedOrb = d > 0 ? orbArmedLong : orbArmedShort;
					double edge	= d > 0 ? boxHigh : boxLow;
					bool beforeEnd = RelMinutes(ToTime(Time[0])) < RelMinutes(OrbEndTime);
					if (!UseEntryOrb)				{ c.Line = "× E3 박스 돌파: 꺼짐"; c.Code = "off"; }
					else if (count >= OrbMaxEntries)	{ c.Line = "  E3 박스 돌파: 이번 구간 " + count + "/" + OrbMaxEntries + "번 사용함"; c.Code = "used"; }
					else if (!armedOrb)				{ c.Line = string.Format("○ E3 박스 돌파 ({0}/{1}번): 종가가 박스 안({2} {3:N2})으로 돌아오면 다시 열림", count, OrbMaxEntries, d > 0 ? "≤" : "≥", edge); c.Code = "rearm"; }
					else if (z != Zone.Post)		{ c.Line = "  E3 박스 돌파: 장후(" + TimeText(MinOfDayToHhmmss(MinOfDay(OpenTime) + OpenBlockAfter)) + "~)에만"; c.Code = "-"; }
					else if (!beforeEnd)			{ c.Line = "  E3 박스 돌파: " + TimeText(OrbEndTime) + " 이후라 안 함"; c.Code = "late"; }
					else if (!BoxReady())			{ c.Line = "  E3 박스 돌파: 장전 박스 없음"; c.Code = "nobox"; }
					else if (suspectBlock != null)	{ c.Line = "× E3 박스 돌파: " + suspectBlock; c.Code = "blk"; }
					else if (d * (Close[0] - edge) > 0 && d * S >= OrbMinSlow && d * F >= OrbMinFast)
													{ c.Ready = true; c.Line = "● E3 박스 돌파: 지금 진입"; c.Code = "go"; }
					else if (d * (Close[0] - edge) > 0)
						{ c.Line = string.Format("× E3 박스 돌파: 종가는 박스 밖이지만 S {0} / F {1} (필요 {2} / {3})", Signed(S), Signed(F), Signed(d * OrbMinSlow), Signed(d * OrbMinFast)); c.Code = "score"; }
					else if (E3StopEntry && d * S >= OrbMinSlow && d * F >= OrbMinFast)
					{
						c.StopPrice	= Instrument.MasterInstrument.RoundToTickSize(edge + d * TickSize);
						c.Line		= string.Format("● E3 박스 돌파: 스탑 주문 {0:N2} (박스 {1} {2:N2})", c.StopPrice, d > 0 ? "고가" : "저가", edge);
						c.Code		= "stop";
					}
					else							{ c.Line = string.Format("○ E3 박스 돌파: 종가 {0} {1:N2}", d > 0 ? ">" : "<", edge); c.Code = "wait"; }
					list.Add(c);
				}

				// ── E5 개장 범위 돌파 (4-10) ── 개장 범위가 완성된 뒤 진입 봉 종가가 범위 고가 위(매도는 저가 아래). 방향별 하루 한 번.
				// 추세 판정·S·F·의심은 보지 않음 (28일 시뮬레이션과 같은 조건)
				{
					Candidate c = new Candidate { Path = "E5", Dir = d, QtyPercent = fallback ? MinEntryQtyPercent : 100, Fallback = fallback };
					double edge	= d > 0 ? orHigh : orLow;
					bool used	= d > 0 ? orLongUsed : orShortUsed;
					string rangeTxt = string.Format("개장 {0}분 범위 {1} {2:N2}", E5RangeMinutes, d > 0 ? "고가" : "저가", edge);
					if (!UseEntryOpenRange)			{ c.Line = "× E5 개장 범위: 꺼짐"; c.Code = "off"; }
					else if (!OpenRangeReady())		{ c.Line = "  E5 개장 범위: " + (double.IsNaN(orHigh) ? "범위 전" : "범위 기록 중"); c.Code = "-"; }
					else if (!fallback && RelMinutes(ToTime(Time[0])) >= RelMinutes(E5EndTime))	{ c.Line = "  E5 개장 범위: " + TimeText(E5EndTime) + " 이후라 안 함"; c.Code = "late"; }
					else if (used)					{ c.Line = "  E5 개장 범위: 오늘 " + DirText(d) + " 씀"; c.Code = "used"; }
					else if (timeBlock != null)		{ c.Line = "× E5 개장 범위: " + timeBlock; c.Code = "blk"; }
					else if (d * (Close[0] - edge) > 0)	{ c.Ready = true; c.Line = "● E5 개장 범위: 지금 진입 (종가 " + Close[0].ToString("N2") + (d > 0 ? " > " : " < ") + rangeTxt + ")"; c.Code = "go"; }
					else							{ c.Line = string.Format("○ E5 개장 범위: 종가 {0} {1}", d > 0 ? ">" : "<", rangeTxt); c.Code = "wait"; }
					list.Add(c);
				}

				// ── B1 박스 돌파 (4-9) ── 진입 봉 종가가 직전 BreakoutBars봉 고가 위(매도는 저가 아래) AND F ≥ BreakoutMinFast
				// AND 직전 BreakoutBoxBars봉이 박스(효율비 ≤ BreakoutMaxEr, 1이면 안 봄) AND |S| ≤ BreakoutMaxAbsSlow(4면 안 봄). 추세 판정(+1/0/−1)은 보지 않는다
				{
					Candidate c = new Candidate { Path = "B1", Dir = d, QtyPercent = qtyPct, Fallback = fallback };
					double edge	= d > 0 ? rangeHigh[1] : rangeLow[1];
					string rangeTxt = string.Format("{0}분 {1} {2:N2}", BreakoutBars, d > 0 ? "고가" : "저가", edge);
					string block = !UseBreakoutEntry ? "꺼짐" : timeBlock;
					double er = BoxEfficiency();
					if (block == null && !fallback && BreakoutMaxEr < 1 && er > BreakoutMaxEr)
						block = string.Format("박스가 아님: 직전 {0}봉 효율비 {1:N2} (≤ {2:N2}일 때만)", BreakoutBoxBars, er, BreakoutMaxEr);
					bool beyond	= d * (Close[0] - edge) > 0;
					bool fastOk	= d * F >= BreakoutMinFast;
					if (!UseBreakoutEntry)			{ c.Line = "× B1 돌파: 꺼짐"; c.Code = "off"; }
					else if (block != null)			{ c.Line = "× B1 돌파: " + block; c.Code = "blk"; }
					else if (beyond && fastOk)		{ c.Ready = true; c.Line = "● B1 돌파: 지금 진입 (종가 " + Close[0].ToString("N2") + (d > 0 ? " > " : " < ") + rangeTxt + ", 효율비 " + er.ToString("N2") + ")"; c.Code = "go"; }
					else if (beyond)				{ c.Line = string.Format("× B1 돌파: 종가는 {0} 밖이지만 F {1} (필요 {2})", rangeTxt, Signed(F), Signed(d * BreakoutMinFast)); c.Code = "fast"; }
					else							{ c.Line = string.Format("○ B1 돌파: 종가 {0} {1}{2}", d > 0 ? ">" : "<", rangeTxt, fastOk ? "" : string.Format(" · F {0} (필요 {1})", Signed(F), Signed(d * BreakoutMinFast))); c.Code = "wait"; }
					list.Add(c);
				}

				// ── E4 횡보 ── 횡보 AND 저가 ≤ Band(−, SideBand) → T1 (또는 SideT2Entry면 T2)
				{
					Candidate c = new Candidate { Path = "E4", Dir = d, QtyPercent = qtyPct, Fallback = fallback };
					bool sig	= SideSignal(d);
					int wait	= d > 0 ? signals.WaitSideLong : signals.WaitSideShort;
					int bars	= signals.T1BarsSide;
					string block = !UseEntrySide ? "꺼짐" : timeBlock;
					if (block == null && z == Zone.Pre && !PreOpenAllowSide && !fallback)	block = "장전 횡보 진입 꺼짐";
					if (block == null && !fallback && SideBlockScore > 0 && d * (S + F) <= -SideBlockScore)
						block = string.Format("횡보 방향 필터: S+F = {0} ({1}쪽)", Signed(S + F), d > 0 ? "하락" : "상승");
					if (block == null && BlockSideAfterStop && (d > 0 ? blockSideLong : blockSideShort))
						block = "손실 손절 뒤 같은 방향 횡보 재진입 금지";
					string bandTxt = BandText(-d, SideBand) + " " + Band(-d, SideBand).ToString("N2");
					if (!UseEntrySide)				{ c.Line = "× E4 횡보: 꺼짐"; c.Code = "off"; }
					else if (st != 0)				{ c.Line = "  E4 횡보: 횡보가 아님"; c.Code = "-"; }
					else if (block != null)			{ c.Line = "× E4 횡보" + DirText(d) + ": " + block; c.Code = "blk"; }
					else if (sig)					{ c.Ready = true; c.Line = "● E4 횡보" + DirText(d) + ": 지금 진입"; c.Code = "go"; }
					else if (wait >= 0)				{ c.Line = "● E4 횡보" + DirText(d) + ": " + (d > 0 ? "MACD▲ / 골든" : "MACD▼ / 데드") + " 대기 " + Boxes(wait, bars); c.Code = "wait" + wait; }
					else							{ c.Line = "○ E4 횡보" + DirText(d) + ": " + (d > 0 ? "저가" : "고가") + " " + bandTxt + " 터치" + (SideT2Entry ? " (또는 MACD + 크로스)" : ""); c.Code = "band"; }
					list.Add(c);
				}

				// ── E6 박스 장 되돌림 (4-11) ── 박스 장으로 감지된 뒤 가짜 돌파의 반대로. 다른 경로가 없을 때만 (우선순위 마지막)
				{
					Candidate c = new Candidate { Path = "E6", Dir = d, QtyPercent = 100, Fallback = false };
					bool range	= RangeDay();
					string erTxt = string.Format("효율비 {0:F2} (≤ {1})", SessionEr(), RangeFadeMaxEr);
					if (!UseEntryRangeFade)			{ c.Line = "× E6 박스 되돌림: 꺼짐"; c.Code = "off"; }
					else if (sessBars < RangeFadeMinBars)	{ c.Line = "  E6 박스 되돌림: 박스 장 판단 전 (" + sessBars + "/" + RangeFadeMinBars + "봉)"; c.Code = "-"; }
					else if (!range)				{ c.Line = "  E6 박스 되돌림: 박스 장 아님, " + erTxt; c.Code = "-"; }
					else if (timeBlock != null || z != Zone.Post)	{ c.Line = "× E6 박스 되돌림: " + (timeBlock ?? "장후에만"); c.Code = "blk"; }
					else if (RangeFadeSignal() == d)	{ c.Ready = true; c.Line = "● E6 박스 되돌림" + DirText(d) + ": 지금 진입 (" + erTxt + ")"; c.Code = "go"; }
					else							{ c.Line = "○ E6 박스 되돌림" + DirText(d) + ": " + RangeFadeBars + "봉 " + (d > 0 ? "저가" : "고가") + " 가짜 돌파 대기, " + erTxt; c.Code = "wait"; }
					list.Add(c);
				}
			}
			return list;
		}

		// 횡보 진입 신호. TQ_Signals의 EntrySideLong/Short는 [밴드 조건 → T1] OR T2를 하나로 낸다.
		// SideT2Entry를 끄면 T2만으로 나온 신호는 뺀다: 직전 봉까지 밴드 대기가 없었는데 T2가 같이 나온 봉이면 T2만의 신호로 본다
		private int prevWaitSideLong = -1, prevWaitSideShort = -1;
		private bool SideSignal(int d)
		{
			bool sig = d > 0 ? signals.EntrySideLong[0] : signals.EntrySideShort[0];
			if (!sig || SideT2Entry)
				return sig;
			bool t2 = d > 0 ? signals.T2Bull[0] : signals.T2Bear[0];
			int prevWait = d > 0 ? prevWaitSideLong : prevWaitSideShort;
			return !t2 || prevWait >= 0;
		}

		// T1 대기 칸: ■ 지난 봉, □ 남은 봉
		private static string Boxes(int wait, int total)
		{
			int done = Math.Max(0, Math.Min(wait, total));
			return new string('■', done) + new string('□', Math.Max(0, total - done));
		}

		private static string TimeText(int hhmmss)
		{
			return string.Format("{0:00}:{1:00}", hhmmss / 10000, (hhmmss / 100) % 100);
		}

		private static int MinOfDayToHhmmss(int minutes)
		{
			minutes = ((minutes % 1440) + 1440) % 1440;
			return (minutes / 60) * 10000 + (minutes % 60) * 100;
		}

		// 준비된 후보 중 우선순위가 가장 높은 것으로 1회 진입 (E1 > E3 > E2 > E4. 같은 경로에 두 방향이면 매수 먼저)
		private void RouteEntry(List<Candidate> cands)
		{
			Candidate best = null;
			foreach (Candidate c in cands)
				if (c.Ready && (best == null || PathRank(c.Path) < PathRank(best.Path)))
					best = c;
			e3StopPlacedNow = false;
			if (best == null)
			{
				PlaceE3Stop(cands);
				if (!e3StopPlacedNow)
					ForgetE3Stop();
				return;
			}
			CancelE3Stop(best.Path + DirText(best.Dir) + " 진입");

			Pos p = best.Path == "E4" ? (best.Dir > 0 ? Pos.SL : Pos.SS) : (best.Dir > 0 ? Pos.TL : Pos.TS);
			// 기록용 경로 이름: E1은 열린 이유별로 나눈다 (E1 추세 시작, E1X 반대 추세 청산 뒤, E1R 재가속, E1S 구간 첫 봉 추세)
			string pathCode = best.Path != "E1" ? best.Path
				: best.Note == "반대 추세 청산 뒤" ? "E1X" : best.Note == "재가속" ? "E1R" : best.Note == "구간 첫 봉 추세" ? "E1S" : "E1";
			Enter(p, best.Dir, pathCode, best.QtyPercent, best.Fallback, best.RR);
			if (best.Path == "E3")
			{
				if (best.Dir > 0)	{ orbCountLong++;	orbArmedLong = false; }
				else				{ orbCountShort++;	orbArmedShort = false; }
			}
			if (best.Path == "E1")
				e1AfterExitDir = 0;
			if (best.Path == "E5")
			{
				if (best.Dir > 0)	orLongUsed = true;
				else				orShortUsed = true;
			}
			if (best.Path == "B1")
			{
				lastBreakoutDir		= best.Dir;
				lastBreakoutTime	= Time[0];
			}
		}

		// spec 4-2 v6 E3 스탑 주문: 시장가로 들어갈 경로가 없을 때, E3 조건(S·F·횟수·시간)은 맞고 종가만 박스 안이면 박스 밖 1틱에 스탑 진입.
		// 한 방향만 건다(관리형 주문은 미보유 때 한 방향 진입 주문이 살아 있으면 반대 진입을 무시, NT8 Managed Approach 문서). 가까운 쪽 우선.
		// 같은 방향을 다음 봉에도 걸면 같은 시그널명으로 다시 내서 주문을 고친다(두 번 체결 방지). 다시 안 걸면 봉 끝에 NinjaTrader가 취소
		private void PlaceE3Stop(List<Candidate> cands)
		{
			if (!E3StopEntry || !UseEntryOrb || cands == null)
				return;
			Candidate pick = null;
			foreach (Candidate c in cands)
				if (c.Path == "E3" && !double.IsNaN(c.StopPrice)
					&& (pick == null || Math.Abs(c.StopPrice - Close[0]) < Math.Abs(pick.StopPrice - Close[0])))
					pick = c;
			if (pick == null)
				return;
			if (e3StopDir != 0 && e3StopDir != pick.Dir)
				return;	// 반대 방향 스탑이 아직 살아 있음: 이번 봉 끝에 취소되면 다음 봉에 건다
			bool renew = e3StopDir == pick.Dir;
			e3StopPx = pick.StopPrice;
			Enter(pick.Dir > 0 ? Pos.TL : Pos.TS, pick.Dir, "E3", pick.QtyPercent, pick.Fallback, double.NaN);
			e3StopPx = double.NaN;
			e3StopDir		= pick.Dir;
			e3StopPlacedNow	= true;
			if (!renew)
				Out(string.Format("[{0}][{1}][TQ_Strategy] E3 스탑 주문 {2} 가격={3:N2}", Time[0], Instrument.FullName, DirText(pick.Dir), pick.StopPrice));
		}

		// 걸어 둔 E3 스탑을 취소한다 (다른 경로로 시장가 진입할 때)
		private void CancelE3Stop(string why)
		{
			if (e3StopDir == 0)
				return;
			bool live = IsWorking(entryOrder) && entryOrder.OrderType == OrderType.StopMarket;
			if (live)
				CancelOrder(entryOrder);
			Out(string.Format("[{0}][{1}][TQ_Strategy] E3 스탑 주문 취소 → {2}", Time[0], Instrument.FullName, why));
			e3StopDir		= 0;
			e3StopPrefix	= "";
		}

		// 걸어 둔 E3 스탑 진입을 이번 봉에 다시 걸지 않음: 비관리형 주문은 취소할 때까지 살아 있으므로 직접 취소 (관리형은 봉 끝에 자동 취소였음)
		private void ForgetE3Stop()
		{
			if (IsWorking(entryOrder) && entryOrder.OrderType == OrderType.StopMarket && PosQty() == 0)
				CancelOrder(entryOrder);
			e3StopDir		= 0;
			e3StopPrefix	= "";
		}

		// 분할 진입. 조각 수량 = 진입 계약 × % 내림, 마지막 조각이 나머지 (4-8)
		private void Enter(Pos p, int d, string entryPath, double qtyPercent, bool fallback, double rr)
		{
			bool stopEntry	= !double.IsNaN(e3StopPx);
			double refPx	= stopEntry ? e3StopPx : Close[0];	// 손절·수량·익절 기준가: 스탑 진입이면 스탑 가격
			ResetTradeState();
			pos				= p;
			dir				= d;
			path			= entryPath;
			fallbackEntry	= fallback;
			session			= ZoneNow() == Zone.Pre ? "pre" : "post";
			rrExpected		= rr;
			// spec 4-8: 기본 수량은 최소 진입 보장이면 그 %, 아니면 100%. 장전 %는 아래 '줄이는 이유'에 넣어 곱함
			double basePct	= fallback ? qtyPercent : 100;
			int qty			= Math.Max(1, (int)Math.Floor(EntryQuantity * basePct / 100.0));

			TradeBegin();
			stopAtr		= atr[0];
			int sinceOpen	= RelMinutes(ToTime(Time[0])) - RelMinutes(OpenTime);
			earlyEntry		= IsTrendPos() && entryPath != "E5" && entryPath != "E6" && sinceOpen > 0 && sinceOpen <= EarlyWindowMinutes;
			stopSwing	= d > 0 ? signals.SwingLow5[0] : signals.SwingHigh5[0];	// 신호 봉 포함 최근 SwingBars봉
			if (entryPath == "E5")
				stopSwing = OpenRangeMid();										// spec 4-10: E5 손절 기준 = 개장 범위 중간
			if (entryPath == "E6")
				stopSwing = d > 0 ? MIN(Lows[0], 4)[0] : MAX(Highs[0], 4)[0];		// spec 4-11: 최근 4봉 극값
			initialStop	= CalcStop(refPx);
			stopPrice	= initialStop;
			// 위험 금액 수량 (UseRiskSizing, 사용자 결정 2026-10-15): 계약 = 위험 한도 × 구간 % ÷ (예상 손절 거리 × 계약당 가치).
			// 최대는 지금 수량(EntryQuantity × 구간 %). 손절 한 번의 손실이 거의 일정해진다
			string riskNote = "";
			if (UseRiskSizing)
			{
				double distPts	= Math.Abs(refPx - initialStop);
				double pv		= Instrument.MasterInstrument.PointValue;
				if (distPts > 0 && pv > 0)
				{
					int byRisk	= (int)Math.Floor(RiskPerTrade * basePct / 100.0 / (distPts * pv));
					qty			= Math.Max(1, Math.Min(qty, byRisk));
					riskNote	= string.Format(" 위험≈${0:N0}(손절 {1:F1}pt × {2}계약)", distPts * pv * qty, distPts, qty);
				}
			}
			// spec 4-8 수량을 줄이는 이유: 장전(PreOpenQtyPercent) · 이미 달린 뒤(ExtendedEntryAtr) · 하루 손실 한도(DayLossLimitR) · 진입 품질 점수(4-8-1).
			// 이유마다 곱하되 SizeFloorPercent 아래로는 줄이지 않음 (18일: 곱하기만 $53,424, 25% 바닥 $53,437, 가장 강한 하나만 $52,097. 바닥이 없으면 6%까지 내려가 1~2계약)
			double mult = 1;
			string why = "";
			if (!fallback && qtyPercent < 100)
			{
				mult *= qtyPercent / 100.0;
				why += string.Format(" 장전 {0}%", qtyPercent);
			}
			if (DayLossLimitR > 0 && sessPnlUsd <= -DayLossLimitR * RiskPerTrade)
			{
				mult *= DayLossQtyPercent / 100.0;
				why += string.Format(" 하루 손실 ${0:N0} ≤ −{1}R", sessPnlUsd, DayLossLimitR);
			}
			bool trendPath = IsTrendPos() && entryPath != "E5" && entryPath != "E6" && entryPath != "FORCE";
			if (trendPath && ExtendedEntryAtr > 0 && atr[0] > 0)
			{
				double ext = d * (refPx - channels.Mid[0]) / atr[0];
				if (ext > ExtendedEntryAtr)
				{
					mult *= ExtendedQtyPercent / 100.0;
					why += string.Format(" 달린 뒤(중심선 {0:F1} ATR)", ext);
				}
			}
			if (trendPath && contReady)
			{
				string qd;
				double q = EntryQuality(d, refPx, out qd);
				riskNote += " " + qd;
				if (q < QualityHalfBelow)
				{
					mult *= 0.5;
					why += string.Format(" 품질 {0:+0.000;-0.000} < {1}", q, QualityHalfBelow);
				}
			}
			if (mult < 1)
			{
				int before = qty;
				double m2 = Math.Max(SizeFloorPercent / 100.0, mult);
				qty = Math.Max(1, (int)Math.Floor(qty * m2));
				riskNote += string.Format(" 수량 줄임:{0} → ×{1:F2}{2} {3}→{4}", why, m2, m2 > mult ? "(바닥)" : "", before, qty);
			}
			if (entryPath == "FORCE")
				qty = 1;	// spec 5 강제 채우기는 1계약
			baseQty = qty;
			MlBegin();

			// 주문 이름은 거래마다 새로: TL7.E = 7번째 거래 진입, TL7.S = 손절, TL7.1T = 1차 조각 익절 (비관리형 주문은 이름으로 찾음)
			// E3 스탑을 같은 방향으로 다시 걸면 같은 이름 (걸어 둔 주문의 가격만 고침)
			string prefix;
			if (stopEntry && e3StopDir == d && e3StopPrefix.Length > 0)
				prefix = e3StopPrefix;
			else
			{
				signalSeq++;
				prefix = p.ToString() + signalSeq + ".";
			}
			if (stopEntry)
				e3StopPrefix = prefix;
			string split;
			if (entryPath == "E6")
			{
				// spec 4-11: 한 조각, 익절 = 진입가 ± RangeFadeTargetR × 손절 거리, RangeFadeMaxHold봉이면 청산
				double risk = Math.Abs(refPx - initialStop);
				SubmitChunk(prefix + "1", qty, 1, refPx + d * RangeFadeTargetR * risk);
				split = qty + " (박스 장 되돌림: 익절 " + RangeFadeTargetR.ToString("F1") + "R, 최대 " + RangeFadeMaxHold + "분, 효율비 " + SessionEr().ToString("F2") + ")";
			}
			else if (entryPath == "E5")
			{
				// spec 4-10: 한 조각, 익절 = 진입가 ± E5TargetR × 손절 거리 (체결되면 체결가 기준으로 다시 검)
				double risk = Math.Abs(Close[0] - initialStop);
				SubmitChunk(prefix + "1", qty, 1, Close[0] + d * E5TargetR * risk);
				split = qty + " (익절 " + E5TargetR.ToString("F1") + "R, " + E5BreakevenR.ToString("F1") + "R 뒤 본전·트레일링, 개장 범위 "
					+ orLow.ToString("N2") + "~" + orHigh.ToString("N2") + ")";
			}
			else if (IsTrendPos())
			{
				int q1 = (qty * TrendTp1Percent) / 100;
				int q2 = (qty * TrendTp2Percent) / 100;
				// spec 4-4 v6: 1·2차 익절 = 진입가 ± TrendTp1R / TrendTp2R × 손절 거리 (체결되면 체결가 기준으로 다시 검)
				double risk = Math.Abs(refPx - initialStop);
				bool allAtTp1 = earlyEntry && EarlyFullExitAtTp1;	// spec 4-4-2: 개장 직후 진입은 1차 익절점에서 전량
				SubmitChunk(prefix + "1", q1, 1, refPx + d * TrendTp1R * risk);
				SubmitChunk(prefix + "2", q2, 2, refPx + d * (allAtTp1 ? TrendTp1R : TrendTp2R) * risk);
				SubmitChunk(prefix + "3", qty - q1 - q2, 3, allAtTp1 ? refPx + d * TrendTp1R * risk : double.NaN);
				split = q1 + "/" + q2 + "/" + (qty - q1 - q2) + (allAtTp1 ? " (개장 직후: 전량 " + TrendTp1R.ToString("F2") + "R)"
					: " (익절 " + TrendTp1R.ToString("F2") + "R / " + TrendTp2R.ToString("F2") + "R)")
					+ (earlyEntry && EarlyStopMult > 1 ? " 개장 직후 손절 ×" + EarlyStopMult.ToString("F1") : "");
			}
			else
			{
				int q1 = (qty * SideTp1Percent) / 100;
				SubmitChunk(prefix + "1", q1, 1, Close[0] + d * SideTp1Atr * stopAtr);	// 진입 때 ATR로 고정. 체결되면 체결가 기준으로 다시 건다
				SubmitChunk(prefix + "2", qty - q1, 3, double.NaN);
				split = q1 + "/" + (qty - q1);
			}

			SubmitEntry(prefix, qty, stopEntry ? e3StopPx : double.NaN);
			if (stopEntry && e3StopDir == d)
				return;	// 같은 스탑 주문을 다시 건 것: 진입 줄은 처음 걸 때만
			Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 {2} {3} 경로={4} 구간={5}{6} 수량={7} ({8}) 손절={9:F2} 손익비={10} 추세={11} S={12} F={13}",
				Time[0], Instrument.FullName, PosName(p), DirText(d), entryPath + (stopEntry ? " 스탑 주문 " + refPx.ToString("N2") : ""), session, fallback ? " 최소진입보장" : "",
				qty, split + riskNote, stopPrice, double.IsNaN(rr) ? "-" : rr.ToString("F2"), RegimeState(), Signed(SlowNow()), Signed(FastNow())));
		}

		// spec 3-4: 보유 방향의 반대로 전환의심이 뜨면 포지션당 한 번, 남은 수량의 50%를 먼 조각부터 청산하고 이익 중이면 손절을 평단으로
		private void WarnCheck()
		{
			if (warnDone || pos == Pos.None || path == "FORCE" || r7Warn != -dir || Position.MarketPosition == MarketPosition.Flat)
				return;
			warnDone = true;
			int remaining = 0;
			foreach (string sig in liveSignals)
				remaining += Math.Max(0, ChunkLeft(sig));
			int want = (int)Math.Floor(remaining * 0.5);
			string why = string.Format("{0} → 남은 {1}계약의 50%", R7Display(), remaining);
			if (want >= remaining)
			{
				FlattenRemaining("WARN", why);
				return;
			}
			if (want > 0)
				ExitFarChunks(want, "WARN", why);
			double avg = AvgPrice();
			if (dir * (Close[0] - avg) > 0)
				MoveStopTo(avg, "전환의심 뒤 평단", Time[0]);
		}

		// spec 5 최소 진입 강제 채우기 (2026-10-16 사용자 결정): ForceStartTime 봉부터 종료 청산 전까지 진입 횟수 < MinEntries면
		// 보유 포지션을 정리하고, 미보유면 점수 방향(0이면 매수)으로 1계약 시장가 진입 → 다음 봉에 청산을 되풀이
		private bool ForceNeeded()
		{
			if (!ForceMinEntries || entryCount >= MinEntries)
				return false;
			int rel = RelMinutes(ToTime(Time[0]));
			return rel >= RelMinutes(ForceStartTime) && rel < RelMinutes(FlattenTime);
		}

		private void ForceEnter()
		{
			int d = contScore < 0 ? -1 : 1;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 최소 진입 강제 채우기 {2}/{3}회 → {4} 1계약", Time[0], Instrument.FullName, entryCount, MinEntries, DirText(d)));
			CancelE3Stop("최소 진입 강제 채우기");
			Enter(d > 0 ? Pos.TL : Pos.TS, d, "FORCE", 1, true, double.NaN);
		}

		// spec 4-4-1 추가 진입: 추세 포지션(E5 제외), 1차 익절 뒤, 점수 속도가 포지션 방향으로 AddOnMinVel 이상이면 처음 수량 × AddOnPercent를 한 번.
		// 넣기 전에 전체 손절을 평단 이상으로. 추가 조각은 마지막 조각처럼(익절 지정가 없음) 트레일링·기준선·의심·손절로 나감
		private void TryAddOn()
		{
			if (!UseAddOn || !contReady || !IsTrendPos() || path == "E5" || path == "E6" || path == "FORCE" || pos == Pos.None || !tp1Done || addSig.Length > 0)
				return;
			if (Position.MarketPosition == MarketPosition.Flat || entryFillQty <= 0)
				return;
			if (RelMinutes(ToTime(Time[0])) >= RelMinutes(AddOnEndTime) || dir * contVel < AddOnMinVel)
				return;
			int q = Math.Max(1, (int)Math.Floor(baseQty * AddOnPercent / 100.0));
			double avg = AvgPrice();
			MoveStopTo(avg, "추가 진입 본전", Time[0]);
			addSig = pos.ToString() + signalSeq + ".4";
			Out(string.Format("[{0}][{1}][TQ_Strategy] 추가 진입 {2} {3}계약: 1차 익절 뒤 점수 속도 {4:F3} (기준 {5}) 손절={6:F2}",
				Time[0], Instrument.FullName, DirText(dir), q, contVel, AddOnMinVel, stopPrice));
			SubmitChunk(addSig, q, 3, double.NaN);
			addOrder = SubmitOrderUnmanaged(0, EntryAction(dir), OrderType.Market, q, 0, 0, "", addSig + "A");
		}

		// 진입 조각 하나를 기록한다 (주문은 SubmitEntry가 합계로 한 번, 손절·익절은 체결 뒤 Reconcile이 건다)
		private void SubmitChunk(string sig, int qty, int stage, double fixedPrice)
		{
			if (qty <= 0)
				return;
			chunkQty[sig]	= qty;
			chunkStage[sig]	= stage;
			chunkFixed[sig]	= fixedPrice;
			chunkOrder.Add(sig);
			liveSignals.Add(sig);
		}

		// 진입 주문: 조각 합계 수량으로 하나. E3 스탑 진입을 같은 방향으로 다시 걸면 걸어 둔 주문의 가격·수량만 고친다
		private void SubmitEntry(string prefix, int qty, double stopPx)
		{
			tradePrefix = prefix;
			string name = prefix + "E";
			if (!double.IsNaN(stopPx))
			{
				double px = Instrument.MasterInstrument.RoundToTickSize(stopPx);
				if (IsWorking(entryOrder) && entryOrder.Name == name)
				{
					if (!IsChanging(entryOrder) && (entryOrder.StopPrice != px || entryOrder.Quantity != qty))
						ChangeOrder(entryOrder, qty, 0, px);
					return;
				}
				entryOrder = SubmitOrderUnmanaged(0, EntryAction(dir), OrderType.StopMarket, qty, 0, px, "", name);
				return;
			}
			entryOrder = SubmitOrderUnmanaged(0, EntryAction(dir), OrderType.Market, qty, 0, 0, "", name);
		}

		// 예상 손절 거리 (진입 전 손익비 계산용). CalcStop과 같은 식, 기준가 = 지금 종가
		private double ExpectedStopDist(int d, bool trend)
		{
			double swing	= d > 0 ? signals.SwingLow5[0] : signals.SwingHigh5[0];
			return StopDistance(d, trend, Close[0], swing, atr[0]);
		}
		#endregion

		#region 손절 (4-3)
		// 구조 손절 거리: 최근 SwingBars봉 저가(매수)·고가(매도) 한 틱 바깥까지. 신호 봉 ATR × [최소, 최대]로 자르고 StopMinTicks보다 좁으면 벌린다
		private double StopDistance(int d, bool trend, double entryPrice, double swing, double a)
		{
			double dist = double.IsNaN(swing) ? double.NaN : d * (entryPrice - swing) + TickSize;
			if (!double.IsNaN(a) && a > 0)
			{
				double lo = (trend ? TrendStopMinAtr : SideStopMinAtr) * a;
				double hi = Math.Max(lo, (trend ? TrendStopMaxAtr : SideStopMaxAtr) * a);
				dist = double.IsNaN(dist) ? lo : Math.Max(lo, Math.Min(hi, dist));
			}
			if (double.IsNaN(dist))
				dist = StopMinTicks * TickSize;
			return Math.Max(dist, StopMinTicks * TickSize);
		}

		private double CalcStop(double entryPrice)
		{
			double dist = path == "E5" ? E5StopDistance(entryPrice) : path == "E6" ? E6StopDistance(entryPrice) : StopDistance(dir, IsTrendPos(), entryPrice, stopSwing, stopAtr);
			if (earlyEntry && EarlyStopMult > 1)
				dist *= EarlyStopMult;	// spec 4-4-2: 개장 직후 흔들림에 바로 맞지 않게 넓힘 (위험 금액 수량이 계약을 줄여 1회 위험은 같음)
			return Instrument.MasterInstrument.RoundToTickSize(entryPrice - dir * dist);
		}

		// spec 4-10 E5 손절 거리: 개장 범위 중간 한 틱 바깥까지, 신호 봉 ATR × [E5StopMinAtr, E5StopMaxAtr]로 자르고 StopMinTicks보다 좁으면 벌림
		private double E5StopDistance(double entryPrice)
		{
			double dist = dir * (entryPrice - stopSwing) + TickSize;
			if (!double.IsNaN(stopAtr) && stopAtr > 0)
				dist = Math.Max(E5StopMinAtr * stopAtr, Math.Min(Math.Max(E5StopMinAtr, E5StopMaxAtr) * stopAtr, dist));
			return Math.Max(dist, StopMinTicks * TickSize);
		}

		// spec 4-11 E6 손절 거리: 최근 4봉 극값 ∓ RangeFadeStopBuf × ATR, ATR × [최소, 최대]로 자르고 StopMinTicks보다 좁으면 벌림
		private double E6StopDistance(double entryPrice)
		{
			double a = double.IsNaN(stopAtr) ? 0 : stopAtr;
			double dist = dir * (entryPrice - stopSwing) + RangeFadeStopBuf * a;
			if (a > 0)
				dist = Math.Max(RangeFadeStopMinAtr * a, Math.Min(RangeFadeStopMaxAtr * a, dist));
			return Math.Max(dist, StopMinTicks * TickSize);
		}

		// 손절 가격이 바뀌었음: 실제 손절 주문은 Reconcile이 보유 수량 전체로 맞춘다
		private void SetStopForLive(double price)
		{
			Reconcile();
		}

		// 체결 전 임시 손절 주문 가격: 기준가와 같거나 넘으면 거부될 수 있어 최소 1틱 띄운다
		private double OrderSafeStop(double stop, double refPrice)
		{
			return dir > 0 ? Math.Min(stop, refPrice - TickSize) : Math.Max(stop, refPrice + TickSize);
		}

		// 손절을 target으로 옮긴다. 유리한 쪽으로만, 현재 호가와 BreakevenBufferTicks 이상 떨어져 있을 때만 (가까우면 미뤘다가 봉 마감마다 재시도)
		private void MoveStopTo(double target, string label, DateTime time)
		{
			if (liveSignals.Count == 0 || double.IsNaN(target) || target <= 0)
				return;	// 평단을 아직 모를 때(진입 체결 전) 0으로 옮기지 않는다 (7/27 22:33 "목표=0")
			target = Instrument.MasterInstrument.RoundToTickSize(target);
			bool improves = double.IsNaN(stopPrice) || dir * (target - stopPrice) > 0;
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
			Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 이동 → {2} ({3})", time, Instrument.FullName, target, label));
		}

		private bool AtOrBeyondAverage(double stop)
		{
			if (entryFillQty <= 0 || double.IsNaN(stop))
				return false;
			double be = Instrument.MasterInstrument.RoundToTickSize(entryFillSum / entryFillQty);
			return dir * (stop - be) >= 0;
		}

		private bool SafeToMoveStop(double newStop)
		{
			double buffer = Math.Max(1, BreakevenBufferTicks) * TickSize;
			return dir > 0 ? GetCurrentBid(0) - newStop >= buffer : newStop - GetCurrentAsk(0) >= buffer;
		}

		private void RetryDeferredStop()
		{
			if (double.IsNaN(deferredStop) || liveSignals.Count == 0 || entryFillQty <= 0)
				return;
			if (!(double.IsNaN(stopPrice) || dir * (deferredStop - stopPrice) > 0))
			{
				deferredStop = double.NaN;
				return;
			}
			if (!SafeToMoveStop(deferredStop))
				return;
			ApplyStop(deferredStop, lastStopLabel, Time[0]);
		}

		private double AvgPrice()
		{
			return entryFillQty > 0 ? entryFillSum / entryFillQty : Position.AveragePrice;
		}

		// 체결: 체결가(평균)로 손절을 다시 계산한다. 갭으로 이미 손절가를 넘었으면 즉시 전량 청산 (GAP)
		private void OnEntryFill(double fillPrice, int fillQty, DateTime time)
		{
			entryFillSum += fillPrice * fillQty;
			entryFillQty += fillQty;

			bool breached = !double.IsNaN(initialStop) && dir * (fillPrice - initialStop) <= 0;
			if (breached || gapFlattened)
			{
				gapFlattened = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 갭 손절 초과 → 즉시 전량 청산 체결가={2} 손절={3}", time, Instrument.FullName, fillPrice, initialStop));
				FlattenRemaining("GAP", "갭으로 손절가를 넘어 체결");
				return;
			}
			if (breakevenDone)
				return;

			double avg	= entryFillSum / entryFillQty;
			double s	= CalcStop(avg);
			if (dir > 0 ? GetCurrentBid(0) <= s : GetCurrentAsk(0) >= s)
			{
				gapFlattened = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 체결 직후 현재가가 손절가를 넘어섬 → 즉시 전량 청산 손절={2}", time, Instrument.FullName, s));
				FlattenRemaining("GAP", "체결 직후 손절가 초과");
				return;
			}
			stopPrice	= s;
			trStop0		= s;
			SetStopForLive(s);

			// R 익절(TrendTpMode 2)은 체결가 ± R × (체결가 − 손절) (spec 4-4)
			if (IsTrendPos() && path != "E5" && path != "E6")
				foreach (string sig in liveSignals)
				{
					bool allAtTp1 = earlyEntry && EarlyFullExitAtTp1;
					if (chunkStage[sig] == 1 || chunkStage[sig] == 2 || (allAtTp1 && chunkStage[sig] == 3))
					{
						double rr = allAtTp1 || chunkStage[sig] == 1 ? TrendTp1R : TrendTp2R;
						chunkFixed[sig] = avg + dir * rr * Math.Abs(avg - s);
						SetTarget(sig);
					}
				}
			// E5 / E6 익절은 체결가 ± 목표 R × (체결가 − 손절) (spec 4-10, 4-11)
			if (path == "E5" || path == "E6")
				foreach (string sig in liveSignals)
				{
					chunkFixed[sig] = avg + dir * (path == "E5" ? E5TargetR : RangeFadeTargetR) * Math.Abs(avg - s);
					SetTarget(sig);
				}
			// 횡보 1단계 익절은 진입가 + dir × SideTp1Atr × 신호 봉 ATR: 체결가 기준으로 다시 건다
			if (!IsTrendPos())
				foreach (string sig in liveSignals)
					if (chunkStage[sig] == 1)
					{
						chunkFixed[sig] = avg + dir * SideTp1Atr * stopAtr;
						SetTarget(sig);
					}
			Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 재계산(체결가 기준) 평균 체결가={2} 손절={3}", time, Instrument.FullName, avg, s));
			Reconcile();
			// 1차 익절 체결이 진입 체결 콜백보다 먼저 와서 본절을 못 옮긴 경우 (평단을 아직 몰랐음): 지금 옮긴다
			if (tp1Done && !double.IsNaN(tp1FillPrice))
				MoveStopAfterTp1(tp1FillPrice, time);
		}

		// 손절 체결: 사유 코드를 정하고, 횡보 포지션이 손실 손절이면 이번 구간 같은 방향 횡보 재진입을 막는다 (BlockSideAfterStop)
		private void OnStopLossFill(double exitPrice, DateTime time)
		{
			exitReason = !breakevenDone ? "STOP" : stopNote == "트레일링" ? "TRAIL" : "BE_STOP";
			if (entryFillQty <= 0 || breakevenDone || IsTrendPos())
				return;
			double avg = entryFillSum / entryFillQty;
			if (dir * (exitPrice - avg) >= 0)
				return;
			bool already = dir > 0 ? blockSideLong : blockSideShort;	// 손절이 여러 번에 나눠 체결돼도 로그는 한 번
			if (dir > 0)	blockSideLong = true;
			else			blockSideShort = true;
			if (BlockSideAfterStop && !already)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 횡보 {2} 손실 손절 → 이번 대회 구간 같은 방향 횡보 진입 금지", time, Instrument.FullName, DirText(dir)));
		}

		// 이익 보호 본절 (BreakevenAtR, v2와 같은 방식. 사용자 결정 2026-10-15): 1차 익절 전이라도 진입 뒤 최대 유리폭이
		// 처음 손절 거리 × 이 값에 닿으면 진입 봉 마감에 손절을 평단으로. v2는 주력 진입을 뺐지만 v3는 4가지 포지션 모두
		// (9일 시뮬레이션: 0.8R이면 지던 4건이 본전, 이기던 1건(+$985)이 잘림, 합계 +$1,192. 큰 이익 거래는 1차 익절이 먼저라 영향 없음)
		private void ProtectProfitCheck()
		{
			if (path == "E6")
				return;	// spec 4-11: 박스 장 되돌림은 본전 이동 없이 1R 익절·손절·시간
			bool e5 = path == "E5";
			double atR = e5 ? E5BreakevenR : BreakevenAtR;	// spec 4-10: E5는 E5BreakevenR에서 본전, 그 뒤 트레일링
			if (atR <= 0 || tp1Done || (breakevenDone && !e5) || e5Trail || entryFillQty <= 0 || double.IsNaN(trStop0))
				return;
			double avg	= AvgPrice();
			double risk	= Math.Abs(avg - trStop0);
			if (risk <= 0 || trMfe < atR * risk)
				return;
			if (lastStopLabel != "이익 보호 본절")
				Out(string.Format("[{0}][{1}][TQ_Strategy] 최대 이익 {2:F1}pt ≥ 손절 거리 {3:F1}pt × {4} → 손절을 평단으로 (이익 보호{5})",
					Time[0], Instrument.FullName, trMfe, risk, atR, e5 ? ", E5 트레일링 시작" : ""));
			MoveStopTo(avg, "이익 보호 본절", Time[0]);
			if (e5)
				e5Trail = true;
		}

		#endregion

		#region 익절 지정가 (봉 진행 중)
		// 조각의 익절 가격 (진입가 ± R, 횡보 1단계 ± ATR, 체결가 기준으로 다시 계산한 고정 가격). 없으면 NaN
		private double TargetPrice(string sig)
		{
			double fixedPrice;
			return chunkFixed.TryGetValue(sig, out fixedPrice) ? fixedPrice : double.NaN;
		}

		// 익절 지정가는 Reconcile이 조각 남은 수량으로 건다 (가격은 chunkFixed)
		private void SetTarget(string sig)
		{
		}

		// 봉 마감마다 밴드 익절 지정가를 새 밴드 값으로 옮긴다. 체결 콜백이 liveSignals를 바꿀 수 있어 복사본으로 돈다
		private void UpdateTargets()
		{
			Reconcile();
		}

		private void OnTargetFill(string sig, double price, DateTime time)
		{
			// 부분 체결이 여러 번 와도 단계 처리는 한 번. 조각을 끝내는 것은 체결 수량으로 (OnExecutionUpdate의 chunkLeft)
			if (sig == null || !chunkStage.ContainsKey(sig) || !stageFilled.Add(sig))
				return;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 익절(지정가) 체결 시그널={2} 가격={3}", time, Instrument.FullName, sig, price));
			OnStageFill(sig, price, time);
		}

		// 익절 단계가 체결됐을 때: 1차(1단계) → 손절을 평단으로, 2차 → 트레일링 시작 (4-4, 4-6)
		private void OnStageFill(string sig, double price, DateTime time)
		{
			int stage = chunkStage[sig];
			tpStagesHit++;
			if (stage == 1)
			{
				tp1Done		= true;
				exitReason	= "TP1";
				tp1FillPrice = price;
				MoveStopAfterTp1(price, time);
			}
			else if (stage == 2)
			{
				tp2Done		= true;
				exitReason	= "TP2";
			}
		}
		#endregion

		// 1차 익절 뒤 손절 (사용자 결정 2026-10-15): 1차 이익이 처음 손절 거리 × BreakevenMinR 이상이면 평단으로.
		// 모자라면 1차 체결가 ∓ BreakevenFallbackAtr × 1분 ATR로 (유리한 쪽으로만). 7/27 22:39: 1차 이익 5pt에 평단으로 옮겨 5초 뒤 본절로 다 나감
		private void MoveStopAfterTp1(double tp1Price, DateTime time)
		{
			if (entryFillQty <= 0)
				return;	// 평단을 아직 모름: 진입 체결 뒤 OnEntryFill에서 다시 부른다
			double avg		= AvgPrice();
			double risk		= double.IsNaN(trStop0) ? double.NaN : Math.Abs(avg - trStop0);
			double gain		= dir * (tp1Price - avg);
			// 1차 익절가는 틱으로 반올림돼 0.5R보다 한 틱 모자랄 수 있음 (TrendTp1R 0.5 = BreakevenMinR): 한 틱 여유
			if (BreakevenMinR <= 0 || double.IsNaN(risk) || risk <= 0 || gain >= BreakevenMinR * risk - TickSize)
			{
				MoveStopTo(avg, "평단", time);
				return;
			}
			double a = atr[0];
			if (BreakevenFallbackAtr <= 0 || double.IsNaN(a) || a <= 0)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 1차 이익 {2:F1}pt < 손절 거리 {3:F1}pt × {4} → 손절 유지", time, Instrument.FullName, gain, risk, BreakevenMinR));
				return;
			}
			double target = tp1Price - dir * BreakevenFallbackAtr * a;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 1차 이익 {2:F1}pt < 손절 거리 {3:F1}pt × {4} → 평단 대신 1차 체결가 {5} {6} ATR×{7} = {8:F2}로",
				time, Instrument.FullName, gain, risk, BreakevenMinR, tp1Price, dir > 0 ? "−" : "+", BreakevenFallbackAtr, target));
			MoveStopTo(target, "1차 뒤 ATR", time);
		}

		#region 추세 포지션 청산 (4-4, 4-5)
		// 2차 익절 뒤 트레일링: 진입 봉 마감마다 진입 뒤 최고가 − TrailAtr × 1분 ATR (매도는 최저가 + …). 유리한 쪽으로만
		private void UpdateTrailStop()
		{
			if (entryFillQty <= 0)
				return;
			trailExtreme = double.IsNaN(trailExtreme) ? (dir > 0 ? High[0] : Low[0])
				: dir > 0 ? Math.Max(trailExtreme, High[0]) : Math.Min(trailExtreme, Low[0]);
			// 2차 뒤(또는 E5 본전 뒤)는 TrailAtr, 1차 뒤 2차 전은 더 넓게 Tp1TrailAtr (2026-10-15 사용자 결정).
			// 손절은 유리한 쪽으로만 움직이므로(MoveStopTo) 1차 뒤 본전 자리가 더 가까우면 본전에 있다가, 가격이 더 올라 트레일링 선이 본전을 넘을 때부터 따라감
			double k = tp2Done || e5Trail ? TrailAtr : tp1Done && path != "E6" ? Tp1TrailAtr : 0;
			if (!IsTrendPos() || k <= 0)
				return;
			double a = atr[0];
			if (double.IsNaN(a) || a <= 0)
				return;
			double before = stopPrice;
			MoveStopTo(trailExtreme - dir * k * a, "트레일링", Time[0]);
			if (stopPrice != before)
				trailUsed = true;
		}

		// 마지막 조각 기준선 (TrendLastExitMa): 1 중기선, 2 단기선, 0 없음
		private double LastExitMa()
		{
			return TrendLastExitMa == 1 ? regime.SmaMid[0] : TrendLastExitMa == 2 ? regime.SmaFast[0] : double.NaN;
		}

		// 보유 중 진입 봉 마감마다 (순서: 반대 추세 → 추세 종료 → 의심 → 진입 봉 T2 반대 신호 → 마지막 조각)
		private void ManageTrendExits()
		{
			if (path == "B1")
			{
				ManageBreakoutExits();
				return;
			}
			if (path == "E5" || path == "FORCE")
				return;	// spec 4-10: 손절·익절 지정가·본전·트레일링·종료 청산만 (공통 처리). FORCE는 다음 봉에 ForceNeeded가 정리
			if (path == "E6")
			{
				// spec 4-11: 추세 판정·의심으로는 청산하지 않고, RangeFadeMaxHold봉이 지나면 시장가 전량
				if (trBars >= RangeFadeMaxHold)
					FlattenRemaining("TIME", "박스 장 되돌림 최대 보유 " + RangeFadeMaxHold + "분");
				return;
			}
			int st = RegimeState();
			int xs = ExitState();	// spec 3-3: 추세 청산은 청산용 판정으로
			if (xs == dir)
				sawOwnTrend = true;

			// 반대 추세 시작 → 항상 전량 청산 (4-1 원칙 4). 같은 봉 반대 진입은 없음. 다음 봉에 E1을 허용한다 (사용자 결정 2026-10-15)
			if (xs == -dir)
			{
				e1AfterExitDir	= xs;
				e1AfterExitBar	= CurrentBars[0];
				FlattenRemaining("OPP_TREND", "반대 추세 시작");
				return;
			}
			// 추세 종료(횡보) → 전량 청산 (TrendExitOnEnd). 보유 중 그 방향 추세였던 적이 있을 때만 (E3로 횡보 중 들어간 경우를 위해)
			if (TrendExitOnEnd && xs == 0 && sawOwnTrend)
			{
				FlattenRemaining("TREND_END", "추세 종료 (점수 C ≤ " + ScoreExitOff.ToString("F2") + ")");
				return;
			}

			if (HandleSuspect(st))
				return;

			// 진입 봉 T2 반대 신호(MACD 전환 AND 크로스): 추세가 포지션 방향인 동안은 무시. 아닐 때만(또는 TrendExitOnEntryT2) 전량 청산
			bool t2Opp = dir > 0 ? signals.T2Bear[0] : signals.T2Bull[0];
			if (t2Opp && (TrendExitOnEntryT2 || st != dir))
			{
				FlattenRemaining("ENTRY_T2", "진입 봉 T2 반대 신호 (추세가 포지션 방향이 아님)");
				return;
			}

			// 마지막 조각: 2차 뒤에만, 추세 봉 종가가 기준선 반대편으로 마감
			double ma = LastExitMa();
			if (tp2Done && !double.IsNaN(ma) && dir * (Closes[regimeBip][0] - ma) < 0)
				FlattenRemaining("LAST_MA", string.Format("마지막 조각: {0}분봉 종가 {1:N2} {2} {3} {4:N2}", RegimeBarMinutes, Closes[regimeBip][0],
					dir > 0 ? "<" : ">", TrendLastExitMa == 1 ? "중기선" : "단기선", ma));
		}

		// B1 돌파 포지션 청산 (4-9): 추세 판정을 보지 않는다. 순서: 반대 돌파 → 의심(F 기준) → 마지막 조각.
		// 익절 단계·손절·트레일링·이익 보호·종료 청산은 추세 포지션과 같다 (OnBarUpdate의 공통 처리)
		private void ManageBreakoutExits()
		{
			// 반대 돌파: 종가가 직전 BreakoutBars봉의 반대쪽 끝을 넘고 F가 그 방향으로 BreakoutMinFast 이상 → 전량. 같은 봉 반대 진입은 없고 다음 봉에 B1로 다시 판단
			if (BreakoutExitOnOpposite && OppositeBreakout())
			{
				double edge = dir > 0 ? rangeLow[1] : rangeHigh[1];
				FlattenRemaining("OPP_BREAKOUT", string.Format("반대 돌파: 종가 {0:N2} {1} {2}분 {3} {4:N2}, F {5}", Close[0], dir > 0 ? "<" : ">",
					BreakoutBars, dir > 0 ? "저가" : "고가", edge, Signed(FastNow())));
				return;
			}
			if (HandleSuspect(RegimeState()))
				return;
			double ma = LastExitMa();
			if (tp2Done && !double.IsNaN(ma) && dir * (Closes[regimeBip][0] - ma) < 0)
				FlattenRemaining("LAST_MA", string.Format("마지막 조각: {0}분봉 종가 {1:N2} {2} {3} {4:N2}", RegimeBarMinutes, Closes[regimeBip][0],
					dir > 0 ? "<" : ">", TrendLastExitMa == 1 ? "중기선" : "단기선", ma));
		}

		// 현재가 청산 (2026-10-16 사용자 결정: 봉 마감을 기다리지 말고 현재가로 바로). 실시간·Playback에서 체결가가 바뀔 때마다.
		// 1틱 데이터 시리즈로 했더니 불러온 며칠 치 틱을 전부 처리해 Playback 이동이 멈춘 듯 느려져서, 과거 데이터에는 불리지 않는 OnMarketData로 바꿈
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last || State != State.Realtime || CurrentBars[0] < minBars0 || e.Price == tkLastPx)
				return;
			tkLastPx = e.Price;
			TickExits(e.Price);
		}

		private void TickExits(double px)
		{
			if (pos == Pos.None || gapFlattened || Position.MarketPosition == MarketPosition.Flat)
				return;
			// spec 4-3 손절 안전장치: 현재가가 손절가를 BreakevenBufferTicks 넘게 지났는데 포지션이 남음 = 손절 주문 수량이 모자람 (7/24 E5).
			// 손절 스탑은 닿는 순간 체결되므로 여유를 두어 정상 체결과 겹치지 않게 함
			if (!double.IsNaN(stopPrice) && dir * (stopPrice - px) >= Math.Max(1, BreakevenBufferTicks) * TickSize && UnprotectedQty() > 0)
			{
				FlattenRemaining(breakevenDone ? "BE_STOP" : "STOP", string.Format("현재가 {0}가 손절가 {1}를 지났는데 {2}계약 남음", px, stopPrice, Position.Quantity));
				return;
			}
			// spec 4-9 B1 반대 돌파 (B1OppTickExit): 현재가가 마감된 직전 BreakoutBars봉의 반대쪽 끝을 넘고, 현재가로 다시 계산한 F가 그 방향으로 BreakoutMinFast 이상
			if (B1OppTickExit && BreakoutExitOnOpposite && path == "B1" && !double.IsNaN(tkAtr) && !double.IsNaN(tkEdgeLow))
			{
				double edge = dir > 0 ? tkEdgeLow : tkEdgeHigh;
				if (-dir * (px - edge) > 0)
				{
					double fNow = R6(Math.Tanh((px - tkSmaFast) / (0.75 * tkAtr)) + tkMacdSl);
					if (-dir * fNow >= BreakoutMinFast * ScoreScale)
					{
						tickOppExitBar = CurrentBars[0];
						FlattenRemaining("OPP_BREAKOUT", string.Format("반대 돌파(현재가): {0:N2} {1} {2}분 {3} {4:N2}, F {5:+0.00;-0.00}", px, dir > 0 ? "<" : ">",
							BreakoutBars, dir > 0 ? "저가" : "고가", edge, fNow));
					}
				}
			}
		}

		// 보유 방향의 반대 돌파 (B1과 같은 식, 느린 점수 거르기는 안 봄)
		private bool OppositeBreakout()
		{
			double edge = dir > 0 ? rangeLow[1] : rangeHigh[1];
			return -dir * (Close[0] - edge) > 0 && -dir * FastNow() >= BreakoutMinFast;
		}

		// 4-5 의심 이벤트: 포지션 반대 방향 의심이 뜨면 SuspectConfirmBars봉 동안 가격 확인(종가가 직전 SwingBars봉 저점 아래)을 기다렸다가
		// 남은 수량의 SuspectExitPercent(%)를 청산한다. S ≤ SuspectStrongSlow면 SuspectStrongExitPercent. 의심 하나당 한 번. 청산했으면 true
		private bool HandleSuspect(int st)
		{
			// B1 돌파 포지션은 추세 판정과 상관없이 F가 포지션 반대로 ±SuspectFast를 처음 넘은 추세 봉을 의심으로 본다 (4-9)
			bool breakoutPos	= path == "B1";
			bool trigger		= breakoutPos ? fastCrossNow == -dir : suspectEventNow == -dir && st == dir;
			if (trigger && lastRegimeBar != suspectDoneBar)
			{
				suspectEventsSeen++;
				suspectEventBar		= lastRegimeBar;
				suspectEventTime	= Time[0];
				suspectWaitLeft		= SuspectNeedPrice ? SuspectConfirmBars : 1;
				Out(string.Format("[{0}][{1}][TQ_Strategy] {2} 이벤트 → {3}", Time[0], Instrument.FullName, dir > 0 ? "하락의심" : "상승의심",
					SuspectNeedPrice ? "가격 확인 대기 " + SuspectConfirmBars + "봉" : "바로 실행"));
			}
			if (suspectWaitLeft <= 0)
				return false;

			// 의심이 풀렸거나 추세가 바뀌었으면 아무것도 안 함
			bool released = breakoutPos ? dir * FastNow() >= 0 : SuspectActiveNow() != -dir || st != dir;
			if (released)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 의심 해제 → 일부청산 안 함", Time[0], Instrument.FullName));
				EndSuspectWait("CANCEL");
				return false;
			}

			double swingPrev = dir > 0 ? signals.SwingLow5[1] : signals.SwingHigh5[1];	// 직전 SwingBars봉(이 봉 제외)
			bool confirmed = !SuspectNeedPrice || dir * (Close[0] - swingPrev) < 0;
			if (!confirmed)
			{
				suspectWaitLeft--;
				if (suspectWaitLeft <= 0)
				{
					Out(string.Format("[{0}][{1}][TQ_Strategy] 의심 가격 확인 없이 대기 끝 → 일부청산 안 함", Time[0], Instrument.FullName));
					EndSuspectWait("CANCEL");
				}
				return false;
			}

			int S = SlowNow();
			bool strong = dir * S <= SuspectStrongSlow;
			int pct = strong ? SuspectStrongExitPercent : SuspectExitPercent;
			string code = strong ? "SUSPECT_STRONG" : "SUSPECT";
			EndSuspectWait(code);

			int remaining = 0;
			foreach (string sig in liveSignals)
				remaining += Math.Max(0, ChunkLeft(sig));
			int want = (int)Math.Floor(remaining * pct / 100.0);
			string why = string.Format("{0} 확인 (S {1}{2}) → 남은 {3}계약의 {4}%", dir > 0 ? "하락의심" : "상승의심", Signed(S), strong ? ", 강한 의심" : "", remaining, pct);
			if (want >= remaining)
			{
				FlattenRemaining(code, why);
				return true;
			}
			if (want <= 0)
				return false;
			ExitFarChunks(want, code, why);
			// 손절을 평단 이상으로 (이익 중일 때만, 안전 거리 지킴)
			double avg = AvgPrice();
			if (dir * (Close[0] - avg) > 0)
				MoveStopTo(avg, "의심 뒤 평단", Time[0]);
			return liveSignals.Count == 0;
		}

		private void EndSuspectWait(string action)
		{
			suspectWaitLeft	= 0;
			suspectDoneBar	= suspectEventBar;
			suspectAction	= action;
		}

		// 의심 일부청산: 먼 조각부터(마지막 → 2차 → 1차) 통째로 뺀다 (사용자 결정 2026-10-15).
		// 한 조각의 일부만 청산하면 그 조각에 걸린 Set 익절·손절 주문 수량이 어떻게 되는지 공식 문서에서 확인되지 않아, 조각 단위로만 뺀다.
		// 원하는 수량에 가장 가까워지는 곳까지 (적어도 한 조각)
		private void ExitFarChunks(int want, string code, string why)
		{
			List<string> order = new List<string>();
			for (int stage = 3; stage >= 1; stage--)
				foreach (string sig in chunkOrder)
					if (liveSignals.Contains(sig) && chunkStage[sig] == stage)
						order.Add(sig);
			int taken = 0;
			List<string> pick = new List<string>();
			foreach (string sig in order)
			{
				int q = ChunkLeft(sig);
				if (q <= 0)
					continue;
				if (pick.Count > 0 && Math.Abs(taken + q - want) >= Math.Abs(taken - want))
					break;
				pick.Add(sig);
				taken += q;
			}
			Out(string.Format("[{0}][{1}][TQ_Strategy] 일부청산 {2}: {3} → {4}계약 (조각 {5})", Time[0], Instrument.FullName, code, why, taken, string.Join(",", pick)));
			foreach (string sig in pick)
				ExitChunk(sig, code, why);
		}
		#endregion

		#region 횡보 포지션 청산 (4-6)
		private void ManageSideExits()
		{
			int st = ExitState();	// spec 3-3: 반대 추세 청산은 청산용 판정으로
			// 반대 방향 추세 시작 → 전량 (SideExitOnOppTrend). 같은 방향 추세면 횡보 규칙 그대로
			if (SideExitOnOppTrend && st == -dir)
			{
				e1AfterExitDir	= st;	// 추세 포지션과 같이, 다음 봉에 그 추세의 E1을 허용
				e1AfterExitBar	= CurrentBars[0];
				FlattenRemaining("OPP_TREND", "반대 추세 시작");
				return;
			}
			// 진입 봉 종가가 Band(dir, SideRestBand)에 닿으면 나머지 전량
			double rest = Band(dir, SideRestBand);
			if (dir * (Close[0] - rest) >= 0)
			{
				FlattenRemaining("SIDE_REST_BAND", string.Format("종가 {0:N2}가 {1} {2:N2} 도달", Close[0], BandText(dir, SideRestBand), rest));
				return;
			}
			// MACD 반대 전환(이벤트) OR 반대 크로스. SideOscExitAfterTp1이면 1단계 체결 뒤에만
			if (SideOscExitAfterTp1 && !tp1Done)
				return;
			if (dir > 0 ? signals.MacdDown[0] : signals.MacdUp[0])
			{
				FlattenRemaining("SIDE_MACD", dir > 0 ? "MACD 하락 전환" : "MACD 상승 전환");
				return;
			}
			if (dir > 0 ? signals.Dead[0] : signals.Golden[0])
				FlattenRemaining("SIDE_CROSS", dir > 0 ? "데드크로스" : "골든크로스");
		}
		#endregion

		#region 청산 주문
		// 조각 하나를 시장가로 청산. 수량은 그 조각의 실제 보유 수량(chunkLeft)
		private void ExitChunk(string sig, string code, string why)
		{
			if (!liveSignals.Contains(sig))
				return;
			int q = ChunkLeft(sig);
			liveSignals.Remove(sig);
			if (q <= 0)
				return;
			exitReason = code;
			exitSent.Add(sig);
			SubmitMarketExit(q, new List<string> { sig });
			Out(string.Format("[{0}][{1}][TQ_Strategy] 청산 {2} 시그널={3} 수량={4} ({5})", Time[0], Instrument.FullName, code, sig, q, why));
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

		private int ChunkLeft(string sig)
		{
			int q;
			return chunkLeft.TryGetValue(sig, out q) ? q : 0;
		}

		// 전량 청산: 실제 보유 수량에서 이미 낸 시장가 청산을 뺀 만큼 시장가로 (조각 기록은 체결 때 먼 조각부터 줄임)
		private void FlattenRemaining(string code, string why)
		{
			exitReason = code;
			List<string> chunks = new List<string>();
			foreach (string sig in liveSignals.ToArray())
				if (ChunkLeft(sig) > 0)
				{
					exitSent.Add(sig);
					chunks.Add(sig);
				}
			liveSignals.Clear();
			pos = Pos.None;
			if (IsWorking(entryOrder) && PosQty() == 0)
				CancelOrder(entryOrder);	// 아직 체결 안 된 진입은 취소
			int q = PosQty() - plannedExitQty;
			if (q > 0)
				SubmitMarketExit(q, chunks);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 전량 청산 {2} ({3}) 수량={4}", Time[0], Instrument.FullName, code, why, Math.Max(0, q)));
			Reconcile();
		}

		// 청산을 결정했는데 포지션이 남아 있으면 (청산 주문 거부·미체결) 남은 수량을 다시 시장가로. 낸 청산 주문이 아직 살아 있으면 기다린다
		private void FlattenLeftover()
		{
			if (PosQty() == 0 || plannedExitQty > 0)
				return;
			int q = PosQty();
			SubmitMarketExit(q, null);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 남은 포지션 청산 {2} {3}계약", Time[0], Instrument.FullName, Position.MarketPosition, q));
		}

		// 미보유 전환 때: 진입·익절·손절·의심 상태 초기화 (대회 구간·추세 관찰 상태는 그대로)
		private void ResetTradeState()
		{
			pos				= Pos.None;
			dir				= 0;
			path			= "";
			session			= "";
			fallbackEntry	= false;
			sawOwnTrend		= false;
			stopSwing		= double.NaN;
			stopAtr			= double.NaN;
			stopPrice		= double.NaN;
			initialStop		= double.NaN;
			entryFillSum	= 0;
			entryFillQty	= 0;
			gapFlattened	= false;
			breakevenDone	= false;
			e5Trail			= false;
			stopNote		= "";
			deferredStop	= double.NaN;
			lastStopTarget	= double.NaN;
			lastStopLabel	= "";
			trailExtreme	= double.NaN;
			trailUsed		= false;
			tp1Done			= false;
			tp2Done			= false;
			tp1FillPrice	= double.NaN;
			addSig			= "";
			warnDone		= false;
			baseQty			= 0;
			earlyEntry		= false;
			addFillSum		= 0;
			addFillQty		= 0;
			tpStagesHit		= 0;
			suspectWaitLeft	= 0;
			suspectEventsSeen	= 0;
			suspectAction	= "";
			rrExpected		= double.NaN;
			liveSignals.Clear();
			chunkOrder.Clear();
			chunkQty.Clear();
			chunkStage.Clear();
			chunkFixed.Clear();
			chunkLeft.Clear();
			stageFilled.Clear();
			exitSent.Clear();
			chunkFilled.Clear();
			exitOrderChunks.Clear();
			plannedExitQty	= 0;
			// 미보유: 남은 손절·익절 주문이 있으면 취소 (Reconcile이 먼저 거두지만 한 번 더). E3 스탑 진입(entryOrder)은 RouteEntry가 다룸
			if (PosQty() == 0)
			{
				CancelIfWorking(stopOrder);
				foreach (Order t in tpOrders.Values)
					CancelIfWorking(t);
			}
			stopOrder		= null;
			addOrder		= null;
			tpOrders.Clear();
		}
		#endregion

		#region 주문 이벤트
		// 주문 상태 (비관리형): 주문 객체를 이름으로 잡고, 손절 거부 → 즉시 전량 청산, 손절 가격 변경 거부 → 기존 가격 유지 뒤 재시도, 취소·거부된 청산은 계획 수량에서 뺌
		protected override void OnOrderUpdate(Order order, double limitPrice, double orderStopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (order == null)
				return;
			ToRealtimeOrders();
			string name = order.Name;
			// 주문 객체는 이름으로 잡는다: 제출 직후에는 돌려받은 참조가 아직 안 채워졌을 수 있음 (NT8 GetRealtimeOrder·Unmanaged 문서)
			if (tradePrefix.Length > 0)
			{
				if (name == tradePrefix + "E")							entryOrder = order;
				else if (name == tradePrefix + "S")						stopOrder = order;
				else if (addSig.Length > 0 && name == addSig + "A")	addOrder = order;
				else if (name.EndsWith("T") && chunkQty.ContainsKey(name.Substring(0, name.Length - 1)))
					tpOrders[name.Substring(0, name.Length - 1)] = order;
			}
			bool gone = orderState == OrderState.Cancelled || orderState == OrderState.Rejected;
			if (gone && exitOrderChunks.ContainsKey(name))
				plannedExitQty = Math.Max(0, plannedExitQty - Math.Max(0, order.Quantity - order.Filled));	// 체결 안 된 청산 수량은 계획에서 뺌

			if (orderState == OrderState.Rejected || error != ErrorCode.NoError)
				Out(string.Format("[{0}][{1}][TQ_Strategy] 주문 오류 이름={2} 상태={3} 오류={4} 내용={5} 스탑가={6} 수량={7}",
					time, Instrument.FullName, name, orderState, error, comment, order.StopPrice, quantity));
			if (name == tradePrefix + "S")
			{
				if (orderState == OrderState.Rejected && PosQty() > 0)
				{
					gapFlattened = true;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 주문 거부 → 손절 없이 둘 수 없어 즉시 전량 청산", time, Instrument.FullName));
					FlattenRemaining("STOP", "손절 주문 거부");
				}
				else if (error == ErrorCode.UnableToChangeOrder)
				{
					stopPrice		= order.StopPrice;
					breakevenDone	= AtOrBeyondAverage(stopPrice);
					if (!breakevenDone)
						stopNote = "";
					if (!double.IsNaN(lastStopTarget))
						deferredStop = lastStopTarget;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 가격 변경 거부 → 기존 손절 {2} 유지", time, Instrument.FullName, stopPrice));
				}
			}
			if (gone || orderState == OrderState.Accepted || orderState == OrderState.Working)
				Reconcile();
		}

		// 실시간으로 넘어올 때 과거 처리 중 낸 주문 참조를 실시간 주문으로 바꾼다 (NT8 GetRealtimeOrder 문서)
		private void ToRealtimeOrders()
		{
			if (State != State.Realtime)
				return;
			if (entryOrder != null && entryOrder.IsBacktestOrder)	entryOrder = GetRealtimeOrder(entryOrder);
			if (addOrder != null && addOrder.IsBacktestOrder)		addOrder = GetRealtimeOrder(addOrder);
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

		// 손절 주문이 덮지 못하는 보유 수량 (시장가 청산을 낸 수량은 덮인 것으로 봄). 안전장치는 이 값이 있을 때만 청산
		private int UnprotectedQty()
		{
			int covered = plannedExitQty + (IsWorking(stopOrder) ? Math.Max(0, stopOrder.Quantity - stopOrder.Filled) : 0);
			return Math.Max(0, PosQty() - covered);
		}
		private int PosSide()	{ return Position.MarketPosition == MarketPosition.Long ? 1 : Position.MarketPosition == MarketPosition.Short ? -1 : 0; }
		private static OrderAction EntryAction(int d)	{ return d > 0 ? OrderAction.Buy : OrderAction.SellShort; }
		private static OrderAction ExitAction(int d)	{ return d > 0 ? OrderAction.Sell : OrderAction.BuyToCover; }

		// 실제 보유 수량에 맞춰 보호 주문을 맞춘다. 체결·주문 상태 변화·봉 마감마다 부른다 (안에서 다시 불리면 끝난 뒤 한 번 더)
		//  · 미보유 → 손절·익절 취소
		//  · 계획에 없는 포지션(방향이 다르거나 거래 정보 없음, 초과 체결) → 손절·익절을 거두고 시장가로 정리
		//  · 그 외 → 손절 1개 = 보유 − 아직 체결 안 된 시장가 청산, 익절 = 조각 단계 순서로 그 수량 안에서 조각 남은 수량
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
			int P = PosQty(), side = PosSide();
			if (P == 0)
			{
				CancelIfWorking(stopOrder);
				foreach (Order t in tpOrders.Values)
					CancelIfWorking(t);
				return;
			}
			if (dir == 0 || side != dir)
			{
				CancelIfWorking(stopOrder);
				foreach (Order t in tpOrders.Values)
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

			// ── 보호 손절: 보유 수량 전체 하나 ──
			if (need > 0 && !double.IsNaN(stopPrice))
			{
				double px = Instrument.MasterInstrument.RoundToTickSize(stopPrice);
				bool beyond = dir > 0 ? px >= GetCurrentBid(0) : px <= GetCurrentAsk(0);
				if (beyond && !IsWorking(stopOrder))
				{
					// 걸려는 손절가를 현재가가 이미 지남: 걸 수 없으니 바로 청산
					Out(string.Format("[{0}][{1}][TQ_Strategy] 손절가 {2}를 현재가가 이미 지남 → 전량 청산", Time[0], Instrument.FullName, px));
					gapFlattened = true;
					FlattenRemaining(breakevenDone ? "BE_STOP" : "STOP", "손절가를 이미 지남");
					return;
				}
				if (IsGoingAway(stopOrder))
				{
					// 취소 중인 손절이 끝나면 다시 건다 (두 개가 겹치지 않게)
				}
				else if (!IsWorking(stopOrder))
					stopOrder = SubmitOrderUnmanaged(0, ExitAction(dir), OrderType.StopMarket, need, 0, px, "", tradePrefix + "S");
				else if (!IsChanging(stopOrder) && !beyond && (stopOrder.StopPrice != px || stopOrder.Quantity - stopOrder.Filled != need))
					ChangeOrder(stopOrder, stopOrder.Filled + need, 0, px);
			}
			else
				CancelIfWorking(stopOrder);

			// ── 익절 지정가: 단계 순서로, 손절이 덮는 수량 안에서 ──
			int room = need;
			foreach (string sig in chunkOrder)
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
					tpOrders[sig] = SubmitOrderUnmanaged(0, ExitAction(dir), OrderType.Limit, want, lp, 0, "", sig + "T");
				else if (!IsChanging(t) && (t.LimitPrice != lp || t.Quantity - t.Filled != want))
				{
					if (t.Filled > 0 && t.Quantity - t.Filled < want)
						continue;	// 일부 체결된 지정가는 늘리지 않음
					ChangeOrder(t, t.Filled + want, lp, 0);
				}
			}
		}

		// 진입 체결을 조각 순서(1차 → 2차 → 마지막)로 나눈다. 계획보다 많이 체결되면(겹친 진입) 마지막 조각에
		private void AllocateEntry(int q)
		{
			string last = null;
			foreach (string sig in chunkOrder)
			{
				if (sig == addSig)
					continue;
				last = sig;
				int f;
				chunkFilled.TryGetValue(sig, out f);
				int take = Math.Min(q, Math.Max(0, chunkQty[sig] - f));
				if (take <= 0)
					continue;
				chunkFilled[sig]	= f + take;
				chunkLeft[sig]		= ChunkLeft(sig) + take;
				q -= take;
				if (pos != Pos.None && !exitSent.Contains(sig) && !liveSignals.Contains(sig))
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
			TradeExitFill(name, price, take, time, sig);
			if (chunkLeft[sig] <= 0)
				liveSignals.Remove(sig);
			return take;
		}

		// 청산 체결 수량을 조각에서 뺀다: 지정한 조각 먼저, 그다음 먼 조각부터 (추가 → 마지막 → 2차 → 1차)
		private void ReduceChunks(int q, double price, DateTime time, string name, List<string> first)
		{
			if (first != null)
				foreach (string sig in first)
				{
					if (q <= 0)
						break;
					q -= ReduceChunk(sig, q, price, time, name);
				}
			List<string> order = new List<string>();
			if (addSig.Length > 0)
				order.Add(addSig);
			for (int st = 3; st >= 1; st--)
				foreach (string sig in chunkOrder)
					if (sig != addSig && chunkStage[sig] == st)
						order.Add(sig);
			foreach (string sig in order)
			{
				if (q <= 0)
					break;
				q -= ReduceChunk(sig, q, price, time, name);
			}
			if (q > 0)
				TradeExitFill(name, price, q, time, "");	// 조각 기록 밖 수량 (계획에 없는 포지션 정리)
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;
			Order o		= execution.Order;
			string name	= o.Name;
			bool isEntry	= o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.SellShort;
			int ed			= o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover ? 1 : -1;	// 이 체결의 매수(+1)·매도(−1)
			AddMark(name, price, quantity, ed > 0, isEntry, time);

			if (isEntry)
			{
				if (addSig.Length > 0 && name == addSig + "A")
				{
					chunkLeft[addSig] = ChunkLeft(addSig) + quantity;
					if (pos != Pos.None && !exitSent.Contains(addSig) && !liveSignals.Contains(addSig))
						liveSignals.Add(addSig);
					addFillSum += price * quantity;
					addFillQty += quantity;
					Out(string.Format("[{0}][{1}][TQ_Strategy] 추가 진입 체결 가격={2} 수량={3} 손절={4:F2}", time, Instrument.FullName, price, quantity, stopPrice));
				}
				else if (dir != 0 && ed == dir && (name == tradePrefix + "E" || pos != Pos.None))
				{
					AllocateEntry(quantity);
					OnEntryFill(price, quantity, time);
				}
				else
					Out(string.Format("[{0}][{1}][TQ_Strategy] 계획에 없는 진입 체결 {2} 가격={3} 수량={4} → 정리", time, Instrument.FullName, name, price, quantity));
			}
			else
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 청산 체결 {2} 가격={3} 수량={4}", time, Instrument.FullName, name, price, quantity));
				List<string> chunks;
				string tsig = name.EndsWith("T") ? name.Substring(0, name.Length - 1) : "";
				// 조각 수량을 먼저 줄이고 단계 처리(손절 이동 → Reconcile)를 한다. 거꾸로 하면 Reconcile이 아직 남은 줄 알고 익절을 다시 냄
				if (name == tradePrefix + "S")
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
				}
				else
					ReduceChunks(quantity, price, time, name, null);
			}
			Reconcile();
			UpdateStatusPanel(null);
		}
		#endregion

		#region 거래 기록과 학습 데이터 (7장) — 매매 판단에는 영향 없음
		private void TradeBegin()
		{
			trOpen		= true;
			trStop0		= double.NaN;
			trPnlPts	= 0;
			trMfe		= 0;
			trMae		= 0;
			trBars		= 0;
			trExits		= "";
			exitReason	= "";
		}

		private void TradeTrack()
		{
			if (!trOpen || entryFillQty <= 0 || Position.MarketPosition == MarketPosition.Flat)
				return;
			double avg = entryFillSum / entryFillQty;
			trBars++;
			trMfe = Math.Max(trMfe, dir > 0 ? High[0] - avg : avg - Low[0]);
			trMae = Math.Max(trMae, dir > 0 ? avg - Low[0] : High[0] - avg);
		}

		private void TradeExitFill(string name, double price, int quantity, DateTime time, string fromSig)
		{
			if (!trOpen || entryFillQty <= 0)
				return;
			// 추가 진입 조각의 청산은 추가분 체결가 기준 (처음 진입 평단과 섞지 않음)
			double avg = fromSig == addSig && addFillQty > 0 ? addFillSum / addFillQty : entryFillSum / entryFillQty;
			trPnlPts	+= dir * (price - avg) * quantity;
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
			sessPnlUsd += trPnlPts * Instrument.MasterInstrument.PointValue;	// spec 4-11 하루 손실 한도
			if (DayLossLimitR > 0 && sessPnlUsd <= -DayLossLimitR * RiskPerTrade && !dayCutLogged)
			{
				dayCutLogged = true;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 하루 손실 ${2:N0} ≤ −{3}R(${4:N0}) → 이후 진입 수량 {5}%", Time[0], Instrument.FullName,
					sessPnlUsd, DayLossLimitR, DayLossLimitR * RiskPerTrade, DayLossQtyPercent));
			}
		}

		// 열: main의 열 + v3 추가 (7장). '#'으로 시작하는 줄은 실행별 설정 요약 (read_csv(comment='#'))
		private const string MlHeader =
			"run_id,settings_id,mode,instrument,bar_min,"
			+ "signal_time,weekday,hhmm,strategy,regime,dir,via_t2,"
			+ "close,atr,atr_pct,stop_dist_atr,mid_dist_atr,sma20_dist_atr,"
			+ "macd_hist_atr,macd_slope_atr,k,d,k_minus_d,rsi,"
			+ "macd_up,macd_down,golden,dead,bar_range_atr,swing_range_atr,vol_ratio,"
			+ "htf_sma20_dist_atr,regime_age,regime_changes6,session_entry_no,prev_r,min_since_prev_exit,"
			+ "path,session,s1,s2,s3,s4,f1,f2,slow,fast,pre_box_high,pre_box_low,atr_entry,rr_expected,fallback_entry,"
			+ "exit_time,hold_bars,qty,avg_price,risk_pts,pnl_pts,pnl_usd,pnl_r,mfe_pts,mae_pts,mfe_r,mae_r,win,exits,"
			+ "suspect_events,suspect_action,tp_stages_hit,trail_used,exit_reason";

		// 신호 봉에서 특징값을 잰다 (Enter에서)
		private void MlBegin()
		{
			mlFeatures = "";
			if (!MlLogToFile)
				return;
			double a	= atr[0];
			double c	= Close[0];
			bool okA	= a > 0;
			int r		= RegimeState();

			int volBars = Math.Min(20, CurrentBar);
			double volSum = 0;
			for (int i = 1; i <= volBars; i++)
				volSum += Volume[i];
			double volRatio = volSum > 0 ? Volume[0] / (volSum / volBars) : double.NaN;

			int maxBack = Math.Min(200, CurrentBars[regimeBip]);
			int age = 1;
			while (age < maxBack && !double.IsNaN(regime.Regime[age]) && (int)regime.Regime[age] == r)
				age++;
			int changes = 0;
			for (int i = 0; i < 6 && i + 1 <= CurrentBars[regimeBip]; i++)
				if (regime.Regime[i] != regime.Regime[i + 1])
					changes++;
			double htfDist = double.IsNaN(regime.SmaFast[0]) ? double.NaN : Closes[regimeBip][0] - regime.SmaFast[0];

			// 점수 항목은 TQ_Regime.ScoreText("S1▲ S2▲ S3− S4▲ │ F1▼ F2▼")에서 읽는다
			string st = regime.ScoreText;
			mlFeatures = string.Join(",", new string[]
			{
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
				((int)Time[0].DayOfWeek).ToString(),
				(ToTime(Time[0]) / 100).ToString(),
				pos.ToString(),
				r.ToString(),
				dir.ToString(),
				"0",	// via_t2: v3에는 없음. main 열 순서를 지키려고 칸만 남긴다
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
				(entryCount + 1).ToString(),
				Ml(mlPrevR),
				mlPrevExit == DateTime.MinValue ? "" : Ml((Time[0] - mlPrevExit).TotalMinutes),
				path,
				session,
				ScoreItem(st, "S1"), ScoreItem(st, "S2"), ScoreItem(st, "S3"), ScoreItem(st, "S4"), ScoreItem(st, "F1"), ScoreItem(st, "F2"),
				SlowNow().ToString(),
				FastNow().ToString(),
				Ml(boxHigh),
				Ml(boxLow),
				Ml(a),
				Ml(rrExpected),
				fallbackEntry ? "1" : "0"
			});
		}

		// ScoreText에서 항목 하나의 값 (+1 / −1 / 0). 그 항목이 없으면(S4 끔) 빈칸
		private static string ScoreItem(string text, string item)
		{
			int i = text.IndexOf(item);
			if (i < 0 || i + item.Length >= text.Length)
				return "";
			char ch = text[i + item.Length];
			return ch == '▲' ? "1" : ch == '▼' ? "-1" : "0";
		}

		private void MlEnd()
		{
			if (entryFillQty <= 0)
				return;
			double avg		= entryFillSum / entryFillQty;
			double pnlPts	= trPnlPts / entryFillQty;
			double risk		= double.IsNaN(trStop0) ? double.NaN : Math.Abs(avg - trStop0);
			bool okR		= !double.IsNaN(risk) && risk > 0;
			double pnlR		= okR ? pnlPts / risk : double.NaN;
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
					trExits,
					suspectEventsSeen.ToString(),
					suspectAction,
					tpStagesHit.ToString(),
					trailUsed ? "1" : "0",
					exitReason
				}));
				System.IO.File.AppendAllText(mlPath, sb.ToString(), new System.Text.UTF8Encoding(true));
			}
			catch (Exception)
			{
				// 파일을 못 써도 매매에는 영향이 없게 한다
			}
			mlFeatures = "";
		}

		private string Ml(double v)
		{
			if (double.IsNaN(v) || double.IsInfinity(v))
				return "";
			return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
		}

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

		// 추세 봉 마감 줄 (채점기 --validate가 읽는 형식: "N분봉 마감 추세=… S=… F=… (…) … 종가=… 이평=… MACD선=… RSI=…")
		private void LogRegimeBar()
		{
			if (State == State.Historical)
				return;	// LogEntryBar와 같음
			int st = RawRegimeState();
			int ev = regime.SuspectEvent[0];
			int ts = regime.TrendStart[0];
			Out(string.Format("[{0}][{1}][TQ_Strategy] {2}분봉 마감 추세={3} S={4} F={5} ({6}) 추세시작={7} 의심={8} 종가={9} 이평={10:F2}/{11:F2}/{12:F2} MACD선={13:F2} RSI={14:F2}",
				Time[0], Instrument.FullName, RegimeBarMinutes, StateText(st), Signed((int)regime.SlowScore[0]), Signed((int)regime.FastScore[0]), regime.ScoreText,
				ts == 0 ? "-" : (ts > 0 ? "상승" : "하락"),
				ev != 0 ? (ev < 0 ? "하락의심 이벤트" : "상승의심 이벤트") : regime.SuspectActive[0] != 0 ? "의심 중" : "-",
				Closes[regimeBip][0], regime.SmaFast[0], regime.SmaMid[0], regime.SmaSlow[0], regime.MacdLine[0], regime.Rsi[0]));
		}

		// 진입 봉 마감 줄: 추세·점수·의심·구간·후보 경로별 상태·보유 상태·시고저종 (7장)
		private void LogEntryBar(List<Candidate> cands)
		{
			if (State == State.Historical)
				return;	// 매 봉 줄은 실시간만 (과거 며칠 치를 Output 창에 찍으면 시작·Playback 이동이 느려짐. 로그 파일은 원래 실시간만)
			string cand = "";
			if (cands != null)
				foreach (Candidate c in cands)
					cand += (cand.Length > 0 ? " " : "") + c.Path + (c.Dir > 0 ? "매수" : "매도") + "=" + c.Code;
			string hold = Position.MarketPosition == MarketPosition.Flat ? "없음"
				: string.Format("{0} {1}계약 손절={2:F2}{3}{4}", PosName(pos), Position.Quantity, stopPrice,
					suspectWaitLeft > 0 ? " 의심확인대기=" + suspectWaitLeft : "", tp2Done ? " 2차뒤" : tp1Done ? " 1차뒤" : "");
			string cont = string.Format(" 점수C={0:F6} 속도={1:F6} 연속S={2:F6} 연속F={3:F6} 레짐7={4} 전환의심={5}", contScore, contVel, contSlow, contFast, r7, r7Warn);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 추세={2} S={3} F={4} ({5}){14} 의심={6} 구간={7} 후보 {8} 보유={9} 시고저종={10}/{11}/{12}/{13}",
				Time[0], Instrument.FullName, RegimeState(), Signed(SlowNow()), Signed(FastNow()), regime.ScoreText,
				SuspectActiveNow() < 0 ? "하락의심" : SuspectActiveNow() > 0 ? "상승의심" : "-",
				ZoneText(ZoneNow()), cand.Length > 0 ? cand : "-", hold, Open[0], High[0], Low[0], Close[0], cont));
		}
		#endregion

		#region 화면 표시 — 매매 판단에는 영향 없음
		// 진입 봉마다: 추세 배경색(의심 중이면 주황·하늘색), 장전 박스 가로선(22:30 이후)
		private void PaintBar()
		{
			if (!ShowStatus || ChartControl == null)
				return;
			int st = RegimeState();
			int sus = SuspectActiveNow();
			// spec 3-4: 배경 = 7레짐. 추세 진하게, 시작의심 옅게, 종료의심 주황·하늘색
			int a7 = Math.Abs(r7);
			// 추세 진하게, 눌림·시작의심 옅게, 전환의심(반대 방향 빠른 출발) 주황(상승 중 하락 전환의심)·하늘색(하락 중 상승 전환의심)
			BackBrush = r7Warn != 0 ? (r7Warn < 0 ? upSuspectBrush : downSuspectBrush)
				: a7 == 2 ? (r7 > 0 ? upBackBrush : downBackBrush)
				: a7 == 3 || a7 == 1 ? (r7 > 0 ? upStartBrush : downStartBrush) : null;

			if (BoxReady())
			{
				// 장전 박스 선: 구간 시작 ~ 지금 봉의 고가·저가를 기록만 하고 그리기는 OnRender (2026-10-22: 그림은 OnRender, 계산은 OnBarUpdate)
				lock (renderLock)
				{
					if (boxLines.Count == 0 || boxLines[boxLines.Count - 1].Tag != sessionTag)
						boxLines.Add(new BoxLine { Tag = sessionTag });
					BoxLine bl = boxLines[boxLines.Count - 1];
					bl.Start = boxStartTime; bl.End = Time[0]; bl.High = boxHigh; bl.Low = boxLow;
					if (boxLines.Count > 30)
						boxLines.RemoveAt(0);
				}
			}
		}

		// ── 왼쪽 위 상태 상자 (main d05a041의 그리기 방식 그대로) ──
		// 계산은 UpdateStatusPanel, 그리기는 OnRender. 줄 색은 맨 앞 기호: ● 충족·완료(초록), ○ 기다리는 조건(흰색), × 막힘(회색), 두 칸 들여쓴 줄 = 참고(옅은 회색)
		private class PanelSection
		{
			public string		Title;
			public Color		Tone;
			public bool			Headline;
			public List<string>	Lines = new List<string>();
		}

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

		private List<PanelSection> panelSections;

		private static readonly Color ToneUp		= Color.FromRgb(0, 150, 60);
		private static readonly Color ToneUpSus		= Color.FromRgb(210, 130, 0);
		private static readonly Color ToneDown		= Color.FromRgb(190, 30, 30);
		private static readonly Color ToneDownSus	= Color.FromRgb(0, 130, 210);
		private static readonly Color ToneSide		= Color.FromRgb(110, 110, 110);
		private static readonly Color ToneEntry		= Color.FromRgb(70, 90, 170);
		private static readonly Color TonePosLong	= Color.FromRgb(0, 120, 90);
		private static readonly Color TonePosShort	= Color.FromRgb(160, 50, 70);
		private static readonly Color ToneTp		= Color.FromRgb(180, 140, 0);
		private static readonly Color ToneStop		= Color.FromRgb(170, 0, 140);
		private static readonly Color ToneExit		= Color.FromRgb(40, 110, 180);

		private Color StateTone(int st, int sus)
		{
			return st == 1 ? (sus != 0 ? ToneUpSus : ToneUp) : st == -1 ? (sus != 0 ? ToneDownSus : ToneDown) : ToneSide;
		}

		// 상태 상자 내용과 손절선·평단선 (실시간·Playback에서만). 6장 구역: 현재 상태 / 진입 조건(미보유) / 포지션 / 익절 / 손절 / 청산
		private void UpdateStatusPanel(List<Candidate> cands)
		{
			if (!ShowStatus || State != State.Realtime || CurrentBars[0] < minBars0 || double.IsNaN(regime.Regime[0]))
				return;

			StatusPanelBuilder sb = new StatusPanelBuilder();
			int st	= RegimeState();
			int sus	= SuspectActiveNow();
			int S	= SlowNow();
			int F	= FastNow();
			Zone z	= ZoneNow();

			// ── 현재 상태 ──
			sb.Section("현재 상태", StateTone(st, sus), true);
			sb.AppendFormat("{0}{1}   {2:HH:mm}\n", StateText(st), sus < 0 ? " · 하락의심" : sus > 0 ? " · 상승의심" : "", Times[0][0]);
			sb.AppendFormat("7레짐: {0}{1}\n", R7Display(), r7Age >= 0 ? string.Format(" (확정 {0}봉째, E1은 {1}봉째)", r7Age, E1DelayBars) : "");
			sb.AppendFormat("점수 C {0:+0.000;-0.000} · 속도 {1:+0.000;-0.000} · 연속 S {2:+0.00;-0.00} (→{3}) · F {4:+0.00;-0.00} (→{5})\n",
				contScore, contVel, contSlow, Signed(S), contFast, Signed(F));
			if (suspectWaitLeft > 0)
			{
				double swingPrev = dir > 0 ? signals.SwingLow5[0] : signals.SwingHigh5[0];
				sb.AppendFormat("  의심: {0:HH:mm} 발생 → 가격 확인 대기 {1}/{2}봉 (종가 {3} {4:N2})\n", suspectEventTime,
					SuspectConfirmBars - suspectWaitLeft + 1, SuspectConfirmBars, dir > 0 ? "<" : ">", swingPrev);
			}
			else if (sus != 0)
				sb.Append("  의심 중 (" + (sus < 0 ? "하락의심" : "상승의심") + ")\n");
			string box = double.IsNaN(boxHigh) ? "" : string.Format(" · 장전 박스 {0:N2} ~ {1:N2}{2}", boxLow, boxHigh, BoxReady() ? "" : " (기록 중)");
			sb.Append(ZoneText(z) + (z == Zone.Pre ? " (수량 " + PreOpenQtyPercent + "%)" : "") + box + "\n");
			if (UseEntryRangeFade && sessBars > 0)
				sb.AppendFormat("박스 장 판단: 효율비 {0:F2} (≤ {1}이면 박스 장, {2}/{3}봉){4}\n", SessionEr(), RangeFadeMaxEr, Math.Min(sessBars, RangeFadeMinBars), RangeFadeMinBars,
					RangeDay() ? " → 박스 장" : "");
			if (DayLossLimitR > 0 && sessPnlUsd != 0)
				sb.AppendFormat("오늘 손익: ${0:N0}{1}\n", sessPnlUsd, sessPnlUsd <= -DayLossLimitR * RiskPerTrade ? " → 손실 한도, 수량 " + DayLossQtyPercent + "%" : "");
			if (UseEntryOpenRange && !double.IsNaN(orHigh))
				sb.AppendFormat("개장 {0}분 범위: {1:N2} ~ {2:N2}{3}\n", E5RangeMinutes, orLow, orHigh, OpenRangeReady() ? "" : " (기록 중)");
			if (UseBreakoutEntry)
				sb.AppendFormat("돌파 범위 ({0}분): {1:N2} ~ {2:N2}{3}\n", BreakoutBars, rangeLow[1], rangeHigh[1],
					lastBreakoutDir == 0 ? "" : string.Format(" · 마지막 돌파 {0} {1:HH:mm}", lastBreakoutDir > 0 ? "▲" : "▼", lastBreakoutTime));
			sb.AppendFormat("{0}분봉: MACD {1}  %K {2:F0} / %D {3:F0}  RSI {4:F0}  ATR {5:F1}\n", BarsPeriod.Value,
				signals.MacdUp[0] ? "▲ 전환" : signals.MacdDown[0] ? "▼ 전환" : signals.MacdRising[0] ? "▲" : signals.MacdFalling[0] ? "▼" : "−",
				signals.K[0], signals.D[0], signals.Rsi[0], atr[0]);

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				renderStop	= double.NaN;
				renderAvg	= double.NaN;
				sb.Section("진입 조건", ToneEntry);
				if (cands != null)
				{
					// 그 추세에서 할 수 있는 방향을 먼저, 경로 순서대로
					for (int k = 0; k < 2; k++)
					{
						int d = (k == 0) == (st >= 0) ? 1 : -1;
						bool any = false;
						foreach (Candidate c in cands)
						{
							if (c.Dir != d || c.Code == "off")	// 꺼진 경로는 상자에 안 그림 (로그에는 남음). v4는 E1~E4가 기본 끔
								continue;
							if (!any)
							{
								sb.Append((d > 0 ? "매수" : "매도") + "\n");
								any = true;
							}
							sb.Append(c.Line + "\n");
						}
					}
				}
				sb.AppendFormat("  진입 {0}/{1}회 · 장전 수량 {2}% ({3}~{4})\n", entryCount, MinEntries, PreOpenQtyPercent,
					TimeText(MinOfDayToHhmmss(MinOfDay(ContestStartTime) + NewsBlockMinutes)), TimeText(MinOfDayToHhmmss(MinOfDay(OpenTime) - OpenBlockBefore)));
			}
			else
			{
				double avg	= AvgPrice();
				double pts	= dir * (Closes[0][0] - avg);
				sb.Section("포지션", dir > 0 ? TonePosLong : TonePosShort);
				sb.AppendFormat("{0} {1} @ {2:N2}  {3}{4:N2}  ({5}, {6}{7})\n", DirText(dir), Position.Quantity, avg, pts >= 0 ? "+" : "", pts,
					PathName(path), session == "pre" ? "장전" : "장후", fallbackEntry ? ", 최소 진입 보장" : "");
				renderStop	= stopPrice;	// 손절선·평단선은 OnRender가 그림
				renderAvg	= avg;
				AppendExitSections(sb, st, S);
			}
			panelSections = sb.Sections;
		}

		// 보유 중 익절·손절·청산 구역
		private void AppendExitSections(StatusPanelBuilder sb, int st, int S)
		{
			// ── 익절 ──
			sb.Section("익절", ToneTp);
			foreach (string sig in chunkOrder)
			{
				int stage = chunkStage[sig];
				bool live = liveSignals.Contains(sig);
				if (stage == 3)
				{
					if (!live)
						continue;
					if (IsTrendPos())
					{
						double ma = LastExitMa();
						sb.AppendFormat("○ 마지막 {0}계약: {1}\n", chunkQty[sig], double.IsNaN(ma) ? "트레일링 손절로만"
							: string.Format("2차 뒤 {0}분 종가 {1} {2} {3:N2}{4}", RegimeBarMinutes, dir > 0 ? "<" : ">", TrendLastExitMa == 1 ? "중기선" : "단기선", ma,
								tp2Done ? "" : " (2차 전에는 안 봄)"));
					}
					else
						sb.AppendFormat("○ 나머지 {0}계약: 종가 {1} {2:N2} 도달 · MACD{3} · {4}{5}\n", chunkQty[sig], BandText(dir, SideRestBand), Band(dir, SideRestBand),
							dir > 0 ? "▼" : "▲", dir > 0 ? "데드" : "골든", SideOscExitAfterTp1 && !tp1Done ? " (MACD·크로스는 1단계 뒤)" : "");
					continue;
				}
				string label = path == "E5" ? "익절 " + E5TargetR.ToString("F1") + "R" : IsTrendPos() ? (stage == 1 ? "1차 " + TrendTp1R.ToString("F2") + "R" : "2차 " + TrendTp2R.ToString("F2") + "R")
					: "1단계 진입가" + (dir > 0 ? "+" : "−") + SideTp1Atr + "ATR";
				if (!live)
					sb.AppendFormat("● {0} ({1}계약 체결)\n", label, chunkQty[sig]);
				else
					sb.AppendFormat("○ {0} {1:N2} → {2}계약\n", label, TargetPrice(sig), chunkQty[sig]);
			}

			// ── 손절 ──
			sb.Section("손절", ToneStop);
			if (!double.IsNaN(stopPrice))
				sb.AppendFormat("{0:N2}{1}{2}\n", stopPrice, stopNote.Length > 0 ? " (" + stopNote + ")" : " (구조 손절)",
					double.IsNaN(deferredStop) ? "" : string.Format(" → {0:N2} 이동 대기", deferredStop));
			if (IsTrendPos() && TrailAtr > 0)
				sb.Append(tp2Done ? string.Format("○ 트레일링: {0} {1:N2} {2} {3} ATR\n", dir > 0 ? "최고가" : "최저가", trailExtreme, dir > 0 ? "−" : "+", TrailAtr)
					: tp1Done ? "○ 1차 뒤 트레일링: " + (dir > 0 ? "최고가 − " : "최저가 + ") + Tp1TrailAtr + " ATR (본전보다 나을 때부터), 2차 뒤 " + TrailAtr + " ATR\n"
					: "  → 1차 뒤 " + Tp1TrailAtr + " ATR, 2차 뒤 " + TrailAtr + " ATR 트레일링\n");
			if (!tp1Done)
				sb.Append("  → 1" + (IsTrendPos() ? "차" : "단계") + " 익절 뒤 평단으로\n");

			// ── 청산 ──
			sb.Section("청산", ToneExit);
			if (pos == Pos.None)
				sb.Append("● 청산 주문 냄, 체결 대기\n");
			else if (path == "E6")
			{
				sb.AppendFormat("○ 익절 {0:F1}R 지정가, {1}분이면 시장가 청산 (지금 {2}분)\n", RangeFadeTargetR, RangeFadeMaxHold, trBars);
				sb.Append("  추세 판정·의심으로는 청산하지 않음\n");
			}
			else if (path == "E5")
			{
				sb.AppendFormat("○ 익절 {0:F1}R 지정가, 최대 이익 {1:F1}R이면 본전 → 트레일링 {2} ATR{3}\n", E5TargetR, E5BreakevenR, TrailAtr, e5Trail ? " (트레일링 중)" : "");
				sb.Append("  추세 판정·의심으로는 청산하지 않음\n");
			}
			else if (path == "B1")
			{
				if (BreakoutExitOnOpposite)
					sb.AppendFormat("○ 반대 돌파: 종가 {0} {1}분 {2} {3:N2} AND F {4} {5}\n", dir > 0 ? "<" : ">", BreakoutBars, dir > 0 ? "저가" : "고가",
						dir > 0 ? rangeLow[1] : rangeHigh[1], dir > 0 ? "≤" : "≥", Signed(-dir * BreakoutMinFast));
				string sName = "F " + (dir > 0 ? "≤ " : "≥ ") + Signed(-dir * SuspectFast);
				if (suspectWaitLeft > 0)
					sb.AppendFormat("● 의심({0}): 가격 확인 대기 중 (확인되면 {1}%, S {2} 이하면 {3}%)\n", sName, SuspectExitPercent, Signed(dir * SuspectStrongSlow), SuspectStrongExitPercent);
				else
					sb.AppendFormat("○ 의심({0}) 뒤 가격 확인 시 {1}% (S {2} 이하면 {3}%)\n", sName, SuspectExitPercent, Signed(dir * SuspectStrongSlow), SuspectStrongExitPercent);
				sb.Append("  추세 판정(상승·횡보·하락)으로는 청산하지 않음\n");
			}
			else if (IsTrendPos())
			{
				string sName = dir > 0 ? "하락의심" : "상승의심";
				if (suspectWaitLeft > 0)
					sb.AppendFormat("● {0}: 가격 확인 대기 중 (확인되면 {1}%, S {2} 이하면 {3}%)\n", sName, SuspectExitPercent, Signed(dir * SuspectStrongSlow), SuspectStrongExitPercent);
				else
					sb.AppendFormat("○ {0} 확인 시 {1}% (S {2} 이하면 {3}%)\n", sName, SuspectExitPercent, Signed(dir * SuspectStrongSlow), SuspectStrongExitPercent);
				if (TrendExitOnEnd)
					sb.AppendFormat("○ 추세 종료 (S {0} {1}){2}\n", dir > 0 ? "≤" : "≥", Signed(dir * TrendEndSlow), sawOwnTrend || st == dir ? "" : " — 추세가 포지션 방향이 된 뒤부터");
				sb.Append("○ 반대 추세 시작\n");
				sb.Append(TrendExitOnEntryT2 || st != dir ? "○ 1분 T2 반대 신호 (MACD 전환 + 크로스)\n" : "  1분 T2 반대 신호: 추세 유지 중 무시\n");
			}
			else
			{
				if (SideExitOnOppTrend)
					sb.Append("○ 반대 추세 시작\n");
				sb.Append("  같은 방향 추세 시작: 횡보 규칙 그대로\n");
			}
			sb.Append("○ " + TimeText(FlattenTime) + " 종료 청산\n");
		}

		// 상태 상자 그리기 (main과 같음). 계산 없이 panelSections만 그리고 SharpDX 자원은 그릴 때 만들고 Dispose
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (!ShowStatus || RenderTarget == null || ChartPanel == null || ChartBars == null)
				return;
			List<IDisposable> marksTrash = new List<IDisposable>();
			try
			{
				RenderChartMarks(chartControl, chartScale, marksTrash);
			}
			finally
			{
				foreach (IDisposable d in marksTrash)
					d.Dispose();
			}

			List<PanelSection> sections = panelSections;
			if (sections == null || sections.Count == 0)
				return;

			float k100		= Math.Max(50, StatusPanelScale) / 100f;
			float margin	= 8f * k100, pad = 6f * k100, gap = 6f * k100, lineGap = 1f * k100;
			float maxW = Math.Max(200f, Math.Min(560f * k100, ChartPanel.W - 2 * margin));

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

				SharpDX.Direct2D1.SolidColorBrush white	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)240, (byte)240, (byte)240, (byte)255));
				SharpDX.Direct2D1.SolidColorBrush done	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)120, (byte)225, (byte)120, (byte)255));
				SharpDX.Direct2D1.SolidColorBrush block	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)150, (byte)150, (byte)150, (byte)255));
				SharpDX.Direct2D1.SolidColorBrush note	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)180, (byte)180, (byte)190, (byte)255));
				trash.Add(white);
				trash.Add(done);
				trash.Add(block);
				trash.Add(note);

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

		// 체결 표시 기록 (그리기는 OnRender). 진입: 매수 ▲ 초록·매도 ▼ 빨강, 청산: 파랑 화살표 + 종류·수량. 같은 분 같은 종류는 수량을 합친다
		private void AddMark(string name, double price, int quantity, bool isBuy, bool isEntry, DateTime time)
		{
			if (!ShowStatus)
				return;
			string kind = isEntry ? (isBuy ? "매수" : "매도")
				: name == tradePrefix + "S" ? (!breakevenDone ? "손절" : stopNote == "트레일링" ? "트레일링 스탑" : "본절")
				: name.EndsWith("T") ? "익절" : "청산";
			DateTime minute = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
			lock (renderLock)
			{
				ExecMark last = marks.Count > 0 ? marks[marks.Count - 1] : null;
				if (last != null && last.Minute == minute && last.Kind == kind)
				{
					last.Qty += quantity;
					return;
				}
				marks.Add(new ExecMark { Time = time, Minute = minute, Price = price, Buy = isBuy, Entry = isEntry, Kind = kind, Qty = quantity });
				if (marks.Count > 400)
					marks.RemoveAt(0);
			}
		}

		private class ExecMark
		{
			public DateTime	Time, Minute;
			public double	Price;
			public bool		Buy, Entry;
			public string	Kind;
			public int		Qty;
		}

		private class BoxLine
		{
			public string	Tag;
			public DateTime	Start, End;
			public double	High, Low;
		}

		private readonly object			renderLock		= new object();
		private readonly List<ExecMark>	marks			= new List<ExecMark>();
		private readonly List<BoxLine>	boxLines		= new List<BoxLine>();
		private double					renderStop		= double.NaN;
		private double					renderAvg		= double.NaN;
		private string					entryCountText	= "";

		// 차트 그림: 장전 박스 점선, 손절선·평단선, 체결 표시, 진입 횟수. 값은 OnBarUpdate·체결 때 기록해 둔 것만 쓴다 (계산 없음)
		private void RenderChartMarks(ChartControl chartControl, ChartScale chartScale, List<IDisposable> trash)
		{
			List<ExecMark> ms;
			List<BoxLine> bls;
			lock (renderLock)
			{
				ms	= new List<ExecMark>(marks);
				bls	= new List<BoxLine>();
				foreach (BoxLine b in boxLines)
					bls.Add(new BoxLine { Tag = b.Tag, Start = b.Start, End = b.End, High = b.High, Low = b.Low });
			}
			float x0 = ChartPanel.X, x1 = ChartPanel.X + ChartPanel.W;
			DateTime tFrom = ChartBars.GetTimeByBarIdx(chartControl, ChartBars.FromIndex);
			DateTime tTo = ChartBars.GetTimeByBarIdx(chartControl, ChartBars.ToIndex);

			SharpDX.Direct2D1.StrokeStyle dash = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
				new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
			trash.Add(dash);
			SharpDX.Direct2D1.SolidColorBrush gold	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)218, (byte)165, (byte)32, (byte)255));
			SharpDX.Direct2D1.SolidColorBrush mag	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)0, (byte)255, (byte)255));
			SharpDX.Direct2D1.SolidColorBrush gray	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)150, (byte)150, (byte)150, (byte)255));
			SharpDX.Direct2D1.SolidColorBrush green	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)50, (byte)205, (byte)50, (byte)255));
			SharpDX.Direct2D1.SolidColorBrush red	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)99, (byte)71, (byte)255));
			SharpDX.Direct2D1.SolidColorBrush blue	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)30, (byte)144, (byte)255, (byte)255));
			SharpDX.Direct2D1.SolidColorBrush white	= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)235, (byte)235, (byte)235, (byte)255));
			trash.Add(gold); trash.Add(mag); trash.Add(gray); trash.Add(green); trash.Add(red); trash.Add(blue); trash.Add(white);
			float k100 = Math.Max(50, StatusPanelScale) / 100f;
			SharpDX.DirectWrite.TextFormat fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic", 11f * k100);
			SharpDX.DirectWrite.TextFormat big = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Malgun Gothic",
				SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 13f * k100);
			trash.Add(fmt); trash.Add(big);

			// 장전 박스: 구간 시작 ~ 마지막 기록 봉까지 고가·저가 점선 (화면 밖 구간은 건너뜀)
			foreach (BoxLine b in bls)
			{
				if (b.End < tFrom || b.Start > tTo)
					continue;
				float xa = Math.Max(x0, chartControl.GetXByTime(b.Start < tFrom ? tFrom : b.Start));
				float xb = Math.Min(x1, chartControl.GetXByTime(b.End > tTo ? tTo : b.End));
				float yh = chartScale.GetYByValue(b.High), yl = chartScale.GetYByValue(b.Low);
				RenderTarget.DrawLine(new SharpDX.Vector2(xa, yh), new SharpDX.Vector2(xb, yh), gold, 1f, dash);
				RenderTarget.DrawLine(new SharpDX.Vector2(xa, yl), new SharpDX.Vector2(xb, yl), gold, 1f, dash);
			}
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
			// 체결 표시
			foreach (ExecMark m in ms)
			{
				if (m.Time < tFrom || m.Time > tTo.AddMinutes(1))
					continue;
				float x = chartControl.GetXByTime(m.Minute);
				float y = chartScale.GetYByValue(m.Price);
				SharpDX.Direct2D1.SolidColorBrush b = m.Entry ? (m.Buy ? green : red) : blue;
				string arrow = m.Buy ? "▲" : "▼";
				float dy = m.Buy ? 6f * k100 : -18f * k100;
				SharpDX.DirectWrite.TextLayout la = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, arrow, big, 40f, 30f);
				SharpDX.DirectWrite.TextLayout lt = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, m.Kind + " " + m.Qty, fmt, 200f, 30f);
				trash.Add(la); trash.Add(lt);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - la.Metrics.Width / 2, y + dy), la, b, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - lt.Metrics.Width / 2, y + dy + (m.Buy ? 16f : -14f) * k100), lt, b, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}
			// 우상단 진입 횟수
			string ec = entryCountText;
			if (!string.IsNullOrEmpty(ec))
			{
				SharpDX.DirectWrite.TextLayout l = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, ec, big, 400f, 40f);
				trash.Add(l);
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x1 - l.Metrics.Width - 8f, ChartPanel.Y + 6f), l, white, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}
		}
		#endregion

		#region Properties
		// 설정창 배치 원칙 (v3 지시서 5-1): 묶음은 판단 순서, 한 묶음 안에서 [설계] 위 [선택] 아래.
		// 태그: [설계] 설계안의 동작과 값 · [선택] 고민사항 스위치 · [안전] 주문 거부 대비 · [표시] 화면·기록

		// ── 01. 추세 판단 (추세 봉) ──



		[NinjaScriptProperty]
		[Range(0.0, 1.0)]
		[Display(Name = "[선택] 점수 판정: 추세 시작 점수 (ScoreTrendOn)", Description = "기본 0.2. 점수 C(항목 11개 평균, −1~+1) ≥ 이 값이면 상승추세 시작(하락은 ≤ −값)", GroupName = "02. 추세 판정", Order = 0)]
		public double ScoreTrendOn { get; set; }



		[NinjaScriptProperty]
		[Range(0.0, 2.0)]
		[Display(Name = "[선택] 점수 판정: 빠른 시작 속도 (ScoreVelMin)", Description = "기본 0.25. C ≥ 0이면서 속도 ≥ 이 값이면 시작 점수 전이라도 상승추세 시작(하락은 거울상). 큰 움직임 잡는 지연 7.4 → 4.5분(7월). 0.15 / 0.35도 비슷", GroupName = "02. 추세 판정", Order = 1)]
		public double ScoreVelMin { get; set; }

		[NinjaScriptProperty]
		[Range(-1.0, 1.0)]
		[Display(Name = "[선택] 점수 판정: 청산용 판정 종료 점수 (ScoreExitOff)", Description = "기본 −0.1. 보유 포지션의 TREND_END·OPP_TREND는 따로 계산한 청산용 판정을 봄: 속도로 시작하지 않고(C ≥ 시작 점수일 때만), 상승은 C ≤ 이 값이면 끝(하락은 ≥ −값). 진입 판정은 빠르게, 청산은 흔들림에 둔하게. 18일 시뮬레이션 10월 −4.6R → −3.1R, 7월 같음", GroupName = "02. 추세 판정", Order = 2)]
		public double ScoreExitOff { get; set; }












		// ── 02. 의심 이벤트 ──








		// ── 03. 진입 — 추세 ──
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E1 추세 시작 봉 진입 (UseEntryTrendStart)", Description = "기본 켬. 추세 봉의 추세 시작을 처음 본 진입 봉 마감에 그 방향으로 진입. 종가가 이미 1차 익절 밴드를 넘었거나 손익비가 모자라면 안 함. 반대 추세 시작으로 청산한 뒤에는 다음 2봉 안에도 허용", GroupName = "01. 진입 경로", Order = 0)]
		public bool UseEntryTrendStart { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E2 추세 눌림 진입 (UseEntryPullback)", Description = "기본 켬. 추세 중 저가가 아래 밴드에 닿은 뒤(매도는 고가가 위 밴드) 대기 봉 수 안에 [MACD 상승 전환 OR 골든크로스]가 나오면 진입", GroupName = "01. 진입 경로", Order = 1)]
		public bool UseEntryPullback { get; set; }



		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E3 장전 박스 돌파 진입 (UseEntryOrb)", Description = "기본 켬. 장후에 아래 마감 시각 전, 진입 봉 종가가 장전 박스(21:30~22:30 최고가) 위로 마감하고 S·F가 아래 기준 이상이면 추세 매수(매도는 박스 최저가 아래). 장후 방향별 한 번", GroupName = "01. 진입 경로", Order = 2)]
		public bool UseEntryOrb { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] E3 스탑 주문으로 진입 (E3StopEntry)", Description = "기본 켬(v6, 2026-10-15 사용자 결정). 봉 마감까지 기다리지 않고, E3 조건(S·F·횟수·시간)이 맞으면 박스 고가 + 1틱(매도는 저가 − 1틱)에 스탑 진입 주문을 걸어 닿는 순간 진입. 다른 경로가 시장가로 들어가면 먼저 취소. 18일 시뮬레이션: 7월 +14.2 → +15.0R, 10월 +1.9 → +4.4R(약 +$5,000). B1은 가짜 돌파가 많아 봉 마감 그대로", GroupName = "01. 진입 경로", Order = 3)]
		public bool E3StopEntry { get; set; }





		// ── E5 개장 범위 돌파 (v6, 4-10) ──
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E5 개장 범위 돌파 진입 (UseEntryOpenRange)", Description = "기본 켬(v6, 2026-10-15 사용자 결정). 개장 시각(OpenTime) 봉부터 N분 동안의 고가·저가를 범위로 잡고, 그 뒤 진입 봉 종가가 범위를 벗어나면 그 방향으로 진입. 방향별 하루 한 번. 손절 = 범위 중간, 익절 = 목표 R, 본전 R에 닿으면 본전 → 트레일링. 추세 판정·S·F는 보지 않음. 28일(7~10월) 시뮬레이션 38건 +10.5R, 하루 최악 −2R", GroupName = "01. 진입 경로", Order = 4)]
		public bool UseEntryOpenRange { get; set; }

		[NinjaScriptProperty]
		[Range(1, 30)]
		[Display(Name = "[선택] E5 개장 범위 분 수 (E5RangeMinutes)", Description = "기본 5(22:30~22:34 봉). 시뮬레이션: 5·7분 +10.5·+10.5R(목표 2R), 3분 +5.9R, 10분 +4.6R, 15분 −0.5R", GroupName = "03. 익절·손절", Order = 3)]
		public int E5RangeMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 10.0)]
		[Display(Name = "[선택] E5 익절 목표 R (E5TargetR)", Description = "기본 2. 익절 지정가 = 진입가 ± 이 값 × 손절 거리. 5분 범위에서 1.5 / 2 / 2.5 / 3R = +8.1 / +10.5 / +10.1 / +7.9R", GroupName = "03. 익절·손절", Order = 4)]
		public double E5TargetR { get; set; }




		// ── B1 돌파 (v4, 4-9) ── 03 묶음 맨 위쪽 순서(Order 20~)로 둔다
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] B1 박스 돌파 진입 (UseBreakoutEntry)", Description = "기본 켬(v6). 직전 구간이 박스(효율비가 아래 값 이하)일 때 돌파로 진입. 7·10월 18일 시뮬레이션: v5 중립 돌파보다 진입 22→29건(7월)·21→29건(10월), R +11.0→+13.0 / −3.4→−2.1. 진입 봉 종가가 직전 N봉 고가 위(매도는 저가 아래)로 마감하고 빠른 점수 F가 그 방향으로 기준 이상이면 추세 포지션 진입. 추세 판정(상승·횡보·하락)은 보지 않음", GroupName = "01. 진입 경로", Order = 5)]
		public bool UseBreakoutEntry { get; set; }









		// ── 04. 진입 — 횡보 ──
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E4 횡보 진입 (UseEntrySide)", Description = "기본 켬. 횡보 중 저가가 아래 밴드에 닿은 뒤 대기 봉 수 안에 [MACD 상승 전환 OR 골든크로스]면 횡보 매수(매도는 거울상)", GroupName = "01. 진입 경로", Order = 6)]
		public bool UseEntrySide { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] E6 박스 장 되돌림 진입 (UseEntryRangeFade)", Description = "기본 켬(v6, 2026-10-15 사용자 결정: 10/7 같은 박스 장 대책). 대회 구간 시작 60분 뒤부터, 시작부터 지금까지 종가 효율비 ≤ 0.15(박스 장)이면, 30봉 고가·저가를 1~3봉 전에 잠깐 넘었다가 다시 안으로 마감할 때 반대로 진입. 익절 1R, 손절 최근 4봉 극값 바깥, 최대 20분. 다른 경로가 없을 때만. 28일 시뮬레이션 단독 47건 +8.8R(7월 +1.9, 8·9월 +3.9, 10월 +3.0), 항상 켜면 −5.6R", GroupName = "01. 진입 경로", Order = 7)]
		public bool UseEntryRangeFade { get; set; }





		// ── 05. 진입 — 공통 ──



		// ── 06. 손절 ──





		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "[안전] 최소 손절 틱 (StopMinTicks)", Description = "기본 32틱(MNQ 8pt). 손절 거리가 이보다 짧으면 이만큼 벌림. 장전 ATR이 절반이라 손절이 너무 좁아지는 것 방지", GroupName = "03. 익절·손절", Order = 5)]
		public int StopMinTicks { get; set; }






		// ── 07. 청산 — 추세 ──




		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 2차 뒤 트레일링 ATR 배수 (TrailAtr)", Description = "기본 2.5. 2차 익절 뒤 진입 봉 마감마다 손절을 진입 뒤 최고가 − 1분 ATR × 이 값으로(매도는 최저가 + …). 0 = 안 씀. 2 / 2.5 / 3 비교용", GroupName = "03. 익절·손절", Order = 2)]
		public double TrailAtr { get; set; }






		[NinjaScriptProperty]
		[Range(0.1, 10.0)]
		[Display(Name = "[선택] 추세 1차 익절 R (TrendTp1R)", Description = "기본 0.5(2026-10-15 사용자 결정, 그 전 1). 1차 익절 = 진입가 ± 이 값 × 손절 거리. 1차 뒤 손절은 평단, 가격이 더 가면 최고가 − 3 ATR로 따라감. 18일 시뮬레이션 합계: 0.5R +24.5R, 1R +23.0R, 밴드 +13.6R", GroupName = "03. 익절·손절", Order = 0)]
		public double TrendTp1R { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10.0)]
		[Display(Name = "[선택] 추세 2차 익절 R (TrendTp2R)", Description = "기본 2.5. 익절 방식 2에서 2차 익절. 2 / 2.5 / 3에서 2.5가 두 달 모두 가장 고름", GroupName = "03. 익절·손절", Order = 1)]
		public double TrendTp2R { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] 추가 진입 (UseAddOn)", Description = "기본 켬(v6, 2026-10-15 사용자 결정). 추세 포지션(E5 제외)이 1차 익절한 뒤 점수 속도가 포지션 방향으로 기준 이상이면 처음 수량 × %를 한 번 더 진입. 넣기 전에 전체 손절을 평단 이상으로. 추가분은 익절 지정가 없이 트레일링·기준선·의심·손절로 나감. 추세 판정 방식 2에서만. 1R/2.5R과 같이: 7월 +17.7 → +20.4R, 10월 +0.2 → +2.5R. '1차가 빨리 나면 추가'는 10월에 −4.9 ~ −7.1R로 나빠서 안 씀", GroupName = "03. 익절·손절", Order = 6)]
		public bool UseAddOn { get; set; }




		[NinjaScriptProperty]
		[Display(Name = "[선택] 개장 직후 진입: 1차 익절점에서 전량 (EarlyFullExitAtTp1)", Description = "기본 끔. 켜면 개장 직후 진입은 세 조각 모두 1차 익절(1R)에 지정가. 익절 방식 2에서만. 18일 시뮬레이션: 5분 +0.3R +$75(거의 같음), 3·8·10분은 나빠짐. 10월 개장 직후 진입은 모두 1R 전에 손절돼 영향 없음", GroupName = "03. 익절·손절", Order = 7)]
		public bool EarlyFullExitAtTp1 { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 5.0)]
		[Display(Name = "[선택] 개장 직후 진입: 손절 배율 (EarlyStopMult)", Description = "기본 1(끔). 1.5면 개장 직후 진입의 손절 거리를 1.5배로 넓히고, 위험 금액 수량이라 계약이 줄어 1회 위험은 같음. 익절 R도 넓어진 거리 기준. 18일 시뮬레이션 1.5: 7월 +20.4 → +20.9R, 10월 +2.5 → +3.7R, 진입 61 → 56건(오래 들고 있어 다음 신호를 못 받음)", GroupName = "03. 익절·손절", Order = 8)]
		public double EarlyStopMult { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] B1 반대 돌파를 현재가로 바로 청산 (B1OppTickExit)", Description = "기본 켬(2026-10-16 사용자 결정). 켜면 B1 포지션의 반대 돌파(직전 15봉 반대쪽 끝을 넘고 F가 반대로 0.9 이상)를 1분봉 마감을 기다리지 않고 현재가로 판단해 바로 시장가 청산. F는 현재가로 단기선 항목만 다시 계산. 끄면 1분봉 종가로(예전 방식). 1틱 데이터가 없으면(Strategy Analyzer에 틱 데이터 없음) 봉 마감 방식만 동작. 시뮬레이터로는 확인 못 해 Playback으로 비교", GroupName = "03. 익절·손절", Order = 9)]
		public bool B1OppTickExit { get; set; }




		// ── 08. 청산 — 횡보 ──





		// ── 09. 시간대 ──
		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] 개장 시각 (OpenTime)", Description = "HHmmss, 기본 223000(미국 정규장 개장, KST 서머타임). 장전·장후와 장전 박스의 기준. 골드는 종목 차트마다 따로 정함", GroupName = "10. 수량·대회 시간", Order = 4)]
		public int OpenTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 120)]
		[Display(Name = "[설계] 지표 발표 진입 금지 분 (NewsBlockMinutes)", Description = "기본 5. 대회 시작부터 이 분 동안 신규 진입 안 함(08:30 ET 지표 발표). 대회 날 일정을 보고 정함", GroupName = "10. 수량·대회 시간", Order = 5)]
		public int NewsBlockMinutes { get; set; }











		// ── 10. 수량·대회 시간 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 최대 계약 수 (EntryQuantity)", Description = "기본 30. 위험 금액 수량을 켜면 이 값이 최대(장전·최소 진입 보장은 × %). 단계 수량은 × % 내림, 마지막 조각이 나머지", GroupName = "10. 수량·대회 시간", Order = 0)]
		public int EntryQuantity { get; set; }


		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "[설계] 1회 위험 한도 $ (RiskPerTrade)", Description = "기본 2000. 손절 한 번에 잃는 금액의 목표(장전은 × 장전 수량 %). 21일 시뮬레이션: $2,000이면 최대 1회 손실 −$4,564 → −$2,009, 합계는 줄어듦", GroupName = "10. 수량·대회 시간", Order = 1)]
		public double RiskPerTrade { get; set; }


		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "[설계] 최소 진입 목표 횟수 (MinEntries)", Description = "기본 2(종목당). 우상단 표시와 최소 진입 보장에 씀", GroupName = "10. 수량·대회 시간", Order = 2)]
		public int MinEntries { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] 진입 시작 시각 (ContestStartTime)", Description = "HHmmss, 한국시간. 213000 = 21:30", GroupName = "10. 수량·대회 시간", Order = 3)]
		public int ContestStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] 진입 마감 시각 (EntryEndTime)", Description = "HHmmss, 기본 231500 = 23:15. 이 시각 뒤의 신호로는 진입하지 않음", GroupName = "10. 수량·대회 시간", Order = 6)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] 전량 청산 시각 (FlattenTime)", Description = "HHmmss, 기본 232700 = 23:27. 이 봉이 마감되면 남은 수량을 전량 시장가", GroupName = "10. 수량·대회 시간", Order = 7)]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 20.0)]
		[Display(Name = "[안전] 하루 손실 한도 R (DayLossLimitR)", Description = "기본 1.5. 이번 대회 구간에 끝난 거래 손익 합이 −이 값 × 1회 위험 한도(기본 −$3,000) 이하가 되면 그 뒤 진입 수량을 50%로. 진입은 그대로. 0 = 안 씀. 18일 시뮬레이션: 최악의 날 −$6,440 → −$4,498, 합계는 거의 같음", GroupName = "10. 수량·대회 시간", Order = 8)]
		public double DayLossLimitR { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[안전] 최소 진입 강제 채우기 (ForceMinEntries)", Description = "기본 켬(2026-10-16 사용자 결정). 23:20 봉부터 종료 청산 전까지 이 종목 진입 횟수가 최소 진입 목표보다 적으면, 보유 포지션을 정리하고 1계약 시장가 진입 → 다음 봉 청산을 되풀이해 횟수를 채움. 방향은 점수 C의 부호(0이면 매수). 대회 최소 진입: 합계 5회, 나스닥·골드 각 2회 → 한 종목은 최소 진입 목표를 3으로", GroupName = "10. 수량·대회 시간", Order = 9)]
		public bool ForceMinEntries { get; set; }

		// ── 11. 지표 계산값 ──















		// ── 12. 화면·기록 ──
		[NinjaScriptProperty]
		[Display(Name = "[표시] 차트에 상태 표시 (ShowStatus)", Description = "추세 배경색, 밴드, 체결 라벨·화살표, 손절선·평단선, 장전 박스선, 왼쪽 위 상태 상자", GroupName = "12. 화면·기록", Order = 0)]
		public bool ShowStatus { get; set; }

		[NinjaScriptProperty]
		[Range(50, 300)]
		[Display(Name = "[표시] 상태 상자 크기 % (StatusPanelScale)", Description = "기본 130. 100이면 본문 12 / 추세 이름 15 / 폭 560px", GroupName = "12. 화면·기록", Order = 1)]
		public int StatusPanelScale { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 로그를 파일로 저장 (LogToFile)", Description = "실시간·Playback 로그를 내 문서\\NinjaTrader 8\\TQ_log_v6_종목.txt에 저장 (v6 브랜치). 진입 봉·추세 봉 마감마다, 사건마다 한 줄", GroupName = "12. 화면·기록", Order = 2)]
		public bool LogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 학습 데이터를 파일로 저장 (MlLogToFile)", Description = "거래 1건마다 TQ_ml_v6_종목.csv(v6 브랜치)에 한 줄(신호 봉 특징값 + 결과). 백테스트에서도 저장. 최적화 때는 끔", GroupName = "12. 화면·기록", Order = 3)]
		public bool MlLogToFile { get; set; }



		// ── 00. 설정 세트 ──
		// "버튼": 설정창에는 버튼이 없어 체크 상자로 만든다. 체크하면 한 번 실행하고 스스로 풀린다.
		// 값을 저장하지 않으므로 [NinjaScriptProperty]를 붙이지 않고 XML에도 쓰지 않는다
		// 표시 전용: 끄면 설정창에 핵심 설정만 보인다 (TQ_StrategyConverter). 값과 동작은 숨겨도 그대로

		[XmlIgnore]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[버튼] 설계안 기본값으로 되돌리기 — 체크하면 실행", Description = "체크하면 매매 규칙 설정을 모두 설계안(v3 지시서) 기본값으로 되돌림. 실행한 뒤 체크는 스스로 풀림. 수량·대회 시간(10번)과 화면·기록(12번) 묶음은 건드리지 않음. 결과는 아래 '마지막 동작'", GroupName = "00. 설정 세트", Order = 0)]
		public bool PresetApplyDefaults
		{
			get { return ButtonRead("defaults"); }
			set { ButtonWrite("defaults", value); }
		}

		[XmlIgnore]
		[Display(Name = "[표시] 마지막 동작", Description = "위 버튼이 마지막으로 한 일과 그 결과. 직접 바꿀 수 없음", GroupName = "00. 설정 세트", Order = 1)]
		public string PresetStatus
		{
			get { return presetStatus; }
		}
		private string presetStatus = "";

		// 버튼의 값: 누른 직후 처음 한 번은 true로 읽히게 해서 설정창이 다시 그려지게 하고, 그다음부터는 false. 2초가 지나면 무조건 false (복제 때 재실행 방지)
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
			if (name == "defaults")
				ApplyDesignDefaults();
			buttonPressed	= name;
			buttonPressedAt	= DateTime.Now;
		}
		#endregion

		#region 설계안 기본값과 설정창 잠금
		// SetDefaults가 끝난 직후의 설정값 = 설계안 기본값 (모든 인스턴스가 같으므로 한 번만 담는다)
		private static Dictionary<string, object> designDefaults;

		// [NinjaScriptProperty]가 붙은 int·double·bool. rulesOnly면 수량·대회 시간(10번)과 화면·기록(12번) 묶음을 뺀다
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
					if (group.StartsWith("10.") || group.StartsWith("12."))
						continue;
				}
				list.Add(p);
			}
			return list;
		}

		private void RememberDesignDefaults()
		{
			if (designDefaults != null)
				return;
			Dictionary<string, object> values = new Dictionary<string, object>();
			foreach (System.Reflection.PropertyInfo p in SettingProperties(false))
				values[p.Name] = p.GetValue(this, null);
			designDefaults = values;
		}

		private void ApplyDesignDefaults()
		{
			try
			{
				if (designDefaults == null)
				{
					presetStatus = "되돌리기 실패: 기본값을 아직 읽지 못함";
					return;
				}
				int count = 0;
				foreach (System.Reflection.PropertyInfo p in SettingProperties(true))
				{
					object value;
					if (!designDefaults.TryGetValue(p.Name, out value))
						continue;
					p.SetValue(this, value, null);
					count++;
				}
				presetStatus = string.Format("{0:HH:mm:ss} 설계안 기본값으로 되돌림 ({1}개. 수량·시간·화면 설정은 그대로)", DateTime.Now, count);
			}
			catch (Exception e)
			{
				presetStatus = "되돌리기 실패: " + e.Message;
			}
		}

		// 지금 설정에서 쓰이지 않는 설정 (설정창에서 회색 잠금, 5-1). 매매 동작에는 영향 없음
		internal HashSet<string> LockedSettings()
		{
			HashSet<string> locked = new HashSet<string>();
			if (!UseEntryOpenRange)
				foreach (string name in new[] { "E5RangeMinutes", "E5TargetR" })
					locked.Add(name);
			if (!UseEntryOrb)
				locked.Add("E3StopEntry");
			return locked;
		}
		#endregion
	}

	/// <summary>
	/// 설정창(속성 표)에서 쓰이지 않는 설정을 잠근다. TQ_Strategy.LockedSettings()가 돌려준 이름을 읽기 전용으로 보여 준다.
	/// 전략용 변환기는 StrategyBaseConverter를 상속해야 한다 (NT8 공식 문서 "Using a TypeConverter to Customize Property Grid Behavior").
	/// 설정값을 바꿨을 때 표를 다시 그리려면 그 설정에 [RefreshProperties(RefreshProperties.All)]이 있어야 한다.
	/// </summary>
	public class TQ_StrategyConverter : StrategyBaseConverter
	{
		public override bool GetPropertiesSupported(ITypeDescriptorContext context) { return true; }

		public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attrs)
		{
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
				return props;
			}

			// 쓰이지 않는 설정은 회색 잠금
			PropertyDescriptorCollection adjusted = new PropertyDescriptorCollection(null);
			foreach (PropertyDescriptor descriptor in props)
			{
				adjusted.Add(locked.Contains(descriptor.Name) ? new TQ_ReadOnlyDescriptor(descriptor) : descriptor);
			}
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
		public override void SetValue(object component, object value)	{ inner.SetValue(component, value); }
		public override bool ShouldSerializeValue(object component)		{ return inner.ShouldSerializeValue(component); }
	}
}
