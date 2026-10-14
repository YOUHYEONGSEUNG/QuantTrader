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
	/// MACD 상승/하락 전환 판정 (spec 2장, docs/interface.md).
	/// TQ_Signals가 이 지표의 Up/Down을 그대로 쓴다. 차트에 올리면 히스토그램 막대 색으로 판정을 볼 수 있다:
	/// 초록 = 상승 전환, 빨강 = 하락 전환, 회색 = 둘 다 아님.
	/// </summary>
	public class TQ_MacdTurn : Indicator
	{
		private MACD			macd;
		private ATR				atr;
		private Series<bool>	up;
		private Series<bool>	down;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "MACD 상승/하락 전환 판정과 히스토그램 색 표시 (spec 2장)";
				Name						= "TQ_MacdTurn";
				Calculate					= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				IsOverlay					= false;
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				Fast						= 12;
				Slow						= 26;
				Smooth						= 9;
				ConfirmBars					= 2;
				AtrPeriod					= 14;
				MinChangeAtr				= 0;

				AddPlot(new Stroke(Brushes.Gray, 2), PlotStyle.Bar, "Hist");
				AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "MacdLine");	// MACD선 (파란색)
				AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "SignalLine");	// 시그널선 (주황색)
				AddLine(Brushes.DarkGray, 0, "Zero");
			}
			else if (State == State.DataLoaded)
			{
				macd	= MACD(Fast, Slow, Smooth);
				atr		= ATR(AtrPeriod);
				up		= new Series<bool>(this);
				down	= new Series<bool>(this);
			}
		}

		protected override void OnBarUpdate()
		{
			Hist[0]			= macd.Diff[0];
			Values[1][0]	= macd.Default[0];	// MACD선
			Values[2][0]	= macd.Avg[0];		// 시그널선

			bool isUp	= false;
			bool isDown	= false;

			// 데이터 부족 가드: ConfirmBars봉 전까지 참조한다
			if (CurrentBar >= ConfirmBars)
			{
				// spec 2장 MACD 전환: 히스토그램이 ConfirmBars봉 연속으로 증가(감소)하고,
				// 그 구간의 변화폭이 MinChangeAtr × ATR 이상이어야 한다
				isUp	= true;
				isDown	= true;
				for (int i = 0; i < ConfirmBars; i++)
				{
					if (!(macd.Diff[i] > macd.Diff[i + 1]))	isUp = false;
					if (!(macd.Diff[i] < macd.Diff[i + 1]))	isDown = false;
				}

				double minChange	= MinChangeAtr * atr[0];
				double change		= macd.Diff[0] - macd.Diff[ConfirmBars];
				if (isUp && change < minChange)		isUp = false;
				if (isDown && -change < minChange)	isDown = false;
			}

			up[0]	= isUp;
			down[0]	= isDown;

			PlotBrushes[0][0] = isUp ? Brushes.LimeGreen : isDown ? Brushes.Red : Brushes.Gray;
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fast", Description = "MACD 단기", GroupName = "Parameters", Order = 0)]
		public int Fast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Slow", Description = "MACD 장기", GroupName = "Parameters", Order = 1)]
		public int Slow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Smooth", Description = "MACD 시그널", GroupName = "Parameters", Order = 2)]
		public int Smooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ConfirmBars", Description = "MACD 전환 확인 봉 수 (연속 증가·감소 봉 수)", GroupName = "Parameters", Order = 3)]
		public int ConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "AtrPeriod", Description = "변화폭 기준에 쓰는 ATR 기간", GroupName = "Parameters", Order = 4)]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "MinChangeAtr", Description = "MACD 전환 최소 변화폭 (ATR 배수, 0 = 사용 안 함)", GroupName = "Parameters", Order = 5)]
		public double MinChangeAtr { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Hist { get { return Values[0]; } }

		// Update()로 호출 시점 값을 최신화
		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> Up { get { Update(); return up; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> Down { get { Update(); return down; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_MacdTurn[] cacheTQ_MacdTurn;
		public TeamQuant.TQ_MacdTurn TQ_MacdTurn(int fast, int slow, int smooth, int confirmBars, int atrPeriod, double minChangeAtr)
		{
			return TQ_MacdTurn(Input, fast, slow, smooth, confirmBars, atrPeriod, minChangeAtr);
		}

		public TeamQuant.TQ_MacdTurn TQ_MacdTurn(ISeries<double> input, int fast, int slow, int smooth, int confirmBars, int atrPeriod, double minChangeAtr)
		{
			if (cacheTQ_MacdTurn != null)
				for (int idx = 0; idx < cacheTQ_MacdTurn.Length; idx++)
					if (cacheTQ_MacdTurn[idx] != null && cacheTQ_MacdTurn[idx].Fast == fast && cacheTQ_MacdTurn[idx].Slow == slow && cacheTQ_MacdTurn[idx].Smooth == smooth && cacheTQ_MacdTurn[idx].ConfirmBars == confirmBars && cacheTQ_MacdTurn[idx].AtrPeriod == atrPeriod && cacheTQ_MacdTurn[idx].MinChangeAtr == minChangeAtr && cacheTQ_MacdTurn[idx].EqualsInput(input))
						return cacheTQ_MacdTurn[idx];
			return CacheIndicator<TeamQuant.TQ_MacdTurn>(new TeamQuant.TQ_MacdTurn(){ Fast = fast, Slow = slow, Smooth = smooth, ConfirmBars = confirmBars, AtrPeriod = atrPeriod, MinChangeAtr = minChangeAtr }, input, ref cacheTQ_MacdTurn);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_MacdTurn TQ_MacdTurn(int fast, int slow, int smooth, int confirmBars, int atrPeriod, double minChangeAtr)
		{
			return indicator.TQ_MacdTurn(Input, fast, slow, smooth, confirmBars, atrPeriod, minChangeAtr);
		}

		public Indicators.TeamQuant.TQ_MacdTurn TQ_MacdTurn(ISeries<double> input , int fast, int slow, int smooth, int confirmBars, int atrPeriod, double minChangeAtr)
		{
			return indicator.TQ_MacdTurn(input, fast, slow, smooth, confirmBars, atrPeriod, minChangeAtr);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_MacdTurn TQ_MacdTurn(int fast, int slow, int smooth, int confirmBars, int atrPeriod, double minChangeAtr)
		{
			return indicator.TQ_MacdTurn(Input, fast, slow, smooth, confirmBars, atrPeriod, minChangeAtr);
		}

		public Indicators.TeamQuant.TQ_MacdTurn TQ_MacdTurn(ISeries<double> input , int fast, int slow, int smooth, int confirmBars, int atrPeriod, double minChangeAtr)
		{
			return indicator.TQ_MacdTurn(input, fast, slow, smooth, confirmBars, atrPeriod, minChangeAtr);
		}
	}
}

#endregion
