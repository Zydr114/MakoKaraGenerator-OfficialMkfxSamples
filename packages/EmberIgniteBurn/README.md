# 炽焰燃边与热浪碳化（SDK v2）

`packageVersion: 3` 混合效果包（C# 控制器 `controller` + 整行图像图 `graph` + SkSL 着色器），以 `SungCoverage` 邻域梯度与音频能量驱动火焰灼烧字幕：

- **多向梯度精准提取燃烧前沿**：在 SkSL 内对 `SungCoverage` 做 8 邻域方向梯度与未唱邻域检测，精确锁定当前扫字灼烧边界并伴随白炽→橘红高光。
- **音频驱动 FBM 火舌与热浪折射**：读取 `AudioFeatures`（无音频时支持 `fallbackBpm` 与音节起唱瞬态降级），实时调制 FBM 上升火舌高度与空气热浪扭曲波动。
- **双质感模式（`burnStyle`）**：支持 `moltenGold`（高温熔金纹理）与 `charredEmber`（前沿白炽、已唱炭黑暗红余烬裂纹）。
- **燃尽化灰退场**：在 `TextLinePhase.ExitReplica` 阶段（`lineExit.durationUs: 1200000`）沿噪声阈值由外向内侵蚀字形并绽放余烬火线，透明导出同样完整保留。
