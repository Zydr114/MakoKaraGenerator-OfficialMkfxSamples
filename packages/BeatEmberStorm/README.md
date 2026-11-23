# BeatEmberStorm — 重低音炽焰余烬风暴

由音频特征与事件时间线协同驱动的燃系余烬粒子特效包（SDK v2）。

## 核心特性

- **全曲音频动态变速连续发射**：在 `Declare` 阶段调用 `source.Audio.GetRange(...)` 按时间片（总段数 ≤ 1024）将 `BassLevel` 与 `Pulse` 映射为平滑分段线性的 `RateTrack`（未分析音频时按 `fallbackBpm` 构造周期脉冲），驱动支持 `BridgePolicy` 跨行滑行的炽焰火星播放头发射器。
- **双事件音频加权爆发**：分别监听 `TokenStarted`（主爆发火星与火丝）与 `TokenCompleted`（收尾余烟与灰烬），按事件时刻 `Audio.GetAt` 能量动态设定爆发数量。
- **SVG 图集双层渲染**：内置 `ember-shard`（菱形火星）与 `smoke-puff`（柔焦热烟）两套 `mode: "atlas"` 静态 SVG 图集（`small` / `medium` / `large`，`paddingPx: 4`，`mipmaps: true`），在 `EvaluateVisuals` 中分别绘制 `BehindText` 热烟与 `AboveText` 火星，并结合 `frame.Particles.GetTrail` 绘制收尖流光火丝。
