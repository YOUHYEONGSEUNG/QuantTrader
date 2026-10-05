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
	/// ATR Channels: 중심선 EMA ± ATR × 1·2·3 (spec 2장 밴드, docs/interface.md)
	/// 스켈레톤: 플롯·속성만 있고 값은 모두 NaN.
	/// </summary>
	public class TQ_ATRChannels : Indicator
	{
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "ATR Channels: EMA ± ATR × 1·2·3 (spec 2장)";
				Name						= "TQ_ATRChannels";
				Calculate					= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				IsOverlay					= true;
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				MidPeriod					= 26;
				AtrPeriod					= 14;

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.Gray, "Mid");
				AddPlot(Brushes.Orange, "Up1");
				AddPlot(Brushes.OrangeRed, "Up2");
				AddPlot(Brushes.Red, "Up3");
				AddPlot(Brushes.DodgerBlue, "Dn1");
				AddPlot(Brushes.RoyalBlue, "Dn2");
				AddPlot(Brushes.Blue, "Dn3");
			}
		}

		protected override void OnBarUpdate()
		{
			// TODO(A) spec 2장 밴드: Mid = EMA(MidPeriod), Up/Dn = Mid ± ATR(AtrPeriod) × 1·2·3
			Mid[0] = double.NaN;
			Up1[0] = double.NaN;
			Up2[0] = double.NaN;
			Up3[0] = double.NaN;
			Dn1[0] = double.NaN;
			Dn2[0] = double.NaN;
			Dn3[0] = double.NaN;
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MidPeriod", Description = "중심선 EMA 기간", GroupName = "Parameters", Order = 0)]
		public int MidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "AtrPeriod", Description = "ATR 기간", GroupName = "Parameters", Order = 1)]
		public int AtrPeriod { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Mid { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Up1 { get { return Values[1]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Up2 { get { return Values[2]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Up3 { get { return Values[3]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Dn1 { get { return Values[4]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Dn2 { get { return Values[5]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Dn3 { get { return Values[6]; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_ATRChannels[] cacheTQ_ATRChannels;
		public TeamQuant.TQ_ATRChannels TQ_ATRChannels(int midPeriod, int atrPeriod)
		{
			return TQ_ATRChannels(Input, midPeriod, atrPeriod);
		}

		public TeamQuant.TQ_ATRChannels TQ_ATRChannels(ISeries<double> input, int midPeriod, int atrPeriod)
		{
			if (cacheTQ_ATRChannels != null)
				for (int idx = 0; idx < cacheTQ_ATRChannels.Length; idx++)
					if (cacheTQ_ATRChannels[idx] != null && cacheTQ_ATRChannels[idx].MidPeriod == midPeriod && cacheTQ_ATRChannels[idx].AtrPeriod == atrPeriod && cacheTQ_ATRChannels[idx].EqualsInput(input))
						return cacheTQ_ATRChannels[idx];
			return CacheIndicator<TeamQuant.TQ_ATRChannels>(new TeamQuant.TQ_ATRChannels(){ MidPeriod = midPeriod, AtrPeriod = atrPeriod }, input, ref cacheTQ_ATRChannels);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_ATRChannels TQ_ATRChannels(int midPeriod, int atrPeriod)
		{
			return indicator.TQ_ATRChannels(Input, midPeriod, atrPeriod);
		}

		public Indicators.TeamQuant.TQ_ATRChannels TQ_ATRChannels(ISeries<double> input , int midPeriod, int atrPeriod)
		{
			return indicator.TQ_ATRChannels(input, midPeriod, atrPeriod);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_ATRChannels TQ_ATRChannels(int midPeriod, int atrPeriod)
		{
			return indicator.TQ_ATRChannels(Input, midPeriod, atrPeriod);
		}

		public Indicators.TeamQuant.TQ_ATRChannels TQ_ATRChannels(ISeries<double> input , int midPeriod, int atrPeriod)
		{
			return indicator.TQ_ATRChannels(input, midPeriod, atrPeriod);
		}
	}
}

#endregion
