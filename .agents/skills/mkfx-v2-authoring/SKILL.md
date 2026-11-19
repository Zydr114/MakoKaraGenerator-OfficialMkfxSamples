---
name: mkfx-v2-authoring
description: >-
  Guide for designing, implementing, debugging, packing, and Vulkan-verifying
  MakoKaraGenerator SDK v2 (.mkfx) effect packages in this repository. Use when
  creating or editing C# controllers, ImageGraphs, Ruby/cluster animations,
  per-token exits, or running pack.py and tools/Verify.
---

# MKFX SDK v2 效果包编写与验证工作流

在编写或修改 `packages/<PackageName>/` 下的 SDK v2（`packageVersion: 3`、`sdkVersion: "2.0.0"`）效果包时遵循本流程。完整原理与代码示例见 [mkfx-v2-authoring.md](../../../docs/mkfx-v2-authoring.md)，API 细则见 [script-sdk-v2.md](../../../docs/script-sdk-v2.md) 与 [api-sample-mapping.md](../../../docs/api-sample-mapping.md)。

## 1. 标准开发与验证步骤

1. **检查包结构与能力声明**：
   - 清单固定字段：`packageVersion: 3`、`sdkVersion: "2.0.0"`、`shaderProfile: "skia-ganesh-3.119.4-v2"`。
   - `manifest.json` 根级 `requires` 与每个导出的 `outputs[].requires` / `kind` / `target` 必须一一对应；同一包内所有文件的路径与 `sha256` 均须列入 `files`。
2. **注册验证场景**：
   - 新增包目录后，**必须**在 [Scenarios.cs](../../../tools/Verify/Scenarios.cs) 的 `Scenarios.All` 中登记至少一个验证场景（[Program.cs](../../../tools/Verify/Program.cs) 会对缺失覆盖的包直接返回退出码 2）。
   - `Program.cs` 会直接拿 `scenario.Apply(baseline)` 与无效果 `baseline` 做逐像素对比：因此 `default` 变体应保持原布局与歌词不变，以确保 `变化像素` 反映效果本身；若效果依赖注音（Ruby）或句尾退场副本（`line.exit`），务必另增带 Ruby 的变体并精确配置过渡时刻采样点（如行显现后 `250ms`/`420ms`、行隐藏后 `200ms`/`400ms` 如 `5_200_000` us）。
3. **打包与校验清单**：
   ```bash
   python3 tools/pack.py <PackageName>     # 更新 sha256 并导出 dist/<PackageName>.mkfx
   python3 tools/pack.py --check           # 校验全部包的合同、文件闭包与摘要
   ```
4. **编译准入与 Vulkan GPU 渲染验证**：
   ```bash
   dotnet build -c Release --artifacts-path .artifacts tools/Verify/Verify.csproj
   dotnet .artifacts/bin/Verify/release/Verify.dll --root . --only <PackageName>
   ```
   - 确认无 `Error` 编译/运行诊断，且 `normal` 与 `transparent` 输出均有合理 `变化像素`。
5. **图像目视验收**：
   - 查看 `out/<PackageName>/*.png`（普通背景与透明背景）。重点检查：**同行后续未退出文字是否被意外裁切**、旋转/缩放支点是否正确、分段与兜底注音是否独立响应。

## 2. 纯度分析器与零分配规范（`MKV2001`–`MKV2009`）

- **禁止 `out`/`ref` 与元组解构（`MKV2003` Error）**：
  - `Evaluate` 中不可调用 `dict.TryGetValue(k, out var v)`（改用 `dict.ContainsKey(k) ? dict[k] : fallback`），也**不可编写带 `out` 参数的辅助方法**或使用**元组解构**（如 `var (a, b) = Helper(...)` 同样触发 `MKV2003`）。
  - **解法**：需要多返回值时定义轻量只读值类型 `readonly record struct`（如 [WordFlipTransition.cs](../../../packages/WordFlipTransition/src/WordFlipTransition.cs) 的 `TokenHorizontalOrder`、`FlipSchedule`），直接读取属性，既合规又零堆分配。
- **静态表与资源全在 `Declare` 预建（`MKV2008` / `MKV2009`）**：
  - 在 `Declare` 构建只读索引表，如 `ImmutableDictionary<TokenId, ulong>`（确定性随机序号）、`ImmutableDictionary<RubyId, ImmutableArray<RubySegmentSpan>>`（注音分段表）、`ImmutableDictionary<LineId, long>`（行起始时间兜底表）。
  - 动态 `BindGraph` 参数可在 `Declare` 预建量化 `ImmutableArray<GraphBindingValue>` 查找表，避免每帧分配 `ParameterValues` 与 `GraphBindingValue`。
  - 热路径空数组写 `ImmutableArray<T>.Empty`，不要写 `[]`；循环查找几何、行装载或统计同 `BindingId` 字序时使用 `for` 遍历，避免 `List`/`Dictionary` 临时集合、LINQ 和闭包。
  - **无分配同行字序统计**：如需在 `Evaluate` 按从左往右/从右往左错峰播放，直接按 `token.BindingId` 筛选并对比 `AdvanceBounds.Center.X`（坐标相近时按帧内序号 `j < i` 破平）统计 `rank` 与 `count`；单帧可写元素数通常在几十以内，纯栈上二重循环即可实现 0 分配（同时天然隔离不同 `BindingId` 的正常行与 `ExitReplica`）。

## 3. 目标粒度、仿射与透明度边界

- **目标能力矩阵**：
  - `token.WithRuby`（`TokenFamilyTarget`）：整体缩放/平移、透明度（`MultiplyOpacity`）、发光/填充/描边/扫字、`BindGraph`。
  - `token.BaseOnly`（`BaseTokenTarget`）：仅作用于主字，不连带 Ruby（参考 [RubySplitEmphasis.cs](../../../packages/RubySplitEmphasis/src/RubySplitEmphasis.cs)）。
  - `cluster.Target`（`TextClusterTarget`）：用于单个不可拆基础字簇或 Ruby 字簇（`RubyId != null`）。**注意**：`TextClusterTarget` 只支持 `AppendTransform`（`cluster.affine`）和 `SetWarp`（`cluster.warp`），**不支持** `MultiplyOpacity`、`SetGlow`、`SetFill`、`SetStrokes`。
- **连字安全检查**：写入任何单字/字簇输出前，必须检查 `geometry.BaseGeometry.CanTransformIndependently` 与 `cluster.Geometry.CanTransformIndependently`。
- **可逆矩阵下限与模拟 3D 翻折矩阵**：
  - 构建 `new Affine2(a, b, c, d, tx, ty)` 时，缩放系数必须夹取正下限（如 `>= 0.05`），不能缩到 `0`，否则在 `float32` 精度下不可逆会抛出 `InvalidTransform`。
  - 若需在 `TokenFamilyTarget` 上表现空间 3D 翻折入/出场（如 [WordFlipTransition.cs](../../../packages/WordFlipTransition/src/WordFlipTransition.cs)），可取 `Rx(rx) * Ry(ry) * Rz(rz)` 的正交 2D 投影：`a = s*(cosY*cosZ)`、`c = s*(-cosY*sinZ)`、`b = s*(sinZ*cosX + cosZ*sinY*sinX)`、`d = s*(cosZ*cosX - sinZ*sinY*sinX)`。该矩阵行列式为 `s² * cosX * cosY`，必须在角度经过 `±90°` 时对 `cosX` 与 `cosY` 各自保留原符号并夹取下限（如 `|cos| >= 0.05`），避免侧立瞬间出现奇异矩阵。
  - 若允许 `CurveValue` 配置过冲控制点（Y 超出 `[0, 1]`），运动插值可保留过冲以做出回弹，但传给 `MultiplyOpacity` 的值仍必须 `Math.Clamp(..., 0.0, 1.0)`。

## 4. 关键效果模式与常见坑点

- **行级入场与整行退场过渡（[WordFlipTransition.cs](../../../packages/WordFlipTransition/src/WordFlipTransition.cs)）**：
  - **入场时间基准**：行显现时（包括前奏准备或双槽位提前揭示）应通过 `frame.Indexes.Presentations.Get(token.BindingId)` 读取 `SourcePresentationLoad.RevealTimeUs`，而不是只看行/字演唱起点；尚未轮到入场的字（`enterElapsedUs < enterStartUs`）**必须显式写入** `output.MultiplyOpacity(token.WithRuby, 0.0)`，否则会提前静止露面。
  - **整行退场副本（`ExitReplica`）**：若在行隐藏后统一退场，在 `load is { Phase: TextLinePhase.ExitReplica }` 分支读取 `load.HideTimeUs ?? presLoad?.HideTimeUs ?? frame.TimeUs` 与 `load.ExitDurationUs` 计算退场进度；未开始退场的字保持原样，已退完的字持续写 `MultiplyOpacity(..., 0.0)`。
  - **超长行时序自适应**：若 `(count - 1) * staggerUs + charUs` 超出行可见时长（`HideTimeUs - RevealTimeUs`）或 `load.ExitDurationUs`，按比例等比缩放 `charUs` 与 `staggerUs`，防止入场未完即退场或行尾字符被截断。
  - **退场副本与宿主内置退场**：`ExitReplica` 冻结的是 `HideTimeUs - 1` 处的几何与样式；若宿主布局（如某些 `SlotSequence`）在隐藏前已将行透明度淡至 `0`，退场副本会沿用透明状态，因此整行退场脚本需搭配无前置透明淡出的布局配置。
  - **翻折曲线调校**：若使用 3D 翻折入场且希望保留边缘侧立细条的清晰轮廓，入场运动曲线宜用较缓和的 ease-out（如 `cubicBezier [0.22, 0.55], [0.35, 1]`，避开过于陡峭的默认三次 `EaseOut` 导致刚起跳就展开过半），并配快速起跳的淡入曲线（如 `[0.05, 0.65], [0.25, 1]`）。
- **逐字独立退出（[WordScatterExit.cs](../../../packages/WordScatterExit/src/WordScatterExit.cs)）**：
  - 退场完成后（`u >= 1.0`）**必须继续写入 `output.MultiplyOpacity(token.WithRuby, 0.0)`**；若直接 `continue` 跳过，该字会在同行后续字演唱期间重新闪现。
  - 启用 `lineExit.durationUs`（静态值 `0..3_000_000` us）与 `"line.exit"`，让行尾最后一个字在 `TextLinePhase.ExitReplica` 阶段也能完整播完退出动画；无 token 定时的降级分支可取 `load.ExitProgress`。
- **主字与注音分离强调（[RubySplitEmphasis.cs](../../../packages/RubySplitEmphasis/src/RubySplitEmphasis.cs)）**：
  - `TextFrame.Presentations.TokensInBinding` 的 `Performance` 仅代表主字；在 `Declare` 遍历 `source.Content.Ruby` 的 `Segments` 计算每段 UTF-16 区间 `[Utf16Start, Utf16End)` 与可用 `TimeRange?`。
  - 按 `cluster.Utf16Start` 匹配分段；若分段无独立时间或单段含多个字簇，可按字簇 `AdvanceBounds.Width` 占比（零宽按序号均分）切片 `TimeRange` 兜底。
- **混合控制器局部 `BindGraph`（`Blur`）防裁切（关键）**：
  - 混合包同时导出 `controller` 与 `graph` 时，`graph.bindingKinds` 须覆盖 `controller.bindingKinds` 全集，否则绑定到 `tokens` 等范围时会触发 `PackageBindingNotAllowed`。
  - 当仅对行内部分 `tokenFamily` 动态调用 `BindGraph` 做局部 `Blur` 且图内不含 `Transform` 节点时，宿主区域收紧会把整行 `WorkBounds` 缩到已绑图字的包围框并裁掉同行其余未退出字符。
  - **解法**：在 `tokenFamily` 图末尾串联一个单位 `Transform` 节点（如 [token-blur.graph.json](../../../packages/WordScatterExit/graphs/token-blur.graph.json) 中的 `fit`），并在 `Declare` 通过 `GraphTransformValues` 将其 `outputBounds` 设为 `new Rect2(0, 0, source.CanvasSize.X, source.CanvasSize.Y)`，即可适配任意画布分辨率且不裁切同行其余文字。
  - `Blur` 节点的 `expandPx` 须声明 `"ceil(3 * parameter:blurSigmaPx)"`，且 `maxExpandPx`、`maxSampleOffsetPx` 与图根 `bounds.maxExpandPx` 均需不小于 `3 * max(sigma)`。
