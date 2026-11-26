# SDK v2 API 与示例包对照索引

本文档将 [SDK v2 完整 API 参考](script-sdk-v2.md) 中的核心能力、清单字段、C# 入口与纯资源图/着色器写法，逐一映射到 [`packages/`](../packages/) 下的 28 个官方示例包，方便按 API 查用法或按示例查规范。

---

## 1. 二十八个示例包总览

| 目录 | `packageId` | 导出种类与目标 | 声明能力（`requires`） | 核心示范点 |
| --- | --- | --- | --- | --- |
| [`AcousticRippleRain`](../packages/AcousticRippleRain) | `makokara.sample.acoustic-ripple-rain` | `controller` → `tokenFamily` + `canvas` | `token.warp`、`particles.burst`、`visuals.segments`、`visuals.dots`、`timeline.index` | `TokenStarted` 踩点斜落雨丝、音频调制透视椭圆涟漪、`BurstAnchor.SourceToken` 水花 `DrawDots` 与单字 `4×2` `GridWarp` 微震 |
| [`AdaptiveChromeFoil`](../packages/AdaptiveChromeFoil) | `makokara.sample.adaptive-chrome-foil` | `controller` + `material` → `tokenFamily` + `region` (`fill`, `stroke`) | `style.fill`、`style.stroke`、`material.uniform`、`media.features` | `media.features` 时域平滑窗口采样上下背景天光/地反色彩、经 `SetMaterialUniform` 实时写入液态镀铬材质（含金属渐变 `fallback`） |
| [`BeatEmberStorm`](../packages/BeatEmberStorm) | `makokara.sample.beat-ember-storm` | `controller` → `canvas` (`Overlay`) | `particles.continuous`、`particles.burst`、`visuals.sprites`、`visuals.trails`、`resources.svg.atlas`、`timeline.index` | `Declare` 阶段 `source.Audio.GetRange` 构建动态变速 `RateTrack`、双事件音频加权爆发、`mode: "atlas"` SVG 图集 + `DrawAtlasSprites` 与 `GetTrail` |
| [`BeatKineticEcho`](../packages/BeatKineticEcho) | `makokara.sample.beat-kinetic-echo` | `controller` + `graph` → `line` | `image.graph` | 音频驱动 3 路并行动态 `Transform` + `Blur` + `Blend(plus/sourceOver)` 发光残影（`radialZoom`/`horizontalShear`/`orbitalSplit`，支持 `sungOnly`） |
| [`BeatPulseNeon`](../packages/BeatPulseNeon) | `makokara.sample.beat-pulse-neon` | `controller` → `tokenFamily` + `canvas` | `token.affine`、`style.glow`、`visuals.segments` | `AudioFeatures`（含备用 BPM 退化）、`OverlayLayer.BehindText` 线段频谱条、`integer` 参数 |
| [`ChromaFireflyBokeh`](../packages/ChromaFireflyBokeh) | `makokara.sample.chroma-firefly-bokeh` | `controller` → `canvas` (`Overlay`) | `particles.continuous`、`particles.burst`、`media.features`、`visuals.sprites`、`visuals.dots`、`visuals.trails`、`resources.svg.atlas`、`timeline.index` | 400ms 平滑窗口画布三分区背景取色自适应、`BehindText` 焦外散景 SVG 图集 `DrawAtlasSprites` 与 `AboveText` 萤火 `DrawTrails` + `DrawDots` |
| [`CyberCrtGlitch`](../packages/CyberCrtGlitch) | `makokara.sample.cyber-crt-glitch` | `controller` + `graph` → `line` | `image.graph`、`line.exit` | CRT 扫描线/RGB 磷光格栅、`AudioFeatures` 触发块状撕裂色散与 `ExitReplica` 关机电子束坍缩退场 |
| [`DirectionalFadeTransition`](../packages/DirectionalFadeTransition) | `makokara.sample.directional-fade-transition` | `controller` → `tokenFamily` | `token.affine`、`token.opacity`、`line.exit` | 零分配简洁整行方向淡入淡出，支持独立配置入场/出场滑动方向（原地/左/右/上/下）、距离与缓动曲线 |
| [`EmberIgniteBurn`](../packages/EmberIgniteBurn) | `makokara.sample.ember-ignite-burn` | `controller` + `graph` → `line` | `image.graph`、`line.exit` | `SungCoverage` 8 邻域方向梯度燃边、音频调制 FBM 火舌热浪、`moltenGold`/`charredEmber` 双质感与燃尽化灰退场 |
| [`FrostedGlassMaterial`](../packages/FrostedGlassMaterial) | `makokara.sample.frosted-glass` | `material` → `region` (`fill`, `stroke`) | `[]` | 大半径 Blur（`ceil(3 * parameter:blurPx)`）+ 晶粒散射 SkSL、缎面高光、渐变 `fallback` |
| [`HoloPrismGlow`](../packages/HoloPrismGlow) | `makokara.sample.holo-prism-glow` | `graph` → `line` | `image.graph` | 整行 `TargetColor` + `SungCoverage` 遮罩、`Mask` → `Blur` → `RuntimeShader` 三节点 DAG、RGB 色散与虹彩 SkSL |
| [`LiquidGlassMaterial`](../packages/LiquidGlassMaterial) | `makokara.sample.liquid-glass` | `material` → `region` (`fill`, `stroke`) | `[]` | `masked` 区域覆盖材质、`parameter:backdropSource` 条件输入（`BackgroundMedia` / `BackdropBelow`）、Blur + SkSL 折射、渐变 `fallback` |
| [`MangaHalftonePop`](../packages/MangaHalftonePop) | `makokara.sample.manga-halftone-pop` | `graph` → `line` | `image.graph` | 零 C# 纯图实现 45°/15° 半调网点矩阵、Risograph 双色错版套印重影与硬边漫画斜线投影 |
| [`NoteBurst`](../packages/NoteBurst) | `makokara.sample.note-burst` | `controller` → `canvas` (`Overlay`) | `timeline.index`、`particles.burst`、`visuals.paths`、`visuals.dots`、`resources.svg.vector` | `TokenStarted` 分层扇形向上爆发单音符（🎵）/连音符（🎶）静态 SVG 矢量与闪烁星点，短促弹跳阻尼消散 |
| [`OpticalInkErosion`](../packages/OpticalInkErosion) | `makokara.sample.optical-ink-erosion` | `controller` + `graph` → `line` + `tokenFamily` + `canvas` | `token.opacity`、`token.affine`、`style.fill`、`style.stroke`、`style.glow`、`image.graph`、`line.exit`、`media.features`、`visuals.trails`、`visuals.dots` | `frame.Media.Sample` 32×18 网格 2×2 Lucas-Kanade 局部光流风场、SkSL 负空间侵蚀平流与历史积分墨丝 `DrawTrails`/`DrawDots` |
| [`PetalFall`](../packages/PetalFall) | `makokara.sample.petal-fall` | `controller` → `canvas` (`Overlay`) | `particles.burst`、`visuals.paths`、`resources.svg.vector` | `TimelineSelector` + `EventBurstPlan`、`FinalGeometry.Lines` 行包围框映射、SVG 3D 翻转仿射、图层切换 |
| [`RefractivePrismCrystal`](../packages/RefractivePrismCrystal) | `makokara.sample.refractive-prism-crystal` | `controller` + `graph` → `line` + `tokenFamily` | `token.opacity`、`style.fill`、`style.stroke`、`style.glow`、`image.graph`、`line.exit` | 弯月面背景折射、反差自适应极细描边、音节中心聚光穿透色散与 `ExitReplica` 亮度梯度碎化消融 |
| [`RepeatCadenceEmphasis`](../packages/RepeatCadenceEmphasis) | `makokara.sample.repeat-cadence-emphasis` | `controller` → `tokenFamily` + `canvas` | `token.affine`、`style.glow`、`timeline.index`、`visuals.trails`、`visuals.segments`、`visuals.dots` | `TimelineIndex.Ordinal` 与词频归一化统计词素重复次数，随重复次数递进鼓点挤压拉伸、高频阻尼震颤、升温辉光与字后冲击波环/放射刻度 |
| [`RubySplitEmphasis`](../packages/RubySplitEmphasis) | `makokara.sample.ruby-split-emphasis` | `controller` → `baseToken` + `cluster` | `token.affine`、`cluster.affine`、`style.glow` | `BaseTokenTarget` 与 `TextClusterTarget` 分离缩放、`SourceRubySegment` 分段时间表与未分段宽度切片兜底 |
| [`SceneLitCastShadow`](../packages/SceneLitCastShadow) | `makokara.sample.scene-lit-cast-shadow` | `controller` + `graph` → `line` | `image.graph`、`media.features` | 四象限 `media.features`（350ms 窗口）主光源反演、互补阴影色推导与基线斜切压扁 `Transform` + `Blur` + `Blend` 立体投影 |
| [`SceneWaterReflection`](../packages/SceneWaterReflection) | `makokara.sample.scene-water-reflection` | `controller` + `graph` → `line` | `image.graph`、`media.features` | 动态基线垂直镜像 `Transform` 矩阵注入、`Blur` + 水波 SkSL 扭曲 + `Blend`，结合 250ms `media.features` 水面明暗与波纹调制 |
| [`SparkleWake`](../packages/SparkleWake) | `makokara.sample.sparkle-wake` | `controller` → `canvas` (`Overlay`) | `particles.continuous`、`visuals.paths`、`resources.svg.vector` | 播放头双色四角星芒尾迹、真实字形墨迹高度均匀分布与像素长度衰减控制 |
| [`StarTrail`](../packages/StarTrail) | `makokara.sample.star-trail` | `controller` → `canvas` (`Overlay`) | `particles.continuous`、`visuals.trails`、`visuals.paths`、`resources.svg.vector` | 双连续发射器（`cursor`）、`BridgePolicy`、粒子 `GetTrail`、SVG 矢量、`DrawTrails` + `DrawPaths` |
| [`UtopiaEmphasis`](../packages/UtopiaEmphasis) | `makokara.sample.utopia-emphasis` | `controller` → `tokenFamily` | `token.affine`、`style.glow` | `number` / `duration` / `enum` / `curve` / `boolean` / `color` 参数、瞬间突放回落、`SetGlow` |
| [`WindParticleTransition`](../packages/WindParticleTransition) | `makokara.sample.wind-particle-transition` | `controller` + `graph` → `tokenFamily` + `canvas` | `token.affine`、`token.opacity`、`style.glow`、`image.graph`、`line.exit`、`timeline.index`、`particles.burst`、`visuals.trails`、`visuals.sprites`、`visuals.dots`、`resources.svg.atlas` | 行显现左侧风丝与自选色粒子刮入聚合入场；唱完词素经 Voronoi 尘粒 SkSL + `TokenCompleted` 爆发碎散向后飘远（含 `line.exit` 句尾解析续播） |
| [`WordFlipTransition`](../packages/WordFlipTransition) | `makokara.sample.word-flip-transition` | `controller` → `tokenFamily` | `token.affine`、`token.opacity`、`line.exit` | 逐字三轴投影翻转出入场、`line.exit`（2.0s）对称翻折退场、长短行等比压缩与连字安全检查 |
| [`WordScatterExit`](../packages/WordScatterExit) | `makokara.sample.word-scatter-exit` | `controller` + `graph` → `baseToken` + `cluster` + `tokenFamily` | `token.affine`、`cluster.affine`、`token.opacity`、`style.glow`、`image.graph`、`line.exit` | 逐字唱完旋转散落退出、注音字簇独立飘散、`line.exit` 退出副本、`BindGraph` 逐字高斯模糊 |
| [`WordSwellEmphasis`](../packages/WordSwellEmphasis) | `makokara.sample.word-swell-emphasis` | `controller` → `tokenFamily` | `token.affine` | 演唱进度渐变膨胀、`IsSung` 唱完后平滑回落、组合平移与缩放矩阵、`CanTransformIndependently` |

---

## 2. 包清单与能力矩阵

参考：[script-sdk-v2.md §3 包清单与能力声明](script-sdk-v2.md#3-包清单与能力声明)

### 2.1 清单根合同

除 `WordFlipTransition`（`2000000`）、`OpticalInkErosion`（`1800000`）、`WindParticleTransition`（`1800000`）、`DirectionalFadeTransition`（`1500000`）、`WordScatterExit`（`1500000`）、`EmberIgniteBurn`（`1200000`）、`RefractivePrismCrystal`（`900000`）与 `CyberCrtGlitch`（`600000`）声明非零 `"lineExit": {"durationUs": ...}` 与 `"line.exit"` 能力外，其余 20 个包均为零行退场；所有包均遵循 SDK v2 包格式 3 固定头部：

- `"packageVersion": 3`
- `"sdkVersion": "2.0.0"`
- `"shaderProfile": "skia-ganesh-3.119.4-v2"`
- `"lineExit": {"durationUs": 0}`（或非零退场微秒数，上限 `3000000`）
- `"files"` 中每项必须包含 `path`、`role` 与 64 位小写 `sha256`，可用 [`tools/pack.py`](../tools/pack.py) 校验与刷新。

### 2.2 文件角色（`files[].role`）对照

| `role` | 含义 | 本仓库实际示例 |
| --- | --- | --- |
| `csharp` | C# 14 控制器源码 | 除纯图/纯材质包（`FrostedGlassMaterial`、`HoloPrismGlow`、`LiquidGlassMaterial`、`MangaHalftonePop`）外的 24 个控制器/混合包各自的 `src/*.cs` |
| `asset` | 随包分发的 `README.md` 等一般文件 | 全部 28 个包的 `README.md` |
| `svg` | 静态 SVG 源文件 | `BeatEmberStorm`、`ChromaFireflyBokeh`、`NoteBurst`、`PetalFall`、`SparkleWake`、`StarTrail`、`WindParticleTransition` 各自的 `assets/*.svg` |
| `svgResource` | SVG 使用描述 JSON（`mako-svg-static-1` 矢量或 `mode: "atlas"` 图集） | 矢量：`NoteBurst`、`PetalFall`、`SparkleWake`、`StarTrail`；图集：`BeatEmberStorm`、`ChromaFireflyBokeh`、`WindParticleTransition` 各自的 `resources/*.json` |
| `graph` | 图像图定义 JSON（`graphVersion: 1`） | `AdaptiveChromeFoil`、`BeatKineticEcho`、`CyberCrtGlitch`、`EmberIgniteBurn`、`FrostedGlassMaterial`、`HoloPrismGlow`、`LiquidGlassMaterial`、`MangaHalftonePop`、`OpticalInkErosion`、`RefractivePrismCrystal`、`SceneLitCastShadow`、`SceneWaterReflection`、`WindParticleTransition`、`WordScatterExit` 各自的 `graphs/*.graph.json` |
| `shaderProgram` | SkSL 程序描述 JSON（`programVersion: 1`、`profile: "skia-raster-3.119.4-v1"`） | 上述除 `WordScatterExit` 外的 13 个含自定义着色器包各自的 `shaders/*.program.json` |
| `shader` | SkSL 着色器源码 | 上述 13 个含自定义着色器包各自的 `shaders/*.sksl` |

### 2.3 参数类型（`parameters[].type`）对照

参考：[script-sdk-v2.md §4.1 参数定义和读取](script-sdk-v2.md#41-参数定义和读取)

| 清单 `type` | C# 读取方法 | 示例包位置 |
| --- | --- | --- |
| `number` | `GetNumber(id)` → `double` | 全部 28 个包均广泛使用（控制器读取或图/着色器参数绑定） |
| `integer` | `GetInteger(id)` → `long` | `AcousticRippleRain`（`rippleRings`、`splashCount`）、`BeatEmberStorm`（`burstMaxCount`）、`BeatPulseNeon`（`barCount`）、`ChromaFireflyBokeh`（`bokehBurstCount`）、`PetalFall`（`lineDensity`、`burstCount`） |
| `duration` | `GetDuration(id)` → `long`（微秒） | `AcousticRippleRain`、`AdaptiveChromeFoil`（`envSmoothUs` 驱动 `mediaQueries` 窗口）、`BeatEmberStorm`、`ChromaFireflyBokeh`、`OpticalInkErosion`、`PetalFall`、`RefractivePrismCrystal`、`RubySplitEmphasis`、`SparkleWake`、`StarTrail`、`UtopiaEmphasis`、`WordFlipTransition`、`WordScatterExit`、`WordSwellEmphasis` |
| `boolean` | `GetBoolean(id)` → `bool` | `AcousticRippleRain`、`BeatEmberStorm`、`BeatKineticEcho`、`BeatPulseNeon`、`ChromaFireflyBokeh`、`CyberCrtGlitch`、`RubySplitEmphasis`、`SceneLitCastShadow`、`UtopiaEmphasis`、`WordFlipTransition`、`WordScatterExit` |
| `enum` | `GetEnum(id)` → `string` | `BeatKineticEcho`（`echoMode`）、`EmberIgniteBurn`（`burnStyle`）、`PetalFall` / `SparkleWake`（`layer`）、`RubySplitEmphasis`（`emphasisMode`、`baseAnchor`、`rubyAnchor`）、`UtopiaEmphasis`（`decayMode`、`anchor`）、`WordFlipTransition`（`enterOrder`、`exitOrder`、`anchor`）、`WordSwellEmphasis`（`anchor`）；以及 `FrostedGlassMaterial` / `LiquidGlassMaterial` / `OpticalInkErosion` / `RefractivePrismCrystal`（`backdropSource`，图输入直接绑定） |
| `color` | `GetColor(id)` → `ColorRgba` | 除 `HoloPrismGlow` 与 `WordFlipTransition` 外绝大多数包均包含 `color` 参数（C# 读取或图/着色器参数） |
| `curve` | `GetCurve(id)` → `CurveValue` | `RubySplitEmphasis`、`UtopiaEmphasis`、`WordFlipTransition`、`WordScatterExit`、`WordSwellEmphasis` |

---

## 3. C# 控制器 API 对照

参考：[script-sdk-v2.md §5 入口与准备数据](script-sdk-v2.md#5-入口与准备数据)、[§6](script-sdk-v2.md#6-源歌词场景与当前帧)、[§7](script-sdk-v2.md#7-几何注音与播放头)、[§8](script-sdk-v2.md#8-文字变换与网格变形)、[§9](script-sdk-v2.md#9-填充描边阴影辉光与扫字)、[§10](script-sdk-v2.md#10-图绑定与材质参数)、[§17](script-sdk-v2.md#17-音频与背景媒体特征)

| API / 类型 | 用途说明 | 示例源码 |
| --- | --- | --- |
| `IPureKaraokeEffect<TPrepared>` + `PreparedEffect` | 纯文字阶段入口（`Declare` + `Evaluate`） | [`UtopiaEmphasis.cs`](../packages/UtopiaEmphasis/src/UtopiaEmphasis.cs)、[`WordFlipTransition.cs`](../packages/WordFlipTransition/src/WordFlipTransition.cs)、[`AdaptiveChromeFoil.cs`](../packages/AdaptiveChromeFoil/src/AdaptiveChromeFoil.cs) |
| `IPureVisualEffect<TPrepared>` | 增加粒子计划/晚期绘制入口 `EvaluateVisuals` | [`StarTrail.cs`](../packages/StarTrail/src/StarTrail.cs)、[`BeatEmberStorm.cs`](../packages/BeatEmberStorm/src/BeatEmberStorm.cs)、[`ChromaFireflyBokeh.cs`](../packages/ChromaFireflyBokeh/src/ChromaFireflyBokeh.cs)、[`AcousticRippleRain.cs`](../packages/AcousticRippleRain/src/AcousticRippleRain.cs) |
| `frame.Presentations.TokensInBinding` & `PerformanceSample` | 读取绑定内当前元素及 `ProgressSource`、`TimeRange`、`Progress`、`IsCurrent`、`IsSung` | `UtopiaEmphasis`、`WordSwellEmphasis`、`BeatPulseNeon`、`WordFlipTransition` |
| `frame.Geometry` & `BaseGeometry.CanTransformIndependently` | 连字安全检查与支点计算（字框中心 / 基线 / 底部） | `UtopiaEmphasis`、`WordSwellEmphasis`、`WordFlipTransition`、`AcousticRippleRain`、`AdaptiveChromeFoil` |
| `output.AppendTransform(token.WithRuby, matrix)` | 围绕支点等比缩放、与平移/投影矩阵组合 | `UtopiaEmphasis`、`WordSwellEmphasis`、`BeatPulseNeon`、`WordFlipTransition` |
| `output.SetWarp(token.WithRuby, new GridWarp(...))` | 在局部外扩网格定义域上构建正向非退化 `GridWarp` 水波微震 | [`AcousticRippleRain.cs`](../packages/AcousticRippleRain/src/AcousticRippleRain.cs) |
| `token.BaseOnly` & `cluster.Target`（`TextClusterTarget`） | 主字与注音单字簇独立缩放、旋转与散落（含 `Affine2(a, b, c, d, tx, ty)`） | [`RubySplitEmphasis.cs`](../packages/RubySplitEmphasis/src/RubySplitEmphasis.cs)、[`WordScatterExit.cs`](../packages/WordScatterExit/src/WordScatterExit.cs) |
| `source.Content.Ruby`（`SourceRubySegment`） | 读取注音分段时间并在 `Declare` 预建 UTF-16 映射表（含未分段宽度切片兜底） | [`RubySplitEmphasis.cs`](../packages/RubySplitEmphasis/src/RubySplitEmphasis.cs) |
| `output.MultiplyOpacity` / `frame.LineLoads`（`ExitReplica`） | 逐字淡出保持隐身及行退场副本接续动画 | `WordScatterExit`、`WordFlipTransition`、`CyberCrtGlitch`、`EmberIgniteBurn`、`OpticalInkErosion`、`RefractivePrismCrystal` |
| `output.SetFill` / `output.SetStrokes` / `output.SetGlow` | 逐帧覆盖填充材质、自适应反差描边与脉冲辉光 | `AdaptiveChromeFoil`、`OpticalInkErosion`、`RefractivePrismCrystal`、`UtopiaEmphasis`、`BeatPulseNeon` |
| `output.SetMaterialUniform(...)` | 控制器在 `Evaluate` 逐帧向挂载材质节点写入自定义 uniform（`UniformValue`） | [`AdaptiveChromeFoil.cs`](../packages/AdaptiveChromeFoil/src/AdaptiveChromeFoil.cs) |
| `output.BindGraph(...)` + `GraphTransformValues` / `GraphUniformValues` | 绑定包内 `graph` 导出，动态注入节点 `Transform` 矩阵/`outputBounds` 与着色器 uniform | `BeatKineticEcho`、`SceneWaterReflection`、`SceneLitCastShadow`、`CyberCrtGlitch`、`EmberIgniteBurn`、`OpticalInkErosion`、`RefractivePrismCrystal`、`WordScatterExit` |
| `mediaQueries` + `frame.Media.Sample(...)` | 声明时域平滑窗口（固定或参数驱动 `envSmoothUs`）并在帧内采样 `Mean`/`MeanPremulY`/`PremulYVariance`（含 `Pending`/`Unavailable`/`NoMedia` 退化） | `AdaptiveChromeFoil`、`ChromaFireflyBokeh`、`OpticalInkErosion`、`SceneLitCastShadow`、`SceneWaterReflection` |
| `source.Audio.GetRange(...)` & `RateTrack` | 在 `Declare` 阶段按时间片扫描全曲音频构建分段线性变速发射轨（含无音频 `fallbackBpm` 退化） | [`BeatEmberStorm.cs`](../packages/BeatEmberStorm/src/BeatEmberStorm.cs) |
| `frame.Audio.GetAt(timeUs)` & `AudioAvailability` | 读取 `BassLevel` / `Pulse` / `BeatPhase`，并在无分析时按 `fallbackBpm` 降级 | `BeatPulseNeon`、`BeatKineticEcho`、`CyberCrtGlitch`、`EmberIgniteBurn`、`AcousticRippleRain` |
| `source.Timeline` & `TimelineSelector` / `TimelineIndex.Ordinal` | 在 `Declare` 阶段查询 `TokenStarted` / `TokenCompleted` 源事件构建 `EventBurstPlan`，或统计相同词素的历史重复演唱次数 | `PetalFall`、`AcousticRippleRain`、`BeatEmberStorm`、`ChromaFireflyBokeh`、`NoteBurst`、`WindParticleTransition`、`RepeatCadenceEmphasis` |
| `CursorEmissionPlan` / `EventBurstPlan` / `ParticleSpec` | 声明连续播放头发射（含 `BridgePolicy`）与事件爆发粒子 | `StarTrail`、`SparkleWake`、`PetalFall`、`BeatEmberStorm`、`ChromaFireflyBokeh`、`AcousticRippleRain` |
| `frame.Particles.Alive` / `Births` / `GetTrail(id)` | 晚期绘制读取存活粒子、出生窗历史与宿主解析拖尾 | `StarTrail`、`SparkleWake`、`PetalFall`、`BeatEmberStorm`、`ChromaFireflyBokeh`、`AcousticRippleRain` |
| `frame.FinalGeometry.Playback.Cursors` & `Lines` (`MapPoint`) | 读取最终实时播放头坐标与行包围框坐标映射（跟随入场/变换） | `StarTrail`、`SparkleWake`、`PetalFall`、`BeatPulseNeon` |
| `source.Indexes.Presentations` | 查询基础布局与可见行装载区间（用于过滤未装载事件或计算行内退场进度） | `BeatEmberStorm`、`CyberCrtGlitch`、`EmberIgniteBurn`、`SparkleWake` |
| `source.Resources.GetVector(id)` & `output.DrawPaths` | 加载静态 SVG 矢量资源并按 `Affine2` 绘制 | `StarTrail`、`SparkleWake`、`PetalFall` |
| `source.Resources.GetSprite(id, variant)` & `output.DrawAtlasSprites` | 加载 `mode: "atlas"` SVG 图集多档位资源并按 `AtlasSprite` 批量绘制 | [`BeatEmberStorm.cs`](../packages/BeatEmberStorm/src/BeatEmberStorm.cs)、[`ChromaFireflyBokeh.cs`](../packages/ChromaFireflyBokeh/src/ChromaFireflyBokeh.cs) |
| `output.DrawTrails` / `output.DrawSegments` / `output.DrawDots` | 绘制可变宽渐隐拖尾、线段与圆点微珠粒子 | `StarTrail`（`Trails`）、`BeatPulseNeon`（`Segments`）、`AcousticRippleRain`（`Segments` + `Dots`）、`BeatEmberStorm`（`Trails`）、`ChromaFireflyBokeh`（`Trails` + `Dots`）、`OpticalInkErosion`（`Trails` + `Dots`） |
| `frame.Random.GetUnitDouble(streamKey, index)` | 确定性伪随机抖动（粒子相角、大小、速度、配色插值与星芒闪烁） | `StarTrail`、`SparkleWake`、`PetalFall`、`BeatEmberStorm`、`ChromaFireflyBokeh`、`AcousticRippleRain` |

---

## 4. 图、Shader 与材质包对照

参考：[script-sdk-v2.md §16 图、shader 与材质包格式](script-sdk-v2.md#16-图shader-与材质包格式)

### 4.1 `material` 导出（`LiquidGlassMaterial`、`FrostedGlassMaterial`、`AdaptiveChromeFoil`）

- **声明方式**：`exports[].kind = "material"`，`regions = ["fill", "stroke"]`，`coverageMode = "masked"`，`outputs[].bypass = {"kind": "materialFallback", "mapping": "identity"}`。
- **透明兜底**：均配置了 `Linear` 线性渐变 `fallback`（`colorSpace: "LinearRgb"`，8 位十六进制 `#RRGGBBAA`）；在透明输出渲染时，宿主自动触发 `TransparentBackgroundDependency` 诊断并回退到该渐变（`AdaptiveChromeFoil` 在控制器主动切至无背景依赖样式或以独立材质模式挂载时均安全渲染）。
- **动态背景来源与 uniform 注入**：
  - `LiquidGlassMaterial` 与 `FrostedGlassMaterial` 使用 `"source": "parameter:backdropSource"` 与 `"choices": {"media": "BackgroundMedia", "below": "BackdropBelow"}` 在底层媒体与下层合成画面间切换；
  - `AdaptiveChromeFoil` 将 `chrome-material` 与控制器组合，由控制器把时域平滑采样的天光/地反色彩通过 `SetMaterialUniform` 注入着色器 `uSkyColor` / `uGroundAndState`，同时支持独立材质挂载。
- **节点链路与外扩边界**：`Blur` 节点使用 `"expandPx": "ceil(3 * parameter:blurPx)"`（遵守 `maxExpandPx` 与 `maxSampleOffsetPx` 预算），下游 `RuntimeShader` 结合 `TargetRegionCoverage` 梯度计算边缘法线，输出**已乘 alpha 的 RGBA**（`half4(lit * alpha, alpha)`）。

### 4.2 `graph` 导出

- **纯图包（零 C# 控制器，`kind = "graph"`, `target: "line"`）**：
  - **`HoloPrismGlow`**：捕获 `TargetColor` + `SungCoverage`，经 `Mask` → `Blur` → `RuntimeShader` 合成原始清晰字形、已唱 RGB 色散、全息流光 sheen 与 Bloom 光晕。
  - **`MangaHalftonePop`**：捕获 `TargetColor`、`SungCoverage` 与 `GlyphCoverage`，全部输入 `backgroundRequirement: "none"`；单节点 `RuntimeShader` 结合 `host:timeSeconds` 输出 45°/15° 半调网点、Risograph 双色错版套印与 45° 斜线漫画硬阴影。
- **动态 `Transform` + `Blend` 混合图包（`controller` + `graph`）**：
  - **`BeatKineticEcho`**：`echoSource` 经三路并行 `Transform`（实时接收 `GraphTransformValues` 做放射放大/水平斜切/轨道旋转）→ `Blur` → 极简双色 `RuntimeShader` 后，通过两级 `Blend(mode: "plus")` 叠加发光残影，末级以 `Blend(mode: "sourceOver")` 将清晰原字覆盖顶层。
  - **`SceneWaterReflection`**：`mirror`（`Transform`）实时接收按每行 `Baseline` 构造的垂直镜像斜切矩阵与受控 `outputBounds`，经 `Blur` 与水波扭曲 `RuntimeShader` 后通过 `Blend(mode: "sourceOver")` 与原字合成。
  - **`SceneLitCastShadow`**：`cast`（`Transform`）按四象限光源主方向实时注入基线斜切压扁矩阵，经 `Blur` 半影模糊与阴影渐隐 `RuntimeShader` 后，在 `Blend(mode: "sourceOver")` 下层合成为立体斜影。
- **多阶段时序/媒体感知混合图包（`controller` + `graph`）**：
  - **`CyberCrtGlitch`** / **`EmberIgniteBurn`**：控制器读取 `AudioFeatures`（含 `fallbackBpm` 降级）与 `ExitReplica` 阶段进度，通过 `GraphUniformValues` 驱动 CRT 故障撕裂/关机坍缩与 `SungCoverage` 8 邻域梯度 FBM 燃边；
  - **`OpticalInkErosion`** / **`RefractivePrismCrystal`**：结合 `TargetColor`、`SungCoverage` 与可选背景输入实现光流墨迹平流侵蚀与音节聚光晶体折射，并通过 `OutputMode.Transparent` 逻辑保证透明导出稳定通过；
  - **`WordScatterExit`（`token-blur`）** / **`WindParticleTransition`（`wind-token`）**：额外导出 `target: "tokenFamily"`，末端串联全画布 `outputBounds` 单位 `Transform`（`fit`）以防裁切同行未退场字符，在 `Evaluate` 动态 `BindGraph` 给过渡字族做逐字高斯模糊或 Voronoi 风蚀碎裂。

---

## 5. 主文档中尚未在本批 28 例演示的进阶能力

本仓库 28 个示例包已覆盖绝大部分 SDK v2 能力；如需查阅少数尚未收录的高阶特性，可直接定位到 [script-sdk-v2.md](script-sdk-v2.md) 对应章节与主仓库参考包：

- **PNG 位图精灵（`DrawSprites`、`Sprite`）**：见 [script-sdk-v2.md §14.2 图元与方法](script-sdk-v2.md#142-图元与方法)（主仓库对照：`SpriteTrail`；SVG 图集 `mode: "atlas"` + `DrawAtlasSprites` 已有 `BeatEmberStorm`、`ChromaFireflyBokeh`、`WindParticleTransition` 演示）。
- **独立标点/分组目标（`TextMarkTarget`、`TextTarget.Group`）**：见 [script-sdk-v2.md §7.3 行、标记和组](script-sdk-v2.md#73-行标记和组) 与 [§8.1 选择目标](script-sdk-v2.md#81-选择目标)。
