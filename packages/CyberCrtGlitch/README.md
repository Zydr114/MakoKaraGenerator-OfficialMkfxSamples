# 赛博 CRT 荧光与故障撕裂（SDK v2）

`packageVersion: 3` 混合效果包（C# 控制器 `controller` + 整行图像图 `graph` + SkSL 着色器），以音频节拍驱动复古 CRT 显像管故障美学：

- **CRT 扫描线与荧光粉格栅**：在屏幕空间施加细腻水平扫描线与 RGB 子像素三色格栅调制，已唱部分通过 `SungCoverage` 掩膜与高斯 `Blur` 生成高饱和磷光余辉。
- **鼓点触发块状撕裂与色散**：控制器读取 `frame.Audio.GetAt`（无音频时自动按 `fallbackBpm` 或音节起唱瞬态降级），结合确定性时间步进哈希驱动多尺度水平切片错位与 RGB 通道分离。
- **CRT 关机坍缩退场**：在 `TextLinePhase.ExitReplica` 阶段（`lineExit.durationUs: 600000`）先将整行垂直压扁为高亮电子束横线，再沿水平收缩为闪点熄灭。
- **透明导出完整保留**：仅使用 `TargetColor` 与 `SungCoverage`（`backgroundRequirement: "none"`），透明背景输出同样呈现完整荧光与故障特效。
