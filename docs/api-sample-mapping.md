# SDK v2 API 与示例包对照索引

本文档将 [SDK v2 完整 API 参考](script-sdk-v2.md) 中的核心能力、清单字段、C# 入口与纯资源图/着色器写法，逐一映射到 [`packages/`](../packages/) 下的 10 个官方示例包，方便按 API 查用法或按示例查规范。

---

## 1. 十个示例包总览

| 目录 | `packageId` | 导出种类与目标 | 声明能力（`requires`） | 核心示范点 |
| --- | --- | --- | --- | --- |
| [`BeatPulseNeon`](../packages/BeatPulseNeon) | `makokara.sample.beat-pulse-neon` | `controller` → `tokenFamily` + `canvas` | `token.affine`、`style.glow`、`visuals.segments` | `AudioFeatures`（含备用 BPM 退化）、`OverlayLayer.BehindText` 线段频谱条、`integer` 参数 |
| [`FrostedGlassMaterial`](../packages/FrostedGlassMaterial) | `makokara.sample.frosted-glass` | `material` → `region` (`fill`, `stroke`) | `[]` | 大半径 Blur（`ceil(3 * parameter:blurPx)`）+ 晶粒散射 SkSL、缎面高光、渐变 `fallback` |
| [`HoloPrismGlow`](../packages/HoloPrismGlow) | `makokara.sample.holo-prism-glow` | `graph` → `line` | `image.graph` | 整行 `TargetColor` + `SungCoverage` 遮罩、`Mask` → `Blur` → `RuntimeShader` 三节点 DAG、RGB 色散与虹彩 SkSL |
| [`LiquidGlassMaterial`](../packages/LiquidGlassMaterial) | `makokara.sample.liquid-glass` | `material` → `region` (`fill`, `stroke`) | `[]` | `masked` 区域覆盖材质、`parameter:backdropSource` 条件输入（`BackgroundMedia` / `BackdropBelow`）、Blur + SkSL 折射、渐变 `fallback` |
| [`PetalFall`](../packages/PetalFall) | `makokara.sample.petal-fall` | `controller` → `canvas` (`Overlay`) | `particles.burst`、`visuals.paths`、`resources.svg.vector` | `TimelineSelector` + `EventBurstPlan`、`FinalGeometry.Lines` 行包围框映射、SVG 3D 翻转仿射、图层切换 |
| [`RubySplitEmphasis`](../packages/RubySplitEmphasis) | `makokara.sample.ruby-split-emphasis` | `controller` → `baseToken` + `cluster` | `token.affine`、`cluster.affine`、`style.glow` | `BaseTokenTarget` 与 `TextClusterTarget` 分离缩放、`SourceRubySegment` 分段时间表与未分段宽度切片兜底 |
| [`StarTrail`](../packages/StarTrail) | `makokara.sample.star-trail` | `controller` → `canvas` (`Overlay`) | `particles.continuous`、`visuals.trails`、`visuals.paths`、`resources.svg.vector` | 双连续发射器（`cursor`）、`BridgePolicy`、粒子 `GetTrail`、SVG 矢量、`DrawTrails` + `DrawPaths` |
| [`UtopiaEmphasis`](../packages/UtopiaEmphasis) | `makokara.sample.utopia-emphasis` | `controller` → `tokenFamily` | `token.affine`、`style.glow` | `number` / `duration` / `enum` / `curve` / `boolean` / `color` 参数、瞬间突放回落、`SetGlow` |
| [`WordScatterExit`](../packages/WordScatterExit) | `makokara.sample.word-scatter-exit` | `controller` + `graph` → `baseToken` + `cluster` + `tokenFamily` | `token.affine`、`cluster.affine`、`token.opacity`、`style.glow`、`image.graph`、`line.exit` | 逐字唱完旋转散落退出、注音字簇独立飘散、`line.exit` 退出副本、`BindGraph` 逐字高斯模糊 |
| [`WordSwellEmphasis`](../packages/WordSwellEmphasis) | `makokara.sample.word-swell-emphasis` | `controller` → `tokenFamily` | `token.affine` | 演唱进度渐变膨胀、`IsSung` 唱完后平滑回落、组合平移与缩放矩阵、`CanTransformIndependently` |

---

## 2. 包清单与能力矩阵

参考：[script-sdk-v2.md §3 包清单与能力声明](script-sdk-v2.md#3-包清单与能力声明)

### 2.1 清单根合同

除 `WordScatterExit` 使用 `"lineExit": {"durationUs": 1500000}`（并声明 `"line.exit"`）外，其余 9 个包均为零行退场；所有包均遵循 SDK v2 包格式 3 固定头部：

- `"packageVersion": 3`
- `"sdkVersion": "2.0.0"`
- `"shaderProfile": "skia-ganesh-3.119.4-v2"`
- `"lineExit": {"durationUs": 0}`（或非零退场微秒数，上限 `3000000`）
- `"files"` 中每项必须包含 `path`、`role` 与 64 位小写 `sha256`，可用 [`tools/pack.py`](../tools/pack.py) 校验与刷新。

### 2.2 文件角色（`files[].role`）对照

| `role` | 含义 | 本仓库实际示例 |
| --- | --- | --- |
| `csharp` | C# 14 控制器源码 | `BeatPulseNeon`、`PetalFall`、`RubySplitEmphasis`、`StarTrail`、`UtopiaEmphasis`、`WordScatterExit`、`WordSwellEmphasis` 各自的 `src/*.cs` |
| `asset` | 随包分发的 `README.md` 等一般文件 | 全部 10 个包的 `README.md` |
| `svg` | 静态 SVG 源文件 | `StarTrail/assets/*.svg`、`PetalFall/assets/*.svg` |
| `svgResource` | SVG 使用描述 JSON（`mako-svg-static-1`） | `StarTrail/resources/*.json`、`PetalFall/resources/*.json` |
| `graph` | 图像图定义 JSON（`graphVersion: 1`） | `FrostedGlassMaterial`、`HoloPrismGlow`、`LiquidGlassMaterial`、`WordScatterExit` 各自的 `graphs/*.graph.json` |
| `shaderProgram` | SkSL 程序描述 JSON（`programVersion: 1`、`profile: "skia-raster-3.119.4-v1"`） | `FrostedGlassMaterial`、`HoloPrismGlow`、`LiquidGlassMaterial` 各自的 `shaders/*.program.json` |
| `shader` | SkSL 着色器源码 | `FrostedGlassMaterial`、`HoloPrismGlow`、`LiquidGlassMaterial` 各自的 `shaders/*.sksl` |

### 2.3 参数类型（`parameters[].type`）对照

参考：[script-sdk-v2.md §4.1 参数定义和读取](script-sdk-v2.md#41-参数定义和读取)

| 清单 `type` | C# 读取方法 | 示例包位置 |
| --- | --- | --- |
| `number` | `GetNumber(id)` → `double` | `BeatPulseNeon`、`PetalFall`、`RubySplitEmphasis`、`StarTrail`、`UtopiaEmphasis`、`WordScatterExit`、`WordSwellEmphasis`；以及 `FrostedGlassMaterial`、`HoloPrismGlow`、`LiquidGlassMaterial` 图/着色器参数 |
| `integer` | `GetInteger(id)` → `long` | `PetalFall`（`lineDensity`、`burstCount`）、`BeatPulseNeon`（`barCount`） |
| `duration` | `GetDuration(id)` → `long`（微秒） | `UtopiaEmphasis`（`attackUs`、`decayUs`）、`WordSwellEmphasis`（`returnUs`）、`RubySplitEmphasis`（`attackUs`、`returnUs`）、`WordScatterExit`（`exitUs`）、`StarTrail`（`lifeUs`、`trailUs`、`bridgeUs`、`bridgeGapLimitUs`）、`PetalFall`（`lifeUs`） |
| `boolean` | `GetBoolean(id)` → `bool` | `UtopiaEmphasis`（`flashGlow`）、`RubySplitEmphasis`（`rubyFallbackSlice`、`flashGlow`）、`WordScatterExit`（`exitGlow`）、`StarTrail`（`bridgeLines`）、`BeatPulseNeon`（`showEqualizer`） |
| `enum` | `GetEnum(id)` → `string` | `UtopiaEmphasis`（`decayMode`、`anchor`）、`WordSwellEmphasis`（`anchor`）、`RubySplitEmphasis`（`emphasisMode`、`baseAnchor`、`rubyAnchor`）、`PetalFall`（`layer`）、`LiquidGlassMaterial` & `FrostedGlassMaterial`（`backdropSource`，图输入直接绑定） |
| `color` | `GetColor(id)` → `ColorRgba` | `UtopiaEmphasis`、`RubySplitEmphasis`、`WordScatterExit`、`StarTrail`、`PetalFall`、`BeatPulseNeon`；以及 `LiquidGlassMaterial` & `FrostedGlassMaterial`（`tintColor`） |
| `curve` | `GetCurve(id)` → `CurveValue` | `UtopiaEmphasis`（`decayCurve`）、`WordSwellEmphasis`（`growCurve`、`returnCurve`）、`RubySplitEmphasis`（`growCurve`、`returnCurve`）、`WordScatterExit`（`exitCurve`、`opacityCurve`） |

---

## 3. C# 控制器 API 对照

参考：[script-sdk-v2.md §5 入口与准备数据](script-sdk-v2.md#5-入口与准备数据)、[§6](script-sdk-v2.md#6-源歌词场景与当前帧)、[§7](script-sdk-v2.md#7-几何注音与播放头)、[§8](script-sdk-v2.md#8-文字变换与网格变形)、[§9](script-sdk-v2.md#9-填充描边阴影辉光与扫字)

| API / 类型 | 用途说明 | 示例源码 |
| --- | --- | --- |
| `IPureKaraokeEffect<TPrepared>` + `PreparedEffect` | 纯文字阶段入口（`Declare` + `Evaluate`） | [`UtopiaEmphasis.cs`](../packages/UtopiaEmphasis/src/UtopiaEmphasis.cs)、[`WordSwellEmphasis.cs`](../packages/WordSwellEmphasis/src/WordSwellEmphasis.cs) |
| `IPureVisualEffect<TPrepared>` | 增加粒子计划/晚期绘制入口 `EvaluateVisuals` | [`StarTrail.cs`](../packages/StarTrail/src/StarTrail.cs)、[`PetalFall.cs`](../packages/PetalFall/src/PetalFall.cs)、[`BeatPulseNeon.cs`](../packages/BeatPulseNeon/src/BeatPulseNeon.cs) |
| `frame.Presentations.TokensInBinding` & `PerformanceSample` | 读取绑定内当前元素及 `ProgressSource`、`TimeRange`、`Progress`、`IsCurrent`、`IsSung` | `UtopiaEmphasis`、`WordSwellEmphasis`、`BeatPulseNeon` |
| `frame.Geometry` & `BaseGeometry.CanTransformIndependently` | 连字安全检查与支点计算（字框中心 / 基线 / 底部） | `UtopiaEmphasis`、`WordSwellEmphasis`、`BeatPulseNeon` |
| `output.AppendTransform(token.WithRuby, matrix)` | 围绕支点等比缩放、与平移矩阵组合（`Translation.Compose(ScaleAt)`） | `UtopiaEmphasis`、`WordSwellEmphasis`、`BeatPulseNeon` |
| `token.BaseOnly` & `cluster.Target`（`TextClusterTarget`） | 主字与注音单字簇独立缩放、旋转与散落（含 `Affine2(a, b, c, d, tx, ty)`） | [`RubySplitEmphasis.cs`](../packages/RubySplitEmphasis/src/RubySplitEmphasis.cs)、[`WordScatterExit.cs`](../packages/WordScatterExit/src/WordScatterExit.cs) |
| `source.Content.Ruby`（`SourceRubySegment`） | 读取注音分段时间并在 `Declare` 预建 UTF-16 映射表（含未分段宽度切片兜底） | [`RubySplitEmphasis.cs`](../packages/RubySplitEmphasis/src/RubySplitEmphasis.cs) |
| `output.MultiplyOpacity` / `frame.LineLoads`（`ExitReplica`） | 逐字淡出保持隐身及行退场副本接续动画 | [`WordScatterExit.cs`](../packages/WordScatterExit/src/WordScatterExit.cs) |
| `output.BindGraph(token.WithRuby, ...)` | 控制器在 `Evaluate` 按需动态绑定包内 `graph` 导出与量化参数 | [`WordScatterExit.cs`](../packages/WordScatterExit/src/WordScatterExit.cs) |
| `output.SetGlow(..., TextState.Sung, GlowValue)` | 脉冲闪耀辉光、双色霓虹辉光与主字独立辉光 | `UtopiaEmphasis`、`BeatPulseNeon`、`WordScatterExit`、`RubySplitEmphasis` |
| `source.Timeline` & `TimelineSelector` | 在 `Declare` 阶段查询 `TokenStarted` 源事件构建 `EventBurstPlan` | `PetalFall`（`TimelineEventKind.TokenStarted`） |
| `CursorEmissionPlan` / `EventBurstPlan` / `ParticleSpec` | 声明连续播放头发射（含 `BridgePolicy`）与事件爆发粒子 | `StarTrail`（连续 `spine` / `dust`）、`PetalFall`（爆发 `petal-burst`） |
| `frame.Particles.Alive` / `Births` / `GetTrail(id)` | 晚期绘制读取存活粒子、出生窗历史与宿主解析拖尾 | `StarTrail`（`Births` 串联主光带 + `GetTrail`）、`PetalFall` |
| `frame.FinalGeometry.Playback.Cursors` | 读取最终变换/变形后的实时播放头坐标 | `StarTrail`（双层反旋星芒主星） |
| `frame.FinalGeometry.Lines` & `GeometrySample.MapPoint` | 从局部行框映射到最终画布坐标（跟随入场/变换） | `PetalFall`（行周花瓣）、`BeatPulseNeon`（行底频谱条） |
| `source.Resources.GetVector(id)` & `output.DrawPaths` | 加载静态 SVG 矢量资源并按 `Affine2` 绘制（含旋转与模拟 3D 缩放翻转） | `StarTrail`、`PetalFall` |
| `output.DrawTrails` / `output.DrawSegments` | 绘制可变宽渐隐拖尾与 `BehindText` 线段频谱条 | `StarTrail`（`Trails`）、`BeatPulseNeon`（`Segments`） |
| `frame.Audio.GetAt(timeUs)` & `AudioAvailability` | 读取 `BassLevel` / `Pulse` / `BeatPhase`，并在无分析时按 `fallbackBpm` 降级 | `BeatPulseNeon` |
| `frame.Random.GetUnitDouble(streamKey, index)` | 确定性伪随机抖动（星尘/花瓣相角、大小、速度与配色插值） | `StarTrail`、`PetalFall` |

---

## 4. 图、Shader 与材质包对照

参考：[script-sdk-v2.md §16 图、shader 与材质包格式](script-sdk-v2.md#16-图shader-与材质包格式)

### 4.1 `material` 导出（`LiquidGlassMaterial`、`FrostedGlassMaterial`）

- **声明方式**：`exports[].kind = "material"`，`regions = ["fill", "stroke"]`，`coverageMode = "masked"`，`outputs[].bypass = {"kind": "materialFallback", "mapping": "identity"}`。
- **透明兜底**：配置了 `Linear` 线性渐变 `fallback`（`colorSpace: "LinearRgb"`，8 位十六进制 `#RRGGBBAA`）；在透明输出渲染时，宿主自动触发 `TransparentBackgroundDependency` 诊断并回退到该渐变。
- **动态背景来源选择**：两份图均使用 `"source": "parameter:backdropSource"` 与 `"choices": {"media": "BackgroundMedia", "below": "BackdropBelow"}`，将 `availability` 设为 `"optional"`、`backgroundRequirement` 设为 `"media"`，使同一套材质包可在“底层背景媒体”与“真实下层合成画面”间切换。
- **节点链路与外扩边界**：`Blur` 节点使用 `"expandPx": "ceil(3 * parameter:blurPx)"`（遵守 `maxExpandPx` 与 `maxSampleOffsetPx` 预算），下游 `RuntimeShader` 结合 `TargetRegionCoverage` 梯度计算边缘法线，输出**已乘 alpha 的 RGBA**（`half4(lit * alpha, alpha)`）。

### 4.2 `graph` 导出（`HoloPrismGlow`、`WordScatterExit`）

- **`HoloPrismGlow`**：`exports[].kind = "graph"`，包级 `requires` 声明 `["image.graph"]`，`bindingKinds` 为 `["project", "branch", "lines", "clips", "node"]`（输出 `target: "line"`）。
  1. 输入捕获 `original`（`TargetColor`，`region: "fillAndStroke"`）与 `sung`（`SungCoverage`，`region: "fill"`）。
  2. `sungOnly`（`op: "Mask"`）仅裁出已唱部分，未唱部分完全不进入 Bloom。
  3. `bloom`（`op: "Blur"`）对已唱区域做高斯扩散，外扩按 `ceil(3 * parameter:bloomSigmaPx)` 声明。
  4. `prism`（`op: "RuntimeShader"`）在 SkSL 中合成原始清晰图像、已唱部分 RGB 色散、全息流光 sheen 与 Bloom 光晕。
- **`WordScatterExit`（`token-blur`）**：在混合控制器包中额外导出 `kind = "graph"`、`target: "tokenFamily"`，由 `Blur`（`sigmaPx: "parameter:blurSigmaPx"`）连接单位 `Transform`（`fit`）组成，由控制器在 `Declare` 预构建量化 `GraphBindingValue` 并在 `Evaluate` 阶段动态 `BindGraph` 给正在退场的 `token.WithRuby`。

---

## 5. 主文档中尚未在本批 10 例演示的进阶能力

本仓库收录上述 10 个示例包；如需查阅 SDK v2 其余高阶特性，可直接定位到 [script-sdk-v2.md](script-sdk-v2.md) 对应章节与主仓库参考包：

- **局部网格变形（`GridWarp`、`output.SetWarp`）**：见 [script-sdk-v2.md §8.3 Warp](script-sdk-v2.md#83-warp)（主仓库对照：`WaveWarp`）。
- **重复演唱次数索引（`TimelineIndex.Ordinal`）**：见 [script-sdk-v2.md §12 时间线与源索引](script-sdk-v2.md#12-时间线与源索引)（主仓库对照：`RepeatEmphasis`）。
- **背景媒体亮度/纹理统计（`mediaQueries`、`frame.Media.Sample`）**：见 [script-sdk-v2.md §17.2–17.5](script-sdk-v2.md#172-声明背景媒体查询)（主仓库对照：`AdaptiveStroke`）。
- **PNG 图片资产与 SVG 图集（`DrawSprites`、`DrawAtlasSprites`、`mode: "atlas"`）**：见 [script-sdk-v2.md §14.2](script-sdk-v2.md#142-图元与方法) 与 [§15.3 SVG 图集](script-sdk-v2.md#153-svg-图集)（主仓库对照：`SpriteTrail`、`VectorTrail`）。
- **脚本内动态覆盖材质 uniform（`SetMaterialUniform`）**：见 [script-sdk-v2.md §10.2 逐帧覆盖材质 uniform](script-sdk-v2.md#102-逐帧覆盖材质-uniform)。
