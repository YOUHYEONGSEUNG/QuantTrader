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
	/// 스토캐스틱 시각화 지표 (TradingView Pine v6 Stochastic과 동일 계산).
	/// TQ_Signals 내부 계산과 같은 식이라, 차트에 올려 %K·%D를 눈으로 검증하는 용도다.
	/// 독립 패널에 %K(파랑)·%D(주황)와 20/50/80 기준선을 그린다.
	/// 원시 %K = 100 × (종가 − 최저 저가) / (최고 고가 − 최저 저가), 구간 = PeriodK
	/// %K = SMA(원시 %K, SmoothK), %D = SMA(%K, PeriodD)
	/// </summary>
	public class TQ_Stochastic : Indicator
	{
		private MIN				stochLow;	// PeriodK 구간 최저 저가
		private MAX				stochHigh;	// PeriodK 구간 최고 고가
		private SMA				stochK;		// %K = SMA(원시 %K, SmoothK)
		private SMA				stochD;		// %D = SMA(%K, PeriodD)
		private Series<double>	rawK;		// 원시 %K (스무딩 전)

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "스토캐스틱 시각화 (TradingView Pine v6과 동일, SMA 스무딩)";
				Name						= "TQ_Stochastic";
				Calculate					= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				IsOverlay					= false;				// 독립 패널(오실레이터)
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값 (Stochastics 5, 10, 5)
				PeriodK						= 10;	// %K 길이
				SmoothK						= 5;	// %K 스무딩
				PeriodD						= 5;	// %D 스무딩

				AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "K");	// %K (파란색)
				AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "D");		// %D (주황색)

				AddLine(Brushes.Gray, 80, "Upper");									// 과매수
				AddLine(new Stroke(Brushes.DarkGray, 1), 50, "Middle");				// 중앙
				AddLine(Brushes.Gray, 20, "Lower");									// 과매도
			}
			else if (State == State.DataLoaded)
			{
				rawK		= new Series<double>(this);
				stochLow	= MIN(Low, PeriodK);
				stochHigh	= MAX(High, PeriodK);
				stochK		= SMA(rawK, SmoothK);
				stochD		= SMA(K, PeriodD);	// K 플롯(Values[0])의 SMA
			}
		}

		protected override void OnBarUpdate()
		{
			double ll		= stochLow[0];
			double hh		= stochHigh[0];
			double range	= hh - ll;
			// 범위가 0이면(고가=저가) 직전 값 유지. 유동성 있는 선물에서는 거의 없음
			rawK[0]	= range == 0 ? (CurrentBar == 0 ? 50 : rawK[1]) : 100 * (Close[0] - ll) / range;
			K[0]	= stochK[0];	// 원시 %K를 SmoothK로 스무딩
			D[0]	= stochD[0];	// %K를 PeriodD로 스무딩
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "PeriodK", Description = "%K 길이 (스토캐스틱 구간)", GroupName = "Parameters", Order = 0)]
		public int PeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SmoothK", Description = "%K 스무딩", GroupName = "Parameters", Order = 1)]
		public int SmoothK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "PeriodD", Description = "%D 스무딩", GroupName = "Parameters", Order = 2)]
		public int PeriodD { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> K { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> D { get { return Values[1]; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_Stochastic[] cacheTQ_Stochastic;
		public TeamQuant.TQ_Stochastic TQ_Stochastic(int periodK, int smoothK, int periodD)
		{
			return TQ_Stochastic(Input, periodK, smoothK, periodD);
		}

		public TeamQuant.TQ_Stochastic TQ_Stochastic(ISeries<double> input, int periodK, int smoothK, int periodD)
		{
			if (cacheTQ_Stochastic != null)
				for (int idx = 0; idx < cacheTQ_Stochastic.Length; idx++)
					if (cacheTQ_Stochastic[idx] != null && cacheTQ_Stochastic[idx].PeriodK == periodK && cacheTQ_Stochastic[idx].SmoothK == smoothK && cacheTQ_Stochastic[idx].PeriodD == periodD && cacheTQ_Stochastic[idx].EqualsInput(input))
						return cacheTQ_Stochastic[idx];
			return CacheIndicator<TeamQuant.TQ_Stochastic>(new TeamQuant.TQ_Stochastic(){ PeriodK = periodK, SmoothK = smoothK, PeriodD = periodD }, input, ref cacheTQ_Stochastic);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Stochastic TQ_Stochastic(int periodK, int smoothK, int periodD)
		{
			return indicator.TQ_Stochastic(Input, periodK, smoothK, periodD);
		}

		public Indicators.TeamQuant.TQ_Stochastic TQ_Stochastic(ISeries<double> input , int periodK, int smoothK, int periodD)
		{
			return indicator.TQ_Stochastic(input, periodK, smoothK, periodD);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Stochastic TQ_Stochastic(int periodK, int smoothK, int periodD)
		{
			return indicator.TQ_Stochastic(Input, periodK, smoothK, periodD);
		}

		public Indicators.TeamQuant.TQ_Stochastic TQ_Stochastic(ISeries<double> input , int periodK, int smoothK, int periodD)
		{
			return indicator.TQ_Stochastic(input, periodK, smoothK, periodD);
		}
	}
}

#endregion
