---
paths:
  - "Indicators/TeamQuant/**"
---

# 신호 엔진(지표) 규칙 — 담당 A

- 지표에는 주문 코드를 넣지 않는다. 계산만 한다.
- 노출 이름·타입·의미는 docs/interface.md와 정확히 일치시킨다.
- 숫자 값은 AddPlot으로 노출한다(차트에서 바로 확인 가능).
- bool처럼 플롯이 아닌 값은 이렇게 노출한다:

```csharp
private Series<bool> golden;                 // State.DataLoaded에서: golden = new Series<bool>(this);

[Browsable(false)]
[XmlIgnore]
public Series<bool> Golden { get { Update(); return golden; } }   // Update()로 호출 시점 값을 최신화
```

- 지표 안에서 AddDataSeries를 쓰지 않는다. 10분봉 지표(TQ_Regime)는 입력 시리즈로 받고, 전략이 `TQ_Regime(BarsArray[1])`로 호출한다. 지표가 AddDataSeries를 쓰면 호스팅하는 전략도 같은 시리즈를 추가해야 하는 제약이 생긴다.
- 검증용 시각화(배경색, 화살표)는 설정값 `ShowDebugVisuals`로 켜고 끈다.
- spec 2장 정의를 그대로 구현한다. 특히 T1(밴드 조건 봉의 다음 봉부터 N봉), T2(같은 봉 또는 바로 다음 봉), T3(갭, 여러 밴드 동시 돌파)를 지킨다.
