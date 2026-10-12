#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.TeamQuant
{
	/// <summary>
	/// 진입 봉(1분봉) 신호 엔진: 숫자 값, 이벤트, 9개 진입 신호 (spec 2장·5장, docs/interface.md)
	/// 포지션·주문·시간대·추세와 무관한 값만 낸다. 어느 신호를 쓸지는 전략이 추세 상태로 고른다.
	/// </summary>
	public class TQ_Signals : Indicator
	{
		private TQ_ATRChannels	channels;
		private MIN				stochLow;	// 스토캐스틱 원시 %K용: periodK 구간 최저 저가
		private MAX				stochHigh;	// 스토캐스틱 원시 %K용: periodK 구간 최고 고가
		private SMA				stochK;		// %K = SMA(원시 %K, smoothK)
		private SMA				stochD;		// %D = SMA(%K, periodD)
		private Series<double>	stochRawK;	// 원시 %K (스무딩 전)
		private RSI				rsi;
		private TQ_MacdTurn	macdTurn;
		private SMA				sma;
		private MAX				swingHigh;
		private MIN				swingLow;
		private int				minBars;

		// spec 2장 T1 대기 상태: 진입 신호마다 따로 둔다. -1 = 대기 없음, 0 = 밴드 조건 봉 t, 1~N = t 이후 봉 수
		private int waitUpLong;
		private int waitUpSusLong;
		private int waitUpSusShort;
		private int waitDnShort;
		private int waitDnLong;
		private int waitDnSusLong;
		private int waitDnSusShort;
		private int waitSideLong;
		private int waitSideShort;

		// 추세별 실제 T1 대기 봉 수 (T1WindowUp/Dn/Side가 0이면 T1Window를 쓴다)
		private int t1BarsUp;
		private int t1BarsDn;
		private int t1BarsSide;

		// 이벤트 (spec 2장)
		private Series<bool> macdUp;
		private Series<bool> macdDown;
		private Series<bool> macdRising;	// 화면 표시용: 히스토그램이 연속으로 느는 중
		private Series<bool> macdFalling;	// 화면 표시용: 히스토그램이 연속으로 주는 중
		private Series<bool> golden;
		private Series<bool> dead;
		private Series<bool> t2Bull;
		private Series<bool> t2Bear;

		// 진입 신호 (spec 5.1~5.9)
		private Series<bool> entryUpLong;
		private Series<bool> entryUpSusLong;
		private Series<bool> entryUpSusShort;
		private Series<bool> entryDnShort;
		private Series<bool> entryDnLong;
		private Series<bool> entryDnSusLong;
		private Series<bool> entryDnSusShort;
		private Series<bool> entrySideLong;
		private Series<bool> entrySideShort;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "진입 봉 신호 엔진: 이벤트와 9개 진입 신호 (spec 2장·5장)";
				Name						= "TQ_Signals";
				Calculate					= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				IsOverlay					= true;		// 가격 패널에 얹어 별도 패널을 만들지 않는다(ShowPlots 꺼지면 투명이라 안 보임)
				IsAutoScale					= false;	// 오버레이로 올라가도 가격 패널 스케일을 건드리지 않게 한다(K/D/Rsi 0~100 등)
				IsSuspendedWhileInactive	= true;

				// spec 8장 기본값
				T1Window					= 5;
				CrossFilterBars				= 5;
				CrossLow					= 20;
				CrossHigh					= 80;
				RsiPeriod					= 14;
				SwingBars					= 5;
				BandMidPeriod				= 26;
				BandAtrPeriod				= 14;
				BandMult1					= 1;
				BandMult2					= 2;
				BandMult3					= 3;
				MacdFast					= 12;
				MacdSlow					= 26;
				MacdSmooth					= 9;
				StochPeriodK				= 10;
				StochSmooth					= 5;
				StochPeriodD				= 5;
				SmaPeriod					= 18;	// spec 5장 마지막 조각 청산용 이동평균
				MacdConfirmBars				= 2;
				MacdMinChangeAtr			= 0;
				ShowDebugVisuals			= false;

				// 진입 밴드 배수 (spec 5장). 0 = 중심선
				UpLongBand					= 1;	// 5.1 상승추세 매수: 저가 −1배 터치
				UpSusLongBand				= 2;	// 5.2 하락 전환 의심 매수: 종가 −2배 아래
				UpSusShortBand				= 1;	// 5.3 하락 전환 의심 매도: 고가 +1배 터치
				DnShortBand					= 1;	// 5.4 하락추세 매도: 고가 +1배 터치
				DnLongBand					= 3;	// 5.5 하락추세 매수: 저가 −3배 터치
				DnLongRsi					= 30;	// 5.5 하락추세 매수: RSI가 이 값 미만
				DnSusLongBand				= 1;	// 5.6 상승 전환 의심 매수: 저가 −1배 터치
				DnSusShortBand				= 2;	// 5.7 상승 전환 의심 매도: 종가 +2배 위
				SideBand					= 2;	// 5.8·5.9 횡보: 저가(고가) ±2배 터치
				T1WindowUp					= 0;	// 0 = T1Window 사용
				T1WindowDn					= 0;
				T1WindowSide				= 0;
				T2CountConfirmBars			= true;	// T2에서 MACD 확인에 걸린 봉 수만큼 앞의 크로스도 인정
				T1SameBar					= false;	// 밴드 조건이 충족된 봉 자체의 반전 신호도 인정
				ShowPlots					= false;	// 차트 표시용. 끄면 플롯 선을 안 그린다(값은 그대로, 전략 동작 무관)

				// 플롯 순서는 아래 Properties의 Values 인덱스와 맞춘다
				AddPlot(Brushes.DodgerBlue, "K");
				AddPlot(Brushes.Orange, "D");
				AddPlot(Brushes.MediumPurple, "Rsi");
				AddPlot(Brushes.Gray, "MacdHist");
				AddPlot(Brushes.Goldenrod, "Sma");
				AddPlot(Brushes.Red, "SwingHigh5");
				AddPlot(Brushes.Blue, "SwingLow5");
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
				macdUp			= new Series<bool>(this);
				macdDown		= new Series<bool>(this);
				macdRising		= new Series<bool>(this);
				macdFalling		= new Series<bool>(this);
				golden			= new Series<bool>(this);
				dead			= new Series<bool>(this);
				t2Bull			= new Series<bool>(this);
				t2Bear			= new Series<bool>(this);

				entryUpLong		= new Series<bool>(this);
				entryUpSusLong	= new Series<bool>(this);
				entryUpSusShort	= new Series<bool>(this);
				entryDnShort	= new Series<bool>(this);
				entryDnLong		= new Series<bool>(this);
				entryDnSusLong	= new Series<bool>(this);
				entryDnSusShort	= new Series<bool>(this);
				entrySideLong	= new Series<bool>(this);
				entrySideShort	= new Series<bool>(this);

				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				// 스토캐스틱: TradingView Pine v6 Stochastic과 동일하게 직접 계산 (SMA 스무딩)
				// 원시 %K = 100 × (종가 − 최저 저가) / (최고 고가 − 최저 저가), periodK = StochPeriodK
				// %K = SMA(원시 %K, StochSmooth), %D = SMA(%K, StochPeriodD)
				stochRawK	= new Series<double>(this);
				stochLow	= MIN(Low, StochPeriodK);
				stochHigh	= MAX(High, StochPeriodK);
				stochK		= SMA(stochRawK, StochSmooth);
				stochD		= SMA(K, StochPeriodD);	// K 플롯(Values[0])의 SMA
				rsi			= RSI(RsiPeriod, 3);									// 두 번째 인자는 평균선용, RSI 값과 무관
				macdTurn	= TQ_MacdTurn(MacdFast, MacdSlow, MacdSmooth, MacdConfirmBars, BandAtrPeriod, MacdMinChangeAtr);
				sma			= SMA(SmaPeriod);
				swingHigh	= MAX(High, SwingBars);
				swingLow	= MIN(Low, SwingBars);

				// 참조하는 가장 먼 인덱스: 크로스 필터 CrossFilterBars - 1봉 전, MACD 전환 MacdConfirmBars봉 전
				minBars		= Math.Max(MacdConfirmBars, CrossFilterBars - 1);

				waitUpLong		= -1;
				waitUpSusLong	= -1;
				waitUpSusShort	= -1;
				waitDnShort		= -1;
				waitDnLong		= -1;
				waitDnSusLong	= -1;
				waitDnSusShort	= -1;
				waitSideLong	= -1;
				waitSideShort	= -1;

				// spec 2장 T1: MACD 전환을 확인하는 데 드는 봉 수만큼 대기 봉 수를 늘린다.
				// 확인 봉 수 2면 반전이 t+1에 시작해도 t+2에야 확인되므로, 늘리지 않으면 기회가 N-1번으로 준다
				t1BarsUp	= (T1WindowUp > 0 ? T1WindowUp : T1Window) + MacdConfirmBars - 1;
				t1BarsDn	= (T1WindowDn > 0 ? T1WindowDn : T1Window) + MacdConfirmBars - 1;
				t1BarsSide	= (T1WindowSide > 0 ? T1WindowSide : T1Window) + MacdConfirmBars - 1;
			}
		}

		protected override void OnBarUpdate()
		{
			// 숫자 값 (docs/interface.md)
			// 스토캐스틱 (TradingView Pine v6과 동일): 원시 %K → SMA(smoothK)=%K → SMA(periodD)=%D
			double stochLL		= stochLow[0];
			double stochHH		= stochHigh[0];
			double stochRange	= stochHH - stochLL;
			// 범위가 0이면(고가=저가) 직전 값 유지. 유동성 있는 선물에서는 거의 없음
			stochRawK[0]	= stochRange == 0 ? (CurrentBar == 0 ? 50 : stochRawK[1]) : 100 * (Close[0] - stochLL) / stochRange;
			K[0]			= stochK[0];		// 원시 %K를 StochSmooth로 스무딩
			D[0]			= stochD[0];		// %K를 StochPeriodD로 스무딩
			Rsi[0]			= rsi[0];
			MacdHist[0]		= macdTurn.Hist[0];
			Sma[0]			= sma[0];
			SwingHigh5[0]	= swingHigh[0];	// spec 2장 전고점: 현재 봉 포함 최근 SwingBars봉
			SwingLow5[0]	= swingLow[0];	// spec 2장 전저점

			// 데이터 부족 가드: 이벤트·신호는 false로 남는다
			if (CurrentBar < minBars)
				return;

			double k0 = K[0];
			double k1 = K[1];
			double d0 = D[0];
			double d1 = D[1];
			double r0 = Rsi[0];

			// spec 2장 MACD 상승/하락 전환: 판정은 TQ_MacdTurn이 한다 (연속 MacdConfirmBars봉 + 최소 변화폭).
			// 전환은 방향이 바뀐 것이 처음 확인된 봉 하나에서만 참이다. 그 방향이 이어지는 동안 계속 참인 값은 MacdRising/MacdFalling
			bool mUp = macdTurn.TurnUp[0];
			bool mDn = macdTurn.TurnDown[0];
			macdRising[0]	= macdTurn.Up[0];
			macdFalling[0]	= macdTurn.Down[0];

			// spec 2장 골든/데드크로스: %K·%D 교차 AND 최근 CrossFilterBars봉(현재 포함) 안에 %K가 20 아래/80 위
			bool kWasLow	= false;
			bool kWasHigh	= false;
			for (int i = 0; i < CrossFilterBars; i++)
			{
				if (K[i] < CrossLow)	kWasLow = true;
				if (K[i] > CrossHigh)	kWasHigh = true;
			}
			bool gold	= k1 <= d1 && k0 > d0 && kWasLow;
			bool dd		= k1 >= d1 && k0 < d0 && kWasHigh;

			macdUp[0]			= mUp;
			macdDown[0]			= mDn;
			golden[0]			= gold;
			dead[0]				= dd;

			// spec 2장 T2: 같은 봉에서 둘 다, 또는 하나가 나온 바로 다음 봉에 나머지
			bool tBull = (mUp && gold) || (macdUp[1] && gold) || (golden[1] && mUp);
			bool tBear = (mDn && dd) || (macdDown[1] && dd) || (dead[1] && mDn);

			// T2CountConfirmBars: MACD 전환은 MacdConfirmBars봉 연속이어야 확인되므로, 실제로 꺾인 봉은 확인 봉보다 앞이다.
			// 전환이 처음 확인된 봉에서는, 확인에 걸린 봉 수만큼 앞에서 나온 크로스도 "바로 옆 봉"으로 인정한다.
			// 확인 봉 수가 1이면 위의 원래 정의와 같다
			if (T2CountConfirmBars)
			{
				bool upFirst = mUp && !macdUp[1];
				bool dnFirst = mDn && !macdDown[1];
				for (int k = 2; k <= MacdConfirmBars && k <= CurrentBar; k++)
				{
					if (upFirst && golden[k])	tBull = true;
					if (dnFirst && dead[k])		tBear = true;
				}
			}
			t2Bull[0]			= tBull;
			t2Bear[0]			= tBear;

			// 반전 신호 (spec 5장 진입 조건의 [대괄호])
			bool revUp	= mUp || gold;
			bool revDn	= mDn || dd;

			// spec 2장 밴드 조건. 터치는 그 봉의 저가(고가)가 밴드에 닿음, 종가 조건은 종가가 밴드 바깥에서 마감.
			// "N배 이상"은 N배 밴드에 닿으면 충족이다 (바깥 밴드에 닿으면 안쪽 밴드에도 닿은 것)
			double lo = Low[0];
			double hi = High[0];
			double c0 = Close[0];

			// spec 5.1 상승추세 매수: 저가가 −1배에 터치 → T1 [MACD 상승 전환 OR 골든크로스]
			bool eUpLong		= StepT1(ref waitUpLong, lo <= Dn(UpLongBand), revUp, t1BarsUp);
			// spec 5.2 하락 전환 의심 매수: 종가가 −2배 아래 → T1 [MACD 상승 전환 OR 골든크로스]
			bool eUpSusLong		= StepT1(ref waitUpSusLong, c0 < Dn(UpSusLongBand), revUp, t1BarsUp);
			// spec 5.3 하락 전환 의심 매도: 고가가 +1배에 터치 → T1 [MACD 하락 전환 OR 데드크로스]
			bool eUpSusShort	= StepT1(ref waitUpSusShort, hi >= Up(UpSusShortBand), revDn, t1BarsUp);
			// spec 5.4 하락추세 매도: 고가가 +1배에 터치 → T1 [MACD 하락 전환 OR 데드크로스]
			bool eDnShort		= StepT1(ref waitDnShort, hi >= Up(DnShortBand), revDn, t1BarsDn);
			// spec 5.5 하락추세 매수: RSI < 30 AND 저가가 −3배에 터치 → T1 [MACD 상승 전환 OR 골든크로스]
			bool eDnLong		= StepT1(ref waitDnLong, r0 < DnLongRsi && lo <= Dn(DnLongBand), revUp, t1BarsDn);
			// spec 5.6 상승 전환 의심 매수: 저가가 −1배에 터치 → T1 [MACD 상승 전환 OR 골든크로스]
			bool eDnSusLong		= StepT1(ref waitDnSusLong, lo <= Dn(DnSusLongBand), revUp, t1BarsDn);
			// spec 5.7 상승 전환 의심 매도: 종가가 +2배 위 → T1 [MACD 하락 전환 OR 데드크로스]
			bool eDnSusShort	= StepT1(ref waitDnSusShort, c0 > Up(DnSusShortBand), revDn, t1BarsDn);
			// spec 5.8 횡보 매수: (저가가 −2배에 터치 → T1 [MACD 상승 전환 OR 골든크로스]) 또는 T2. 동시 충족은 1회 (spec T3)
			bool t1SideLong		= StepT1(ref waitSideLong, lo <= Dn(SideBand), revUp, t1BarsSide);
			bool eSideLong		= t1SideLong || tBull;
			// spec 5.9 횡보 매도: (고가가 +2배에 터치 → T1 [MACD 하락 전환 OR 데드크로스]) 또는 T2
			bool t1SideShort	= StepT1(ref waitSideShort, hi >= Up(SideBand), revDn, t1BarsSide);
			bool eSideShort		= t1SideShort || tBear;

			entryUpLong[0]		= eUpLong;
			entryUpSusLong[0]	= eUpSusLong;
			entryUpSusShort[0]	= eUpSusShort;
			entryDnShort[0]		= eDnShort;
			entryDnLong[0]		= eDnLong;
			entryDnSusLong[0]	= eDnSusLong;
			entryDnSusShort[0]	= eDnSusShort;
			entrySideLong[0]	= eSideLong;
			entrySideShort[0]	= eSideShort;

			// 신호 로그와 검증용 화살표. 추세와 무관하게 9개 신호를 모두 남긴다
			if (eUpLong)		MarkSignal("EntryUpLong", true, 0, Brushes.Lime);
			if (eUpSusLong)		MarkSignal("EntryUpSusLong", true, 1, Brushes.YellowGreen);
			if (eDnLong)		MarkSignal("EntryDnLong", true, 2, Brushes.Cyan);
			if (eDnSusLong)		MarkSignal("EntryDnSusLong", true, 3, Brushes.DeepSkyBlue);
			if (eSideLong)		MarkSignal("EntrySideLong", true, 4, Brushes.Yellow);
			if (eDnShort)		MarkSignal("EntryDnShort", false, 0, Brushes.Magenta);
			if (eDnSusShort)	MarkSignal("EntryDnSusShort", false, 1, Brushes.Orchid);
			if (eUpSusShort)	MarkSignal("EntryUpSusShort", false, 2, Brushes.Red);
			if (eSideShort)		MarkSignal("EntrySideShort", false, 3, Brushes.Orange);
		}

		/// <summary>
		/// spec 2장 T1. 한 봉에 한 번 호출한다. 진입 신호가 나가면 true.
		/// </summary>
		private bool StepT1(ref int wait, bool bandCondition, bool reversal, int bars)
		{
			bool fire = false;

			// 먼저 기존 대기로 신호를 판정한다
			if (wait >= 0)
			{
				wait++;						// t 다음 봉이 1
				if (reversal)
				{
					fire = true;			// 밴드 조건 1번당 신호 1번: 대기 소멸
					wait = -1;
				}
				else if (wait >= bars)
					wait = -1;				// N봉 안에 반전 신호가 없으면 무효
			}

			if (bandCondition)
			{
				// T1SameBar: 밴드 조건이 충족된 봉 자체에 반전 신호가 있으면 그 봉에서 바로 신호를 낸다.
				// 꺼져 있으면 봉 t의 반전 신호는 세지 않고 다음 봉부터 센다
				if (T1SameBar && reversal && !fire)
				{
					fire = true;
					wait = -1;
				}
				else
					wait = 0;				// 이 봉을 새 t로 등록한다 (다음 봉부터 센다)
			}

			return fire;
		}

		// 밴드 배수(0 = 중심선, 1~3)에 해당하는 위쪽 밴드 값
		private double Up(int level)
		{
			return level <= 0 ? channels.Mid[0] : level == 1 ? channels.Up1[0] : level == 2 ? channels.Up2[0] : channels.Up3[0];
		}

		// 밴드 배수(0 = 중심선, 1~3)에 해당하는 아래쪽 밴드 값
		private double Dn(int level)
		{
			return level <= 0 ? channels.Mid[0] : level == 1 ? channels.Dn1[0] : level == 2 ? channels.Dn2[0] : channels.Dn3[0];
		}

		/// <summary>
		/// 진입 신호 로그와 검증용 화살표. slot은 같은 봉에서 화살표가 겹치지 않게 하는 순번.
		/// </summary>
		private void MarkSignal(string name, bool isLong, int slot, Brush brush)
		{
			Print(string.Format("[{0}][{1}][TQ_Signals] 진입 신호 {2}", Time[0], Instrument.FullName, name));

			if (!ShowDebugVisuals)
				return;

			string	tag		= "TQ_" + name + "_" + CurrentBar;
			double	offset	= TickSize * 8 * (slot + 1);
			if (isLong)
				Draw.ArrowUp(this, tag, false, 0, Low[0] - offset, brush);
			else
				Draw.ArrowDown(this, tag, false, 0, High[0] + offset, brush);
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
		[Display(Name = "CrossLow", Description = "골든크로스 필터 기준", GroupName = "Parameters", Order = 2)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossHigh", Description = "데드크로스 필터 기준", GroupName = "Parameters", Order = 3)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간", GroupName = "Parameters", Order = 4)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SwingBars", Description = "전고점·전저점 구간 (신호 봉 포함)", GroupName = "Parameters", Order = 5)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandMidPeriod", Description = "ATR 채널 중심선 EMA 기간", GroupName = "Parameters", Order = 6)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandAtrPeriod", Description = "ATR 채널 ATR 기간", GroupName = "Parameters", Order = 7)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult1", Description = "밴드 배수 1", GroupName = "Parameters", Order = 8)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult2", Description = "밴드 배수 2", GroupName = "Parameters", Order = 9)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult3", Description = "밴드 배수 3", GroupName = "Parameters", Order = 10)]
		public double BandMult3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdFast", Description = "MACD 단기", GroupName = "Parameters", Order = 11)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSlow", Description = "MACD 장기", GroupName = "Parameters", Order = 12)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSmooth", Description = "MACD 시그널", GroupName = "Parameters", Order = 13)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodK", Description = "스토캐스틱 %K 길이", GroupName = "Parameters", Order = 14)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochSmooth", Description = "스토캐스틱 %K 스무딩", GroupName = "Parameters", Order = 15)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodD", Description = "스토캐스틱 %D 스무딩", GroupName = "Parameters", Order = 16)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SmaPeriod", Description = "마지막 조각 청산용 이동평균 기간 (spec 5장, 18)", GroupName = "Parameters", Order = 17)]
		public int SmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdConfirmBars", Description = "MACD 전환 확인 봉 수 (1 = 직전 봉 대비, 2 = 2봉 연속)", GroupName = "Parameters", Order = 18)]
		public int MacdConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "MacdMinChangeAtr", Description = "MACD 전환 최소 변화폭 (ATR 배수, 0 = 사용 안 함)", GroupName = "Parameters", Order = 19)]
		public double MacdMinChangeAtr { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 화살표 표시", GroupName = "Parameters", Order = 20)]
		public bool ShowDebugVisuals { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "UpLongBand", Description = "5.1 상승추세 매수: 저가가 몇 배 밴드에 닿으면 (spec 1)", GroupName = "Parameters", Order = 21)]
		public int UpLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "UpSusLongBand", Description = "5.2 하락 전환 의심 매수: 종가가 몇 배 밴드 아래면 (spec 2)", GroupName = "Parameters", Order = 22)]
		public int UpSusLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "UpSusShortBand", Description = "5.3 하락 전환 의심 매도: 고가가 몇 배 밴드에 닿으면 (spec 1)", GroupName = "Parameters", Order = 23)]
		public int UpSusShortBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "DnShortBand", Description = "5.4 하락추세 매도: 고가가 몇 배 밴드에 닿으면 (spec 1)", GroupName = "Parameters", Order = 24)]
		public int DnShortBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "DnLongBand", Description = "5.5 하락추세 매수: 저가가 몇 배 밴드에 닿으면 (spec 3)", GroupName = "Parameters", Order = 25)]
		public int DnLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "DnLongRsi", Description = "5.5 하락추세 매수: RSI가 이 값 미만 (spec 30)", GroupName = "Parameters", Order = 26)]
		public double DnLongRsi { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "DnSusLongBand", Description = "5.6 상승 전환 의심 매수: 저가가 몇 배 밴드에 닿으면 (spec 1)", GroupName = "Parameters", Order = 27)]
		public int DnSusLongBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "DnSusShortBand", Description = "5.7 상승 전환 의심 매도: 종가가 몇 배 밴드 위면 (spec 2)", GroupName = "Parameters", Order = 28)]
		public int DnSusShortBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, 3)]
		[Display(Name = "SideBand", Description = "5.8·5.9 횡보: 저가(고가)가 몇 배 밴드에 닿으면 (spec 2)", GroupName = "Parameters", Order = 29)]
		public int SideBand { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "T1WindowUp", Description = "상승추세·하락 전환 의심 진입(5.1~5.3)의 T1 대기 봉 수. 0 = T1Window 사용", GroupName = "Parameters", Order = 30)]
		public int T1WindowUp { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "T1WindowDn", Description = "하락추세·상승 전환 의심 진입(5.4~5.7)의 T1 대기 봉 수. 0 = T1Window 사용", GroupName = "Parameters", Order = 31)]
		public int T1WindowDn { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "T1WindowSide", Description = "횡보 진입(5.8·5.9)의 T1 대기 봉 수. 0 = T1Window 사용", GroupName = "Parameters", Order = 32)]
		public int T1WindowSide { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "T2CountConfirmBars", Description = "T2(MACD 전환 AND 크로스)에서 MACD 확인에 걸린 봉 수만큼 앞에서 나온 크로스도 인정", GroupName = "Parameters", Order = 33)]
		public bool T2CountConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "T1SameBar", Description = "밴드 조건이 충족된 봉 자체의 반전 신호도 인정. 끄면 다음 봉부터 센다", GroupName = "Parameters", Order = 34)]
		public bool T1SameBar { get; set; }

		// 표시 전용 토글. [NinjaScriptProperty]를 붙이지 않아 생성자 시그니처가 바뀌지 않는다(전략 호출부·생성 코드 그대로)
		[Display(Name = "ShowPlots", Description = "플롯 선 표시. 끄면 차트에서 선이 안 보임(값·전략 동작은 그대로)", GroupName = "Parameters", Order = 35)]
		public bool ShowPlots { get; set; }

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
		public Series<double> Sma { get { return Values[4]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SwingHigh5 { get { return Values[5]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> SwingLow5 { get { return Values[6]; } }

		// T1 대기 상태 (전략의 화면 표시용). -1 = 대기 없음, 0 = 밴드 조건 봉, 1 이상 = 그 뒤 지난 봉 수
		[Browsable(false)]
		[XmlIgnore]
		public int WaitUpLong { get { Update(); return waitUpLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitUpSusLong { get { Update(); return waitUpSusLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitUpSusShort { get { Update(); return waitUpSusShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitDnShort { get { Update(); return waitDnShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitDnLong { get { Update(); return waitDnLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitDnSusLong { get { Update(); return waitDnSusLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitDnSusShort { get { Update(); return waitDnSusShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitSideLong { get { Update(); return waitSideLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitSideShort { get { Update(); return waitSideShort; } }

		// 추세별 실제 T1 대기 봉 수 (화면 표시용)
		[Browsable(false)]
		[XmlIgnore]
		public int T1BarsUp { get { Update(); return t1BarsUp; } }

		[Browsable(false)]
		[XmlIgnore]
		public int T1BarsDn { get { Update(); return t1BarsDn; } }

		[Browsable(false)]
		[XmlIgnore]
		public int T1BarsSide { get { Update(); return t1BarsSide; } }

		// 이벤트: Update()로 호출 시점 값을 최신화
		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> MacdUp { get { Update(); return macdUp; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> MacdDown { get { Update(); return macdDown; } }

		// 화면 표시용: MACD 히스토그램의 방향 (연속으로 늘거나 주는 동안 계속 참). 매매 판단에는 쓰지 않는다
		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> MacdRising { get { Update(); return macdRising; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> MacdFalling { get { Update(); return macdFalling; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> Golden { get { Update(); return golden; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> Dead { get { Update(); return dead; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> T2Bull { get { Update(); return t2Bull; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> T2Bear { get { Update(); return t2Bear; } }

		// 진입 신호 (spec 5.1~5.9)
		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryUpLong { get { Update(); return entryUpLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryUpSusLong { get { Update(); return entryUpSusLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryUpSusShort { get { Update(); return entryUpSusShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryDnShort { get { Update(); return entryDnShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryDnLong { get { Update(); return entryDnLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryDnSusLong { get { Update(); return entryDnSusLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<bool> EntryDnSusShort { get { Update(); return entryDnSusShort; } }

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
		public TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals, int upLongBand, int upSusLongBand, int upSusShortBand, int dnShortBand, int dnLongBand, double dnLongRsi, int dnSusLongBand, int dnSusShortBand, int sideBand, int t1WindowUp, int t1WindowDn, int t1WindowSide, bool t2CountConfirmBars, bool t1SameBar)
		{
			return TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals, upLongBand, upSusLongBand, upSusShortBand, dnShortBand, dnLongBand, dnLongRsi, dnSusLongBand, dnSusShortBand, sideBand, t1WindowUp, t1WindowDn, t1WindowSide, t2CountConfirmBars, t1SameBar);
		}

		public TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input, int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals, int upLongBand, int upSusLongBand, int upSusShortBand, int dnShortBand, int dnLongBand, double dnLongRsi, int dnSusLongBand, int dnSusShortBand, int sideBand, int t1WindowUp, int t1WindowDn, int t1WindowSide, bool t2CountConfirmBars, bool t1SameBar)
		{
			if (cacheTQ_Signals != null)
				for (int idx = 0; idx < cacheTQ_Signals.Length; idx++)
					if (cacheTQ_Signals[idx] != null && cacheTQ_Signals[idx].T1Window == t1Window && cacheTQ_Signals[idx].CrossFilterBars == crossFilterBars && cacheTQ_Signals[idx].CrossLow == crossLow && cacheTQ_Signals[idx].CrossHigh == crossHigh && cacheTQ_Signals[idx].RsiPeriod == rsiPeriod && cacheTQ_Signals[idx].SwingBars == swingBars && cacheTQ_Signals[idx].BandMidPeriod == bandMidPeriod && cacheTQ_Signals[idx].BandAtrPeriod == bandAtrPeriod && cacheTQ_Signals[idx].BandMult1 == bandMult1 && cacheTQ_Signals[idx].BandMult2 == bandMult2 && cacheTQ_Signals[idx].BandMult3 == bandMult3 && cacheTQ_Signals[idx].MacdFast == macdFast && cacheTQ_Signals[idx].MacdSlow == macdSlow && cacheTQ_Signals[idx].MacdSmooth == macdSmooth && cacheTQ_Signals[idx].StochPeriodK == stochPeriodK && cacheTQ_Signals[idx].StochSmooth == stochSmooth && cacheTQ_Signals[idx].StochPeriodD == stochPeriodD && cacheTQ_Signals[idx].SmaPeriod == smaPeriod && cacheTQ_Signals[idx].MacdConfirmBars == macdConfirmBars && cacheTQ_Signals[idx].MacdMinChangeAtr == macdMinChangeAtr && cacheTQ_Signals[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Signals[idx].UpLongBand == upLongBand && cacheTQ_Signals[idx].UpSusLongBand == upSusLongBand && cacheTQ_Signals[idx].UpSusShortBand == upSusShortBand && cacheTQ_Signals[idx].DnShortBand == dnShortBand && cacheTQ_Signals[idx].DnLongBand == dnLongBand && cacheTQ_Signals[idx].DnLongRsi == dnLongRsi && cacheTQ_Signals[idx].DnSusLongBand == dnSusLongBand && cacheTQ_Signals[idx].DnSusShortBand == dnSusShortBand && cacheTQ_Signals[idx].SideBand == sideBand && cacheTQ_Signals[idx].T1WindowUp == t1WindowUp && cacheTQ_Signals[idx].T1WindowDn == t1WindowDn && cacheTQ_Signals[idx].T1WindowSide == t1WindowSide && cacheTQ_Signals[idx].T2CountConfirmBars == t2CountConfirmBars && cacheTQ_Signals[idx].T1SameBar == t1SameBar && cacheTQ_Signals[idx].EqualsInput(input))
						return cacheTQ_Signals[idx];
			return CacheIndicator<TeamQuant.TQ_Signals>(new TeamQuant.TQ_Signals(){ T1Window = t1Window, CrossFilterBars = crossFilterBars, CrossLow = crossLow, CrossHigh = crossHigh, RsiPeriod = rsiPeriod, SwingBars = swingBars, BandMidPeriod = bandMidPeriod, BandAtrPeriod = bandAtrPeriod, BandMult1 = bandMult1, BandMult2 = bandMult2, BandMult3 = bandMult3, MacdFast = macdFast, MacdSlow = macdSlow, MacdSmooth = macdSmooth, StochPeriodK = stochPeriodK, StochSmooth = stochSmooth, StochPeriodD = stochPeriodD, SmaPeriod = smaPeriod, MacdConfirmBars = macdConfirmBars, MacdMinChangeAtr = macdMinChangeAtr, ShowDebugVisuals = showDebugVisuals, UpLongBand = upLongBand, UpSusLongBand = upSusLongBand, UpSusShortBand = upSusShortBand, DnShortBand = dnShortBand, DnLongBand = dnLongBand, DnLongRsi = dnLongRsi, DnSusLongBand = dnSusLongBand, DnSusShortBand = dnSusShortBand, SideBand = sideBand, T1WindowUp = t1WindowUp, T1WindowDn = t1WindowDn, T1WindowSide = t1WindowSide, T2CountConfirmBars = t2CountConfirmBars, T1SameBar = t1SameBar }, input, ref cacheTQ_Signals);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals, int upLongBand, int upSusLongBand, int upSusShortBand, int dnShortBand, int dnLongBand, double dnLongRsi, int dnSusLongBand, int dnSusShortBand, int sideBand, int t1WindowUp, int t1WindowDn, int t1WindowSide, bool t2CountConfirmBars, bool t1SameBar)
		{
			return indicator.TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals, upLongBand, upSusLongBand, upSusShortBand, dnShortBand, dnLongBand, dnLongRsi, dnSusLongBand, dnSusShortBand, sideBand, t1WindowUp, t1WindowDn, t1WindowSide, t2CountConfirmBars, t1SameBar);
		}

		public Indicators.TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input , int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals, int upLongBand, int upSusLongBand, int upSusShortBand, int dnShortBand, int dnLongBand, double dnLongRsi, int dnSusLongBand, int dnSusShortBand, int sideBand, int t1WindowUp, int t1WindowDn, int t1WindowSide, bool t2CountConfirmBars, bool t1SameBar)
		{
			return indicator.TQ_Signals(input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals, upLongBand, upSusLongBand, upSusShortBand, dnShortBand, dnLongBand, dnLongRsi, dnSusLongBand, dnSusShortBand, sideBand, t1WindowUp, t1WindowDn, t1WindowSide, t2CountConfirmBars, t1SameBar);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals, int upLongBand, int upSusLongBand, int upSusShortBand, int dnShortBand, int dnLongBand, double dnLongRsi, int dnSusLongBand, int dnSusShortBand, int sideBand, int t1WindowUp, int t1WindowDn, int t1WindowSide, bool t2CountConfirmBars, bool t1SameBar)
		{
			return indicator.TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals, upLongBand, upSusLongBand, upSusShortBand, dnShortBand, dnLongBand, dnLongRsi, dnSusLongBand, dnSusShortBand, sideBand, t1WindowUp, t1WindowDn, t1WindowSide, t2CountConfirmBars, t1SameBar);
		}

		public Indicators.TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input , int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals, int upLongBand, int upSusLongBand, int upSusShortBand, int dnShortBand, int dnLongBand, double dnLongRsi, int dnSusLongBand, int dnSusShortBand, int sideBand, int t1WindowUp, int t1WindowDn, int t1WindowSide, bool t2CountConfirmBars, bool t1SameBar)
		{
			return indicator.TQ_Signals(input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals, upLongBand, upSusLongBand, upSusShortBand, dnShortBand, dnLongBand, dnLongRsi, dnSusLongBand, dnSusShortBand, sideBand, t1WindowUp, t1WindowDn, t1WindowSide, t2CountConfirmBars, t1SameBar);
		}
	}
}

#endregion
