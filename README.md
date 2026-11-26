# MakoKaraGenerator-OfficialMkfxSamples

> [!WARNING]
> 本仓库目前仍处于积极开发阶段，仓库内的 `.mkfx` 示例包、参数接口与实现细节可能随时调整或发生破坏性变更，**请勿将其当作稳定版效果包直接用于正式生产环境**。

MakoKaraGenerator 官方 MKFX 效果包示例仓库（SDK `2.0.x` / 包格式 `packageVersion: 3`）。

仓库同时提供各效果包的**展开目录源码与资源**（`packages/`）以及对应的**预打包 `.mkfx` 归档**（`dist/`），并提供 SDK v2 API 对照文档、清单摘要校验/本地打包脚本，以及直接对接 `MakoKaraGenerator-App` 后端的 Vulkan GPU 真机渲染验证工具。

---

## 仓库结构

```text
.
├── .agents/skills/        # Agent 工作区 Skill（mkfx-v2-authoring）
├── dist/                  # 27 个已打包的 .mkfx 效果包归档（可直接导入应用）
├── packages/              # 27 个展开后的 SDK v2 示例效果包目录
├── docs/                  # SDK v2 API 完整参考与示例对照文档
│   ├── script-sdk-v2.md
│   └── api-sample-mapping.md
├── tools/                 # 清单校验/本地打包工具与后端真实渲染验证器
│   ├── pack.py
│   └── Verify/
├── LICENSE
└── README.md
```

---

## 示例效果包索引（`packages/` / `dist/`，共 27 个）

| 目录 | `packageId` | 类型 | 核心特性 |
| --- | --- | --- | --- |
| [`packages/AcousticRippleRain`](packages/AcousticRippleRain) | `makokara.sample.acoustic-ripple-rain` | 控制器 (`controller`) | 精准踩点斜落雨丝、音频调制透视椭圆涟漪、抛物线水花飞溅与单字 `4×2` `GridWarp` 水面微震 |
| [`packages/AdaptiveChromeFoil`](packages/AdaptiveChromeFoil) | `makokara.sample.adaptive-chrome-foil` | 控制器 + 材质 (`controller` + `material`) | `media.features` 时域平滑采样天光/地反色彩并经 `SetMaterialUniform` 注入镀铬材质（含渐变兜底） |
| [`packages/BeatEmberStorm`](packages/BeatEmberStorm) | `makokara.sample.beat-ember-storm` | 控制器 (`controller`) | `Declare` 阶段 `Audio.GetRange` 构建变速 `RateTrack`、双事件爆发、SVG 图集热烟/火星与流光火丝 |
| [`packages/BeatKineticEcho`](packages/BeatKineticEcho) | `makokara.sample.beat-kinetic-echo` | 控制器 + 图 (`controller` + `graph`) | 音频驱动 3 路并行 `Transform` + `Blur` + `Blend(plus)` 放射/斜切/轨道残影（支持 `sungOnly`） |
| [`packages/BeatPulseNeon`](packages/BeatPulseNeon) | `makokara.sample.beat-pulse-neon` | 控制器 (`controller`) | 音频特征（含备用 BPM）驱动脉冲缩放、霓虹辉光与字后频谱条 |
| [`packages/ChromaFireflyBokeh`](packages/ChromaFireflyBokeh) | `makokara.sample.chroma-firefly-bokeh` | 控制器 (`controller`) | 画布三分区背景取色自适应、`BehindText` 焦外散景 SVG 图集与 `AboveText` 萤火柔光微尾 |
| [`packages/CyberCrtGlitch`](packages/CyberCrtGlitch) | `makokara.sample.cyber-crt-glitch` | 控制器 + 图 (`controller` + `graph`) | CRT 扫描线与 RGB 磷光格栅、音频触发块状撕裂色散与 `ExitReplica` 关机坍缩退场 |
| [`packages/EmberIgniteBurn`](packages/EmberIgniteBurn) | `makokara.sample.ember-ignite-burn` | 控制器 + 图 (`controller` + `graph`) | `SungCoverage` 8 邻域梯度燃烧前沿、音频调制 FBM 火舌热浪、`moltenGold`/`charredEmber` 双模态与燃尽退场 |
| [`packages/FrostedGlassMaterial`](packages/FrostedGlassMaterial) | `makokara.sample.frosted-glass` | 材质 (`material`) | 大半径高斯模糊、微米晶粒散射、缎面高光与透明渐变兜底 |
| [`packages/HoloPrismGlow`](packages/HoloPrismGlow) | `makokara.sample.holo-prism-glow` | 图效果 (`graph`) | 整行 `SungCoverage` 遮罩提取、Bloom、RGB 棱镜色散与全息流光 |
| [`packages/LiquidGlassMaterial`](packages/LiquidGlassMaterial) | `makokara.sample.liquid-glass` | 材质 (`material`) | 折射模糊、轮廓梯度 Fresnel 边缘、色散、流光与透明渐变兜底 |
| [`packages/MangaHalftonePop`](packages/MangaHalftonePop) | `makokara.sample.manga-halftone-pop` | 图效果 (`graph`) | 零 C# 纯图实现 45°/15° 半调网点矩阵、Risograph 双色错版套印与硬边漫画斜线投影 |
| [`packages/NoteBurst`](packages/NoteBurst) | `makokara.sample.note-burst` | 控制器 (`controller`) | `TokenStarted` 起唱瞬间分层扇形向上爆出单音符（🎵）/连音符（🎶）静态 SVG 矢量粒子与星点，短促弹跳后迅速消散 |
| [`packages/OpticalInkErosion`](packages/OpticalInkErosion) | `makokara.sample.optical-ink-erosion` | 控制器 + 图 (`controller` + `graph`) | `media.features` 2×2 Lucas-Kanade 局部光流风场、SkSL 负空间侵蚀平流与历史积分墨丝飞溅 |
| [`packages/PetalFall`](packages/PetalFall) | `makokara.sample.petal-fall` | 控制器 (`controller`) | 行周 3D 翻转樱花雨 + `TokenStarted` 逐字惊落爆发粒子 |
| [`packages/RefractivePrismCrystal`](packages/RefractivePrismCrystal) | `makokara.sample.refractive-prism-crystal` | 控制器 + 图 (`controller` + `graph`) | 弯月面折射、反差自适应极细描边、音节中心聚光穿透与 `ExitReplica` 亮度梯度碎化消融 |
| [`packages/RepeatCadenceEmphasis`](packages/RepeatCadenceEmphasis) | `makokara.sample.repeat-cadence-emphasis` | 控制器 (`controller`) | 基于 `TimelineIndex.Ordinal` 与归一化词频统计词素重复次数，随出现次数递进鼓点挤压拉伸缩放、高频震颤、升温辉光与字后冲击波环 |
| [`packages/RubySplitEmphasis`](packages/RubySplitEmphasis) | `makokara.sample.ruby-split-emphasis` | 控制器 (`controller`) | 主字（`BaseOnly`）与注音单字簇（`TextClusterTarget`）分离缩放，支持 `SourceRubySegment` 定时与未分段宽度切片兜底 |
| [`packages/SceneLitCastShadow`](packages/SceneLitCastShadow) | `makokara.sample.scene-lit-cast-shadow` | 控制器 + 图 (`controller` + `graph`) | 四象限 `media.features` 光源探针、互补冷暖阴影色推导与基线斜切压扁立体投影 |
| [`packages/SceneWaterReflection`](packages/SceneWaterReflection) | `makokara.sample.scene-water-reflection` | 控制器 + 图 (`controller` + `graph`) | 动态基线垂直镜像 `Transform` 矩阵注入与水波 SkSL 扭曲，结合 `media.features` 水面明暗与波纹调制 |
| [`packages/SparkleWake`](packages/SparkleWake) | `makokara.sample.sparkle-wake` | 控制器 (`controller`) | 播放头双色四角星芒尾迹、真实字形墨迹高度均匀分布与像素长度衰减控制 |
| [`packages/StarTrail`](packages/StarTrail) | `makokara.sample.star-trail` | 控制器 (`controller`) | 播放头双层旋转 SVG 主星、连续粒子、彗星拖尾与跨行飞行衔接 |
| [`packages/UtopiaEmphasis`](packages/UtopiaEmphasis) | `makokara.sample.utopia-emphasis` | 控制器 (`controller`) | 仿 Utopia 逐字爆发缩放、可配置衰减曲线与闪耀辉光 |
| [`packages/WindParticleTransition`](packages/WindParticleTransition) | `makokara.sample.wind-particle-transition` | 控制器 + 图 (`controller` + `graph`) | 左侧风流与自选色粒子刮入聚合成句入场，已唱词素经 Voronoi 风蚀 SkSL 与 `TokenCompleted` 爆发碎裂成粒子和流线风丝向后飘散（含 `line.exit` 句尾续播） |
| [`packages/WordFlipTransition`](packages/WordFlipTransition) | `makokara.sample.word-flip-transition` | 控制器 (`controller`) | 逐字三轴投影翻转出入场（含 `line.exit` 2.0s 对称翻折与左右顺序切换） |
| [`packages/WordScatterExit`](packages/WordScatterExit) | `makokara.sample.word-scatter-exit` | 控制器 + 图 (`controller` + `graph`) | 逐字唱完旋转散落淡出、注音字簇飘散、`line.exit` 句尾续播与逐字高斯模糊（`BindGraph`） |
| [`packages/WordSwellEmphasis`](packages/WordSwellEmphasis) | `makokara.sample.word-swell-emphasis` | 控制器 (`controller`) | 逐字平滑膨胀、上浮与唱完回落，支持连字安全判断 |

---

## 文档导航（`docs/`）

- [SDK v2 完整 API 参考](docs/script-sdk-v2.md)：包格式 3 清单、C# 纯求值入口、几何映射、样式、粒子、SVG、ImageGraph/SkSL 与音频/媒体特征完整规范。
- [SDK v2 API 与示例包对照索引](docs/api-sample-mapping.md)：按清单字段、参数类型、C# API 与图/着色器机制快速定位到本仓库具体示例。
- [SDK v2 进阶脚本编写经验与避坑指南](docs/mkfx-v2-authoring.md)：沉淀纯度零分配写法、目标粒度选择、逐字退场、Ruby 分段/兜底切片与局部 `BindGraph` 防裁切等实战经验（对应工作区 Skill：[`.agents/skills/mkfx-v2-authoring/SKILL.md`](.agents/skills/mkfx-v2-authoring/SKILL.md)）。

---

## 辅助工具与验证（`tools/`）

### 1. 清单摘要校验与本地 `.mkfx` 导出（`tools/pack.py`）

使用 Python 3 标准库实现，打包格式与 `MakoKaraGenerator-App` 后端 `EffectPackageTransfer.WriteArchiveAsync` 一致（先写 `manifest.json`，再按 ordinal 路径顺序写入声明文件，固定时间戳 `2000-01-01 00:00:00`）。

```bash
python3 tools/pack.py --check                   # 仅校验全部包的合同字段、文件闭包与 SHA-256 摘要
python3 tools/pack.py --refresh                 # 修改源码或资源后，仅更新 manifest.json 的 SHA-256
python3 tools/pack.py                           # 更新摘要并导出全部 .mkfx 到 dist/
python3 tools/pack.py UtopiaEmphasis            # 仅打包指定效果包
```

### 2. 后端编译准入与 Vulkan 真实渲染验证（`tools/Verify/`）

`tools/Verify` 直接引用同级主仓库 `../MakoKaraGenerator-App/backend`（可通过 `-p:MakoBackendRoot=<路径>` 覆盖），对 `packages/` 中的每个效果包依次执行：

1. `EffectPackageStorage.ReadSupportedDirectoryAsync` 与 `EffectPackageDefinition.Read` 目录级闭包/语义检查；
2. `.mkfx` 打包写入与重读的 `PackageId` / `Revision` 往返一致性检查；
3. `CompiledEffectPackage.CompileAsync` 全套准入验证（C# Roslyn + 纯度分析器、Shader、ImageGraph、Material 回退、SVG 矢量）；
4. 使用 `ProjectRenderSessionFactory(..., renderBackend: "vulkan")` 在 1280×720 日语卡拉 OK 测试工程上分别以普通背景（含彩色棋盘格）和透明背景渲染多帧，检查无 Error 诊断且相对无效果基准确实产生像素变化，并将渲染结果写入 `out/<包名>/`。

需要 **.NET 10 SDK**、**Vulkan 运行环境**与系统字体 **Noto Sans CJK JP**：

```bash
dotnet build -c Release --artifacts-path .artifacts tools/Verify/Verify.csproj
dotnet .artifacts/bin/Verify/release/Verify.dll --root .
dotnet .artifacts/bin/Verify/release/Verify.dll --root . --only LiquidGlassMaterial,HoloPrismGlow
```
