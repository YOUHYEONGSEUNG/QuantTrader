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
	/// 10분봉 레짐: +1 상승추세, −1 하락추세, 0 횡보 (spec 3장, docs/interface.md)
	/// 입력 시리즈가 10분봉이어야 한다. 전략에서는 TQ_Regime(BarsArray[1])로 호출한다.
	/// 스켈레톤: 플롯·속성만 있고 값은 모두 NaN.
	/// </summary>
	public class TQ_Regime : Indicator
	{
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "10분봉 레짐 (+1 상승 / −1 하락 / 0 횡보)과 10분봉 SMA20 (spec 3장)";
				Name						= "TQ_Regime";
				Calculate					= Calculate.OnBarClose;	// spec 3장: 10분봉 마감 때마다 다시 계산
				IsOverlay					= false;
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				Fast						= 20;
				Mid							= 60;
				Slow						= 120;
				UseRule12					= false;	// spec 1.2·2.2, 정의 확정 전까지 끔
				ShowDebugVisuals			= false;

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.Gold, "Regime");
				AddPlot(Brushes.Gray, "Sma20");
			}
		}

		protected override void OnBarUpdate()
		{
			// TODO(A) spec 3장 (1.1)(2.1)(3.1): SMA Fast/Mid/Slow 정배열·역배열과 종가 vs SMA Fast
			// TODO(A) UseRule12가 켜졌을 때의 1.2·2.2는 정의 확정 후 (spec 9장)
			// TODO(A) ShowDebugVisuals: 레짐별 배경색
			Regime[0]	= double.NaN;
			Sma20[0]	= double.NaN;
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fast", Description = "레짐 SMA 단기", GroupName = "Parameters", Order = 0)]
		public int Fast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Mid", Description = "레짐 SMA 중기", GroupName = "Parameters", Order = 1)]
		public int Mid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Slow", Description = "레짐 SMA 장기", GroupName = "Parameters", Order = 2)]
		public int Slow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseRule12", Description = "spec 1.2·2.2 사용 (정의 확정 전까지 끔)", GroupName = "Parameters", Order = 3)]
		public bool UseRule12 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 배경색 표시", GroupName = "Parameters", Order = 4)]
		public bool ShowDebugVisuals { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Regime { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Sma20 { get { return Values[1]; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_Regime[] cacheTQ_Regime;
		public TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool useRule12, bool showDebugVisuals)
		{
			return TQ_Regime(Input, fast, mid, slow, useRule12, showDebugVisuals);
		}

		public TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input, int fast, int mid, int slow, bool useRule12, bool showDebugVisuals)
		{
			if (cacheTQ_Regime != null)
				for (int idx = 0; idx < cacheTQ_Regime.Length; idx++)
					if (cacheTQ_Regime[idx] != null && cacheTQ_Regime[idx].Fast == fast && cacheTQ_Regime[idx].Mid == mid && cacheTQ_Regime[idx].Slow == slow && cacheTQ_Regime[idx].UseRule12 == useRule12 && cacheTQ_Regime[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Regime[idx].EqualsInput(input))
						return cacheTQ_Regime[idx];
			return CacheIndicator<TeamQuant.TQ_Regime>(new TeamQuant.TQ_Regime(){ Fast = fast, Mid = mid, Slow = slow, UseRule12 = useRule12, ShowDebugVisuals = showDebugVisuals }, input, ref cacheTQ_Regime);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool useRule12, bool showDebugVisuals)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, useRule12, showDebugVisuals);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool useRule12, bool showDebugVisuals)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, useRule12, showDebugVisuals);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool useRule12, bool showDebugVisuals)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, useRule12, showDebugVisuals);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool useRule12, bool showDebugVisuals)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, useRule12, showDebugVisuals);
		}
	}
}

#endregion
