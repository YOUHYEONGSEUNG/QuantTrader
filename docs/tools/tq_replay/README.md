# TQ_Strategy 재현기 (Python)

TQ_Strategy(v6-box) C# 로직을 그대로 옮긴 1분봉 재현기. 규칙을 바꿔 보기 전에 Playback과 같은 결과가 나오는지 맞춰 두고, 그 위에서 실험한다.

- `ind.py` 내장 지표(EMA·SMA·ATR·MACD·RSI, NinjaTrader 식)와 TQ_Signals·TQ_MacdTurn·TQ_ATRChannels, 3분봉 만들기
- `score.py` 연속 점수 C·속도·연속 S·F, 7레짐·전환의심, 의심 이벤트, 재가속 (TQ_Strategy.UpdateContScore / ObserveCont / Observe7Regime)
- `strat.py` 진입 경로·수량·손절·익절·청산 (OnBarUpdate 흐름). 체결: 봉 마감 시장가 → 다음 봉 시가, 손절·익절·E3 스탑·현재가 청산 → 봉 안 가격 경로(시가 → 가까운 극값 → 먼 극값 → 종가). `P(...)`로 설정, `hooks`로 실험(evaluate, qty_mult, skip, tp1_dist, hold, after, stop_dist)
- `base.py` 7월·10월 한꺼번에 돌려 표 한 줄로 (`run`, `row`)
- `parse_log.py` Playback 로그(`TQ_log_v6_종목.txt`) → `logruns.json` (실행별 매 봉 C·속도·S·F·7레짐·OHLC·후보, 사건 줄)
- `build_bars.py` 로그 봉 합쳐 `bars_dec.json` (연속 1분봉)
- `run_cmp.py` 재현기 거래와 Playback 학습 데이터(`TQ_ml_v6_종목.csv`) 거래를 하나씩 맞대기

**검증 (2026-10-22):** 10/8 21:20 ~ 10/16 이어서 돌린 Playback 한 번과 비교해 매 봉 C·속도·S·F·7레짐·전환의심 6,839봉 소수 6자리까지 같음, 거래 33건 진입 시각·경로·방향·수량·청산 사유 같음, 금액 −$6,901 vs −$6,762(체결가 차이: Playback은 봉 마감 1초 뒤 호가로 체결). 날마다 따로 돌린 Playback은 NinjaTrader가 불러온 과거 데이터가 달라 점수부터 어긋남 → 비교할 Playback은 여러 날을 한 번에 이어서.

**한계:** 1분봉이라 봉 안 순서·체결 시점은 근사. 틱 데이터(`db/replay` .nrd)는 NinjaTrader 전용 형식이라 못 읽음.
