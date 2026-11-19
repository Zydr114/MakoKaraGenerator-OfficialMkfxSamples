# SDK v2（MKFX）进阶脚本编写经验与避坑指南

本文总结了在开发与 Vulkan 实机验证 [`WordScatterExit`](../packages/WordScatterExit)（逐字散落退出 + 高斯模糊）和 [`RubySplitEmphasis`](../packages/RubySplitEmphasis)（主字与注音单簇分离强调）过程中沉淀的实战经验，与 [SDK v2 完整 API 参考](script-sdk-v2.md) 和 [API 与示例包对照索引](api-sample-mapping.md) 配合使用。

---

## 1. 标准开发与验证闭环

1. **声明与打包**：修改包内任意文件（`.cs`、`.json`、`README.md` 等）后，使用 `tools/pack.py` 刷新 `manifest.json` 摘要并导出 `.mkfx`：
   ```bash
   python3 tools/pack.py <包目录名>
   python3 tools/pack.py --check
   ```
2. **注册验证场景**：[tools/Verify/Program.cs](../tools/Verify/Program.cs) 会校验 `packages/` 下每个包目录是否都在 [tools/Verify/Scenarios.cs](../tools/Verify/Scenarios.cs) 配置了验证场景（缺项会以退出码 `2` 中止）。新增包时需同步补充场景，且针对 Ruby 或退场副本建议专门构造含注音分段/未分段及行尾后时刻的测试场景。
3. **编译准入与 Vulkan 真机检查**：
   ```bash
   dotnet build -c Release --artifacts-path .artifacts tools/Verify/Verify.csproj
   dotnet .artifacts/bin/Verify/release/Verify.dll --root . --only <包目录名>
   ```
4. **图片目视核查**：不要只看“0 Error 诊断”和“变化像素 > 0”。务必直接查看 `out/<包目录名>/*.png`（普通与透明背景），重点核查**同行未受影响文字是否被裁切**、旋转/缩放支点是否偏移、半透明与模糊边界是否符合预期。

---

## 2. 纯度分析器规则与零分配技巧（`MKV2001`–`MKV2009`）

SDK v2 编译器会启用严格的纯度分析器，`MKV2001`–`MKV2007` 为硬性编译错误，`MKV2008` / `MKV2009` 为逐帧分配提示：

1. **避开 `out` / `ref` 局部变量写法（`MKV2003`）**：
   - 在 `Evaluate` 中调用 `dict.TryGetValue(key, out var value)` 会触发 `MKV2003` 编译错误。
   - **正确写法**：改为 `dict.ContainsKey(key) ? dict[key] : fallback`（参考 [PetalFall.cs](../packages/PetalFall/src/PetalFall.cs#L134-L136)）。
2. **把查询表与图绑定尽量前移到 `Declare`（消除 `MKV2008`）**：
   - 在 `Declare` 阶段预建不可变数据结构，例如：`ImmutableDictionary<TokenId, ulong>`（稳定随机序号）、`ImmutableDictionary<RubyId, ImmutableArray<RubySegmentSpan>>`（注音分段时间表）。
   - 对于带动态参数（如模糊半径 `sigma`）的 `BindGraph` 调用，可在 `Declare` 预生成一组量化步进（例如 65 档）的 `ImmutableArray<GraphBindingValue>`，在 `Evaluate` 按下标直接读取，做到**零堆分配**（不再每帧 `new ParameterValues`、`new GraphBindingValue`）。
   - 热路径上的空不可变数组请使用 `ImmutableArray<T>.Empty`，不要写 `[]`（集合表达式在非常量上下文可能产生分配提示）。
   - 在 `Evaluate` 内使用索引 `for` 循环匹配 `frame.Geometry` / `frame.LineLoads`，避免 LINQ、闭包与临时字典分配。

---

## 3. 目标粒度与属性支持矩阵

| 目标类型 | 取得方式 | 支持的常用操作 | 不支持 / 注意事项 |
| --- | --- | --- | --- |
| `TokenFamilyTarget` | `token.WithRuby` | 仿射、透明度、两态外观（填充/描边/阴影/辉光/扫字）、`BindGraph`、材质参数 | 会连带主字与全部关联 Ruby 一同变换 |
| `BaseTokenTarget` | `token.BaseOnly` | 仿射、透明度、两态外观、扫字、材质参数 | **仅影响基础主字**，关联 Ruby 保持父族基线与外观 |
| `TextClusterTarget` | `cluster.Target` | 字簇仿射（`cluster.affine`）、字簇网格（`cluster.warp`） | **不支持** `MultiplyOpacity`、`SetGlow`、`SetFill`、`SetStrokes` |

- **连字独立性检查**：任何针对单字或字簇的独立变换、透明度、外观或图绑定，必须先检查 `geometry.BaseGeometry.CanTransformIndependently` 与 `cluster.Geometry.CanTransformIndependently`；若遇共享字簇跳过处理，可避免运行时 `UnsupportedGlyphPartition`。
- **完整 2D 仿射矩阵**：`new Affine2(a, b, c, d, tx, ty)` 支持绕任意支点组合“缩放 + 旋转 + 平移”（公式与实现见 [WordScatterExit.cs](../packages/WordScatterExit/src/WordScatterExit.cs#L204-L214)）。矩阵在后端 `float32` 下必须可逆且行列式非零，缩放因子需保留下限（如 `>= 0.05`），不能直接缩至 `0`。

---

## 4. 逐字独立退场经验（`WordScatterExit`）

1. **已退完的字必须持续写零透明**：当某字在 `frame.TimeUs >= range.EndUs + exitUs` 后退出完成，同行后面的字可能仍在演唱。此时脚本**必须**继续对该字调用 `output.MultiplyOpacity(token.WithRuby, 0.0)`，切勿直接 `continue` 跳过（跳过会导致宿主按默认 `opacity = 1` 把已退出字重新显示出来）。
2. **配合 `line.exit` 保住句尾最后一字**：在 `manifest.json` 中声明静态 `"lineExit": {"durationUs": 1500000}` 与 `"line.exit"`。进入 `TextLinePhase.ExitReplica` 阶段后，退出副本保留原有 `BindingId` 与源 `token.Performance.TimeRange`，使句尾最后一个字在行隐藏后依然能完整跑完退出动画；对于无单字定时的元素，可退回使用 `load.ExitProgress`。
3. **主字与 Ruby 分离飘散**：主字用 `token.BaseOnly` 向下倾转缩小，注音按每个 `cluster.Target`（`RubyId != null`）叠加确定性随机散开偏移与倾转，即可做出“主字坠落、注音假名各自散开”的视觉层次；同一字族的透明度由 `token.WithRuby` 统一淡出。

---

## 5. 主字与 Ruby 分离强调及兜底经验（`RubySplitEmphasis`）

1. **为何在 `Declare` 读取 `SourceRuby.Segments`**：`TextFrame` 上 `token.Performance` 只反映**主字**进度，不直接附带注音分段进度。真正的注音分段信息位于 `DeclareContext.Content.Ruby` 的 `SourceRubySegment`（`Text`、`TimeRange?`、`TimingStatus`）。
2. **匹配字簇与兜底切片策略**：
   - 在 `Declare` 阶段按 `Segments` 顺序累加 `Text.Length`，记录每段的 UTF-16 起止区间 `[Utf16Start, Utf16End)` 与有效 `TimeRange?`。
   - 在 `Evaluate` 阶段按 `cluster.Utf16Start` 定位到所属分段（与后端“字簇归属首 code unit 所在分段”规则一致）：
     - 若分段拥有独立 `TimeRange` 且只包含 1 个字簇，直接使用精准分段时间；
     - **兜底机制**：遇到未拆分（仅 1 个未定时分段）或单分段内含多个字簇的 Ruby 时，按字簇 `AdvanceBounds.Width` 占总宽度的比例（总宽或字簇宽接近零时退回按字簇序号均分）对主字/分段的 `TimeRange` 做连续无重叠时间切片，即可让未独立定时的注音假名也能逐个跟随扫字进度单独缩放。

---

## 6. 混合 `controller` + 局部 `BindGraph`（高斯模糊）关键坑点

如果像 `WordScatterExit` 那样在同一个包中同时提供 `controller` 与 `graph`（`target: "tokenFamily"`）并用 `BindGraph` 做逐字模糊，需要特别注意以下两点：

1. **`graph.bindingKinds` 必须对齐 `controller`**：宿主校验 `BindGraph` 时会核对目标图导出是否允许当前实例绑定范围，因此 `graph` 导出需覆盖 `controller` 声明的全部 `bindingKinds`（例如同时包含 `tokens`、`lines` 等）。
2. **局部绑定 `Blur` 图时防止整行其余文字被裁切**：
   - 当某行只有部分正在退出的字绑定了纯 `Blur` 图（图内不含 `Transform` 节点），而同行其余未退出文字没有绑图时，渲染管线的区域收紧逻辑会把整行工作框收紧到“仅已绑图字族的包围框并集”，从而把同行右侧正常演唱的文字裁掉。
   - **解法**：在 `tokenFamily` 图末尾串联一个单位 `Transform` 节点（见 [token-blur.graph.json](../packages/WordScatterExit/graphs/token-blur.graph.json) 中的 `"id": "fit"`），并在 `Declare` 通过 `GraphTransformValues` 将该节点的 `outputBounds` 显式设为 `new Rect2(0, 0, source.CanvasSize.X, source.CanvasSize.Y)`。这样既能适配任意工程画布分辨率、保证同行其余文字完全不被裁切，又只需对 `0 < u < 1` 退场中的字符绑定图，兼顾正确性与 GPU 表面预算。
3. **`Blur` 节点外扩与透明输出**：`Blur` 节点的 `bounds.expandPx` 必须满足 `"ceil(3 * parameter:blurSigmaPx)"`，且 `maxExpandPx`、`maxSampleOffsetPx` 与图根 `bounds.maxExpandPx` 须覆盖 `3 * max(sigma)`；输入仅使用 `TargetColor`（`backgroundRequirement: "none"`）时，在透明背景导出下同样完整生效。
