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
	/// v3 추세 판단 (v3 지시서 3장, docs/interface.md). 입력 시리즈 = 추세 봉 (전략에서 TQ_Regime(BarsArray[regimeBip], ...)).
	/// 느린 점수 S(가격 vs 중기선, 구조, MACD선 부호, RSI)와 빠른 점수 F(단기 가격, MACD선 기울기)로
	/// 추세 +1 상승 / 0 횡보 / −1 하락을 정하고, 추세 중 F가 반대로 꺾이면 의심 이벤트를 낸다.
	/// 의심은 상태가 아니라 이벤트다: 추세 값은 그대로 두고 SuspectEvent에 한 번만 낸다.
	/// 데이터가 모자라면(장기선·MACD·RSI가 다 채워지기 전) Regime은 NaN이다.
	/// </summary>
	public class TQ_Regime : Indicator
	{
		private SMA				smaFast;
		private SMA				smaMid;
		private SMA				smaSlow;
		private MACD			macd;
		private RSI				rsi;
		private Series<double>	slowScore;
		private Series<double>	fastScore;
		private Series<int>		trendStart;
		private Series<int>		suspectEvent;
		private Series<int>		suspectActive;
		private Brush			upBrush;
		private Brush			downBrush;
		private Brush			upSuspectBrush;		// 상승추세 중 하락의심 (주황)
		private Brush			downSuspectBrush;	// 하락추세 중 상승의심 (하늘색)
		private int				trend;				// +1 상승추세, 0 횡보, −1 하락추세
		private int				suspect;			// 의심 중이면 그 방향: 상승추세 중 하락의심 −1, 하락추세 중 상승의심 +1, 없으면 0
		private string			scoreText	= "";
		private int				minBars;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "v3 추세 판단: 느린 점수 S + 빠른 점수 F → +1 상승 / 0 횡보 / −1 하락, 추세 중 의심 이벤트 (v3 지시서 3장)";
				Name						= "TQ_Regime";
				Calculate					= Calculate.OnBarClose;	// 추세 봉이 마감될 때마다 다시 계산
				IsOverlay					= true;		// 가격 패널에 얹는다 (ShowPlots가 꺼져 있으면 선은 투명)
				IsAutoScale					= false;	// 가격 패널 스케일을 건드리지 않는다
				IsSuspendedWhileInactive	= true;

				// v3 지시서 5-2 기본값
				Fast						= 6;
				Mid							= 20;
				Slow						= 60;
				ShowDebugVisuals			= false;
				MacdFast					= 12;
				MacdSlow					= 26;
				MacdSmooth					= 9;
				RsiPeriod					= 14;
				S2Mode						= 1;		// 1 = 중기선 > 장기선, 2 = 장기선이 SlopeBars봉 전보다 높음
				SlopeBars					= 5;
				UseRsi						= true;		// S4 RSI 항목 사용
				RsiBand						= 5;		// RSI가 50에서 이만큼 벗어나야 +1 / −1
				MacdSlopeBars				= 2;		// F2: MACD선이 몇 봉 연속 커져야(작아져야) 하는지
				TrendStartFast				= 1;		// 추세 시작: F ≥ 이 값 (하락은 ≤ −값). 2026-10-15 사용자 결정 2 → 1
				TrendStartSlow				= 1;		// 추세 시작: S ≥ 이 값. 2026-10-15 사용자 결정 2 → 1
				TrendEndSlow				= 0;		// 상승추세 끝: S ≤ 이 값 (하락추세 끝: S ≥ −값)
				SuspectFast					= 2;		// 의심: 추세 중 F가 반대로 이 값에 닿은 첫 봉
				DirectReverse				= false;	// 추세가 끝나는 봉에 F가 이미 반대로 ±2면 횡보를 건너뛰고 반대 추세로 (2026-10-15 사용자 결정, 기본 끔)
				ShowPlots					= false;	// 차트 표시용. 끄면 플롯 선을 안 그린다 (값·전략 동작은 그대로)

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.Gold, "Regime");
				AddPlot(Brushes.Gray, "SmaFast");
				AddPlot(Brushes.DodgerBlue, "SmaMid");
				AddPlot(Brushes.MediumPurple, "SmaSlow");
				AddPlot(Brushes.Orange, "MacdLine");
				AddPlot(Brushes.LightGreen, "Rsi");
			}
			else if (State == State.Configure)
			{
				// ShowPlots가 꺼져 있으면 플롯 선을 투명으로 바꿔 보이지만 않게 한다 (저장된 설정값이 적용된 뒤라 여기서)
				if (!ShowPlots)
					for (int i = 0; i < Plots.Length; i++)
						Plots[i].Brush = Brushes.Transparent;
			}
			else if (State == State.DataLoaded)
			{
				smaFast			= SMA(Fast);
				smaMid			= SMA(Mid);
				smaSlow			= SMA(Slow);
				macd			= MACD(MacdFast, MacdSlow, MacdSmooth);
				rsi				= RSI(RsiPeriod, 3);	// 두 번째 인자는 평균선용, RSI 값과 무관
				slowScore		= new Series<double>(this);
				fastScore		= new Series<double>(this);
				trendStart		= new Series<int>(this);
				suspectEvent	= new Series<int>(this);
				suspectActive	= new Series<int>(this);

				trend		= 0;
				suspect		= 0;
				scoreText	= "";

				// 참조하는 가장 먼 값: 장기선, MACD(장기 + 시그널), RSI 기간, 장기선 기울기, MACD선 기울기 연속 봉 수
				minBars		= Math.Max(Slow - 1, Math.Max(MacdSlow + MacdSmooth, Math.Max(RsiPeriod, Math.Max(SlopeBars, MacdSlopeBars + 1))));

				// 검증용 배경색. 직접 만든 브러시는 한 번만 만들고 Freeze한다 (main 색 그대로)
				upBrush				= new SolidColorBrush(Color.FromArgb(45, 0, 200, 0));
				downBrush			= new SolidColorBrush(Color.FromArgb(45, 230, 0, 0));
				upSuspectBrush		= new SolidColorBrush(Color.FromArgb(45, 255, 165, 0));
				downSuspectBrush	= new SolidColorBrush(Color.FromArgb(45, 0, 160, 255));
				upBrush.Freeze();
				downBrush.Freeze();
				upSuspectBrush.Freeze();
				downSuspectBrush.Freeze();
			}
		}

		protected override void OnBarUpdate()
		{
			// 표시·청산·기록용 값
			Values[1][0]	= smaFast[0];
			Values[2][0]	= smaMid[0];
			Values[3][0]	= smaSlow[0];
			Values[4][0]	= macd[0];
			Values[5][0]	= rsi[0];
			trendStart[0]		= 0;
			suspectEvent[0]		= 0;
			suspectActive[0]	= 0;

			// 데이터 가드: 장기선·MACD·RSI가 다 채워지기 전에는 추세를 내지 않는다
			if (CurrentBar < minBars)
			{
				Values[0][0]	= double.NaN;
				slowScore[0]	= 0;
				fastScore[0]	= 0;
				return;
			}

			double c	= Close[0];
			double f	= smaFast[0];
			double m	= smaMid[0];
			double s	= smaSlow[0];
			double ml	= macd[0];
			double r0	= rsi[0];

			// ── 느린 점수 S (3-1) ──
			int s1 = Sign(c - m);																		// S1 가격 vs 중기선
			int s2 = S2Mode == 1 ? Sign(m - s) : Sign(s - smaSlow[SlopeBars]);							// S2 구조: 중기 > 장기, 또는 장기선 기울기
			int s3 = Sign(ml);																			// S3 MACD선 부호
			int s4 = !UseRsi ? 0 : r0 >= 50 + RsiBand ? 1 : r0 <= 50 - RsiBand ? -1 : 0;				// S4 RSI

			// ── 빠른 점수 F (3-1) ──
			int f1 = c > f && f > smaFast[1] ? 1 : c < f && f < smaFast[1] ? -1 : 0;					// F1 단기 가격
			bool rising = true, falling = true;															// F2 MACD선 기울기: MacdSlopeBars봉 연속
			for (int j = 0; j < MacdSlopeBars; j++)
			{
				if (!(macd[j] > macd[j + 1]))	rising	= false;
				if (!(macd[j] < macd[j + 1]))	falling	= false;
			}
			int f2 = rising ? 1 : falling ? -1 : 0;

			int S = s1 + s2 + s3 + s4;
			int F = f1 + f2;
			slowScore[0]	= S;
			fastScore[0]	= F;
			scoreText		= "S1" + Arrow(s1) + " S2" + Arrow(s2) + " S3" + Arrow(s3) + (UseRsi ? " S4" + Arrow(s4) : "")
								+ " │ F1" + Arrow(f1) + " F2" + Arrow(f2);

			// ── 상태 전이 (3-2, 상승 기준이고 하락은 거울상) ──
			//  · 추세 아님 → F ≥ TrendStartFast AND S ≥ TrendStartSlow 이면 상승추세 (그 봉 = 추세 시작 봉)
			//  · 상승추세 → 하락추세 시작 조건이면 바로 하락추세, 아니고 S ≤ TrendEndSlow 이면 횡보
			//  · 확정 연속 봉 수는 두지 않는다 (흔들림은 시작 S ≥ 2 / 종료 S ≤ 0 기준 차이로 막는다)
			bool startUp	= F >= TrendStartFast && S >= TrendStartSlow;
			bool startDn	= F <= -TrendStartFast && S <= -TrendStartSlow;
			int prev		= trend;
			//  · DirectReverse: 추세가 끝나는 봉(S가 종료 기준에 닿음)에 F가 이미 반대로 ±2(최대)면 횡보 없이 반대 추세로.
			//    S는 끝 기준(0)에 먼저 닿고 시작 기준(±1 이상)은 그 뒤라, 이게 없으면 반전은 거의 항상 횡보를 거친다
			//    (7/15~9/10 21일: 반대 전환 27번 중 22번이 횡보를 거침. 재현: 횡보 판정 8 → 5%, 추세 전환 35 → 50번)
			if (trend == 1)
			{
				if (startDn)					trend = -1;
				else if (S <= TrendEndSlow)		trend = DirectReverse && F <= -2 ? -1 : 0;
			}
			else if (trend == -1)
			{
				if (startUp)					trend = 1;
				else if (S >= -TrendEndSlow)	trend = DirectReverse && F >= 2 ? 1 : 0;
			}
			else
			{
				if (startUp)					trend = 1;
				else if (startDn)				trend = -1;
			}
			if (trend != prev && trend != 0)
				trendStart[0] = trend;

			// ── 의심 이벤트 (3-2) ──
			// 추세가 바뀌면 의심을 지운다. 추세 중 F가 반대로 SuspectFast에 닿은 첫 봉에서 이벤트 한 번, F가 0 이상(하락추세는 0 이하)으로 돌아오면 해제.
			// 해제된 뒤 다시 닿으면 다시 이벤트
			if (trend != prev || trend == 0)
				suspect = 0;
			if (trend != 0)
			{
				if (suspect == 0 && F * trend <= -SuspectFast)
				{
					suspect			= -trend;
					suspectEvent[0]	= suspect;
				}
				else if (suspect != 0 && F * trend >= 0)
					suspect = 0;
			}
			suspectActive[0]	= suspect;
			Values[0][0]		= trend;

			if (trendStart[0] != 0 || trend != prev || suspectEvent[0] != 0)
				Print(string.Format("[{0}][{1}][TQ_Regime] {2}{3} S={4:+0;-0;0} F={5:+0;-0;0} ({6})",
					Time[0], Instrument.FullName,
					trend != prev ? "추세 " + StateName(prev) + " → " + StateName(trend) : StateName(trend),
					suspectEvent[0] != 0 ? (suspectEvent[0] < 0 ? " · 하락의심 이벤트" : " · 상승의심 이벤트") : "",
					S, F, scoreText));

			if (ShowDebugVisuals)
				BackBrushAll = trend == 1 ? (suspect != 0 ? upSuspectBrush : upBrush)
					: trend == -1 ? (suspect != 0 ? downSuspectBrush : downBrush) : null;
		}

		private static int Sign(double v)
		{
			return v > 0 ? 1 : v < 0 ? -1 : 0;
		}

		private static string Arrow(int v)
		{
			return v > 0 ? "▲" : v < 0 ? "▼" : "−";
		}

		private static string StateName(int state)
		{
			return state == 1 ? "상승추세" : state == -1 ? "하락추세" : "횡보";
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fast", Description = "단기 이동평균 (F1)", GroupName = "Parameters", Order = 0)]
		public int Fast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Mid", Description = "중기 이동평균 (S1, S2 방식 1)", GroupName = "Parameters", Order = 1)]
		public int Mid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Slow", Description = "장기 이동평균 (S2)", GroupName = "Parameters", Order = 2)]
		public int Slow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 배경색: 상승 초록, 하락 빨강, 의심 중 주황(하락의심)·하늘색(상승의심)", GroupName = "Parameters", Order = 3)]
		public bool ShowDebugVisuals { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdFast", Description = "MACD 단기", GroupName = "Parameters", Order = 4)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSlow", Description = "MACD 장기", GroupName = "Parameters", Order = 5)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSmooth", Description = "MACD 시그널", GroupName = "Parameters", Order = 6)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간 (S4)", GroupName = "Parameters", Order = 7)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "S2Mode", Description = "S2 구조 항목: 1 = 중기선 > 장기선, 2 = 장기선이 SlopeBars봉 전보다 높음", GroupName = "Parameters", Order = 8)]
		public int S2Mode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SlopeBars", Description = "S2 방식 2: 장기선 기울기를 몇 봉 전과 비교할지", GroupName = "Parameters", Order = 9)]
		public int SlopeBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseRsi", Description = "S4 RSI 항목 사용. 끄면 S는 −3 ~ +3", GroupName = "Parameters", Order = 10)]
		public bool UseRsi { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "RsiBand", Description = "S4: RSI가 50 + 이 값 이상이면 +1, 50 − 이 값 이하면 −1", GroupName = "Parameters", Order = 11)]
		public double RsiBand { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSlopeBars", Description = "F2: MACD선이 몇 봉 연속 커지면 +1, 작아지면 −1", GroupName = "Parameters", Order = 12)]
		public int MacdSlopeBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2)]
		[Display(Name = "TrendStartFast", Description = "추세 시작: F ≥ 이 값 (하락추세는 F ≤ −이 값)", GroupName = "Parameters", Order = 13)]
		public int TrendStartFast { get; set; }

		[NinjaScriptProperty]
		[Range(0, 4)]
		[Display(Name = "TrendStartSlow", Description = "추세 시작: S ≥ 이 값 (하락추세는 S ≤ −이 값)", GroupName = "Parameters", Order = 14)]
		public int TrendStartSlow { get; set; }

		[NinjaScriptProperty]
		[Range(-4, 4)]
		[Display(Name = "TrendEndSlow", Description = "상승추세 끝: S ≤ 이 값 (하락추세 끝: S ≥ −이 값)", GroupName = "Parameters", Order = 15)]
		public int TrendEndSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "SuspectFast", Description = "의심 이벤트: 상승추세 중 F ≤ −이 값이 된 첫 봉 (하락추세는 F ≥ 이 값)", GroupName = "Parameters", Order = 16)]
		public int SuspectFast { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "DirectReverse", Description = "추세가 끝나는 봉에 F가 이미 반대로 ±2면 횡보 없이 반대 추세로 (기본 끔)", GroupName = "Parameters", Order = 17)]
		public bool DirectReverse { get; set; }

		// 표시 전용 토글. [NinjaScriptProperty]를 붙이지 않아 생성자 시그니처가 바뀌지 않는다
		[Display(Name = "ShowPlots", Description = "플롯 선 표시. 끄면 차트에서 선이 안 보임(값·전략 동작은 그대로)", GroupName = "Parameters", Order = 18)]
		public bool ShowPlots { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Regime { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SmaFast { get { return Values[1]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SmaMid { get { return Values[2]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SmaSlow { get { return Values[3]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> MacdLine { get { return Values[4]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Rsi { get { return Values[5]; } }

		// 느린 점수 S (−4 ~ +4, RSI를 끄면 −3 ~ +3)
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SlowScore { get { Update(); return slowScore; } }

		// 빠른 점수 F (−2 ~ +2)
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> FastScore { get { Update(); return fastScore; } }

		// 이 봉에서 추세가 시작되면 ±1, 아니면 0
		[Browsable(false)]
		[XmlIgnore]
		public Series<int> TrendStart { get { Update(); return trendStart; } }

		// 이 봉에서 의심이 생기면 상승추세 중 하락의심 −1, 하락추세 중 상승의심 +1, 아니면 0
		[Browsable(false)]
		[XmlIgnore]
		public Series<int> SuspectEvent { get { Update(); return suspectEvent; } }

		// 의심 중이면 그 방향(−1 / +1), 아니면 0 (표시용)
		[Browsable(false)]
		[XmlIgnore]
		public Series<int> SuspectActive { get { Update(); return suspectActive; } }

		// 상태 상자·로그용: "S1▲ S2▲ S3− S4▲ │ F1▼ F2▼"
		[Browsable(false)]
		[XmlIgnore]
		public string ScoreText { get { Update(); return scoreText; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_Regime[] cacheTQ_Regime;
		public TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int rsiPeriod, int s2Mode, int slopeBars, bool useRsi, double rsiBand, int macdSlopeBars, int trendStartFast, int trendStartSlow, int trendEndSlow, int suspectFast, bool directReverse)
		{
			return TQ_Regime(Input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, rsiPeriod, s2Mode, slopeBars, useRsi, rsiBand, macdSlopeBars, trendStartFast, trendStartSlow, trendEndSlow, suspectFast, directReverse);
		}

		public TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input, int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int rsiPeriod, int s2Mode, int slopeBars, bool useRsi, double rsiBand, int macdSlopeBars, int trendStartFast, int trendStartSlow, int trendEndSlow, int suspectFast, bool directReverse)
		{
			if (cacheTQ_Regime != null)
				for (int idx = 0; idx < cacheTQ_Regime.Length; idx++)
					if (cacheTQ_Regime[idx] != null && cacheTQ_Regime[idx].Fast == fast && cacheTQ_Regime[idx].Mid == mid && cacheTQ_Regime[idx].Slow == slow && cacheTQ_Regime[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Regime[idx].MacdFast == macdFast && cacheTQ_Regime[idx].MacdSlow == macdSlow && cacheTQ_Regime[idx].MacdSmooth == macdSmooth && cacheTQ_Regime[idx].RsiPeriod == rsiPeriod && cacheTQ_Regime[idx].S2Mode == s2Mode && cacheTQ_Regime[idx].SlopeBars == slopeBars && cacheTQ_Regime[idx].UseRsi == useRsi && cacheTQ_Regime[idx].RsiBand == rsiBand && cacheTQ_Regime[idx].MacdSlopeBars == macdSlopeBars && cacheTQ_Regime[idx].TrendStartFast == trendStartFast && cacheTQ_Regime[idx].TrendStartSlow == trendStartSlow && cacheTQ_Regime[idx].TrendEndSlow == trendEndSlow && cacheTQ_Regime[idx].SuspectFast == suspectFast && cacheTQ_Regime[idx].DirectReverse == directReverse && cacheTQ_Regime[idx].EqualsInput(input))
						return cacheTQ_Regime[idx];
			return CacheIndicator<TeamQuant.TQ_Regime>(new TeamQuant.TQ_Regime(){ Fast = fast, Mid = mid, Slow = slow, ShowDebugVisuals = showDebugVisuals, MacdFast = macdFast, MacdSlow = macdSlow, MacdSmooth = macdSmooth, RsiPeriod = rsiPeriod, S2Mode = s2Mode, SlopeBars = slopeBars, UseRsi = useRsi, RsiBand = rsiBand, MacdSlopeBars = macdSlopeBars, TrendStartFast = trendStartFast, TrendStartSlow = trendStartSlow, TrendEndSlow = trendEndSlow, SuspectFast = suspectFast, DirectReverse = directReverse }, input, ref cacheTQ_Regime);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int rsiPeriod, int s2Mode, int slopeBars, bool useRsi, double rsiBand, int macdSlopeBars, int trendStartFast, int trendStartSlow, int trendEndSlow, int suspectFast, bool directReverse)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, rsiPeriod, s2Mode, slopeBars, useRsi, rsiBand, macdSlopeBars, trendStartFast, trendStartSlow, trendEndSlow, suspectFast, directReverse);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int rsiPeriod, int s2Mode, int slopeBars, bool useRsi, double rsiBand, int macdSlopeBars, int trendStartFast, int trendStartSlow, int trendEndSlow, int suspectFast, bool directReverse)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, rsiPeriod, s2Mode, slopeBars, useRsi, rsiBand, macdSlopeBars, trendStartFast, trendStartSlow, trendEndSlow, suspectFast, directReverse);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int rsiPeriod, int s2Mode, int slopeBars, bool useRsi, double rsiBand, int macdSlopeBars, int trendStartFast, int trendStartSlow, int trendEndSlow, int suspectFast, bool directReverse)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, rsiPeriod, s2Mode, slopeBars, useRsi, rsiBand, macdSlopeBars, trendStartFast, trendStartSlow, trendEndSlow, suspectFast, directReverse);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int rsiPeriod, int s2Mode, int slopeBars, bool useRsi, double rsiBand, int macdSlopeBars, int trendStartFast, int trendStartSlow, int trendEndSlow, int suspectFast, bool directReverse)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, rsiPeriod, s2Mode, slopeBars, useRsi, rsiBand, macdSlopeBars, trendStartFast, trendStartSlow, trendEndSlow, suspectFast, directReverse);
		}
	}
}

#endregion
