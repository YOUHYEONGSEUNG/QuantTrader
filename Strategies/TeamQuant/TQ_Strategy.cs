#region Using declarations
using System;
using System.Collections.Generic;	// 보유 시그널·수량 추적 (task3·4)
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
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
		private bool			breakevenDone;					// 본절 이동 완료
		private readonly List<string>			liveSignals	= new List<string>();		// 아직 보유 중인 진입 시그널명
		private readonly Dictionary<string, int>	chunkQty	= new Dictionary<string, int>();	// 진입 시그널별 수량

		// spec 5장 익절 상태 머신 (task4)
		private bool			tp1Done;						// 1차 익절 완료
		private bool			tp2Done;						// 5.4 2차 익절 완료
		private bool			strongMomentum;					// 5.1 강한 모멘텀 전환
		private SideSet			sideSet		= SideSet.None;		// 5.5·5.6 먼저 충족된 세트
		private enum SideSet { None, A, B }

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 (docs/spec.md)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				EntriesPerDirection	= 1;
				EntryHandling		= EntryHandling.UniqueEntries;	// 시그널명별 분할 진입

				// spec 8장 기본값
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
				SwingBars			= 5;
				StopPercent			= 0.2;
				T1Window			= 3;
				RegimeFast			= 20;
				RegimeMid			= 60;
				RegimeSlow			= 120;
				MomentumSmaPeriod	= 20;
				UseRule12			= true;		// spec 3장 1.2·2.2
				EntryQuantity		= 30;
				MaxQuantity			= 40;
				ContestStartTime	= 223000;	// 22:30:00 KST
				EntryEndTime		= 1500;		// 00:15:00 KST
				FlattenTime			= 2700;		// 00:27:00 KST 봉 마감
				BlockReentryAfterStop	= true;
				BreakevenAtR		= 1;
				UseCounterTrend		= true;
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
				regime		= TQ_Regime(BarsArray[1], RegimeFast, RegimeMid, RegimeSlow, UseRule12, false);
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, RsiHigh, RsiLow,
								DivFrom, DivTo, SwingBars,
								BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3,
								MacdFast, MacdSlow, MacdSmooth,
								StochPeriodK, StochSmooth, StochPeriodD,
								MomentumSmaPeriod, MacdConfirmBars, MacdMinChangeAtr, false);
			}
		}

		protected override void OnBarUpdate()
		{
			// 데이터 부족 가드: 10분봉 SMA120이 채워지기 전에는 아무것도 하지 않는다
			if (CurrentBars[0] < DivTo || CurrentBars[1] < RegimeSlow)
				return;

			if (BarsInProgress == 1)
			{
				Print(string.Format("[{0}][{1}][TQ_Strategy] 10분봉 마감 레짐={2}",
					Time[0], Instrument.FullName, regime.Regime[0]));

				// spec 5.2 1차 익절: 상승추세 숏 보유 중 10분봉 저가 <= 10분봉 SMA20이면 즉시 익절.
				// 다음 3분봉 마감을 기다리지 않고 10분봉 마감 즉시 처리하며, 주문은 BIP 0 대상으로 낸다.
				if (active == ActiveStrategy.UpShort && !tp1Done
					&& Position.MarketPosition == MarketPosition.Short
					&& !double.IsNaN(regime.Sma20[0]) && Lows[1][0] <= regime.Sma20[0])
				{
					ExitChunk("UpS1", "5.2 1차 익절(10분봉 저가<=SMA20)");
					tp1Done = true;
					MoveBreakeven();
				}
				return;
			}

			if (BarsInProgress != 0)
				return;

			Print(string.Format("[{0}][{1}][TQ_Strategy] 레짐={2} UpLong={3} UpShort={4} DnLong={5} DnShort={6} SideLong={7} SideShort={8}",
				Time[0], Instrument.FullName, regime.Regime[0],
				signals.EntryUpLong[0], signals.EntryUpShort[0],
				signals.EntryDnLong[0], signals.EntryDnShort[0],
				signals.EntrySideLong[0], signals.EntrySideShort[0]));

			// spec 7장: 진입 횟수는 대회 구간마다 0부터 센다. 진입 시간대에 들어서는 첫 봉에서 리셋한다
			bool inEntryWindow = InEntryWindow();
			if (inEntryWindow && !wasInEntryWindow)
			{
				entryCount	= 0;
				blockLong	= false;	// 재진입 제한도 대회 구간마다 푼다
				blockShort	= false;
			}
			wasInEntryWindow = inEntryWindow;

			// spec 7장: 진입 횟수 화면 표시. 분할 진입 여러 건은 OnPositionUpdate에서 신호 1회로 센다
			Draw.TextFixed(this, "TQ_EntryCount",
				string.Format("{0} 진입: {1}회", Instrument.MasterInstrument.Name, entryCount),
				TextPosition.TopRight);

			// spec 7장: 종료 청산 — FlattenTime(00:27) 봉이 마감되면 남은 포지션을 전량 시장가로 청산한다
			if (IsFlattenTime())
			{
				if (Position.MarketPosition == MarketPosition.Long)
				{
					Print(string.Format("[{0}][{1}][TQ_Strategy] 종료 청산(롱) 수량={2}",
						Time[0], Instrument.FullName, Position.Quantity));
					ExitLong();	// 전량 시장가. 걸린 손절 스탑은 포지션 청산 시 자동 취소됨
				}
				else if (Position.MarketPosition == MarketPosition.Short)
				{
					Print(string.Format("[{0}][{1}][TQ_Strategy] 종료 청산(숏) 수량={2}",
						Time[0], Instrument.FullName, Position.Quantity));
					ExitShort();
				}
				return;	// 종료 시각 이후에는 신규 진입·익절 판단을 하지 않는다
			}

			if (Position.MarketPosition == MarketPosition.Flat)
			{
				// 미보유: 진입·익절·손절 상태 전체 리셋 (spec 1장 3, strategy.md)
				ResetTradeState();

				// spec 1장 2 + 7장: 미보유 + 진입 허용 시간대(신호 봉 22:30~00:15)일 때만 신규 진입
				if (InEntryWindow())
					RouteEntry();

				// TODO UseTestEntry: 주문 흐름 검증용 임시 진입 (task5)
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
					return;
				}

				// spec 4장 이익 보호 본절: 1차 익절 전이라도 유리폭이 기준에 닿으면 본절로 옮긴다
				CheckBreakevenAtR();

				// spec 5장: 진입 시점 레짐의 익절·청산 상태 머신
				ManageExits();

			}
		}

		// spec 7장: 분할 진입 여러 건은 신호 1회. 미보유(Flat)→보유 전환 시점에만 1 증가시킨다
		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (lastPosition == MarketPosition.Flat && marketPosition != MarketPosition.Flat)
			{
				entryCount++;
				Print(string.Format("[{0}][{1}][TQ_Strategy] 신규 진입 #{2} 방향={3} 평균가={4} 수량={5}",
					Time[0], Instrument.FullName, entryCount, marketPosition, averagePrice, quantity));
			}

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
				if      (signals.EntryUpLong[0])							EnterUpLong();
				else if (canShort && UseCounterTrend && signals.EntryUpShort[0])	EnterUpShort();
			}
			else if (r == -1)	// 하락추세: 주력 숏(5.4) 우선, 역추세 롱(5.3)
			{
				if      (signals.EntryDnShort[0])						EnterDnShort();
				else if (canLong && UseCounterTrend && signals.EntryDnLong[0])		EnterDnLong();
			}
			else				// 횡보(0): 롱(5.5)·숏(5.6)
			{
				if      (canLong && signals.EntrySideLong[0])	EnterSideLong();
				else if (canShort && signals.EntrySideShort[0])	EnterSideShort();
			}
		}

		// spec 5.1 상승추세 롱(주력): 1/5 + 2/5 + 나머지
		private void EnterUpLong()
		{
			BeginTrade(true, 1, ActiveStrategy.UpLong);
			int q1 = Split(1, 5), q2 = Split(2, 5), q3 = EntryQuantity - q1 - q2;
			SubmitChunk("UpL1", q1);
			SubmitChunk("UpL2", q2);
			SubmitChunk("UpL3", q3);
			LogEntry("상승추세 롱(5.1)", q1, q2, q3);
		}

		// spec 5.2 상승추세 숏(역추세): 1/2 + 나머지
		private void EnterUpShort()
		{
			BeginTrade(false, 1, ActiveStrategy.UpShort);
			int q1 = Split(1, 2), q2 = EntryQuantity - q1;
			SubmitChunk("UpS1", q1);
			SubmitChunk("UpS2", q2);
			LogEntry("상승추세 숏(5.2)", q1, q2, 0);
		}

		// spec 5.3 하락추세 롱(역추세): 1/3 + 나머지
		private void EnterDnLong()
		{
			BeginTrade(true, -1, ActiveStrategy.DnLong);
			int q1 = Split(1, 3), q2 = EntryQuantity - q1;
			SubmitChunk("DnL1", q1);
			SubmitChunk("DnL2", q2);
			LogEntry("하락추세 롱(5.3)", q1, q2, 0);
		}

		// spec 5.4 하락추세 숏(주력): 1/3 + 1/3 + 나머지
		private void EnterDnShort()
		{
			BeginTrade(false, -1, ActiveStrategy.DnShort);
			int q1 = Split(1, 3), q2 = Split(1, 3), q3 = EntryQuantity - q1 - q2;
			SubmitChunk("DnS1", q1);
			SubmitChunk("DnS2", q2);
			SubmitChunk("DnS3", q3);
			LogEntry("하락추세 숏(5.4)", q1, q2, q3);
		}

		// spec 5.5 횡보 롱: 1/2 + 나머지
		private void EnterSideLong()
		{
			BeginTrade(true, 0, ActiveStrategy.SideLong);
			int q1 = Split(1, 2), q2 = EntryQuantity - q1;
			SubmitChunk("SideL1", q1);
			SubmitChunk("SideL2", q2);
			LogEntry("횡보 롱(5.5)", q1, q2, 0);
		}

		// spec 5.6 횡보 숏: 1/2 + 나머지
		private void EnterSideShort()
		{
			BeginTrade(false, 0, ActiveStrategy.SideShort);
			int q1 = Split(1, 2), q2 = EntryQuantity - q1;
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
			return (EntryQuantity * num) / den;
		}

		private void LogEntry(string name, int q1, int q2, int q3)
		{
			Print(string.Format("[{0}][{1}][TQ_Strategy] 진입 {2} 수량={3}/{4}/{5} (레짐={6}) 손절={7}",
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
				return Instrument.MasterInstrument.RoundToTickSize(pct);
			return Instrument.MasterInstrument.RoundToTickSize(isLongPos ? Math.Max(stopSwing, pct) : Math.Min(stopSwing, pct));
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

			if (chunkQty.ContainsKey(name))
				OnEntryFill(price, quantity, time);
			else if (breakevenPending && name.StartsWith("x"))	// 분할 익절 주문 이름은 "x" + 진입 시그널명
				OnFirstTakeProfitFill(price, time);
			else if (name == "Stop loss")						// SetStopLoss가 내는 주문 이름
				OnStopLossFill(price, time);
		}

		// spec 4장 재진입 제한: 손절이 손실로 체결되면 이번 대회 구간에서 같은 방향 진입을 막는다.
		// 본절 손절(손실 없음)은 막지 않는다
		private void OnStopLossFill(double exitPrice, DateTime time)
		{
			if (entryFillQty <= 0)
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
				Print(string.Format("[{0}][{1}][TQ_Strategy] 손실 손절 → 이번 대회 구간 {2} 재진입 금지",
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
			Print(string.Format("[{0}][{1}][TQ_Strategy] 이익 보호 본절 이동 손절→평균가={2} (유리폭 기준 {3})",
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
				Print(string.Format("[{0}][{1}][TQ_Strategy] 갭 손절 초과 → 즉시 전량 청산 체결가={2} 손절={3}",
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
			Print(string.Format("[{0}][{1}][TQ_Strategy] 손절 재계산(체결가 기준) 평균 체결가={2} 손절={3}",
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
				Print(string.Format("[{0}][{1}][TQ_Strategy] 본절 보류(손실 중) 평균가={2} 익절 체결가={3} 손절 유지={4}",
					time, Instrument.FullName, be, exitPrice, stopPrice));
				return;
			}

			stopPrice		= be;
			breakevenDone	= true;
			SetStopForLive(be);
			Print(string.Format("[{0}][{1}][TQ_Strategy] 본절 이동 손절→평균가={2}",
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
			Print(string.Format("[{0}][{1}][TQ_Strategy] 익절 {2} 시그널={3} 수량={4}",
				Time[0], Instrument.FullName, reason, sig, q));
		}

		// 남은 전량 시장가 청산 (손절 스탑은 포지션 청산 시 자동 취소). 청산 후 중복 주문 방지
		private void FlattenRemaining(string reason)
		{
			if (isLongPos)	ExitLong();
			else			ExitShort();
			liveSignals.Clear();
			active = ActiveStrategy.None;
			Print(string.Format("[{0}][{1}][TQ_Strategy] 전량 청산 {2}",
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
				if (!tp1Done) ExitChunk("UpL1", "5.1 강한모멘텀 1/5");
				ExitChunk("UpL2", "5.1 강한모멘텀 2/5");
				tp1Done			= true;
				strongMomentum	= true;		// 이후 약한 모멘텀 청산 적용 안 함
				MoveBreakeven();
				return;
			}

			if (!tp1Done)
			{
				// 공통 규칙: 1차 익절 전/동시에 나머지 청산 조건이 나오면 전량 청산
				if (up2 && weak)	{ FlattenRemaining("5.1 1차+약한모멘텀 동시 → 전량"); return; }
				if (weak)			{ FlattenRemaining("5.1 약한모멘텀 청산(1차 전) → 전량"); return; }
				if (up2)			{ ExitChunk("UpL1", "5.1 1차 익절 1/5"); tp1Done = true; MoveBreakeven(); }
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
				if (tp1)			{ ExitChunk("UpS1", "5.2 1차 익절 1/2"); tp1Done = true; MoveBreakeven(); }
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
				if (tp1)			{ ExitChunk("DnL1", "5.3 1차 익절 1/3"); tp1Done = true; MoveBreakeven(); }
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
				if (tp1)	{ ExitChunk("DnS1", "5.4 1차 익절 1/3"); tp1Done = true; MoveBreakeven(); }
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
				bool aRest = High[0] >= channels.Up3[0];	// A 나머지 청산: 고가 +3배 터치
				bool bRest = signals.T2Bear[0];				// B 나머지 청산: MACD 하락 전환 AND 데드크로스
				bool anyTp1  = aTp1 || bTp1;
				bool anyRest = aRest || bRest;

				// 공통 규칙: 1차 익절 전/동시에 A·B 어느 세트든 나머지 청산 조건이 나오면 전량 청산
				if (anyTp1 && anyRest)	{ FlattenRemaining("5.5 1차+나머지 동시 → 전량"); return; }
				if (anyRest)			{ FlattenRemaining("5.5 나머지 청산(1차 전) → 전량"); return; }
				if (anyTp1)
				{
					sideSet = aTp1 ? SideSet.A : SideSet.B;	// 동시 충족이면 A (기본값)
					ExitChunk("SideL1", "5.5 1차 익절 1/2 (세트 " + sideSet + ")");
					tp1Done = true;
					MoveBreakeven();
				}
			}
			else if (sideSet == SideSet.A)
			{
				if (High[0] >= channels.Up3[0])	FlattenRemaining("5.5 A 나머지 청산(고가 +3배 터치)");
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
				bool aRest = Low[0] <= channels.Dn3[0];		// A 나머지 청산: 저가 -3배 터치
				bool bRest = signals.T2Bull[0];				// B 나머지 청산: MACD 상승 전환 AND 골든크로스
				bool anyTp1  = aTp1 || bTp1;
				bool anyRest = aRest || bRest;

				if (anyTp1 && anyRest)	{ FlattenRemaining("5.6 1차+나머지 동시 → 전량"); return; }
				if (anyRest)			{ FlattenRemaining("5.6 나머지 청산(1차 전) → 전량"); return; }
				if (anyTp1)
				{
					sideSet = aTp1 ? SideSet.A : SideSet.B;	// 동시 충족이면 A (기본값)
					ExitChunk("SideS1", "5.6 1차 익절 1/2 (세트 " + sideSet + ")");
					tp1Done = true;
					MoveBreakeven();
				}
			}
			else if (sideSet == SideSet.A)
			{
				if (Low[0] <= channels.Dn3[0])	FlattenRemaining("5.6 A 나머지 청산(저가 −3배 터치)");
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
			liveSignals.Clear();
			chunkQty.Clear();
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
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandMidPeriod", Description = "ATR 채널 중심선 EMA 기간", GroupName = "1. 밴드", Order = 0)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandAtrPeriod", Description = "ATR 기간", GroupName = "1. 밴드", Order = 1)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult1", Description = "밴드 배수 1", GroupName = "1. 밴드", Order = 2)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult2", Description = "밴드 배수 2", GroupName = "1. 밴드", Order = 3)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult3", Description = "밴드 배수 3", GroupName = "1. 밴드", Order = 4)]
		public double BandMult3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdFast", Description = "MACD 단기", GroupName = "2. 신호", Order = 0)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSlow", Description = "MACD 장기", GroupName = "2. 신호", Order = 1)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSmooth", Description = "MACD 시그널", GroupName = "2. 신호", Order = 2)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodK", Description = "스토캐스틱 %K 길이", GroupName = "2. 신호", Order = 3)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochSmooth", Description = "스토캐스틱 %K 스무딩", GroupName = "2. 신호", Order = 4)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodD", Description = "스토캐스틱 %D 스무딩", GroupName = "2. 신호", Order = 5)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossLow", Description = "골든크로스 필터·%K 하방 돌파 기준", GroupName = "2. 신호", Order = 6)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossHigh", Description = "데드크로스 필터·%K 상향 돌파 기준", GroupName = "2. 신호", Order = 7)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "CrossFilterBars", Description = "크로스 필터 봉 수 (현재 봉 포함)", GroupName = "2. 신호", Order = 8)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간", GroupName = "2. 신호", Order = 9)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiHigh", Description = "RSI 과매수 기준 (이상)", GroupName = "2. 신호", Order = 10)]
		public double RsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiLow", Description = "RSI 과매도 기준 (미만)", GroupName = "2. 신호", Order = 11)]
		public double RsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivFrom", Description = "다이버전스 비교 구간 시작 (봉 전)", GroupName = "2. 신호", Order = 12)]
		public int DivFrom { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivTo", Description = "다이버전스 비교 구간 끝 (봉 전)", GroupName = "2. 신호", Order = 13)]
		public int DivTo { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "T1Window", Description = "T1 대기 봉 수 N (미정, 임시 3)", GroupName = "2. 신호", Order = 14)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdConfirmBars", Description = "MACD 전환 확인 봉 수 (1 = 직전 봉 대비, 2 = 2봉 연속)", GroupName = "2. 신호", Order = 15)]
		public int MacdConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "MacdMinChangeAtr", Description = "MACD 전환 최소 변화폭 (ATR 배수, 0 = 사용 안 함)", GroupName = "2. 신호", Order = 16)]
		public double MacdMinChangeAtr { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RegimeFast", Description = "레짐 SMA 단기 (10분봉)", GroupName = "3. 레짐", Order = 0)]
		public int RegimeFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RegimeMid", Description = "레짐 SMA 중기 (10분봉)", GroupName = "3. 레짐", Order = 1)]
		public int RegimeMid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RegimeSlow", Description = "레짐 SMA 장기 (10분봉)", GroupName = "3. 레짐", Order = 2)]
		public int RegimeSlow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseRule12", Description = "레짐 1.2·2.2 사용 (spec 3장)", GroupName = "3. 레짐", Order = 3)]
		public bool UseRule12 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SwingBars", Description = "전고점·전저점 구간 (신호 봉 포함)", GroupName = "4. 손절·청산", Order = 0)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "StopPercent", Description = "손절 % (종목별)", GroupName = "4. 손절·청산", Order = 1)]
		public double StopPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MomentumSmaPeriod", Description = "강한 모멘텀 청산 SMA 기간 (3분봉)", GroupName = "4. 손절·청산", Order = 2)]
		public int MomentumSmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "BlockReentryAfterStop", Description = "손실 손절 뒤 그 대회 구간에서 같은 방향 재진입 금지", GroupName = "4. 손절·청산", Order = 3)]
		public bool BlockReentryAfterStop { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BreakevenAtR", Description = "이익 보호 본절: 유리폭이 처음 손절 거리의 몇 배에 닿으면 본절로 옮길지 (0 = 사용 안 함)", GroupName = "4. 손절·청산", Order = 4)]
		public double BreakevenAtR { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseCounterTrend", Description = "역추세 진입(5.2 상승추세 숏, 5.3 하락추세 롱) 사용", GroupName = "3. 레짐", Order = 4)]
		public bool UseCounterTrend { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EntryQuantity", Description = "기본 진입 수량 (마이크로)", GroupName = "5. 수량", Order = 0)]
		public int EntryQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MaxQuantity", Description = "최대 보유 수량 (v1 미사용)", GroupName = "5. 수량", Order = 1)]
		public int MaxQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "ContestStartTime", Description = "신규 진입 시작 (HHmmss, KST)", GroupName = "6. 대회", Order = 0)]
		public int ContestStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "EntryEndTime", Description = "신규 진입 마감, 신호 봉 기준 (HHmmss, KST)", GroupName = "6. 대회", Order = 1)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "FlattenTime", Description = "종료 청산, 이 시각 봉 마감 (HHmmss, KST)", GroupName = "6. 대회", Order = 2)]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseTestEntry", Description = "주문 흐름 검증용 임시 진입", GroupName = "7. 테스트", Order = 0)]
		public bool UseTestEntry { get; set; }
		#endregion
	}
}
