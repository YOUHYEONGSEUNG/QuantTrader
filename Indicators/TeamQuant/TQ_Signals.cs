#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.TeamQuant
{
	/// <summary>
	/// 3분봉 신호 엔진: 숫자 값, 이벤트, 6개 진입 신호 (spec 2장·5장, docs/interface.md)
	/// 포지션·주문·시간대와 무관한 값만 낸다.
	/// 스켈레톤: 모든 속성이 있고 값은 false/NaN.
	/// </summary>
	public class TQ_Signals : Indicator
	{
		// 이벤트 (spec 2장)
		private Series<bool> crossAboveUp1;
		private Series<bool> crossAboveUp2;
		private Series<bool> crossAboveUp3;
		private Series<bool> crossBelowDn1;
		private Series<bool> crossBelowDn2;
		private Series<bool> crossBelowDn3;
		private Series<bool> macdUp;
		private Series<bool> macdDown;
		private Series<bool> golden;
		private Series<bool> dead;
		private Series<bool> k80CrossUp;
		private Series<bool> k20CrossDown;
		private Series<bool> bearDiv;
		private Series<bool> t2Bull;
		private Series<bool> t2Bear;

		// 진입 신호 (spec 5.1~5.6)
		private Series<bool> entryUpLong;
		private Series<bool> entryUpShort;
		private Series<bool> entryDnLong;
		private Series<bool> entryDnShort;
		private Series<bool> entrySideLong;
		private Series<bool> entrySideShort;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "3분봉 신호 엔진: 이벤트와 진입 신호 (spec 2장·5장)";
				Name						= "TQ_Signals";
				Calculate					= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				IsOverlay					= false;
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				T1Window					= 3;
				CrossFilterBars				= 5;
				CrossLow					= 20;
				CrossHigh					= 80;
				RsiPeriod					= 14;
				RsiHigh						= 70;
				RsiLow						= 20;
				DivFrom						= 5;
				DivTo						= 30;
				SwingBars					= 5;
				ShowDebugVisuals			= false;

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.DodgerBlue, "K");
				AddPlot(Brushes.Orange, "D");
				AddPlot(Brushes.MediumPurple, "Rsi");
				AddPlot(Brushes.Gray, "MacdHist");
				AddPlot(Brushes.Goldenrod, "Sma20");
				AddPlot(Brushes.Red, "SwingHigh5");
				AddPlot(Brushes.Blue, "SwingLow5");
			}
			else if (State == State.DataLoaded)
			{
				crossAboveUp1	= new Series<bool>(this);
				crossAboveUp2	= new Series<bool>(this);
				crossAboveUp3	= new Series<bool>(this);
				crossBelowDn1	= new Series<bool>(this);
				crossBelowDn2	= new Series<bool>(this);
				crossBelowDn3	= new Series<bool>(this);
				macdUp			= new Series<bool>(this);
				macdDown		= new Series<bool>(this);
				golden			= new Series<bool>(this);
				dead			= new Series<bool>(this);
				k80CrossUp		= new Series<bool>(this);
				k20CrossDown	= new Series<bool>(this);
				bearDiv			= new Series<bool>(this);
				t2Bull			= new Series<bool>(this);
				t2Bear			= new Series<bool>(this);

				entryUpLong		= new Series<bool>(this);
				entryUpShort	= new Series<bool>(this);
				entryDnLong		= new Series<bool>(this);
				entryDnShort	= new Series<bool>(this);
				entrySideLong	= new Series<bool>(this);
				entrySideShort	= new Series<bool>(this);
			}
		}

		protected override void OnBarUpdate()
		{
			// TODO(A) 숫자 값: Stochastics(5, 10, 5), RSI, MACD.Diff, SMA20, 최근 SwingBars봉 고가·저가
			K[0]			= double.NaN;
			D[0]			= double.NaN;
			Rsi[0]			= double.NaN;
			MacdHist[0]		= double.NaN;
			Sma20[0]		= double.NaN;
			SwingHigh5[0]	= double.NaN;
			SwingLow5[0]	= double.NaN;

			// TODO(A) spec 2장 이벤트: 돌파, MACD 전환, 필터된 크로스, %K 80/20, 하락 다이버전스, T2
			crossAboveUp1[0]	= false;
			crossAboveUp2[0]	= false;
			crossAboveUp3[0]	= false;
			crossBelowDn1[0]	= false;
			crossBelowDn2[0]	= false;
			crossBelowDn3[0]	= false;
			macdUp[0]			= false;
			macdDown[0]			= false;
			golden[0]			= false;
			dead[0]				= false;
			k80CrossUp[0]		= false;
			k20CrossDown[0]		= false;
			bearDiv[0]			= false;
			t2Bull[0]			= false;
			t2Bear[0]			= false;

			// TODO(A) spec 2장 T1 + 5.1~5.6 진입 신호: 신호마다 대기 상태를 따로 둔다
			// TODO(A) ShowDebugVisuals: 진입 신호 위치에 화살표
			entryUpLong[0]		= false;
			entryUpShort[0]		= false;
			entryDnLong[0]		= false;
			entryDnShort[0]		= false;
			entrySideLong[0]	= false;
			entrySideShort[0]	= false;
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "T1Window", Description = "T1 대기 봉 수 N (spec 2장 T1)", GroupName = "Parameters", Order = 0)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "CrossFilterBars", Description = "크로스 필터 봉 수 (현재 봉 포함)", GroupName = "Parameters", Order = 1)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossLow", Description = "골든크로스 필터·%K 하방 돌파 기준", GroupName = "Parameters", Order = 2)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossHigh", Description = "데드크로스 필터·%K 상향 돌파 기준", GroupName = "Parameters", Order = 3)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간", GroupName = "Parameters", Order = 4)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiHigh", Description = "RSI 과매수 기준 (이상)", GroupName = "Parameters", Order = 5)]
		public double RsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiLow", Description = "RSI 과매도 기준 (미만)", GroupName = "Parameters", Order = 6)]
		public double RsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivFrom", Description = "다이버전스 비교 구간 시작 (봉 전)", GroupName = "Parameters", Order = 7)]
		public int DivFrom { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivTo", Description = "다이버전스 비교 구간 끝 (봉 전)", GroupName = "Parameters", Order = 8)]
		public int DivTo { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SwingBars", Description = "전고점·전저점 구간 (신호 봉 포함)", GroupName = "Parameters", Order = 9)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 화살표 표시", GroupName = "Parameters", Order = 10)]
		public bool ShowDebugVisuals { get; set; }

		// 숫자 값
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> K { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> D { get { return Values[1]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Rsi { get { return Values[2]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> MacdHist { get { return Values[3]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Sma20 { get { return Values[4]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SwingHigh5 { get { return Values[5]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SwingLow5 { get { return Values[6]; } }

		// 이벤트: Update()로 호출 시점 값을 최신화
		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> CrossAboveUp1 { get { Update(); return crossAboveUp1; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> CrossAboveUp2 { get { Update(); return crossAboveUp2; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> CrossAboveUp3 { get { Update(); return crossAboveUp3; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> CrossBelowDn1 { get { Update(); return crossBelowDn1; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> CrossBelowDn2 { get { Update(); return crossBelowDn2; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> CrossBelowDn3 { get { Update(); return crossBelowDn3; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> MacdUp { get { Update(); return macdUp; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> MacdDown { get { Update(); return macdDown; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> Golden { get { Update(); return golden; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> Dead { get { Update(); return dead; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> K80CrossUp { get { Update(); return k80CrossUp; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> K20CrossDown { get { Update(); return k20CrossDown; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> BearDiv { get { Update(); return bearDiv; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> T2Bull { get { Update(); return t2Bull; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> T2Bear { get { Update(); return t2Bear; } }

		// 진입 신호
		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryUpLong { get { Update(); return entryUpLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryUpShort { get { Update(); return entryUpShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryDnLong { get { Update(); return entryDnLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryDnShort { get { Update(); return entryDnShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntrySideLong { get { Update(); return entrySideLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntrySideShort { get { Update(); return entrySideShort; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_Signals[] cacheTQ_Signals;
		public TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, bool showDebugVisuals)
		{
			return TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, showDebugVisuals);
		}

		public TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input, int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, bool showDebugVisuals)
		{
			if (cacheTQ_Signals != null)
				for (int idx = 0; idx < cacheTQ_Signals.Length; idx++)
					if (cacheTQ_Signals[idx] != null && cacheTQ_Signals[idx].T1Window == t1Window && cacheTQ_Signals[idx].CrossFilterBars == crossFilterBars && cacheTQ_Signals[idx].CrossLow == crossLow && cacheTQ_Signals[idx].CrossHigh == crossHigh && cacheTQ_Signals[idx].RsiPeriod == rsiPeriod && cacheTQ_Signals[idx].RsiHigh == rsiHigh && cacheTQ_Signals[idx].RsiLow == rsiLow && cacheTQ_Signals[idx].DivFrom == divFrom && cacheTQ_Signals[idx].DivTo == divTo && cacheTQ_Signals[idx].SwingBars == swingBars && cacheTQ_Signals[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Signals[idx].EqualsInput(input))
						return cacheTQ_Signals[idx];
			return CacheIndicator<TeamQuant.TQ_Signals>(new TeamQuant.TQ_Signals(){ T1Window = t1Window, CrossFilterBars = crossFilterBars, CrossLow = crossLow, CrossHigh = crossHigh, RsiPeriod = rsiPeriod, RsiHigh = rsiHigh, RsiLow = rsiLow, DivFrom = divFrom, DivTo = divTo, SwingBars = swingBars, ShowDebugVisuals = showDebugVisuals }, input, ref cacheTQ_Signals);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, showDebugVisuals);
		}

		public Indicators.TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input , int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, showDebugVisuals);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, showDebugVisuals);
		}

		public Indicators.TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input , int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, showDebugVisuals);
		}
	}
}

#endregion
