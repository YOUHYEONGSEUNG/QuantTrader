# 팀 퀀트 — NinjaTrader 8 대회 전략

## 프로젝트
- NinjaTrader 8 NinjaScript(C#)로 대회용 자동매매 전략을 구현한다.
- 매매 규칙의 유일한 기준: @docs/spec.md
  - 명세에 없는 규칙을 만들거나 바꾸지 않는다. 모호하면 코드를 쓰기 전에 사람에게 질문한다.
  - 코드 주석에 명세 조항 번호를 단다. 예: `// spec 5.1 1차 익절`
- 지표 ↔ 전략 인터페이스: @docs/interface.md
  - 속성 이름·의미·타입을 바꾸려면 먼저 사람에게 확인한다. 두 사람이 이 계약에 맞춰 동시에 작업한다.
- 작업 분담: @docs/work-split.md

## 폴더와 담당
- 저장소 루트 = `Documents\NinjaTrader 8\bin\Custom`
- `Indicators/TeamQuant/` — 신호 엔진 (담당 A)
- `Strategies/TeamQuant/` — 전략·주문 (담당 B)
- 위 두 폴더와 `docs/`, `.claude/` 밖의 파일(NinjaTrader 기본 스크립트)은 절대 수정하지 않는다.
- 클래스·파일 이름은 `TQ_` 접두어를 쓴다.

## 빌드와 검증
- Claude Code는 NinjaScript를 컴파일할 수 없다. 사람이 NinjaScript Editor에서 F5로 컴파일하고 에러를 붙여넣으면 그걸 고친다.
- NinjaTrader는 Custom 폴더의 모든 .cs를 한 번에 컴파일한다. 한 파일이라도 에러가 있으면 전체가 컴파일되지 않으므로, 컴파일되는 코드만 커밋·푸시한다.
- 지표 검증: 차트에 띄워 TradingView 값과 비교한다.
- 전략 검증: Market Replay/Playback으로 주문 흐름을 확인하고, Strategy Analyzer로 백테스트한다.

## 코딩 규칙
- 모든 설정값은 `[NinjaScriptProperty]`로 노출하고 기본값은 spec.md 8장 값을 쓴다.
- 주석과 로그는 한국어로 쓴다.
- 신호 발생, 주문 제출, 상태 전환마다 `Print()` 로그를 남긴다. 형식: `[시간][종목][모듈] 내용`
- NinjaScript 세부 규칙은 `.claude/rules/` 아래 파일을 따른다.

## 공식 문서
- NinjaScript 레퍼런스: https://ninjatrader.com/support/helpguides/nt8/
- 문서 인덱스(LLM용): https://docs.ninjatrader.com/llms.txt
- API 시그니처나 동작이 확실하지 않으면 추측하지 말고 위 문서에서 해당 페이지를 찾아 확인한 뒤 작성한다.
