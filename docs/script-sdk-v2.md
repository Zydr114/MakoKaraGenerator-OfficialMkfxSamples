# 脚本 SDK v2 完整 API 参考

适用版本：SDK `2.0.x`，包格式 `3`。源码核对日期：2026-11-17；项目版本 `0.115.0`，HTTP 合同 `1.56.0`。

这份文档面向编写歌词动画、粒子、画布图元和材质的开发者。正文说明写法、参数和使用规则；文末提供完整类型与成员速查。示例使用 C# 14，以及应用随附的 .NET 10 脚本参考库。脚本效果使用 Vulkan GPU 执行。

> **仓库同步说明**：本文档同步自 `MakoKaraGenerator-App/docs/style-reference/script-sdk-v2.md`。本仓库中对应的 8 个官方示例包与 API 对照索引请参见 [api-sample-mapping.md](api-sample-mapping.md) 与 [`packages/`](../packages/)。

## 目录

1. [快速开始](#1-快速开始)
2. [时间、坐标、颜色与身份](#2-时间坐标颜色与身份)
3. [包清单与能力声明](#3-包清单与能力声明)
4. [参数、曲线与随机数](#4-参数曲线与随机数)
5. [入口与准备数据](#5-入口与准备数据)
6. [源歌词、场景与当前帧](#6-源歌词场景与当前帧)
7. [几何、注音与播放头](#7-几何注音与播放头)
8. [文字变换与网格变形](#8-文字变换与网格变形)
9. [填充、描边、阴影、辉光与扫字](#9-填充描边阴影辉光与扫字)
10. [图绑定与材质参数](#10-图绑定与材质参数)
11. [行退场](#11-行退场)
12. [时间线与源索引](#12-时间线与源索引)
13. [粒子计划与粒子数据](#13-粒子计划与粒子数据)
14. [晚期绘制](#14-晚期绘制)
15. [图片与 SVG 资源](#15-图片与-svg-资源)
16. [图、shader 与材质包格式](#16-图shader-与材质包格式)
17. [音频与背景媒体特征](#17-音频与背景媒体特征)
18. [编辑、验证与发布](#18-编辑验证与发布)
19. [规模与诊断参考](#19-规模与诊断参考)
20. [完整类型与成员速查](#20-完整类型与成员速查)
21. [示例与源码索引](#21-示例与源码索引)

## 1. 快速开始

一个 C# 效果包至少包含 `manifest.json` 和一个 `.cs` 文件。下面的效果把正在演唱的字及其注音逐渐放大。

### 1.1 编写入口

保存为 `src/QuickStart.cs`：

```csharp
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record QuickStartData(double PeakScale) : PreparedEffect;

public sealed class QuickStart : IPureKaraokeEffect<QuickStartData>
{
    public QuickStartData Declare(DeclareContext source)
        => new(source.Parameters.GetNumber("peakScale"));

    public void Evaluate(in TextFrame frame, QuickStartData data, TextOutput output)
    {
        foreach (var token in frame.Presentations.TokensInBinding)
        {
            if (!token.Performance.IsCurrent || token.Performance.Progress is not { } progress)
                continue;

            foreach (var geometry in frame.Geometry)
            {
                if (geometry.WithRuby != token.WithRuby ||
                    !geometry.BaseGeometry.CanTransformIndependently)
                    continue;

                double scale = 1 + (data.PeakScale - 1) * Curves.EaseOut(progress);
                output.AppendTransform(token.WithRuby,
                    Affine2.ScaleAt(scale, geometry.BaseGeometry.AdvanceBounds.Center));
            }
        }
    }
}
```

`WithRuby` 表示基础字与关联注音组成的字族。`Progress` 是当前字的演唱进度。围绕字框中心缩放时，使用与矩阵相同的局部坐标。

### 1.2 编写清单

保存为 `manifest.json`。其中 `sha256` 先保留空串，再执行下一步填入文件摘要。

```json
{
  "packageVersion": 3,
  "packageId": "example.quick-start",
  "name": "逐字放大",
  "version": "1.0.0",
  "sdkVersion": "2.0.0",
  "shaderProfile": "skia-ganesh-3.119.4-v2",
  "files": [
    {"path": "src/QuickStart.cs", "role": "csharp", "sha256": ""}
  ],
  "parameters": [
    {"id": "peakScale", "type": "number", "label": "最大比例", "default": 1.3, "min": 1, "max": 3}
  ],
  "exports": [
    {
      "id": "quick-start",
      "kind": "controller",
      "name": "逐字放大",
      "entryType": "QuickStart",
      "parameters": ["peakScale"],
      "bindingKinds": ["project", "branch", "lines", "tokens", "clips", "node"],
      "outputs": [
        {"id": "scale", "kind": "VisualTransform", "target": "tokenFamily", "requires": [], "dependsOn": []}
      ]
    }
  ],
  "requires": ["token.affine"],
  "lineExit": {"durationUs": 0},
  "emitters": []
}
```

### 1.3 更新摘要并使用

在包目录执行以下命令。每次修改包内文件后，执行一次即可更新 `files` 中全部文件的 SHA-256。

```bash
python3 - <<'PY'
from pathlib import Path
import hashlib
import json
manifest_path = Path("manifest.json")
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
for item in manifest["files"]:
    item["sha256"] = hashlib.sha256(Path(item["path"]).read_bytes()).hexdigest()
manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY
```

在样式库中导入效果包，创建作者草稿，选择 GPU，编译并采用该源码修订。在试样中调整参数，再保存到效果组件库，供模板和工程使用。包文件可通过作者页下载为 `.mkfx`。

## 2. 时间、坐标、颜色与身份

### 2.1 时间

时间统一使用 `long` 整数微秒，`1_000_000` 微秒等于一秒。`frame.TimeUs` 是歌曲中的绝对时刻；动画应直接由这个时刻和固定输入计算。

`new TimeRange(startUs, endUs)` 表示半开区间 `[startUs, endUs)`：`startUs >= 0`，`endUs > startUs`。成员为 `StartUs`、`EndUs`、`DurationUs`、`IsValid` 和 `Contains(timeUs)`。例如 `[0, 1_000_000)` 在一秒整时已经结束。

可空时间或进度表示“尚无该项数据”。读取 `TimeRange?`、`Progress?`、`ProjectDurationUs` 时，先按可空值分支处理。粒子的 `BirthWindow` 另用两端都包含的区间，详见粒子一节。

### 2.2 坐标与矩阵

画布原点位于左上，X 向右，Y 向下；尺寸与图元位置使用逻辑像素。文字的字框、基线和 Warp 定义域使用目标局部坐标；通过几何对象的映射方法取得画布坐标。

| 类型 / 方法 | 用途 |
| --- | --- |
| `Point2(x, y)` | 一个位置 |
| `Vector2(x, y)`、`Vector2.Zero` | 位移、方向、速度或加速度；单位随字段规定 |
| `Rect2(left, top, right, bottom)` | 四条边的位置；提供 `Width`、`Height`、`Center` |
| `Affine2(a, b, c, d, tx, ty)` | 六分量仿射；`x' = a*x + c*y + tx`，`y' = b*x + d*y + ty` |
| `Affine2.Identity` | 恒等变换 |
| `Affine2.Translation(x, y)` | 平移 |
| `Affine2.ScaleAt(factor, pivot)` | 绕支点做正比例等比缩放 |
| `matrix.Map(point)` | 映射点 |
| `parent.Compose(child)` | 先应用 `child`，再应用 `parent` |
| `matrix.TryInverse(out inverse)` | 获取可用的逆矩阵，成功返回 `true` |

几何分量使用有限数值。`Rect2` 的右边界大于或等于左边界，下边界大于或等于上边界。变换使用可逆矩阵；恒等值写作 `Affine2.Identity`。

### 2.3 颜色

`new ColorRgba(red, green, blue, alpha)` 使用 sRGB 颜色，每个分量取 `[0,1]`，alpha 与 RGB 分开表示。白色为 `new ColorRgba(1, 1, 1, 1)`。`MultiplyRgb(factor)` 调整 RGB，并保留 alpha；结果分量仍需位于 `[0,1]`。

背景媒体统计中的 `LinearPremulMean` 使用线性、已乘 alpha 的颜色，详见媒体特征一节。

### 2.4 身份

| 身份 | 代表的对象 |
| --- | --- |
| `BranchId`、`LineId`、`TokenId`、`RubyId` | 源歌词分支、行、元素、注音 |
| `NodeId`、`ProgramId`、`ClipId`、`CueId` | 场景节点、显示程序、片段、前奏配置 |
| `BindingId` | 一次真实的展示装载 |
| `OccurrenceId` | 一次真实的源元素演唱 |
| `EventId`、`CursorTrackId` | 时间线事件、播放头轨道 |
| `EffectInstanceId` | 当前效果实例 |
| `EmitterId`、`OutputId` | 包声明的发射器与输出 |
| `DrawUnitId` | 一个真实字形绘制单元 |

这些类型大多提供 `Value` 与 `IsValid`。查询和输出时沿用宿主返回的身份；包中自行声明的发射器和输出使用清单中对应的名字。同一行可以有多个装载，每个装载各有 `BindingId`，而源歌词身份保持一致。

## 3. 包清单与能力声明

### 3.1 清单字段

| 字段 | 写法与含义 |
| --- | --- |
| `packageVersion` | `3` |
| `packageId`、`name`、`version` | 包 ID、显示名、包自身版本 |
| `sdkVersion` | 规范的 `2.0.x` 版本号，例如 `2.0.0` |
| `shaderProfile` | `skia-ganesh-3.119.4-v2` |
| `files` | 文件条目：`path`、`role`、`sha256`；摘要为 64 位小写十六进制 |
| `parameters` | 全包参数定义 |
| `exports` | 一个或多个导出；导出 ID 在包内唯一 |
| `requires` | 全包使用的能力名 |
| `lineExit` | `{"durationUs": 0}`；非零时声明 `line.exit` |
| `emitters` | 发射器定义；每项有 `id`、`kind`、`maxLifetimeUs` |
| `mediaQueries` | 背景媒体查询定义；由各导出显式引用 |

除 `mediaQueries` 可按需省略外，表中其余根字段均填写；空集合写 `[]`，零退场写 `{"durationUs":0}`。JSON 对象使用本参考列出的字段，每个键出现一次。后文标为“加入”或“示意”的 JSON 是相应位置的片段。

文件路径为包内相对路径，采用 `/` 分隔。`manifest.json` 单独作为清单，其余分发内容列入 `files`。源码和资源按这一份文件集合及摘要共同确定版本。

| `files[].role` | 内容 |
| --- | --- |
| `csharp` | C# 源码 |
| `asset` | README 等一般附属文件 |
| `image` | 包图片 |
| `svg` | SVG 源文件 |
| `svgResource` | SVG 使用描述文件 |
| `graph` | 图 JSON |
| `shaderProgram` | shader 程序描述 JSON |
| `shader` | SkSL 源码 |

### 3.2 三种导出

`controller` 导出由 C# 入口实现；`entryType` 为入口完整类型名。其 `parameters` 列出该导出使用的参数 ID，`bindingKinds` 列出可绑定范围，`outputs` 描述文字或画布输出。

`graph` 导出引用 `graph` 文件，由宿主按绑定范围应用图效果。`material` 导出也引用图，另用 `regions` 指定 `fill` / `stroke`，用 `coverageMode` 指定 `masked` / `unmasked`，并给出 `fallback` 基础外观。这两类导出可完全由 JSON 和 SkSL 编写。

绑定范围可为 `project`、`branch`、`lines`、`tokens`、`clips`、`node`。节点绑定还由实例配置选择是否包含后代。输出范围随绑定确定；读取时间线等源信息的范围由实例的读取配置确定。

| 输出 `kind` | 支持的 `target` | 用途 |
| --- | --- | --- |
| `VisualTransform` | `tokenFamily`、`baseToken`、`line`、`group`、`textMark`、`cluster` | 仿射与对应目标的 Warp |
| `StyleModulation` | `tokenFamily`、`baseToken`、`group`、`textMark` | 对应目标的透明度、外观、扫字或材质参数 |
| `ImageGraph` | `tokenFamily`、`line`、`group`、`textMark` | 图绑定 |
| `ImageGraph` | `region` | 材质导出 |
| `Overlay` | `canvas` | controller 的粒子与晚期图元 |

每项输出包括 `id`、`kind`、`target`、`requires` 和 `dependsOn`。当前可执行导出的 `dependsOn` 写作 `[]`。手动晚期绘制还需在 `Declare` 的 `VisualOutputs` 中声明相同的输出 ID、图元种类与图层。

### 3.3 能力名速查

| 用途 | `requires` 中的能力名 |
| --- | --- |
| 仿射 | `token.affine`、`line.affine`、`group.affine`、`mark.affine`、`cluster.affine` |
| 透明度 | `token.opacity`、`group.opacity` |
| 外观 | `style.fill`、`style.stroke`、`style.shadow`、`style.glow`、`style.wipe` |
| 材质参数 | `material.uniform` |
| Warp | `token.warp`、`cluster.warp`、`mark.warp` |
| 时间线与退场 | `timeline.index`、`line.exit` |
| 粒子 | `particles.continuous`、`particles.burst` |
| 晚期绘制 | `visuals.dots`、`visuals.segments`、`visuals.trails`、`visuals.sprites`、`visuals.paths` |
| SVG | `resources.svg.vector`、`resources.svg.atlas` |
| 图 | `image.graph`、`image.graph.group` |
| 背景特征 | `media.features` |

C# 中可用 `CapabilityNames` 的同值常量，`CapabilityNames.All` 列出全集。名称区分大小写。画布输出在自身的 `requires` 中同时列出所用能力；全包 `requires` 覆盖各导出的能力需求。

## 4. 参数、曲线与随机数

### 4.1 参数定义和读取

所有参数都有 `id`、`type`、`label`、`default`，可附 `unit`。`id` 区分大小写。数值上下限是参数可编辑的完整范围。

| `type` | `default` 的例子 | 读取方法与结果 |
| --- | --- | --- |
| `number` | `1.25` | `GetNumber(id)` → `double`；定义 `min`、`max` |
| `integer` | `8` | `GetInteger(id)` → `long`；定义 `min`、`max` |
| `duration` | `300000` | `GetDuration(id)` → `long` 微秒；定义非负 `min`、`max` |
| `boolean` | `true` | `GetBoolean(id)` → `bool` |
| `enum` | `"soft"` | `GetEnum(id)` → 选项 ID；另定义 `values` |
| `text` | `"Hello"` | `GetText(id)` → `string`，允许空串 |
| `color` | `{"r":1,"g":0.5,"b":0.2,"a":1}` | `GetColor(id)` → `ColorRgba` |
| `curve` | `{"kind":"easing","easing":"easeOut"}` | `GetCurve(id)` → `CurveValue` |
| `asset` | `"assets/spark.png"` | `GetAsset(id)` → 本包 `ImageHandle` |

枚举定义示例：

```json
{"id":"mode","type":"enum","label":"模式","default":"soft","values":[{"value":"soft","label":"柔和"},{"value":"sharp","label":"锐利"}]}
```

实例参数覆盖使用 JSON 对象，例如 `{"peakScale":1.6}`。键取当前导出 `parameters` 中的 ID，值使用该参数的原类型并位于其声明范围内；其余项采用清单默认值。`unit` 用于显示，数值仍按参数类型和原单位读取。`asset` 参数的默认值指向同包 `role: "image"` 文件。

参数类型与读取方法逐项对应。`ParameterValues.Ids` 返回按名字稳定排序的参数 ID。帧中可通过 `frame.Parameters` 读取；固定参数也可以在 `Declare` 中读取并保存为准备数据。

### 4.2 曲线

`CurveValue.Evaluate(progress)` 接受 `[0,1]` 进度。可用曲线如下：

| C# | 清单中的曲线值 |
| --- | --- |
| `new CurveValue.Easing(EasingKind.Linear)` | `{"kind":"easing","easing":"linear"}` |
| `new CurveValue.Easing(EasingKind.EaseOut)` | `{"kind":"easing","easing":"easeOut"}` |
| `new CurveValue.Easing(EasingKind.EaseInOut)` | `{"kind":"easing","easing":"easeInOut"}` |
| `new CurveValue.CubicBezier(new(.2, 0), new(.8, 1))` | `{"kind":"cubicBezier","first":[0.2,0],"second":[0.8,1]}` |
| `new CurveValue.ControlPoints([new(0,0), new(.5,.8), new(1,1)])` | `{"kind":"controlPoints","points":[[0,0],[0.5,0.8],[1,1]]}` |

`EaseOut` 为三次缓出，`EaseInOut` 为 `3p²−2p³`。贝塞尔控制点 X 位于 `[0,1]`；分段控制点 X 严格递增，首尾为 `(0,0)`、`(1,1)`。自定义控制点的 Y 可用于表现过冲；把结果用于透明度、尺寸等字段时，按该字段的数值范围转换。

便捷成员：`Curves.LinearCurve`、`Curves.EaseOutCurve`、`Curves.EaseInOutCurve`，以及 `Curves.EaseOut(progress)`。

### 4.3 自行构造参数集

给包材质或图传参时，使用对应的 `ParameterValue`：

```csharp
var parameters = new ParameterValues([
    new("strength", new ParameterValue.Number(0.8)),
    new("count", new ParameterValue.Whole(8)),
    new("enabled", new ParameterValue.Toggle(true)),
    new("mode", new ParameterValue.Choice("soft")),
    new("duration", new ParameterValue.Duration(300_000)),
    new("caption", new ParameterValue.Text("Hello")),
    new("tint", new ParameterValue.Color(new ColorRgba(1, 1, 1, 1))),
    new("curve", new ParameterValue.Curve(Curves.EaseOutCurve))
]);
```

图片参数使用 `new ParameterValue.Asset(imageHandle)`。空参数集为 `ParameterValues.Empty`。传入图或材质的参数集应完整对应那个导出的 `parameters`。

### 4.4 确定性随机

```csharp
uint bits = frame.Random.GetUInt32("spark", 10UL);
double unit = frame.Random.GetUnitDouble("spark", 10UL);
```

`unit` 位于 `[0,1)`。种子、实例、流键和下标一致时，结果一致；每次查询独立，可以按任意顺序调用。为颜色、散布、旋转等用途各取一个清楚的流键，并用稳定的源顺序或粒子来源序号作为下标。

## 5. 入口与准备数据

### 5.1 生命周期

固定输入准备时调用一次 `Declare`。对请求时刻，宿主按需要依次执行文字阶段、合成文字结果、解析粒子，再执行晚期绘制阶段。预览跳转、重复取帧和乱序取帧都通过目标歌曲时刻直接求值。

| 接口 | 作者实现的方法 |
| --- | --- |
| `IPureKaraokeEffect<TPrepared>` | `TPrepared Declare(DeclareContext source)`；`void Evaluate(in TextFrame frame, TPrepared data, TextOutput output)` |
| `IPureVisualEffect<TPrepared>` | 上述两个方法，加 `void EvaluateVisuals(in VisualFrame frame, TPrepared data, VisualOutput output)` |
| `IPureKaraokeEffect` | 相同入口，准备类型统一写成 `PreparedEffect` |
| `IPureVisualEffect` | 使用 `PreparedEffect` 的晚期入口，搭配非泛型文字入口 |

泛型入口的 `TPrepared` 派生自 `PreparedEffect`，文字与晚期阶段共用同一个准备类型。接口负责泛型与非泛型调用之间的转接。

固定参数、源码、包资源、工程内容、字体布局、分析输入、画布或成片时长变化时，会建立对应的新准备结果。单纯跳转时刻可复用同一准备数据。

### 5.2 准备结果

推荐用 `sealed record` 保存数值、源身份、资源句柄和不可变集合。嵌套成员也使用只读数据。临时计算可使用局部变量、数组和局部集合，结束后转为 `ImmutableArray` 等固定值。

`PreparedEffect` 提供三个可初始化成员：

| 成员 | 默认值 | 用途 |
| --- | --- | --- |
| `Particles` | `ParticleSchedule.Empty` | 连续与事件爆发计划 |
| `VisualOutputs` | `[]` | 晚期绘制的具名输出 |
| `GeometrySampling` | `GeometrySamplingPolicy.Exact` | 粒子最终锚点的几何采样策略 |

没有准备数据时返回 `PreparedEffect.Empty`。在派生记录上用对象初始化器设置上述成员。

`DeclareContext`、帧、输出 writer、`ParticleFrame`、源查询服务和媒体采样服务在各自阶段调用内使用。长期使用的数据以返回的只读值或准备记录保存。

### 5.3 `DeclareContext`

| 成员 | 类型 | 内容 |
| --- | --- | --- |
| `Content` | `ContentCatalog` | 完整源歌词目录 |
| `Scene` | `SceneCatalog` | 完整场景目录 |
| `Parameters` | `ParameterValues` | 当前导出的固定参数 |
| `Audio` | `AudioFeatures` | 固定音频分析 |
| `Media` | `MediaQueryCatalog` | 当前导出的背景查询目录 |
| `Resources` | `ResourceCatalog` | 当前包修订的资源 |
| `Seed` | `ulong` | 固定种子 |
| `InstanceId` | `EffectInstanceId` | 当前实例身份 |
| `Random` | `DeterministicRandom` | 固定种子与实例的随机服务 |
| `Timeline` | `TimelineIndex` | 真实源事件索引 |
| `Indexes` | `SourceIndexes` | 时间线、装载、基础布局和基础播放头索引 |
| `CanvasSize` | `Vector2` | 画布逻辑宽高 |
| `ProjectDurationUs` | `long?` | 工程明确设置的成片时长；未设置时为 `null` |

脚本使用同步、确定性的 C# 计算。数学函数、自定义纯辅助方法、明确使用 ordinal 规则的文本比较、局部集合和不可变集合都可用于组织效果。时间、随机、资源和分析数据通过以上输入取得。

### 5.4 C# 辅助计算

以下标准库写法可配合 SDK 组织效果：

| 用途 | 常用写法 |
| --- | --- |
| 数学与插值 | `Math`、`MathF`，例如 `Sin`、`Cos`、`Clamp`、`Min`、`Max` |
| 文本处理 | `Concat`、`Join`、`Substring`、`Replace`、`Trim`、`Split`、`ToLowerInvariant`、`ToUpperInvariant` |
| 文本比较 | `StringComparison.Ordinal` / `OrdinalIgnoreCase`，`StringComparer.Ordinal` / `OrdinalIgnoreCase` |
| 局部集合 | 当前调用内创建的数组、`List<T>`、`Dictionary<TKey,TValue>`、`HashSet<T>` 和 immutable builder |
| 固定集合 | `System.Collections.Immutable`、`System.Collections.Frozen`，用于准备数据或固定查询结果 |
| 筛选与投影 | `Enumerable.Where`、`Select`、`ToArray` 等；排序显式传入 comparer / comparison |
| 向量与矩阵计算 | `System.Numerics` 的向量、矩阵、四元数与复数；SDK 坐标使用本参考的 `Point2` / `Vector2` |

字符串的搜索、前后缀和比较使用带 `StringComparison` 的重载，或字符重载。集合排序及二分查找显式传入比较规则。固定的网格、渐变、材质值和参数集合可在 `Declare` 中建立，逐时刻调用只计算与当前时刻有关的值。

## 6. 源歌词、场景与当前帧

### 6.1 源歌词目录

`source.Content` 提供 `Branches`、`Lines`、`Tokens`、`Ruby`。集合保持原始分支、行和行内元素顺序，序号从 0 开始。

| 类型 | 主要字段 |
| --- | --- |
| `SourceBranch` | `Id`、`Name`、`Role`、`SourceOrder`、`LineIds` |
| `SourceLine` | `Id`、`BranchId`、`Text`、`TimeRange?`、`TimingStatus`、`SourceOrder`、`TokenIds`、`RubyIds` |
| `SourceToken` | `Id`、`BranchId`、`LineId`、`Text`、`TimeRange?`、`TimingStatus`、`SourceOrder` |
| `SourceRuby` | `Id`、`LineId`、`BaseTokenId`、`Text`、`Language?`、`Segments` |
| `SourceRubySegment` | `Id`、`Text`、`TimeRange?`、`TimingStatus` |

查找方法：`GetBranch(id)`、`GetLine(id)`、`GetToken(id)`、`TryGetToken(id, out token)`、`GetRuby(id)`；`FindTokens(text, comparison = StringComparison.Ordinal)` 按文本找元素，比较方式可取 `Ordinal` 或 `OrdinalIgnoreCase`。

`SourceTimingStatus` 为 `Missing`、`Imported`、`Estimated`、`Edited`、`Confirmed`、`Invalid`。可用于执行的定时数据具有有效时间区间；`Missing` 对应空时间。注音片段保留各自的源时间，适合在准备阶段构造与注音同步的动画表。

### 6.2 场景目录

`source.Scene` 的 `Nodes`、`Programs`、`Clips`、`LeadIns` 描述固定的场景结构：

| 类型 | 字段与含义 |
| --- | --- |
| `SourceNode` | `Id`、`ParentId?`、`Kind`、`Layout`、`ReferenceRatio`、`BranchId?`、`LineId?`、`RubyId?`、`Order` |
| `SourceProgram` | `Id`、`BranchId`、`RootNodeId`、`DefaultNodeId` |
| `SourceClip` | `Id`、`ProgramId`、`ContainerId`、`LineIds`、`TimeRange` |
| `SourceCue` | `Id`、`ProgramId`、`TargetLineId`、`ContainerId`、`Enabled`、`TimeRange?` |

`SceneNodeKind` 为 `Group`、`Text`；`NodeLayoutKind` 为 `None`、`FreeLine`、`SlotSequence`、`TranslationFlow`、`Ruby`。`GetNode(id)` 查询指定节点。运行中某行具体装载到哪个槽位，可从当前帧的 `FrameLine` 读取。

### 6.3 `TextFrame`

`frame.Source` 为该实例的 `DeclareContext`；`frame.TimeUs` 为当前时刻。以下快捷属性与固定输入对应：`Parameters`、`Audio`、`Resources`、`Random`、`Timeline`、`Indexes`、`CanvasSize`、`ProjectDurationUs`。

| 其他成员 | 内容 |
| --- | --- |
| `Presentations` | 当前输出绑定内的文字和标记 |
| `LineLoads` | 行的正常装载与退场阶段 |
| `Geometry` | 各字族的基础几何 |
| `Clusters` | 当前真实字簇，按目标身份去重 |
| `LineGeometry` | 行基础几何 |
| `MarkGeometry` | 提示文字标记基础几何 |
| `GroupGeometry` | 场景组基础几何 |
| `Baseline` | 静态继承后的外观基线 |
| `Binding` | 当前实例的输出绑定 |
| `OutputMode` | `Opaque` 或 `Transparent` |
| `Media` | 本次调用的背景媒体采样入口 |

文字阶段读取源几何与源样式，宿主随后按实例顺序合成各实例的输出。每次文字调用完整成功返回时发布该次输出。

### 6.4 当前展示与演唱进度

`FramePresentations` 提供 `LinesInBinding`、`TokensInBinding`、`MarksInBinding`，以及真实组目录 `Groups`。

`FrameLine` 包括 `LineId`、`BranchId`、`BindingId`、`NodeId`、`ParentId`、`ProgramId`、可空的 `ClipId` / `CueId`、`DisplayPhase`、`EnterProgress` 和 `Target`。`DisplayPhase` 为 `Preparing`、`Performing`、`Retained`。

`FrameToken` 包括 `TokenId`、`BranchId`、`LineId`、`BindingId`、可空的 `OccurrenceId`、`SourceText`、`Performance`、`WithRuby`、`BaseOnly`。

`PerformanceSample` 的完整字段：

| 字段 | 含义 |
| --- | --- |
| `TimeRange?`、`DurationUs?` | 演唱区间及其时长 |
| `ElapsedUs?` | 相对演唱起点的局部时间，起唱前可为负数 |
| `Progress?` | `[0,1]` 演唱进度 |
| `IsCurrent` | 当前时刻处于演唱半开区间 |
| `IsSung` | 已到演唱终点 |
| `ProgressSource` | `Token`、`RubySegment`、`Line`、`Missing`、`Unavailable` |

展示阶段、入场进度、布局退出进度与演唱进度分别表达各自的动画时钟。常用逐字效果以 `IsCurrent` 和 `Progress` 判断当前字。`FrameMark` 描述真实提示标记，包含 `Target`、`TargetLine`、`ParentId`、`Text`、`IsElapsed`、`AnimationOpacity`。

## 7. 几何、注音与播放头

### 7.1 字族与几何字段

`TokenGeometrySample` 包含 `BranchId`、`LineId`、`WithRuby`、`BaseOnly`、`BaseGeometry`、`FamilyGeometry`、`Ruby`、`Clusters` 和可空的 `ProgressPath`。

`Ruby` 中每项为 `RubyGeometrySample(RubyId, Geometry)`。`FamilyGeometry` 的墨迹范围联合基础字与注音，其 `AdvanceBounds` 和 `Baseline` 仍以基础字为准；基础字的进度路径从 `ProgressPath` 读取。

`GeometrySample` 的常用字段：

| 成员 | 含义 |
| --- | --- |
| `AdvanceBounds` | 用于排版和进度路径的局部字框 |
| `InkBounds?` | 字形本身的局部墨迹包围框；空白元素为 `null`；描边、阴影和辉光另按各自外扩计算 |
| `Baseline` | 局部基线位置 |
| `BaseWorldMatrix` | 基础或最终仿射中的世界矩阵 |
| `Mapping?` | 含 Warp 时的完整点映射 |
| `WorldInkCorners` | 画布中的墨迹边界点；Warp 后可有多个顶点 |
| `MappedInkVertices` | 可显式提供已映射的墨迹顶点；`default` 按原墨迹区域推导，显式空数组表示最终墨迹为空 |
| `CanTransformIndependently` | 当前单元可独立变换 |
| `DiagnosticCode?` | 该几何的具体说明 |
| `DrawUnits` | 关联绘制单元 |

`geometry.MapPoint(point)` 和 `MapVectorAt(point, vector)` 处理完整映射。字族的 `GeometryAt(point)` 会选择该位置对应的基础字簇映射；`TokenGeometrySample.MapPoint` / `MapVectorAt` 适合跟随局部字簇动画的定位。

基础世界矩阵已包含源父组、槽位、行和入场动画。取得画布位置时，将局部点通过当前几何映射一次。

### 7.2 字簇

`TextClusterGeometrySample` 提供 `Target`、`OwnerTokens`、`Utf16Start`、`Utf16Length`、`Geometry`。`Target` 包括 `TokenId`、`BindingId`、`DrawUnitId`、`UnitIndex` 和可空的 `RubyId`。`RubyId` 有值时属于注音，否则属于基础字。

字簇使用字体整形后的实际单元。`UnitIndex` 的编号范围为整行基础字或完整的一段注音；`OwnerTokens` 列出该单元关联的全部源元素，UTF-16 范围用于定位对应源文本，独立动画边界以实际字簇为准。独立动画先检查 `CanTransformIndependently`；共享连字可采用整行变换。基础字共享连字时，关联的注音字簇仍可按自身独立性参与动画。

### 7.3 行、标记和组

`LineGeometrySample` 含 `BranchId`、`Target`、`BaseGeometry`、`ProgressPath`。`TextMarkGeometrySample` 含 `Target`、`Geometry`。`GroupGeometrySample` 含 `Target`、`ReferenceBounds`、`BaseWorldMatrix`、`WorldPivot`、`WorldInkCorners`。

### 7.4 最终播放头

晚期阶段从 `frame.FinalGeometry.Playback` 读取播放头：

```csharp
foreach (var cursor in frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.AdvanceCenter))
{
    Point2 position = cursor.Position;
    Vector2 direction = cursor.Direction;
}
```

`FinalSceneGeometry` 提供 `TimeUs`、`Tokens`、`Lines`、`Marks`、`Groups`、`Presentations`、`Playback`。这些几何已经合成所有文字效果，适合放置沿文字移动的星光或提示物。`Playback.IsAvailable` 为 `true` 时读取播放头；可用且当前无人演唱时，集合为空。

`PlaybackFrame.Cursors` 返回 `CursorGeometry`：`TrackId`、`Identity`、`TimeUs`、`Progress`、`Path`、`WorldMatrix`、`Mapping?`。调用 `At(mode)` 得到最终 `PlaybackCursor`。`CursorAnchorMode.Baseline` 取基线，`AdvanceCenter` 取字框中线。沿文字布置多个跟随图元时，先用最终 `TokenGeometrySample.ProgressPath.At(progress, mode)` 取得局部点，再用该 token 的 `MapPoint` 映射到画布；每个进度会选择对应的基础字簇映射。这些点都位于当前帧的文字几何上；历史轨迹可从粒子拖尾取得。

`PlaybackCursor` 有 `Position`、`Direction`、`Progress`、`TimeUs`、`AnchorMode`、`TrackId`、`Identity`，并提供 `LineId`、`BindingId`、`TokenId?`、`OccurrenceId?`、`ProgressSource` 快捷属性。并发演唱时保留多个播放头。

`CursorIdentity` 保存真实的 `BranchId`、`LineId`、`BindingId`、`TimeRange`、`ProgressSource`、`TokenId?`、`OccurrenceId?`。行进度播放头以行身份和行时间为依据。几何映射同时用于播放头方向、最终位置与粒子出生点。

## 8. 文字变换与网格变形

### 8.1 选择目标

`TextOutput` 的目标参数统一为 `TextTarget`。下列真实目标都可直接传入，由 SDK 完成转换。

| 目标 | 取得方式 | 可用操作 |
| --- | --- | --- |
| `TokenFamilyTarget` | `token.WithRuby` 或 `geometry.WithRuby` | 仿射、Warp、透明度、两态外观、扫字、图、材质参数 |
| `BaseTokenTarget` | `token.BaseOnly` 或 `geometry.BaseOnly` | 仿射、Warp、透明度、两态外观、扫字、材质参数 |
| `LineTarget` | `line.Target` 或 `load.Target` | 整行仿射、图 |
| `GroupTarget` | 当前可写的真实场景组目标 | 组仿射、组透明度、组图 |
| `TextMarkTarget` | `mark.Target` | 仿射、Warp、两态外观、图、材质参数 |
| `TextClusterTarget` | `cluster.Target` | 字簇仿射、字簇 Warp |

字族同时影响基础字与注音；Base 作用于基础字；字簇用于更细的独立动画。组的写范围由实例绑定及实际父树共同确定，适合在工程绑定或明确的节点绑定上使用。元素绑定授予该元素范围的输出。

### 8.2 仿射与透明度

```csharp
output.AppendTransform(target, Affine2.Translation(0, -20));
output.MultiplyOpacity(target, 0.8);
```

`AppendTransform(TextTarget target, Affine2 localMatrix)` 在目标局部坐标追加矩阵。先写 A、再写 B，结果按 `B * A` 合成。父子层级由宿主统一组合。

`MultiplyOpacity(TextTarget target, double factor)` 的因子位于 `[0,1]`，同一目标的多次写入累乘。字族、Base 与组可使用透明度；组按整体图层合成透明度。

### 8.3 Warp

`SetWarp(TextTarget target, GridWarp? value)` 设置目标的局部网格；`null` 表示关闭该目标的网格。不写该属性时沿用已有结果。同一层先网格、后仿射。

```csharp
var warp = new GridWarp(
    new Rect2(0, 0, 100, 100), 1, 1,
    [new Point2(0, 0), new Point2(100, 10),
     new Point2(0, 100), new Point2(100, 110)]);
output.SetWarp(target, warp);
```

此片段演示网格排列。实际使用时，`domain` 取目标原有局部坐标中的范围，并覆盖字框、基线、墨迹、描边/阴影/辉光外扩及子层变换后的区域。

`GridWarp(domain, columns, rows, destinationVertices)` 每轴 1～32 格，目标顶点按行从左到右排列，共 `(columns+1)*(rows+1)` 个。每格分为左上—右上—右下、左上—右下—左下两个三角形。网格保持正向、非退化且边界简单。构造后的 `Domain`、`SourceVertices` 是后续查询使用的规范坐标。

| 成员 | 用途 |
| --- | --- |
| `Domain`、`Columns`、`Rows` | 源域与网格规模 |
| `SourceVertices`、`DestinationVertices`、`TriangleIndices` | 完整网格 |
| `DestinationBounds`、`IsIdentity` | 目标外框与恒等判断 |
| `MapPoint(point)` | 映射域内点，包含外边界 |
| `MapVectorAt(point, vector)` | 取得指定位置的局部向量变化 |

基础字簇依次经过 Cluster、Base、Family 变换；注音字簇依次经过 Cluster、Family 变换，随后进入行和组的世界空间。最终像素、播放头与粒子锚点共享这条映射。

### 8.4 读取完整映射

`TextGeometryMapping` 按源到父的顺序保存 `Steps`。步骤可为 `new TextGeometryMapping.Affine(matrix)` 或 `new TextGeometryMapping.Grid(warp)`。空步骤数组对应 `TextGeometryMapping.Identity`。

`MapPoint` / `MapVectorAt` 读取完整结果。`MapRectangle(rectangle, budget = null)` 返回经网格分段后的边界顶点；`ProjectRectangle(rectangle, budget = null, cancellationToken = default)` 返回源顶点、目标顶点和三角索引组成的 `TextGeometryProjectionMesh`。结果的 `VertexCount` / `TriangleCount` 可用于自行绘制映射后的内容。`MaximumScale()` 查询映射中的最大缩放估计。

## 9. 填充、描边、阴影、辉光与扫字

### 9.1 两态写入

`TextState` 为 `Unsung`、`Sung`、`Both`。提示标记对应未经过与已经过两态。`Both` 对两态各写一次；同一态、同一成员以后写值为准。

```csharp
output.SetFill(target, TextState.Both,
    MaterialValue.Solid(new ColorRgba(1, 0.7, 0.2, 1)));
output.SetStrokes(target, TextState.Both,
    new StrokeValue(3, MaterialValue.Solid(new ColorRgba(0, 0, 0, 1))));
output.SetShadow(target, TextState.Both,
    new ShadowValue(new ColorRgba(0, 0, 0, 1)));
output.SetGlow(target, TextState.Sung,
    new GlowValue(new ColorRgba(0.2, 0.6, 1, 1), blurRadiusPx: 10));
```

选择 `Unsung` 时，`Sung` 保留其源基线；要统一改变两态，选择 `Both`。

### 9.2 材质与描边

填充和描边的材质来自三个工厂：

| 工厂 | 用途 |
| --- | --- |
| `MaterialValue.Solid(color)` | 纯色；alpha 为 0 时为透明填充 |
| `MaterialValue.Gradient(gradient)` | 渐变 |
| `MaterialValue.PackageExport(handle, parameters)` | 本包材质导出及其完整参数 |

`SetStrokes(target, state, params StrokeValue[] strokes)` 整表替换一态的描边，顺序由内向外，0～4 层。`StrokeValue(widthPx, material, join = StrokeJoin.Round)` 的宽度为正逻辑像素，连接方式可选 `Miter`、`Round`、`Bevel`。包材质用于最内层描边。

`output.SetStrokes(target, TextState.Both)` 设置空描边表。`SetShadow(..., null)` 和 `SetGlow(..., null)` 关闭对应效果。

### 9.3 阴影与辉光默认值

```text
ShadowValue(color, opacity = 1, angleDegrees = 45, distancePx = 4, blurRadiusPx = 8)
GlowValue(color, opacity = 1, blurRadiusPx = 8, spreadPx = 0)
```

不透明度取 `[0,1]`，距离、模糊半径、扩展为非负逻辑像素。阴影角度 0 指向右侧，90 指向下方。辉光从包含描边的轮廓向外扩展；模糊半径采用高斯 sigma。

### 9.4 渐变

```csharp
var gradient = new GradientValue(
    [new GradientStop(0, new ColorRgba(1, 0.2, 0.2, 1)),
     new GradientStop(1, new ColorRgba(0.2, 0.5, 1, 1))],
    angleDegrees: 90,
    coordinateSpace: GradientCoordinateSpace.Glyph);
output.SetFill(target, TextState.Both, MaterialValue.Gradient(gradient));
```

完整构造参数与默认值：

```text
GradientValue(stops, kind = Linear, angleDegrees = 0,
    coordinateSpace = Line, colorSpace = LinearRgb, huePath = Shorter,
    centerX = .5, centerY = .5, radiusX = .5, radiusY = .5)
```

`stops` 至少两项，首尾位置为 0 和 1，中间位置非递减。同位置双点可表达硬边。`kind` 为 `Linear` / `Radial`；径向中心位于 `[0,1]`，半径为正。

| 选项 | 可选值与含义 |
| --- | --- |
| 坐标空间 | `Canvas`：画布；`Line`：整行字体度量框；`Glyph`：每个真实字簇的字体度量框 |
| 插值空间 | `LinearRgb`、`Hsv`、`Oklch` |
| 色相方向 | `Shorter`、`Longer`、`Increasing`、`Decreasing` |
| 线性角度 | 画布顺时针方向，单位度 |

`GradientValue.Bands(colors, boundaries, ...)` 构造硬边分层渐变：n 色配 n−1 个严格递增、位于 `(0,1)` 的分界。它也接受种类、角度、坐标空间、径向中心和半径参数。

### 9.5 扫字

```csharp
output.SetWipe(target, new WipeValue(
    featherRatio: 0.15,
    highlight: new WipeHighlightValue(new ColorRgba(1, 1, 1, 1))));
```

`WipeValue(featherRatio = 0, highlight = null)` 的羽化比例为 `[0,2]` 倍字号。`WipeHighlightValue(color, widthRatio = .25, opacity = 1, blend = Screen)` 的宽度比例为 `(0,2]`，不透明度为 `[0,1]`，混合方式为 `Normal`、`Screen`、`Add`。`WipeValue.Hard` 表示硬边扫字。

扫字写入作用于字族或 Base，两态共享。高亮用于内置纯色与渐变部件。

### 9.6 读取外观基线

`frame.Baseline.Get(...)` 支持 `TokenId`、`TokenFamilyTarget`、`LineId`、`LineTarget`、`TextMarkTarget`，返回 `StyleView`。传 `GroupTarget` 返回 `GroupStyleView`，其字段为 `Opacity`。读取 Base 的源样式时使用对应的字族或 `TokenId`。

`StyleView` 包含 `Unsung`、`Sung`、`Opacity`、`Wipe`。每态的 `AppearanceView` 含 `Fill`、`Strokes`、`InnermostStroke?`、`Shadow?`、`Glow?`。

`Material(slot)` 读取指定填充或最内层描边材质；`RequireSungSolidColor()` 适用于已知使用已唱纯色的效果。可通过 `MaterialValue.SolidMaterial`、`GradientMaterial`、`PackageMaterial` 模式匹配读取材质内容。

`AppearanceUpdate` / `StyleUpdate` 可用于在自己的只读值中组织外观修改：`Then` 按顺序组合，`StyleUpdate.Both` 设置两态，`Apply` 应用到基线，`ApplyMaterials` 应用材质部分。提交帧结果使用 `TextOutput` 的对应写入方法。

## 10. 图绑定与材质参数

### 10.1 在脚本中绑定图

```csharp
output.BindGraph(target, new GraphBindingValue(
    frame.Resources.GetGraph("glitch"),
    ParameterValues.Empty,
    GraphUniformValues.Empty,
    GraphTransformValues.Empty));
```

此例适用于参数表为空的 `glitch` 图导出。完整构造器为 `GraphBindingValue(graph, parameters, uniforms, transforms)`；每次调用整体替换该目标的图描述。

`GraphUniformValues` 接受以 `(nodeId, name)` 为键的 `UniformValue` 集合；`GraphTransformValues` 接受以 Transform 节点 ID 为键的 `GraphTransformValue(matrix, outputBounds)` 集合。输出框使用图自身的坐标空间。

字族、行、标记使用 `image.graph`；组同时使用 `image.graph` 和 `image.graph.group`，并由工程或明确的节点绑定授予组范围。controller 与被引用图导出都声明对应的 `ImageGraph` 目标。当前脚本图使用 `ColorOnly`，其变换改变图像外观，文字几何与播放头继续由文字变换控制。

### 10.2 逐帧覆盖材质 uniform

```text
SetMaterialUniform(target, slot, material, nodeId, name, value)
```

例如对已配置的 `ink` 材质、`paint` 节点写入强度：

```csharp
output.SetMaterialUniform(target, MaterialSlot.SungFill,
    frame.Resources.GetMaterial("ink"), "paint", "amount", UniformValue.Float(0.8));
```

`MaterialSlot` 为 `UnsungFill`、`SungFill`、`UnsungStroke`、`SungStroke`。描边槽指该态最内层材质。目标可为字族、Base 或标记。

材质句柄、节点名、uniform 名、值类型和数组长度与实际材质定义对应。同名 uniform 后写覆盖；完整填充或描边材质替换会重建该槽的覆盖范围。覆盖属于当前帧。

### 10.3 `UniformValue`

| 工厂 | 参数 |
| --- | --- |
| `Float` | 一个有限 `double` |
| `Float2` / `Float3` / `Float4` | 2 / 3 / 4 个原始数据分量 |
| `Int` | 一个 `int` |
| `Color` | `ColorRgba`，带颜色语义 |
| `Matrix3x3` | `m11,m12,m13,m21,m22,m23,m31,m32,m33`，按行给出 |
| `Array` | 非空、同类型的 `UniformValue` 集合 |

`Color` 与 `Float4` 分别匹配颜色声明和原始向量声明。数组与单值分别匹配各自的声明，长度为 1 的数组也保留数组类型。公开读取成员为 `Kind`、`ArrayLength?`、`Numbers`、`Integers`。

## 11. 行退场

在包中设置 `lineExit.durationUs`，并在非零寿命时声明 `line.exit`。行隐藏后，宿主在 `[hide, hide + duration)` 保留退出副本；寿命范围为 0～3,000,000 微秒。

`frame.LineLoads` 的每个 `TextLineLoad` 包含 `Line`、`Target`、`Phase`、`HideTimeUs?`、`ExitDurationUs`、`ExitProgress`。

| `Phase` | 几何与时钟 |
| --- | --- |
| `Active` | 正常场景装载，使用源布局 |
| `ExitReplica` | 取隐藏前最后一微秒的布局、父链和外观基线，使用本实例的退出进度 |

退出进度位于 `[0,1]`。多个效果使用不同寿命时，各实例按自己的寿命读进度；其他实例继续保留副本期间，已结束的实例读到 1。新句按正常布局进入，旧句同时完成退场。

退出副本沿用原装载的 `BindingId`，源演唱进度仍按当前时刻读取。同源行重新出现时会有新的装载身份。按 `BindingId` 匹配 token 与行装载，可以分别处理旧副本和新装载。

下面片段放入文字阶段，可让退出行中的字族淡出：

```csharp
foreach (var load in frame.LineLoads)
{
    if (load.Phase != TextLinePhase.ExitReplica) continue;
    foreach (var geometry in frame.Geometry)
        if (geometry.WithRuby.BindingId == load.Line.BindingId &&
            geometry.BaseGeometry.CanTransformIndependently)
            output.MultiplyOpacity(geometry.WithRuby, 1 - load.ExitProgress);
}
```

这个写法使用 `line.exit` 和 `token.opacity`，并声明对应的字族 `StyleModulation` 输出。退场几何进入文字与晚期阶段；源索引保留源场景的正常装载与演唱语义。

## 12. 时间线与源索引

`source.Indexes` 与 `frame.Indexes` 包含 `Timeline`、`Presentations`、`Layout`、`Cursors`。`source.Timeline` / `frame.Timeline` 是同一个时间线索引。

### 12.1 读取范围

实例配置的读取范围可为绑定关联源、整个工程或某个分支，分别对应 `SourceSubscription.BoundSources`、`Project`、`Branch`。查询再用 `SourceFilter.Project`、`Branch(id)`、`Lines(ids)`、`Tokens(ids)` 筛选；实际结果取固定订阅与筛选的交集。

源目录提供完整固定内容，源索引提供订阅范围中的时间查询，输出绑定决定可写目标。行/元素筛选数组使用非空、有效且互不重复的源身份。

### 12.2 时间线选择器

`new TimelineSelector(kind, scope = null, sourceText = null)` 指定事件种类、范围和可选的源文本。默认范围为 `TimelineScope.Project`。

范围类型为 `Project`、`Branch(BranchId)`、`Line(LineId)`、`Token(TokenId)`、`Occurrence(OccurrenceId)`、`Binding(BindingId)`、`Node(NodeId)`、`Cue(CueId)`。

| 查询 | 返回规则 |
| --- | --- |
| `Events` | 固定、稳定排序的全部订阅事件 |
| `Last(selector, timeUs)` | 最后一个 `TimeUs <= timeUs` 的事件；没有时为 `null` |
| `Next(selector, timeUs)` | 第一个 `TimeUs > timeUs` 的事件；没有时为 `null` |
| `Count(selector)` | 包括未来事件的总数 |
| `Count(selector, timeUs)` | 截至该时刻，包含边界的数量 |
| `Count(selector, range)` | 半开区间内的数量 |
| `Nth(selector, zeroBasedIndex)` | 从 0 开始的第 n 项；下标为非负整数，达到总数后返回 `null` |
| `Ordinal(selector, eventId)` | 从 1 开始的事件序号；未命中时为 `null` |
| `Ordinal(selector, occurrenceId)` | 从 1 开始的真实演唱序号；未命中时为 `null` |
| `GetRange(selector, range)` | 半开范围内的有序事件 |

`sourceText` 用于逐字或整行演唱事件，按源文本做精确 ordinal 比较。例如，可统计相同歌词的重复演唱次数。

### 12.3 事件种类与载荷

所有 `TimelineEvent` 有 `Kind`、`TimeUs`、`EventId`、`SourceOrder`、`ReplayReason`、`BoundaryPriority`。索引中的事件以固定源身份去重，保存规范的 `ReplayReason.Initial` 视图。

| `TimelineEventKind` / 对应具体类型 | 可读取的载荷 |
| --- | --- |
| `TokenStarted`、`TokenCompleted` | `Source`、`TokenId`、`LineId`、`BranchId`、`OccurrenceId` |
| `LinePerformanceStarted`、`LinePerformanceCompleted` | `Source`、`LineId`、`BranchId`、`PerformanceRange`、`ProgressSource` |
| `BindingEntered`、`BindingLeft` | `Binding`，即真实 `FrameLine` |
| `ElementVisible`、`ElementHidden` | `Element`，即歌词、标记或组的场景身份 |
| `LeadInStarted`、`LeadInStepChanged`、`LeadInCompleted` | `Target`、`ElapsedMarks`、`CueId`；`Target` 有 `Cue`、`Target` 行装载、`MarkCount` |
| `CursorTransition`（类型 `CursorTransitionEvent`） | `TrackId`、`From`、`To`、`GapUs`、`TransitionReason` |
| `AudioFeatureTick` | `AnalysisRevision`、真实分析点 `Point` |

`SceneElementIdentity` 有 `NodeId`，其分支为 `Lyric`、`Mark`、`Group`。光标转换原因可为 `TokenChange`、`LineChange`、`BindingChange`。

同一微秒先完成/隐藏，再进入/准备/音频，再起唱，最后光标转换；各组内按源分支、行、元素顺序和事件身份排序。准备显示、重新装载、注音和退出副本分别保留展示语义，源起唱统计依据真实演唱事件。

### 12.4 源装载

```text
Presentations.Get(BindingId) → SourcePresentationLoad?
Presentations.GetAt(timeUs, filter) → ImmutableArray<SourcePresentationSample>
Presentations.GetRange(range, filter) → ImmutableArray<SourcePresentationLoad>
```

`SourcePresentationLoad` 有 `Binding`、`RevealTimeUs`、`HideTimeUs?`、`LoadOrdinal`（从 0 开始）、`PreparationRange?`、`LayoutExitRange?` 和 `Contains(timeUs)`。可见范围为 `[reveal, hide)`；尚无真实隐藏边界时 `HideTimeUs` 保持 `null`。

`SourcePresentationSample` 有 `Load`、`TimeUs`、`DisplayPhase`、`LayoutExitProgress`。这里的布局退出发生在隐藏前；第 11 节的副本退出从隐藏后开始。

`GetRange` 返回与查询窗相交的完整装载。

### 12.5 基础布局与基础播放头

```text
Layout.GetBaseAt(timeUs, filter) → BaseSceneGeometry
Cursors.GetSegments(trackId, range) → ImmutableArray<SourceCursorSegment>
Cursors.GetBaseAt(timeUs, filter, anchorMode) → ImmutableArray<PlaybackCursor>
```

`BaseSceneGeometry` 有 `TimeUs`、`Lines`、`Tokens`、`Marks`、`Groups`、`LineGeometry`、`MarkGeometry`、`GroupGeometry`，来自该时刻的源基础布局。可用于提前查询未来的源位置。

`SourceCursorSegment` 有 `TrackId`、`Identity`、`ActiveRange`。有效范围为真实演唱区间与展示可见窗的交集，查询返回相交的完整区段；播放进度仍按 `Identity.TimeRange` 计算。

`GetBaseAt` 返回当前正在演唱的全部真实播放头。晚期阶段需要跟随文字效果时，使用 `FinalGeometry.Playback`。

装载、布局与播放头查询的时刻取非负微秒，范围使用有效的 `TimeRange`，筛选身份取自当前 `Content` 目录。有效轨道在查询范围内无区段时返回空数组。

装载、布局与播放头查询在当前阶段使用，受会话有效期和宿主取消控制。`TimelineIndex` 本身是独立的固定事件值。

## 13. 粒子计划与粒子数据

粒子在 `Declare` 中描述出生计划。宿主根据目标时刻解析位置、年龄外观和拖尾，适合任意时刻预览与导出。

### 13.1 连续发射完整例子

下面入口使用本包名为 `trail` 的连续发射器，按每秒 40 颗沿播放头发射圆点：

```csharp
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record CursorDotsData : PreparedEffect;

public sealed class CursorDots : IPureKaraokeEffect<CursorDotsData>
{
    public CursorDotsData Declare(DeclareContext source)
    {
        var range = new TimeRange(0, source.ProjectDurationUs ?? 60_000_000);
        var particle = new ParticleSpec
        {
            LifetimeUs = 600_000,
            SizePx = 6,
            Color = new ColorRgba(1, 0.7, 0.2, 1),
            SeedStreamKey = "trail",
            SpreadPx = 2,
            Velocity = new Vector2(0, -20),
            Motion = new ParticleMotion
            {
                Opacity = new ParticleAgeCurve(1, 0, Curves.LinearCurve)
            }
        };
        return new CursorDotsData
        {
            Particles = new ParticleSchedule(
                [new CursorEmissionPlan(new EmitterId("trail"),
                    new RateTrack([new RateSegment(range, 40, 40)], [range]),
                    particle, CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump)],
                [])
        };
    }

    public void Evaluate(in TextFrame frame, CursorDotsData data, TextOutput output) { }
}
```

清单中的对应部分如下；其余字段按快速开始填写，入口改为 `CursorDots`，参数表为空：

```json
{
  "requires": ["particles.continuous", "visuals.dots"],
  "emitters": [{"id":"trail","kind":"cursor","maxLifetimeUs":600000}],
  "outputs": [{"id":"trail","kind":"Overlay","target":"canvas","requires":["particles.continuous","visuals.dots"],"dependsOn":[]}]
}
```

其中 `requires`、`emitters` 位于清单根，`outputs` 位于 controller 导出内。此示例对未设成片时长的工程选择 60 秒发射窗；可改为适合效果的明确范围。

### 13.2 计划类型

| 类型 | 构造参数 |
| --- | --- |
| `ParticleSchedule` | `Continuous`、`Bursts` 两个不可变数组 |
| `CursorEmissionPlan` | `Id`、`Rate`、`Particle`、`AnchorMode`、`Bridge` |
| `RateTrack` | `Segments`、`EnabledRanges` |
| `RateSegment` | `Range`、`StartPerSecond`、`EndPerSecond` |
| `EventBurstPlan` | `Id`、`Selector`、`Rows` |
| `BurstEventRow` | `EventId`、`Count`、`Anchor`、`Particle` |

速率段与启用窗按开始时间排序、互不重叠，相接合法。速率是歌曲时间上的颗数/秒；起止速率不同则线性变化。启用窗内零速率保留累积余量，停用或真实播放头空档会重开累计。相接且属于同一轨道、同一装载的字可以连续发射。

首颗粒子在累积到 1 颗的首个整数微秒出生。每个计划的 `EmitterId` 与清单的发射器种类、最大寿命及同名画布输出对应。

### 13.3 Bridge 与事件爆发

`BridgePolicy.Jump` 表示播放头间直接切换。`new BridgePolicy(TransitionMode.Bridge, durationUs, gapLimitUs, curve)` 为真实后继之间的正时间空档加入飞行路径；空档位于 `gapLimitUs` 内时，在空档末段安排至多 `durationUs` 的飞行，各飞行独立累计粒子。

Burst 在准备阶段查询真实时间线事件，建立 `BurstEventRow`。`Count` 为非负整数。锚点可选：

| 锚点 | 位置 |
| --- | --- |
| `new BurstAnchor.SourceToken(tokenId)` | 对应真实字族的 Advance 中心 |
| `new BurstAnchor.SourceLine(lineId)` | 对应真实行的 Advance 中心 |
| `new BurstAnchor.TextMark(nodeId)` | 对应提示标记的 Advance 中心 |
| `new BurstAnchor.BindingCursors(mode)` | 绑定内全部当前播放头 |

一个计划内每个真实 `EventId` 使用一行；锚点按照该真实事件的时刻解析。同发射器各行使用一致的图层和绘制模式。

### 13.4 `ParticleSpec` 与默认值

| 成员 | 默认 / 必填 | 含义 |
| --- | --- | --- |
| `LifetimeUs` | 必填 | 正微秒，至多 5,000,000，位于发射器最大寿命内 |
| `SizePx` | 必填 | 正逻辑像素，表示直径 |
| `Color` | 必填 | sRGB RGBA |
| `SeedStreamKey` | 必填 | 随机分流名称 |
| `Velocity` | `(0,0)` | 世界空间 px/s |
| `SpreadPx` | `0` | 出生位置 X/Y 散布的半宽，非负 |
| `TrailDurationUs` | `0` | 拖尾时间；0 为关闭 |
| `Layer` | `AboveText` | 背景与文字之间的 `BehindText`，或文字上方的 `AboveText` |
| `Rendering` | `Dots` | 宿主圆点，或 `Custom` 自定义绘制 |
| `Motion` | `ParticleMotion.ConstantVelocity` | 加速度与年龄曲线 |

`ParticleMotion` 有 `Acceleration`（默认零，px/s²）、`Opacity` 和 `Size`（默认 `ParticleAgeCurve.ConstantOne`）。`ParticleAgeCurve(start, end, curve)` 接受 `[0,1]` 年龄，Opacity 是颜色 alpha 的乘数，Size 是直径乘数。年龄曲线按原值求值；编写时使最终不透明度位于 `[0,1]`，最终尺寸为非负数。

出生位置来自出生时刻的最终文字几何。此后的世界位置按出生位置、初速度和恒加速度计算：`p = birthPosition + velocity*s + acceleration*s*s/2`。

### 13.5 身份、出生窗与存活粒子

晚期的 `frame.Particles` 提供：

| 成员 | 内容 |
| --- | --- |
| `Alive` | 当前存活的 `ParticleView` 数组，满足 `BirthUs <= t < DeathUs` |
| `Births` | `ParticleBirthWindow`；含 `Window` 和完整 `Births` 数组 |
| `GetTrail(id)` | 当前实例、当前存活粒子的解析拖尾点 |

出生窗为闭区间 `[max(0,t−5_000_000), t]`，含窗内已死亡粒子的出生记录。文字离场后，已出生粒子继续存活至自己的 `DeathUs`。

`ParticleBirth` 保存 `Id`、`Source`、`EventId?`、`BirthUs`、`DeathUs`、`BirthPosition`、`InitialVelocity`、`Spec`。`ParticleView` 另提供当前 `Position`、`SizePx`、`Color`，以及便捷的出生/死亡时刻、图层、来源、绘制模式。

稳定身份为 `ParticleIdentity(InstanceId, EmitterId, Origin)`。连续来源保存 Head、episode 起点、从 1 开始的 `SequenceStartingAt1`；Burst 来源保存真实事件、计划序号、锚点、粒子序号。`ParticleSource` 可为 `Cursor`、`Bridge`、`Token`、`Line`、`TextMark`，共同提供真实 `BindingId`，播放头来源另带轨道。

`TrailDurationUs` 为 0 时，`GetTrail` 返回空数组。启用后的拖尾为 `ParticleTrailSample(TimeUs, Position)` 数组，包括裁剪起点、歌曲绝对 120 Hz 网格点和当前点。刚出生时可能只有一个点；绘制 `Trail` 时使用至少两个点。

### 13.6 最终锚点的采样策略

默认 `GeometrySamplingPolicy.Exact` 在所需时刻精确计算文字几何。效果在明确时间范围内连续时，可返回：

```csharp
new GeometrySamplingPolicy(
    GeometrySamplingMode.Continuous120Hz,
    [new TimeRange(0, 10_000_000)],
    [2_000_000, 5_000_000]);
```

`ContinuousRanges` 声明连续区间，`DiscontinuitiesUs` 列出不连续边界。宿主结合相关文字实例、源布局和字簇边界，选择满足全部连续性条件的采样；其余时刻精确求值。声明范围涵盖该实例实际输出的全部文字属性。

## 14. 晚期绘制

### 14.1 入口与帧

`IPureVisualEffect<TPrepared>` 的 `EvaluateVisuals` 使用文字阶段之后的最终几何和本实例粒子。

`VisualFrame` 有 `Source`、`TimeUs`、`Binding`、`OutputMode`、`FinalGeometry`、`Particles`、`Media`，以及快捷属性 `Parameters`、`Audio`、`Resources`、`Random`、`Timeline`、`Indexes`、`CanvasSize`、`ProjectDurationUs`。

下面是完整的手动绘制入口，把一个圆点放在每个最终播放头上：

```csharp
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record CursorDecorationData : PreparedEffect;

public sealed class CursorDecoration : IPureVisualEffect<CursorDecorationData>
{
    public CursorDecorationData Declare(DeclareContext source)
        => new()
        {
            VisualOutputs = [new VisualOutputDeclaration(
                new OutputId("heads"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)]
        };

    public void Evaluate(in TextFrame frame, CursorDecorationData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, CursorDecorationData data, VisualOutput output)
    {
        if (!frame.FinalGeometry.Playback.IsAvailable) return;
        var dots = ImmutableArray.CreateBuilder<Dot>();
        foreach (var cursor in frame.FinalGeometry.Playback.GetCursors(CursorAnchorMode.AdvanceCenter))
            dots.Add(new Dot(cursor.Position, 4, new ColorRgba(1, 1, 1, 1)));
        output.DrawDots(new OutputId("heads"), dots.ToImmutable(), OverlayLayer.AboveText);
    }
}
```

对应清单使用 `requires: ["visuals.dots"]`，入口 `CursorDecoration`，并在该导出的 `outputs` 数组中加入：

```json
{"id":"heads","kind":"Overlay","target":"canvas","requires":["visuals.dots"],"dependsOn":[]}
```

### 14.2 图元与方法

方法统一形状为 `DrawX(OutputId id, ImmutableArray<T> items, OverlayLayer layer)`。

| 方法 / 数组元素 | 构造参数 | 能力 |
| --- | --- | --- |
| `DrawDots` / `Dot` | `position, radius, color` | `visuals.dots` |
| `DrawSegments` / `Segment` | `start, end, width, color` | `visuals.segments` |
| `DrawTrails` / `Trail` | `ImmutableArray<TrailPoint>`；每点 `position, width, color` | `visuals.trails` |
| `DrawSprites` / `Sprite` | `image, unitToWorld, tint, source = null` | `visuals.sprites` |
| `DrawPaths` / `VectorVisual` | `vector, unitToWorld, tint` | `visuals.paths`、`resources.svg.vector` |
| `DrawAtlasSprites` / `AtlasSprite` | `sprite, unitToWorld, tint` | `visuals.sprites`、`resources.svg.atlas` |

圆点半径为正，线段宽度为正且使用平端。拖尾至少两个点，每点宽度非负，可用零宽收尖。粒子的 `SizePx` 是直径，绘制 `Dot` 时常用 `SizePx / 2`。

图元坐标位于最终画布空间。图片、矢量和图集通过可逆 `unitToWorld` 仿射定位。图片的 `source` 为原图 `[0,1]²` 内的非空裁片，省略时为全图；裁片先映射为单位方形，再应用矩阵。SVG viewport 也按单位方形映射。白色 tint 保留资源原色。

### 14.3 声明与批次

`VisualOutputDeclaration(Id, Primitive, Layer, Resource = None)` 的组合如下：

| 绘制内容 | `Primitive` | `Resource` |
| --- | --- | --- |
| 圆点 / 线段 / 拖尾 | `Dots` / `Segments` / `Trails` | `None` |
| 图片精灵 | `Sprites` | `None` |
| SVG 矢量 | `Paths` | `SvgVector` |
| SVG 图集 | `Sprites` | `SvgAtlas` |

清单以相同 ID 声明 `Overlay` / `canvas` 输出和所需能力；准备声明与绘制调用还应匹配图元类型、图层和资源类型。每次晚期调用中，每个输出 ID 至多提交一个完整批次；先在局部集合汇总图元，再调用一次 `DrawX`。空数组是合法空批次。批次按输出 ID 的 ordinal 顺序排列，批次内部保留图元顺序。

发射器 ID 保留给其粒子输出。`Rendering = Custom` 时，手动绘制使用另一个具名输出，并在 `VisualOutputs` 中声明。

`BehindText` 位于背景之后、文字之前；`AboveText` 位于文字、注音和提示之后。

## 15. 图片与 SVG 资源

### 15.1 资源目录

`source.Resources` 或 `frame.Resources` 提供以下方法：

| 方法 | 参数所指对象 |
| --- | --- |
| `GetImage(assetId)` | `role: image` 的包内图片路径 |
| `GetVector(resourceId)` | vector SVG 描述中的资源 ID |
| `GetSprite(resourceId, variantId)` | atlas SVG 的资源 ID 与尺寸档 ID |
| `GetGraph(exportId)` | graph 导出 ID |
| `GetMaterial(exportId)` | material 导出 ID |
| `Contains(handle)` | 检查以上各类句柄是否属于当前目录 |

图片参数也可通过 `Parameters.GetAsset(id)` 取得。句柄固定到包修订，可保存在准备数据中。目录的 `PackageId`、`Revision` 和句柄的 `Identity` 可用于识别资源来源；`SpriteHandle` 另有 `VariantId`。

### 15.2 SVG 矢量

在 `files` 中列出 SVG 为 `svg`，描述 JSON 为 `svgResource`。例如 `resources/star.json`：

```json
{
  "svgResourceVersion": 1,
  "id": "star",
  "source": "assets/star.svg",
  "profile": "mako-svg-static-1",
  "mode": "vector",
  "viewport": {"width": 100, "height": 100},
  "fonts": [],
  "images": []
}
```

SVG 可使用静态路径、基础形状、组、符号引用、渐变、直接形状裁切和所支持的静态滤镜。文字可在制作素材时转为路径。`fonts` 写 `[]`；图片通过同包 `image` 资源白名单或合规的内嵌 PNG/JPEG 使用。

`images` 列出 SVG 引用的包内图片路径。SVG 的尺寸与描述的 viewport 对应；每份 SVG 源文件均由描述文件覆盖。矢量资源声明能力 `resources.svg.vector`，通过 `GetVector("star")` 取得。

静态元素包括：`svg`、`g`、`defs`、`symbol`、`use`、`path`、`rect`、`circle`、`ellipse`、`line`、`polyline`、`polygon`、`linearGradient`、`radialGradient`、`stop`、`clipPath`、`image`、`filter`、`feGaussianBlur`、`feOffset`、`feColorMatrix`、`feMerge`、`feMergeNode`、`style`、`title`、`desc`、`metadata`。CSS 使用单个元素名、ID 或 class 选择器；滤镜显式给出有限区域和 units。

### 15.3 SVG 图集

将描述改为 `mode: "atlas"`，加入：

```json
{
  "atlas": {
    "variants": [
      {"id": "small", "width": 64, "height": 64},
      {"id": "large", "width": 128, "height": 128}
    ],
    "paddingPx": 2,
    "mipmaps": true
  }
}
```

`atlas` 是描述根的一个字段。使用 1～8 个尺寸档，padding 为 1～64 像素，包含双侧 padding 后每边至多 4096 像素。显式启用 mipmaps。图集资源声明 `resources.svg.atlas`，用 `GetSprite("star", "small")` 取得。

vector 描述保留基础字段；atlas 描述在相同基础字段上加入 `atlas`。资源 ID、尺寸档 ID 使用 1～128 个 ASCII 字母、数字及 `._-`，首字符为字母或数字。

## 16. 图、shader 与材质包格式

图可以处理文字图像、区域覆盖和背景，也可以直接生成填充或描边材质。一个最小材质包由清单、图 JSON、程序 JSON 和 SkSL 组成。

### 16.1 完整的材质文件组合

在包参数表中定义：

```json
{"id":"amount","type":"number","label":"强度","default":0.5,"min":0,"max":1}
```

在 `exports` 中加入：

```json
{
  "id":"ink",
  "kind":"material",
  "name":"流动墨色",
  "graph":"graphs/ink.json",
  "parameters":["amount"],
  "regions":["fill","stroke"],
  "coverageMode":"unmasked",
  "fallback":{"kind":"solid","color":"#3380CCFF"},
  "outputs":[{
    "id":"color","kind":"ImageGraph","target":"region",
    "requires":[],"dependsOn":[],
    "bypass":{"kind":"materialFallback","mapping":"identity"}
  }]
}
```

清单的 `files` 分别登记 `graphs/ink.json` 为 `graph`、`shaders/ink.program.json` 为 `shaderProgram`、`shaders/ink.sksl` 为 `shader`，按第 1 节更新摘要。其余根字段沿用包格式，`emitters` 为空、`lineExit.durationUs` 为 0。

`graphs/ink.json`：

```json
{
  "graphVersion":1,
  "id":"ink",
  "inputs":[],
  "parameters":["amount"],
  "nodes":[{
    "id":"paint",
    "op":"RuntimeShader",
    "program":"shaders/ink.program.json",
    "children":{},
    "uniforms":{"amount":"parameter:amount","timeSeconds":"host:timeSeconds"},
    "outputType":"color",
    "bounds":{"expandPx":0,"maxExpandPx":0,"maxSampleOffsetPx":0},
    "edge":"transparent"
  }],
  "output":"node:paint",
  "anchorMode":"ColorOnly",
  "bounds":{"space":"canvas","source":"targetRegion","maxExpandPx":0},
  "composite":{"mode":"sourceOver"}
}
```

`shaders/ink.program.json`：

```json
{
  "programVersion":1,
  "source":"shaders/ink.sksl",
  "profile":"skia-raster-3.119.4-v1",
  "uniforms":[
    {"name":"amount","type":"float"},
    {"name":"timeSeconds","type":"float"}
  ],
  "children":[]
}
```

`shaders/ink.sksl`：

```sksl
uniform float amount;
uniform float timeSeconds;
half4 main(float2 p) {
    float wave = 0.5 + 0.5 * sin(p.x * 0.04 + timeSeconds * 3.0);
    float3 rgb = mix(float3(0.2, 0.5, 0.8), float3(1.0, 0.8, 0.3), wave * amount);
    return half4(rgb, 1.0);
}
```

包级 `shaderProfile` 使用 `skia-ganesh-3.119.4-v2`，程序 JSON 的 `profile` 使用 `skia-raster-3.119.4-v1`，分别填写上述值。SkSL 颜色输出使用已乘 alpha 的 RGBA；半透明输出写成 `half4(rgb * alpha, alpha)`。

`coverageMode: "masked"` 让结果受到目标区域覆盖约束，`"unmasked"` 按材质输出参与合成。

### 16.2 图字段与节点

把上一节图用于独立的整行图效果时，可在 `exports` 加入以下对象，并在包级 `requires` 加入 `image.graph`：

```json
{
  "id":"ink-graph",
  "kind":"graph",
  "name":"整行流动墨色",
  "graph":"graphs/ink.json",
  "parameters":["amount"],
  "bindingKinds":["project","branch","lines","clips","node"],
  "outputs":[{
    "id":"image","kind":"ImageGraph","target":"line",
    "requires":[],"dependsOn":[]
  }]
}
```

图使用的全部参数 ID 同时列入该导出的 `parameters`。

图根的必需字段为：`graphVersion`、`id`、`inputs`、`parameters`、`nodes`、`output`、`anchorMode`、`bounds`、`composite`。按需要加入 `bypassInput`。当前文字图与组图使用 `anchorMode: "ColorOnly"`。

`output` 和节点输入使用 `input:<id>` / `node:<id>` 引用。图内节点构成有向无环关系。图的 `parameters` 与导出的参数引用共同覆盖实际使用的参数。

| 节点 `op` | 专用字段 |
| --- | --- |
| `RuntimeShader` | `program`、`children`、`uniforms`、`outputType`（`color` / `coverage`） |
| `Blur` | `input`、`sigmaPx`（非负数值或 `parameter:<numberId>`） |
| `Mask` | `color`、`coverage` |
| `Transform` | `input`、`matrix: [a,b,c,d,tx,ty]`、`outputBounds: [left,top,right,bottom]` |
| `Blend` | `foreground`、`background`、`mode`（`sourceOver` / `plus` / `multiply`） |

所有节点还包括 `id`、`bounds`、`edge`。`edge` 取 `transparent`、`clamp`、`repeat`。节点边界完整格式：

```json
{"expandPx":0,"maxExpandPx":0,"maxSampleOffsetPx":0}
```

`expandPx` 也可写为 `"ceil(3 * parameter:blurPx)"` 这一形式，系数为非负数；`maxExpandPx` 声明该节点的最大外扩，`maxSampleOffsetPx` 声明最大采样偏移。

图根 `bounds` 为 `{"space":"canvas","source":"targetRegion","maxExpandPx":0}`；空间可取 `token`、`line`、`node`、`canvas`，与输入的 `space` 一致。Transform 的 `outputBounds` 与 C# `Rect2` 均使用左、上、右、下四条边的位置。

### 16.3 图输入

| `source` | `type` | 内容 |
| --- | --- | --- |
| `TargetColor` | `color` | 捕获目标图像 |
| `GlyphCoverage`、`StrokeCoverage`、`SungCoverage`、`TargetRegionCoverage` | `coverage` | 字形、描边、已唱部分或目标区域覆盖 |
| `BackgroundMedia` | `color` | 背景媒体 |
| `BackdropBelow` | `color` | 实际下层合成结果 |
| `BackgroundColor` | `color` | 工程底色 |
| `ResourceImage` | `color` | 包图片，另写 `resource` 路径 |

目标图像输入的完整例子：

```json
{
  "id":"original",
  "source":"TargetColor",
  "type":"color",
  "availability":"required",
  "backgroundRequirement":"none",
  "space":"canvas",
  "capture":{
    "includeRuby":true,
    "includeStroke":true,
    "includeShadow":true,
    "includeGlow":true,
    "includeDescendantGroups":false,
    "includeOwnedOverlay":false,
    "region":"fillAndStroke"
  }
}
```

每个输入都填写 `availability`（`required` / `optional`）、`backgroundRequirement`（`none` / `media` / `opaqueCanvas`）及 `space`。背景来源按实际需要选择背景要求；`BackgroundMedia` 使用 `media` 或 `opaqueCanvas`。

`TargetColor` 和各类 coverage 输入填写 `capture`。其中 `includeGlow` 省略时为 `true`，其余列出的捕获字段均填写。`region` 可为 `fill`、`stroke`、`fillAndStroke`、`materialRegion`。

可根据枚举参数选择来源，例如 `"source":"parameter:mode"`，配合 `"choices":{"media":"BackgroundMedia","below":"BackdropBelow"}`；`choices` 覆盖该枚举的全部选项，只把选项映射到来源名。任一候选需要 `capture` 或 `resource` 时，在这个输入对象中填写一份共享的对应字段。

### 16.4 程序、uniform 与 child

程序 JSON 有 `programVersion: 1`、`source`、`profile`、`uniforms`、`children` 五个根字段。uniform 名和 child 名共用唯一名字集合，使用 ASCII 标识符。每个 uniform 写 `name`、`type`，定长数组另写正整数 `arrayLength`。

| uniform 类型 | 图中可用绑定 |
| --- | --- |
| `float` | 数值、`parameter:<numberId>`、`host:timeSeconds` |
| `int` | Int32 整数、范围位于 Int32 内的 integer 参数、boolean 参数 |
| `color` | `{r,g,b,a}`、color 参数 |
| `float2` | 二数数组、`host:canvasSize` |
| `float3` / `float4` | 三数 / 四数数组 |
| `matrix3x3` | 按行排列的九数数组 |
| 定长数组 | 完整的字面量值；向量和矩阵数组按标量展开，颜色数组使用颜色对象 |

程序和 SkSL 按名称、类型、数组形状及长度对应。颜色写作 `layout(color) uniform float4 name;` 或相应 `half4`。child 在程序中声明 `{"name":"image","type":"color"}` 或 `coverage`，在图节点的 `children` 中绑定对应表面引用，SkSL 使用 `uniform shader image;` 读取。

### 16.5 合成、旁路与 fallback

常用合成为 `{"mode":"sourceOver"}`。需要替换被覆盖的下层时用 `{"mode":"replaceCoveredBackdrop","coverage":"input:coverage"}`，指定 coverage 表面。

图效果存在活动背景依赖时，给图根设置 `bypassInput: "input:original"`，并给导出输出设置：

```json
{"kind":"graphInput","input":"input:original","mapping":"identity"}
```

这里的 `original` 是 `required`、`backgroundRequirement: "none"` 的直接 `TargetColor` 输入。材质输出使用 `{"kind":"materialFallback","mapping":"identity"}`。

不透明输出中，必需背景媒体缺席时，图采用原图旁路，材质采用 `fallback`；可选背景媒体输入使用工程底色。透明输出中，存在活动背景依赖时，图采用原图旁路，材质采用 `fallback`，这一规则也适用于 optional 输入。预览、测量和导出按同一规则记录对应诊断。

材质 fallback 纯色格式为 `{"kind":"solid","color":"#RRGGBBAA"}`。渐变格式如下，颜色使用八位十六进制 RGBA：

```json
{
  "kind":"gradient",
  "gradient":{
    "kind":"Linear","angleDegrees":0,
    "centerX":0.5,"centerY":0.5,"radiusX":0.5,"radiusY":0.5,
    "coordinateSpace":"Line","colorSpace":"LinearRgb","huePath":"Shorter",
    "stops":[{"offset":0,"color":"#FF0000FF"},{"offset":1,"color":"#0000FFFF"}]
  }
}
```

渐变选项与第 9 节一致，JSON 枚举按示例大小写填写。

## 17. 音频与背景媒体特征

### 17.1 音频

`source.Audio` 与 `frame.Audio` 提供工程固定分析结果。

| 成员 | 内容 |
| --- | --- |
| `Availability` | 整份分析的状态 |
| `Revision?`、`DurationUs?` | 分析身份与真实音频时长 |
| `GetAt(timeUs)` | `AudioFeatureSample`，取该时刻左侧最近的真实分析点并保持其值 |
| `GetRange(range)` | `AudioFeatureRange`，返回时间戳落在半开范围内的真实分析点 |

状态为 `Available`、`NoAudio`、`NotAnalyzed`、`AnalysisFailed`、`OutsideRange`。`Available` 的采样带 `Values`；已分析静音为有效零值。

`AudioFeatureValues` 包含：`BassLevel`、`Pulse`、`BeatConfidence`（均 `[0,1]`），以及可空的 `BeatPhase`（`[0,1)`）。节拍相位尚无估计时为 `null`，置信度为 0。

```csharp
var sample = frame.Audio.GetAt(frame.TimeUs);
if (sample.Availability == AudioAvailability.Available && sample.Values is { } values)
{
    double pulse = values.Pulse;
    double bass = values.BassLevel;
}
```

`AudioFeatureSample` 有 `TimeUs`、`Availability`、`Values?`、`DiagnosticCode?`。范围结果有 `RequestedRange`、`AvailableRange?`、`Availability`、`Points`、`DiagnosticCode?`；点为 `AudioFeaturePoint(TimeUs, Values)`。请求部分越过真实音频时长时，整体状态为 `OutsideRange`，`AvailableRange` 和 `Points` 仍可描述相交部分。单点查询在 `t >= DurationUs` 时返回 `OutsideRange`。

### 17.2 声明背景媒体查询

背景查询先在清单根定义，再由导出引用。以下为两个位置的示意：

```json
{
  "mediaQueries":[{
    "id":"behind",
    "space":"canvasPixels",
    "window":{"kind":"literal","durationUs":300000}
  }],
  "requires":["media.features"]
}
```

controller 导出内加入：

```json
{
  "mediaQueries":[{
    "query":"behind",
    "fallback":{
      "pending":{"kind":"authorBranch","outputs":["stroke"]},
      "unavailable":{"kind":"authorBranch","outputs":["stroke"]},
      "noMedia":{"kind":"authorBranch","outputs":["stroke"]}
    }
  }]
}
```

把 `stroke` 换成该导出的完整输出 ID 集合；三个状态各覆盖全部输出。controller 使用 `authorBranch`，由作者代码按状态处理；graph / material 使用 `outputBypass`，按图旁路或材质 fallback 处理。

窗口也可写 `{"kind":"parameter","parameter":"window"}`，引用同包 duration 参数，且该参数列入导出的 `parameters`。窗口及参数的完整允许范围位于 0～2,000,000 微秒。

### 17.3 查询坐标与时间

`source.Media.GetId("behind")` 取得当前导出的查询 ID，可保存到准备数据。文字或晚期阶段调用：

```text
frame.Media.Sample(queryId, rect, projectTimeUs) → MediaFeatureSample
```

| `space` | 矩形范围与结果 |
| --- | --- |
| `sourceNormalized` | 旋转后源图的 `[0,1]²`；返回 `SourceValues` |
| `canvasPixels` | 画布逻辑像素范围；返回 `CanvasValues`，含媒体摆放、裁切、留边及工程底色 |

矩形为非空的 `Rect2(left, top, right, bottom)`，完整位于所选空间内。使用文字的世界墨迹范围查询背景时，可先将它与画布取交集，再采样。

窗口 0 取指定时刻对应的媒体帧；正窗口按 `[t−window,t)` 加权统计。歌曲开头窗口越过零的部分使用媒体首帧，尾部按工程的循环或末帧保持规则取值。静态图片在各时刻保持相同源内容。

### 17.4 状态与固定输入

`MediaFeatureSample.Input.State` 为：

| 状态 | 处理方式 |
| --- | --- |
| `Available` | 读取所选空间的统计值 |
| `Pending` | 本次固定输入的分析尚在准备中，执行作者的 pending 分支 |
| `Unavailable` | 按 `DiagnosticCode` 处理不可用分支 |
| `NoMedia` | 按 `NoMediaReason` 处理无媒体分支 |

后三个状态的统计值为 `null`。常用自适应描边可在 `Available` 时写入，在其他状态保留源描边。

| 使用场景 | 分析固定方式 |
| --- | --- |
| 工程预览 | 固定当时的可用性；分析未完成时可为 `Pending` |
| 作者与模板试样 | 最多等待 20 秒取得完整分析 |
| 正式验证、导出 | 等待完整分析结果后固定 |
| 透明输出 | `NoMedia`，原因为 `TransparentOutput` |

状态在同一次固定运行中保持一致；分析完成后重新固定预览输入即可使用新结果。

`MediaQueryCatalog` 有 `Profiles`、`Input?`、`GetId(declaredId)`。每个 profile 有 `Id`、`Space`、`WindowUs`。查询 ID 由当前目录取得，供该目录对应的固定输入使用。

### 17.5 统计字段

`MediaFeatureSample` 还提供 `Query`、`Rect`、`ProjectTimeUs`、`SourceValues?`、`CanvasValues?`。

`LinearPremulMean` 有 `R`、`G`、`B`、`Alpha`；它是线性且已乘 alpha 的均值，RGB 分量位于 `[0, Alpha]`。`MeanPremulY` 等字段统计的是已乘 alpha 的亮度。

| 结果类型 | 全部统计字段 |
| --- | --- |
| `SourceMediaFeatureValues` | `Mean`、`MeanPremulY`、`MeanSquaredPremulY`、`MeanAlphaTimesPremulY`、`MeanSquaredAlpha`、`PremulYVariance` |
| `CanvasMediaFeatureValues` | `Mean`、`MeanPremulY`、`MeanSquaredPremulY`、`PremulYVariance`、`IsOpaque` |

`MeanPremulY` 用于背景亮度判断，`PremulYVariance` 可衡量背景纹理变化。平方与交叉矩字段各自统计区域内对应值的均值，可用于计算透明图像合成后的亮度变化。源统计保留源透明度；画布统计按固定摆放和底色处理，`IsOpaque` 表示平均 alpha 恰为 1。

`Input` 的其他字段为 `InputRevision`、`MappingKey`、`ProfileDigest`、`SourceKey?`、`ArtifactHash?`、`DiagnosticCode?`、`NoMediaReason?`，可用于识别本次结果和显示状态说明。

## 18. 编辑、验证与发布

1. 在样式库导入包，创建作者草稿，选择 Vulkan GPU。
2. 编辑 C#、清单和文本资源；按需要替换包图片等素材，保存为源码修订。
3. C# 包编译指定修订，按诊断中的文件、行和列修正源码。
4. 采用成功编译的修订；若清单同时修改了参数、入口或绑定，在采用时设置对应配置。纯图与材质包可采用其资源修订。
5. 查看试样，检查时间点、参数边界、字族/注音/标记、透明输出等实际效果。试样随机分布随固定种子与实例保持一致。
6. 执行作者验证，再保存到效果组件库，下载 `.mkfx` 或作为模板配方用于工程。

“最新源码”“编译的源码”“在用源码”分别标明各操作对应的修订。试样、验证和保存使用已采用的配置与源码；采用操作把选定修订用于后续效果。

作者正式验证使用 640×360、30 fps、12 秒的试样，对不透明与透明输出各检查 360 帧。实际工程验证和导出再按工程的布局、媒体、字体、参数与成片长度运行。

帧检查可查看字框、墨迹、字簇、播放头、粒子出生点与单帧成本。编译诊断定位源码，运行诊断标明实例、阶段、时刻和包来源。需要通过 HTTP 集成作者工作流时，可使用文末链接的作者接口参考。

## 19. 规模与诊断参考

### 19.1 常用规模边界

下表为当前默认上限。运行配置可选择更紧的额度；同帧多个实例共同使用相关帧预算。规模较大的效果可通过减少网格、缩短寿命、降低发射率、合并同类图元等方式控制成本。

| 项目 | 默认边界 |
| --- | --- |
| 包内容 | 256 MiB，1024 文件（含清单），单文件 64 MiB |
| 时间线 | 200,000 事件、1,000,000 查询键、4,000,000 键内引用 |
| 源索引 / 单次基础布局 | 各类主要记录至多 200,000 项 |
| 行退场 | 每行至多 3 秒；同时 128 个副本 |
| Warp | 每轴 1～32 格；原始写入 256 次、65,536 顶点、131,072 三角、8 MiB |
| 映射投影 | 32 个步骤；矩形投影累计 65,536 顶点 |
| 图 / 材质写入 | 256 次、65,536 标量、8 MiB；被后写覆盖的写入仍计入 |
| 图编译 | 每图 32 pass，单边 8192 像素，外扩 256 像素，blur sigma 64 像素 |
| 图表面 | 默认 256 个表面、512 MiB |
| shader uniform | 每个 shader 至多 128 个标量 |
| 发射器 | 每实例至多 32 个，单粒子寿命至多 5 秒 |
| 速率轨 | 速率段与启用窗合计 4096 项 |
| Burst 行 | 50,000 行 |
| 粒子查询 | 完整出生 50,000，存活 20,000，拖尾 200,000 节点 |
| 粒子内存 | 准备 256 MiB，帧 64 MiB |
| 最终锚点 | 唯一时刻 4096、文字求值 32,768 次、窗口外端点 1024 |
| 画布图元 | 默认 100,000 项；拖尾每节点计一项，自动和手动绘制共同计入 |
| SVG 资源 | 至多 128 个；单源 4 MiB、全包源 256 MiB；viewport 每边至多 1,000,000；准备内存 256 MiB |
| SVG 图集 | 单边含 padding 至多 4096，全包 mip 图集 128 MiB |
| 媒体特征 | 窗口 0～2 秒，同工程至多 4 种不同窗口 |

达到额度时，诊断会标明对应项目，便于调整效果规模。完整公开粒子常量见签名速查中的 `ParticleLimits`。

### 19.2 编译提示的处理

| 编号 | 编写时的处理方向 |
| --- | --- |
| `MKV2001` | 时间、随机、资源与分析改从 SDK 的固定输入读取 |
| `MKV2002` | 共享常量使用 `const` 或递归只读值 |
| `MKV2003` | 把可变计算放在当前调用的局部变量和局部集合中 |
| `MKV2004` | 准备结果及嵌套成员使用只读数据与不可变集合 |
| `MKV2005` | 上下文、帧、writer 和查询服务在当前阶段使用，把所需只读值放进准备结果 |
| `MKV2006` | 使用受支持的确定性函数；比较、筛选、排序明确指定 ordinal 规则 |
| `MKV2007` | 使用同步的确定性 C# 计算 |
| `MKV2008` | 检查逐时刻分配，把固定结果提前准备 |
| `MKV2009` | 检查逐时刻 LINQ 枚举与分配成本 |

`MKV2001`～`MKV2007` 为编译错误，`MKV2008` / `MKV2009` 为性能提示。

### 19.3 常见运行诊断

| 诊断 | 对应调整 |
| --- | --- |
| `TargetOutsideBinding` | 使用当前绑定授予的真实目标 |
| `UndeclaredCapability` | 在包与相关输出声明实际使用的能力 |
| `UndeclaredPackageOutput` | 对齐清单输出与准备数据中的输出 ID、类型、图层及资源种类 |
| `UnsupportedGlyphPartition` | 检查单元的独立性，使用独立字簇或整行变换 |
| `WarpOutsideDomain` | 扩大网格定义域以覆盖字框、装饰和实际映射范围 |
| `DuplicateOverlayBatch` | 将同一输出的图元汇总为一次批次提交 |
| `InvalidParameterResource`、`InvalidMaterialResource` | 从当前包修订的资源目录取得句柄 |
| `MaterialUniformIdentityMismatch` | 对齐最终材质身份和覆盖的材质槽 |
| `ShaderUniformValueMismatch` | 对齐 uniform 的类型、数组形状和长度 |
| `ParticleLifetimeExceeded` | 把粒子寿命设置在清单发射器与 5 秒范围内 |
| `BackgroundMediaUnavailable` | 图采用原图旁路，材质采用声明的 fallback；补充背景后重新固定输入 |
| `TransparentBackgroundDependency` | 透明输出按图旁路或材质 fallback 绘制 |
| `SourceIndexExpired` | 在当前有效阶段重新取得源查询服务 |
| `PackageBackendUnsupported` | 为脚本效果选择 Vulkan GPU |

当效果主动终止当前求值时，可抛出带有清楚说明的异常。宿主将失败关联到该实例、阶段和歌曲时刻。预览可显示标明修订的最近成功帧，导出按实际运行结果报告完成或失败。

## 20. 完整类型与成员速查

以下列出公开类型的构造器、方法、属性、常量与枚举值。通用值类型位于 `MakoKaraGenerator.Scripting`，纯求值入口及帧输出位于 `MakoKaraGenerator.Scripting.V2`；脚本通常同时引用这两个命名空间。

`ctor` 表示构造器；`get` 表示可读，`init` 表示可在构造或 `with` 时初始化。记录类型可用 C# 的值比较与 `with` 复制。成员签名使用 C# 基础类型别名，`ImmutableArray` 来自 `System.Collections.Immutable`，`CancellationToken` 来自 `System.Threading`。父类成员随继承可用，参数默认值在签名中标明。`required` 属性在自行构造时一并初始化；帧、几何和查询结果通常直接使用宿主提供的实例。

### 20.1 入口与帧读写

#### BaseTokenStyle

```text
sealed class BaseTokenStyle
  ctor (BaseTokenTarget Target, StyleUpdate Update)
  property BaseTokenTarget Target { get; init; }
  property StyleUpdate Update { get; init; }
```

#### BaseTokenTransform

```text
sealed class BaseTokenTransform
  ctor (BaseTokenTarget Target, Affine2 LocalMatrix)
  property Affine2 LocalMatrix { get; init; }
  property BaseTokenTarget Target { get; init; }
```

#### CapabilityNames

```text
static class CapabilityNames
  field const string ClusterAffine = "cluster.affine"
  field const string ClusterWarp = "cluster.warp"
  field const string GroupAffine = "group.affine"
  field const string GroupOpacity = "group.opacity"
  field const string ImageGraph = "image.graph"
  field const string ImageGraphGroup = "image.graph.group"
  field const string LineAffine = "line.affine"
  field const string LineExit = "line.exit"
  field const string MarkAffine = "mark.affine"
  field const string MarkWarp = "mark.warp"
  field const string MaterialUniform = "material.uniform"
  field const string MediaFeatures = "media.features"
  field const string ParticlesBurst = "particles.burst"
  field const string ParticlesContinuous = "particles.continuous"
  field const string StyleFill = "style.fill"
  field const string StyleGlow = "style.glow"
  field const string StyleShadow = "style.shadow"
  field const string StyleStroke = "style.stroke"
  field const string StyleWipe = "style.wipe"
  field const string SvgAtlas = "resources.svg.atlas"
  field const string SvgVector = "resources.svg.vector"
  field const string TimelineIndex = "timeline.index"
  field const string TokenAffine = "token.affine"
  field const string TokenOpacity = "token.opacity"
  field const string TokenWarp = "token.warp"
  field const string VisualsDots = "visuals.dots"
  field const string VisualsPaths = "visuals.paths"
  field const string VisualsSegments = "visuals.segments"
  field const string VisualsSprites = "visuals.sprites"
  field const string VisualsTrails = "visuals.trails"
  property static ImmutableArray<string> All { get; }
```

#### DeclareContext

```text
sealed class DeclareContext
  property AudioFeatures Audio { get; }
  property ContentCatalog Content { get; }
  property DeterministicRandom Random { get; }
  property EffectInstanceId InstanceId { get; }
  property ParameterValues Parameters { get; }
  property ResourceCatalog Resources { get; }
  property SceneCatalog Scene { get; }
  property MediaQueryCatalog Media { get; }
  property SourceIndexes Indexes { get; }
  property TimelineIndex Timeline { get; }
  property Vector2 CanvasSize { get; }
  property long? ProjectDurationUs { get; }
  property ulong Seed { get; }
```

#### IPureKaraokeEffect

```text
interface IPureKaraokeEffect
  method abstract PreparedEffect Declare(DeclareContext source)
  method abstract void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
```

#### IPureKaraokeEffect<TPrepared>

```text
interface IPureKaraokeEffect<TPrepared> : IPureKaraokeEffect
  method abstract void Evaluate(in TextFrame frame, TPrepared data, TextOutput output)
  method abstract TPrepared Declare(DeclareContext source)
```

#### IPureVisualEffect

```text
interface IPureVisualEffect
  method abstract void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
```

#### IPureVisualEffect<TPrepared>

```text
interface IPureVisualEffect<TPrepared> : IPureKaraokeEffect, IPureKaraokeEffect<TPrepared>, IPureVisualEffect
  method abstract void EvaluateVisuals(in VisualFrame frame, TPrepared data, VisualOutput output)
```

#### PreparedEffect

```text
abstract class PreparedEffect
  property GeometrySamplingPolicy GeometrySampling { get; init; }
  property ParticleSchedule Particles { get; init; }
  property ImmutableArray<VisualOutputDeclaration> VisualOutputs { get; init; }
  property static PreparedEffect Empty { get; }
```

#### TextClusterTransform

```text
sealed class TextClusterTransform
  ctor (TextClusterTarget Target, Affine2 LocalMatrix)
  property Affine2 LocalMatrix { get; init; }
  property TextClusterTarget Target { get; init; }
```

#### TextFrame

```text
struct TextFrame
  property AudioFeatures Audio { get; }
  property BaselineStyles Baseline { get; }
  property DeterministicRandom Random { get; }
  property EffectBinding Binding { get; }
  property FramePresentations Presentations { get; }
  property OutputMode OutputMode { get; }
  property ParameterValues Parameters { get; }
  property ResourceCatalog Resources { get; }
  property DeclareContext Source { get; }
  property MediaFeatures Media { get; }
  property SourceIndexes Indexes { get; }
  property TimelineIndex Timeline { get; }
  property Vector2 CanvasSize { get; }
  property ImmutableArray<GroupGeometrySample> GroupGeometry { get; }
  property ImmutableArray<LineGeometrySample> LineGeometry { get; }
  property ImmutableArray<TextMarkGeometrySample> MarkGeometry { get; }
  property ImmutableArray<TokenGeometrySample> Geometry { get; }
  property ImmutableArray<TextClusterGeometrySample> Clusters { get; }
  property ImmutableArray<TextLineLoad> LineLoads { get; }
  property long TimeUs { get; }
  property long? ProjectDurationUs { get; }
```

#### TextOutput

```text
sealed class TextOutput
  method void AppendTransform(TextTarget target, Affine2 localMatrix)
  method void BindGraph(TextTarget target, GraphBindingValue binding)
  method void MultiplyOpacity(TextTarget target, double factor)
  method void SetFill(TextTarget target, TextState state, MaterialValue material)
  method void SetGlow(TextTarget target, TextState state, GlowValue? value)
  method void SetMaterialUniform(TextTarget target, MaterialSlot slot, MaterialHandle material, string nodeId, string name, UniformValue value)
  method void SetShadow(TextTarget target, TextState state, ShadowValue? value)
  method void SetStrokes(TextTarget target, TextState state, params StrokeValue[] strokes)
  method void SetWarp(TextTarget target, GridWarp? value)
  method void SetWipe(TextTarget target, WipeValue wipe)
```

#### TextOutputSnapshot

```text
sealed class TextOutputSnapshot
  property ImmutableArray<TextWrite> Writes { get; }
  property static TextOutputSnapshot Empty { get; }
```

#### TextState

```text
enum TextState
  value Both = 2
  value Sung = 1
  value Unsung = 0
```

#### TextTarget

```text
abstract class TextTarget
  method static TextTarget op_Implicit(BaseTokenTarget value)
  method static TextTarget op_Implicit(GroupTarget value)
  method static TextTarget op_Implicit(LineTarget value)
  method static TextTarget op_Implicit(TextMarkTarget value)
  method static TextTarget op_Implicit(TokenFamilyTarget value)
  method static TextTarget op_Implicit(TextClusterTarget value)
```

#### TextTarget.Base

```text
sealed class TextTarget.Base : TextTarget
  ctor (BaseTokenTarget Value)
  property BaseTokenTarget Value { get; init; }
```

#### TextTarget.Cluster

```text
sealed class TextTarget.Cluster : TextTarget
  ctor (TextClusterTarget Value)
  property TextClusterTarget Value { get; init; }
```

#### TextTarget.Family

```text
sealed class TextTarget.Family : TextTarget
  ctor (TokenFamilyTarget Value)
  property TokenFamilyTarget Value { get; init; }
```

#### TextTarget.Group

```text
sealed class TextTarget.Group : TextTarget
  ctor (GroupTarget Value)
  property GroupTarget Value { get; init; }
```

#### TextTarget.Line

```text
sealed class TextTarget.Line : TextTarget
  ctor (LineTarget Value)
  property LineTarget Value { get; init; }
```

#### TextTarget.Mark

```text
sealed class TextTarget.Mark : TextTarget
  ctor (TextMarkTarget Value)
  property TextMarkTarget Value { get; init; }
```

#### TextWrite

```text
abstract class TextWrite
  property TextTarget Target { get; }
```

#### TextWrite.Fill

```text
sealed class TextWrite.Fill : TextWrite
  property MaterialValue Material { get; }
  property TextState State { get; }
```

#### TextWrite.Glow

```text
sealed class TextWrite.Glow : TextWrite
  property GlowValue? Value { get; }
  property TextState State { get; }
```

#### TextWrite.GraphBinding

```text
sealed class TextWrite.GraphBinding : TextWrite
  property GraphBindingValue Value { get; }
```

#### TextWrite.MaterialUniform

```text
sealed class TextWrite.MaterialUniform : TextWrite
  property MaterialHandle Material { get; }
  property MaterialSlot Slot { get; }
  property UniformValue Value { get; }
  property string Name { get; }
  property string NodeId { get; }
```

#### TextWrite.Opacity

```text
sealed class TextWrite.Opacity : TextWrite
  property double Factor { get; }
```

#### TextWrite.Shadow

```text
sealed class TextWrite.Shadow : TextWrite
  property ShadowValue? Value { get; }
  property TextState State { get; }
```

#### TextWrite.Strokes

```text
sealed class TextWrite.Strokes : TextWrite
  property TextState State { get; }
  property ImmutableArray<StrokeValue> Layers { get; }
```

#### TextWrite.Transform

```text
sealed class TextWrite.Transform : TextWrite
  property Affine2 LocalMatrix { get; }
```

#### TextWrite.Warp

```text
sealed class TextWrite.Warp : TextWrite
  property GridWarp? Value { get; }
```

#### TextWrite.Wipe

```text
sealed class TextWrite.Wipe : TextWrite
  property WipeValue Value { get; }
```

#### VisualFrame

```text
struct VisualFrame
  property AudioFeatures Audio { get; }
  property DeterministicRandom Random { get; }
  property EffectBinding Binding { get; }
  property FinalSceneGeometry FinalGeometry { get; }
  property OutputMode OutputMode { get; }
  property ParameterValues Parameters { get; }
  property ResourceCatalog Resources { get; }
  property DeclareContext Source { get; }
  property MediaFeatures Media { get; }
  property ParticleFrame Particles { get; }
  property SourceIndexes Indexes { get; }
  property TimelineIndex Timeline { get; }
  property Vector2 CanvasSize { get; }
  property long TimeUs { get; }
  property long? ProjectDurationUs { get; }
```

#### VisualOutput

```text
sealed class VisualOutput
  method void DrawAtlasSprites(OutputId id, ImmutableArray<AtlasSprite> items, OverlayLayer layer)
  method void DrawDots(OutputId id, ImmutableArray<Dot> items, OverlayLayer layer)
  method void DrawPaths(OutputId id, ImmutableArray<VectorVisual> items, OverlayLayer layer)
  method void DrawSegments(OutputId id, ImmutableArray<Segment> items, OverlayLayer layer)
  method void DrawSprites(OutputId id, ImmutableArray<Sprite> items, OverlayLayer layer)
  method void DrawTrails(OutputId id, ImmutableArray<Trail> items, OverlayLayer layer)
```

### 20.2 参数、曲线与随机

#### CurveValue

```text
abstract class CurveValue
  method double Evaluate(double progress)
```

#### CurveValue.ControlPoints

```text
sealed class CurveValue.ControlPoints : CurveValue
  ctor (ImmutableArray<Point2> points)
  property ImmutableArray<Point2> Points { get; }
```

#### CurveValue.CubicBezier

```text
sealed class CurveValue.CubicBezier : CurveValue
  ctor (Point2 first, Point2 second)
  property Point2 First { get; }
  property Point2 Second { get; }
```

#### CurveValue.Easing

```text
sealed class CurveValue.Easing : CurveValue
  ctor (EasingKind kind)
  property EasingKind Kind { get; }
```

#### DeterministicRandom

```text
sealed class DeterministicRandom
  ctor (ulong seed, EffectInstanceId instance)
  method double GetUnitDouble(string streamKey, ulong index)
  method uint GetUInt32(string streamKey, ulong index)
  property static string Algorithm { get; }
```

#### EasingKind

```text
enum EasingKind
  value EaseInOut = 2
  value EaseOut = 1
  value Linear = 0
```

#### ParameterValue

```text
abstract class ParameterValue
```

#### ParameterValue.Asset

```text
sealed class ParameterValue.Asset : ParameterValue
  ctor (ImageHandle value)
  property ImageHandle Value { get; }
```

#### ParameterValue.Choice

```text
sealed class ParameterValue.Choice : ParameterValue
  ctor (string value)
  property string Value { get; }
```

#### ParameterValue.Color

```text
sealed class ParameterValue.Color : ParameterValue
  ctor (ColorRgba value)
  property ColorRgba Value { get; }
```

#### ParameterValue.Curve

```text
sealed class ParameterValue.Curve : ParameterValue
  ctor (CurveValue value)
  property CurveValue Value { get; }
```

#### ParameterValue.Duration

```text
sealed class ParameterValue.Duration : ParameterValue
  ctor (long value)
  property long Value { get; }
```

#### ParameterValue.Number

```text
sealed class ParameterValue.Number : ParameterValue
  ctor (double value)
  property double Value { get; }
```

#### ParameterValue.Text

```text
sealed class ParameterValue.Text : ParameterValue
  ctor (string value)
  property string Value { get; }
```

#### ParameterValue.Toggle

```text
sealed class ParameterValue.Toggle : ParameterValue
  ctor (bool Value)
  property bool Value { get; init; }
```

#### ParameterValue.Whole

```text
sealed class ParameterValue.Whole : ParameterValue
  ctor (long Value)
  property long Value { get; init; }
```

#### ParameterValues

```text
sealed class ParameterValues
  ctor (IEnumerable<KeyValuePair<string, ParameterValue>> values)
  method ColorRgba GetColor(string id)
  method CurveValue GetCurve(string id)
  method ImageHandle GetAsset(string id)
  method bool GetBoolean(string id)
  method double GetNumber(string id)
  method long GetDuration(string id)
  method long GetInteger(string id)
  method string GetEnum(string id)
  method string GetText(string id)
  method void ValidateResources(ResourceCatalog resources)
  property ImmutableArray<string> Ids { get; }
  property static ParameterValues Empty { get; }
```

### 20.3 源歌词、场景、绑定与身份

#### BindingId

```text
struct BindingId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### BranchId

```text
struct BranchId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### ClipId

```text
struct ClipId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### ContentCatalog

```text
sealed class ContentCatalog
  ctor (ImmutableArray<SourceBranch> branches, ImmutableArray<SourceLine> lines, ImmutableArray<SourceToken> tokens, ImmutableArray<SourceRuby> ruby)
  method SourceBranch GetBranch(BranchId id)
  method SourceLine GetLine(LineId id)
  method SourceRuby GetRuby(RubyId id)
  method SourceToken GetToken(TokenId id)
  method bool TryGetToken(TokenId id, out SourceToken? token)
  method ImmutableArray<SourceToken> FindTokens(string text, StringComparison comparison = StringComparison.Ordinal)
  property ImmutableArray<SourceBranch> Branches { get; }
  property ImmutableArray<SourceLine> Lines { get; }
  property ImmutableArray<SourceRuby> Ruby { get; }
  property ImmutableArray<SourceToken> Tokens { get; }
```

#### CueId

```text
struct CueId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### CursorTrackId

```text
struct CursorTrackId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### DisplayPhase

```text
enum DisplayPhase
  value Performing = 1
  value Preparing = 0
  value Retained = 2
```

#### DrawUnitId

```text
struct DrawUnitId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### EffectBinding

```text
abstract class EffectBinding
```

#### EffectBinding.Branch

```text
sealed class EffectBinding.Branch : EffectBinding
  ctor (BranchId Id)
  property BranchId Id { get; init; }
```

#### EffectBinding.Clips

```text
sealed class EffectBinding.Clips : EffectBinding
  ctor (ImmutableArray<ClipId> Ids)
  property ImmutableArray<ClipId> Ids { get; init; }
```

#### EffectBinding.Lines

```text
sealed class EffectBinding.Lines : EffectBinding
  ctor (ImmutableArray<LineId> Ids)
  property ImmutableArray<LineId> Ids { get; init; }
```

#### EffectBinding.Node

```text
sealed class EffectBinding.Node : EffectBinding
  ctor (NodeId Id, bool IncludeDescendants)
  property NodeId Id { get; init; }
  property bool IncludeDescendants { get; init; }
```

#### EffectBinding.Project

```text
sealed class EffectBinding.Project : EffectBinding
  ctor ()
```

#### EffectBinding.Tokens

```text
sealed class EffectBinding.Tokens : EffectBinding
  ctor (ImmutableArray<TokenId> Ids)
  property ImmutableArray<TokenId> Ids { get; init; }
```

#### EffectInstanceId

```text
struct EffectInstanceId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### EmitterId

```text
struct EmitterId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### EventId

```text
struct EventId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### FrameGroup

```text
sealed class FrameGroup
  ctor (GroupTarget Target, NodeId? ParentId, bool IsVisible, double AnimationOpacity)
  property GroupTarget Target { get; init; }
  property NodeId? ParentId { get; init; }
  property bool IsVisible { get; init; }
  property double AnimationOpacity { get; init; }
```

#### FrameLine

```text
sealed class FrameLine
  ctor (LineId LineId, BranchId BranchId, BindingId BindingId, NodeId NodeId, NodeId ParentId, ProgramId ProgramId, ClipId? ClipId, CueId? CueId, DisplayPhase DisplayPhase, double EnterProgress)
  property BindingId BindingId { get; init; }
  property BranchId BranchId { get; init; }
  property ClipId? ClipId { get; init; }
  property CueId? CueId { get; init; }
  property DisplayPhase DisplayPhase { get; init; }
  property LineId LineId { get; init; }
  property LineTarget Target { get; }
  property NodeId NodeId { get; init; }
  property NodeId ParentId { get; init; }
  property ProgramId ProgramId { get; init; }
  property double EnterProgress { get; init; }
```

#### FrameMark

```text
sealed class FrameMark
  ctor (TextMarkTarget Target, FrameLine TargetLine, NodeId ParentId, string Text, bool IsElapsed, double AnimationOpacity)
  property FrameLine TargetLine { get; init; }
  property NodeId ParentId { get; init; }
  property TextMarkTarget Target { get; init; }
  property bool IsElapsed { get; init; }
  property double AnimationOpacity { get; init; }
  property string Text { get; init; }
```

#### FramePresentations

```text
sealed class FramePresentations
  ctor ()
  property ImmutableArray<FrameGroup> Groups { get; init; }
  property ImmutableArray<FrameLine> LinesInBinding { get; init; }
  property ImmutableArray<FrameMark> MarksInBinding { get; init; }
  property ImmutableArray<FrameToken> TokensInBinding { get; init; }
```

#### FrameToken

```text
sealed class FrameToken
  ctor ()
  property required BaseTokenTarget BaseOnly { get; init; }
  property required BindingId BindingId { get; init; }
  property required BranchId BranchId { get; init; }
  property required LineId LineId { get; init; }
  property OccurrenceId? OccurrenceId { get; init; }
  property required PerformanceSample Performance { get; init; }
  property required TokenFamilyTarget WithRuby { get; init; }
  property required TokenId TokenId { get; init; }
  property required string SourceText { get; init; }
```

#### LineId

```text
struct LineId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### NodeId

```text
struct NodeId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### NodeLayoutKind

```text
enum NodeLayoutKind
  value FreeLine = 1
  value None = 0
  value Ruby = 4
  value SlotSequence = 2
  value TranslationFlow = 3
```

#### OccurrenceId

```text
struct OccurrenceId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### OutputId

```text
struct OutputId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### ProgramId

```text
struct ProgramId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### RubyId

```text
struct RubyId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

#### SceneCatalog

```text
sealed class SceneCatalog
  ctor (ContentCatalog content, ImmutableArray<SourceNode> nodes, ImmutableArray<SourceProgram> programs, ImmutableArray<SourceClip> clips, ImmutableArray<SourceCue> cues)
  method SourceNode GetNode(NodeId id)
  property ImmutableArray<SourceClip> Clips { get; }
  property ImmutableArray<SourceCue> LeadIns { get; }
  property ImmutableArray<SourceNode> Nodes { get; }
  property ImmutableArray<SourceProgram> Programs { get; }
```

#### SceneNodeKind

```text
enum SceneNodeKind
  value Group = 0
  value Text = 1
```

#### SourceBranch

```text
sealed class SourceBranch
  ctor (BranchId Id, string Name, string Role, int SourceOrder, ImmutableArray<LineId> LineIds)
  property BranchId Id { get; init; }
  property ImmutableArray<LineId> LineIds { get; init; }
  property int SourceOrder { get; init; }
  property string Name { get; init; }
  property string Role { get; init; }
```

#### SourceClip

```text
sealed class SourceClip
  ctor (ClipId Id, ProgramId ProgramId, NodeId ContainerId, ImmutableArray<LineId> LineIds, TimeRange TimeRange)
  property ClipId Id { get; init; }
  property NodeId ContainerId { get; init; }
  property ProgramId ProgramId { get; init; }
  property TimeRange TimeRange { get; init; }
  property ImmutableArray<LineId> LineIds { get; init; }
```

#### SourceCue

```text
sealed class SourceCue
  ctor (CueId Id, ProgramId ProgramId, LineId TargetLineId, NodeId ContainerId, bool Enabled, TimeRange? TimeRange)
  property CueId Id { get; init; }
  property LineId TargetLineId { get; init; }
  property NodeId ContainerId { get; init; }
  property ProgramId ProgramId { get; init; }
  property TimeRange? TimeRange { get; init; }
  property bool Enabled { get; init; }
```

#### SourceLine

```text
sealed class SourceLine
  ctor (LineId Id, BranchId BranchId, string Text, TimeRange? TimeRange, SourceTimingStatus TimingStatus, int SourceOrder, ImmutableArray<TokenId> TokenIds, ImmutableArray<RubyId> RubyIds)
  property BranchId BranchId { get; init; }
  property LineId Id { get; init; }
  property SourceTimingStatus TimingStatus { get; init; }
  property TimeRange? TimeRange { get; init; }
  property ImmutableArray<RubyId> RubyIds { get; init; }
  property ImmutableArray<TokenId> TokenIds { get; init; }
  property int SourceOrder { get; init; }
  property string Text { get; init; }
```

#### SourceNode

```text
sealed class SourceNode
  ctor (NodeId Id, NodeId? ParentId, SceneNodeKind Kind, NodeLayoutKind Layout, Vector2 ReferenceRatio, BranchId? BranchId, LineId? LineId, RubyId? RubyId, int Order)
  property BranchId? BranchId { get; init; }
  property LineId? LineId { get; init; }
  property NodeId Id { get; init; }
  property NodeId? ParentId { get; init; }
  property NodeLayoutKind Layout { get; init; }
  property RubyId? RubyId { get; init; }
  property SceneNodeKind Kind { get; init; }
  property Vector2 ReferenceRatio { get; init; }
  property int Order { get; init; }
```

#### SourceProgram

```text
sealed class SourceProgram
  ctor (ProgramId Id, BranchId BranchId, NodeId RootNodeId, NodeId DefaultNodeId)
  property BranchId BranchId { get; init; }
  property NodeId DefaultNodeId { get; init; }
  property NodeId RootNodeId { get; init; }
  property ProgramId Id { get; init; }
```

#### SourceRuby

```text
sealed class SourceRuby
  ctor (RubyId Id, LineId LineId, TokenId BaseTokenId, string Text, string? Language)
  property LineId LineId { get; init; }
  property RubyId Id { get; init; }
  property TokenId BaseTokenId { get; init; }
  property ImmutableArray<SourceRubySegment> Segments { get; init; }
  property string Text { get; init; }
  property string? Language { get; init; }
```

#### SourceRubySegment

```text
sealed class SourceRubySegment
  ctor (string Id, string Text, TimeRange? TimeRange, SourceTimingStatus TimingStatus)
  property SourceTimingStatus TimingStatus { get; init; }
  property TimeRange? TimeRange { get; init; }
  property string Id { get; init; }
  property string Text { get; init; }
```

#### SourceTimingStatus

```text
enum SourceTimingStatus
  value Confirmed = 4
  value Edited = 3
  value Estimated = 2
  value Imported = 1
  value Invalid = 5
  value Missing = 0
```

#### SourceToken

```text
sealed class SourceToken
  ctor (TokenId Id, BranchId BranchId, LineId LineId, string Text, TimeRange? TimeRange, SourceTimingStatus TimingStatus, int SourceOrder)
  property BranchId BranchId { get; init; }
  property LineId LineId { get; init; }
  property SourceTimingStatus TimingStatus { get; init; }
  property TimeRange? TimeRange { get; init; }
  property TokenId Id { get; init; }
  property int SourceOrder { get; init; }
  property string Text { get; init; }
```

#### TokenId

```text
struct TokenId
  ctor (string value)
  property bool IsValid { get; }
  property string Value { get; }
```

### 20.4 几何、映射与播放头

#### Affine2

```text
struct Affine2
  ctor (double a, double b, double c, double d, double tx, double ty)
  method Affine2 Compose(Affine2 child)
  method Point2 Map(Point2 point)
  method bool TryInverse(out Affine2 inverse)
  method static Affine2 ScaleAt(double factor, Point2 pivot)
  method static Affine2 Translation(double x, double y)
  property double A { get; }
  property double B { get; }
  property double C { get; }
  property double D { get; }
  property double Tx { get; }
  property double Ty { get; }
  property static Affine2 Identity { get; }
```

#### BaseSceneGeometry

```text
sealed class BaseSceneGeometry
  ctor ()
  property ImmutableArray<FrameGroup> Groups { get; init; }
  property ImmutableArray<FrameLine> Lines { get; init; }
  property ImmutableArray<FrameMark> Marks { get; init; }
  property ImmutableArray<GroupGeometrySample> GroupGeometry { get; init; }
  property ImmutableArray<LineGeometrySample> LineGeometry { get; init; }
  property ImmutableArray<TextMarkGeometrySample> MarkGeometry { get; init; }
  property ImmutableArray<TokenGeometrySample> Tokens { get; init; }
  property required long TimeUs { get; init; }
```

#### BaseTokenTarget

```text
sealed class BaseTokenTarget
  ctor (TokenId TokenId, BindingId BindingId)
  property BindingId BindingId { get; init; }
  property TokenId TokenId { get; init; }
```

#### CursorAnchorMode

```text
enum CursorAnchorMode
  value AdvanceCenter = 1
  value Baseline = 0
```

#### CursorGeometry

```text
sealed class CursorGeometry
  ctor ()
  method PlaybackCursor At(CursorAnchorMode mode)
  property required Affine2 WorldMatrix { get; init; }
  property required CursorIdentity Identity { get; init; }
  property required CursorTrackId TrackId { get; init; }
  property TextGeometryMapping? Mapping { get; init; }
  property required TextProgressPath Path { get; init; }
  property required double Progress { get; init; }
  property required long TimeUs { get; init; }
```

#### CursorIdentity

```text
sealed class CursorIdentity
  ctor (BranchId branchId, LineId lineId, BindingId bindingId, TimeRange range, ProgressSource source, TokenId? tokenId = null, OccurrenceId? occurrenceId = null)
  property BindingId BindingId { get; }
  property BranchId BranchId { get; }
  property LineId LineId { get; }
  property OccurrenceId? OccurrenceId { get; }
  property ProgressSource ProgressSource { get; }
  property TimeRange TimeRange { get; }
  property TokenId? TokenId { get; }
```

#### FinalSceneGeometry

```text
sealed class FinalSceneGeometry
  ctor ()
  property required FramePresentations Presentations { get; init; }
  property PlaybackFrame Playback { get; init; }
  property ImmutableArray<GroupGeometrySample> Groups { get; init; }
  property ImmutableArray<LineGeometrySample> Lines { get; init; }
  property ImmutableArray<TextMarkGeometrySample> Marks { get; init; }
  property ImmutableArray<TokenGeometrySample> Tokens { get; init; }
  property required long TimeUs { get; init; }
```

#### GeometrySample

```text
sealed class GeometrySample
  ctor ()
  method Point2 MapPoint(Point2 point)
  method Vector2 MapVectorAt(Point2 point, Vector2 vector)
  property required Affine2 BaseWorldMatrix { get; init; }
  property required Point2 Baseline { get; init; }
  property required Rect2 AdvanceBounds { get; init; }
  property Rect2? InkBounds { get; init; }
  property TextGeometryMapping? Mapping { get; init; }
  property bool CanTransformIndependently { get; init; }
  property ImmutableArray<DrawUnitId> DrawUnits { get; init; }
  property ImmutableArray<Point2> MappedInkVertices { get; init; }
  property ImmutableArray<Point2> WorldInkCorners { get; }
  property string? DiagnosticCode { get; init; }
```

#### GridWarp

```text
sealed class GridWarp
  ctor (Rect2 domain, int columns, int rows, ImmutableArray<Point2> destinationVertices)
  method Point2 MapPoint(Point2 point)
  method Vector2 MapVectorAt(Point2 point, Vector2 vector)
  property Rect2 DestinationBounds { get; }
  property Rect2 Domain { get; }
  property bool IsIdentity { get; }
  property ImmutableArray<Point2> DestinationVertices { get; }
  property ImmutableArray<Point2> SourceVertices { get; }
  property ImmutableArray<ushort> TriangleIndices { get; }
  property int Columns { get; }
  property int Rows { get; }
```

#### GroupGeometrySample

```text
sealed class GroupGeometrySample
  ctor ()
  property required Affine2 BaseWorldMatrix { get; init; }
  property required GroupTarget Target { get; init; }
  property required Point2 WorldPivot { get; init; }
  property required Rect2 ReferenceBounds { get; init; }
  property ImmutableArray<Point2> WorldInkCorners { get; init; }
```

#### GroupTarget

```text
sealed class GroupTarget
  ctor (NodeId NodeId)
  property NodeId NodeId { get; init; }
```

#### LineGeometrySample

```text
sealed class LineGeometrySample
  ctor ()
  property required BranchId BranchId { get; init; }
  property required GeometrySample BaseGeometry { get; init; }
  property required LineTarget Target { get; init; }
  property required TextProgressPath ProgressPath { get; init; }
```

#### LineTarget

```text
sealed class LineTarget
  ctor (LineId LineId, BindingId BindingId)
  property BindingId BindingId { get; init; }
  property LineId LineId { get; init; }
```

#### PerformanceSample

```text
sealed class PerformanceSample
  ctor (TimeRange? TimeRange, long? DurationUs, long? ElapsedUs, double? Progress, bool IsCurrent, bool IsSung, ProgressSource ProgressSource)
  property ProgressSource ProgressSource { get; init; }
  property TimeRange? TimeRange { get; init; }
  property bool IsCurrent { get; init; }
  property bool IsSung { get; init; }
  property double? Progress { get; init; }
  property long? DurationUs { get; init; }
  property long? ElapsedUs { get; init; }
```

#### PlaybackCursor

```text
sealed class PlaybackCursor
  ctor (CursorTrackId TrackId, CursorIdentity Identity, long TimeUs, double Progress, Point2 Position, Vector2 Direction, CursorAnchorMode AnchorMode)
  property BindingId BindingId { get; }
  property CursorAnchorMode AnchorMode { get; init; }
  property CursorIdentity Identity { get; init; }
  property CursorTrackId TrackId { get; init; }
  property LineId LineId { get; }
  property OccurrenceId? OccurrenceId { get; }
  property Point2 Position { get; init; }
  property ProgressSource ProgressSource { get; }
  property TokenId? TokenId { get; }
  property Vector2 Direction { get; init; }
  property double Progress { get; init; }
  property long TimeUs { get; init; }
```

#### PlaybackFrame

```text
sealed class PlaybackFrame
  ctor (ImmutableArray<CursorGeometry> cursors)
  method ImmutableArray<PlaybackCursor> GetCursors(CursorAnchorMode mode)
  property bool IsAvailable { get; }
  property ImmutableArray<CursorGeometry> Cursors { get; }
  property static PlaybackFrame Unavailable { get; }
```

#### Point2

```text
struct Point2
  ctor (double x, double y)
  property double X { get; }
  property double Y { get; }
```

#### ProgressSource

```text
enum ProgressSource
  value Line = 2
  value Missing = 3
  value RubySegment = 1
  value Token = 0
  value Unavailable = 4
```

#### Rect2

```text
struct Rect2
  ctor (double left, double top, double right, double bottom)
  property Point2 Center { get; }
  property double Bottom { get; }
  property double Height { get; }
  property double Left { get; }
  property double Right { get; }
  property double Top { get; }
  property double Width { get; }
```

#### RubyGeometrySample

```text
sealed class RubyGeometrySample
  ctor (RubyId RubyId, GeometrySample Geometry)
  property GeometrySample Geometry { get; init; }
  property RubyId RubyId { get; init; }
```

#### TextGeometryMapping

```text
sealed class TextGeometryMapping
  ctor (ImmutableArray<TextGeometryMapping.Step> steps)
  method Point2 MapPoint(Point2 point)
  method TextGeometryProjectionMesh ProjectRectangle(Rect2 rectangle, TextGeometryProjectionBudget? budget = null, CancellationToken cancellationToken = default)
  method Vector2 MapVectorAt(Point2 point, Vector2 vector)
  method ImmutableArray<Point2> MapRectangle(Rect2 rectangle, TextGeometryProjectionBudget? budget = null)
  method double MaximumScale()
  property bool HasWarp { get; }
  property bool IsIdentity { get; }
  property ImmutableArray<TextGeometryMapping.Step> Steps { get; }
  property static TextGeometryMapping Identity { get; }
```

#### TextGeometryMapping.Affine

```text
sealed class TextGeometryMapping.Affine : TextGeometryMapping.Step
  ctor (Affine2 matrix)
  property Affine2 Matrix { get; }
```

#### TextGeometryMapping.Grid

```text
sealed class TextGeometryMapping.Grid : TextGeometryMapping.Step
  ctor (GridWarp warp)
  property GridWarp Warp { get; }
```

#### TextGeometryMapping.Step

```text
abstract class TextGeometryMapping.Step
```

#### TextGeometryProjectionBudget

```text
sealed class TextGeometryProjectionBudget
  ctor ()
  field const int MaximumVertices = 65536
  property int Vertices { get; }
```

#### TextGeometryProjectionMesh

```text
sealed class TextGeometryProjectionMesh
  ctor (ImmutableArray<Point2> sourceVertices, ImmutableArray<Point2> destinationVertices, ImmutableArray<ushort> triangleIndices)
  property ImmutableArray<Point2> DestinationVertices { get; }
  property ImmutableArray<Point2> SourceVertices { get; }
  property ImmutableArray<ushort> TriangleIndices { get; }
  property int TriangleCount { get; }
  property int VertexCount { get; }
```

#### TextMarkGeometrySample

```text
sealed class TextMarkGeometrySample
  ctor (TextMarkTarget Target, GeometrySample Geometry)
  property GeometrySample Geometry { get; init; }
  property TextMarkTarget Target { get; init; }
```

#### TextMarkTarget

```text
sealed class TextMarkTarget
  ctor (NodeId NodeId, CueId CueId, BindingId BindingId)
  property BindingId BindingId { get; init; }
  property CueId CueId { get; init; }
  property NodeId NodeId { get; init; }
```

#### TextProgressPath

```text
sealed class TextProgressPath
  ctor (float startX, float endX, double baselineY, double centerY)
  method Point2 At(double progress, CursorAnchorMode mode)
  method static float XAt(float startX, float endX, double progress)
  property double BaselineY { get; }
  property double CenterY { get; }
  property float EndX { get; }
  property float StartX { get; }
```

#### TimeRange

```text
struct TimeRange
  ctor (long startUs, long endUs)
  method bool Contains(long timeUs)
  property bool IsValid { get; }
  property long DurationUs { get; }
  property long EndUs { get; }
  property long StartUs { get; }
```

#### TokenFamilyTarget

```text
sealed class TokenFamilyTarget
  ctor (TokenId TokenId, BindingId BindingId)
  property BindingId BindingId { get; init; }
  property TokenId TokenId { get; init; }
```

#### TokenGeometrySample

```text
sealed class TokenGeometrySample
  ctor ()
  method GeometrySample GeometryAt(Point2 point)
  method Point2 MapPoint(Point2 point)
  method Vector2 MapVectorAt(Point2 point, Vector2 vector)
  property required BaseTokenTarget BaseOnly { get; init; }
  property required BranchId BranchId { get; init; }
  property required GeometrySample BaseGeometry { get; init; }
  property required GeometrySample FamilyGeometry { get; init; }
  property required LineId LineId { get; init; }
  property TextProgressPath? ProgressPath { get; init; }
  property required TokenFamilyTarget WithRuby { get; init; }
  property ImmutableArray<RubyGeometrySample> Ruby { get; init; }
  property ImmutableArray<TextClusterGeometrySample> Clusters { get; init; }
```

#### TextClusterGeometrySample

```text
sealed class TextClusterGeometrySample
  ctor (TextClusterTarget target, ImmutableArray<TokenId> ownerTokens, int utf16Start, int utf16Length, GeometrySample geometry)
  method TextClusterGeometrySample WithGeometry(GeometrySample geometry)
  property GeometrySample Geometry { get; }
  property TextClusterTarget Target { get; }
  property ImmutableArray<TokenId> OwnerTokens { get; }
  property int Utf16Length { get; }
  property int Utf16Start { get; }
```

#### TextClusterTarget

```text
sealed class TextClusterTarget
  ctor (TokenId tokenId, BindingId bindingId, DrawUnitId drawUnitId, int unitIndex, RubyId? rubyId = null)
  property BindingId BindingId { get; }
  property DrawUnitId DrawUnitId { get; }
  property RubyId? RubyId { get; }
  property TokenId TokenId { get; }
  property int UnitIndex { get; }
```

#### Vector2

```text
struct Vector2
  ctor (double x, double y)
  property double X { get; }
  property double Y { get; }
  property static Vector2 Zero { get; }
```

### 20.5 外观与渐变

#### AppearanceUpdate

```text
sealed class AppearanceUpdate
  ctor (MaterialValue? fill = null, StrokesUpdate? strokes = null, ShadowUpdate? shadow = null, GlowUpdate? glow = null)
  method AppearanceUpdate Then(AppearanceUpdate next)
  property GlowUpdate? Glow { get; }
  property MaterialValue? Fill { get; }
  property ShadowUpdate? Shadow { get; }
  property StrokesUpdate? Strokes { get; }
  property static AppearanceUpdate None { get; }
```

#### AppearanceView

```text
sealed class AppearanceView
  ctor (MaterialValue fill, ImmutableArray<StrokeValue> strokes = default, ShadowValue? shadow = null, GlowValue? glow = null)
  field const int MaxStrokes = 4
  property GlowValue? Glow { get; }
  property MaterialValue Fill { get; }
  property ShadowValue? Shadow { get; }
  property StrokeValue? InnermostStroke { get; }
  property ImmutableArray<StrokeValue> Strokes { get; }
```

#### BaselineStyles

```text
sealed class BaselineStyles
  ctor (IEnumerable<KeyValuePair<TokenId, StyleView>> tokens)
  ctor (IEnumerable<KeyValuePair<TokenId, StyleView>> tokens, IEnumerable<KeyValuePair<LineId, StyleView>> lines, IEnumerable<KeyValuePair<NodeId, StyleView>> marks, IEnumerable<KeyValuePair<NodeId, GroupStyleView>> groups)
  method BaselineStyles WithLoaded(IEnumerable<KeyValuePair<TokenFamilyTarget, StyleView>> tokens, IEnumerable<KeyValuePair<LineTarget, StyleView>> lines)
  method GroupStyleView Get(GroupTarget target)
  method StyleView Get(LineId id)
  method StyleView Get(LineTarget target)
  method StyleView Get(TextMarkTarget target)
  method StyleView Get(TokenFamilyTarget target)
  method StyleView Get(TokenId id)
  property static BaselineStyles Empty { get; }
```

#### ColorRgba

```text
struct ColorRgba
  ctor (double red, double green, double blue, double alpha)
  method ColorRgba MultiplyRgb(double factor)
  property double Alpha { get; }
  property double Blue { get; }
  property double Green { get; }
  property double Red { get; }
```

#### GlowUpdate

```text
sealed class GlowUpdate
  ctor (GlowValue? Value)
  property GlowValue? Value { get; init; }
```

#### GlowValue

```text
sealed class GlowValue
  ctor (ColorRgba color, double opacity = 1, double blurRadiusPx = 8, double spreadPx = 0)
  property ColorRgba Color { get; }
  property double BlurRadiusPx { get; }
  property double Opacity { get; }
  property double SpreadPx { get; }
```

#### GradientColorSpace

```text
enum GradientColorSpace
  value Hsv = 1
  value LinearRgb = 0
  value Oklch = 2
```

#### GradientCoordinateSpace

```text
enum GradientCoordinateSpace
  value Canvas = 0
  value Glyph = 2
  value Line = 1
```

#### GradientHuePath

```text
enum GradientHuePath
  value Decreasing = 3
  value Increasing = 2
  value Longer = 1
  value Shorter = 0
```

#### GradientKind

```text
enum GradientKind
  value Linear = 0
  value Radial = 1
```

#### GradientStop

```text
struct GradientStop
  ctor (double offset, ColorRgba color)
  property ColorRgba Color { get; }
  property double Offset { get; }
```

#### GradientValue

```text
sealed class GradientValue
  ctor (ImmutableArray<GradientStop> stops, GradientKind kind = GradientKind.Linear, double angleDegrees = 0, GradientCoordinateSpace coordinateSpace = GradientCoordinateSpace.Line, GradientColorSpace colorSpace = GradientColorSpace.LinearRgb, GradientHuePath huePath = GradientHuePath.Shorter, double centerX = 0.5, double centerY = 0.5, double radiusX = 0.5, double radiusY = 0.5)
  method static GradientValue Bands(ImmutableArray<ColorRgba> colors, ImmutableArray<double> boundaries, GradientKind kind = GradientKind.Linear, double angleDegrees = 0, GradientCoordinateSpace coordinateSpace = GradientCoordinateSpace.Line, double centerX = 0.5, double centerY = 0.5, double radiusX = 0.5, double radiusY = 0.5)
  property GradientColorSpace ColorSpace { get; }
  property GradientCoordinateSpace CoordinateSpace { get; }
  property GradientHuePath HuePath { get; }
  property GradientKind Kind { get; }
  property ImmutableArray<GradientStop> Stops { get; }
  property double AngleDegrees { get; }
  property double CenterX { get; }
  property double CenterY { get; }
  property double RadiusX { get; }
  property double RadiusY { get; }
```

#### GroupStyleView

```text
sealed class GroupStyleView
  ctor (double opacity)
  property double Opacity { get; }
```

#### ShadowUpdate

```text
sealed class ShadowUpdate
  ctor (ShadowValue? Value)
  property ShadowValue? Value { get; init; }
```

#### ShadowValue

```text
sealed class ShadowValue
  ctor (ColorRgba color, double opacity = 1, double angleDegrees = 45, double distancePx = 4, double blurRadiusPx = 8)
  property ColorRgba Color { get; }
  property double AngleDegrees { get; }
  property double BlurRadiusPx { get; }
  property double DistancePx { get; }
  property double Opacity { get; }
```

#### StrokeJoin

```text
enum StrokeJoin
  value Bevel = 2
  value Miter = 0
  value Round = 1
```

#### StrokeValue

```text
sealed class StrokeValue
  ctor (double widthPx, MaterialValue material, StrokeJoin join = StrokeJoin.Round)
  property MaterialValue Material { get; }
  property StrokeJoin Join { get; }
  property double WidthPx { get; }
```

#### StrokesUpdate

```text
sealed class StrokesUpdate
  ctor (ImmutableArray<StrokeValue> layers)
  property ImmutableArray<StrokeValue> Layers { get; }
```

#### StyleUpdate

```text
sealed class StyleUpdate
  ctor (AppearanceUpdate? unsung = null, AppearanceUpdate? sung = null, double opacityFactor = 1, WipeValue? wipe = null)
  method StyleUpdate Then(StyleUpdate next)
  method StyleView Apply(StyleView baseline)
  method StyleView ApplyMaterials(StyleView baseline)
  method static StyleUpdate Both(AppearanceUpdate appearance)
  property AppearanceUpdate Sung { get; }
  property AppearanceUpdate Unsung { get; }
  property WipeValue? Wipe { get; }
  property ImmutableArray<MaterialEdit> MaterialEdits { get; }
  property double OpacityFactor { get; }
```

#### StyleView

```text
sealed class StyleView
  ctor (AppearanceView unsung, AppearanceView sung, double opacity, WipeValue? wipe = null)
  method ColorRgba RequireSungSolidColor()
  method MaterialValue? Material(MaterialSlot slot)
  property AppearanceView Sung { get; }
  property AppearanceView Unsung { get; }
  property WipeValue Wipe { get; }
  property double Opacity { get; }
```

#### TransitionMode

```text
enum TransitionMode
  value Bridge = 1
  value Jump = 0
```

#### TextLineLoad

```text
sealed class TextLineLoad
  ctor (FrameLine line, TextLinePhase phase = TextLinePhase.Active, long? hideTimeUs = null, long exitDurationUs = 0, double exitProgress = 0)
  property FrameLine Line { get; }
  property LineTarget Target { get; }
  property TextLinePhase Phase { get; }
  property double ExitProgress { get; }
  property long ExitDurationUs { get; }
  property long? HideTimeUs { get; }
```

#### TextLinePhase

```text
enum TextLinePhase
  value Active = 0
  value ExitReplica = 1
```

#### WipeHighlightBlend

```text
enum WipeHighlightBlend
  value Add = 2
  value Normal = 0
  value Screen = 1
```

#### WipeHighlightValue

```text
sealed class WipeHighlightValue
  ctor (ColorRgba color, double widthRatio = 0.25, double opacity = 1, WipeHighlightBlend blend = WipeHighlightBlend.Screen)
  field const double MaxWidthRatio = 2
  property ColorRgba Color { get; }
  property WipeHighlightBlend Blend { get; }
  property double Opacity { get; }
  property double WidthRatio { get; }
```

#### WipeValue

```text
sealed class WipeValue
  ctor (double featherRatio = 0, WipeHighlightValue? highlight = null)
  field const double MaxFeatherRatio = 2
  property WipeHighlightValue? Highlight { get; }
  property double FeatherRatio { get; }
  property static WipeValue Hard { get; }
```

### 20.6 时间线与源查询

#### CursorTransitionEvent

```text
sealed class CursorTransitionEvent : TimelineEvent
  ctor (CursorTrackId trackId, CursorIdentity from, CursorIdentity to, long timeUs, CursorTransitionReason transitionReason, EventSourceOrder order, ReplayReason reason)
  property CursorIdentity From { get; }
  property CursorIdentity To { get; }
  property CursorTrackId TrackId { get; }
  property CursorTransitionReason TransitionReason { get; }
  property long GapUs { get; }
```

#### CursorTransitionReason

```text
enum CursorTransitionReason
  value BindingChange = 2
  value LineChange = 1
  value TokenChange = 0
```

#### EventSourceOrder

```text
struct EventSourceOrder
  ctor (int branch, int line = -1, int token = -1)
  property int Branch { get; }
  property int Line { get; }
  property int Token { get; }
  property static EventSourceOrder Global { get; }
```

#### ReplayReason

```text
enum ReplayReason
  value AnchorQuery = 5
  value Export = 4
  value Initial = 0
  value InputsChanged = 3
  value Loop = 2
  value Seek = 1
```

#### SourceFilter

```text
abstract class SourceFilter
```

#### SourceFilter.Branch

```text
sealed class SourceFilter.Branch : SourceFilter
  ctor (BranchId id)
  property BranchId Id { get; }
```

#### SourceFilter.Lines

```text
sealed class SourceFilter.Lines : SourceFilter
  ctor (ImmutableArray<LineId> ids)
  property ImmutableArray<LineId> Ids { get; }
```

#### SourceFilter.Project

```text
sealed class SourceFilter.Project : SourceFilter
  ctor ()
```

#### SourceFilter.Tokens

```text
sealed class SourceFilter.Tokens : SourceFilter
  ctor (ImmutableArray<TokenId> ids)
  property ImmutableArray<TokenId> Ids { get; }
```

#### TimelineEvent

```text
abstract class TimelineEvent
  method TimelineEvent WithReplayReason(ReplayReason reason)
  property EventId EventId { get; }
  property EventSourceOrder SourceOrder { get; }
  property ReplayReason ReplayReason { get; }
  property TimelineEventKind Kind { get; }
  property int BoundaryPriority { get; }
  property long TimeUs { get; }
  property static IComparer<TimelineEvent> Comparer { get; }
```

#### TimelineEventKind

```text
enum TimelineEventKind
  value AudioFeatureTick = 12
  value BindingEntered = 4
  value BindingLeft = 5
  value CursorTransition = 11
  value ElementHidden = 7
  value ElementVisible = 6
  value LeadInCompleted = 10
  value LeadInStarted = 8
  value LeadInStepChanged = 9
  value LinePerformanceCompleted = 3
  value LinePerformanceStarted = 2
  value TokenCompleted = 1
  value TokenStarted = 0
```

#### SourceCursorIndex

```text
sealed class SourceCursorIndex
  method ImmutableArray<PlaybackCursor> GetBaseAt(long timeUs, SourceFilter filter, CursorAnchorMode anchorMode)
  method ImmutableArray<SourceCursorSegment> GetSegments(CursorTrackId trackId, TimeRange range)
```

#### SourceCursorSegment

```text
sealed class SourceCursorSegment
  ctor (CursorTrackId trackId, CursorIdentity identity, TimeRange activeRange)
  property CursorIdentity Identity { get; }
  property CursorTrackId TrackId { get; }
  property TimeRange ActiveRange { get; }
```

#### SourceIndexes

```text
sealed class SourceIndexes
  property SourceCursorIndex Cursors { get; }
  property SourceLayoutIndex Layout { get; }
  property SourcePresentationIndex Presentations { get; }
  property TimelineIndex Timeline { get; }
```

#### SourceLayoutIndex

```text
sealed class SourceLayoutIndex
  method BaseSceneGeometry GetBaseAt(long timeUs, SourceFilter filter)
```

#### SourcePresentationIndex

```text
sealed class SourcePresentationIndex
  method SourcePresentationLoad? Get(BindingId bindingId)
  method ImmutableArray<SourcePresentationLoad> GetRange(TimeRange range, SourceFilter filter)
  method ImmutableArray<SourcePresentationSample> GetAt(long timeUs, SourceFilter filter)
```

#### SourcePresentationLoad

```text
sealed class SourcePresentationLoad
  ctor (FrameLine binding, long revealTimeUs, long? hideTimeUs, int loadOrdinal, TimeRange? preparationRange = null, TimeRange? layoutExitRange = null)
  method bool Contains(long timeUs)
  property FrameLine Binding { get; }
  property TimeRange? LayoutExitRange { get; }
  property TimeRange? PreparationRange { get; }
  property int LoadOrdinal { get; }
  property long RevealTimeUs { get; }
  property long? HideTimeUs { get; }
```

#### SourcePresentationSample

```text
sealed class SourcePresentationSample
  ctor (SourcePresentationLoad Load, long TimeUs, DisplayPhase DisplayPhase, double LayoutExitProgress)
  property DisplayPhase DisplayPhase { get; init; }
  property SourcePresentationLoad Load { get; init; }
  property double LayoutExitProgress { get; init; }
  property long TimeUs { get; init; }
```

#### TimelineIndex

```text
sealed class TimelineIndex
  ctor (IEnumerable<TimelineEvent> events, TimelineIndexBudget? budget = null, CancellationToken cancellationToken = default)
  method TimelineEvent? Last(TimelineSelector selector, long timeUs)
  method TimelineEvent? Next(TimelineSelector selector, long timeUs)
  method TimelineEvent? Nth(TimelineSelector selector, int zeroBasedIndex)
  method ImmutableArray<TimelineEvent> GetRange(TimelineSelector selector, TimeRange range)
  method int Count(TimelineSelector selector)
  method int Count(TimelineSelector selector, TimeRange range)
  method int Count(TimelineSelector selector, long timeUs)
  method int? Ordinal(TimelineSelector selector, EventId eventId)
  method int? Ordinal(TimelineSelector selector, OccurrenceId occurrenceId)
  property ImmutableArray<TimelineEvent> Events { get; }
  property static TimelineIndex Empty { get; }
```

#### TimelineIndexBudget

```text
sealed class TimelineIndexBudget
  ctor ()
  property int MaximumEvents { get; init; }
  property int MaximumKeys { get; init; }
  property int MaximumReferences { get; init; }
```

#### TimelineScope

```text
abstract class TimelineScope
```

#### TimelineScope.Binding

```text
sealed class TimelineScope.Binding : TimelineScope
  ctor (BindingId Id)
  property BindingId Id { get; init; }
```

#### TimelineScope.Branch

```text
sealed class TimelineScope.Branch : TimelineScope
  ctor (BranchId Id)
  property BranchId Id { get; init; }
```

#### TimelineScope.Cue

```text
sealed class TimelineScope.Cue : TimelineScope
  ctor (CueId Id)
  property CueId Id { get; init; }
```

#### TimelineScope.Line

```text
sealed class TimelineScope.Line : TimelineScope
  ctor (LineId Id)
  property LineId Id { get; init; }
```

#### TimelineScope.Node

```text
sealed class TimelineScope.Node : TimelineScope
  ctor (NodeId Id)
  property NodeId Id { get; init; }
```

#### TimelineScope.Occurrence

```text
sealed class TimelineScope.Occurrence : TimelineScope
  ctor (OccurrenceId Id)
  property OccurrenceId Id { get; init; }
```

#### TimelineScope.Project

```text
sealed class TimelineScope.Project : TimelineScope
  ctor ()
```

#### TimelineScope.Token

```text
sealed class TimelineScope.Token : TimelineScope
  ctor (TokenId Id)
  property TokenId Id { get; init; }
```

#### TimelineSelector

```text
sealed class TimelineSelector
  ctor (TimelineEventKind kind, TimelineScope? scope = null, string? sourceText = null)
  property TimelineEventKind Kind { get; }
  property TimelineScope Scope { get; }
  property string? SourceText { get; }
```

### 20.7 粒子计划与粒子值

#### BurstAnchor

```text
abstract class BurstAnchor
```

#### BurstAnchor.BindingCursors

```text
sealed class BurstAnchor.BindingCursors : BurstAnchor
  ctor (CursorAnchorMode Mode)
  property CursorAnchorMode Mode { get; init; }
```

#### BurstAnchor.SourceLine

```text
sealed class BurstAnchor.SourceLine : BurstAnchor
  ctor (LineId Id)
  property LineId Id { get; init; }
```

#### BurstAnchor.SourceToken

```text
sealed class BurstAnchor.SourceToken : BurstAnchor
  ctor (TokenId Id)
  property TokenId Id { get; init; }
```

#### BurstAnchor.TextMark

```text
sealed class BurstAnchor.TextMark : BurstAnchor
  ctor (NodeId Id)
  property NodeId Id { get; init; }
```

#### ParticleAgeCurve

```text
sealed class ParticleAgeCurve
  ctor (double start, double end, CurveValue curve)
  method double Evaluate(double age)
  property CurveValue Curve { get; }
  property double End { get; }
  property double Start { get; }
  property static ParticleAgeCurve ConstantOne { get; }
```

#### ParticleDescription

```text
abstract class ParticleDescription
  method virtual void Validate()
  property required ColorRgba Color { get; init; }
  property OverlayLayer Layer { get; init; }
  property ParticleMotion Motion { get; init; }
  property ParticleRenderMode Rendering { get; init; }
  property Vector2 Velocity { get; init; }
  property required double SizePx { get; init; }
  property double SpreadPx { get; init; }
  property required long LifetimeUs { get; init; }
  property long TrailDurationUs { get; init; }
  property required string SeedStreamKey { get; init; }
```

#### ParticleMotion

```text
sealed class ParticleMotion
  ctor ()
  property ParticleAgeCurve Opacity { get; init; }
  property ParticleAgeCurve Size { get; init; }
  property Vector2 Acceleration { get; init; }
  property static ParticleMotion ConstantVelocity { get; }
```

#### ParticleRenderMode

```text
enum ParticleRenderMode
  value Custom = 1
  value Dots = 0
```

#### ParticleSource

```text
abstract class ParticleSource
  property CursorTrackId? TrackId { get; }
  property abstract BindingId BindingId { get; }
```

#### ParticleSource.Bridge

```text
sealed class ParticleSource.Bridge : ParticleSource
  ctor (CursorTransitionEvent transition, TimeRange flightRange)
  property CursorIdentity From { get; }
  property CursorIdentity To { get; }
  property CursorTrackId Track { get; }
  property EventId TransitionId { get; }
  property TimeRange FlightRange { get; }
  property virtual BindingId BindingId { get; }
```

#### ParticleSource.Cursor

```text
sealed class ParticleSource.Cursor : ParticleSource
  ctor (CursorTrackId trackId, CursorIdentity identity)
  property CursorIdentity Identity { get; }
  property CursorTrackId Track { get; }
  property virtual BindingId BindingId { get; }
```

#### ParticleSource.Line

```text
sealed class ParticleSource.Line : ParticleSource
  ctor (LineTarget target)
  property LineTarget Target { get; }
  property virtual BindingId BindingId { get; }
```

#### ParticleSource.TextMark

```text
sealed class ParticleSource.TextMark : ParticleSource
  ctor (TextMarkTarget target)
  property TextMarkTarget Target { get; }
  property virtual BindingId BindingId { get; }
```

#### ParticleSource.Token

```text
sealed class ParticleSource.Token : ParticleSource
  ctor (TokenFamilyTarget target)
  property TokenFamilyTarget Target { get; }
  property virtual BindingId BindingId { get; }
```

#### ParticleTrailSample

```text
struct ParticleTrailSample
  ctor (long TimeUs, Point2 Position)
  property Point2 Position { get; init; }
  property long TimeUs { get; init; }
```

#### BirthWindow

```text
struct BirthWindow
  ctor (long startInclusiveUs, long endInclusiveUs)
  method bool Contains(long timeUs)
  property long EndInclusiveUs { get; }
  property long StartInclusiveUs { get; }
```

#### BridgePolicy

```text
sealed class BridgePolicy
  ctor (TransitionMode Mode, long DurationUs, long GapLimitUs, CurveValue Curve)
  property CurveValue Curve { get; init; }
  property TransitionMode Mode { get; init; }
  property long DurationUs { get; init; }
  property long GapLimitUs { get; init; }
  property static BridgePolicy Jump { get; }
```

#### BurstEventRow

```text
sealed class BurstEventRow
  ctor (EventId EventId, int Count, BurstAnchor Anchor, ParticleSpec Particle)
  property BurstAnchor Anchor { get; init; }
  property EventId EventId { get; init; }
  property ParticleSpec Particle { get; init; }
  property int Count { get; init; }
```

#### CursorEmissionPlan

```text
sealed class CursorEmissionPlan
  ctor (EmitterId Id, RateTrack Rate, ParticleSpec Particle, CursorAnchorMode AnchorMode, BridgePolicy Bridge)
  property CursorAnchorMode AnchorMode { get; init; }
  property EmitterId Id { get; init; }
  property BridgePolicy Bridge { get; init; }
  property ParticleSpec Particle { get; init; }
  property RateTrack Rate { get; init; }
```

#### EventBurstPlan

```text
sealed class EventBurstPlan
  ctor (EmitterId Id, TimelineSelector Selector, ImmutableArray<BurstEventRow> Rows)
  property EmitterId Id { get; init; }
  property TimelineSelector Selector { get; init; }
  property ImmutableArray<BurstEventRow> Rows { get; init; }
```

#### GeometrySamplingMode

```text
enum GeometrySamplingMode
  value Continuous120Hz = 1
  value Exact = 0
```

#### GeometrySamplingPolicy

```text
sealed class GeometrySamplingPolicy
  ctor (GeometrySamplingMode Mode, ImmutableArray<TimeRange> ContinuousRanges, ImmutableArray<long> DiscontinuitiesUs)
  property GeometrySamplingMode Mode { get; init; }
  property ImmutableArray<TimeRange> ContinuousRanges { get; init; }
  property ImmutableArray<long> DiscontinuitiesUs { get; init; }
  property static GeometrySamplingPolicy Exact { get; }
```

#### ParticleBirth

```text
sealed class ParticleBirth
  ctor (ParticleIdentity Id, ParticleSource Source, EventId? EventId, long BirthUs, long DeathUs, Point2 BirthPosition, Vector2 InitialVelocity, ParticleSpec Spec)
  property EventId? EventId { get; init; }
  property ParticleSource Source { get; init; }
  property Point2 BirthPosition { get; init; }
  property ParticleIdentity Id { get; init; }
  property ParticleSpec Spec { get; init; }
  property Vector2 InitialVelocity { get; init; }
  property long BirthUs { get; init; }
  property long DeathUs { get; init; }
```

#### ParticleBirthWindow

```text
sealed class ParticleBirthWindow
  ctor (BirthWindow Window, ImmutableArray<ParticleBirth> Births)
  property BirthWindow Window { get; init; }
  property ImmutableArray<ParticleBirth> Births { get; init; }
```

#### ParticleFrame

```text
sealed class ParticleFrame
  method ImmutableArray<ParticleTrailSample> GetTrail(ParticleIdentity id)
  property ParticleBirthWindow Births { get; }
  property ImmutableArray<ParticleView> Alive { get; }
```

#### ParticleHead

```text
abstract class ParticleHead
```

#### ParticleHead.Bridge

```text
sealed class ParticleHead.Bridge : ParticleHead
  ctor (ParticleSource.Bridge Source)
  property ParticleSource.Bridge Source { get; init; }
```

#### ParticleHead.Normal

```text
sealed class ParticleHead.Normal : ParticleHead
  ctor (CursorTrackId Track, BindingId Binding)
  property BindingId Binding { get; init; }
  property CursorTrackId Track { get; init; }
```

#### ParticleIdentity

```text
sealed class ParticleIdentity
  ctor (EffectInstanceId InstanceId, EmitterId EmitterId, ParticleOrigin Origin)
  property EffectInstanceId InstanceId { get; init; }
  property EmitterId EmitterId { get; init; }
  property ParticleOrigin Origin { get; init; }
```

#### ParticleLimits

```text
static class ParticleLimits
  field const int MaximumAlive = 20000
  field const int MaximumAnchorTimes = 4096
  field const int MaximumBirths = 50000
  field const int MaximumBurstRows = 50000
  field const int MaximumEmitters = 32
  field const int MaximumFutureEndpoints = 1024
  field const int MaximumIntegerBits = 4096
  field const int MaximumInversionOperations = 4000000
  field const int MaximumMemoEntries = 2048
  field const int MaximumRateElements = 4096
  field const int MaximumSourceIntervals = 200000
  field const int MaximumTextCalls = 32768
  field const int MaximumTrailNodes = 200000
  field const long MaximumAnchorWallMilliseconds = 15000
  field const long MaximumFrameBytes = 67108864
  field const long MaximumLifetimeUs = 5000000
  field const long MaximumMemoBytes = 67108864
  field const long MaximumPrepareBytes = 268435456
```

#### ParticleOrigin

```text
abstract class ParticleOrigin
```

#### ParticleOrigin.Burst

```text
sealed class ParticleOrigin.Burst : ParticleOrigin
  ctor (EventId EventId, int PlanOrdinal, ParticleSource Anchor, int ParticleOrdinal)
  property EventId EventId { get; init; }
  property ParticleSource Anchor { get; init; }
  property int ParticleOrdinal { get; init; }
  property int PlanOrdinal { get; init; }
```

#### ParticleOrigin.Continuous

```text
sealed class ParticleOrigin.Continuous : ParticleOrigin
  ctor (ParticleHead Head, long EpisodeStartUs, ulong SequenceStartingAt1)
  property ParticleHead Head { get; init; }
  property long EpisodeStartUs { get; init; }
  property ulong SequenceStartingAt1 { get; init; }
```

#### ParticleSchedule

```text
sealed class ParticleSchedule
  ctor (ImmutableArray<CursorEmissionPlan> Continuous, ImmutableArray<EventBurstPlan> Bursts)
  property ImmutableArray<CursorEmissionPlan> Continuous { get; init; }
  property ImmutableArray<EventBurstPlan> Bursts { get; init; }
  property static ParticleSchedule Empty { get; }
```

#### ParticleSpec

```text
sealed class ParticleSpec : ParticleDescription
  ctor ()
  method virtual void Validate()
```

#### ParticleView

```text
sealed class ParticleView
  ctor (ParticleBirth Birth, Point2 Position, double SizePx, ColorRgba Color)
  property ColorRgba Color { get; init; }
  property OverlayLayer Layer { get; }
  property ParticleRenderMode Rendering { get; }
  property ParticleSource Source { get; }
  property Point2 Position { get; init; }
  property ParticleBirth Birth { get; init; }
  property ParticleIdentity Id { get; }
  property double SizePx { get; init; }
  property long BirthUs { get; }
  property long DeathUs { get; }
```

#### RateSegment

```text
sealed class RateSegment
  ctor (TimeRange Range, double StartPerSecond, double EndPerSecond)
  property TimeRange Range { get; init; }
  property double EndPerSecond { get; init; }
  property double StartPerSecond { get; init; }
```

#### RateTrack

```text
sealed class RateTrack
  ctor (ImmutableArray<RateSegment> Segments, ImmutableArray<TimeRange> EnabledRanges)
  property ImmutableArray<TimeRange> EnabledRanges { get; init; }
  property ImmutableArray<RateSegment> Segments { get; init; }
```

### 20.8 音频与媒体特征

#### AudioAvailability

```text
enum AudioAvailability
  value AnalysisFailed = 2
  value Available = 3
  value NoAudio = 0
  value NotAnalyzed = 1
  value OutsideRange = 4
```

#### AudioFeaturePoint

```text
sealed class AudioFeaturePoint
  ctor (long timeUs, AudioFeatureValues values)
  property AudioFeatureValues Values { get; }
  property long TimeUs { get; }
```

#### AudioFeatureRange

```text
sealed class AudioFeatureRange
  ctor (TimeRange RequestedRange, TimeRange? AvailableRange, AudioAvailability Availability, ImmutableArray<AudioFeaturePoint> Points, string? DiagnosticCode)
  property AudioAvailability Availability { get; init; }
  property TimeRange RequestedRange { get; init; }
  property TimeRange? AvailableRange { get; init; }
  property ImmutableArray<AudioFeaturePoint> Points { get; init; }
  property string? DiagnosticCode { get; init; }
```

#### AudioFeatureSample

```text
sealed class AudioFeatureSample
  ctor (long TimeUs, AudioAvailability Availability, AudioFeatureValues? Values, string? DiagnosticCode)
  property AudioAvailability Availability { get; init; }
  property AudioFeatureValues? Values { get; init; }
  property long TimeUs { get; init; }
  property string? DiagnosticCode { get; init; }
```

#### AudioFeatureValues

```text
struct AudioFeatureValues
  ctor (double bassLevel, double pulse, double? beatPhase, double beatConfidence)
  property double BassLevel { get; }
  property double BeatConfidence { get; }
  property double Pulse { get; }
  property double? BeatPhase { get; }
```

#### AudioFeatures

```text
sealed class AudioFeatures
  ctor (string revision, long durationUs, ImmutableArray<AudioFeaturePoint> points)
  method AudioFeatureRange GetRange(TimeRange range)
  method AudioFeatureSample GetAt(long timeUs)
  method static AudioFeatures Unavailable(AudioAvailability availability, string diagnosticCode)
  property AudioAvailability Availability { get; }
  property long? DurationUs { get; }
  property string? Revision { get; }
  property static AudioFeatures NotAnalyzed { get; }
```

#### CanvasMediaFeatureValues

```text
sealed class CanvasMediaFeatureValues
  property LinearPremulMean Mean { get; }
  property bool IsOpaque { get; }
  property double MeanPremulY { get; }
  property double MeanSquaredPremulY { get; }
  property double PremulYVariance { get; }
```

#### LinearPremulMean

```text
struct LinearPremulMean
  ctor (double r, double g, double b, double alpha)
  property double Alpha { get; }
  property double B { get; }
  property double G { get; }
  property double R { get; }
```

#### MediaCoordinateSpace

```text
enum MediaCoordinateSpace
  value CanvasPixels = 1
  value SourceNormalized = 0
```

#### MediaFeatureInputInfo

```text
sealed class MediaFeatureInputInfo
  property MediaInputState State { get; }
  property string InputRevision { get; }
  property string MappingKey { get; }
  property string ProfileDigest { get; }
  property string? ArtifactHash { get; }
  property string? DiagnosticCode { get; }
  property string? NoMediaReason { get; }
  property string? SourceKey { get; }
```

#### MediaFeatureSample

```text
sealed class MediaFeatureSample
  property Rect2 Rect { get; }
  property CanvasMediaFeatureValues? CanvasValues { get; }
  property MediaFeatureInputInfo Input { get; }
  property MediaQueryProfile Query { get; }
  property SourceMediaFeatureValues? SourceValues { get; }
  property long ProjectTimeUs { get; }
```

#### MediaFeatures

```text
sealed class MediaFeatures
  method MediaFeatureSample Sample(MediaQueryId query, Rect2 rect, long projectTimeUs)
  property MediaQueryCatalog Catalog { get; }
```

#### MediaInputState

```text
enum MediaInputState
  value Available = 2
  value NoMedia = 0
  value Pending = 1
  value Unavailable = 3
```

#### MediaQueryCatalog

```text
sealed class MediaQueryCatalog
  method MediaQueryId GetId(string declaredId)
  property MediaFeatureInputInfo? Input { get; }
  property ImmutableArray<MediaQueryProfile> Profiles { get; }
```

#### MediaQueryId

```text
struct MediaQueryId
  property string? Value { get; }
```

#### MediaQueryProfile

```text
sealed class MediaQueryProfile
  property MediaCoordinateSpace Space { get; }
  property MediaQueryId Id { get; }
  property long WindowUs { get; }
```

#### SourceMediaFeatureValues

```text
sealed class SourceMediaFeatureValues
  property LinearPremulMean Mean { get; }
  property double MeanAlphaTimesPremulY { get; }
  property double MeanPremulY { get; }
  property double MeanSquaredAlpha { get; }
  property double MeanSquaredPremulY { get; }
  property double PremulYVariance { get; }
```

### 20.9 资源、图、uniform 与图元

#### AtlasSprite

```text
struct AtlasSprite
  ctor (SpriteHandle sprite, Affine2 unitToWorld, ColorRgba tint)
  property Affine2 UnitToWorld { get; }
  property ColorRgba Tint { get; }
  property SpriteHandle Sprite { get; }
  property bool IsValid { get; }
```

#### Dot

```text
struct Dot
  ctor (Point2 position, double radius, ColorRgba color)
  property ColorRgba Color { get; }
  property Point2 Position { get; }
  property bool IsValid { get; }
  property double Radius { get; }
```

#### GraphBindingValue

```text
sealed class GraphBindingValue
  ctor (GraphHandle graph, ParameterValues parameters, GraphUniformValues uniforms, GraphTransformValues transforms)
  property GraphHandle Graph { get; }
  property GraphTransformValues Transforms { get; }
  property GraphUniformValues Uniforms { get; }
  property ParameterValues Parameters { get; }
```

#### GraphHandle

```text
sealed class GraphHandle
  property ResourceIdentity Identity { get; }
```

#### GraphTransformValue

```text
sealed class GraphTransformValue
  ctor (Affine2 matrix, Rect2 outputBounds)
  property Affine2 Matrix { get; }
  property Rect2 OutputBounds { get; }
```

#### GraphTransformValues

```text
sealed class GraphTransformValues
  ctor (IEnumerable<KeyValuePair<string, GraphTransformValue>> values)
  property ImmutableDictionary<string, GraphTransformValue> Values { get; }
  property static GraphTransformValues Empty { get; }
```

#### GraphUniformValues

```text
sealed class GraphUniformValues
  ctor (IEnumerable<KeyValuePair<ValueTuple<string, string>, UniformValue>> values)
  property ImmutableDictionary<ValueTuple<string, string>, UniformValue> Values { get; }
  property static GraphUniformValues Empty { get; }
```

#### ImageHandle

```text
sealed class ImageHandle
  property ResourceIdentity Identity { get; }
```

#### MaterialEdit

```text
abstract class MaterialEdit
```

#### MaterialEdit.ReplaceMaterial

```text
sealed class MaterialEdit.ReplaceMaterial : MaterialEdit
  ctor (MaterialSlot Slot, MaterialValue? Material)
  property MaterialSlot Slot { get; init; }
  property MaterialValue? Material { get; init; }
```

#### MaterialEdit.Uniform

```text
sealed class MaterialEdit.Uniform : MaterialEdit
  ctor (MaterialSlot Slot, MaterialHandle Handle, string NodeId, string Name, UniformValue Value)
  property MaterialHandle Handle { get; init; }
  property MaterialSlot Slot { get; init; }
  property UniformValue Value { get; init; }
  property string Name { get; init; }
  property string NodeId { get; init; }
```

#### MaterialHandle

```text
sealed class MaterialHandle
  property ResourceIdentity Identity { get; }
```

#### MaterialSlot

```text
enum MaterialSlot
  value SungFill = 1
  value SungStroke = 3
  value UnsungFill = 0
  value UnsungStroke = 2
```

#### MaterialValue

```text
abstract class MaterialValue
  method static MaterialValue Gradient(GradientValue gradient)
  method static MaterialValue PackageExport(MaterialHandle handle, ParameterValues parameters)
  method static MaterialValue Solid(ColorRgba color)
```

#### MaterialValue.GradientMaterial

```text
sealed class MaterialValue.GradientMaterial : MaterialValue
  property GradientValue Value { get; }
```

#### MaterialValue.PackageMaterial

```text
sealed class MaterialValue.PackageMaterial : MaterialValue
  property GraphUniformValues Uniforms { get; }
  property MaterialHandle Handle { get; }
  property ParameterValues Parameters { get; }
```

#### MaterialValue.SolidMaterial

```text
sealed class MaterialValue.SolidMaterial : MaterialValue
  property ColorRgba Color { get; }
```

#### OutputMode

```text
enum OutputMode
  value Opaque = 0
  value Transparent = 1
```

#### OverlayLayer

```text
enum OverlayLayer
  value AboveText = 1
  value BehindText = 0
```

#### ResourceCatalog

```text
sealed class ResourceCatalog
  ctor (string packageId, string revision, ImmutableArray<string> materials, ImmutableArray<string> graphs, ImmutableArray<string> images)
  ctor (string packageId, string revision, ImmutableArray<string> materials, ImmutableArray<string> graphs, ImmutableArray<string> images, ImmutableArray<string> vectors, ImmutableDictionary<string, ImmutableArray<string>> sprites)
  method GraphHandle GetGraph(string exportId)
  method ImageHandle GetImage(string assetId)
  method MaterialHandle GetMaterial(string exportId)
  method SpriteHandle GetSprite(string resourceId, string variantId)
  method VectorHandle GetVector(string resourceId)
  method bool Contains(GraphHandle handle)
  method bool Contains(ImageHandle handle)
  method bool Contains(MaterialHandle handle)
  method bool Contains(SpriteHandle handle)
  method bool Contains(VectorHandle handle)
  property string? PackageId { get; }
  property string? Revision { get; }
  property static ResourceCatalog Empty { get; }
```

#### ResourceIdentity

```text
sealed class ResourceIdentity
  ctor (string packageId, string revision, string exportId)
  property string ExportId { get; }
  property string PackageId { get; }
  property string Revision { get; }
```

#### Segment

```text
struct Segment
  ctor (Point2 start, Point2 end, double width, ColorRgba color)
  property ColorRgba Color { get; }
  property Point2 End { get; }
  property Point2 Start { get; }
  property bool IsValid { get; }
  property double Width { get; }
```

#### Sprite

```text
struct Sprite
  ctor (ImageHandle image, Affine2 unitToWorld, ColorRgba tint, Rect2? source = null)
  property Affine2 UnitToWorld { get; }
  property ColorRgba Tint { get; }
  property ImageHandle Image { get; }
  property Rect2 Source { get; }
  property bool IsValid { get; }
```

#### SpriteHandle

```text
sealed class SpriteHandle
  property ResourceIdentity Identity { get; }
  property string VariantId { get; }
```

#### Trail

```text
sealed class Trail
  ctor (ImmutableArray<TrailPoint> points)
  property ImmutableArray<TrailPoint> Points { get; }
```

#### TrailPoint

```text
struct TrailPoint
  ctor (Point2 position, double width, ColorRgba color)
  property ColorRgba Color { get; }
  property Point2 Position { get; }
  property bool IsValid { get; }
  property double Width { get; }
```

#### UniformValue

```text
sealed class UniformValue
  method static UniformValue Array(IEnumerable<UniformValue> values)
  method static UniformValue Color(ColorRgba value)
  method static UniformValue Float(double value)
  method static UniformValue Float2(double x, double y)
  method static UniformValue Float3(double x, double y, double z)
  method static UniformValue Float4(double x, double y, double z, double w)
  method static UniformValue Int(int value)
  method static UniformValue Matrix3x3(double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33)
  property UniformValueKind Kind { get; }
  property ImmutableArray<double> Numbers { get; }
  property ImmutableArray<int> Integers { get; }
  property int? ArrayLength { get; }
```

#### UniformValueKind

```text
enum UniformValueKind
  value Color = 5
  value Float = 0
  value Float2 = 1
  value Float3 = 2
  value Float4 = 3
  value Int = 4
  value Matrix3x3 = 6
```

#### VisualOutputDeclaration

```text
sealed class VisualOutputDeclaration
  ctor (OutputId Id, VisualPrimitiveKind Primitive, OverlayLayer Layer, VisualResourceKind Resource = VisualResourceKind.None)
  property OutputId Id { get; init; }
  property OverlayLayer Layer { get; init; }
  property VisualPrimitiveKind Primitive { get; init; }
  property VisualResourceKind Resource { get; init; }
```

#### VisualPrimitiveKind

```text
enum VisualPrimitiveKind
  value Dots = 0
  value Paths = 4
  value Segments = 1
  value Sprites = 3
  value Trails = 2
```

#### VisualResourceKind

```text
enum VisualResourceKind
  value None = 0
  value SvgAtlas = 2
  value SvgVector = 1
```

#### VectorHandle

```text
sealed class VectorHandle
  property ResourceIdentity Identity { get; }
```

#### VectorVisual

```text
struct VectorVisual
  ctor (VectorHandle vector, Affine2 unitToWorld, ColorRgba tint)
  property Affine2 UnitToWorld { get; }
  property ColorRgba Tint { get; }
  property VectorHandle Vector { get; }
  property bool IsValid { get; }
```

### 20.10 曲线便捷方法与事件载荷补充

`Curves` 提供以下便捷成员：

```text
static class Curves
  static CurveValue LinearCurve { get; }
  static CurveValue EaseOutCurve { get; }
  static CurveValue EaseInOutCurve { get; }
  static double EaseOut(double progress)
```

时间线的具体事件类型继承 `TimelineEvent`，可在查询后用模式匹配读取以下载荷：

```text
abstract record TokenPerformanceEvent : TimelineEvent
  SourceToken Source { get; }
  TokenId TokenId { get; }
  LineId LineId { get; }
  BranchId BranchId { get; }
  OccurrenceId OccurrenceId { get; }
sealed record TokenStarted : TokenPerformanceEvent
  ctor (SourceToken token, EventSourceOrder order, ReplayReason reason)
sealed record TokenCompleted : TokenPerformanceEvent
  ctor (SourceToken token, EventSourceOrder order, ReplayReason reason)

abstract record LinePerformanceEvent : TimelineEvent
  SourceLine Source { get; }
  LineId LineId { get; }
  BranchId BranchId { get; }
  TimeRange PerformanceRange { get; }
  ProgressSource ProgressSource { get; }
sealed record LinePerformanceStarted : LinePerformanceEvent
  ctor (SourceLine line, TimeRange range, ProgressSource source, EventSourceOrder order, ReplayReason reason)
sealed record LinePerformanceCompleted : LinePerformanceEvent
  ctor (SourceLine line, TimeRange range, ProgressSource source, EventSourceOrder order, ReplayReason reason)

abstract record BindingTimelineEvent : TimelineEvent
  FrameLine Binding { get; }
sealed record BindingEntered : BindingTimelineEvent
  ctor (FrameLine binding, long timeUs, EventSourceOrder order, ReplayReason reason)
sealed record BindingLeft : BindingTimelineEvent
  ctor (FrameLine binding, long timeUs, EventSourceOrder order, ReplayReason reason)

abstract record SceneElementIdentity
  NodeId NodeId { get; }
sealed record SceneElementIdentity.Lyric : SceneElementIdentity
  ctor (NodeId nodeId, FrameLine binding, RubyId? rubyId = null)
  FrameLine Binding { get; }
  RubyId? RubyId { get; }
sealed record SceneElementIdentity.Mark : SceneElementIdentity
  ctor (NodeId nodeId, CueId cueId, FrameLine target)
  CueId CueId { get; }
  FrameLine Target { get; }
sealed record SceneElementIdentity.Group : SceneElementIdentity
  ctor (NodeId nodeId, BranchId? branchId)
  BranchId? BranchId { get; }
abstract record ElementTimelineEvent : TimelineEvent
  SceneElementIdentity Element { get; }
sealed record ElementVisible : ElementTimelineEvent
  ctor (SceneElementIdentity element, long timeUs, EventSourceOrder order, ReplayReason reason)
sealed record ElementHidden : ElementTimelineEvent
  ctor (SceneElementIdentity element, long timeUs, EventSourceOrder order, ReplayReason reason)

sealed record LeadInEventTarget
  ctor (SourceCue cue, FrameLine target, int markCount)
  SourceCue Cue { get; }
  FrameLine Target { get; }
  int MarkCount { get; }
abstract record LeadInTimelineEvent : TimelineEvent
  LeadInEventTarget Target { get; }
  int ElapsedMarks { get; }
  CueId CueId { get; }
sealed record LeadInStarted : LeadInTimelineEvent
  ctor (LeadInEventTarget target, EventSourceOrder order, ReplayReason reason)
sealed record LeadInStepChanged : LeadInTimelineEvent
  ctor (LeadInEventTarget target, long timeUs, int elapsedMarks, EventSourceOrder order, ReplayReason reason)
sealed record LeadInCompleted : LeadInTimelineEvent
  ctor (LeadInEventTarget target, EventSourceOrder order, ReplayReason reason)

sealed record AudioFeatureTick : TimelineEvent
  ctor (string revision, AudioFeaturePoint point, ReplayReason reason)
  string AnalysisRevision { get; }
  AudioFeaturePoint Point { get; }
```

实例读取配置使用以下订阅值；它在实例建立时固定：

```text
SourceSubscription.BoundSources()
SourceSubscription.Project()
SourceSubscription.Branch(BranchId id)
```

## 21. 示例与源码索引

### 21.1 本仓库示例包

本仓库的 8 个官方示例包位于 [`packages/`](../packages/)，各特性、API 与包文件的详细映射参见 [API 与示例包对照索引](api-sample-mapping.md)。

### 21.2 主仓库内置参考样例（`MakoKaraGenerator-App`）

主仓库 `MakoKaraGenerator-App/backend/samples/ScriptEffectsV2/` 中另包含以下基础特性对照目录：

| 包 | 编写参考（主仓库路径） |
| --- | --- |
| `WordEmphasis` | 逐字进度、字族缩放、唱完回落（`backend/samples/ScriptEffectsV2/WordEmphasis`） |
| `RepeatEmphasis` | 同文本演唱次数、范围选择、已唱填充（`backend/samples/ScriptEffectsV2/RepeatEmphasis`） |
| `ClusterExit` | 基础字和注音字簇错峰退场、整行变换（`backend/samples/ScriptEffectsV2/ClusterExit`） |
| `WaveWarp` | 按当前进度生成局部网格、确定 Warp 范围（`backend/samples/ScriptEffectsV2/WaveWarp`） |
| `CursorTrail` | 连续发射、分段速率、Bridge、自动圆点（`backend/samples/ScriptEffectsV2/CursorTrail`） |
| `EventBurst` | 真实事件、Burst 计划与四类锚点（`backend/samples/ScriptEffectsV2/EventBurst`） |
| `VectorTrail` | 自定义粒子、圆点、线段、带状拖尾与图片精灵（`backend/samples/ScriptEffectsV2/VectorTrail`） |
| `SpriteTrail` | 图片参数、粒子旋转与拖尾淡出（`backend/samples/ScriptEffectsV2/SpriteTrail`） |
| `StarSweep` | 最终播放头、SVG 矢量和跟随图元（`backend/samples/ScriptEffectsV2/StarSweep`） |
| `AdaptiveStroke` | 背景媒体特征、亮度和纹理自适应描边（`backend/samples/ScriptEffectsV2/AdaptiveStroke`） |
| `CustomInk` | JSON 与 SkSL 材质包（`backend/samples/ScriptEffectsV2/CustomInk`） |
| `LineGlitch` | 图效果、图输入、整行图像处理（`backend/samples/ScriptEffectsV2/LineGlitch`） |
| `GlassInk` | 背景媒体、真实下层、区域覆盖与旁路（`backend/samples/ScriptEffectsV2/GlassInk`） |

### 21.3 主仓库源码与进阶阅读（`MakoKaraGenerator-App`）

正文中的格式、签名和边界已包含在本文件。以下路径均位于主仓库 `MakoKaraGenerator-App/`，用于核查源码与集成应用：

- SDK 入口与准备数据：`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/PureEffect.cs`
- 文字输出与几何值：`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TextOutput.cs`、`backend/src/MakoKaraGenerator.Scripting.Abstractions/`
- 固定源索引与时间线：`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/SourceIndexes.cs`、`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/TimelineIndex.cs`
- 粒子计划与晚期绘制：`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/ParticleSchedule.cs`、`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/VisualOutput.cs`
- 包、参数与能力版本：`backend/src/MakoKaraGenerator.Core/Effects/EffectPackageProfiles.cs`、`backend/src/MakoKaraGenerator.Core/Effects/EffectPackageParameterParser.cs`
- 图、程序与 SVG 格式：`backend/src/MakoKaraGenerator.Core/Effects/EffectPackageImageGraphs.cs`、`backend/src/MakoKaraGenerator.Core/Effects/EffectPackageShaderPrograms.cs`、`backend/src/MakoKaraGenerator.Core/Effects/EffectPackageSvgResources.Parser.cs`
- 媒体查询与音频特征：`backend/src/MakoKaraGenerator.Scripting.Abstractions/V2/MediaFeatures.cs`、`backend/src/MakoKaraGenerator.Scripting.Abstractions/AudioFeatures.cs`
- 公开签名核对文件：`backend/tests/MakoKaraGenerator.Scripting.Tests/Snapshots/sdk-v2-public-surface.txt`
- HTTP 作者接口参考：`backend/docs/style-reference/http-authoring.md`
