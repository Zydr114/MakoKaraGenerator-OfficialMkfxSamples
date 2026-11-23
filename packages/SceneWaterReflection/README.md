# 实景水面倒影与波纹镜像（`SceneWaterReflection`）

`packageVersion: 3` 混合图变换特效包（C# 控制器 `controller` + 行级图像图 `graph` + SkSL 水波着色器），利用动态 `GraphTransformValues` 与 `media.features` 背景采样实现与画面水面材质联动的实时倒影：

- **动态基线镜像变换**：读取每行 `LineGeometry.BaseGeometry.Baseline` 的世界坐标 `baselineY`，每帧构造沿 `y = baselineY + gapPx` 垂直镜像翻转、带垂直压缩 `reflectionScaleY` 与轻微斜切 `skewX` 的仿射矩阵，连同严格受控的倒影 `outputBounds` 注入 `mirror`（`Transform`）节点。
- **背景水面分析驱动物理参数**：对基线下方倒影区调用 `frame.Media.Sample`（250ms 时域平滑窗口），读取 `Mean` 作为水面染色、`MeanPremulY` 调节倒影明暗清晰度（暗面更清晰、亮面更柔和）、`PremulYVariance` 驱动波纹扰动活跃度。
- **无背景依赖 / 透明导出完整支持**：图输入全程声明 `backgroundRequirement: "none"`，`NoMedia` / `TransparentOutput` 下退化为 `defaultWaterTint` 与温和波纹，透明背景仍完整渲染清晰原字与水波倒影。
