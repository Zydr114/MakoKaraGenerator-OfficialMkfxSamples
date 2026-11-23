# 节拍动感残影与放射叠印（`BeatKineticEcho`）

`packageVersion: 3` 混合图变换特效包（C# 控制器 `controller` + 13 pass 并行行级图像图 `graph` + 极简 SkSL 染色/门控着色器），由音频节拍或音节起唱瞬态驱动三路并行 `Transform` + `Blur` + `Blend(plus)` 发光叠印：

- **三路并行图变换支路**：图内从 `echoSource`（支持 `sungOnly` 门控 `SungCoverage`）分出 `echo1` / `echo2` / `echo3` 三个并行 `Transform` 节点，依次经 `Blur` 与极简双色梯度 `RuntimeShader` 后通过两级 `Blend(mode: "plus")` 叠加发光残影，最后用 `Blend(mode: "sourceOver")` 将清晰锐利的原字盖在最顶层。
- **三种动感变换模态（`echoMode`）**：
  - `radialZoom`：以行中心为支点三层递进放射外扩缩放；
  - `horizontalShear`：三层正反水平错位与动态斜切拖影；
  - `orbitalSplit`：随 `BeatPhase` 沿三等分相角（120° 间隔）做小圆轨道环绕重影。
- **音频驱动与双重退化**：读取 `frame.Audio.GetAt` 的 `Pulse`、`BassLevel` 与 `BeatPhase`；无音频分析时自动按 `fallbackBpm` 节拍脉冲驱动（当 `fallbackBpm = 0` 时退化为当前音节起唱瞬态）。完全不依赖背景媒体，透明背景导出同样完整呈现。
