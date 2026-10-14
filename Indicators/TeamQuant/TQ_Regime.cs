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
	/// SMA Slow가 채워지기 전(봉 수 부족)에는 Regime이 NaN이다.
	/// </summary>
	public class TQ_Regime : Indicator
	{
		private SMA		smaFast;
		private SMA		smaMid;
		private SMA		smaSlow;
		private MAX		maxHigh;
		private MIN		minLow;
		private Brush	upBrush;
		private Brush	downBrush;
		private int		lastRegime;

		// 점수 방식 (Mode 2, spec 3-1). main의 TQ_Regime 점수 방식을 이 버전의 3단계(+1/0/−1)에 옮긴 것
		private TQ_MacdTurn		macdTurn;
		private RSI				rsi;
		private MIN				stochLow;	// 스토캐스틱은 TQ_Signals·TQ_Stochastic과 같은 식으로 직접 계산한다
		private MAX				stochHigh;
		private SMA				stochK;
		private SMA				stochD;
		private Series<double>	stochRawK;
		private Series<double>	kSeries;
		private int				minBars;
		private int				trendDir;		// 점수 방식의 현재 추세 방향: +1 상승, −1 하락, 0 없음
		private int				lastEventDir;	// 가장 최근 사건의 방향: +1 상승 쪽, −1 하락 쪽
		private int				lastEventBar;	// 그 사건이 나온 봉 번호
		private int				score;			// 점수 합계 (−5 ~ +5)
		private string			scoreText;		// 로그용: 항목별 방향

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "10분봉 레짐 (+1 상승 / −1 하락 / 0 횡보)과 10분봉 SMA20 (spec 3장)";
				Name						= "TQ_Regime";
				Calculate					= Calculate.OnBarClose;	// spec 3장: 10분봉 마감 때마다 다시 계산
				IsOverlay					= true;		// 가격 패널에 얹어 별도 패널을 만들지 않는다(ShowPlots 꺼지면 투명이라 안 보임)
				IsAutoScale					= false;	// 오버레이로 올라가도 가격 패널 스케일을 건드리지 않게 한다(Regime −1~1, Sma20 가격)
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				Fast						= 6;
				Mid							= 18;
				Slow						= 36;
				UseRule12					= true;		// spec 3장 1.2·2.2
				ShowDebugVisuals			= false;
				ShowPlots					= false;	// 차트 표시용. 끄면 플롯 선을 안 그린다(값은 그대로, 전략 동작 무관)

				// 레짐 전환을 빠르게 하는 옵션. 기본값은 운용 기본값이다 (spec 8장). Fast/Mid/Slow도 6/18/36(명세 20/60/120)
				Rule12Bars					= 3;		// 1.2·2.2의 비교 구간. 명세는 5 (최근 5봉 vs 그 전 5봉)
				Rule12PriceFilter			= true;
				FastTrendBars				= 2;		// 0 = 사용 안 함
				FastTrendSlope				= false;

				// 추세 판단 방식 (spec 3-1). 1 = 위의 이동평균·고저점 규칙, 2 = 점수 방식
				Mode						= 1;
				ScoreMin					= 2;
				ScoreRsiBand				= 5;
				ScoreEventBars				= 3;
				ScoreExit					= 0;
				MacdFast					= 12;
				MacdSlow					= 26;
				MacdSmooth					= 9;
				HistBars					= 2;
				RsiPeriod					= 14;
				StochPeriodK				= 10;
				StochSmooth					= 5;
				StochPeriodD				= 5;
				CrossLow					= 20;
				CrossHigh					= 80;
				CrossFilterBars				= 5;
				DivBars						= 15;
				PivotStrength				= 2;

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.Gold, "Regime");
				AddPlot(Brushes.Gray, "Sma20");
			}
			else if (State == State.Configure)
			{
				// ShowPlots가 꺼져 있으면 플롯 선을 투명으로 바꿔 시각적으로만 숨긴다.
				// SetDefaults가 아니라 여기서 하는 이유: 저장된 설정값이 적용된 뒤라 사용자가 켠 값이 반영된다
				if (!ShowPlots)
					for (int i = 0; i < Plots.Length; i++)
						Plots[i].Brush = Brushes.Transparent;
			}
			else if (State == State.DataLoaded)
			{
				smaFast		= SMA(Fast);
				smaMid		= SMA(Mid);
				smaSlow		= SMA(Slow);
				maxHigh		= MAX(High, Rule12Bars);
				minLow		= MIN(Low, Rule12Bars);
				lastRegime	= int.MinValue;

				// 점수 방식용 지표. Mode 1에서도 만들지만 값은 쓰지 않는다
				macdTurn	= TQ_MacdTurn(MacdFast, MacdSlow, MacdSmooth, HistBars, 14, 0);
				rsi			= RSI(RsiPeriod, 3);	// 두 번째 인자는 평균선용, RSI 값과 무관
				stochRawK	= new Series<double>(this);
				kSeries		= new Series<double>(this);
				stochLow	= MIN(Low, StochPeriodK);
				stochHigh	= MAX(High, StochPeriodK);
				stochK		= SMA(stochRawK, StochSmooth);
				stochD		= SMA(kSeries, StochPeriodD);
				trendDir		= 0;
				lastEventDir	= 0;
				lastEventBar	= -1;
				score			= 0;
				scoreText		= "";
				// 참조하는 가장 먼 인덱스: 이동평균 Slow, 다이버전스 DivBars + PivotStrength봉 전, 크로스 필터, MACD 연속 봉
				minBars		= Math.Max(Slow - 1, Math.Max(DivBars + PivotStrength, Math.Max(CrossFilterBars, HistBars + 1)));

				// 검증용 배경색. 직접 만든 브러시는 한 번만 만들고 Freeze한다
				upBrush		= new SolidColorBrush(Color.FromArgb(45, 0, 200, 0));
				downBrush	= new SolidColorBrush(Color.FromArgb(45, 230, 0, 0));
				upBrush.Freeze();
				downBrush.Freeze();
			}
		}

		protected override void OnBarUpdate()
		{
			Sma20[0] = smaFast[0];	// spec 5.2 1차 익절용 10분봉 SMA20

			// 스토캐스틱은 가드 전에 매 봉 채운다 (SMA 입력 시리즈라 빈 봉이 생기면 안 됨)
			double stochLL		= stochLow[0];
			double stochRange	= stochHigh[0] - stochLL;
			// 범위가 0이면(고가=저가) 직전 값 유지
			stochRawK[0]	= stochRange == 0 ? (CurrentBar == 0 ? 50 : stochRawK[1]) : 100 * (Close[0] - stochLL) / stochRange;
			kSeries[0]		= stochK[0];

			// 데이터 부족 가드: SMA Slow가 채워지기 전에는 레짐을 내지 않는다 (점수 방식은 다이버전스·크로스 구간까지)
			if (CurrentBar < (Mode == 2 ? minBars : Slow - 1))
			{
				Regime[0] = double.NaN;
				return;
			}

			double f = smaFast[0];
			double m = smaMid[0];
			double s = smaSlow[0];

			int r = 0;									// spec 3장 (3.1) 횡보
			if (Mode == 2)
				r = ScoreRegime(f);						// spec 3-1 점수 방식
			else if (f > m && m > s && Close[0] > f)
				r = 1;									// spec 3장 (1.1) 상승추세
			else if (f < m && m < s && Close[0] < f)
				r = -1;									// spec 3장 (2.1) 하락추세
			else
			{
				// 실험 (spec 3장 1.3·2.3 자리, 미확정): 정배열·역배열을 기다리지 않고,
				// 종가가 FastTrendBars봉 연속 SMA Fast 위에서 마감하면 상승추세, 연속 아래면 하락추세로 본다
				if (FastTrendBars > 0 && CurrentBar >= FastTrendBars - 1)
				{
					bool allAbove = true;
					bool allBelow = true;
					for (int i = 0; i < FastTrendBars; i++)
					{
						if (!(Close[i] > smaFast[i]))	allAbove = false;
						if (!(Close[i] < smaFast[i]))	allBelow = false;
					}
					// 실험: 단기선이 FastTrendBars봉 전보다 오르는(내리는) 중일 때만 인정한다. 가격이 선을 잠깐 넘나드는 것을 거른다
					if (FastTrendSlope && CurrentBar >= FastTrendBars)
					{
						allAbove = allAbove && smaFast[0] > smaFast[FastTrendBars];
						allBelow = allBelow && smaFast[0] < smaFast[FastTrendBars];
					}

					if (allAbove)		r = 1;
					else if (allBelow)	r = -1;
				}

				if (r == 0 && UseRule12 && CurrentBar >= Rule12Bars * 2 - 1)
				{
					// spec 3장 (1.2)(2.2): 최근 Rule12Bars봉의 최고 고가·최저 저가를 그 전 Rule12Bars봉과 비교.
					// 이동평균 조건(1.1·2.1)이 우선이라 여기는 둘 다 아닐 때만 온다
					bool up		= maxHigh[0] > maxHigh[Rule12Bars] && minLow[0] > minLow[Rule12Bars];
					bool down	= maxHigh[0] < maxHigh[Rule12Bars] && minLow[0] < minLow[Rule12Bars];

					// 실험: 종가가 SMA Fast의 맞는 쪽에 있을 때만 인정한다.
					// 가격이 이미 20선을 되찾았는데 지난 구간의 낮은 고점·저점 때문에 하락추세로 남는 것을 막는다
					if (Rule12PriceFilter)
					{
						up		= up && Close[0] > f;
						down	= down && Close[0] < f;
					}

					if (up)			r = 1;
					else if (down)	r = -1;
				}
			}

			Regime[0] = r;

			if (r != lastRegime)
			{
				Print(string.Format("[{0}][{1}][TQ_Regime] 레짐 전환 {2} → {3}{4}",
					Time[0], Instrument.FullName, lastRegime == int.MinValue ? "없음" : lastRegime.ToString(), r,
					Mode == 2 ? string.Format(" (점수={0} {1})", score, scoreText) : ""));
				lastRegime = r;
			}

			if (ShowDebugVisuals)
				BackBrushAll = r > 0 ? upBrush : r < 0 ? downBrush : null;
		}

		// spec 3-1 점수 방식 (main의 TQ_Regime Mode 2와 같은 식). 이 버전은 의심 상태가 없어 +1 / 0 / −1만 낸다
		private int ScoreRegime(double f)
		{
			double k0	= kSeries[0];
			double d0	= stochD[0];
			double r0	= rsi[0];

			// MACD 전환: 히스토그램이 HistBars봉 연속 증가(감소)한 것이 처음 확인된 봉 하나에서만 참 (spec 2장)
			bool histUp		= macdTurn.Up[0] && !macdTurn.Up[1];
			bool histDown	= macdTurn.Down[0] && !macdTurn.Down[1];

			// 골든·데드크로스 (spec 2장): %K·%D 교차 AND 최근 CrossFilterBars봉 안에 %K가 CrossLow 아래 / CrossHigh 위
			bool kWasLow	= false;
			bool kWasHigh	= false;
			for (int i = 0; i < CrossFilterBars; i++)
			{
				if (kSeries[i] < CrossLow)	kWasLow = true;
				if (kSeries[i] > CrossHigh)	kWasHigh = true;
			}
			bool golden	= kSeries[1] <= stochD[1] && k0 > d0 && kWasLow;
			bool dead	= kSeries[1] >= stochD[1] && k0 < d0 && kWasHigh;

			// 다이버전스 (spec 3장): 최근 DivBars봉 안의 확연한 고점(좌우 PivotStrength봉보다 높은 봉) 중 가장 높은 것과 비교.
			// 지금 고가가 더 높은데 RSI는 낮으면 하락 다이버전스. 상승 쪽은 저점으로 거울상
			int hiIdx = -1, loIdx = -1;
			for (int i = PivotStrength; i <= DivBars; i++)
			{
				bool pivotHigh	= true;
				bool pivotLow	= true;
				for (int j = 1; j <= PivotStrength; j++)
				{
					if (!(High[i] > High[i - j] && High[i] > High[i + j]))	pivotHigh = false;
					if (!(Low[i] < Low[i - j] && Low[i] < Low[i + j]))		pivotLow = false;
				}
				if (pivotHigh && (hiIdx < 0 || High[i] > High[hiIdx]))	hiIdx = i;
				if (pivotLow && (loIdx < 0 || Low[i] < Low[loIdx]))		loIdx = i;
			}
			bool bearDiv = hiIdx >= 0 && High[0] > High[hiIdx] && r0 < rsi[hiIdx];
			bool bullDiv = loIdx >= 0 && Low[0] < Low[loIdx] && r0 > rsi[loIdx];

			// 가장 최근 사건의 방향. 한 봉에 양쪽이 같이 나오면 사건으로 치지 않는다
			bool bullEvent = histUp || golden || bullDiv;
			bool bearEvent = histDown || dead || bearDiv;
			if (bullEvent != bearEvent)
			{
				lastEventDir	= bullEvent ? 1 : -1;
				lastEventBar	= CurrentBar;
			}

			// 항목별 +1 / −1 / 0 (spec 3-1 표)
			//  ① 가격: 종가 > 단기·중기·장기선 AND 단기선이 직전 봉보다 높음 (2026-10-14 변경: 단기선 하나만 보던 것에 중기·장기선 추가)
			//  ② MACD: MACD선 > 0 AND MACD선이 직전 봉보다 줄지 않음 (2026-10-14 변경: 히스토그램은 진입 타점용이라 큰 추세는 MACD선으로 봄)
			//  ③ RSI: 50 ± ScoreRsiBand
			//  ④ 스토캐스틱: %K > %D AND %K > 50
			//  ⑤ 사건: 최근 ScoreEventBars봉 안의 가장 최근 사건의 방향. 0이면 쓰지 않음
			double m		= smaMid[0];
			double s		= smaSlow[0];
			double macd		= macdTurn.MacdLine[0];
			double macd1	= macdTurn.MacdLine[1];
			int cMa		= Close[0] > f && Close[0] > m && Close[0] > s && f > smaFast[1] ? 1
						: Close[0] < f && Close[0] < m && Close[0] < s && f < smaFast[1] ? -1 : 0;
			int cMacd	= macd > 0 && macd >= macd1 ? 1 : macd < 0 && macd <= macd1 ? -1 : 0;
			int cRsi	= r0 >= 50 + ScoreRsiBand ? 1 : r0 <= 50 - ScoreRsiBand ? -1 : 0;
			int cSto	= k0 > d0 && k0 > 50 ? 1 : k0 < d0 && k0 < 50 ? -1 : 0;
			int cEvt	= ScoreEventBars > 0 && lastEventBar >= 0 && CurrentBar - lastEventBar < ScoreEventBars ? lastEventDir : 0;
			score		= cMa + cMacd + cRsi + cSto + cEvt;
			scoreText	= "가격" + Arrow(cMa) + " MACD" + Arrow(cMacd) + " RSI" + Arrow(cRsi) + " 스토" + Arrow(cSto)
							+ (ScoreEventBars > 0 ? " 사건" + Arrow(cEvt) : "");

			// 끝나는 기준(ScoreExit)을 시작 기준(ScoreMin)보다 느슨하게 둬서 지표 하나가 흔들릴 때마다 추세가 켜졌다 꺼지지 않게 한다
			if (trendDir == 1 && score <= ScoreExit)		trendDir = 0;
			if (trendDir == -1 && score >= -ScoreExit)	trendDir = 0;
			// 시작: 가격 항목이 같은 방향 AND 합계가 ScoreMin 이상
			if (trendDir != 1 && cMa > 0 && score >= ScoreMin)			trendDir = 1;
			else if (trendDir != -1 && cMa < 0 && score <= -ScoreMin)	trendDir = -1;

			return trendDir;
		}

		private string Arrow(int v)
		{
			return v > 0 ? "▲" : v < 0 ? "▼" : "−";
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
		[Display(Name = "UseRule12", Description = "spec 3장 1.2·2.2 사용", GroupName = "Parameters", Order = 3)]
		public bool UseRule12 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 배경색 표시", GroupName = "Parameters", Order = 4)]
		public bool ShowDebugVisuals { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Rule12Bars", Description = "1.2·2.2 비교 구간 봉 수 (최근 N봉 vs 그 전 N봉)", GroupName = "Parameters", Order = 5)]
		public int Rule12Bars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Rule12PriceFilter", Description = "실험: 1.2·2.2를 종가가 SMA Fast의 맞는 쪽에 있을 때만 인정", GroupName = "Parameters", Order = 6)]
		public bool Rule12PriceFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "FastTrendBars", Description = "실험: 종가가 N봉 연속 SMA Fast 위(아래)면 상승(하락)추세. 0 = 사용 안 함", GroupName = "Parameters", Order = 7)]
		public int FastTrendBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "FastTrendSlope", Description = "실험: FastTrendBars 판정에 SMA Fast의 기울기 조건 추가 (N봉 전보다 높아야 상승, 낮아야 하락)", GroupName = "Parameters", Order = 8)]
		public bool FastTrendSlope { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "Mode", Description = "추세 판단 방식. 1 = 이동평균·고저점 규칙(위 설정), 2 = 점수 방식(가격·MACD·RSI·스토캐스틱·사건, spec 3-1). 2면 UseRule12·Rule12·FastTrend 설정은 쓰지 않음", GroupName = "Parameters", Order = 10)]
		public int Mode { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "ScoreMin", Description = "점수 방식: 추세로 인정할 최소 점수", GroupName = "Parameters", Order = 11)]
		public int ScoreMin { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "ScoreRsiBand", Description = "점수 방식: RSI가 50에서 이만큼 벗어나야 방향으로 인정", GroupName = "Parameters", Order = 12)]
		public double ScoreRsiBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "ScoreEventBars", Description = "점수 방식: 최근 몇 봉 안의 사건(MACD 전환, 골든·데드크로스, 다이버전스)을 점수에 넣을지. 0 = 안 넣음", GroupName = "Parameters", Order = 13)]
		public int ScoreEventBars { get; set; }

		[NinjaScriptProperty]
		[Range(-5, 5)]
		[Display(Name = "ScoreExit", Description = "점수 방식: 상승추세는 점수가 이 값 이하로 내려가면 끝난다 (하락추세는 부호 반대)", GroupName = "Parameters", Order = 14)]
		public int ScoreExit { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdFast", Description = "MACD 단기", GroupName = "Parameters", Order = 15)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSlow", Description = "MACD 장기", GroupName = "Parameters", Order = 16)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSmooth", Description = "MACD 시그널", GroupName = "Parameters", Order = 17)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "HistBars", Description = "MACD 히스토그램이 몇 봉 연속 늘어야(줄어야) 전환으로 볼지", GroupName = "Parameters", Order = 18)]
		public int HistBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간", GroupName = "Parameters", Order = 19)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodK", Description = "스토캐스틱 %K 길이", GroupName = "Parameters", Order = 20)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochSmooth", Description = "스토캐스틱 %K 스무딩", GroupName = "Parameters", Order = 21)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodD", Description = "스토캐스틱 %D 스무딩", GroupName = "Parameters", Order = 22)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossLow", Description = "골든크로스 필터 기준", GroupName = "Parameters", Order = 23)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossHigh", Description = "데드크로스 필터 기준", GroupName = "Parameters", Order = 24)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "CrossFilterBars", Description = "크로스 필터 봉 수 (현재 봉 포함)", GroupName = "Parameters", Order = 25)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivBars", Description = "다이버전스: 최근 몇 봉 안의 고점·저점과 비교할지", GroupName = "Parameters", Order = 26)]
		public int DivBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "PivotStrength", Description = "고점·저점으로 인정할 좌우 봉 수", GroupName = "Parameters", Order = 27)]
		public int PivotStrength { get; set; }

		// 표시 전용 토글. [NinjaScriptProperty]를 붙이지 않아 생성자 시그니처가 바뀌지 않는다(전략 호출부·생성 코드 그대로)
		[Display(Name = "ShowPlots", Description = "플롯 선 표시. 끄면 차트에서 선이 안 보임(값·전략 동작은 그대로)", GroupName = "Parameters", Order = 28)]
		public bool ShowPlots { get; set; }

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
		public TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool useRule12, bool showDebugVisuals, int rule12Bars, bool rule12PriceFilter, int fastTrendBars, bool fastTrendSlope, int mode, int scoreMin, double scoreRsiBand, int scoreEventBars, int scoreExit, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, int divBars, int pivotStrength)
		{
			return TQ_Regime(Input, fast, mid, slow, useRule12, showDebugVisuals, rule12Bars, rule12PriceFilter, fastTrendBars, fastTrendSlope, mode, scoreMin, scoreRsiBand, scoreEventBars, scoreExit, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, divBars, pivotStrength);
		}

		public TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input, int fast, int mid, int slow, bool useRule12, bool showDebugVisuals, int rule12Bars, bool rule12PriceFilter, int fastTrendBars, bool fastTrendSlope, int mode, int scoreMin, double scoreRsiBand, int scoreEventBars, int scoreExit, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, int divBars, int pivotStrength)
		{
			if (cacheTQ_Regime != null)
				for (int idx = 0; idx < cacheTQ_Regime.Length; idx++)
					if (cacheTQ_Regime[idx] != null && cacheTQ_Regime[idx].Fast == fast && cacheTQ_Regime[idx].Mid == mid && cacheTQ_Regime[idx].Slow == slow && cacheTQ_Regime[idx].UseRule12 == useRule12 && cacheTQ_Regime[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Regime[idx].Rule12Bars == rule12Bars && cacheTQ_Regime[idx].Rule12PriceFilter == rule12PriceFilter && cacheTQ_Regime[idx].FastTrendBars == fastTrendBars && cacheTQ_Regime[idx].FastTrendSlope == fastTrendSlope && cacheTQ_Regime[idx].Mode == mode && cacheTQ_Regime[idx].ScoreMin == scoreMin && cacheTQ_Regime[idx].ScoreRsiBand == scoreRsiBand && cacheTQ_Regime[idx].ScoreEventBars == scoreEventBars && cacheTQ_Regime[idx].ScoreExit == scoreExit && cacheTQ_Regime[idx].MacdFast == macdFast && cacheTQ_Regime[idx].MacdSlow == macdSlow && cacheTQ_Regime[idx].MacdSmooth == macdSmooth && cacheTQ_Regime[idx].HistBars == histBars && cacheTQ_Regime[idx].RsiPeriod == rsiPeriod && cacheTQ_Regime[idx].StochPeriodK == stochPeriodK && cacheTQ_Regime[idx].StochSmooth == stochSmooth && cacheTQ_Regime[idx].StochPeriodD == stochPeriodD && cacheTQ_Regime[idx].CrossLow == crossLow && cacheTQ_Regime[idx].CrossHigh == crossHigh && cacheTQ_Regime[idx].CrossFilterBars == crossFilterBars && cacheTQ_Regime[idx].DivBars == divBars && cacheTQ_Regime[idx].PivotStrength == pivotStrength && cacheTQ_Regime[idx].EqualsInput(input))
						return cacheTQ_Regime[idx];
			return CacheIndicator<TeamQuant.TQ_Regime>(new TeamQuant.TQ_Regime(){ Fast = fast, Mid = mid, Slow = slow, UseRule12 = useRule12, ShowDebugVisuals = showDebugVisuals, Rule12Bars = rule12Bars, Rule12PriceFilter = rule12PriceFilter, FastTrendBars = fastTrendBars, FastTrendSlope = fastTrendSlope, Mode = mode, ScoreMin = scoreMin, ScoreRsiBand = scoreRsiBand, ScoreEventBars = scoreEventBars, ScoreExit = scoreExit, MacdFast = macdFast, MacdSlow = macdSlow, MacdSmooth = macdSmooth, HistBars = histBars, RsiPeriod = rsiPeriod, StochPeriodK = stochPeriodK, StochSmooth = stochSmooth, StochPeriodD = stochPeriodD, CrossLow = crossLow, CrossHigh = crossHigh, CrossFilterBars = crossFilterBars, DivBars = divBars, PivotStrength = pivotStrength }, input, ref cacheTQ_Regime);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool useRule12, bool showDebugVisuals, int rule12Bars, bool rule12PriceFilter, int fastTrendBars, bool fastTrendSlope, int mode, int scoreMin, double scoreRsiBand, int scoreEventBars, int scoreExit, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, int divBars, int pivotStrength)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, useRule12, showDebugVisuals, rule12Bars, rule12PriceFilter, fastTrendBars, fastTrendSlope, mode, scoreMin, scoreRsiBand, scoreEventBars, scoreExit, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, divBars, pivotStrength);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool useRule12, bool showDebugVisuals, int rule12Bars, bool rule12PriceFilter, int fastTrendBars, bool fastTrendSlope, int mode, int scoreMin, double scoreRsiBand, int scoreEventBars, int scoreExit, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, int divBars, int pivotStrength)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, useRule12, showDebugVisuals, rule12Bars, rule12PriceFilter, fastTrendBars, fastTrendSlope, mode, scoreMin, scoreRsiBand, scoreEventBars, scoreExit, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, divBars, pivotStrength);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool useRule12, bool showDebugVisuals, int rule12Bars, bool rule12PriceFilter, int fastTrendBars, bool fastTrendSlope, int mode, int scoreMin, double scoreRsiBand, int scoreEventBars, int scoreExit, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, int divBars, int pivotStrength)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, useRule12, showDebugVisuals, rule12Bars, rule12PriceFilter, fastTrendBars, fastTrendSlope, mode, scoreMin, scoreRsiBand, scoreEventBars, scoreExit, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, divBars, pivotStrength);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool useRule12, bool showDebugVisuals, int rule12Bars, bool rule12PriceFilter, int fastTrendBars, bool fastTrendSlope, int mode, int scoreMin, double scoreRsiBand, int scoreEventBars, int scoreExit, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, int divBars, int pivotStrength)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, useRule12, showDebugVisuals, rule12Bars, rule12PriceFilter, fastTrendBars, fastTrendSlope, mode, scoreMin, scoreRsiBand, scoreEventBars, scoreExit, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, divBars, pivotStrength);
		}
	}
}

#endregion
