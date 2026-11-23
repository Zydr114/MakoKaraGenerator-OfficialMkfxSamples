# SceneLitCastShadow（实景光源感知立体投影）

结合背景媒体四象限分析（`media.features`）与动态 `ImageGraph` 仿射节点（`Transform` + `Blur` + `RuntimeShader` + `Blend`），让歌词像矗立于实景中的实体标识牌，随环境主光源实时摆动立体斜影。

## 核心机制

1. **四象限光源探针**：对画布左上、右上、左下、右下四象限调用 `frame.Media.Sample`（`350ms` 平滑窗口），解算主光源入射方向与环境明暗反差，并从最亮象限颜色推导互补冷暖阴影色。
2. **动态基线斜切压扁**：以每行真实基线为接地支点构造 `Affine2` 矩阵，通过 `GraphTransformValues` 实时注入图内 `cast` 节点，将字形向背光方向斜切压扁并经过半影高斯模糊与沿距离渐隐着色后，置于清晰原文字下方合成。
3. **透明背景完整退化**：图输入全程采用 `backgroundRequirement: "none"`（背景分析仅在 C# 端读取），因此在 `NoMedia` 与透明导出模式下自动退化至 `defaultLightAngleDeg` 与 `defaultShadowColor`，仍产出完整立体斜影。
