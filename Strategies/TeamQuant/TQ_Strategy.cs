#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.TeamQuant;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.TeamQuant
{
	/// <summary>
	/// 대회 전략 (docs/spec.md). 3분봉 차트에 올리고 10분봉은 내부에서 추가한다.
	/// 스켈레톤: 데이터·설정값·지표 인스턴스와 로그만 있고 주문 로직은 없다.
	/// </summary>
	public class TQ_Strategy : Strategy
	{
		private TQ_ATRChannels	channels;	// 3분봉 밴드 (spec 2장)
		private TQ_Regime		regime;		// 10분봉 레짐 (spec 3장)
		private TQ_Signals		signals;	// 3분봉 신호 (spec 2장·5장)

		// TODO spec 1장 3: 진입 시점 레짐 저장 (청산이 끝날 때까지 고정)
		// TODO spec 4장: 신호 봉 기준 손절가, 본절 이동 여부
		// TODO spec 5장 청산 공통 규칙: 1차 익절 여부, 남은 수량
		// TODO spec 5.1: 약한/강한 모멘텀 상태
		// TODO spec 5.4: 2차 익절 여부
		// TODO spec 5.5·5.6: 횡보 익절 세트 A/B 선택
		// TODO spec 7장: 종목별 진입 횟수 (분할 진입 여러 건은 신호 1회)

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= "팀 퀀트 대회 전략 (docs/spec.md)";
				Name				= "TQ_Strategy";
				Calculate			= Calculate.OnBarClose;	// spec 1장 1: 봉 마감 후 판단
				EntriesPerDirection	= 1;
				EntryHandling		= EntryHandling.UniqueEntries;	// 시그널명별 분할 진입

				// spec 8장 기본값
				BandMidPeriod		= 26;
				BandAtrPeriod		= 14;
				BandMult1			= 1;
				BandMult2			= 2;
				BandMult3			= 3;
				MacdFast			= 12;
				MacdSlow			= 26;
				MacdSmooth			= 9;
				StochPeriodK		= 10;
				StochSmooth			= 5;
				StochPeriodD		= 5;
				CrossLow			= 20;
				CrossHigh			= 80;
				CrossFilterBars		= 5;
				RsiPeriod			= 14;
				RsiHigh				= 70;
				RsiLow				= 20;
				DivFrom				= 5;
				DivTo				= 30;
				SwingBars			= 5;
				StopPercent			= 0.2;
				T1Window			= 3;
				RegimeFast			= 20;
				RegimeMid			= 60;
				RegimeSlow			= 120;
				MomentumSmaPeriod	= 20;
				UseRule12			= false;
				EntryQuantity		= 30;
				MaxQuantity			= 40;
				ContestStartTime	= 223000;	// 22:30:00 KST
				EntryEndTime		= 1500;		// 00:15:00 KST
				FlattenTime			= 2700;		// 00:27:00 KST 봉 마감
				UseTestEntry		= false;
			}
			else if (State == State.Configure)
			{
				// 10분봉 → BarsArray[1]. 인자는 하드코딩한다
				AddDataSeries(BarsPeriodType.Minute, 10);
			}
			else if (State == State.DataLoaded)
			{
				channels	= TQ_ATRChannels(BandMidPeriod, BandAtrPeriod);
				regime		= TQ_Regime(BarsArray[1], RegimeFast, RegimeMid, RegimeSlow, UseRule12, false);
				signals		= TQ_Signals(T1Window, CrossFilterBars, CrossLow, CrossHigh, RsiPeriod, RsiHigh, RsiLow,
								DivFrom, DivTo, SwingBars, false);

				// TODO spec 8장: 밴드 배수, MACD, 스토캐스틱, 강한 모멘텀 SMA 기간은 지표에 아직 전달하지 않는다
				//               (docs/interface.md 설정표에 없음)
			}
		}

		protected override void OnBarUpdate()
		{
			// 데이터 부족 가드: 10분봉 SMA120이 채워지기 전에는 아무것도 하지 않는다
			if (CurrentBars[0] < DivTo || CurrentBars[1] < RegimeSlow)
				return;

			if (BarsInProgress == 1)
			{
				Print(string.Format("[{0}][{1}][TQ_Strategy] 10분봉 마감 레짐={2}",
					Time[0], Instrument.FullName, regime.Regime[0]));

				// TODO spec 5.2 1차 익절: 10분봉 저가 <= 10분봉 SMA20이면 즉시 익절 (주문은 BIP 0 대상)
				return;
			}

			if (BarsInProgress != 0)
				return;

			Print(string.Format("[{0}][{1}][TQ_Strategy] 레짐={2} UpLong={3} UpShort={4} DnLong={5} DnShort={6} SideLong={7} SideShort={8}",
				Time[0], Instrument.FullName, regime.Regime[0],
				signals.EntryUpLong[0], signals.EntryUpShort[0],
				signals.EntryDnLong[0], signals.EntryDnShort[0],
				signals.EntrySideLong[0], signals.EntrySideShort[0]));

			// TODO spec 7장: 종료 청산 (FlattenTime 봉 마감 시 전량 시장가)
			// TODO spec 4장: 포지션이 없을 때 손절 리셋, 체결 후 실제 체결가로 손절 재계산, 본절 이동
			// TODO spec 5장 청산 공통 규칙 + 5.1~5.6: 진입 시점 레짐의 익절·청산 상태 머신
			// TODO spec 7장: 신규 진입 시간 필터 (신호 봉이 ContestStartTime~EntryEndTime)
			// TODO spec 1장 2, 3장: 미보유 시 현재 레짐에 맞는 진입 신호로 분할 진입 (spec 6장 수량)
			// TODO UseTestEntry: 주문 흐름 검증용 임시 진입
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandMidPeriod", Description = "ATR 채널 중심선 EMA 기간", GroupName = "1. 밴드", Order = 0)]
		public int BandMidPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BandAtrPeriod", Description = "ATR 기간", GroupName = "1. 밴드", Order = 1)]
		public int BandAtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult1", Description = "밴드 배수 1", GroupName = "1. 밴드", Order = 2)]
		public double BandMult1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult2", Description = "밴드 배수 2", GroupName = "1. 밴드", Order = 3)]
		public double BandMult2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "BandMult3", Description = "밴드 배수 3", GroupName = "1. 밴드", Order = 4)]
		public double BandMult3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdFast", Description = "MACD 단기", GroupName = "2. 신호", Order = 0)]
		public int MacdFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSlow", Description = "MACD 장기", GroupName = "2. 신호", Order = 1)]
		public int MacdSlow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MacdSmooth", Description = "MACD 시그널", GroupName = "2. 신호", Order = 2)]
		public int MacdSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodK", Description = "스토캐스틱 %K 길이", GroupName = "2. 신호", Order = 3)]
		public int StochPeriodK { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochSmooth", Description = "스토캐스틱 %K 스무딩", GroupName = "2. 신호", Order = 4)]
		public int StochSmooth { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "StochPeriodD", Description = "스토캐스틱 %D 스무딩", GroupName = "2. 신호", Order = 5)]
		public int StochPeriodD { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossLow", Description = "골든크로스 필터·%K 하방 돌파 기준", GroupName = "2. 신호", Order = 6)]
		public double CrossLow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "CrossHigh", Description = "데드크로스 필터·%K 상향 돌파 기준", GroupName = "2. 신호", Order = 7)]
		public double CrossHigh { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "CrossFilterBars", Description = "크로스 필터 봉 수 (현재 봉 포함)", GroupName = "2. 신호", Order = 8)]
		public int CrossFilterBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RsiPeriod", Description = "RSI 기간", GroupName = "2. 신호", Order = 9)]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiHigh", Description = "RSI 과매수 기준 (이상)", GroupName = "2. 신호", Order = 10)]
		public double RsiHigh { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "RsiLow", Description = "RSI 과매도 기준 (미만)", GroupName = "2. 신호", Order = 11)]
		public double RsiLow { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivFrom", Description = "다이버전스 비교 구간 시작 (봉 전)", GroupName = "2. 신호", Order = 12)]
		public int DivFrom { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "DivTo", Description = "다이버전스 비교 구간 끝 (봉 전)", GroupName = "2. 신호", Order = 13)]
		public int DivTo { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "T1Window", Description = "T1 대기 봉 수 N (미정, 임시 3)", GroupName = "2. 신호", Order = 14)]
		public int T1Window { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RegimeFast", Description = "레짐 SMA 단기 (10분봉)", GroupName = "3. 레짐", Order = 0)]
		public int RegimeFast { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RegimeMid", Description = "레짐 SMA 중기 (10분봉)", GroupName = "3. 레짐", Order = 1)]
		public int RegimeMid { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RegimeSlow", Description = "레짐 SMA 장기 (10분봉)", GroupName = "3. 레짐", Order = 2)]
		public int RegimeSlow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseRule12", Description = "레짐 1.2·2.2 사용 (정의 확정 전까지 끔)", GroupName = "3. 레짐", Order = 3)]
		public bool UseRule12 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "SwingBars", Description = "전고점·전저점 구간 (신호 봉 포함)", GroupName = "4. 손절·청산", Order = 0)]
		public int SwingBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "StopPercent", Description = "손절 % (종목별)", GroupName = "4. 손절·청산", Order = 1)]
		public double StopPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MomentumSmaPeriod", Description = "강한 모멘텀 청산 SMA 기간 (3분봉)", GroupName = "4. 손절·청산", Order = 2)]
		public int MomentumSmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EntryQuantity", Description = "기본 진입 수량 (마이크로)", GroupName = "5. 수량", Order = 0)]
		public int EntryQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MaxQuantity", Description = "최대 보유 수량 (v1 미사용)", GroupName = "5. 수량", Order = 1)]
		public int MaxQuantity { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "ContestStartTime", Description = "신규 진입 시작 (HHmmss, KST)", GroupName = "6. 대회", Order = 0)]
		public int ContestStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "EntryEndTime", Description = "신규 진입 마감, 신호 봉 기준 (HHmmss, KST)", GroupName = "6. 대회", Order = 1)]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 235959)]
		[Display(Name = "FlattenTime", Description = "종료 청산, 이 시각 봉 마감 (HHmmss, KST)", GroupName = "6. 대회", Order = 2)]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "UseTestEntry", Description = "주문 흐름 검증용 임시 진입", GroupName = "7. 테스트", Order = 0)]
		public bool UseTestEntry { get; set; }
		#endregion
	}
}
