# 대회 전략 명세 v3 (코딩용)

2026-10-15 · 브랜치 `v3-integrated` · 기준 문서: v3 통합 설계 지시서(`v3-build-order.md`, 10/15). v2(회의 문서 규칙 + 유기 모드)는 태그 없이 `main`(`d05a041`)과 `organic-defaults-1014` 브랜치에 그대로 있음.

**읽는 법:** 기본값만으로 돌리면 이 문서의 설계 그대로임. 고민 중인 사항은 모두 스위치(설정값)로 두고 기본값은 설계안 값. 설정값 전체는 8장, 고민 → 스위치 대응은 9장.

## 1. 개요

추세 봉(기본 3분봉)으로 추세를 **+1 상승 / 0 횡보 / −1 하락** 세 가지로 판단하고, 1분봉(진입 봉)에서 4가지 포지션 중 하나로 들어감. 의심은 진입이 아니라 **보유 중 일부청산 이벤트**이고, 시간대를 **장전(미국 개장 전) / 장후**로 나눔. 나스닥(MNQ)과 골드(MGC)에 같은 규칙을 쓰고 숫자는 종목 차트마다 설정값으로 둠.

**용어:** 진입 봉 = 전략을 올린 차트의 봉(1분봉), 추세 봉 = 추세를 판단하는 봉(3분 또는 10분). S = 느린 점수, F = 빠른 점수. dir = 방향 부호(매수 +1, 매도 −1).

## 2. 거울상 원칙 (4-0)

**모든 규칙은 매수로 한 번만 정의하고 매도는 거울상으로 계산함.** 트레이더 문서의 매수·매도 문구가 거울상이 아니면 매수 쪽을 기준으로 읽음. 코드도 방향 부호 `dir` 하나로 한 경로만 씀(매수·매도 함수를 따로 두지 않음). 밴드는 `Band(dir, n)`(n = 0 중심선), 비교는 `dir × (a − b) > 0` 꼴.

| 매수 | ↔ 매도 |
| --- | --- |
| 위 / 상방 / +N배 / 고가 | 아래 / 하방 / −N배 / 저가 |
| MACD 상승 전환, 골든크로스, %K 80 | MACD 하락 전환, 데드크로스, %K 20 |
| 최고가 − k·ATR (트레일링) | 최저가 + k·ATR |
| 종가 < 중기선 (청산) | 종가 > 중기선 |

## 3. 추세 판단 — `TQ_Regime` v3 (입력 = 추세 봉)

모든 이동평균·MACD·RSI는 추세 봉 기준. 진입 봉 지표는 `TQ_Signals`가 맡음.

### 3-1. 점수 항목 (각 +1 / 0 / −1)

| 층 | 항목 | +1 | −1 | 0 |
| --- | --- | --- | --- | --- |
| 느린 S | S1 가격 vs 중기선 | 종가 > SMA(Mid) | 종가 < SMA(Mid) | 같음 |
| | S2 구조 | `RegimeS2Mode` 1: SMA(Mid) > SMA(Slow) / 2: SMA(Slow)가 `RegimeSlopeBars`봉 전보다 높음 | 반대 | 같음 |
| | S3 MACD선 부호 | MACD선 > 0 | MACD선 < 0 | 0 |
| | S4 RSI (`RegimeUseRsi`) | RSI ≥ 50 + `RegimeRsiBand` | RSI ≤ 50 − 밴드 | 사이 |
| 빠른 F | F1 단기 가격 | 종가 > SMA(Fast) AND SMA(Fast) > 직전 봉 | 종가 < SMA(Fast) AND SMA(Fast) < 직전 봉 | 나머지 |
| | F2 MACD선 기울기 | MACD선이 `RegimeMacdSlopeBars`봉 연속 커짐 | 연속 작아짐 | 나머지 |

- S = S1+S2+S3(+S4): −4~+4 (S4를 끄면 −3~+3). F = F1+F2: −2~+2.
- 스토캐스틱·크로스·다이버전스는 추세 점수에 넣지 않음 (오실레이터는 타점용).

### 3-2. 상태 전이 (추세 봉 마감마다, 상승 기준이고 하락은 거울상)

| 지금 | 조건 | 다음 |
| --- | --- | --- |
| 상승추세 아님 | F ≥ `TrendStartFast`(1, 10/15에 2에서 내림) AND S ≥ `TrendStartSlow`(1, 같음) | 상승추세 (그 봉 = 추세 시작 봉) |
| 상승추세 | 하락추세 시작 조건 충족 | 바로 하락추세 |
| 상승추세 | S ≤ `TrendEndSlow`(0) | 횡보. `DirectReverse`(전략 `RegimeDirectReverse`, 기본 끔)를 켜면 그 봉에 F ≤ −2일 때 바로 하락추세 |
| 상승추세 | F ≤ −`SuspectFast`(2)가 처음 된 봉 | 상승추세 유지 + **하락의심 이벤트** 1회 |
| 상승추세 + 의심 중 | F ≥ 0 | 의심 해제 (이벤트 없음) |

- 의심은 해제된 뒤 다시 뜰 수 있음(해제 → 재발생마다 이벤트 1회). 추세가 바뀌면 의심은 지워짐.
- 확정 연속 봉 수는 두지 않음(10/14 데이터: 2봉 확정은 3분 늦어져 이긴 거래가 졌음). 흔들림은 시작(S ≥ 2)·종료(S ≤ 0) 기준 차이로 막음.
- 데이터 가드: 장기선, MACD(장기 + 시그널), RSI, 기울기 봉 수가 다 채워지기 전에는 `Regime = NaN`.
- 출력은 `docs/interface.md`.

## 4. 매매 규칙

### 4-1. 확정 원칙

1. 판단은 봉 마감 뒤. 예외: 밴드·ATR 지정가 익절과 손절 스탑은 봉 진행 중 체결.
2. 종목당 포지션 하나. 보유 중 새 진입 신호는 무시. 청산과 같은 봉에 반대 진입하지 않음.
3. **포지션은 4가지:** 추세 매수(TL), 추세 매도(TS), 횡보 매수(SL), 횡보 매도(SS). 역진입·역추세 진입은 없음.
4. **진입 뒤 추세가 바뀌면:**
   - 추세 포지션: 의심 이벤트 → 4-5 일부청산. 추세 종료(횡보) → 전량 청산(`TrendExitOnEnd`). 반대 추세 시작 → 항상 전량 청산.
   - 횡보 포지션: 반대 방향 추세 시작 → 전량 청산(`SideExitOnOppTrend`). 같은 방향 추세 시작 → 그대로 들고 감(횡보 규칙 유지).
   - **반대 추세 시작으로 전량 청산한 뒤에는 미보유가 된 다음 진입 봉부터 2봉 안에 그 추세의 E1을 허용함** (2026-10-15 사용자 결정. 그대로 두면 추세가 뒤집히는 봉에서 E1을 영영 놓침. 같은 봉 반대 진입 금지는 지킴).
5. 추세 판단은 직전에 마감된 추세 봉의 값을 씀 (추세 봉과 진입 봉이 같은 시각에 닫히면 1분 지연).

### 4-2. 진입

| 경로 | 포지션 | 조건 (매수 기준) | 스위치 |
| --- | --- | --- | --- |
| **E1 추세 시작** | TL | 추세 봉 `TrendStart = +1`을 처음 본 진입 봉 마감에 매수. 필터 ① 종가 < Band(+1, `TrendTp1Band`) ② (Band(+1, `TrendTp2Band`) − 종가) ≥ 예상 손절 거리 × `EntryMinRR` | `UseEntryTrendStart` |
| E1 추세 재가속 (`E1R`) | TL | 상승추세 중 F가 0 이하로 내려갔다가 다시 F ≥ 2 AND S ≥ 2가 된 추세 봉(추세 시작 기준과 따로 고정)을 처음 본 진입 봉 마감. 필터 ①② 같음 (2026-10-15 사용자 결정) | `E1Reaccel` |
| E1 구간 첫 봉 추세 (`E1S`) | TL | 대회 구간 첫 진입 가능 봉(기본 21:35)에 추세 = 상승. 필터 ①② 같음, 장전 규칙 적용 (2026-10-15 사용자 결정, 같은 날 기본 끔: 21일 10건 3승 −$2,792) | `E1AtSessionStart` |
| **E2 추세 눌림** | TL | 추세 = 상승 AND `EntryUpLong`(저가 ≤ Band(−, `TrendPullbackBand`) → T1 [MACD 상승 전환 OR 골든]). 필터: E1의 ②(손익비 `EntryMinRR`), S ≥ `PullbackMinSlow`(2) (둘 다 2026-10-15 추가). 기본 밴드는 중심선(`TrendPullbackBand` 0, 10/15에 1에서 바꿈) | `UseEntryPullback` |
| **E3 장전 박스 돌파** | TL | 장후(4-7) AND `OrbEndTime` 전 AND 진입 봉 종가 > 장전 박스 고가 AND S ≥ `OrbMinSlow` AND F ≥ `OrbMinFast`. 방향별 `OrbMaxEntries`(2)번까지: 한 번 쓰면 진입 봉 종가가 박스 안(고가 이하)으로 돌아온 뒤에야 다시 돌파를 받음 (2026-10-15 사용자 결정, 지시서 원래 규칙은 방향별 한 번 = 1) | `UseEntryOrb` |
| **E4 횡보** | SL | 추세 = 횡보 AND `EntrySideLong`(저가 ≤ Band(−, `SideBand`) → T1, 또는 `SideT2Entry`면 T2Bull) | `UseEntrySide` |

공통 필터 (막히면 상태 상자·로그에 사유를 수치와 함께 남김):
- 진입 시간(4-7)과 장전 제한.
- 의심 중에는 그 추세 방향 신규 진입 안 함 (`BlockEntryWhileSuspect`). `SuspectBlockNeedPrice`를 켜면 의심이 뜬 뒤 진입 봉 종가가 직전 `SwingBars`봉 저점 아래로 마감한 적이 있을 때만 막음(4-5의 가격 확인과 같은 식). 10/15에 기본 켬으로 넣었다가 같은 날 기본 끔으로 되돌림(21일: 반대 의심 중 E2 8건 3승 −$9,997).
- **추세 진입은 빠른 점수가 반대면 안 함 (`TrendEntryNeedFast`, 기본 켬, 2026-10-15 사용자 결정):** E1·E2·E3 매수는 F ≥ 0, 매도는 F ≤ 0일 때만. 추세 판정은 늦어서(큰 움직임 시간의 약 28%를 반대 추세로 판정) 반전 초입에는 판정은 옛 방향인데 F는 이미 반대임.
- 횡보 방향 필터: S+F ≥ `SideBlockScore`(3)이면 SS 금지, ≤ −3이면 SL 금지.
- 재진입 제한: 횡보 포지션이 본절 전 손실 손절로 끝나면 그 대회 구간에서 같은 방향 횡보 진입 금지(`BlockSideAfterStop`). 추세 포지션은 제한하지 않음.
- 같은 봉에 여러 경로가 충족되면 E1 > E3 > E2 > E4, 진입은 1회. 같은 경로에 두 방향이 같이 충족되면 매수 먼저(구현 선택).
- 밴드 조건 없이 1분 T2만으로 추세 진입하는 경로는 없음.
- `SideT2Entry`를 끄면: `TQ_Signals.EntrySideLong`이 밴드 경로와 T2를 하나로 내므로, 직전 봉까지 밴드 대기가 없었는데 T2가 같이 나온 봉은 T2만의 신호로 보고 뺌.

### 4-3. 손절 (전 포지션 공통 식, 범위만 다름)

- **구조 손절:** 신호 봉 포함 최근 `SwingBars`(5)봉 저가 − 1틱. 거리를 신호 봉 1분 ATR(14) × [최소, 최대]로 자름. 추세 [`TrendStopMinAtr` 1.5, `TrendStopMaxAtr` 3.0], 횡보 [`SideStopMinAtr` 1.0, `SideStopMaxAtr` 2.0].
- 거리가 `StopMinTicks`(32틱 = MNQ 8pt)보다 작으면 그만큼 벌림 (장전 ATR이 절반이라 너무 좁아지는 것 방지).
- 신호 봉 종가로 먼저 걸고 체결되면 체결가로 다시 계산. 갭으로 이미 넘었으면 즉시 전량 청산(`GAP`).
- **이익 보호 본절 (`BreakevenAtR` 0.8, 2026-10-15 사용자 결정, v2와 같은 방식):** 1차 익절 전이라도 진입 뒤 최대 유리폭이 처음 손절 거리 × 0.8에 닿으면 진입 봉 마감에 손절을 평단으로. 4가지 포지션 모두(v2는 주력 진입을 뺐음). 근거: 7/15~30 9일 35건 시뮬레이션에서 0.8R이면 지던 4건이 본전, 이기던 1건(+$985)이 잘림, 합계 +$1,192. 0.5R은 이기던 5건을 잘랐음.
- 손절은 유리한 쪽으로만 움직임. 옮길 때 `BreakevenBufferTicks`(8) 안전 거리(가까우면 미뤘다가 봉 마감마다 재시도).

### 4-4. 추세 포지션 청산 (TL / TS)

| 단계 | 조건 | 수량 (진입 계약 대비) | 그 뒤 손절 |
| --- | --- | --- | --- |
| 1차 | 가격이 Band(dir, `TrendTp1Band`=2)에 닿음 (지정가, 봉 진행 중) | `TrendTp1Percent` 33% | 평단 (이익이 처음 손절 거리 × `BreakevenMinR` 0.5 미만이면 1차 체결가 ∓ `BreakevenFallbackAtr` 1.5 × ATR) |
| 2차 | Band(dir, `TrendTp2Band`=3)에 닿음 | `TrendTp2Percent` 33% | **트레일링 시작:** 진입 봉 마감마다 진입 뒤 최고가 − `TrailAtr`(2.5) × 1분 ATR |
| 마지막 조각 | **2차 뒤에만:** 추세 봉 종가가 기준선(`TrendLastExitMa` 1 중기선 / 2 단기선 / 0 끔) 반대편으로 마감 (`LAST_MA`) — 또는 트레일링 손절 | 나머지 | |
| 의심 | 4-5 | | |
| 전량 | 추세 종료(`TREND_END`) / 반대 추세 시작(`OPP_TREND`) / 종료 청산 시각(`FLATTEN_TIME`) | 남은 전부 | |
| 진입 봉 T2 반대 신호 | MACD 반대 전환 AND 반대 크로스 | 추세가 포지션 방향인 동안은 무시. 추세 판정이 포지션 방향이 아닐 때만(E3로 횡보 중 들어간 경우 등) 또는 `TrendExitOnEntryT2`면 전량 (`ENTRY_T2`) | |

- 익절 조건은 가격만 봄(MACD·%K 조건 없음). 한 봉에 두 밴드를 같이 넘으면 두 단계 같이 체결.
- **진입 때 이미 지난 밴드 (2026-10-15 사용자 결정):** 신호 봉 종가가 1차 밴드를 넘었거나 1차 밴드까지 남은 거리가 예상 손절 거리 × `TrendTpMinR`(0.5)보다 짧으면 1차 조각도 2차 밴드에서 익절, 2차 밴드도 같은 기준에 걸리면 1·2차 조각을 마지막 조각처럼 둠(진입 때부터 2차 뒤로 보고 트레일링·기준선 청산). 거리 기준은 E1에도 적용(7/27 22:39 E3: 1차까지 5pt, 손절 69pt). 그 전에는 지정가가 진입가 안쪽에 걸려 진입하자마자 체결됐음.
- **1차 뒤 손절 (2026-10-15 사용자 결정):** 1차 익절 이익(체결가 − 평단)이 처음 손절 거리 × `BreakevenMinR` 이상이면 평단으로. 모자라면 1차 체결가 ∓ `BreakevenFallbackAtr` × 1분 ATR(14)로(지금보다 유리할 때만). 횡보 1단계에도 같음. 안전 거리 규칙은 따름. 그 전에는 항상 평단이었고, 7/27 22:39는 1차 이익 5pt에 평단으로 옮겨 5초 뒤 본절로 다 나갔음.
- 추세 종료 청산은 보유 중 추세가 포지션 방향이었던 적이 있을 때만 함 (E3로 횡보 중 들어가 처음부터 횡보인 경우 바로 청산되지 않게. 구현 선택).

### 4-5. 의심 이벤트 처리 (추세 포지션 보유 중에만)

1. 추세 봉 `SuspectEvent`가 포지션 반대 방향으로 뜨면 확인 대기 시작 (`SuspectConfirmBars` 5 진입 봉, 이벤트를 본 봉 포함).
2. 대기 안에서 가격 확인: 진입 봉 종가가 직전 `SwingBars`봉(그 봉 제외) 저가 아래로 마감 (`SuspectNeedPrice`. 끄면 이벤트 즉시 실행).
3. 확인되면 남은 수량의 `SuspectExitPercent`(50%)를 시장가로 청산(`SUSPECT`). 그때 S ≤ `SuspectStrongSlow`(1)이면 `SuspectStrongExitPercent`(100%, `SUSPECT_STRONG`). 손절을 평단 이상으로(이익 중일 때만, 안전 거리 지킴).
   - **청산 수량은 먼 조각부터(마지막 → 2차 → 1차) 조각 단위로** 뺌 (2026-10-15 사용자 결정). 한 조각의 일부만 청산하면 그 조각에 걸린 익절·손절 주문 수량이 어떻게 되는지 NT8 문서에서 확인되지 않아 조각을 쪼개지 않음. 원하는 수량에 가장 가까워지는 곳까지 뺌(적어도 한 조각). 예: 30계약 9/9/12에서 50% → 마지막 12계약.
4. 대기 안에 가격 확인이 없거나 의심이 해제되면 아무것도 안 함.
5. 의심 하나당 한 번만 실행. 역진입 없음. 미보유면 4-2의 진입 차단만 적용.

### 4-6. 횡보 포지션 청산 (SL / SS)

| 단계 | 조건 | 수량 | 그 뒤 손절 |
| --- | --- | --- | --- |
| 1단계 | 가격이 진입가 + dir × `SideTp1Atr`(2) × ATR에 닿음 (지정가, 봉 진행 중). 채널 밴드가 아님. ATR = 신호 봉 1분 ATR(14), 진입 때 고정(체결되면 체결가 기준으로 다시 검) | `SideTp1Percent` 33% | 평단 |
| 전량 | 진입 봉 종가가 Band(dir, `SideRestBand`=3) 도달(`SIDE_REST_BAND`) OR MACD 반대 전환(`SIDE_MACD`) OR 반대 크로스(`SIDE_CROSS`) | 나머지 | |
| 반대 추세 | 4-1 원칙 4 (`OPP_TREND`) | 전부 | |

- `SideOscExitAfterTp1`(켬): MACD·크로스 조건은 1단계 체결 뒤에만 봄. 그 전에는 손절·±3배·반대 추세로만 나감.
- MACD 전환은 `MacdUp/MacdDown`(처음 확인된 봉 하나에서만 참)을 씀.

### 4-7. 시간대 — 장전 / 장후 (KST, 서머타임 기준, 신호 봉 마감 시각)

| 구간 | 기본 시각 | 규칙 | 설정 |
| --- | --- | --- | --- |
| 지표 발표 | 21:30~21:34 봉 | 신규 진입 금지 (08:30 ET 지표 발표) | `NewsBlockMinutes` 5 |
| 장전 | 21:35~22:24 봉 | 수량 × `PreOpenQtyPercent`(50%). E1·E2는 S ≥ `PreOpenTrendMinSlow`(2, 10/15에 3에서 내림)일 때만. E4는 `PreOpenAllowSide`면 허용. **장전 박스(21:31~22:30 봉의 최고가·최저가) 기록** | 09 묶음 |
| 개장 전후 | 22:25~22:32 봉 | 신규 진입 금지 | `OpenTime` 223000, `OpenBlockBefore` 5, `OpenBlockAfter` 3 |
| 개장 보호 | 22:29 봉 마감 | 보유 중이고 이익이 1분 ATR × `ProtectAtr`(1) 이상이면 손절을 평단으로 | `ProtectBeforeOpen` 끔 |
| 장후 | 22:33~23:15 봉 | 수량 100%. E3는 `OrbEndTime` 230000 전까지 | |
| 최소 진입 보장 | 23:00 이후 | 종목 진입 횟수 < `MinEntries`(2)면 장전 제한·횡보 방향 필터 없이 다음 유효 신호를 수량 × `MinEntryQtyPercent`(30%)로 받음 | `MinEntryFallback`, `MinEntryFallbackTime` 230000 |
| 진입 마감 / 종료 청산 | 23:15 / 23:27 봉 마감 | | `EntryEndTime`, `FlattenTime` |

- 골드(MGC)는 `OpenTime`·장전 규칙을 종목 차트마다 바꿀 수 있음.
- 대회 날 경제지표 일정을 따로 확인해 `NewsBlockMinutes`를 정함.

### 4-8. 수량

- `EntryQuantity` 30, `MaxQuantity` 40(경고만).
- 단계 수량 = 진입 계약 × %를 내림, 마지막 조각이 나머지. 30계약 33/33 → 9 / 9 / 12. 장전 50%면 15계약 → 4 / 4 / 7.
- 의심 일부청산은 그때 남은 수량 기준(4-5).

## 5. 대회 운영 규칙

- **시간:** 예선 10/27(화), 본선 10/30(금) 모두 한국시간 21:30\~23:30.
- **최소 진입:** 총 5회, 나스닥 2회, 골드 2회. 종목별 진입 횟수를 화면 우상단에 표시함. 분할 진입 여러 건은 1회로 셈.
- **데이터:** 추세 봉 장기선 60봉(3분봉이면 3시간)과 지표 계산에 대회 시작 전 데이터가 넉넉히 필요하므로 전체 세션(ETH) 기준으로 3일 이상 불러옴.

## 6. 화면과 기록

- **상태 상자:** main(`d05a041`)의 그리기 방식(구역별 색 제목줄·배경, ●○× 기호, `StatusPanelScale`). 구역: 현재 상태 / 진입 조건(미보유) / 포지션 / 익절 / 손절 / 청산. 막힌 진입은 사유를 수치와 함께 적음.
- **차트:** 배경색 = 추세(상승 초록, 하락 빨강, 의심 중이면 주황·하늘색). 진입 화살표(매수 초록 ↑, 매도 빨강 ↓), 청산 화살표(파랑), 체결 라벨, 손절선·평단선, 장전 박스 고가·저가 점선(22:30 이후).
- **로그** `내 문서\NinjaTrader 8\TQ_log_v3_종목.txt` (실시간·Playback): 실행 시작 설정 줄(`버전=v3`, settings_id), 추세 봉 마감마다(S·F·항목별·원값), 진입 봉 마감마다(추세, S·F, 의심, 구간, 경로별 후보 상태, 보유 상태, `시고저종=`), 사건마다(진입, 익절·청산 사유 코드, 손절 이동, 주문 오류).
- **학습 데이터** `TQ_ml_v3_종목.csv` (백테스트 포함, 거래 1건마다): main의 열 + `path, session, s1..s4, f1, f2, slow, fast, pre_box_high, pre_box_low, atr_entry, rr_expected, fallback_entry, suspect_events, suspect_action, tp_stages_hit, trail_used, exit_reason`. v2 기록과 섞이지 않게 파일 이름을 따로 둠(2026-10-15 사용자 결정).
- **사유 코드:** `TP1`, `TP2`, `LAST_MA`, `TRAIL`, `STOP`, `BE_STOP`, `SUSPECT`, `SUSPECT_STRONG`, `TREND_END`, `OPP_TREND`, `SIDE_REST_BAND`, `SIDE_MACD`, `SIDE_CROSS`, `FLATTEN_TIME`, `GAP`, 그리고 지시서 목록에 없던 `ENTRY_T2`(4-4의 진입 봉 T2 반대 신호 청산).

## 7. 설정창 원칙

- 이름: `[태그] 한국어 설명 (CodeName)`. 태그: **[설계]** 설계안의 동작과 값 · **[선택]** 고민사항 스위치 · **[안전]** 주문 거부 대비 · **[표시]** 화면·기록.
- 묶음은 판단 순서, 한 묶음 안에서 [설계] 위 [선택] 아래. 쓰이지 않는 설정은 회색 잠금.
- 00 묶음: "설계안 기본값으로 되돌리기" 버튼(10 수량·대회 시간, 12 화면·기록 묶음은 건드리지 않음), "마지막 동작" 표시.
- 설정값은 108개 (10/15 진입 늘리기 4개, 1차 뒤 손절·밴드 거리 3개, 이익 보호 본절 1개, E2 최소 S 1개, F 반대 진입 금지·바로 반전 2개 추가. 원래 97개, 지시서 5-1은 50개 안쪽이 원칙이나, 5-2 표 그대로 두기로 2026-10-15 사용자 결정).

## 8. 설정값

| 묶음 | 설정 (CodeName) | 기본 | 태그 |
| --- | --- | --- | --- |
| **00 설정 세트** | 설계안 기본값으로 되돌리기 (`PresetApplyDefaults`), 마지막 동작 (`PresetStatus`) | – | 표시 |
| **01 추세 판단 (추세 봉)** | 추세 봉 분 (`RegimeBarMinutes`) 3 / 10 | 3 | 설계 |
| | 이동평균 단기 / 중기 / 장기 (`RegimeFast` / `RegimeMid` / `RegimeSlow`) | 6 / 20 / 60 | 설계 |
| | 추세 시작: 빠른 점수 / 느린 점수 (`TrendStartFast` / `TrendStartSlow`) | 1 / 1 (10/15에 2 / 2에서) | 설계 |
| | 추세 종료: 느린 점수가 이 값 이하 (`TrendEndSlow`) | 0 | 설계 |
| | MACD선 기울기 연속 봉 수 (`RegimeMacdSlopeBars`) | 2 | 설계 |
| | S2 방식 / 기울기 봉 수 (`RegimeS2Mode` / `RegimeSlopeBars`) | 1 / 5 | 선택 |
| | RSI 항목 사용 / 폭 (`RegimeUseRsi` / `RegimeRsiBand`) | 켬 / 5 | 선택 |
| **02 의심 이벤트** | 의심: 빠른 점수가 반대로 이 값 (`SuspectFast`) | 2 | 설계 |
| | 가격 확인 필요 / 대기 봉 수 (`SuspectNeedPrice` / `SuspectConfirmBars`) | 켬 / 5 | 설계 |
| | 일부청산 % (`SuspectExitPercent`) | 50 | 선택 |
| | 강한 의심 기준 S / 그때 청산 % (`SuspectStrongSlow` / `SuspectStrongExitPercent`) | 1 / 100 | 선택 |
| | 의심 중 추세 방향 신규 진입 금지 (`BlockEntryWhileSuspect`) | 켬 | 선택 |
| | 그 금지는 가격 확인 뒤에만 (`SuspectBlockNeedPrice`) | 끔 | 선택 |
| | 추세 진입은 빠른 점수가 반대면 안 함 (`TrendEntryNeedFast`) | 켬 | 선택 |
| | 추세 끝날 때 바로 반전 (`RegimeDirectReverse`, 01 묶음) | 끔 | 선택 |
| **03 진입 — 추세** | E1 추세 시작 봉 진입 (`UseEntryTrendStart`) | 켬 | 설계 |
| | E2 눌림 진입 / 밴드 (`UseEntryPullback` / `TrendPullbackBand`) | 켬 / 0 (중심선, 10/15에 1에서) | 설계 |
| | E2 최소 느린 점수 (`PullbackMinSlow`), 0 = 안 봄 | 2 | 선택 |
| | E3 장전 박스 돌파 / 마감 시각 / 최소 S / 최소 F (`UseEntryOrb` / `OrbEndTime` / `OrbMinSlow` / `OrbMinFast`) | 켬 / 230000 / 1 / 1 | 설계 |
| | E3 방향별 최대 횟수 (`OrbMaxEntries`), 1 = 지시서 원래 규칙 | 2 | 선택 |
| | E1·E2 최소 손익비 (`EntryMinRR`), 0 = 안 봄 | 1.0 | 선택 |
| | E1 추세 재가속 (`E1Reaccel`) / 구간 첫 봉에 이미 있는 추세 (`E1AtSessionStart`) | 켬 / 끔 | 선택 |
| **04 진입 — 횡보** | E4 횡보 진입 / 밴드 (`UseEntrySide` / `SideBand`) | 켬 / 2 | 설계 |
| | 밴드 없이 T2로도 진입 (`SideT2Entry`) | 켬 | 설계 |
| | 횡보 방향 필터 점수 (`SideBlockScore`) | 3 | 설계 |
| | 손실 손절 뒤 같은 방향 횡보 재진입 금지 (`BlockSideAfterStop`) | 켬 | 선택 |
| **05 진입 — 공통** | 반전 신호 대기 봉 수 (`T1Window`), MACD 전환 확인 봉 수 (`MacdConfirmBars`) | 5 / 2 | 설계 |
| | T2에서 MACD 꺾인 봉의 크로스 인정 (`T2CountConfirmBars`) | 켬 | 설계 |
| **06 손절** | 구조 손절 봉 수 (`SwingBars`) | 5 | 설계 |
| | 추세 손절 ATR 최소 / 최대 (`TrendStopMinAtr` / `TrendStopMaxAtr`) | 1.5 / 3.0 | 설계 |
| | 횡보 손절 ATR 최소 / 최대 (`SideStopMinAtr` / `SideStopMaxAtr`) | 1.0 / 2.0 | 설계 |
| | 최소 손절 틱 (`StopMinTicks`) | 32 | 안전 |
| | 1차 뒤 본절에 필요한 이익 R (`BreakevenMinR`) / 모자랄 때 1차 체결가 ∓ ATR 배수 (`BreakevenFallbackAtr`) | 0.5 / 1.5 | 선택 |
| | 이익 보호 본절: 최대 이익이 손절 거리 × (`BreakevenAtR`), 0 = 끔 | 0.8 | 선택 |
| | 손절 이동 안전 거리 틱 (`BreakevenBufferTicks`), 주문 오류 직접 처리 (`HandleOrderErrors`) | 8 / 켬 | 안전 |
| **07 청산 — 추세** | 1차 / 2차 익절 밴드 (`TrendTp1Band` / `TrendTp2Band`) | 2 / 3 | 설계 |
| | 1차 / 2차 익절 % (`TrendTp1Percent` / `TrendTp2Percent`) | 33 / 33 | 설계 |
| | 2차 뒤 트레일링 ATR 배수 (`TrailAtr`), 0 = 안 씀 | 2.5 | 설계 |
| | 마지막 조각 기준선: 0 끔 / 1 중기 / 2 단기 (`TrendLastExitMa`) | 1 | 설계 |
| | 추세 종료 시 전량 청산 (`TrendExitOnEnd`) | 켬 | 선택 |
| | 추세 유지 중에도 1분 T2 반대 신호로 청산 (`TrendExitOnEntryT2`) | 끔 | 선택 |
| | 익절 밴드 최소 거리 R (`TrendTpMinR`), 0 = 이미 넘은 경우만 | 0.5 | 선택 |
| **08 청산 — 횡보** | 1단계 ATR 배수 / % (`SideTp1Atr` / `SideTp1Percent`) | 2 / 33 | 설계 |
| | 전량 청산 밴드 (`SideRestBand`) | 3 | 설계 |
| | MACD·크로스 청산은 1단계 뒤에만 (`SideOscExitAfterTp1`) | 켬 | 선택 |
| | 반대 추세 시작 시 전량 청산 (`SideExitOnOppTrend`) | 켬 | 선택 |
| **09 시간대** | 개장 시각 (`OpenTime`) | 223000 | 설계 |
| | 지표 발표 진입 금지 분 (`NewsBlockMinutes`) | 5 | 설계 |
| | 개장 전 / 후 진입 금지 분 (`OpenBlockBefore` / `OpenBlockAfter`) | 5 / 3 | 설계 |
| | 장전 수량 % (`PreOpenQtyPercent`) | 50 | 선택 |
| | 장전 추세 진입 최소 S (`PreOpenTrendMinSlow`), 장전 횡보 허용 (`PreOpenAllowSide`) | 2 / 켬 | 선택 |
| | 개장 전 본절 보호 / ATR 배수 (`ProtectBeforeOpen` / `ProtectAtr`) | 끔 / 1 | 선택 |
| | 최소 진입 보장 / 시각 / 수량 % (`MinEntryFallback` / `MinEntryFallbackTime` / `MinEntryQtyPercent`) | 켬 / 230000 / 30 | 선택 |
| **10 수량·대회 시간** | `EntryQuantity` / `MaxQuantity` / `MinEntries` | 30 / 40 / 2 | 설계 |
| | `ContestStartTime` / `EntryEndTime` / `FlattenTime` | 213000 / 231500 / 232700 | 설계 |
| **11 지표 계산값** | ATR 채널 (`BandMidPeriod`, `BandAtrPeriod`, `BandMult1~3`), MACD (`MacdFast` / `MacdSlow` / `MacdSmooth`), 스토캐스틱 (`StochPeriodK` / `StochSmooth` / `StochPeriodD`), 크로스 필터 (`CrossLow` / `CrossHigh` / `CrossFilterBars`), RSI 기간 (`RsiPeriod`) | 26·14·1/2/3, 12/26/9, 10/5/5, 20/80/5, 14 | 설계 |
| **12 화면·기록** | `ShowStatus`, `StatusPanelScale`, `LogToFile`, `MlLogToFile`, `UseTestEntry` | 켬 / 130 / 켬 / 켬 / 끔 | 표시 |
| | 익절을 봉 진행 중에 바로 (`IntrabarTakeProfit`) | 켬 | 설계 |

**회색 잠금:** S2 방식 1 → 기울기 봉 수, RSI 항목 끔 → RSI 폭, 가격 확인 끔 → 대기 봉 수, E1 끔 → 손익비, E2 끔 → 눌림 밴드, E3 끔 → 마감 시각·최소 S·F, E4 끔 → 횡보 밴드·T2·방향 필터·재진입 금지·장전 횡보 허용, 개장 보호 끔 → ATR 배수, 최소 진입 보장 끔 → 시각·수량.

## 9. 고민사항 → 스위치, 확인할 것

| 고민 | 스위치 | 비교할 값 |
| --- | --- | --- |
| 장기선을 배열로 볼지 기울기로 볼지 | `RegimeS2Mode` | 1 / 2 |
| RSI 수준이 지연을 만드는지 | `RegimeUseRsi` | 켬 / 끔 |
| 추세 봉 3분 vs 10분 | `RegimeBarMinutes` | 3 / 10 (10이면 이평 6/18/36 권장) |
| 의심 때 얼마나 줄일지 | `SuspectExitPercent`, `SuspectStrongSlow` | 33 / 50 / 100 |
| 의심에 가격 확인을 둘지 | `SuspectNeedPrice` | 켬 / 끔 |
| 늦은 추세 진입 거르기 | `EntryMinRR` | 0 / 1 / 1.5 |
| 트레일링 폭 | `TrailAtr` | 2 / 2.5 / 3 |
| 마지막 조각: 이평 vs 트레일링만 | `TrendLastExitMa` | 0 / 1 / 2 |
| 장전 수량·제한 | `PreOpenQtyPercent`, `PreOpenTrendMinSlow` | 50/3 vs 100/2 |
| 횡보 1단계 전 오실레이터 청산 | `SideOscExitAfterTp1` | 켬 / 끔 |
| 개장 직전 보호 | `ProtectBeforeOpen` | 끔 / 켬 |

**사람에게 물어볼 것 (기본값으로 진행, 답이 오면 설정값만 바꿈)**

- [ ] 의심 일부청산 비율 — 기본 50%, S ≤ 1이면 전량.
- [ ] 추세 익절 비율을 매수·매도 같게(33/33/나머지) — v1은 매수 1/5·2/5, 매도 1/3·1/3이었음.
- [ ] 장전 수량 50%와 장전 추세 진입 S ≥ 3.
- [ ] 장전 박스 돌파(E3)를 쓸지.
- [ ] 10/27·10/30 21:30 경제지표 일정 (`NewsBlockMinutes`).

**확인 전 (지시서 8장 단계별 완료 기준)**

- [ ] 1단계 채점기 비교표 (`docs/tools/README.md`): mode=3 코드는 넣었고, Python을 설치한 뒤 9월 데이터로 고르고 10월 데이터로 확인.
- [ ] 2단계 F5, 차트 배경색·로그 S·F 확인, `--validate`로 로그와 채점기 재현값 일치.
- [ ] 3~6단계 F5, 설정창 묶음·태그·잠금, `UseTestEntry` 주문 흐름, Playback에서 경로별 진입·막힌 사유, 지시서 8장 시나리오 14개.
- [ ] 8단계 Strategy Analyzer 9~10월 MNQ·MGC → `docs/status-v3.md`.
