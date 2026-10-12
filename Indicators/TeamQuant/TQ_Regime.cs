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
	/// 10분봉 추세 5단계 (spec 3장, docs/interface.md):
	/// +2 상승추세, +1 하락 전환 의심, 0 횡보, −1 상승 전환 의심, −2 하락추세.
	/// 부호는 바탕이 되는 추세의 방향이다 (+1은 상승추세 조건이 유지되는 중에 뜬 의심).
	/// 입력 시리즈가 추세를 판단하는 봉이어야 한다. 전략에서는 TQ_Regime(BarsArray[1], ...)로 호출한다.
	/// 이동평균 Slow가 채워지기 전(봉 수 부족)에는 Regime이 NaN이다.
	/// </summary>
	public class TQ_Regime : Indicator
	{
		private SMA				smaFast;
		private SMA				smaMid;
		private SMA				smaSlow;
		private TQ_MacdTurn		macdTurn;	// MACD선과 히스토그램 연속 증가·감소 판정
		private RSI				rsi;
		private SMA				rsiAvg;		// RSI의 최근 평균 (RsiRelative)
		private double			rsiCenter		= double.NaN;	// 화면 표시용: 지금 RSI 중심
		private MIN				stochLow;	// 스토캐스틱은 TQ_Signals·TQ_Stochastic과 같은 식으로 직접 계산한다
		private MAX				stochHigh;
		private SMA				stochK;
		private SMA				stochD;
		private Series<double>	stochRawK;
		private Series<double>	kSeries;
		private Brush			upBrush;
		private Brush			downBrush;
		private Brush			upSuspectBrush;
		private Brush			downSuspectBrush;
		private int				lastRegime;
		private int				minBars;

		// 의심 상태는 한 번 뜨면 유지한다 (spec 3장). 추세 조건이 깨지거나, SuspectReturn이 켜져 있고 반대 신호가 나오면 푼다
		private bool			suspectInUp;
		private bool			suspectInDown;
		private string			suspectReason	= "";
		private int				aligned;		// +1 정배열, −1 역배열, 0 둘 다 아님
		private int				lastDir;		// 마지막으로 인정된 추세의 방향 (NoSideways용): +1 상승, −1 하락, 0 아직 없음
		private int				trendDir;		// 점수 방식(Mode 2)의 현재 추세 방향: +1 상승, −1 하락, 0 없음
		private int				lastEventDir;	// 가장 최근 사건의 방향: +1 상승 쪽(MACD 상승 전환·골든크로스·상승 다이버전스), −1 하락 쪽
		private int				lastEventBar	= -1;	// 그 사건이 나온 봉 번호
		private int				score;			// 점수 방식의 합계 (−5 ~ +5)
		private string			scoreText		= "";	// 화면·로그용: 지표별 방향

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "10분봉 추세 5단계: +2 상승 / +1 하락 전환 의심 / 0 횡보 / −1 상승 전환 의심 / −2 하락 (spec 3장)";
				Name						= "TQ_Regime";
				Calculate					= Calculate.OnBarClose;	// spec 3장: 추세 봉이 마감될 때마다 다시 계산
				IsOverlay					= true;		// 가격 패널에 얹어 별도 패널을 만들지 않는다(ShowPlots 꺼지면 투명이라 안 보임)
				IsAutoScale					= false;	// 오버레이로 올라가도 가격 패널 스케일을 건드리지 않게 한다
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				Fast						= 6;
				Mid							= 18;
				Slow						= 36;
				ShowDebugVisuals			= false;
				MacdFast					= 12;
				MacdSlow					= 26;
				MacdSmooth					= 9;
				HistBars					= 2;		// MACD 히스토그램이 몇 봉 연속 줄어야(늘어야) 전환으로 볼지
				RsiPeriod					= 14;
				RsiHigh						= 60;		// 하락 전환 의심: RSI가 이 값 이상
				RsiLow						= 40;		// 상승 전환 의심: RSI가 이 값 이하
				DivBars						= 15;		// 다이버전스: 최근 몇 봉 안의 고점·저점과 비교할지
				PivotStrength				= 2;		// 고점·저점으로 인정할 좌우 봉 수
				StochPeriodK				= 10;
				StochSmooth					= 5;
				StochPeriodD				= 5;
				CrossLow					= 20;
				CrossHigh					= 80;
				CrossFilterBars				= 5;
				SuspectReturn				= true;		// 의심 중 반대 신호가 나오면 원래 추세로 돌아감 (spec 3장 시나리오 2)
				RsiLookback					= 1;		// 의심의 RSI 조건을 최근 몇 봉 안에서 볼지. 1 = 같은 봉에서만
				Mode						= 1;		// 1 = 회의 문서 정의(정배열 AND MACD선 부호), 2 = 점수 방식(가격·MACD·RSI·스토캐스틱)
				NoSideways					= false;	// 켜면 횡보 없이 상승·하락추세와 각각의 전환 의심만 낸다
				ScoreEventBars				= 3;		// 점수 방식: 최근 몇 봉 안의 사건을 점수에 넣을지. 0 = 안 넣음
				RsiRelative					= false;	// 켜면 의심의 RSI 조건을 고정 기준 대신 최근 평균에서 벗어난 폭으로 본다
				RsiCenterBars				= 20;		// RSI 중심을 구하는 봉 수
				RsiOffset					= 8;		// 중심에서 이만큼 벗어나야 과열·침체로 본다
				ScoreExit					= 0;		// 점수 방식: 상승추세는 점수가 이 값 이하로 내려가면 끝난다 (하락추세는 부호 반대)
				SuspectNeedsPrice			= false;	// 켜면 가격이 아직 추세 방향(종가가 단기선 위 + 단기선 상승)이면 의심을 띄우지 않는다
				ScoreMin					= 2;		// 점수 방식: 추세로 인정할 최소 점수 (같은 방향 지표 수 − 반대 방향 지표 수)
				ScoreRsiBand				= 5;		// 점수 방식: RSI가 50에서 이만큼 벗어나야 방향으로 인정 (5면 55 이상 / 45 이하)
				ShowPlots					= false;	// 차트 표시용. 끄면 플롯 선을 안 그린다(값은 그대로, 전략 동작 무관)

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.Gold, "Regime");
				AddPlot(Brushes.Gray, "SmaFast");
				AddPlot(Brushes.DodgerBlue, "MacdLine");
				AddPlot(Brushes.MediumPurple, "Rsi");
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
				macdTurn	= TQ_MacdTurn(MacdFast, MacdSlow, MacdSmooth, HistBars, 14, 0);
				rsi			= RSI(RsiPeriod, 3);	// 두 번째 인자는 평균선용, RSI 값과 무관
				rsiAvg		= SMA(Values[3], RsiCenterBars);	// RSI 플롯의 평균 = 그 시점의 RSI 중심 (RsiRelative용)

				// 스토캐스틱: 원시 %K → SMA(StochSmooth) = %K → SMA(StochPeriodD) = %D (.claude/rules/ninjascript.md)
				stochRawK	= new Series<double>(this);
				kSeries		= new Series<double>(this);
				stochLow	= MIN(Low, StochPeriodK);
				stochHigh	= MAX(High, StochPeriodK);
				stochK		= SMA(stochRawK, StochSmooth);
				stochD		= SMA(kSeries, StochPeriodD);

				lastRegime		= int.MinValue;
				suspectInUp		= false;
				suspectInDown	= false;
				suspectReason	= "";
				aligned			= 0;
				trendDir		= 0;
				lastEventDir	= 0;
				lastEventBar	= -1;
				lastDir			= 0;
				score			= 0;
				scoreText		= "";

				// 참조하는 가장 먼 인덱스: 이동평균 Slow, 다이버전스 DivBars + PivotStrength봉 전, 크로스 필터
				minBars		= Math.Max(Slow - 1, Math.Max(DivBars + PivotStrength, Math.Max(CrossFilterBars, Math.Max(HistBars, RsiLookback + RsiCenterBars))));

				// 검증용 배경색. 직접 만든 브러시는 한 번만 만들고 Freeze한다
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
			// 숫자 값
			double stochLL		= stochLow[0];
			double stochRange	= stochHigh[0] - stochLL;
			// 범위가 0이면(고가=저가) 직전 값 유지
			stochRawK[0]	= stochRange == 0 ? (CurrentBar == 0 ? 50 : stochRawK[1]) : 100 * (Close[0] - stochLL) / stochRange;
			kSeries[0]		= stochK[0];
			double k0		= kSeries[0];
			double d0		= stochD[0];

			SmaFast[0]		= smaFast[0];
			MacdLine[0]		= macdTurn.Values[1][0];
			Rsi[0]			= rsi[0];

			// 데이터 부족 가드: 이동평균 Slow가 채워지기 전에는 추세를 내지 않는다
			if (CurrentBar < minBars)
			{
				Regime[0] = double.NaN;
				return;
			}

			double f	= smaFast[0];
			double m	= smaMid[0];
			double s	= smaSlow[0];
			double ml	= MacdLine[0];
			double r0	= Rsi[0];

			aligned		= f > m && m > s ? 1 : f < m && m < s ? -1 : 0;

			// MACD 히스토그램 전환 (spec 2장 MACD 전환과 같은 정의, 최소 변화폭 없음):
			// 히스토그램이 HistBars봉 연속 감소(증가)한 것이 처음 확인된 봉 하나에서만 참이다
			bool histDown	= macdTurn.TurnDown[0];
			bool histUp		= macdTurn.TurnUp[0];

			// 골든·데드크로스 (spec 2장 정의 그대로): %K·%D 교차 AND 최근 CrossFilterBars봉 안에 %K가 20 아래/80 위
			double k1 = kSeries[1];
			double d1 = stochD[1];
			bool kWasLow	= false;
			bool kWasHigh	= false;
			for (int i = 0; i < CrossFilterBars; i++)
			{
				if (kSeries[i] < CrossLow)	kWasLow = true;
				if (kSeries[i] > CrossHigh)	kWasHigh = true;
			}
			bool golden	= k1 <= d1 && k0 > d0 && kWasLow;
			bool dead	= k1 >= d1 && k0 < d0 && kWasHigh;

			// spec 3장 RSI 다이버전스: 최근 DivBars봉 안에 생긴 확연한 고점(좌우 PivotStrength봉보다 높은 봉) 중 가장 높은 것과 비교.
			// 현재 봉이 그 고점보다 높은 새 고점인데 RSI는 그때보다 낮으면 하락 다이버전스. 상승 쪽은 저점으로 거울상.
			// 고점·저점이 여러 개면 가장 높은(낮은) 것, 같으면 최근 것
			bool bearDiv = false;
			bool bullDiv = false;
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
			if (hiIdx >= 0)	bearDiv = High[0] > High[hiIdx] && r0 < Rsi[hiIdx];
			if (loIdx >= 0)	bullDiv = Low[0] < Low[loIdx] && r0 > Rsi[loIdx];

			// 가장 최근의 사건과 그 방향을 기억한다 (점수 방식의 ⑤번 항목용).
			// 상승 쪽 사건 = MACD 상승 전환, 골든크로스, 상승 다이버전스. 하락 쪽은 그 반대. 한 봉에 양쪽이 같이 나오면 사건으로 치지 않는다
			bool bullEvent = histUp || golden || bullDiv;
			bool bearEvent = histDown || dead || bearDiv;
			if (bullEvent != bearEvent)
			{
				lastEventDir	= bullEvent ? 1 : -1;
				lastEventBar	= CurrentBar;
			}

			// spec 3장 점수 방식 (Mode 2): 아래 항목이 각각 상승 +1 / 하락 −1 / 중립 0을 내고 합친다 (−5 ~ +5, 사건을 끄면 −4 ~ +4)
			//  ① 가격: 종가가 단기선 위에 있고 단기선이 오르는 중이면 +1, 아래에 있고 내리는 중이면 −1
			//  ② MACD: 히스토그램(MACD선 − 시그널선)이 0보다 크고 직전 봉보다 줄지 않았으면 +1, 0보다 작고 늘지 않았으면 −1
			//  ③ RSI: 50 + ScoreRsiBand 이상이면 +1, 50 − ScoreRsiBand 이하면 −1
			//  ④ 스토캐스틱: %K가 %D 위이고 50 위면 +1, %D 아래이고 50 아래면 −1
			//  ⑤ 사건: 최근 ScoreEventBars봉(현재 포함) 안의 가장 최근 사건이 상승 쪽이면 +1, 하락 쪽이면 −1. 0이면 이 항목을 쓰지 않는다
			// ②와 ④는 방향이 맞아도 힘이 빠지는 중이면(히스토그램이 줄어듦, %K가 반대쪽 절반에 있음) 0점으로 둔다
			double hist		= macdTurn.Hist[0];
			double hist1	= macdTurn.Hist[1];
			int cMa		= Close[0] > f && f > smaFast[1] ? 1 : Close[0] < f && f < smaFast[1] ? -1 : 0;
			int cMacd	= hist > 0 && hist >= hist1 ? 1 : hist < 0 && hist <= hist1 ? -1 : 0;
			int cRsi	= r0 >= 50 + ScoreRsiBand ? 1 : r0 <= 50 - ScoreRsiBand ? -1 : 0;
			int cSto	= k0 > d0 && k0 > 50 ? 1 : k0 < d0 && k0 < 50 ? -1 : 0;
			int cEvt	= ScoreEventBars > 0 && lastEventBar >= 0 && CurrentBar - lastEventBar < ScoreEventBars ? lastEventDir : 0;
			score		= cMa + cMacd + cRsi + cSto + cEvt;
			scoreText	= "가격" + Arrow(cMa) + " MACD" + Arrow(cMacd) + " RSI" + Arrow(cRsi) + " 스토" + Arrow(cSto)
							+ (ScoreEventBars > 0 ? " 사건" + Arrow(cEvt) : "");

			bool upCond, dnCond;
			if (Mode == 2)
			{
				// 추세를 벗어나는 기준은 들어가는 기준보다 느슨하게 둔다 (점수가 1 아래로 내려가야 상승추세가 끝남).
				// 같은 기준을 쓰면 지표 하나가 흔들릴 때마다 추세가 켜졌다 꺼졌다 한다
				if (trendDir == 1 && score <= ScoreExit)		trendDir = 0;
				if (trendDir == -1 && score >= -ScoreExit)	trendDir = 0;
				// 추세로 들어가는 기준: 가격 조건(①)이 맞고 점수가 ScoreMin 이상
				if (trendDir != 1 && cMa > 0 && score >= ScoreMin)			trendDir = 1;
				else if (trendDir != -1 && cMa < 0 && score <= -ScoreMin)	trendDir = -1;
				upCond = trendDir == 1;
				dnCond = trendDir == -1;
			}
			else
			{
				// spec 3장 회의 문서 정의 (Mode 1): 상승추세 = 정배열 AND MACD선 > 0, 하락추세 = 역배열 AND MACD선 < 0
				upCond = aligned > 0 && ml > 0;
				dnCond = aligned < 0 && ml < 0;
			}

			// spec 3장 의심의 RSI 조건: 최근 RsiLookback봉(현재 포함) 안에 기준을 넘은 적이 있으면 충족.
			// MACD 전환·크로스는 가격이 꺾인 뒤 1~2봉 늦게 확인되는데 그때는 RSI가 이미 기준 안쪽으로 돌아와 있어서,
			// 같은 봉에서만 보면(RsiLookback 1) 급하게 꺾이는 장에서 의심이 뜨지 않는다 (2026-10-12 Playback)
			bool rsiWasHigh	= false;
			bool rsiWasLow	= false;
			// RsiRelative: 고정 기준(RsiHigh / RsiLow) 대신 그 시점의 RSI 중심(최근 RsiCenterBars봉 평균)에서
			// RsiOffset만큼 벗어났는지로 본다. RSI는 오르는 날에는 60 위에 오래 머물고 그런 날의 하락 구간에서는 40까지 내려가지 않아서,
			// 고정 기준으로는 하락 전환 의심만 자주 뜨고 상승 전환 의심은 거의 뜨지 않는다 (2026-10-12 Playback: 60 이상 37봉, 40 이하 8봉)
			for (int i = 0; i < RsiLookback; i++)
			{
				double hiLine = RsiRelative ? rsiAvg[i] + RsiOffset : RsiHigh;
				double loLine = RsiRelative ? rsiAvg[i] - RsiOffset : RsiLow;
				if (Rsi[i] >= hiLine)	rsiWasHigh = true;
				if (Rsi[i] <= loLine)	rsiWasLow = true;
			}
			rsiCenter = rsiAvg[0];

			int r = 0;	// spec 3장 횡보
			if (upCond)
			{
				suspectInDown = false;
				// 하락 전환 의심: RSI ≥ RsiHigh (최근 RsiLookback봉) AND [RSI 다이버전스 OR MACD 하락 전환 OR 데드크로스]
				if (rsiWasHigh && (bearDiv || histDown || dead) && !(SuspectNeedsPrice && cMa > 0))
				{
					suspectInUp		= true;
					suspectReason	= Reason(bearDiv, histDown, dead, false);
				}
				// 시나리오 2 (SuspectReturn): 의심 중 MACD 히스토그램 연속 증가 또는 골든크로스가 나오면 상승추세로 돌아간다
				else if (suspectInUp && SuspectReturn && (histUp || golden))
					suspectInUp = false;

				r = suspectInUp ? 1 : 2;
			}
			else if (dnCond)
			{
				suspectInUp = false;
				// 상승 전환 의심: RSI ≤ RsiLow (최근 RsiLookback봉) AND [RSI 다이버전스 OR MACD 상승 전환 OR 골든크로스]
				if (rsiWasLow && (bullDiv || histUp || golden) && !(SuspectNeedsPrice && cMa < 0))
				{
					suspectInDown	= true;
					suspectReason	= Reason(bullDiv, histUp, golden, true);
				}
				else if (suspectInDown && SuspectReturn && (histDown || dead))
					suspectInDown = false;

				r = suspectInDown ? -1 : -2;
			}
			else
			{
				// 추세 조건이 깨지면 의심도 끝난다 (시나리오 1: 추세 → 의심 → 횡보 또는 반대 추세)
				suspectInUp		= false;
				suspectInDown	= false;

				// NoSideways: 횡보를 두지 않는다. 상승·하락 조건이 둘 다 아닌 구간은 마지막 추세의 "전환 의심"으로 본다
				// (상승추세였으면 하락 전환 의심, 하락추세였으면 상승 전환 의심). 반대 추세 조건이 충족될 때까지 이어진다.
				// 아직 추세가 한 번도 없었으면 종가가 장기선 위인지 아래인지로 방향을 정한다
				if (NoSideways)
				{
					if (lastDir == 0)
						lastDir = Close[0] >= s ? 1 : -1;
					r				= lastDir;
					suspectReason	= "추세 조건이 깨짐";
				}
			}
			if (upCond)			lastDir = 1;
			else if (dnCond)	lastDir = -1;
			if (r == 2 || r == -2 || r == 0)
				suspectReason = "";

			Regime[0] = r;

			if (r != lastRegime)
			{
				Print(string.Format("[{0}][{1}][TQ_Regime] 추세 전환 {2} → {3}{4} (배열={5} MACD선={6:F2} RSI={7:F1} 점수={8} {9})",
					Time[0], Instrument.FullName, lastRegime == int.MinValue ? "없음" : StateName(lastRegime), StateName(r),
					suspectReason.Length > 0 ? " [" + suspectReason + "]" : "", aligned, ml, r0, score, scoreText));
				lastRegime = r;
			}

			if (ShowDebugVisuals)
				BackBrushAll = r == 2 ? upBrush : r == 1 ? upSuspectBrush : r == -1 ? downSuspectBrush : r == -2 ? downBrush : null;
		}

		private string Arrow(int v)
		{
			return v > 0 ? "▲" : v < 0 ? "▼" : "−";
		}

		// 의심의 근거를 화면·로그용 글로
		private string Reason(bool div, bool hist, bool cross, bool bullish)
		{
			string text = "";
			if (div)	text += (text.Length > 0 ? ", " : "") + "RSI 다이버전스";
			if (hist)	text += (text.Length > 0 ? ", " : "") + (bullish ? "MACD 상승 전환" : "MACD 하락 전환");
			if (cross)	text += (text.Length > 0 ? ", " : "") + (bullish ? "골든크로스" : "데드크로스");
			return text;
		}

		// 추세 값(+2 ~ −2)의 이름 (로그용)
		private string StateName(int state)
		{
			switch (state)
			{
				case 2:		return "상승추세";
				case 1:		return "하락 전환 의심";
				case -1:	return "상승 전환 의심";
				case -2:	return "하락추세";
				default:	return "횡보";
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fast", Description = "추세 이동평균 단기", GroupName = "Parameters", Order = 0)]
		public int Fast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Mid", Description = "추세 이동평균 중기", GroupName = "Parameters", Order = 1)]
		public int Mid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Slow", Description = "추세 이동평균 장기", GroupName = "Parameters", Order = 2)]
		public int Slow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 배경색 표시", GroupName = "Parameters", Order = 3)]
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
		[Display(Name = "HistBars", Description = "MACD 히스토그램이 몇 봉 연속 줄어야(늘어야) 전환으로 볼지", GroupName = "Parameters", Order = 7)]
		public int HistBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간", GroupName = "Parameters", Order = 8)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiHigh", Description = "하락 전환 의심: RSI가 이 값 이상", GroupName = "Parameters", Order = 9)]
		public double RsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiLow", Description = "상승 전환 의심: RSI가 이 값 이하", GroupName = "Parameters", Order = 10)]
		public double RsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivBars", Description = "다이버전스: 최근 몇 봉 안의 고점·저점과 비교할지", GroupName = "Parameters", Order = 11)]
		public int DivBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "PivotStrength", Description = "고점·저점으로 인정할 좌우 봉 수 (좌우 N봉보다 높으면 고점)", GroupName = "Parameters", Order = 12)]
		public int PivotStrength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodK", Description = "스토캐스틱 %K 길이", GroupName = "Parameters", Order = 13)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochSmooth", Description = "스토캐스틱 %K 스무딩", GroupName = "Parameters", Order = 14)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodD", Description = "스토캐스틱 %D 스무딩", GroupName = "Parameters", Order = 15)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossLow", Description = "골든크로스 필터 기준", GroupName = "Parameters", Order = 16)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossHigh", Description = "데드크로스 필터 기준", GroupName = "Parameters", Order = 17)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "CrossFilterBars", Description = "크로스 필터 봉 수 (현재 봉 포함)", GroupName = "Parameters", Order = 18)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "SuspectReturn", Description = "의심 중 반대 신호(MACD 히스토그램 연속 반전 또는 크로스)가 나오면 원래 추세로 돌아감. 끄면 추세 조건이 깨질 때까지 의심 유지", GroupName = "Parameters", Order = 19)]
		public bool SuspectReturn { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiLookback", Description = "의심의 RSI 조건을 최근 몇 봉(현재 포함) 안에서 볼지. 1 = 같은 봉에서만", GroupName = "Parameters", Order = 20)]
		public int RsiLookback { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name = "Mode", Description = "추세 판단 방식. 1 = 회의 문서 정의(정배열 AND MACD선 부호), 2 = 점수 방식(가격·MACD·RSI·스토캐스틱)", GroupName = "Parameters", Order = 21)]
		public int Mode { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "ScoreMin", Description = "점수 방식: 추세로 인정할 최소 점수 (1~4)", GroupName = "Parameters", Order = 22)]
		public int ScoreMin { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "ScoreRsiBand", Description = "점수 방식: RSI가 50에서 이만큼 벗어나야 방향으로 인정", GroupName = "Parameters", Order = 23)]
		public double ScoreRsiBand { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NoSideways", Description = "켜면 횡보를 내지 않는다. 상승·하락 조건이 둘 다 아닌 구간은 마지막 추세의 전환 의심으로 본다", GroupName = "Parameters", Order = 24)]
		public bool NoSideways { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "ScoreEventBars", Description = "점수 방식: 최근 몇 봉(현재 포함) 안의 사건(MACD 전환, 골든·데드크로스, 다이버전스)을 점수의 한 항목으로 넣을지. 0 = 안 넣음", GroupName = "Parameters", Order = 25)]
		public int ScoreEventBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RsiRelative", Description = "의심의 RSI 조건을 고정 기준(RsiHigh / RsiLow) 대신 최근 RsiCenterBars봉의 RSI 평균에서 RsiOffset만큼 벗어났는지로 본다", GroupName = "Parameters", Order = 26)]
		public bool RsiRelative { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiCenterBars", Description = "RsiRelative: RSI 중심(평균)을 구하는 봉 수", GroupName = "Parameters", Order = 27)]
		public int RsiCenterBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "RsiOffset", Description = "RsiRelative: RSI가 중심에서 이만큼 위면 하락 전환 의심의 RSI 조건 충족, 이만큼 아래면 상승 전환 의심의 RSI 조건 충족", GroupName = "Parameters", Order = 28)]
		public double RsiOffset { get; set; }

		[NinjaScriptProperty]
		[Range(-5, 5)]
		[Display(Name = "ScoreExit", Description = "점수 방식: 상승추세는 점수가 이 값 이하로 내려가면 끝난다. 하락추세는 점수가 이 값의 부호를 바꾼 값 이상으로 올라가면 끝난다. 0이면 지표들이 중립이 되는 순간, -1이면 반대쪽을 가리킬 때", GroupName = "Parameters", Order = 29)]
		public int ScoreExit { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "SuspectNeedsPrice", Description = "켜면 종가가 아직 단기선 위에 있고 단기선이 오르는 중일 때는 하락 전환 의심을 띄우지 않는다 (상승 전환 의심은 반대)", GroupName = "Parameters", Order = 30)]
		public bool SuspectNeedsPrice { get; set; }

		// 표시 전용 토글. [NinjaScriptProperty]를 붙이지 않아 생성자 시그니처가 바뀌지 않는다(전략 호출부·생성 코드 그대로)
		[Display(Name = "ShowPlots", Description = "플롯 선 표시. 끄면 차트에서 선이 안 보임(값·전략 동작은 그대로)", GroupName = "Parameters", Order = 31)]
		public bool ShowPlots { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Regime { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SmaFast { get { return Values[1]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> MacdLine { get { return Values[2]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Rsi { get { return Values[3]; } }

		// 화면 표시용: 이동평균 배열 (+1 정배열, −1 역배열, 0 둘 다 아님)
		[Browsable(false)]
		[XmlIgnore]
		public int Aligned { get { Update(); return aligned; } }

		// 화면 표시용: 지금 의심 상태의 근거 (의심이 아니면 빈 문자열)
		[Browsable(false)]
		[XmlIgnore]
		public string SuspectReason { get { Update(); return suspectReason; } }

		// 화면 표시용: 점수 방식의 합계(−4 ~ +4)와 지표별 방향. Mode 1에서도 계산은 한다
		[Browsable(false)]
		[XmlIgnore]
		public int Score { get { Update(); return score; } }

		[Browsable(false)]
		[XmlIgnore]
		public string ScoreText { get { Update(); return scoreText; } }

		// 화면 표시용: 지금의 RSI 중심 (최근 RsiCenterBars봉 평균). RsiRelative가 꺼져 있어도 계산한다
		[Browsable(false)]
		[XmlIgnore]
		public double RsiCenter { get { Update(); return rsiCenter; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TeamQuant.TQ_Regime[] cacheTQ_Regime;
		public TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, double rsiHigh, double rsiLow, int divBars, int pivotStrength, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, bool suspectReturn, int rsiLookback, int mode, int scoreMin, double scoreRsiBand, bool noSideways, int scoreEventBars, bool rsiRelative, int rsiCenterBars, double rsiOffset, int scoreExit, bool suspectNeedsPrice)
		{
			return TQ_Regime(Input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, rsiHigh, rsiLow, divBars, pivotStrength, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, suspectReturn, rsiLookback, mode, scoreMin, scoreRsiBand, noSideways, scoreEventBars, rsiRelative, rsiCenterBars, rsiOffset, scoreExit, suspectNeedsPrice);
		}

		public TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input, int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, double rsiHigh, double rsiLow, int divBars, int pivotStrength, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, bool suspectReturn, int rsiLookback, int mode, int scoreMin, double scoreRsiBand, bool noSideways, int scoreEventBars, bool rsiRelative, int rsiCenterBars, double rsiOffset, int scoreExit, bool suspectNeedsPrice)
		{
			if (cacheTQ_Regime != null)
				for (int idx = 0; idx < cacheTQ_Regime.Length; idx++)
					if (cacheTQ_Regime[idx] != null && cacheTQ_Regime[idx].Fast == fast && cacheTQ_Regime[idx].Mid == mid && cacheTQ_Regime[idx].Slow == slow && cacheTQ_Regime[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Regime[idx].MacdFast == macdFast && cacheTQ_Regime[idx].MacdSlow == macdSlow && cacheTQ_Regime[idx].MacdSmooth == macdSmooth && cacheTQ_Regime[idx].HistBars == histBars && cacheTQ_Regime[idx].RsiPeriod == rsiPeriod && cacheTQ_Regime[idx].RsiHigh == rsiHigh && cacheTQ_Regime[idx].RsiLow == rsiLow && cacheTQ_Regime[idx].DivBars == divBars && cacheTQ_Regime[idx].PivotStrength == pivotStrength && cacheTQ_Regime[idx].StochPeriodK == stochPeriodK && cacheTQ_Regime[idx].StochSmooth == stochSmooth && cacheTQ_Regime[idx].StochPeriodD == stochPeriodD && cacheTQ_Regime[idx].CrossLow == crossLow && cacheTQ_Regime[idx].CrossHigh == crossHigh && cacheTQ_Regime[idx].CrossFilterBars == crossFilterBars && cacheTQ_Regime[idx].SuspectReturn == suspectReturn && cacheTQ_Regime[idx].RsiLookback == rsiLookback && cacheTQ_Regime[idx].Mode == mode && cacheTQ_Regime[idx].ScoreMin == scoreMin && cacheTQ_Regime[idx].ScoreRsiBand == scoreRsiBand && cacheTQ_Regime[idx].NoSideways == noSideways && cacheTQ_Regime[idx].ScoreEventBars == scoreEventBars && cacheTQ_Regime[idx].RsiRelative == rsiRelative && cacheTQ_Regime[idx].RsiCenterBars == rsiCenterBars && cacheTQ_Regime[idx].RsiOffset == rsiOffset && cacheTQ_Regime[idx].ScoreExit == scoreExit && cacheTQ_Regime[idx].SuspectNeedsPrice == suspectNeedsPrice && cacheTQ_Regime[idx].EqualsInput(input))
						return cacheTQ_Regime[idx];
			return CacheIndicator<TeamQuant.TQ_Regime>(new TeamQuant.TQ_Regime(){ Fast = fast, Mid = mid, Slow = slow, ShowDebugVisuals = showDebugVisuals, MacdFast = macdFast, MacdSlow = macdSlow, MacdSmooth = macdSmooth, HistBars = histBars, RsiPeriod = rsiPeriod, RsiHigh = rsiHigh, RsiLow = rsiLow, DivBars = divBars, PivotStrength = pivotStrength, StochPeriodK = stochPeriodK, StochSmooth = stochSmooth, StochPeriodD = stochPeriodD, CrossLow = crossLow, CrossHigh = crossHigh, CrossFilterBars = crossFilterBars, SuspectReturn = suspectReturn, RsiLookback = rsiLookback, Mode = mode, ScoreMin = scoreMin, ScoreRsiBand = scoreRsiBand, NoSideways = noSideways, ScoreEventBars = scoreEventBars, RsiRelative = rsiRelative, RsiCenterBars = rsiCenterBars, RsiOffset = rsiOffset, ScoreExit = scoreExit, SuspectNeedsPrice = suspectNeedsPrice }, input, ref cacheTQ_Regime);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, double rsiHigh, double rsiLow, int divBars, int pivotStrength, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, bool suspectReturn, int rsiLookback, int mode, int scoreMin, double scoreRsiBand, bool noSideways, int scoreEventBars, bool rsiRelative, int rsiCenterBars, double rsiOffset, int scoreExit, bool suspectNeedsPrice)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, rsiHigh, rsiLow, divBars, pivotStrength, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, suspectReturn, rsiLookback, mode, scoreMin, scoreRsiBand, noSideways, scoreEventBars, rsiRelative, rsiCenterBars, rsiOffset, scoreExit, suspectNeedsPrice);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, double rsiHigh, double rsiLow, int divBars, int pivotStrength, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, bool suspectReturn, int rsiLookback, int mode, int scoreMin, double scoreRsiBand, bool noSideways, int scoreEventBars, bool rsiRelative, int rsiCenterBars, double rsiOffset, int scoreExit, bool suspectNeedsPrice)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, rsiHigh, rsiLow, divBars, pivotStrength, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, suspectReturn, rsiLookback, mode, scoreMin, scoreRsiBand, noSideways, scoreEventBars, rsiRelative, rsiCenterBars, rsiOffset, scoreExit, suspectNeedsPrice);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Regime TQ_Regime(int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, double rsiHigh, double rsiLow, int divBars, int pivotStrength, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, bool suspectReturn, int rsiLookback, int mode, int scoreMin, double scoreRsiBand, bool noSideways, int scoreEventBars, bool rsiRelative, int rsiCenterBars, double rsiOffset, int scoreExit, bool suspectNeedsPrice)
		{
			return indicator.TQ_Regime(Input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, rsiHigh, rsiLow, divBars, pivotStrength, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, suspectReturn, rsiLookback, mode, scoreMin, scoreRsiBand, noSideways, scoreEventBars, rsiRelative, rsiCenterBars, rsiOffset, scoreExit, suspectNeedsPrice);
		}

		public Indicators.TeamQuant.TQ_Regime TQ_Regime(ISeries<double> input , int fast, int mid, int slow, bool showDebugVisuals, int macdFast, int macdSlow, int macdSmooth, int histBars, int rsiPeriod, double rsiHigh, double rsiLow, int divBars, int pivotStrength, int stochPeriodK, int stochSmooth, int stochPeriodD, double crossLow, double crossHigh, int crossFilterBars, bool suspectReturn, int rsiLookback, int mode, int scoreMin, double scoreRsiBand, bool noSideways, int scoreEventBars, bool rsiRelative, int rsiCenterBars, double rsiOffset, int scoreExit, bool suspectNeedsPrice)
		{
			return indicator.TQ_Regime(input, fast, mid, slow, showDebugVisuals, macdFast, macdSlow, macdSmooth, histBars, rsiPeriod, rsiHigh, rsiLow, divBars, pivotStrength, stochPeriodK, stochSmooth, stochPeriodD, crossLow, crossHigh, crossFilterBars, suspectReturn, rsiLookback, mode, scoreMin, scoreRsiBand, noSideways, scoreEventBars, rsiRelative, rsiCenterBars, rsiOffset, scoreExit, suspectNeedsPrice);
		}
	}
}

#endregion
