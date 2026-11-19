# 逐字散落退出（SDK v2）

- **逐字独立退场**：每个字在演唱完成后（`frame.TimeUs >= range.EndUs`）立即各自进入退出动画，同行后续歌词继续正常演唱；退场结束的字持续保持隐身直到整行退出。
- **主字与注音分离散落**：主字（`BaseTokenTarget`）旋转、缩小并下落漂移，关联注音的每个字簇（`TextClusterTarget`）带确定性随机散布与倾转各自飘散。
- **高斯模糊与余晖渐隐**：控制器通过 `BindGraph` 为正在退场的 `tokenFamily` 动态绑定包内 `Blur` 图导出（`token-blur`）实现逐字高斯模糊，并配合 `MultiplyOpacity` 与 `SetGlow` 淡出。
- **句尾退出副本兜底**：启用 `lineExit`（1.5 秒）与 `line.exit`，保证行尾最后一个字也能在整行隐藏后完整播完退场动画。
