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
	/// 3분봉 신호 엔진: 숫자 값, 이벤트, 6개 진입 신호 (spec 2장·5장, docs/interface.md)
	/// 포지션·주문·시간대와 무관한 값만 낸다.
	/// </summary>
	public class TQ_Signals : Indicator
	{
		private TQ_ATRChannels	channels;
		private Stochastics		stoch;
		private RSI				rsi;
		private TQ_MacdTurn	macdTurn;
		private SMA				sma;
		private MAX				swingHigh;
		private MIN				swingLow;
		private int				minBars;
		private int				t1Bars;		// 실제 T1 대기 봉 수 = T1Window + MacdConfirmBars - 1

		// spec 2장 T1 대기 상태: 진입 신호마다 따로 둔다. -1 = 대기 없음, 0 = 밴드 조건 봉 t, 1~N = t 이후 봉 수
		private int waitUpLong;
		private int waitUpShort;
		private int waitDnLong;
		private int waitDnShort;
		private int waitSideLong;
		private int waitSideShort;

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
				SmaPeriod					= 20;
				MacdConfirmBars				= 2;
				MacdMinChangeAtr			= 0;
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

				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod, BandMult1, BandMult2, BandMult3);
				stoch		= Stochastics(StochPeriodD, StochPeriodK, StochSmooth);	// 인자 순서: periodD, periodK, smooth
				rsi			= RSI(RsiPeriod, 3);									// 두 번째 인자는 평균선용, RSI 값과 무관
				macdTurn	= TQ_MacdTurn(MacdFast, MacdSlow, MacdSmooth, MacdConfirmBars, BandAtrPeriod, MacdMinChangeAtr);
				sma			= SMA(SmaPeriod);
				swingHigh	= MAX(High, SwingBars);
				swingLow	= MIN(Low, SwingBars);

				// 참조하는 가장 먼 인덱스: 다이버전스 DivTo봉 전, 크로스 필터 CrossFilterBars - 1봉 전, MACD 전환 MacdConfirmBars봉 전
				minBars		= Math.Max(MacdConfirmBars, Math.Max(DivTo, CrossFilterBars - 1));

				// spec 2장 T1: MACD 전환을 확인하는 데 드는 봉 수만큼 대기 봉 수를 늘린다.
				// 확인 봉 수 2면 반전이 t+1에 시작해도 t+2에야 확인되므로, 늘리지 않으면 기회가 N-1번으로 준다
				t1Bars		= T1Window + MacdConfirmBars - 1;

				waitUpLong		= -1;
				waitUpShort		= -1;
				waitDnLong		= -1;
				waitDnShort		= -1;
				waitSideLong	= -1;
				waitSideShort	= -1;
			}
		}

		protected override void OnBarUpdate()
		{
			// 숫자 값 (docs/interface.md) 
			K[0]			= stoch.K[0];
			D[0]			= stoch.D[0];
			Rsi[0]			= rsi[0];
			MacdHist[0]		= macdTurn.Hist[0];
			Sma20[0]		= sma[0];
			SwingHigh5[0]	= swingHigh[0];	// spec 2장 전고점: 현재 봉 포함 최근 SwingBars봉
			SwingLow5[0]	= swingLow[0];	// spec 2장 전저점

			// 데이터 부족 가드: 이벤트·신호는 false로 남는다
			if (CurrentBar < minBars)
				return;

			double c0 = Close[0];
			double c1 = Close[1];
			double k0 = K[0];
			double k1 = K[1];
			double d0 = D[0];
			double d1 = D[1];
			double r0 = Rsi[0];

			// spec 2장 상방/하방 돌파: 직전 봉 종가는 밴드 안쪽, 현재 봉 종가가 바깥 (갭 포함, spec T3)
			bool xUp1 = c1 <= channels.Up1[1] && c0 > channels.Up1[0];
			bool xUp2 = c1 <= channels.Up2[1] && c0 > channels.Up2[0];
			bool xUp3 = c1 <= channels.Up3[1] && c0 > channels.Up3[0];
			bool xDn1 = c1 >= channels.Dn1[1] && c0 < channels.Dn1[0];
			bool xDn2 = c1 >= channels.Dn2[1] && c0 < channels.Dn2[0];
			bool xDn3 = c1 >= channels.Dn3[1] && c0 < channels.Dn3[0];

			// spec 2장 MACD 상승/하락 전환: 판정은 TQ_MacdTurn이 한다 (연속 MacdConfirmBars봉 + 최소 변화폭)
			bool mUp = macdTurn.Up[0];
			bool mDn = macdTurn.Down[0];

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

			// spec 2장 %K 80 상향 돌파 / 20 하방 돌파
			bool k80Up	= k1 <= CrossHigh && k0 > CrossHigh;
			bool k20Dn	= k1 >= CrossLow && k0 < CrossLow;

			// spec 2장 하락 다이버전스: DivFrom~DivTo봉 전 구간의 최고 고가 봉과 비교.
			// 최고 고가 봉이 여러 개면 가장 최근 봉 (최근 → 과거 순으로 보면서 더 클 때만 바꾼다)
			bool div = false;
			if (DivFrom <= DivTo)
			{
				int		hiIdx	= DivFrom;
				double	hi		= High[DivFrom];
				for (int i = DivFrom + 1; i <= DivTo; i++)
				{
					if (High[i] > hi)
					{
						hi		= High[i];
						hiIdx	= i;
					}
				}
				div = High[0] > hi && r0 < Rsi[hiIdx];
			}

			crossAboveUp1[0]	= xUp1;
			crossAboveUp2[0]	= xUp2;
			crossAboveUp3[0]	= xUp3;
			crossBelowDn1[0]	= xDn1;
			crossBelowDn2[0]	= xDn2;
			crossBelowDn3[0]	= xDn3;
			macdUp[0]			= mUp;
			macdDown[0]			= mDn;
			golden[0]			= gold;
			dead[0]				= dd;
			k80CrossUp[0]		= k80Up;
			k20CrossDown[0]		= k20Dn;
			bearDiv[0]			= div;

			// spec 2장 T2: 같은 봉에서 둘 다, 또는 하나가 나온 바로 다음 봉에 나머지
			bool tBull = (mUp && gold) || (macdUp[1] && gold) || (golden[1] && mUp);
			bool tBear = (mDn && dd) || (macdDown[1] && dd) || (dead[1] && mDn);
			t2Bull[0]			= tBull;
			t2Bear[0]			= tBear;

			// 반전 신호 (spec 5장 진입 조건의 [대괄호])
			bool revUp	= mUp || gold;
			bool revDn	= mDn || dd;

			// spec 5.1 상승추세 롱: 종가 −1배 하방 돌파 → T1 [MACD 상승 전환 OR 골든크로스]
			bool eUpLong	= StepT1(ref waitUpLong, xDn1, revUp);
			// spec 5.2 상승추세 숏: 고가 +3배 터치 AND RSI ≥ 70 AND 하락 다이버전스 → T1 [MACD 하락 전환 OR 데드크로스]
			bool eUpShort	= StepT1(ref waitUpShort, High[0] >= channels.Up3[0] && r0 >= RsiHigh && div, revDn);
			// spec 5.3 하락추세 롱: 종가 −3배 하방 돌파 → T1 [RSI < 20 OR MACD 상승 전환 OR 골든크로스]
			bool eDnLong	= StepT1(ref waitDnLong, xDn3, r0 < RsiLow || revUp);
			// spec 5.4 하락추세 숏: 종가 +2배 상방 돌파 → T1 [MACD 하락 전환 OR 데드크로스 OR (RSI ≥ 70 AND 하락 다이버전스)]
			bool eDnShort	= StepT1(ref waitDnShort, xUp2, revDn || (r0 >= RsiHigh && div));
			// spec 5.5 횡보 롱: (−2배 또는 −3배 하방 돌파 → T1 [MACD 상승 전환 OR 골든크로스]) 또는 T2. 동시 충족은 1회 (spec T3)
			bool t1SideLong	= StepT1(ref waitSideLong, xDn2 || xDn3, revUp);
			bool eSideLong	= t1SideLong || tBull;
			// spec 5.6 횡보 숏: (+2배 또는 +3배 상방 돌파 → T1 [MACD 하락 전환 OR 데드크로스]) 또는 T2
			bool t1SideShort	= StepT1(ref waitSideShort, xUp2 || xUp3, revDn);
			bool eSideShort		= t1SideShort || tBear;

			entryUpLong[0]		= eUpLong;
			entryUpShort[0]		= eUpShort;
			entryDnLong[0]		= eDnLong;
			entryDnShort[0]		= eDnShort;
			entrySideLong[0]	= eSideLong;
			entrySideShort[0]	= eSideShort;

			if (eUpLong)	MarkSignal("EntryUpLong", true, 0, Brushes.Lime);
			if (eDnLong)	MarkSignal("EntryDnLong", true, 1, Brushes.Cyan);
			if (eSideLong)	MarkSignal("EntrySideLong", true, 2, Brushes.Yellow);
			if (eUpShort)	MarkSignal("EntryUpShort", false, 0, Brushes.Red);
			if (eDnShort)	MarkSignal("EntryDnShort", false, 1, Brushes.Magenta);
			if (eSideShort)	MarkSignal("EntrySideShort", false, 2, Brushes.Orange);
		}

		/// <summary>
		/// spec 2장 T1. 한 봉에 한 번 호출한다. 진입 신호가 나가면 true.
		/// </summary>
		private bool StepT1(ref int wait, bool bandCondition, bool reversal)
		{
			bool fire = false;

			// 먼저 기존 대기로 신호를 판정한다. 봉 t 자체(wait == 0이 되는 봉)의 반전 신호는 세지 않는다
			if (wait >= 0)
			{
				wait++;						// t 다음 봉이 1
				if (reversal)
				{
					fire = true;			// 밴드 조건 1번당 신호 1번: 대기 소멸
					wait = -1;
				}
				else if (wait >= t1Bars)
					wait = -1;				// N봉 안에 반전 신호가 없으면 무효
			}

			// 그다음 이 봉이 밴드 조건 봉이면 새 t로 등록한다 (다음 봉부터 센다)
			if (bandCondition)
				wait = 0;

			return fire;
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
		[Range(1, int.MaxValue)]
		[Display(Name = "BandMidPeriod", Description = "ATR 채널 중심선 EMA 기간", GroupName = "Parameters", Order = 10)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandAtrPeriod", Description = "ATR 채널 ATR 기간", GroupName = "Parameters", Order = 11)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult1", Description = "밴드 배수 1", GroupName = "Parameters", Order = 12)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult2", Description = "밴드 배수 2", GroupName = "Parameters", Order = 13)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult3", Description = "밴드 배수 3", GroupName = "Parameters", Order = 14)]
		public double BandMult3 { get; set; }

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
		[Display(Name = "StochPeriodK", Description = "스토캐스틱 %K 길이", GroupName = "Parameters", Order = 18)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochSmooth", Description = "스토캐스틱 %K 스무딩", GroupName = "Parameters", Order = 19)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodD", Description = "스토캐스틱 %D 스무딩", GroupName = "Parameters", Order = 20)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SmaPeriod", Description = "3분봉 SMA 기간 (spec 5.1 강한 모멘텀 청산)", GroupName = "Parameters", Order = 21)]
		public int SmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdConfirmBars", Description = "MACD 전환 확인 봉 수 (1 = 직전 봉 대비, 2 = 2봉 연속)", GroupName = "Parameters", Order = 22)]
		public int MacdConfirmBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "MacdMinChangeAtr", Description = "MACD 전환 최소 변화폭 (ATR 배수, 0 = 사용 안 함)", GroupName = "Parameters", Order = 23)]
		public double MacdMinChangeAtr { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ShowDebugVisuals", Description = "검증용 화살표 표시", GroupName = "Parameters", Order = 24)]
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

		// T1 대기 상태 (전략의 화면 표시용). -1 = 대기 없음, 0 = 밴드 조건 봉, 1 이상 = 그 뒤 지난 봉 수
		[Browsable(false)]
		[XmlIgnore]
		public int WaitUpLong { get { Update(); return waitUpLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitUpShort { get { Update(); return waitUpShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitDnLong { get { Update(); return waitDnLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitDnShort { get { Update(); return waitDnShort; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitSideLong { get { Update(); return waitSideLong; } }

		[Browsable(false)]
		[XmlIgnore]
		public int WaitSideShort { get { Update(); return waitSideShort; } }

		// 실제 T1 대기 봉 수 = T1Window + MacdConfirmBars - 1
		[Browsable(false)]
		[XmlIgnore]
		public int T1Bars { get { Update(); return t1Bars; } }

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
		public TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals)
		{
			return TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals);
		}

		public TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input, int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals)
		{
			if (cacheTQ_Signals != null)
				for (int idx = 0; idx < cacheTQ_Signals.Length; idx++)
					if (cacheTQ_Signals[idx] != null && cacheTQ_Signals[idx].T1Window == t1Window && cacheTQ_Signals[idx].CrossFilterBars == crossFilterBars && cacheTQ_Signals[idx].CrossLow == crossLow && cacheTQ_Signals[idx].CrossHigh == crossHigh && cacheTQ_Signals[idx].RsiPeriod == rsiPeriod && cacheTQ_Signals[idx].RsiHigh == rsiHigh && cacheTQ_Signals[idx].RsiLow == rsiLow && cacheTQ_Signals[idx].DivFrom == divFrom && cacheTQ_Signals[idx].DivTo == divTo && cacheTQ_Signals[idx].SwingBars == swingBars && cacheTQ_Signals[idx].BandMidPeriod == bandMidPeriod && cacheTQ_Signals[idx].BandAtrPeriod == bandAtrPeriod && cacheTQ_Signals[idx].BandMult1 == bandMult1 && cacheTQ_Signals[idx].BandMult2 == bandMult2 && cacheTQ_Signals[idx].BandMult3 == bandMult3 && cacheTQ_Signals[idx].MacdFast == macdFast && cacheTQ_Signals[idx].MacdSlow == macdSlow && cacheTQ_Signals[idx].MacdSmooth == macdSmooth && cacheTQ_Signals[idx].StochPeriodK == stochPeriodK && cacheTQ_Signals[idx].StochSmooth == stochSmooth && cacheTQ_Signals[idx].StochPeriodD == stochPeriodD && cacheTQ_Signals[idx].SmaPeriod == smaPeriod && cacheTQ_Signals[idx].MacdConfirmBars == macdConfirmBars && cacheTQ_Signals[idx].MacdMinChangeAtr == macdMinChangeAtr && cacheTQ_Signals[idx].ShowDebugVisuals == showDebugVisuals && cacheTQ_Signals[idx].EqualsInput(input))
						return cacheTQ_Signals[idx];
			return CacheIndicator<TeamQuant.TQ_Signals>(new TeamQuant.TQ_Signals(){ T1Window = t1Window, CrossFilterBars = crossFilterBars, CrossLow = crossLow, CrossHigh = crossHigh, RsiPeriod = rsiPeriod, RsiHigh = rsiHigh, RsiLow = rsiLow, DivFrom = divFrom, DivTo = divTo, SwingBars = swingBars, BandMidPeriod = bandMidPeriod, BandAtrPeriod = bandAtrPeriod, BandMult1 = bandMult1, BandMult2 = bandMult2, BandMult3 = bandMult3, MacdFast = macdFast, MacdSlow = macdSlow, MacdSmooth = macdSmooth, StochPeriodK = stochPeriodK, StochSmooth = stochSmooth, StochPeriodD = stochPeriodD, SmaPeriod = smaPeriod, MacdConfirmBars = macdConfirmBars, MacdMinChangeAtr = macdMinChangeAtr, ShowDebugVisuals = showDebugVisuals }, input, ref cacheTQ_Signals);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals);
		}

		public Indicators.TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input , int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TeamQuant.TQ_Signals TQ_Signals(int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(Input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals);
		}

		public Indicators.TeamQuant.TQ_Signals TQ_Signals(ISeries<double> input , int t1Window, int crossFilterBars, double crossLow, double crossHigh, int rsiPeriod, double rsiHigh, double rsiLow, int divFrom, int divTo, int swingBars, int bandMidPeriod, int bandAtrPeriod, double bandMult1, double bandMult2, double bandMult3, int macdFast, int macdSlow, int macdSmooth, int stochPeriodK, int stochSmooth, int stochPeriodD, int smaPeriod, int macdConfirmBars, double macdMinChangeAtr, bool showDebugVisuals)
		{
			return indicator.TQ_Signals(input, t1Window, crossFilterBars, crossLow, crossHigh, rsiPeriod, rsiHigh, rsiLow, divFrom, divTo, swingBars, bandMidPeriod, bandAtrPeriod, bandMult1, bandMult2, bandMult3, macdFast, macdSlow, macdSmooth, stochPeriodK, stochSmooth, stochPeriodD, smaPeriod, macdConfirmBars, macdMinChangeAtr, showDebugVisuals);
		}
	}
}

#endregion
