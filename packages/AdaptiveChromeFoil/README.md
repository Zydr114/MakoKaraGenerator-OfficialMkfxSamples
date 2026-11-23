# AdaptiveChromeFoil（环境光自适应液态镀铬）

将背景媒体时域窗口分析（`media.features`）与逐帧材质 Uniform 注入（`SetMaterialUniform`）结合，呈现随场景天地光色自动变换的 Y2K 液态镀铬字效。

## 导出形态

- `adaptive-chrome-foil`（`controller`）：针对每个可独立变换字族采样其上方「天光区」与下方「地反区」背景统计，经线性→sRGB 提纯后打包写入 `uSkyColor` 与 `uGroundAndState`，并自适应切换暗/亮底高反差轮廓描边。
- `chrome-material`（`material`，`target: "region"`，`coverageMode: "masked"`）：可独立挂载至填充或描边槽位，利用 `TargetRegionCoverage` 多尺度梯度构建凸起法线、经典镀铬地平线反射暗带与高光扫掠，并自带金属线性渐变 `fallback`。

## 退化策略

在 `Pending` / `Unavailable` / `NoMedia`（含透明背景导出）状态下，控制器自动采用 `fallbackSkyColor` 与 `fallbackGroundColor` 构建虚拟双色环境反射，保持完整液态镀铬质感。
