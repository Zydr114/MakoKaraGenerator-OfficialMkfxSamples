# ChromaFireflyBokeh — 环境取色萤火与焦外散景

结合 `media.features` 时域平滑背景采样与双景深图层的抒情光斑粒子特效包（SDK v2）。

## 核心特性

- **三分区背景媒体取色与自适应**：声明 400ms 平滑窗口的 `canvasPixels` 查询 `ambient`，在 `EvaluateVisuals` 中对活跃歌词行所在画布左/中/右三分区调用 `frame.Media.Sample`，将线性 `Mean` 转换到 sRGB 提纯并与 `accentTint` 融合，依据亮度 `MeanPremulY` 调节光斑显色与透亮度，依据纹理方差 `PremulYVariance` 自适应抑制杂乱背景下的大散景尺寸。
- **双图层景深渲染**：
  - `BehindText`：监听 `BindingEntered` 与 `TokenStarted` 触发柔和上升散景光斑，使用 `bokeh-orb` 静态 SVG 图集（`small` / `medium` / `large`，`paddingPx: 4`，`mipmaps: true`）经 `DrawAtlasSprites` 绘制。
  - `AboveText`：连续播放头发射器（支持 `BridgePolicy` 跨行滑行）结合正弦布朗微飘轨迹，以 `DrawTrails` 绘制柔光微尾，并以 `DrawDots` 绘制双层萤火光核与光晕。
- **透明背景优雅降级**：在 `NoMedia` / `TransparentOutput` 下自动退化为 `accentTint` / `secondaryTint` 双色萤火散景。
