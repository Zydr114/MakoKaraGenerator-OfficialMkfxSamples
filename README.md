# MakoKaraGenerator-OfficialMkfxSamples

MakoKaraGenerator 官方 MKFX 效果包示例仓库（SDK `2.0.x` / 包格式 `packageVersion: 3`）。

仓库同时提供各效果包的**展开目录源码与资源**（`packages/`）以及对应的**预打包 `.mkfx` 归档**（`dist/`），并提供 SDK v2 API 对照文档、清单摘要校验/本地打包脚本，以及直接对接 `MakoKaraGenerator-App` 后端的 Vulkan GPU 真机渲染验证工具。

---

## 仓库结构

```text
.
├── .agents/skills/        # Agent 工作区 Skill（mkfx-v2-authoring）
├── dist/                  # 10 个已打包的 .mkfx 效果包归档（可直接导入应用）
├── packages/              # 10 个展开后的 SDK v2 示例效果包目录
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

## 示例效果包索引（`packages/` / `dist/`）

| 目录 | `packageId` | 类型 | 核心特性 |
| --- | --- | --- | --- |
| [`packages/BeatPulseNeon`](packages/BeatPulseNeon) | `makokara.sample.beat-pulse-neon` | 控制器 (`controller`) | 音频特征（含备用 BPM）驱动脉冲缩放、霓虹辉光与字后频谱条 |
| [`packages/FrostedGlassMaterial`](packages/FrostedGlassMaterial) | `makokara.sample.frosted-glass` | 材质 (`material`) | 大半径高斯模糊、微米晶粒散射、缎面高光与透明渐变兜底 |
| [`packages/HoloPrismGlow`](packages/HoloPrismGlow) | `makokara.sample.holo-prism-glow` | 图效果 (`graph`) | 整行 `SungCoverage` 遮罩提取、Bloom、RGB 棱镜色散与全息流光 |
| [`packages/LiquidGlassMaterial`](packages/LiquidGlassMaterial) | `makokara.sample.liquid-glass` | 材质 (`material`) | 折射模糊、轮廓梯度 Fresnel 边缘、色散、流光与透明渐变兜底 |
| [`packages/PetalFall`](packages/PetalFall) | `makokara.sample.petal-fall` | 控制器 (`controller`) | 行周 3D 翻转樱花雨 + `TokenStarted` 逐字惊落爆发粒子 |
| [`packages/RubySplitEmphasis`](packages/RubySplitEmphasis) | `makokara.sample.ruby-split-emphasis` | 控制器 (`controller`) | 主字（`BaseOnly`）与注音单字簇（`TextClusterTarget`）分离缩放，支持 `SourceRubySegment` 定时与未分段宽度切片兜底 |
| [`packages/StarTrail`](packages/StarTrail) | `makokara.sample.star-trail` | 控制器 (`controller`) | 播放头双层旋转 SVG 主星、连续粒子、彗星拖尾与跨行飞行衔接 |
| [`packages/UtopiaEmphasis`](packages/UtopiaEmphasis) | `makokara.sample.utopia-emphasis` | 控制器 (`controller`) | 仿 Utopia 逐字爆发缩放、可配置衰减曲线与闪耀辉光 |
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
