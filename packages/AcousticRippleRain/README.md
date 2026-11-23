# AcousticRippleRain — 声波雨滴与文字水纹涟漪

精准踩点雨滴击字、声波椭圆涟漪、抛物线水花飞溅与单字 `GridWarp` 水面微震效果包（SDK v2）。

## 核心特性

- **精准踩点斜落雨丝**：在 `Declare` 收集 `TokenStarted`（以及存在时的 `LeadInStepChanged`）事件，在 `EvaluateVisuals` 中于 `[evTime - fallUs, evTime)` 绘制斜落细雨丝（`DrawSegments`），在起唱瞬间精确击中目标字符顶缘。
- **音频调制透视椭圆涟漪**：击中后 `[evTime, evTime + rippleUs)` 围绕落点绘制 1～3 圈 24 段光滑透视压扁椭圆水波纹（`DrawSegments`），最大半径与线宽受击中时刻音频能量（含 `fallbackBpm` 降级）调制。
- **抛物线飞溅水花微珠**：通过 `EventBurstPlan`（`BurstAnchor.SourceToken`）在击中瞬间沿字符顶缘向斜上方溅出受重力回落的水花微珠（`DrawDots`）。
- **单字 `GridWarp` 水面微震**：在 `Evaluate` 中对刚被雨滴击中的独立字符（检查 `CanTransformIndependently`）构建带充分外扩定义域的 `4×2` 正向非退化 `GridWarp`，呈现快速阻尼衰减的水波纹微震。
