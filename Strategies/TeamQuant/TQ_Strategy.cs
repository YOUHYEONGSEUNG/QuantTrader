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
using NinjaTrader.NinjaScript.DrawingTools;	// Draw.*
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
		private int				regimeBip	= 2;	// 추세를 판단하는 시리즈 번호: 1 = 10분봉, 2 = 3분봉
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
		private const int		NoBand			= int.MinValue;
		private readonly List<string>				liveSignals		= new List<string>();				// 아직 보유 중인 조각
		private readonly List<string>				chunkOrder		= new List<string>();				// 이번 진입의 모든 조각 (단계 순서)
		private readonly Dictionary<string, int>	chunkQty		= new Dictionary<string, int>();
		private readonly Dictionary<string, int>	chunkStage		= new Dictionary<string, int>();	// 1 = 1차(1단계), 2 = 2차, 3 = 마지막 조각(횡보는 나머지)
		private readonly Dictionary<string, int>	chunkBand		= new Dictionary<string, int>();	// 밴드 익절 배수 (NoBand = 밴드 익절 아님)
		private readonly Dictionary<string, double>	chunkFixed		= new Dictionary<string, double>();	// 고정 가격 익절 (횡보 1단계, NaN = 없음)
		private readonly HashSet<string>			pendingBandExit	= new HashSet<string>();			// 봉 마감 시장가로 낸 익절 주문 이름 (IntrabarTakeProfit 끔)
		// 조각별 실제 보유 수량: 진입 체결로 늘고, 그 조각에서 나간 청산 체결(FromEntrySignal)로 준다. 0이 되면 조각이 끝난 것.
		// 진입·청산이 여러 번에 나눠 체결되면 NinjaTrader가 조각별로 기억하는 수량이 실제와 어긋날 수 있어(10/6 Playback: 9계약 보유에 11계약 청산) 청산 수량은 이 값으로 직접 준다
		private readonly Dictionary<string, int>	chunkLeft		= new Dictionary<string, int>();
		private readonly HashSet<string>			stageFilled		= new HashSet<string>();			// 익절 단계 처리를 이미 한 조각 (부분 체결이 여러 번 와도 한 번만)
		private readonly HashSet<string>			exitSent		= new HashSet<string>();			// 직접 청산 주문을 낸 조각 (늦게 온 진입 체결로 다시 살리지 않음)
		private readonly List<Order>				exitOrders		= new List<Order>();				// 직접 낸 청산 주문 (남은 포지션 재청산 전에 아직 살아 있는지 본다)
		private bool			tp1Done;
		private bool			tp2Done;
		private double			tp1FillPrice	= double.NaN;	// 1차 익절 체결가 (1차 뒤 손절 계산용)
		private int				signalSeq;						// 진입마다 하나씩 늘려 시그널명에 붙인다 (지난 거래의 Set 익절 값이 남지 않게)
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
		private int				lastRegimeBar	= -1;
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
		private bool			sessionOpenUsed;				// 이번 대회 구간의 첫 진입 가능 봉이 지났는지 (E1AtSessionStart)
		private bool			suspectPriceBroken;				// 지금 의심 중에 가격 확인(직전 저점 이탈)이 있었는지 (SuspectBlockNeedPrice)

		// ── 대회 구간 ──
		private double			boxHigh			= double.NaN;	// 장전 박스 (21:30~22:30 진입 봉 최고가·최저가)
		private double			boxLow			= double.NaN;
		private DateTime		boxStartTime;
		private int				orbCountLong;					// 이번 구간 E3 매수 횟수
		private int				orbCountShort;
		private bool			orbArmedLong	= true;			// E3 매수가 열려 있음: 구간 시작 때, 그리고 E3 뒤 종가가 박스 안으로 돌아오면 (OrbMaxEntries, 사용자 결정 2026-10-15)
		private bool			orbArmedShort	= true;
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
		private Brush			downSuspectBrush;
		private SimpleFont		markFont;
		private string			markTag			= "";
		private int				markQty;

		// 검증용 임시 진입 (UseTestEntry)
		private int				testHoldBars;
		private const int		TestTp1Bar	= 2;
		private const int		TestTp2Bar	= 3;
		private const int		TestExitBar	= 4;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 v3 (docs/spec.md v3)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;			// 4-1 원칙 1: 봉 마감 뒤 판단
				EntriesPerDirection	= 1;
				EntryHandling		= EntryHandling.UniqueEntries;	// 시그널명별 분할 진입

				// 기본값 = v3 지시서 5-2 (설계안)
				// 01 추세 판단
				RegimeBarMinutes		= 3;
				RegimeFast				= 6;
				RegimeMid				= 20;
				RegimeSlow				= 60;
				TrendStartFast			= 1;	// 2026-10-15 사용자 결정 2 → 1 (21일 재현: 지연 중간 20 → 7분, 추세 전환은 늘어남)
				TrendStartSlow			= 1;
				TrendEndSlow			= 0;
				RegimeMacdSlopeBars		= 2;
				RegimeS2Mode			= 1;
				RegimeSlopeBars			= 5;
				RegimeUseRsi			= true;
				RegimeRsiBand			= 5;
				// 02 의심 이벤트
				SuspectFast				= 2;
				SuspectNeedPrice		= true;
				SuspectConfirmBars		= 5;
				SuspectExitPercent		= 50;
				SuspectStrongSlow		= 1;
				SuspectStrongExitPercent	= 100;
				BlockEntryWhileSuspect	= true;
				SuspectBlockNeedPrice	= false;	// 2026-10-15 켬으로 넣었다가 되돌림: 의심 중 E2 8건 3승 −$9,997
				TrendEntryNeedFast		= true;
				RegimeDirectReverse		= false;
				// 03 진입 — 추세
				UseEntryTrendStart		= true;
				UseEntryPullback		= true;
				TrendPullbackBand		= 0;	// 2026-10-15 사용자 결정 1(−1배) → 0(중심선)
				PullbackMinSlow			= 2;
				UseEntryOrb				= true;
				OrbEndTime				= 230000;
				OrbMinSlow				= 1;
				OrbMinFast				= 1;
				OrbMaxEntries			= 2;
				EntryMinRR				= 1.0;
				E1Reaccel				= true;
				E1AtSessionStart		= false;	// 2026-10-15 사용자 결정 켬 → 끔 (21일 10건 3승, −$2,792)
				// 04 진입 — 횡보
				UseEntrySide			= true;
				SideBand				= 2;
				SideT2Entry				= true;
				SideBlockScore			= 3;
				BlockSideAfterStop		= true;
				// 05 진입 — 공통
				T1Window				= 5;
				MacdConfirmBars			= 2;
				T2CountConfirmBars		= true;
				// 06 손절
				SwingBars				= 5;
				TrendStopMinAtr			= 1.5;
				TrendStopMaxAtr			= 3.0;
				SideStopMinAtr			= 1.0;
				SideStopMaxAtr			= 2.0;
				StopMinTicks			= 32;
				BreakevenBufferTicks	= 8;
				HandleOrderErrors		= true;
				BreakevenMinR			= 0.5;
				BreakevenFallbackAtr	= 1.5;
				BreakevenAtR			= 0.8;
				// 07 청산 — 추세
				TrendTp1Band			= 2;
				TrendTp2Band			= 3;
				TrendTp1Percent			= 33;
				TrendTp2Percent			= 33;
				TrailAtr				= 2.5;
				TrendLastExitMa			= 1;
				TrendExitOnEnd			= true;
				TrendExitOnEntryT2		= false;
				TrendTpMinR				= 0.5;
				// 08 청산 — 횡보
				SideTp1Atr				= 2;
				SideTp1Percent			= 33;
				SideRestBand			= 3;
				SideOscExitAfterTp1		= true;
				SideExitOnOppTrend		= true;
				// 09 시간대
				OpenTime				= 223000;
				NewsBlockMinutes		= 5;
				OpenBlockBefore			= 5;
				OpenBlockAfter			= 3;
				PreOpenQtyPercent		= 50;
				PreOpenTrendMinSlow		= 2;	// 2026-10-15 사용자 결정 (3 → 2)
				PreOpenAllowSide		= true;
				ProtectBeforeOpen		= false;
				ProtectAtr				= 1;
				MinEntryFallback		= true;
				MinEntryFallbackTime	= 230000;
				MinEntryQtyPercent		= 30;
				// 10 수량·대회 시간
				EntryQuantity			= 30;
				MaxQuantity				= 40;
				MinEntries				= 2;
				ContestStartTime		= 213000;
				EntryEndTime			= 231500;
				FlattenTime				= 232700;
				// 11 지표 계산값 (main 12번 묶음 그대로)
				BandMidPeriod			= 26;
				BandAtrPeriod			= 14;
				BandMult1				= 1;
				BandMult2				= 2;
				BandMult3				= 3;
				MacdFast				= 12;
				MacdSlow				= 26;
				MacdSmooth				= 9;
				StochPeriodK			= 10;
				StochSmooth				= 5;
				StochPeriodD			= 5;
				CrossLow				= 20;
				CrossHigh				= 80;
				CrossFilterBars			= 5;
				RsiPeriod				= 14;
				// 12 화면·기록
				ShowStatus				= true;
				StatusPanelScale		= 130;
				LogToFile				= true;
				MlLogToFile				= true;
				UseTestEntry			= false;
				IntrabarTakeProfit		= true;

				// 여기까지가 설계안 기본값이다. "설계안 기본값으로 되돌리기" 버튼이 쓰도록 담아 둔다
				RememberDesignDefaults();
			}
			else if (State == State.Configure)
			{
				// 10분봉 → BarsArray[1], 3분봉 → BarsArray[2]. AddDataSeries 인자는 변수로 줄 수 없어서 둘 다 추가하고 고른다
				AddDataSeries(BarsPeriodType.Minute, 10);
				AddDataSeries(BarsPeriodType.Minute, 3);

				// 주문 오류를 NinjaTrader 기본 동작(포지션을 닫고 전략 종료)에 맡기지 않고 OnOrderUpdate에서 직접 처리한다 (SetDefaults·Configure에서만 정할 수 있음)
				if (HandleOrderErrors)
					RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
			}
			else if (State == State.DataLoaded)
			{
				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				regimeBip	= RegimeBarMinutes == 10 ? 1 : 2;
				regime		= TQ_Regime(BarsArray[regimeBip], RegimeFast, RegimeMid, RegimeSlow, false, MacdFast, MacdSlow, MacdSmooth, RsiPeriod,
								RegimeS2Mode, RegimeSlopeBars, RegimeUseRsi, RegimeRsiBand, RegimeMacdSlopeBars,
								TrendStartFast, TrendStartSlow, TrendEndSlow, SuspectFast, RegimeDirectReverse);
				// v3는 EntryUpLong / EntryDnShort(추세 눌림, 밴드 = TrendPullbackBand), EntrySideLong / EntrySideShort(횡보, 밴드 = SideBand)만 쓴다.
				// 쓰지 않는 밴드 인자(의심 진입 4개, 역추세 매수)에는 main 기본값을 넘긴다: 2 / 1 / 3 / 30 / 1 / 2. 마지막 조각 이동평균은 쓰지 않아 main 기본 18
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, SwingBars,
								BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3,
								MacdFast, MacdSlow, MacdSmooth, StochPeriodK, StochSmooth, StochPeriodD,
								18, MacdConfirmBars, 0, false,
								TrendPullbackBand, 2, 1, TrendPullbackBand, 3, 30, 1, 2, SideBand,
								0, 0, 0, T2CountConfirmBars, false);
				atr			= ATR(BandAtrPeriod);

				minBars0	= Math.Max(Math.Max(BandMidPeriod, MacdSlow + MacdSmooth), Math.Max(BandAtrPeriod, SwingBars)) + 5;

				// 기록 파일: 내 문서\NinjaTrader 8\TQ_log_v3_종목.txt, TQ_ml_v3_종목.csv (v2 기록과 섞이지 않게 따로, 사용자 결정 2026-10-15)
				string docs	= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8");
				logPath		= System.IO.Path.Combine(docs, "TQ_log_v3_" + Instrument.MasterInstrument.Name + ".txt");
				mlPath		= System.IO.Path.Combine(docs, "TQ_ml_v3_" + Instrument.MasterInstrument.Name + ".csv");
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
				downSuspectBrush.Freeze();
				markFont			= new SimpleFont("Malgun Gothic", 11);

				// 4-8: 보유 수량은 EntryQuantity를 넘지 않는다. 한도(MaxQuantity)보다 크면 경고만
				if (EntryQuantity > MaxQuantity)
					Out(string.Format("[{0}][TQ_Strategy] 경고: 진입 계약 수({1}) > 최대 보유 수량({2}, 대회 한도). 설정 확인 필요",
						Instrument.FullName, EntryQuantity, MaxQuantity));
			}
			else if (State == State.Realtime)
			{
				Out("==== " + SettingsSummary());
			}
		}

		// 차트 왼쪽 위 이름표는 이름만 (설정값은 로그 첫 줄과 설정창에서 본다)
		public override string DisplayName
		{
			get { return "TQ_Strategy"; }
		}

		// 실행·설정 구분용 한 줄. 채점기(docs/tools/tq_regime_score.py --validate)가 "버전=v3"와 추세 판단 값을 읽는다
		private string SettingsSummary()
		{
			System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
			return "[" + Instrument.FullName + "] " + BarsPeriod.Value + "분봉 | 버전=v3"
				+ " | 추세봉=" + RegimeBarMinutes + "분 MA=" + RegimeFast + "/" + RegimeMid + "/" + RegimeSlow
				+ " S2방식=" + RegimeS2Mode + " 기울기봉=" + RegimeSlopeBars + " RSI항목=" + RegimeUseRsi + " RSI폭=" + RegimeRsiBand.ToString(inv)
				+ " MACD기울기봉=" + RegimeMacdSlopeBars + " 시작F=" + TrendStartFast + " 시작S=" + TrendStartSlow + " 종료S=" + TrendEndSlow + " 바로반전=" + RegimeDirectReverse
				+ " | 의심F=" + SuspectFast + " 가격확인=" + SuspectNeedPrice + "(" + SuspectConfirmBars + "봉) 청산%=" + SuspectExitPercent
				+ " 강한의심S=" + SuspectStrongSlow + "(" + SuspectStrongExitPercent + "%) 의심중진입금지=" + BlockEntryWhileSuspect + "(가격확인때만=" + SuspectBlockNeedPrice + ") 추세진입F필요=" + TrendEntryNeedFast
				+ " | E1=" + UseEntryTrendStart + "(재가속=" + E1Reaccel + " 구간첫봉=" + E1AtSessionStart + ") E2=" + UseEntryPullback + "(밴드" + TrendPullbackBand + " S" + PullbackMinSlow + ") E3=" + UseEntryOrb
				+ "(~" + OrbEndTime + " S" + OrbMinSlow + " F" + OrbMinFast + " 최대" + OrbMaxEntries + "번) 손익비=" + EntryMinRR.ToString(inv)
				+ " | E4=" + UseEntrySide + "(밴드" + SideBand + " T2=" + SideT2Entry + ") 횡보필터=" + SideBlockScore + " 횡보재진입금지=" + BlockSideAfterStop
				+ " | 대기=" + T1Window + " MACD확인=" + MacdConfirmBars + " T2앞봉인정=" + T2CountConfirmBars
				+ " | 손절봉=" + SwingBars + " 추세손절ATR=" + TrendStopMinAtr.ToString(inv) + "~" + TrendStopMaxAtr.ToString(inv)
				+ " 횡보손절ATR=" + SideStopMinAtr.ToString(inv) + "~" + SideStopMaxAtr.ToString(inv) + " 최소손절틱=" + StopMinTicks
				+ " 본절최소R=" + BreakevenMinR.ToString(inv) + " 모자라면ATR=" + BreakevenFallbackAtr.ToString(inv) + " 밴드최소R=" + TrendTpMinR.ToString(inv) + " 이익보호R=" + BreakevenAtR.ToString(inv)
				+ " | 추세익절=" + TrendTp1Band + "/" + TrendTp2Band + "배 " + TrendTp1Percent + "/" + TrendTp2Percent + "% 트레일링ATR=" + TrailAtr.ToString(inv)
				+ " 마지막조각=" + TrendLastExitMa + " 추세종료청산=" + TrendExitOnEnd + " 1분T2청산=" + TrendExitOnEntryT2
				+ " | 횡보1단계=" + SideTp1Atr.ToString(inv) + "ATR " + SideTp1Percent + "% 전량밴드=" + SideRestBand
				+ " 오실레이터는1단계뒤=" + SideOscExitAfterTp1 + " 반대추세청산=" + SideExitOnOppTrend
				+ " | 개장=" + OpenTime + " 지표금지=" + NewsBlockMinutes + "분 개장전후금지=" + OpenBlockBefore + "/" + OpenBlockAfter
				+ " 장전수량%=" + PreOpenQtyPercent + " 장전추세S=" + PreOpenTrendMinSlow + " 장전횡보=" + PreOpenAllowSide
				+ " 개장보호=" + ProtectBeforeOpen + "(" + ProtectAtr.ToString(inv) + ") 최소진입보장=" + MinEntryFallback + "(" + MinEntryFallbackTime + " " + MinEntryQtyPercent + "%)"
				+ " | 수량=" + EntryQuantity + " 시간=" + ContestStartTime + "/" + EntryEndTime + "/" + FlattenTime + " 봉중익절=" + IntrabarTakeProfit;
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
			UpdateSuspectPrice();
			PaintBar();
			TradeTrack();
			UpdateSession();

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
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				ResetTradeState();
				cands = EvaluateEntries();
				if (InEntryWindow())
				{
					if (UseTestEntry)	RouteTestEntry();
					else				RouteEntry(cands);
				}
			}
			else
			{
				if (gapFlattened || pos == Pos.None)
				{
					// 전량 청산 주문을 낸 뒤에도 포지션이 남아 있으면 다시 낸다 (거부·미체결·수량 어긋남)
					FlattenLeftover();
				}
				else
				{
					RetryDeferredStop();
					if (UseTestEntry)
					{
						testHoldBars++;
						ManageTestExits();
					}
					else
					{
						ProtectBeforeOpenCheck();
						ProtectProfitCheck();
						UpdateTrailStop();
						UpdateTargets();
						if (IsTrendPos())	ManageTrendExits();
						else				ManageSideExits();
					}
				}
			}

			LogEntryBar(cands);
			UpdateStatusPanel(cands);

			// 이번 구간의 첫 진입 가능 봉(장전 또는 장후)을 지났다 (E1AtSessionStart는 그 봉 하나에서만)
			Zone zNow = ZoneNow();
			if (zNow == Zone.Pre || zNow == Zone.Post)
				sessionOpenUsed = true;

			// 다음 봉의 SideSignal이 쓰는 직전 봉 횡보 대기 상태
			prevWaitSideLong	= signals.WaitSideLong;
			prevWaitSideShort	= signals.WaitSideShort;
		}

		// 4-1 원칙 2: 분할 진입 여러 건은 1회. 미보유 → 보유 전환 때만 센다. 보유 → 미보유가 되면 거래 기록을 쓴다
		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (lastPosition == MarketPosition.Flat && marketPosition != MarketPosition.Flat)
			{
				entryCount++;
				Out(string.Format("[{0}][{1}][TQ_Strategy] 신규 진입 #{2} 방향={3} 평균가={4} 수량={5}",
					Time[0], Instrument.FullName, entryCount, marketPosition, averagePrice, quantity));
			}
			if (lastPosition != MarketPosition.Flat && marketPosition == MarketPosition.Flat)
				TradeEnd();
			lastPosition = marketPosition;
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
		private int RegimeState()
		{
			double v = regime.Regime[0];
			return double.IsNaN(v) ? 0 : (int)v;
		}

		private int SlowNow()	{ return (int)regime.SlowScore[0]; }
		private int FastNow()	{ return (int)regime.FastScore[0]; }

		// 진입 봉에서 새 추세 봉 값을 처음 볼 때 그 봉의 추세 시작·의심 이벤트를 꺼낸다 (그 진입 봉에서만 0이 아님)
		private void ObserveRegime()
		{
			trendStartNow	= 0;
			suspectEventNow	= 0;
			reaccelNow		= 0;
			int rb = CurrentBars[regimeBip];
			if (rb == lastRegimeBar)
				return;
			lastRegimeBar	= rb;
			trendStartNow	= regime.TrendStart[0];
			suspectEventNow	= regime.SuspectEvent[0];
			if (trendStartNow != 0)
			{
				lastTrendStartTime	= Time[0];
				lastTrendStartDir	= trendStartNow;
			}
			ObserveReaccel();
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

		// 의심 중 가격 확인 (SuspectBlockNeedPrice): 의심이 뜬 뒤 진입 봉 종가가 직전 SwingBars봉 저점 아래로 마감한 적이 있는지 (매도는 고점 위).
		// 4-5의 보유 중 일부청산 가격 확인과 같은 식. 의심이 풀리거나 새로 뜨면 지운다
		private void UpdateSuspectPrice()
		{
			int active = regime.SuspectActive[0];
			if (active == 0 || suspectEventNow != 0)
				suspectPriceBroken = false;
			if (active == 0)
				return;
			int d		= -active;	// 의심이 걸린 추세의 방향
			double sw	= d > 0 ? signals.SwingLow5[1] : signals.SwingHigh5[1];
			if (!double.IsNaN(sw) && d * (Close[0] - sw) < 0)
				suspectPriceBroken = true;
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
			return p == "E1" ? "E1 추세 시작" : p == "E1X" ? "E1 반대 추세 청산 뒤" : p == "E1R" ? "E1 추세 재가속" : p == "E1S" ? "E1 구간 첫 봉 추세" : p == "E2" ? "E2 추세 눌림" : p == "E3" ? "E3 장전 박스 돌파" : p == "E4" ? "E4 횡보" : p;
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
				sessionOpenUsed	= false;
				boxHigh			= double.NaN;
				boxLow			= double.NaN;
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
				// E3 다시 열기: 박스가 완성된 뒤 종가가 박스 안(매수는 고가 이하, 매도는 저가 이상)으로 마감하면 (보유 중에도 본다)
				if (BoxReady())
				{
					if (Close[0] <= boxHigh)	orbArmedLong = true;
					if (Close[0] >= boxLow)		orbArmedShort = true;
				}
			}

			// 우상단 진입 횟수 (최소 진입 목표)
			Draw.TextFixed(this, "TQ_EntryCount",
				string.Format("{0} 진입: {1}/{2}회{3}", Instrument.MasterInstrument.Name, entryCount, MinEntries, entryCount >= MinEntries ? "" : " (최소 미달)"),
				TextPosition.TopRight);
		}

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
			public string	Note		= "";	// E1이 열린 이유 (추세 시작 / 반대 추세 청산 뒤 / 재가속 / 구간 첫 봉 추세)
		}

		// 경로 우선순위: E1 > E3 > E2 > E4 (4-2)
		private static int PathRank(string p)
		{
			return p == "E1" ? 0 : p == "E3" ? 1 : p == "E2" ? 2 : 3;
		}

		// 진입 경로 4개 × 방향 2개의 후보와 막힌 사유를 만든다. 미보유일 때 매 진입 봉 마감마다
		private List<Candidate> EvaluateEntries()
		{
			List<Candidate> list	= new List<Candidate>();
			int st		= RegimeState();
			int S		= SlowNow();
			int F		= FastNow();
			Zone z		= ZoneNow();
			bool fallback = MinEntryFallback && z == Zone.Post && entryCount < MinEntries
							&& RelMinutes(ToTime(Time[0])) >= RelMinutes(MinEntryFallbackTime);
			int suspectActive = regime.SuspectActive[0];

			for (int k = 0; k < 2; k++)
			{
				int d = k == 0 ? 1 : -1;

				// 시간대 공통 사유
				string timeBlock = z == Zone.Out ? "진입 시간 아님" : z == Zone.News ? "지표 발표 진입 금지 (" + NewsBlockMinutes + "분)"
					: z == Zone.OpenBlock ? "개장 전후 진입 금지" : null;
				// 의심 중에는 그 추세 방향 신규 진입 안 함
				// SuspectBlockNeedPrice면 의심 뒤 가격 확인(직전 저점 이탈)이 있었을 때만 막는다 (사용자 결정 2026-10-15)
				string suspectBlock = BlockEntryWhileSuspect && st == d && suspectActive == -d && (!SuspectBlockNeedPrice || suspectPriceBroken)
					? (SuspectBlockNeedPrice ? "의심 중 + 가격 확인됨(직전 " + SwingBars + "봉 " + (d > 0 ? "저점 이탈" : "고점 돌파") + ")이라 추세 방향 신규 진입 안 함"
						: "의심 중이라 추세 방향 신규 진입 안 함")
					: null;
				// 추세 진입(E1·E2·E3)은 빠른 점수가 반대면 안 함 (TrendEntryNeedFast, 사용자 결정 2026-10-15):
				// 추세 판정은 늦어서, 반전 초입에는 판정은 옛 방향인데 F는 이미 반대다 (21일: 반대 의심 중 E2 8건 −$9,997)
				string fastBlock = TrendEntryNeedFast && d * F < 0 ? "빠른 점수 F가 반대 (" + Signed(F) + ")" : null;
				if (suspectBlock == null)
					suspectBlock = fastBlock;
				// E1이 열리는 경우: 추세 시작 / 반대 추세 청산 뒤 / 재가속 / 이번 구간 첫 진입 가능 봉에 이미 있는 추세
				bool sessionOpenNow	= E1AtSessionStart && !sessionOpenUsed && (z == Zone.Pre || z == Zone.Post);
				string e1Kind		= trendStartNow == d ? "추세 시작"
					: e1AfterExitDir == d && CurrentBars[0] - e1AfterExitBar <= E1AfterExitBars ? "반대 추세 청산 뒤"
					: E1Reaccel && reaccelNow == d ? "재가속"
					: sessionOpenNow ? "구간 첫 봉 추세"
					: null;
				double qtyPct = z == Zone.Pre && !fallback ? PreOpenQtyPercent : 100;
				if (fallback)
					qtyPct = MinEntryQtyPercent;

				// ── E1 추세 시작 ──
				{
					Candidate c = new Candidate { Path = "E1", Dir = d, QtyPercent = qtyPct, Fallback = fallback };
					bool armed = st == d && e1Kind != null;
					c.Note = e1Kind ?? "";
					string block = null;
					if (!UseEntryTrendStart)				block = "꺼짐";
					else if (st != d)						block = null;
					else if (timeBlock != null)				block = timeBlock;
					else if (suspectBlock != null)			block = suspectBlock;
					else if (z == Zone.Pre && !fallback && d * S < PreOpenTrendMinSlow)	block = "장전에는 S가 " + Signed(d * PreOpenTrendMinSlow) + " 이상이어야 함 (지금 " + Signed(S) + ")";
					if (block == null && armed)
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
					else if (st != d)				{ c.Line = "  E1 추세 시작: " + (d > 0 ? "상승" : "하락") + "추세가 아님"; c.Code = "-"; }
					else if (armed && block == null)	{ c.Ready = true; c.Line = "● E1 추세 시작: 지금 진입 (" + e1Kind + ")"; c.Code = "go"; }
					else if (armed)					{ c.Line = "× E1 추세 시작 (" + e1Kind + "): " + block; c.Code = "blk"; }
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
					else							{ c.Line = string.Format("○ E3 박스 돌파: 종가 {0} {1:N2}", d > 0 ? ">" : "<", edge); c.Code = "wait"; }
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
			if (best == null)
				return;

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
		}

		// 분할 진입. 조각 수량 = 진입 계약 × % 내림, 마지막 조각이 나머지 (4-8)
		private void Enter(Pos p, int d, string entryPath, double qtyPercent, bool fallback, double rr)
		{
			ResetTradeState();
			pos				= p;
			dir				= d;
			path			= entryPath;
			fallbackEntry	= fallback;
			session			= ZoneNow() == Zone.Pre ? "pre" : "post";
			rrExpected		= rr;
			int qty			= Math.Max(1, (int)Math.Floor(EntryQuantity * qtyPercent / 100.0));

			TradeBegin();
			stopAtr		= atr[0];
			stopSwing	= d > 0 ? signals.SwingLow5[0] : signals.SwingHigh5[0];	// 신호 봉 포함 최근 SwingBars봉
			initialStop	= CalcStop(Close[0]);
			stopPrice	= initialStop;
			MlBegin();

			// 시그널명은 거래마다 새로: TL7.1 = 7번째 진입의 1차 조각. NinjaTrader는 같은 시그널명에 걸었던 Set 익절·손절 값을 다음 진입에도 쓰고
			// 지우는 메서드가 없어서(NT8 문서 SetProfitTarget), 이름을 재사용하면 익절을 걸지 않는 조각에 지난 거래의 익절가가 남는다
			// (7/27 22:33: 트레일링으로 보낸 TS1·TS2가 21:35 거래의 익절가로 진입가 28522에 바로 체결). 멀리 거는 방법은 차트 가격 축이 늘어나서 뺐다
			signalSeq++;
			string prefix = p.ToString() + signalSeq + ".";
			string split;
			if (IsTrendPos())
			{
				int q1 = (qty * TrendTp1Percent) / 100;
				int q2 = (qty * TrendTp2Percent) / 100;
				// 신호 봉 종가가 익절 밴드를 이미 지나 있으면 (E2·E3, 사용자 결정 2026-10-15): 진입하자마자 진입가에 익절되지 않게
				// 1차 밴드를 넘었으면 1차 조각도 2차 밴드로, 2차 밴드까지 넘었으면 두 조각 모두 마지막 조각처럼 트레일링·기준선으로 내보낸다
				// 밴드까지 남은 거리가 예상 손절 거리 × TrendTpMinR보다 짧은 것도 지난 것으로 본다 (7/27 22:39: 1차까지 5pt, 손절 69pt. 사용자 결정 2026-10-15)
				int b1 = TrendTp1Band, b2 = TrendTp2Band;
				string passNote = "";
				double minRoom = TrendTpMinR * Math.Abs(Close[0] - initialStop);
				double room1 = d * (Band(d, TrendTp1Band) - Close[0]);
				double room2 = d * (Band(d, TrendTp2Band) - Close[0]);
				if (room2 <= 0 || room2 < minRoom)
				{
					b1 = NoBand;
					b2 = NoBand;
					tp2Done = true;	// 2차 뒤와 같이: 트레일링 시작, 마지막 조각 기준선 청산
					passNote = string.Format(" | 2차 밴드({0} {1:N2})까지 {2:F1}pt (최소 {3:F1}pt) → 1·2차 조각도 트레일링으로", BandText(d, TrendTp2Band), Band(d, TrendTp2Band), room2, minRoom);
				}
				else if (room1 <= 0 || room1 < minRoom)
				{
					b1 = TrendTp2Band;
					passNote = string.Format(" | 1차 밴드({0} {1:N2})까지 {2:F1}pt (최소 {3:F1}pt) → 1차 조각도 2차 밴드로", BandText(d, TrendTp1Band), Band(d, TrendTp1Band), room1, minRoom);
				}
				SubmitChunk(prefix + "1", q1, 1, b1, double.NaN);
				SubmitChunk(prefix + "2", q2, 2, b2, double.NaN);
				SubmitChunk(prefix + "3", qty - q1 - q2, 3, NoBand, double.NaN);
				split = q1 + "/" + q2 + "/" + (qty - q1 - q2) + passNote;
			}
			else
			{
				int q1 = (qty * SideTp1Percent) / 100;
				SubmitChunk(prefix + "1", q1, 1, NoBand, Close[0] + d * SideTp1Atr * stopAtr);	// 진입 때 ATR로 고정. 체결되면 체결가 기준으로 다시 건다
				SubmitChunk(prefix + "2", qty - q1, 3, NoBand, double.NaN);
				split = q1 + "/" + (qty - q1);
			}

			Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 {2} {3} 경로={4} 구간={5}{6} 수량={7} ({8}) 손절={9:F2} 손익비={10} 추세={11} S={12} F={13}",
				Time[0], Instrument.FullName, PosName(p), DirText(d), entryPath, session, fallback ? " 최소진입보장" : "",
				qty, split, stopPrice, double.IsNaN(rr) ? "-" : rr.ToString("F2"), RegimeState(), Signed(SlowNow()), Signed(FastNow())));
		}

		// 진입 조각 하나: 손절과 익절 지정가를 먼저 걸고 진입한다 (SetStopLoss는 진입 전에, NT8 공식 문서)
		private void SubmitChunk(string sig, int qty, int stage, int band, double fixedPrice)
		{
			if (qty <= 0)
				return;
			chunkQty[sig]	= qty;
			chunkStage[sig]	= stage;
			chunkBand[sig]	= band;
			chunkFixed[sig]	= fixedPrice;
			chunkOrder.Add(sig);
			liveSignals.Add(sig);
			SetStopLoss(sig, CalculationMode.Price, OrderSafeStop(stopPrice, Close[0]), false);
			SetTarget(sig);
			if (dir > 0)	EnterLong(qty, sig);
			else			EnterShort(qty, sig);
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
			double dist = StopDistance(dir, IsTrendPos(), entryPrice, stopSwing, stopAtr);
			return Instrument.MasterInstrument.RoundToTickSize(entryPrice - dir * dist);
		}

		private void SetStopForLive(double price)
		{
			foreach (string sig in liveSignals)
				SetStopLoss(sig, CalculationMode.Price, price, false);
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

			// 횡보 1단계 익절은 진입가 + dir × SideTp1Atr × 신호 봉 ATR: 체결가 기준으로 다시 건다
			if (!IsTrendPos())
				foreach (string sig in liveSignals)
					if (chunkStage[sig] == 1)
					{
						chunkFixed[sig] = avg + dir * SideTp1Atr * stopAtr;
						SetTarget(sig);
					}
			Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 재계산(체결가 기준) 평균 체결가={2} 손절={3}", time, Instrument.FullName, avg, s));
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
			if (BreakevenAtR <= 0 || tp1Done || breakevenDone || entryFillQty <= 0 || double.IsNaN(trStop0))
				return;
			double avg	= AvgPrice();
			double risk	= Math.Abs(avg - trStop0);
			if (risk <= 0 || trMfe < BreakevenAtR * risk)
				return;
			if (lastStopLabel != "이익 보호 본절")
				Out(string.Format("[{0}][{1}][TQ_Strategy] 최대 이익 {2:F1}pt ≥ 손절 거리 {3:F1}pt × {4} → 손절을 평단으로 (이익 보호)",
					Time[0], Instrument.FullName, trMfe, risk, BreakevenAtR));
			MoveStopTo(avg, "이익 보호 본절", Time[0]);
		}

		// 개장 보호 (ProtectBeforeOpen): 개장 직전 봉(OpenTime − 1분) 마감에 이익이 ATR × ProtectAtr 이상이면 손절을 평단으로
		private void ProtectBeforeOpenCheck()
		{
			if (!ProtectBeforeOpen || entryFillQty <= 0)
				return;
			if (ToTime(Time[0]) != MinOfDayToHhmmss(MinOfDay(OpenTime) - 1))
				return;
			double avg = AvgPrice();
			if (dir * (Close[0] - avg) >= ProtectAtr * atr[0])
				MoveStopTo(avg, "개장 전 보호", Time[0]);
		}
		#endregion

		#region 익절 지정가 (봉 진행 중, IntrabarTakeProfit)
		// 조각의 익절 가격: 밴드 익절은 방금 마감된 봉의 밴드 값, 횡보 1단계는 고정 가격. 없으면 NaN
		private double TargetPrice(string sig)
		{
			double fixedPrice;
			if (chunkFixed.TryGetValue(sig, out fixedPrice) && !double.IsNaN(fixedPrice))
				return fixedPrice;
			int band;
			if (!chunkBand.TryGetValue(sig, out band) || band == NoBand)
				return double.NaN;
			return Band(dir, band);
		}

		private void SetTarget(string sig)
		{
			double price = !IntrabarTakeProfit || UseTestEntry ? double.NaN : TargetPrice(sig);
			if (double.IsNaN(price))
				return;	// 익절이 없는 조각. 시그널명이 거래마다 새것이라(Enter의 signalSeq) 지난 거래의 익절 값이 남아 있지 않다
			SetProfitTarget(sig, CalculationMode.Price, Instrument.MasterInstrument.RoundToTickSize(price));
		}

		// 봉 마감마다 밴드 익절 지정가를 새 밴드 값으로 옮긴다. 체결 콜백이 liveSignals를 바꿀 수 있어 복사본으로 돈다
		private void UpdateTargets()
		{
			if (IntrabarTakeProfit && !UseTestEntry)
			{
				foreach (string sig in liveSignals.ToArray())
					SetTarget(sig);
				return;
			}
			// IntrabarTakeProfit 끔: 봉 마감 종가로 판단해 시장가
			foreach (string sig in liveSignals.ToArray())
			{
				double price = TargetPrice(sig);
				if (!double.IsNaN(price) && dir * (Close[0] - price) >= 0)
				{
					pendingBandExit.Add("x" + sig);
					ExitChunk(sig, chunkStage[sig] == 1 ? "TP1" : "TP2", "익절(봉 마감)");
				}
			}
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
			if (BreakevenMinR <= 0 || double.IsNaN(risk) || risk <= 0 || gain >= BreakevenMinR * risk)
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
			if (!IsTrendPos() || !tp2Done || TrailAtr <= 0)
				return;
			double a = atr[0];
			if (double.IsNaN(a) || a <= 0)
				return;
			double before = stopPrice;
			MoveStopTo(trailExtreme - dir * TrailAtr * a, "트레일링", Time[0]);
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
			int st = RegimeState();
			if (st == dir)
				sawOwnTrend = true;

			// 반대 추세 시작 → 항상 전량 청산 (4-1 원칙 4). 같은 봉 반대 진입은 없음. 다음 봉에 E1을 허용한다 (사용자 결정 2026-10-15)
			if (st == -dir)
			{
				e1AfterExitDir	= st;
				e1AfterExitBar	= CurrentBars[0];
				FlattenRemaining("OPP_TREND", "반대 추세 시작");
				return;
			}
			// 추세 종료(횡보) → 전량 청산 (TrendExitOnEnd). 보유 중 그 방향 추세였던 적이 있을 때만 (E3로 횡보 중 들어간 경우를 위해)
			if (TrendExitOnEnd && st == 0 && sawOwnTrend)
			{
				FlattenRemaining("TREND_END", "추세 종료 (S ≤ " + TrendEndSlow + ")");
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

		// 4-5 의심 이벤트: 포지션 반대 방향 의심이 뜨면 SuspectConfirmBars봉 동안 가격 확인(종가가 직전 SwingBars봉 저점 아래)을 기다렸다가
		// 남은 수량의 SuspectExitPercent(%)를 청산한다. S ≤ SuspectStrongSlow면 SuspectStrongExitPercent. 의심 하나당 한 번. 청산했으면 true
		private bool HandleSuspect(int st)
		{
			if (suspectEventNow == -dir && st == dir && lastRegimeBar != suspectDoneBar)
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
			if (regime.SuspectActive[0] != -dir || st != dir)
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
			int st = RegimeState();
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
		// 조각 하나를 시장가로 청산. 체결 콜백이 주문 호출 도중에 올 수 있어 상태를 먼저 바꾼다
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
			Order o = dir > 0 ? ExitLong(0, q, "x" + sig, sig) : ExitShort(0, q, "x" + sig, sig);
			if (o != null)
				exitOrders.Add(o);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 청산 {2} 시그널={3} 수량={4} ({5})", Time[0], Instrument.FullName, code, sig, q, why));
		}

		private int ChunkLeft(string sig)
		{
			int q;
			return chunkLeft.TryGetValue(sig, out q) ? q : 0;
		}

		// 전량 청산: 살아 있는 조각마다 실제 보유 수량만큼 청산한다. ExitLong()처럼 수량 없이 내면 NinjaTrader가 조각별로 기억하는 수량을 써서
		// 실제 포지션보다 많이 청산하고 반대 포지션이 생길 수 있다 (10/6 Playback). 조각 기록으로 다 못 덮은 수량은 다음 봉에 FlattenLeftover가 정리한다
		private void FlattenRemaining(string code, string why)
		{
			exitReason = code;
			int sent = 0;
			foreach (string sig in liveSignals.ToArray())
			{
				int q = ChunkLeft(sig);
				if (q <= 0)
					continue;
				exitSent.Add(sig);
				Order o = dir > 0 ? ExitLong(0, q, "x" + sig, sig) : ExitShort(0, q, "x" + sig, sig);
				if (o != null)
					exitOrders.Add(o);
				sent += q;
			}
			liveSignals.Clear();
			pendingBandExit.Clear();
			pos = Pos.None;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 전량 청산 {2} ({3}) 수량={4}", Time[0], Instrument.FullName, code, why, sent));
			if (sent == 0)
				FlattenLeftover();
		}

		// 조각과 상관없이 남은 포지션을 실제 보유 수량(Position.Quantity)만큼 청산한다. 직접 낸 청산 주문이 아직 살아 있으면 기다린다 (같은 수량을 두 번 내지 않게)
		private void FlattenLeftover()
		{
			if (Position.MarketPosition == MarketPosition.Flat)
				return;
			foreach (Order o in exitOrders)
				if (o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled && o.OrderState != OrderState.Rejected)
					return;
			int q = Position.Quantity;
			Order x = Position.MarketPosition == MarketPosition.Long ? ExitLong(0, q, "Flat", "") : ExitShort(0, q, "Flat", "");
			if (x != null)
				exitOrders.Add(x);
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
			stopNote		= "";
			deferredStop	= double.NaN;
			lastStopTarget	= double.NaN;
			lastStopLabel	= "";
			trailExtreme	= double.NaN;
			trailUsed		= false;
			tp1Done			= false;
			tp2Done			= false;
			tp1FillPrice	= double.NaN;
			tpStagesHit		= 0;
			suspectWaitLeft	= 0;
			suspectEventsSeen	= 0;
			suspectAction	= "";
			rrExpected		= double.NaN;
			testHoldBars	= 0;
			liveSignals.Clear();
			chunkOrder.Clear();
			chunkQty.Clear();
			chunkStage.Clear();
			chunkBand.Clear();
			chunkFixed.Clear();
			pendingBandExit.Clear();
			chunkLeft.Clear();
			stageFilled.Clear();
			exitSent.Clear();
			exitOrders.Clear();
		}
		#endregion

		#region 주문 이벤트
		// 주문 오류 처리 (HandleOrderErrors): 손절 변경 거부 → 실제 가격으로 되돌리고 나중에 재시도, 손절 주문 거부 → 즉시 전량 청산, 진입 거부 → 그 조각 제외
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
				return;

			if (name == "Stop loss")
			{
				if (rejected)
				{
					if (Position.MarketPosition != MarketPosition.Flat)
					{
						gapFlattened = true;
						Out(string.Format("[{0}][{1}][TQ_Strategy] 손절 주문 거부 → 손절 없이 둘 수 없어 즉시 전량 청산", time, Instrument.FullName));
						FlattenRemaining("STOP", "손절 주문 거부");
					}
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
			else if (rejected && chunkQty.ContainsKey(name))
			{
				liveSignals.Remove(name);
				Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 주문 거부 → 시그널 {2} 제외", time, Instrument.FullName, name));
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;
			string name = execution.Order.Name;
			MarkExecution(name, price, quantity, marketPosition, time);

			if (!chunkQty.ContainsKey(name))
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 청산 체결 {2} 가격={3} 수량={4}", time, Instrument.FullName, name, price, quantity));
				TradeExitFill(name, price, quantity, time);
			}

			if (chunkQty.ContainsKey(name))
			{
				// 조각 진입 체결: 실제 보유 수량을 늘린다. 익절·손절로 0이 됐다가 늦게 온 부분 체결이면 다시 살린다(직접 청산한 조각은 빼고)
				chunkLeft[name] = ChunkLeft(name) + quantity;
				if (chunkLeft[name] > 0 && pos != Pos.None && !exitSent.Contains(name) && !liveSignals.Contains(name))
					liveSignals.Add(name);
				OnEntryFill(price, quantity, time);
			}
			else
			{
				if (name == "Profit target")
					OnTargetFill(execution.Order.FromEntrySignal, price, time);
				else if (pendingBandExit.Remove(name))
				{
					string sig = name.Substring(1);
					if (chunkStage.ContainsKey(sig) && stageFilled.Add(sig))
						OnStageFill(sig, price, time);
				}
				else if (name == "Stop loss")
					OnStopLossFill(price, time);

				// 그 조각의 실제 보유 수량을 줄이고, 0 이하가 되면 조각을 끝낸다 (손절·익절·직접 청산 모두).
				// 청산 체결 콜백이 진입 체결 콜백보다 먼저 올 수 있어(7/27 22:33) 음수를 허용하고 뒤에 오는 진입 체결로 맞춘다
				string from = execution.Order.FromEntrySignal;
				if (!string.IsNullOrEmpty(from) && chunkQty.ContainsKey(from))
				{
					chunkLeft[from] = ChunkLeft(from) - quantity;
					if (chunkLeft[from] <= 0)
						liveSignals.Remove(from);
				}
			}

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

		private void TradeExitFill(string name, double price, int quantity, DateTime time)
		{
			if (!trOpen || entryFillQty <= 0)
				return;
			double avg = entryFillSum / entryFillQty;
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
			int st = RegimeState();
			int ev = regime.SuspectEvent[0];
			int ts = regime.TrendStart[0];
			Out(string.Format("[{0}][{1}][TQ_Strategy] {2}분봉 마감 추세={3} S={4} F={5} ({6}) 추세시작={7} 의심={8} 종가={9} 이평={10:F2}/{11:F2}/{12:F2} MACD선={13:F2} RSI={14:F2}",
				Time[0], Instrument.FullName, RegimeBarMinutes, StateText(st), Signed(SlowNow()), Signed(FastNow()), regime.ScoreText,
				ts == 0 ? "-" : (ts > 0 ? "상승" : "하락"),
				ev != 0 ? (ev < 0 ? "하락의심 이벤트" : "상승의심 이벤트") : regime.SuspectActive[0] != 0 ? "의심 중" : "-",
				Closes[regimeBip][0], regime.SmaFast[0], regime.SmaMid[0], regime.SmaSlow[0], regime.MacdLine[0], regime.Rsi[0]));
		}

		// 진입 봉 마감 줄: 추세·점수·의심·구간·후보 경로별 상태·보유 상태·시고저종 (7장)
		private void LogEntryBar(List<Candidate> cands)
		{
			string cand = "";
			if (cands != null)
				foreach (Candidate c in cands)
					cand += (cand.Length > 0 ? " " : "") + c.Path + (c.Dir > 0 ? "매수" : "매도") + "=" + c.Code;
			string hold = Position.MarketPosition == MarketPosition.Flat ? "없음"
				: string.Format("{0} {1}계약 손절={2:F2}{3}{4}", PosName(pos), Position.Quantity, stopPrice,
					suspectWaitLeft > 0 ? " 의심확인대기=" + suspectWaitLeft : "", tp2Done ? " 2차뒤" : tp1Done ? " 1차뒤" : "");
			Out(string.Format("[{0}][{1}][TQ_Strategy] 추세={2} S={3} F={4} ({5}) 의심={6} 구간={7} 후보 {8} 보유={9} 시고저종={10}/{11}/{12}/{13}",
				Time[0], Instrument.FullName, RegimeState(), Signed(SlowNow()), Signed(FastNow()), regime.ScoreText,
				regime.SuspectActive[0] < 0 ? "하락의심" : regime.SuspectActive[0] > 0 ? "상승의심" : "-",
				ZoneText(ZoneNow()), cand.Length > 0 ? cand : "-", hold, Open[0], High[0], Low[0], Close[0]));
		}
		#endregion

		#region 검증 — 임시 진입 (UseTestEntry)
		// 진입 시간대에 미보유면 추세 매수로 들어가 2봉째 1차, 3봉째 2차, 4봉째 전량 청산 (익절 지정가는 걸지 않음)
		private void RouteTestEntry()
		{
			Enter(Pos.TL, 1, "TEST", 100, false, double.NaN);
		}

		private void ManageTestExits()
		{
			if (testHoldBars == TestTp1Bar)
			{
				foreach (string sig in liveSignals.ToArray())
					if (chunkStage[sig] == 1)
					{
						pendingBandExit.Add("x" + sig);
						ExitChunk(sig, "TP1", "TEST 1차");
					}
			}
			else if (testHoldBars == TestTp2Bar)
			{
				foreach (string sig in liveSignals.ToArray())
					if (chunkStage[sig] == 2)
					{
						pendingBandExit.Add("x" + sig);
						ExitChunk(sig, "TP2", "TEST 2차");
					}
			}
			else if (testHoldBars >= TestExitBar)
				FlattenRemaining("FLATTEN_TIME", "TEST 최종 청산");
		}
		#endregion

		#region 화면 표시 — 매매 판단에는 영향 없음
		// 진입 봉마다: 추세 배경색(의심 중이면 주황·하늘색), 장전 박스 가로선(22:30 이후)
		private void PaintBar()
		{
			if (!ShowStatus || ChartControl == null)
				return;
			int st = RegimeState();
			int sus = regime.SuspectActive[0];
			BackBrush = st == 1 ? (sus != 0 ? upSuspectBrush : upBackBrush) : st == -1 ? (sus != 0 ? downSuspectBrush : downBackBrush) : null;

			if (BoxReady())
			{
				Draw.Line(this, "TQ_BoxH_" + sessionTag, false, boxStartTime, boxHigh, Time[0], boxHigh, Brushes.Goldenrod, DashStyleHelper.Dash, 1);
				Draw.Line(this, "TQ_BoxL_" + sessionTag, false, boxStartTime, boxLow, Time[0], boxLow, Brushes.Goldenrod, DashStyleHelper.Dash, 1);
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
			int sus	= regime.SuspectActive[0];
			int S	= SlowNow();
			int F	= FastNow();
			Zone z	= ZoneNow();

			// ── 현재 상태 ──
			sb.Section("현재 상태", StateTone(st, sus), true);
			sb.AppendFormat("{0}{1}   {2:HH:mm}\n", StateText(st), sus < 0 ? " · 하락의심" : sus > 0 ? " · 상승의심" : "", Times[0][0]);
			sb.AppendFormat("{0}분봉: S {1} · F {2} ({3})\n", RegimeBarMinutes, Signed(S), Signed(F), regime.ScoreText);
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
			sb.AppendFormat("{0}분봉: MACD {1}  %K {2:F0} / %D {3:F0}  RSI {4:F0}  ATR {5:F1}\n", BarsPeriod.Value,
				signals.MacdUp[0] ? "▲ 전환" : signals.MacdDown[0] ? "▼ 전환" : signals.MacdRising[0] ? "▲" : signals.MacdFalling[0] ? "▼" : "−",
				signals.K[0], signals.D[0], signals.Rsi[0], atr[0]);

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				RemoveDrawObject("TQ_StopLine");
				RemoveDrawObject("TQ_AvgLine");
				sb.Section("진입 조건", ToneEntry);
				if (UseTestEntry)
					sb.Append("TEST: 진입 시간대에 다음 봉 검증용 매수\n");
				else if (cands != null)
				{
					// 그 추세에서 할 수 있는 방향을 먼저, 경로 순서대로
					for (int k = 0; k < 2; k++)
					{
						int d = (k == 0) == (st >= 0) ? 1 : -1;
						bool any = false;
						foreach (Candidate c in cands)
						{
							if (c.Dir != d)
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
				if (!double.IsNaN(stopPrice))
					Draw.HorizontalLine(this, "TQ_StopLine", false, stopPrice, Brushes.Magenta, DashStyleHelper.Solid, 2);
				Draw.HorizontalLine(this, "TQ_AvgLine", false, avg, Brushes.Gray, DashStyleHelper.Dash, 1);
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
				string label = IsTrendPos() ? (stage == 1 ? "1차 " : "2차 ") + BandText(dir, chunkBand[sig]) : "1단계 진입가" + (dir > 0 ? "+" : "−") + SideTp1Atr + "ATR";
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
					: "  → 2차 뒤 트레일링 " + (dir > 0 ? "최고가 − " : "최저가 + ") + TrailAtr + " ATR\n");
			if (!tp1Done)
				sb.Append("  → 1" + (IsTrendPos() ? "차" : "단계") + " 익절 뒤 평단으로\n");

			// ── 청산 ──
			sb.Section("청산", ToneExit);
			if (pos == Pos.None)
				sb.Append("● 청산 주문 냄, 체결 대기\n");
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

			List<PanelSection> sections = panelSections;
			if (!ShowStatus || sections == null || sections.Count == 0 || RenderTarget == null || ChartPanel == null)
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

		// 체결 라벨과 화살표: 진입은 화살표(매수 ↑ 초록, 매도 ↓ 빨강) + 라벨, 청산은 라벨. 같은 봉 같은 종류는 수량을 합친다
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
				if (isBuy)	Draw.ArrowUp(this, "TQ_Arr_" + CurrentBars[0], false, time, Low[0] - TickSize * 8, Brushes.LimeGreen);
				else		Draw.ArrowDown(this, "TQ_Arr_" + CurrentBars[0], false, time, High[0] + TickSize * 8, Brushes.Tomato);
			}
			else if (name.StartsWith("x") || name == "Profit target")
			{
				kind	= "익절·청산";
				brush	= Brushes.Goldenrod;
			}
			else if (name == "Stop loss")
			{
				kind	= !breakevenDone ? "손절" : stopNote == "트레일링" ? "트레일링 스탑" : "본절";
				brush	= Brushes.Magenta;
			}
			else
			{
				kind	= "청산";
				brush	= Brushes.DodgerBlue;
			}
			// 청산 화살표: 매도 체결(매수 청산)은 체결가 위에 ↓, 매수 체결(매도 청산)은 아래에 ↑ (파랑)
			if (!chunkQty.ContainsKey(name))
			{
				if (isBuy)	Draw.ArrowUp(this, "TQ_ArrX_" + CurrentBars[0], false, time, price - TickSize * 6, Brushes.DodgerBlue);
				else		Draw.ArrowDown(this, "TQ_ArrX_" + CurrentBars[0], false, time, price + TickSize * 6, Brushes.DodgerBlue);
			}
			string tag	= "TQ_Ex_" + kind + "_" + CurrentBars[0];
			markQty		= tag == markTag ? markQty + quantity : quantity;
			markTag		= tag;
			Draw.Text(this, tag, false, kind + " " + markQty, time, price, isBuy ? -20 : 20, brush, markFont,
				System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
		}
		#endregion

		#region Properties
		// 설정창 배치 원칙 (v3 지시서 5-1): 묶음은 판단 순서, 한 묶음 안에서 [설계] 위 [선택] 아래.
		// 태그: [설계] 설계안의 동작과 값 · [선택] 고민사항 스위치 · [안전] 주문 거부 대비 · [표시] 화면·기록

		// ── 01. 추세 판단 (추세 봉) ──
		[NinjaScriptProperty]
		[Range(3, 10)]
		[Display(Name = "[설계] 추세 봉 분: 3 / 10 (RegimeBarMinutes)", Description = "추세를 판단하는 봉. 기본 3(3분봉). 10이면 10분봉(이때 이동평균은 6/18/36 권장). 3과 10만 쓸 수 있고 다른 값은 3으로 봄. 이 묶음의 봉 수는 모두 이 봉 기준", GroupName = "01. 추세 판단 (추세 봉)", Order = 0)]
		public int RegimeBarMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 이동평균 단기 (RegimeFast)", Description = "기본 6. F1(종가 > 단기선 AND 단기선 상승)과 마지막 조각 기준선 2에 씀. 줄이면 빠른 점수가 빨리 바뀜", GroupName = "01. 추세 판단 (추세 봉)", Order = 1)]
		public int RegimeFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 이동평균 중기 (RegimeMid)", Description = "기본 20. S1(종가 vs 중기선), S2 방식 1(중기 > 장기), 마지막 조각 기준선 1에 씀", GroupName = "01. 추세 판단 (추세 봉)", Order = 2)]
		public int RegimeMid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 이동평균 장기 (RegimeSlow)", Description = "기본 60. S2(구조)에 씀. 늘리면 추세가 늦게 바뀌고 데이터가 더 필요함", GroupName = "01. 추세 판단 (추세 봉)", Order = 3)]
		public int RegimeSlow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2)]
		[Display(Name = "[설계] 추세 시작: 빠른 점수 F 기준 (TrendStartFast)", Description = "기본 1(2026-10-15 사용자 결정, 그 전 2). F ≥ 이 값 AND S ≥ 느린 점수 기준이면 상승추세 시작(하락은 거울상). 내리면 추세 인정이 빠르지만 잦아짐", GroupName = "01. 추세 판단 (추세 봉)", Order = 4)]
		public int TrendStartFast { get; set; }

		[NinjaScriptProperty]
		[Range(0, 4)]
		[Display(Name = "[설계] 추세 시작: 느린 점수 S 기준 (TrendStartSlow)", Description = "기본 1(2026-10-15 사용자 결정, 그 전 2). 추세 시작에 필요한 S. 올리면 추세 인정이 느리지만 확실해짐. E1 재가속(E1R)은 이 값과 따로 F ±2·S ±2", GroupName = "01. 추세 판단 (추세 봉)", Order = 5)]
		public int TrendStartSlow { get; set; }

		[NinjaScriptProperty]
		[Range(-4, 4)]
		[Display(Name = "[설계] 추세 종료: S가 이 값 이하 (TrendEndSlow)", Description = "기본 0. 상승추세는 S ≤ 이 값이면 횡보로(하락추세는 S ≥ −값). 시작 기준(2)과의 차이가 흔들림을 막음. 올리면 추세가 빨리 끝남", GroupName = "01. 추세 판단 (추세 봉)", Order = 6)]
		public int TrendEndSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] MACD선 기울기 연속 봉 수 (RegimeMacdSlopeBars)", Description = "기본 2. F2: MACD선이 이 봉 수만큼 연속 커지면 +1, 작아지면 −1. 늘리면 F2가 늦고 덜 흔들림", GroupName = "01. 추세 판단 (추세 봉)", Order = 7)]
		public int RegimeMacdSlopeBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] S2 방식: 1 중기>장기 / 2 장기선 기울기 (RegimeS2Mode)", Description = "기본 1(중기선 > 장기선이면 +1). 2면 장기선이 아래 봉 수 전보다 높으면 +1. 장기선을 배열로 볼지 기울기로 볼지", GroupName = "01. 추세 판단 (추세 봉)", Order = 10)]
		public int RegimeS2Mode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[선택] S2 방식 2: 기울기 봉 수 (RegimeSlopeBars)", Description = "기본 5. S2 방식 2에서 장기선을 몇 봉 전과 비교할지. S2 방식이 1이면 쓰지 않음", GroupName = "01. 추세 판단 (추세 봉)", Order = 11)]
		public int RegimeSlopeBars { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] RSI 항목 사용 (RegimeUseRsi)", Description = "기본 켬. S4: RSI ≥ 50 + 폭이면 +1, ≤ 50 − 폭이면 −1. 끄면 S는 −3 ~ +3. RSI 수준이 지연을 만드는지 비교용", GroupName = "01. 추세 판단 (추세 봉)", Order = 12)]
		public bool RegimeUseRsi { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "[선택] RSI 항목 폭 (RegimeRsiBand)", Description = "기본 5(55 이상 / 45 이하). RSI 항목을 끄면 쓰지 않음", GroupName = "01. 추세 판단 (추세 봉)", Order = 13)]
		public double RegimeRsiBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 추세 끝날 때 바로 반전 (RegimeDirectReverse)", Description = "기본 끔(2026-10-15 사용자 결정). 추세가 끝나는 봉(S가 종료 기준에 닿음)에 F가 이미 반대로 ±2면 횡보를 건너뛰고 반대 추세로. 21일 재현: 횡보 판정 8 → 5%, 추세 전환 35 → 50번, 반대 판정 시간은 그대로(28 → 29%)", GroupName = "01. 추세 판단 (추세 봉)", Order = 14)]
		public bool RegimeDirectReverse { get; set; }

		// ── 02. 의심 이벤트 ──
		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "[설계] 의심: 빠른 점수가 반대로 이 값 (SuspectFast)", Description = "기본 2. 상승추세 중 F ≤ −2가 된 첫 봉에 하락의심 이벤트(하락추세는 거울상). F가 0 이상으로 돌아오면 해제되고 다시 생길 수 있음. 1이면 자주 뜸", GroupName = "02. 의심 이벤트", Order = 0)]
		public int SuspectFast { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] 의심: 가격 확인 필요 (SuspectNeedPrice)", Description = "기본 켬. 의심 이벤트 뒤 아래 봉 수 안에 진입 봉 종가가 직전 손절 봉 수(06 묶음)의 저점 아래로 마감해야(매도는 고점 위) 일부청산. 끄면 이벤트 즉시 실행", GroupName = "02. 의심 이벤트", Order = 1)]
		public bool SuspectNeedPrice { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 의심: 가격 확인 대기 봉 수 (SuspectConfirmBars)", Description = "기본 5(진입 봉). 이 안에 가격 확인이 없거나 의심이 해제되면 아무것도 안 함. 가격 확인을 끄면 쓰지 않음", GroupName = "02. 의심 이벤트", Order = 2)]
		public int SuspectConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[선택] 의심 일부청산 % (SuspectExitPercent)", Description = "기본 50. 확인되면 남은 수량의 이 %를 시장가 청산(먼 조각부터 조각 단위로, 가장 가까운 수량). 33 / 50 / 100 비교용", GroupName = "02. 의심 이벤트", Order = 10)]
		public int SuspectExitPercent { get; set; }

		[NinjaScriptProperty]
		[Range(-4, 4)]
		[Display(Name = "[선택] 강한 의심: 느린 점수 기준 (SuspectStrongSlow)", Description = "기본 1. 확인 때 S ≤ 이 값이면(매도는 S ≥ −값) 위 % 대신 아래 강한 의심 %로 청산", GroupName = "02. 의심 이벤트", Order = 11)]
		public int SuspectStrongSlow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[선택] 강한 의심 때 청산 % (SuspectStrongExitPercent)", Description = "기본 100(전량)", GroupName = "02. 의심 이벤트", Order = 12)]
		public int SuspectStrongExitPercent { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] 의심 중 추세 방향 신규 진입 금지 (BlockEntryWhileSuspect)", Description = "기본 켬. 상승추세 중 하락의심이 살아 있으면 매수 E1·E2·E3을 하지 않음(하락은 거울상). 역진입은 원래 없음", GroupName = "02. 의심 이벤트", Order = 13)]
		public bool BlockEntryWhileSuspect { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 의심 진입 금지는 가격 확인 뒤에만 (SuspectBlockNeedPrice)", Description = "기본 끔(2026-10-15 켬으로 넣었다가 같은 날 되돌림: 의심 중 E2 8건 3승 −$9,997). 켜면 의심이 뜬 뒤 진입 봉 종가가 직전 구조 손절 봉 수의 저점 아래로 마감한 적이 있을 때만 위 진입 금지를 적용(매도는 고점 위). 끄면 의심이 살아 있는 동안 항상 막음", GroupName = "02. 의심 이벤트", Order = 14)]
		public bool SuspectBlockNeedPrice { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 추세 진입은 빠른 점수가 반대면 안 함 (TrendEntryNeedFast)", Description = "기본 켬(2026-10-15 사용자 결정). 매수 E1·E2·E3는 F ≥ 0, 매도는 F ≤ 0일 때만. 추세 판정은 늦어서 반전 초입에는 판정은 옛 방향인데 F는 이미 반대(21일: 반대 의심 중 E2 8건 −$9,997)", GroupName = "02. 의심 이벤트", Order = 15)]
		public bool TrendEntryNeedFast { get; set; }

		// ── 03. 진입 — 추세 ──
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E1 추세 시작 봉 진입 (UseEntryTrendStart)", Description = "기본 켬. 추세 봉의 추세 시작을 처음 본 진입 봉 마감에 그 방향으로 진입. 종가가 이미 1차 익절 밴드를 넘었거나 손익비가 모자라면 안 함. 반대 추세 시작으로 청산한 뒤에는 다음 2봉 안에도 허용", GroupName = "03. 진입 — 추세", Order = 0)]
		public bool UseEntryTrendStart { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E2 추세 눌림 진입 (UseEntryPullback)", Description = "기본 켬. 추세 중 저가가 아래 밴드에 닿은 뒤(매도는 고가가 위 밴드) 대기 봉 수 안에 [MACD 상승 전환 OR 골든크로스]가 나오면 진입", GroupName = "03. 진입 — 추세", Order = 1)]
		public bool UseEntryPullback { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[설계] E2 눌림 밴드 (TrendPullbackBand)", Description = "기본 0 = 중심선(2026-10-15 사용자 결정, 그 전 1 = 매수 −1배·매도 +1배). 올리면 깊은 눌림만 받음", GroupName = "03. 진입 — 추세", Order = 2)]
		public int TrendPullbackBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 4)]
		[Display(Name = "[선택] E2 최소 느린 점수 (PullbackMinSlow)", Description = "기본 2(2026-10-15 사용자 결정). 매수 E2는 S ≥ 이 값, 매도는 S ≤ −값일 때만. 추세 끝물(S ±1)의 눌림을 거름. 0 = 안 봄", GroupName = "03. 진입 — 추세", Order = 13)]
		public int PullbackMinSlow { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E3 장전 박스 돌파 진입 (UseEntryOrb)", Description = "기본 켬. 장후에 아래 마감 시각 전, 진입 봉 종가가 장전 박스(21:30~22:30 최고가) 위로 마감하고 S·F가 아래 기준 이상이면 추세 매수(매도는 박스 최저가 아래). 장후 방향별 한 번", GroupName = "03. 진입 — 추세", Order = 3)]
		public bool UseEntryOrb { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] E3 마감 시각 (OrbEndTime)", Description = "HHmmss, 기본 230000. 이 시각 전의 신호만 E3로 받음", GroupName = "03. 진입 — 추세", Order = 4)]
		public int OrbEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 4)]
		[Display(Name = "[설계] E3 최소 느린 점수 (OrbMinSlow)", Description = "기본 1. 매수는 S ≥ 1, 매도는 S ≤ −1", GroupName = "03. 진입 — 추세", Order = 5)]
		public int OrbMinSlow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2)]
		[Display(Name = "[설계] E3 최소 빠른 점수 (OrbMinFast)", Description = "기본 1. 매수는 F ≥ 1, 매도는 F ≤ −1", GroupName = "03. 진입 — 추세", Order = 6)]
		public int OrbMinFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "[선택] E3 방향별 최대 횟수 (OrbMaxEntries)", Description = "기본 2(2026-10-15 사용자 결정, 그 전에는 1). 한 번 쓰면 진입 봉 종가가 박스 안(매수는 박스 고가 이하, 매도는 저가 이상)으로 돌아온 뒤에야 다시 돌파를 받음. 1 = 지시서 원래 규칙", GroupName = "03. 진입 — 추세", Order = 7)]
		public int OrbMaxEntries { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[선택] E1·E2 최소 손익비 (EntryMinRR)", Description = "기본 1.0. E1·E2는 (2차 익절 밴드 − 종가) ≥ 예상 손절 거리 × 이 값일 때만(E2는 2026-10-15부터). 0 = 안 봄. 0 / 1 / 1.5 비교용", GroupName = "03. 진입 — 추세", Order = 10)]
		public double EntryMinRR { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] E1 추세 재가속 진입 (E1Reaccel)", Description = "기본 켬(2026-10-15 사용자 결정). 추세가 이어지는 중에 F가 0 이하로 내려갔다가(매도는 0 이상) 다시 추세 시작 조건(F, S)을 채운 추세 봉을 처음 본 진입 봉에서 E1과 같은 필터로 진입. 경로 이름 E1R", GroupName = "03. 진입 — 추세", Order = 11)]
		public bool E1Reaccel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] E1 구간 첫 봉에 이미 있는 추세로 진입 (E1AtSessionStart)", Description = "기본 끔(2026-10-15 넣고 같은 날 끔: 21일 10건 3승 −$2,792). 대회 구간의 첫 진입 가능 봉(기본 21:35, 장전이면 장전 수량·S 기준 적용)에 추세가 있으면 E1과 같은 필터로 그 방향 진입. 경로 이름 E1S", GroupName = "03. 진입 — 추세", Order = 12)]
		public bool E1AtSessionStart { get; set; }

		// ── 04. 진입 — 횡보 ──
		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[설계] E4 횡보 진입 (UseEntrySide)", Description = "기본 켬. 횡보 중 저가가 아래 밴드에 닿은 뒤 대기 봉 수 안에 [MACD 상승 전환 OR 골든크로스]면 횡보 매수(매도는 거울상)", GroupName = "04. 진입 — 횡보", Order = 0)]
		public bool UseEntrySide { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[설계] E4 횡보 밴드 (SideBand)", Description = "기본 2(매수 −2배, 매도 +2배)", GroupName = "04. 진입 — 횡보", Order = 1)]
		public int SideBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[설계] 밴드 없이 T2로도 횡보 진입 (SideT2Entry)", Description = "기본 켬. MACD 전환 AND 크로스(같은 봉 또는 이웃 봉)만으로도 횡보 진입. 끄면 밴드 조건 뒤의 신호만", GroupName = "04. 진입 — 횡보", Order = 2)]
		public bool SideT2Entry { get; set; }

		[NinjaScriptProperty]
		[Range(0, 6)]
		[Display(Name = "[설계] 횡보 방향 필터 점수 (SideBlockScore)", Description = "기본 3. S+F ≥ 3이면 횡보 매도를, ≤ −3이면 횡보 매수를 하지 않음. 0이면 막지 않음", GroupName = "04. 진입 — 횡보", Order = 3)]
		public int SideBlockScore { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 손실 손절 뒤 같은 방향 횡보 재진입 금지 (BlockSideAfterStop)", Description = "기본 켬. 횡보 포지션이 본절 전 손실 손절로 끝나면 그 대회 구간에서 같은 방향 횡보 진입을 하지 않음. 추세 포지션은 제한 없음", GroupName = "04. 진입 — 횡보", Order = 10)]
		public bool BlockSideAfterStop { get; set; }

		// ── 05. 진입 — 공통 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 반전 신호 대기 봉 수 (T1Window)", Description = "기본 5. 밴드 조건 뒤 반전 신호를 기다리는 봉 수. 실제로는 + (MACD 확인 봉 수 − 1) = 6봉", GroupName = "05. 진입 — 공통", Order = 0)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] MACD 전환 확인 봉 수 (MacdConfirmBars)", Description = "기본 2. 진입 봉 MACD 히스토그램이 이 봉 수 연속 커져야(작아져야) 전환. 전환은 처음 확인된 봉 하나에서만 참", GroupName = "05. 진입 — 공통", Order = 1)]
		public int MacdConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[설계] T2에서 MACD 꺾인 봉의 크로스 인정 (T2CountConfirmBars)", Description = "기본 켬. MACD 전환이 확인된 봉에서 확인 봉 수만큼 앞의 크로스도 같이 나온 것으로 봄", GroupName = "05. 진입 — 공통", Order = 2)]
		public bool T2CountConfirmBars { get; set; }

		// ── 06. 손절 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 구조 손절 봉 수 (SwingBars)", Description = "기본 5. 손절 = 신호 봉 포함 최근 이 봉 수의 저가 − 1틱(매도는 고가 + 1틱). 의심 가격 확인에도 씀(그 봉 제외)", GroupName = "06. 손절", Order = 0)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 추세 손절 ATR 최소 (TrendStopMinAtr)", Description = "기본 1.5. 추세 포지션 손절 거리를 신호 봉 1분 ATR(14) × 이 값 이상으로", GroupName = "06. 손절", Order = 1)]
		public double TrendStopMinAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 추세 손절 ATR 최대 (TrendStopMaxAtr)", Description = "기본 3.0. 추세 포지션 손절 거리를 ATR × 이 값 이하로", GroupName = "06. 손절", Order = 2)]
		public double TrendStopMaxAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 횡보 손절 ATR 최소 (SideStopMinAtr)", Description = "기본 1.0", GroupName = "06. 손절", Order = 3)]
		public double SideStopMinAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 횡보 손절 ATR 최대 (SideStopMaxAtr)", Description = "기본 2.0", GroupName = "06. 손절", Order = 4)]
		public double SideStopMaxAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "[안전] 최소 손절 틱 (StopMinTicks)", Description = "기본 32틱(MNQ 8pt). 손절 거리가 이보다 짧으면 이만큼 벌림. 장전 ATR이 절반이라 손절이 너무 좁아지는 것 방지", GroupName = "06. 손절", Order = 10)]
		public int StopMinTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[안전] 손절 이동 안전 거리 틱 (BreakevenBufferTicks)", Description = "기본 8. 손절을 옮길 때 현재 호가가 옮길 가격에서 이 틱 수 이상 떨어져 있어야 옮김. 가까우면 미뤘다가 봉 마감마다 다시 시도(주문 거부 방지)", GroupName = "06. 손절", Order = 11)]
		public int BreakevenBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[안전] 주문 오류를 전략이 직접 처리 (HandleOrderErrors)", Description = "기본 켬. 손절 변경 거부는 기존 손절 유지 후 재시도, 손절 주문 거부는 즉시 전량 청산. 끄면 NinjaTrader가 포지션을 닫고 전략을 끔", GroupName = "06. 손절", Order = 12)]
		public bool HandleOrderErrors { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Range(0, double.MaxValue)]
		[Display(Name = "[선택] 1차 뒤 본절에 필요한 이익 (BreakevenMinR)", Description = "기본 0.5(2026-10-15 사용자 결정). 1차 익절 이익이 처음 손절 거리 × 이 값 이상이면 손절을 평단으로. 모자라면 아래 ATR 배수 방식. 0 = 항상 평단(그 전 동작)", GroupName = "06. 손절", Order = 5)]
		public double BreakevenMinR { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[선택] 본절 이익이 모자랄 때 손절 = 1차 체결가 ∓ ATR × (BreakevenFallbackAtr)", Description = "기본 1.5. 매수는 1차 체결가 − 이 값 × 1분 ATR, 매도는 +. 지금 손절보다 유리할 때만 옮김. 0 = 손절 유지", GroupName = "06. 손절", Order = 6)]
		public double BreakevenFallbackAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[선택] 이익 보호 본절: 최대 이익이 손절 거리 × (BreakevenAtR)", Description = "기본 0.8(2026-10-15 사용자 결정, v2와 같은 방식). 1차 익절 전이라도 진입 뒤 최대 유리폭이 처음 손절 거리 × 이 값에 닿으면 진입 봉 마감에 손절을 평단으로. 4가지 포지션 모두. 0 = 끔", GroupName = "06. 손절", Order = 7)]
		public double BreakevenAtR { get; set; }

		// ── 07. 청산 — 추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[설계] 1차 익절 밴드 (TrendTp1Band)", Description = "기본 2(매수 +2배, 매도 −2배). 가격이 닿는 즉시 지정가. 체결되면 손절을 평단으로", GroupName = "07. 청산 — 추세", Order = 0)]
		public int TrendTp1Band { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[설계] 2차 익절 밴드 (TrendTp2Band)", Description = "기본 3. 체결되면 트레일링 시작. E1 손익비도 이 밴드까지로 계산", GroupName = "07. 청산 — 추세", Order = 1)]
		public int TrendTp2Band { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[설계] 1차 익절 % (TrendTp1Percent)", Description = "기본 33. 진입 계약 × % 내림", GroupName = "07. 청산 — 추세", Order = 2)]
		public int TrendTp1Percent { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[설계] 2차 익절 % (TrendTp2Percent)", Description = "기본 33. 나머지가 마지막 조각", GroupName = "07. 청산 — 추세", Order = 3)]
		public int TrendTp2Percent { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 2차 뒤 트레일링 ATR 배수 (TrailAtr)", Description = "기본 2.5. 2차 익절 뒤 진입 봉 마감마다 손절을 진입 뒤 최고가 − 1분 ATR × 이 값으로(매도는 최저가 + …). 0 = 안 씀. 2 / 2.5 / 3 비교용", GroupName = "07. 청산 — 추세", Order = 4)]
		public double TrailAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2)]
		[Display(Name = "[설계] 마지막 조각 기준선: 0 끔 / 1 중기 / 2 단기 (TrendLastExitMa)", Description = "기본 1. 2차 익절 뒤에만, 추세 봉 종가가 이 이동평균 반대편으로 마감하면(매수: 종가 < 선) 나머지 전량. 0이면 트레일링 손절로만 나감", GroupName = "07. 청산 — 추세", Order = 5)]
		public int TrendLastExitMa { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 추세 종료 시 전량 청산 (TrendExitOnEnd)", Description = "기본 켬. 추세가 횡보로 바뀌면(S ≤ 종료 기준) 전량. 보유 중 추세가 포지션 방향이었던 적이 있을 때만(E3로 횡보 중 들어간 경우). 반대 추세 시작은 이 설정과 상관없이 항상 전량", GroupName = "07. 청산 — 추세", Order = 10)]
		public bool TrendExitOnEnd { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 추세 유지 중에도 1분 T2 반대 신호로 청산 (TrendExitOnEntryT2)", Description = "기본 끔. 끄면 진입 봉의 MACD 반대 전환 AND 반대 크로스는 추세가 포지션 방향인 동안 무시하고, 아닐 때만 전량 청산 조건으로 씀", GroupName = "07. 청산 — 추세", Order = 11)]
		public bool TrendExitOnEntryT2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[선택] 익절 밴드 최소 거리 (TrendTpMinR)", Description = "기본 0.5(2026-10-15 사용자 결정). 진입 때 익절 밴드까지 남은 거리가 예상 손절 거리 × 이 값보다 짧으면 지난 밴드로 봄: 1차면 1차 조각도 2차 밴드로, 2차면 1·2차 조각을 트레일링으로. 0 = 이미 넘은 경우만", GroupName = "07. 청산 — 추세", Order = 6)]
		public double TrendTpMinR { get; set; }

		// ── 08. 청산 — 횡보 ──
		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 횡보 1단계 ATR 배수 (SideTp1Atr)", Description = "기본 2. 1단계 익절 = 진입가 + 2 × 신호 봉 1분 ATR(매도는 −). 채널 밴드가 아님. 진입 때 고정", GroupName = "08. 청산 — 횡보", Order = 0)]
		public double SideTp1Atr { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[설계] 횡보 1단계 % (SideTp1Percent)", Description = "기본 33. 체결되면 손절을 평단으로", GroupName = "08. 청산 — 횡보", Order = 1)]
		public int SideTp1Percent { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "[설계] 횡보 전량 청산 밴드 (SideRestBand)", Description = "기본 3. 진입 봉 종가가 +3배(매도 −3배)에 닿으면 나머지 전량", GroupName = "08. 청산 — 횡보", Order = 2)]
		public int SideRestBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] MACD·크로스 청산은 1단계 뒤에만 (SideOscExitAfterTp1)", Description = "기본 켬. MACD 반대 전환·반대 크로스 청산은 1단계 체결 뒤에만 봄. 그 전에는 손절·±3배·반대 추세로만 나감", GroupName = "08. 청산 — 횡보", Order = 10)]
		public bool SideOscExitAfterTp1 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 반대 추세 시작 시 전량 청산 (SideExitOnOppTrend)", Description = "기본 켬. 횡보 매수 보유 중 하락추세가 시작되면 전량(매도는 거울상). 같은 방향 추세는 그대로 들고 감", GroupName = "08. 청산 — 횡보", Order = 11)]
		public bool SideExitOnOppTrend { get; set; }

		// ── 09. 시간대 ──
		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] 개장 시각 (OpenTime)", Description = "HHmmss, 기본 223000(미국 정규장 개장, KST 서머타임). 장전·장후와 장전 박스의 기준. 골드는 종목 차트마다 따로 정함", GroupName = "09. 시간대", Order = 0)]
		public int OpenTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 120)]
		[Display(Name = "[설계] 지표 발표 진입 금지 분 (NewsBlockMinutes)", Description = "기본 5. 대회 시작부터 이 분 동안 신규 진입 안 함(08:30 ET 지표 발표). 대회 날 일정을 보고 정함", GroupName = "09. 시간대", Order = 1)]
		public int NewsBlockMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0, 60)]
		[Display(Name = "[설계] 개장 전 진입 금지 분 (OpenBlockBefore)", Description = "기본 5. 개장 5분 전부터 신규 진입 안 함", GroupName = "09. 시간대", Order = 2)]
		public int OpenBlockBefore { get; set; }

		[NinjaScriptProperty]
		[Range(0, 60)]
		[Display(Name = "[설계] 개장 후 진입 금지 분 (OpenBlockAfter)", Description = "기본 3. 개장 뒤 3분까지 신규 진입 안 함. 그 뒤가 장후", GroupName = "09. 시간대", Order = 3)]
		public int OpenBlockAfter { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "[선택] 장전 수량 % (PreOpenQtyPercent)", Description = "기본 50. 장전 진입은 진입 계약 × 이 %", GroupName = "09. 시간대", Order = 10)]
		public int PreOpenQtyPercent { get; set; }

		[NinjaScriptProperty]
		[Range(0, 4)]
		[Display(Name = "[선택] 장전 추세 진입 최소 S (PreOpenTrendMinSlow)", Description = "기본 2(2026-10-15 사용자 결정, 그 전 3). 장전 E1·E2는 S ≥ 이 값(매도 ≤ −값)일 때만", GroupName = "09. 시간대", Order = 11)]
		public int PreOpenTrendMinSlow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[선택] 장전 횡보 진입 허용 (PreOpenAllowSide)", Description = "기본 켬. 끄면 장전에는 E4를 하지 않음", GroupName = "09. 시간대", Order = 12)]
		public bool PreOpenAllowSide { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] 개장 전 본절 보호 (ProtectBeforeOpen)", Description = "기본 끔. 켜면 개장 1분 전 봉(22:29) 마감에 보유 중이고 이익이 1분 ATR × 아래 배수 이상이면 손절을 평단으로", GroupName = "09. 시간대", Order = 13)]
		public bool ProtectBeforeOpen { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[선택] 개장 전 보호 ATR 배수 (ProtectAtr)", Description = "기본 1", GroupName = "09. 시간대", Order = 14)]
		public double ProtectAtr { get; set; }

		[NinjaScriptProperty]
		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "[선택] 최소 진입 보장 (MinEntryFallback)", Description = "기본 켬. 아래 시각 이후 이 종목 진입 횟수가 최소 진입 목표보다 적으면, 장전 제한·횡보 방향 필터 없이 다음 유효 신호를 수량 × 아래 %로 받음", GroupName = "09. 시간대", Order = 15)]
		public bool MinEntryFallback { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[선택] 최소 진입 보장 시각 (MinEntryFallbackTime)", Description = "HHmmss, 기본 230000", GroupName = "09. 시간대", Order = 16)]
		public int MinEntryFallbackTime { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "[선택] 최소 진입 보장 수량 % (MinEntryQtyPercent)", Description = "기본 30", GroupName = "09. 시간대", Order = 17)]
		public int MinEntryQtyPercent { get; set; }

		// ── 10. 수량·대회 시간 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 진입 계약 수 (EntryQuantity)", Description = "기본 30. 단계 수량은 × % 내림, 마지막 조각이 나머지", GroupName = "10. 수량·대회 시간", Order = 0)]
		public int EntryQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 대회 한도 계약 수 (MaxQuantity)", Description = "기본 40. 진입 계약 수가 이보다 크면 시작할 때 경고만", GroupName = "10. 수량·대회 시간", Order = 1)]
		public int MaxQuantity { get; set; }

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
		[Display(Name = "[설계] 진입 마감 시각 (EntryEndTime)", Description = "HHmmss, 기본 231500 = 23:15. 이 시각 뒤의 신호로는 진입하지 않음", GroupName = "10. 수량·대회 시간", Order = 4)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "[설계] 전량 청산 시각 (FlattenTime)", Description = "HHmmss, 기본 232700 = 23:27. 이 봉이 마감되면 남은 수량을 전량 시장가", GroupName = "10. 수량·대회 시간", Order = 5)]
		public int FlattenTime { get; set; }

		// ── 11. 지표 계산값 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 밴드 중심선 EMA 기간 (BandMidPeriod)", Description = "기본 26", GroupName = "11. 지표 계산값", Order = 0)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 밴드·손절 ATR 기간 (BandAtrPeriod)", Description = "기본 14. 밴드, 손절 거리, 횡보 1단계, 트레일링에 같이 씀", GroupName = "11. 지표 계산값", Order = 1)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 1배 밴드 ATR 배수 (BandMult1)", Description = "기본 1", GroupName = "11. 지표 계산값", Order = 2)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 2배 밴드 ATR 배수 (BandMult2)", Description = "기본 2", GroupName = "11. 지표 계산값", Order = 3)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "[설계] 3배 밴드 ATR 배수 (BandMult3)", Description = "기본 3", GroupName = "11. 지표 계산값", Order = 4)]
		public double BandMult3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] MACD 단기 (MacdFast)", Description = "기본 12. 진입 봉과 추세 봉에 같이 씀", GroupName = "11. 지표 계산값", Order = 5)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] MACD 장기 (MacdSlow)", Description = "기본 26", GroupName = "11. 지표 계산값", Order = 6)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] MACD 시그널 (MacdSmooth)", Description = "기본 9", GroupName = "11. 지표 계산값", Order = 7)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 스토캐스틱 %K 길이 (StochPeriodK)", Description = "기본 10", GroupName = "11. 지표 계산값", Order = 8)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 스토캐스틱 %K 스무딩 (StochSmooth)", Description = "기본 5", GroupName = "11. 지표 계산값", Order = 9)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 스토캐스틱 %D 스무딩 (StochPeriodD)", Description = "기본 5", GroupName = "11. 지표 계산값", Order = 10)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[설계] 골든크로스 과매도 기준 (CrossLow)", Description = "기본 20. 최근 크로스 필터 봉 수 안에 %K가 이 값 아래였어야 골든크로스", GroupName = "11. 지표 계산값", Order = 11)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "[설계] 데드크로스 과매수 기준 (CrossHigh)", Description = "기본 80", GroupName = "11. 지표 계산값", Order = 12)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] 크로스 필터 봉 수 (CrossFilterBars)", Description = "기본 5", GroupName = "11. 지표 계산값", Order = 13)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "[설계] RSI 기간 (RsiPeriod)", Description = "기본 14. 진입 봉과 추세 봉(S4)에 같이 씀", GroupName = "11. 지표 계산값", Order = 14)]
		public int RsiPeriod { get; set; }

		// ── 12. 화면·기록 ──
		[NinjaScriptProperty]
		[Display(Name = "[표시] 차트에 상태 표시 (ShowStatus)", Description = "추세 배경색, 밴드, 체결 라벨·화살표, 손절선·평단선, 장전 박스선, 왼쪽 위 상태 상자", GroupName = "12. 화면·기록", Order = 0)]
		public bool ShowStatus { get; set; }

		[NinjaScriptProperty]
		[Range(50, 300)]
		[Display(Name = "[표시] 상태 상자 크기 % (StatusPanelScale)", Description = "기본 130. 100이면 본문 12 / 추세 이름 15 / 폭 560px", GroupName = "12. 화면·기록", Order = 1)]
		public int StatusPanelScale { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 로그를 파일로 저장 (LogToFile)", Description = "실시간·Playback 로그를 내 문서\\NinjaTrader 8\\TQ_log_v3_종목.txt에 저장. 진입 봉·추세 봉 마감마다, 사건마다 한 줄", GroupName = "12. 화면·기록", Order = 2)]
		public bool LogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 학습 데이터를 파일로 저장 (MlLogToFile)", Description = "거래 1건마다 TQ_ml_v3_종목.csv에 한 줄(신호 봉 특징값 + 결과). 백테스트에서도 저장. 최적화 때는 끔", GroupName = "12. 화면·기록", Order = 3)]
		public bool MlLogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[표시] 주문 테스트용 가짜 진입 (UseTestEntry)", Description = "실제 신호를 무시하고 진입 시간대에 미보유면 추세 매수로 들어가 2봉째 1차, 3봉째 2차, 4봉째 전량 청산. 주문 흐름 확인용. 평소와 대회 때는 반드시 끔", GroupName = "12. 화면·기록", Order = 4)]
		public bool UseTestEntry { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "[설계] 익절을 봉 진행 중에 바로 (IntrabarTakeProfit)", Description = "기본 켬. 밴드·ATR 익절은 가격이 닿는 즉시 지정가로 체결(가격은 방금 마감된 봉의 값으로 봉마다 옮김). 끄면 봉 마감 종가로 판단해 시장가", GroupName = "12. 화면·기록", Order = 5)]
		public bool IntrabarTakeProfit { get; set; }

		// ── 00. 설정 세트 ──
		// "버튼": 설정창에는 버튼이 없어 체크 상자로 만든다. 체크하면 한 번 실행하고 스스로 풀린다.
		// 값을 저장하지 않으므로 [NinjaScriptProperty]를 붙이지 않고 XML에도 쓰지 않는다
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
			if (RegimeS2Mode != 2)		locked.Add("RegimeSlopeBars");
			if (!RegimeUseRsi)			locked.Add("RegimeRsiBand");
			if (!SuspectNeedPrice)		locked.Add("SuspectConfirmBars");
			if (!UseEntryTrendStart && !UseEntryPullback)	locked.Add("EntryMinRR");
			if (!UseEntryTrendStart)
				foreach (string name in new[] { "E1Reaccel", "E1AtSessionStart" })
					locked.Add(name);
			if (!BlockEntryWhileSuspect)	locked.Add("SuspectBlockNeedPrice");
			if (BreakevenMinR <= 0)			locked.Add("BreakevenFallbackAtr");
			if (!UseEntryPullback)
				foreach (string name in new[] { "TrendPullbackBand", "PullbackMinSlow" })
					locked.Add(name);
			if (!UseEntryOrb)
				foreach (string name in new[] { "OrbEndTime", "OrbMinSlow", "OrbMinFast", "OrbMaxEntries" })
					locked.Add(name);
			if (!UseEntrySide)
				foreach (string name in new[] { "SideBand", "SideT2Entry", "SideBlockScore", "BlockSideAfterStop", "PreOpenAllowSide" })
					locked.Add(name);
			if (!ProtectBeforeOpen)		locked.Add("ProtectAtr");
			if (!MinEntryFallback)
				foreach (string name in new[] { "MinEntryFallbackTime", "MinEntryQtyPercent" })
					locked.Add(name);
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
		public override void SetValue(object component, object value)	{ inner.SetValue(component, value); }
		public override bool ShouldSerializeValue(object component)		{ return inner.ShouldSerializeValue(component); }
	}
}
