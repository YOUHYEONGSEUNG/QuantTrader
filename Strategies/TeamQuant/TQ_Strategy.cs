#region Using declarations
using System;
using System.Collections.Generic;	// 보유 시그널·수량 추적 (task3·4)
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;					// Brush, Brushes (화면 표시)
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;						// DashStyleHelper
using NinjaTrader.Gui.Tools;				// SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;	// Draw.TextFixed, TextPosition
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
		private enum ActiveStrategy { None, UpLong, UpShort, DnLong, DnShort, SideLong, SideShort }

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
		private string			logPath;						// LogToFile용 로그 파일 경로
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
		private ATR				atr;						// 실험 옵션 MinStopAtr용 3분봉 ATR
		private double			stopAtr			= double.NaN;	// 신호 봉의 ATR (손절 거리 하한 계산용)
		private Brush			upBackBrush;				// 화면 표시용: 상승추세 배경색
		private Brush			downBackBrush;					// 화면 표시용: 하락추세 배경색
		private SimpleFont		statusFont;						// 화면 표시용: 왼쪽 위 상자 글꼴
		private SimpleFont		markFont;						// 화면 표시용: 체결 라벨 글꼴
		private string			markTag			= "";			// 화면 표시용: 마지막 체결 라벨 태그 (같은 봉 수량 합산)
		private int				markQty;						// 화면 표시용: 그 라벨의 합산 수량
		private bool			breakevenDone;					// 본절 이동 완료
		private readonly List<string>			liveSignals	= new List<string>();		// 아직 보유 중인 진입 시그널명
		private readonly Dictionary<string, int>	chunkQty	= new Dictionary<string, int>();	// 진입 시그널별 수량

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

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 (docs/spec.md)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				EntriesPerDirection	= 1;
				EntryHandling		= EntryHandling.UniqueEntries;	// 시그널명별 분할 진입

				// 기본값 = 운용 기본값 (spec 8장의 "운용 기본값" 열, 2026-10-09 기준).
				// 뒤에 "명세 N"이라고 적힌 줄은 원래 명세 값과 다르게 둔 것이다. 팀 확정 전까지의 임시 값이다

				// 지표 계산값 (명세와 같음)
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
				RsiHigh				= 70;
				RsiLow				= 20;
				DivFrom				= 5;
				DivTo				= 30;

				// 진입 — 추세별 밴드 배수와 반전 신호 대기 봉 수
				UpLongBand			= 0;		// 명세 1. 0 = 중심선
				UpShortBand			= 3;
				DnShortBand			= 0;		// 명세 2. 0 = 중심선
				DnShortNeedBoth		= false;
				DnLongBand			= 3;
				DnLongNeedDiv		= false;
				SideBand			= 2;
				T1Window			= 5;		// 명세 3
				T1WindowUp			= 0;		// 0 = T1Window 사용
				T1WindowDn			= 0;
				T1WindowSide		= 0;

				// 진입 — 공통
				TrendT2Entry		= true;		// 명세에 없음 (끔과 같음)
				T2CountConfirmBars	= true;		// 명세에 없음 (끔과 같음)
				UseCounterTrend		= false;	// 명세 켬
				CounterQtyPercent	= 100;
				BlockReentryAfterStop	= true;

				// 추세 판단 (10분봉)
				RegimeFast			= 6;		// 명세 20
				RegimeMid			= 18;		// 명세 60
				RegimeSlow			= 36;		// 명세 120
				UseRule12			= true;
				Rule12Bars			= 3;		// 명세 5
				Rule12PriceFilter	= true;		// 명세에 없음 (끔과 같음)
				FastTrendBars		= 2;		// 명세에 없음 (0과 같음)
				FastTrendSlope		= false;

				// 손절·본절
				StopPercent			= 0.2;
				UseSwingStop		= true;
				SwingBars			= 5;
				MinStopAtr			= 1;		// 명세에 없음 (0과 같음)
				BreakevenAtR		= 1;

				// 청산
				SideRestBand		= 3;
				SideRestNarrowTime	= 223000;
				MomentumSmaPeriod	= 20;

				// 수량·대회 시간 (spec 6·7장)
				EntryQuantity		= 30;
				MaxQuantity			= 40;
				MinEntries			= 2;		// 종목당 최소 진입 (나스닥 2, 골드 2)
				ContestStartTime	= 223000;	// 22:30:00 KST
				EntryEndTime		= 1500;		// 00:15:00 KST
				FlattenTime			= 2700;		// 00:27:00 KST 봉 마감

				// 화면·기록·테스트
				ShowStatus			= true;
				LogToFile			= true;
				TradeLogToFile		= true;
				UseTestEntry		= false;
			}
			else if (State == State.Configure)
			{
				// 10분봉 → BarsArray[1]. 인자는 하드코딩한다
				AddDataSeries(BarsPeriodType.Minute, 10);
			}
			else if (State == State.DataLoaded)
			{
				// 마지막 인자 false = ShowDebugVisuals. 밴드 설정값은 두 지표에 같은 값을 넘긴다 (docs/interface.md)
				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				regime		= TQ_Regime(BarsArray[1], RegimeFast, RegimeMid, RegimeSlow, UseRule12, false,
								Rule12Bars, Rule12PriceFilter, FastTrendBars, FastTrendSlope);
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, RsiHigh, RsiLow,
								DivFrom, DivTo, SwingBars,
								BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3,
								MacdFast, MacdSlow, MacdSmooth,
								StochPeriodK, StochSmooth, StochPeriodD,
								MomentumSmaPeriod, MacdConfirmBars, MacdMinChangeAtr, false,
								UpLongBand, UpShortBand, DnLongBand, DnShortBand, SideBand,
								T1WindowUp, T1WindowDn, T1WindowSide, T2CountConfirmBars);

				// 로그 파일: 내 문서\NinjaTrader 8\TQ_log_종목.txt (LogToFile)
				logPath		= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_log_" + Instrument.MasterInstrument.Name + ".txt");
				tradePath	= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
								"NinjaTrader 8", "TQ_trades_" + Instrument.MasterInstrument.Name + ".csv");

				atr			= ATR(BandAtrPeriod);

				// 화면 표시: 밴드를 차트에 같이 올리고, 배경색 브러시와 글꼴을 한 번만 만든다 (직접 만든 브러시는 Freeze)
				if (ShowStatus)
					AddChartIndicator(channels);
				upBackBrush		= new SolidColorBrush(Color.FromArgb(30, 0, 200, 0));
				downBackBrush	= new SolidColorBrush(Color.FromArgb(30, 230, 0, 0));
				upBackBrush.Freeze();
				downBackBrush.Freeze();
				statusFont		= new SimpleFont("Malgun Gothic", 12);
				markFont		= new SimpleFont("Malgun Gothic", 11);

				// spec 6장: 분할 진입 합계는 EntryQuantity이고 불타기가 없으므로 보유 수량은 EntryQuantity를 넘지 않는다.
				// MaxQuantity(= 대회 한도, NQ 4계약 상당)보다 크게 설정하면 한도를 넘을 수 있어 경고한다.
				if (EntryQuantity > MaxQuantity)
					Out(string.Format("[{0}][TQ_Strategy] 경고: 기본 진입 수량({1}) > 최대 보유 수량({2}, 대회 한도). 설정 확인 필요 (spec 6장)",
						Instrument.FullName, EntryQuantity, MaxQuantity));
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
			return string.Format("[{0}] {1}분봉 | 밴드 상승 매수/매도={2}/{3} 하락 매도/매수={4}/{5} 횡보={6} | 대기 공통/상승/하락/횡보={7}/{8}/{9}/{10} | 역추세={11} 재진입금지={12} 매도둘다={13} | 추세 MA={14}/{15}/{16} 고저규칙={17} 구간={18} 20선조건={19} 빠른전환={20} 기울기={28} | 손절%={21} 전저점={22} 최소ATR={23} 본절R={24} | 횡보청산밴드={25} MACD확인={26} 추세T2진입={27} | 역추세매수 다이버전스={29} 역추세수량%={30} T2앞봉인정={31}",
				Instrument.FullName, BarsPeriod.Value, UpLongBand, UpShortBand, DnShortBand, DnLongBand, SideBand,
				T1Window, T1WindowUp, T1WindowDn, T1WindowSide, UseCounterTrend, BlockReentryAfterStop, DnShortNeedBoth,
				RegimeFast, RegimeMid, RegimeSlow, UseRule12, Rule12Bars, Rule12PriceFilter, FastTrendBars,
				StopPercent, UseSwingStop, MinStopAtr, BreakevenAtR, SideRestBand, MacdConfirmBars, TrendT2Entry,
				FastTrendSlope, DnLongNeedDiv, CounterQtyPercent, T2CountConfirmBars);
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

		private void TradeEnd()
		{
			if (!trOpen)
				return;
			trOpen = false;
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
					entryFillQty.ToString(inv),
					avg.ToString("0.##", inv),
					double.IsNaN(trStop0) ? "" : trStop0.ToString("0.##", inv),
					double.IsNaN(trStop0) ? "" : Math.Abs(avg - trStop0).ToString("0.##", inv),
					(trPnlPts / entryFillQty).ToString("0.##", inv),
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

		protected override void OnBarUpdate()
		{
			// 데이터 부족 가드: 10분봉 SMA120이 채워지기 전에는 아무것도 하지 않는다
			if (CurrentBars[0] < DivTo || CurrentBars[1] < RegimeSlow)
				return;

			if (BarsInProgress == 1)
			{
				Out(string.Format("[{0}][{1}][TQ_Strategy] 10분봉 마감 레짐={2} 종가={3} 20선={4:F2}",
					Time[0], Instrument.FullName, regime.Regime[0], Closes[1][0], regime.Sma20[0]));

				// spec 5.2 1차 익절: 상승추세 숏 보유 중 10분봉 저가 <= 10분봉 SMA20이면 즉시 익절.
				// 다음 3분봉 마감을 기다리지 않고 10분봉 마감 즉시 처리하며, 주문은 BIP 0 대상으로 낸다.
				if (active == ActiveStrategy.UpShort && !tp1Done
					&& Position.MarketPosition == MarketPosition.Short
					&& !double.IsNaN(regime.Sma20[0]) && Lows[1][0] <= regime.Sma20[0])
				{
					MoveBreakeven();	// 체결 콜백이 주문 호출 도중에 올 수 있어 익절 주문보다 먼저 예약한다
					ExitChunk("UpS1", "5.2 1차 익절(10분봉 저가<=SMA20)");
					tp1Done = true;
				}
				return;
			}

			if (BarsInProgress != 0)
				return;

			Out(string.Format("[{0}][{1}][TQ_Strategy] 레짐={2} UpLong={3} UpShort={4} DnLong={5} DnShort={6} SideLong={7} SideShort={8} 대기(같은 순서)={9}/{10}/{11}/{12}/{13}/{14}",
				Time[0], Instrument.FullName, regime.Regime[0],
				signals.EntryUpLong[0], signals.EntryUpShort[0],
				signals.EntryDnLong[0], signals.EntryDnShort[0],
				signals.EntrySideLong[0], signals.EntrySideShort[0],
				signals.WaitUpLong, signals.WaitUpShort, signals.WaitDnLong, DnShortWait(), signals.WaitSideLong, signals.WaitSideShort));

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
			wasInEntryWindow = inEntryWindow;

			// spec 7장: 종목별 진입 횟수와 최소 진입 목표를 화면에 표시. 분할 진입 여러 건은 OnPositionUpdate에서 신호 1회로 센다.
			// 최소 진입 미달이면 경고를 덧붙인다 (미달 대책 자체는 spec 9장 미정 → 표시만 한다)
			string entryStatus = entryCount >= MinEntries ? "" : string.Format(" (최소 {0} 미달)", MinEntries);
			Draw.TextFixed(this, "TQ_EntryCount",
				string.Format("{0} 진입: {1}/{2}회{3}", Instrument.MasterInstrument.Name, entryCount, MinEntries, entryStatus),
				TextPosition.TopRight);

			// spec 7장: 종료 청산 — FlattenTime(00:27) 봉이 마감되면 남은 포지션을 전량 시장가로 청산한다
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
				// 미보유: 진입·익절·손절 상태 전체 리셋 (spec 1장 3, strategy.md)
				ResetTradeState();

				// spec 1장 2 + 7장: 미보유 + 진입 허용 시간대(신호 봉 22:30~00:15)일 때만 신규 진입
				if (InEntryWindow())
				{
					if (UseTestEntry)	RouteTestEntry();	// task5: 주문 흐름 검증용 임시 진입
					else				RouteEntry();
				}
			}
			else
			{
				// 보유 중: 새 진입 신호는 무시 (spec 1장 2).
				// 손절 재계산·갭 청산·본절 이동은 체결 시점에 OnExecutionUpdate에서 처리한다 (spec 4장)
				if (gapFlattened)
				{
					// 갭 청산 주문 뒤에도 포지션이 남아 있으면 다시 전량 청산한다
					if (Position.MarketPosition == MarketPosition.Long)			ExitLong();
					else if (Position.MarketPosition == MarketPosition.Short)	ExitShort();
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

					// spec 5장: 진입 시점 레짐의 익절·청산 상태 머신
					ManageExits();
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

		#region 진입 라우팅 (spec 3장·5장·6장)
		// 진입 시점 레짐으로 세부 전략을 고르고, 그 방향 신호가 있으면 분할 진입한다 (spec 3장).
		// 같은 봉에 롱·숏 신호가 동시에 나는 극단적 경우는 각 레짐의 주력 방향을 우선한다 (spec 미정, 사람 확인 필요).
		private void RouteEntry()
		{
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
			BeginTrade(true, 1, ActiveStrategy.UpLong);
			int q1 = Split(1, 5), q2 = Split(2, 5), q3 = tradeQty - q1 - q2;
			SubmitChunk("UpL1", q1);
			SubmitChunk("UpL2", q2);
			SubmitChunk("UpL3", q3);
			LogEntry("상승추세 롱(5.1)", q1, q2, q3);
		}

		// spec 5.2 상승추세 숏(역추세): 1/2 + 나머지
		private void EnterUpShort()
		{
			BeginTrade(false, 1, ActiveStrategy.UpShort);
			int q1 = Split(1, 2), q2 = tradeQty - q1;
			SubmitChunk("UpS1", q1);
			SubmitChunk("UpS2", q2);
			LogEntry("상승추세 숏(5.2)", q1, q2, 0);
		}

		// spec 5.3 하락추세 롱(역추세): 1/3 + 나머지
		private void EnterDnLong()
		{
			BeginTrade(true, -1, ActiveStrategy.DnLong);
			int q1 = Split(1, 3), q2 = tradeQty - q1;
			SubmitChunk("DnL1", q1);
			SubmitChunk("DnL2", q2);
			LogEntry("하락추세 롱(5.3)", q1, q2, 0);
		}

		// spec 5.4 하락추세 숏(주력): 1/3 + 1/3 + 나머지
		private void EnterDnShort()
		{
			BeginTrade(false, -1, ActiveStrategy.DnShort);
			int q1 = Split(1, 3), q2 = Split(1, 3), q3 = tradeQty - q1 - q2;
			SubmitChunk("DnS1", q1);
			SubmitChunk("DnS2", q2);
			SubmitChunk("DnS3", q3);
			LogEntry("하락추세 숏(5.4)", q1, q2, q3);
		}

		// spec 5.5 횡보 롱: 1/2 + 나머지
		private void EnterSideLong()
		{
			BeginTrade(true, 0, ActiveStrategy.SideLong);
			int q1 = Split(1, 2), q2 = tradeQty - q1;
			SubmitChunk("SideL1", q1);
			SubmitChunk("SideL2", q2);
			LogEntry("횡보 롱(5.5)", q1, q2, 0);
		}

		// spec 5.6 횡보 숏: 1/2 + 나머지
		private void EnterSideShort()
		{
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
			TradeBegin(act);
			stopAtr		= atr[0];
			// 전고점·전저점은 신호 봉 기준 (spec 4장). 신호 봉 = 현재 마감된 봉([0])
			stopSwing	= isLong ? signals.SwingLow5[0] : signals.SwingHigh5[0];
			initialStop	= CalcStop(Close[0]);	// 신호 봉 종가를 진입가로 보고 계산 (spec 4장)
			stopPrice	= initialStop;
		}

		// spec 4·6장: 시그널명별 분할 진입. 진입 주문 제출 전에 손절을 먼저 걸어 체결 즉시 손절이 서도록 한다
		private void SubmitChunk(string sig, int qty)
		{
			if (qty <= 0)
				return;
			chunkQty[sig] = qty;
			liveSignals.Add(sig);
			// SetStopLoss는 진입 전에 호출해야 초기 손절가가 보장된다 (NT8 공식 문서)
			// 걸어두는 가격은 신호 봉 종가에서 최소 1틱 띄운다. 체결되면 OnExecutionUpdate에서 체결가 기준으로 다시 건다
			SetStopLoss(sig, CalculationMode.Price, OrderSafeStop(stopPrice, Close[0]), false);

			if (isLongPos)	EnterLong(qty, sig);
			else			EnterShort(qty, sig);
		}

		// spec 6장: 진입 수량 × (num/den)을 내림. 양수 정수 나눗셈이 곧 내림이다
		private int Split(int num, int den)
		{
			return (tradeQty * num) / den;
		}

		private void LogEntry(string name, int q1, int q2, int q3)
		{
			Out(string.Format("[{0}][{1}][TQ_Strategy] 진입 {2} 수량={3}/{4}/{5} (레짐={6}) 손절={7}",
				Time[0], Instrument.FullName, name, q1, q2, q3, entryRegime, stopPrice));
		}
		#endregion

		#region 손절·본절 (spec 4장 — task3)
		// spec 4장 손절가: 전저점(롱)/전고점(숏)과 진입가 ±StopPercent% 중 진입가에 가까운 쪽.
		// 롱은 둘 중 높은 값, 숏은 둘 중 낮은 값. 전고점·전저점이 없으면(NaN) %만 쓴다(방어).
		private double CalcStop(double entryPrice)
		{
			double pct = isLongPos
				? entryPrice * (1 - StopPercent / 100.0)
				: entryPrice * (1 + StopPercent / 100.0);
			if (double.IsNaN(stopSwing))	// 지표 미완성·데이터 부족 방어
				return FloorStop(entryPrice, pct);
			if (!UseSwingStop)				// 실험 옵션: 전고점·전저점을 쓰지 않고 %만 쓴다
				return FloorStop(entryPrice, pct);
			return FloorStop(entryPrice, isLongPos ? Math.Max(stopSwing, pct) : Math.Min(stopSwing, pct));
		}

		// 실험 옵션 MinStopAtr: 손절 거리가 신호 봉 ATR × MinStopAtr보다 짧으면 그 거리까지 벌린다. 0이면 그대로 둔다.
		// 신호 봉이 최근 5봉의 저점(롱)·고점(숏) 근처에서 마감하면 손절 거리가 0에 가까워지는 것을 막는다
		private double FloorStop(double entryPrice, double stop)
		{
			if (MinStopAtr > 0 && !double.IsNaN(stopAtr) && stopAtr > 0)
			{
				double minDist = MinStopAtr * stopAtr;
				if (Math.Abs(entryPrice - stop) < minDist)
					stop = isLongPos ? entryPrice - minDist : entryPrice + minDist;
			}
			return Instrument.MasterInstrument.RoundToTickSize(stop);
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

		// 체결 시점 처리 (spec 4장): 진입 체결 → 갭 판정과 손절 재계산, 첫 부분 익절 체결 → 본절 이동
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
			else if (breakevenPending && name.StartsWith("x"))	// 분할 익절 주문 이름은 "x" + 진입 시그널명
				OnFirstTakeProfitFill(price, time);
			else if (name == "Stop loss")						// SetStopLoss가 내는 주문 이름
				OnStopLossFill(price, time);

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
			bool valid = isLongPos ? Close[0] > be : Close[0] < be;
			if (!valid)
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

			// 본절이 이미 이동했으면 체결가 기준으로 되돌리지 않는다
			if (breakevenDone)
				return;

			double avg	= entryFillSum / entryFillQty;
			double s	= CalcStop(avg);
			stopPrice	= s;
			initialRisk	= Math.Abs(avg - s);	// 이익 보호 본절의 기준 거리 (1R)
			SetStopForLive(s);
			trStop0		= s;
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

			stopPrice		= be;
			breakevenDone	= true;
			SetStopForLive(be);
			Out(string.Format("[{0}][{1}][TQ_Strategy] 본절 이동 손절→평균가={2}",
				time, Instrument.FullName, be));
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
			int q = chunkQty[sig];
			liveSignals.Remove(sig);	// 체결 콜백이 주문 호출 도중에 올 수 있어 먼저 뺀다
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
			active = ActiveStrategy.None;
			Out(string.Format("[{0}][{1}][TQ_Strategy] 전량 청산 {2}",
				Time[0], Instrument.FullName, reason));
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
			bool final = signals.Rsi[0] < RsiLow;		// 최종 청산
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
		// 손절값은 다음 진입의 SubmitChunk에서 시그널별로 SetStopLoss를 다시 하므로 직전 값이 남지 않는다.
		private void ResetTradeState()
		{
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
			initialRisk = double.NaN;
			reachedR = false;	// 직전 거래의 이익 보호 본절 판정이 다음 거래로 넘어가지 않게 한다
			liveSignals.Clear();
			chunkQty.Clear();
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

			double rv = regime.Regime[0];
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
			if (longSetup)
				Draw.Diamond(this, "TQ_SetupL_" + CurrentBars[0], false, 0, Low[0] - gap, Brushes.DeepSkyBlue);
			if (shortSetup)
				Draw.Diamond(this, "TQ_SetupS_" + CurrentBars[0], false, 0, High[0] + gap, Brushes.Orange);
		}

		// 왼쪽 위 상자와 손절선·평단선. 실시간·Playback에서만 갱신한다.
		// 같은 태그를 다시 써서 그리기 개체는 글 1개와 가로선 2개로 고정된다
		private void UpdateStatusPanel()
		{
			if (!ShowStatus || State != State.Realtime || CurrentBars[0] < DivTo || CurrentBars[1] < RegimeSlow)
				return;

			System.Text.StringBuilder sb = new System.Text.StringBuilder();

			double rv		= regime.Regime[0];
			double close	= Closes[0][0];

			sb.AppendFormat("{0}  {1:HH:mm}\n", rv > 0 ? "▲ 상승추세" : rv < 0 ? "▼ 하락추세" : "◆ 횡보", Times[0][0]);
			sb.AppendFormat("MACD {0}  %K {1:F0} / %D {2:F0}  RSI {3:F0}\n",
				signals.MacdUp[0] ? "▲" : signals.MacdDown[0] ? "▼" : "−",
				signals.K[0], signals.D[0], signals.Rsi[0]);

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				RemoveDrawObject("TQ_StopLine");
				RemoveDrawObject("TQ_AvgLine");

				if (IsFlattenTime())
					sb.Append("× 대회 시간 밖\n");
				else if (!InEntryWindow())
					sb.Append("× 진입 시간 아님\n");
				else if (active != ActiveStrategy.None)
					sb.Append("● 진입 주문 냄, 체결 대기\n");
				else if (UseTestEntry)
					sb.Append("TEST: 다음 봉에 검증용 진입\n");
				else
				{
					string longBlock	= BlockReentryAfterStop && blockLong ? "손절 뒤 재진입 금지" : null;
					string shortBlock	= BlockReentryAfterStop && blockShort ? "손절 뒤 재진입 금지" : null;
					string counterOff	= UseCounterTrend ? null : "역추세 진입 꺼짐";

					if (rv > 0)
					{
						sb.Append(CandidateLine("매수(주력)", signals.WaitUpLong, BandText(UpLongBand, false) + " 이탈" + (TrendT2Entry ? " (또는 MACD▲ + 골든)" : ""), "MACD▲ / 골든", null, signals.T1BarsUp));
						sb.Append(CandidateLine("매도(역추세)", signals.WaitUpShort, "+" + UpShortBand + "배 터치 + RSI 70 + 다이버전스", "MACD▼ / 데드", counterOff ?? shortBlock, signals.T1BarsUp));
					}
					else if (rv < 0)
					{
						sb.Append(CandidateLine("매도(주력)", DnShortWait(), BandText(DnShortBand, true) + " 돌파" + (TrendT2Entry ? " (또는 MACD▼ + 데드)" : ""),
							DnShortNeedBoth ? "MACD▼ + 데드" : "MACD▼ / 데드 / RSI 70 + 다이버전스", null, signals.T1BarsDn));
						if (DnLongNeedDiv)
							sb.Append(CandidateLine("매수(역추세)", signals.WaitDnLongDiv, BandText(DnLongBand, false) + " 터치 + RSI 과매도 + 다이버전스", "MACD▲ / 골든", counterOff ?? longBlock, signals.T1BarsDn));
						else
							sb.Append(CandidateLine("매수(역추세)", signals.WaitDnLong, BandText(DnLongBand, false) + " 이탈", "RSI 20 아래 / MACD▲ / 골든", counterOff ?? longBlock, signals.T1BarsDn));
					}
					else
					{
						sb.Append(CandidateLine("매수", signals.WaitSideLong, "−" + SideBand + "배 이상 이탈 (또는 MACD▲ + 골든)", "MACD▲ / 골든", longBlock, signals.T1BarsSide));
						sb.Append(CandidateLine("매도", signals.WaitSideShort, "+" + SideBand + "배 이상 돌파 (또는 MACD▼ + 데드)", "MACD▼ / 데드", shortBlock, signals.T1BarsSide));
					}
				}
			}
			else
			{
				bool isLong	= Position.MarketPosition == MarketPosition.Long;
				double avg	= entryFillQty > 0 ? entryFillSum / entryFillQty : Position.AveragePrice;
				double pts	= isLong ? close - avg : avg - close;

				sb.AppendFormat("{0} {1} @ {2:N2}  {3}{4:N2}  ({5})\n", isLong ? "매수" : "매도", Position.Quantity, avg,
					pts >= 0 ? "+" : "", pts, ActiveName());
				if (!double.IsNaN(stopPrice))
				{
					sb.AppendFormat("손절 {0:N2}{1}\n", stopPrice, breakevenDone ? " (본절)" : "");
					Draw.HorizontalLine(this, "TQ_StopLine", false, stopPrice, Brushes.Magenta, DashStyleHelper.Solid, 2);
				}
				Draw.HorizontalLine(this, "TQ_AvgLine", false, avg, Brushes.Gray, DashStyleHelper.Dash, 1);

				AppendExitSteps(sb);
			}

			Draw.TextFixed(this, "TQ_Status", sb.ToString().TrimEnd('\n'), TextPosition.TopLeft,
				Brushes.White, statusFont, Brushes.Transparent, Brushes.Black, 60);
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
					sb.Append("○ RSI 20 아래 → 전량 청산\n");
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
				default:
					sb.Append("● 청산 주문 냄, 체결 대기\n");
					break;
			}
		}

		// 체결을 차트에 라벨로 표시한다. 매수 체결은 가격 아래, 매도 체결은 가격 위.
		// 같은 봉에서 같은 종류로 여러 건 체결되면(분할 진입 3건 등) 수량을 합쳐 라벨 하나로 보여 준다
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
			else if (name.StartsWith("x"))
			{
				kind	= "익절";
				brush	= Brushes.Goldenrod;
			}
			else if (name == "Stop loss")
			{
				kind	= breakevenDone ? "본절" : "손절";
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
		// 신규 진입 허용 시간대: 신호 봉이 ContestStartTime(22:30)~EntryEndTime(00:15). 자정을 넘는다
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

		// 종료 청산 시각: FlattenTime(00:27)부터 다음 대회 시작(ContestStartTime) 전까지.
		// 이 구간에서 포지션이 있으면 청산하고, 첫 청산 뒤에는 미보유라 추가 주문이 나가지 않는다
		private bool IsFlattenTime()
		{
			int t = ToTime(Time[0]);
			return t >= FlattenTime && t < ContestStartTime;
		}
		#endregion

		#region Properties
		// 설정창에는 Display의 Name(한국어 설명 + 괄호 안 코드 이름)이 보인다. 묶음은 기능별이고 앞의 숫자 순서로 나온다.
		// 코드 이름은 docs/backtest-guide.md와 docs/spec.md 8장에서 쓰는 이름이다

		// ── 0. 화면·기록·테스트 ──
		[NinjaScriptProperty]
		[Display(Name = "차트에 상태 표시 (ShowStatus)", Description = "추세 배경색, 밴드, 밴드 조건 마름모, 체결 라벨, 손절선, 상태 상자를 차트에 표시. 매매 판단과 무관", GroupName = "0. 화면·기록·테스트", Order = 0)]
		public bool ShowStatus { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "로그를 파일로 저장 (LogToFile)", Description = "실시간·Playback 로그를 내 문서\\NinjaTrader 8\\TQ_log_종목.txt에 이어서 저장. 백테스트에서는 저장하지 않음", GroupName = "0. 화면·기록·테스트", Order = 1)]
		public bool LogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "거래 요약을 파일로 저장 (TradeLogToFile)", Description = "거래 1건이 끝날 때마다 한 줄을 내 문서\\NinjaTrader 8\\TQ_trades_종목.csv에 저장. 백테스트에서도 저장하므로 여러 날 결과를 거래 단위로 볼 수 있음. 최적화(Optimize)를 돌릴 때는 끔", GroupName = "0. 화면·기록·테스트", Order = 3)]
		public bool TradeLogToFile { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "주문 테스트용 가짜 진입 (UseTestEntry)", Description = "실제 신호를 무시하고 주문 흐름만 확인. 백테스트 때는 반드시 끔", GroupName = "0. 화면·기록·테스트", Order = 2)]
		public bool UseTestEntry { get; set; }

		// ── 1. 진입 — 상승추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "주력 매수: 몇 배 밴드 아래로 이탈하면 (UpLongBand)", Description = "상승추세 매수(5.1)의 밴드 조건. 명세는 1 (−1배 이탈). 0 = 중심선 이탈", GroupName = "1. 진입 — 상승추세", Order = 0)]
		public int UpLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(1, 3)]
		[Display(Name = "역추세 매도: 고가가 몇 배 밴드에 닿으면 (UpShortBand)", Description = "상승추세 매도(5.2)의 밴드 조건. RSI 70 이상 + 하락 다이버전스도 같이 필요. 명세는 3", GroupName = "1. 진입 — 상승추세", Order = 1)]
		public int UpShortBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "반전 신호를 기다리는 봉 수 (T1WindowUp)", Description = "상승추세 진입에서 밴드 조건 뒤 반전 신호를 기다리는 봉 수. 0이면 공통값(T1Window) 사용", GroupName = "1. 진입 — 상승추세", Order = 2)]
		public int T1WindowUp { get; set; }

		// ── 2. 진입 — 하락추세 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "주력 매도: 몇 배 밴드 위로 돌파하면 (DnShortBand)", Description = "하락추세 매도(5.4)의 밴드 조건. 명세는 2 (+2배 돌파). 1로 낮추면 얕은 반등에서도 매도. 0 = 중심선 돌파", GroupName = "2. 진입 — 하락추세", Order = 0)]
		public int DnShortBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "주력 매도: MACD 하락 + 데드크로스 둘 다 필요 (DnShortNeedBoth)", Description = "켜면 반전 신호로 MACD 하락 전환과 데드크로스가 둘 다 있어야 매도. 끄면 명세대로 하나만 있어도 됨", GroupName = "2. 진입 — 하락추세", Order = 1)]
		public bool DnShortNeedBoth { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "역추세 매수: 몇 배 밴드 아래로 이탈하면 (DnLongBand)", Description = "하락추세 매수(5.3)의 밴드 조건. 명세는 3 (−3배 이탈)", GroupName = "2. 진입 — 하락추세", Order = 2)]
		public int DnLongBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "역추세 매수: RSI 과매도 + 상승 다이버전스 필요 (DnLongNeedDiv)", Description = "켜면 역추세 매도(5.2)와 대칭인 조건을 씀: 저가가 위 밴드에 닿고, RSI가 30 이하(100 − 과매수 기준)이고, 상승 다이버전스가 있을 때만 대기 시작. 반전 신호는 MACD 상승 전환 또는 골든크로스. 떨어지는 중에 사는 것을 줄임", GroupName = "2. 진입 — 하락추세", Order = 4)]
		public bool DnLongNeedDiv { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "반전 신호를 기다리는 봉 수 (T1WindowDn)", Description = "하락추세 진입에서 밴드 조건 뒤 반전 신호를 기다리는 봉 수. 0이면 공통값(T1Window) 사용", GroupName = "2. 진입 — 하락추세", Order = 3)]
		public int T1WindowDn { get; set; }

		// ── 3. 진입 — 횡보 ──
		[NinjaScriptProperty]
		[Range(1, 3)]
		[Display(Name = "매수·매도: 몇 배 밴드부터 (SideBand)", Description = "횡보 매수·매도(5.5·5.6)의 밴드 조건. 이 배수와 그 바깥 밴드를 이탈·돌파하면 충족. 명세는 2 (±2배 또는 ±3배)", GroupName = "3. 진입 — 횡보", Order = 0)]
		public int SideBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "반전 신호를 기다리는 봉 수 (T1WindowSide)", Description = "횡보 진입에서 밴드 조건 뒤 반전 신호를 기다리는 봉 수. 0이면 공통값(T1Window) 사용", GroupName = "3. 진입 — 횡보", Order = 1)]
		public int T1WindowSide { get; set; }

		// ── 4. 진입 — 공통 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "반전 신호를 기다리는 봉 수, 공통 (T1Window)", Description = "밴드 조건 뒤 반전 신호를 기다리는 봉 수. 실제로는 여기에 (MACD 확인 봉 수 − 1)을 더한다. 명세는 3 (미정)", GroupName = "4. 진입 — 공통", Order = 0)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "추세 방향 진입: 밴드 조건 없이 MACD 전환 + 크로스로도 (TrendT2Entry)", Description = "켜면 상승추세에서 MACD 상승 전환 + 골든크로스가 같이 나오면 주력 매수, 하락추세에서 MACD 하락 전환 + 데드크로스면 주력 매도. 밴드까지 눌리지(반등하지) 않는 추세에서 진입하기 위함. 명세에는 횡보에만 있는 조건", GroupName = "4. 진입 — 공통", Order = 5)]
		public bool TrendT2Entry { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MACD + 크로스 동시 조건: MACD가 꺾인 봉의 크로스도 인정 (T2CountConfirmBars)", Description = "MACD 전환은 2봉 연속이어야 확인되므로 실제로 꺾인 봉은 확인 봉보다 앞이다. 켜면 전환이 처음 확인된 봉에서, 확인에 걸린 봉 수만큼 앞에서 나온 골든·데드크로스도 같이 나온 것으로 인정한다. 진입과 청산의 'MACD 전환 + 크로스' 조건 모두에 적용", GroupName = "4. 진입 — 공통", Order = 7)]
		public bool T2CountConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "역추세 진입 사용 (UseCounterTrend)", Description = "상승추세 매도(5.2)와 하락추세 매수(5.3)를 할지. 끄면 추세 방향 진입과 횡보 진입만", GroupName = "4. 진입 — 공통", Order = 1)]
		public bool UseCounterTrend { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "역추세 진입 수량 % (CounterQtyPercent)", Description = "역추세 진입(상승추세 매도, 하락추세 매수)은 진입 계약 수의 이 %만 들어감. 100이면 같은 수량. 진입 횟수는 채우면서 위험을 줄이기 위함", GroupName = "4. 진입 — 공통", Order = 6)]
		public int CounterQtyPercent { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "손실 손절 뒤 같은 방향 재진입 금지 (BlockReentryAfterStop)", Description = "역추세·횡보 진입이 손실로 손절되면 그 대회 구간에서는 같은 방향 역추세·횡보 진입을 다시 하지 않음. 주력 진입은 영향 없음", GroupName = "4. 진입 — 공통", Order = 2)]
		public bool BlockReentryAfterStop { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 전환 확인 봉 수 (MacdConfirmBars)", Description = "MACD 히스토그램이 몇 봉 연속 커져야(작아져야) 전환으로 볼지. 1 = 직전 봉 대비, 2 = 2봉 연속", GroupName = "4. 진입 — 공통", Order = 3)]
		public int MacdConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "MACD 전환 최소 변화폭, ATR 배수 (MacdMinChangeAtr)", Description = "전환으로 인정할 최소 변화폭. 0 = 사용 안 함", GroupName = "4. 진입 — 공통", Order = 4)]
		public double MacdMinChangeAtr { get; set; }

		// ── 5. 추세 판단 (10분봉) ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "이동평균 단기 (RegimeFast)", Description = "정배열·역배열을 보는 10분봉 이동평균 중 가장 짧은 것. '20선'으로 부르는 선", GroupName = "5. 추세 판단 (10분봉)", Order = 0)]
		public int RegimeFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "이동평균 중기 (RegimeMid)", Description = "정배열·역배열을 보는 10분봉 이동평균", GroupName = "5. 추세 판단 (10분봉)", Order = 1)]
		public int RegimeMid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "이동평균 장기 (RegimeSlow)", Description = "정배열·역배열을 보는 10분봉 이동평균. 120이면 20시간 평균. 이 봉 수만큼 데이터가 쌓여야 전략이 시작됨", GroupName = "5. 추세 판단 (10분봉)", Order = 2)]
		public int RegimeSlow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "고점·저점 규칙 사용 (UseRule12)", Description = "정배열·역배열이 아닐 때, 최근 구간의 고점·저점이 그 전 구간보다 둘 다 높으면 상승추세, 둘 다 낮으면 하락추세", GroupName = "5. 추세 판단 (10분봉)", Order = 3)]
		public bool UseRule12 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "고점·저점 비교 구간 봉 수 (Rule12Bars)", Description = "최근 N봉과 그 전 N봉을 비교. 명세는 5 (50분 vs 그 전 50분). 줄이면 전환을 빨리 인식", GroupName = "5. 추세 판단 (10분봉)", Order = 4)]
		public int Rule12Bars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "고점·저점 규칙에 20선 위치 조건 추가 (Rule12PriceFilter)", Description = "켜면 종가가 20선 위일 때만 상승, 아래일 때만 하락으로 인정. 반등했는데 하락추세로 남는 것을 막음", GroupName = "5. 추세 판단 (10분봉)", Order = 5)]
		public bool Rule12PriceFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "빠른 추세 전환: 20선 위·아래 연속 봉 수 (FastTrendBars)", Description = "종가가 N봉 연속 20선 위면 정배열을 기다리지 않고 상승추세, 연속 아래면 하락추세. 0 = 사용 안 함", GroupName = "5. 추세 판단 (10분봉)", Order = 6)]
		public int FastTrendBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "빠른 추세 전환에 단기선 기울기 조건 추가 (FastTrendSlope)", Description = "켜면 종가가 단기선 위에 있는 것에 더해 단기선 자체가 N봉 전보다 올라와 있어야 상승추세(하락은 반대). 가격이 선을 잠깐 넘나드는 것을 걸러 추세를 더 확실히 잡음. 전환은 조금 늦어짐", GroupName = "5. 추세 판단 (10분봉)", Order = 7)]
		public bool FastTrendSlope { get; set; }

		// ── 6. 손절·본절 ──
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "손절 % (StopPercent)", Description = "진입가에서 이 %만큼 불리한 가격. 전저점·전고점과 이 값 중 진입가에 가까운 쪽이 손절가", GroupName = "6. 손절·본절", Order = 0)]
		public double StopPercent { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "전저점·전고점 손절 사용 (UseSwingStop)", Description = "끄면 손절 %만 사용", GroupName = "6. 손절·본절", Order = 1)]
		public bool UseSwingStop { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "전저점·전고점을 찾는 봉 수 (SwingBars)", Description = "신호 봉을 포함한 최근 몇 봉의 최저가·최고가를 쓸지. 명세는 5", GroupName = "6. 손절·본절", Order = 2)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "손절 최소 거리, ATR 배수 (MinStopAtr)", Description = "손절이 이보다 가까우면 이 거리까지 벌림. 0 = 사용 안 함. 손절이 진입가 바로 옆에 놓이는 것을 막음", GroupName = "6. 손절·본절", Order = 3)]
		public double MinStopAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "이익 보호 본절: 손절 거리의 몇 배 이익에서 (BreakevenAtR)", Description = "이익이 처음 손절 거리의 이 배수에 닿으면 손절을 평단으로 옮김. 역추세·횡보 진입만. 0 = 사용 안 함", GroupName = "6. 손절·본절", Order = 4)]
		public double BreakevenAtR { get; set; }

		// ── 7. 청산 ──
		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "횡보: 나머지 수량 청산 밴드 (SideRestBand)", Description = "횡보 세트 A에서 고가(저가)가 몇 배 밴드에 닿으면 나머지를 청산할지. 명세는 3, 0 = 중심선", GroupName = "7. 청산", Order = 0)]
		public int SideRestBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "횡보: 위 밴드를 적용하기 시작하는 시각 (SideRestNarrowTime)", Description = "HHmmss, 한국시간. 이 시각 전에는 3배를 씀", GroupName = "7. 청산", Order = 1)]
		public int SideRestNarrowTime { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "상승추세 매수: 강한 모멘텀 청산 이동평균 (MomentumSmaPeriod)", Description = "+3배 돌파 뒤 종가가 이 3분봉 이동평균 아래로 내려가면 나머지 전량 청산. 명세는 20", GroupName = "7. 청산", Order = 2)]
		public int MomentumSmaPeriod { get; set; }

		// ── 8. 수량·대회 시간 ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "진입 계약 수 (EntryQuantity)", Description = "한 번 진입할 때의 마이크로 계약 수. 30이면 1/2·1/3·1/5로 나눠떨어짐", GroupName = "8. 수량·대회 시간", Order = 0)]
		public int EntryQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "대회 한도 계약 수 (MaxQuantity)", Description = "진입 계약 수가 이보다 크면 시작할 때 경고만 찍음. 매매에는 쓰이지 않음", GroupName = "8. 수량·대회 시간", Order = 1)]
		public int MaxQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "진입 시작 시각 (ContestStartTime)", Description = "HHmmss, 한국시간. 223000 = 22:30. 이 시각부터의 신호로 진입", GroupName = "8. 수량·대회 시간", Order = 2)]
		public int ContestStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "진입 마감 시각 (EntryEndTime)", Description = "HHmmss, 한국시간. 1500 = 00:15. 이 시각 뒤의 신호로는 진입하지 않음", GroupName = "8. 수량·대회 시간", Order = 3)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "전량 청산 시각 (FlattenTime)", Description = "HHmmss, 한국시간. 2700 = 00:27. 이 시각의 봉이 마감되면 남은 수량을 전량 청산", GroupName = "8. 수량·대회 시간", Order = 4)]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "최소 진입 목표 횟수 (MinEntries)", Description = "화면의 진입 횟수 표시에만 쓰임", GroupName = "8. 수량·대회 시간", Order = 5)]
		public int MinEntries { get; set; }

		// ── 9. 지표 계산값 (보통 그대로 둠) ──
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "밴드 중심선 EMA 기간 (BandMidPeriod)", Description = "ATR 채널 중심선. 명세는 26", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 0)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "밴드 ATR 기간 (BandAtrPeriod)", Description = "명세는 14", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 1)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "1배 밴드의 ATR 배수 (BandMult1)", Description = "명세는 1", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 2)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "2배 밴드의 ATR 배수 (BandMult2)", Description = "명세는 2", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 3)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "3배 밴드의 ATR 배수 (BandMult3)", Description = "명세는 3", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 4)]
		public double BandMult3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 단기 (MacdFast)", Description = "명세는 12", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 5)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 장기 (MacdSlow)", Description = "명세는 26", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 6)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MACD 시그널 (MacdSmooth)", Description = "명세는 9", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 7)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "스토캐스틱 %K 길이 (StochPeriodK)", Description = "명세는 10", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 8)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "스토캐스틱 %K 스무딩 (StochSmooth)", Description = "명세는 5", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 9)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "스토캐스틱 %D 스무딩 (StochPeriodD)", Description = "명세는 5", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 10)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "스토캐스틱 과매도 기준 (CrossLow)", Description = "골든크로스는 %K가 이 값 아래였던 뒤에만 인정. %K 하방 돌파 기준이기도 함. 명세는 20", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 11)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "스토캐스틱 과매수 기준 (CrossHigh)", Description = "데드크로스는 %K가 이 값 위였던 뒤에만 인정. %K 상향 돌파 기준이기도 함. 명세는 80", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 12)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "크로스 인정 기한 봉 수 (CrossFilterBars)", Description = "위의 '아래(위)였던 적'을 현재 봉 포함 최근 몇 봉에서 찾을지. 명세는 5", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 13)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RSI 기간 (RsiPeriod)", Description = "명세는 14", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 14)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RSI 과매수 기준 (RsiHigh)", Description = "이 값 이상이면 과매수. 명세는 70", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 15)]
		public double RsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RSI 과매도 기준 (RsiLow)", Description = "이 값 미만이면 과매도. 명세는 20", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 16)]
		public double RsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "다이버전스 비교 구간 시작, 몇 봉 전 (DivFrom)", Description = "명세는 5", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 17)]
		public int DivFrom { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "다이버전스 비교 구간 끝, 몇 봉 전 (DivTo)", Description = "명세는 30", GroupName = "9. 지표 계산값 (보통 그대로 둠)", Order = 18)]
		public int DivTo { get; set; }
		#endregion
	}
}
