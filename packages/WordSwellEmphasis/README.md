# 逐字强调（SDK v2）

- **逐字渐大**：正在演唱的歌词字（含关联注音）在演唱区间内按 `growCurve`（默认 `easeInOut`）从原尺寸平滑放大到 `peakScale`（默认 `1.38` 倍），并可配合轻微垂直浮动 `liftPx`。
- **平滑回落**：唱完该字后，在 `returnUs`（默认 `220000` us）内按 `returnCurve`（默认 `easeOut`）温柔收缩回原有尺寸 `1.0` 倍。
- **连字安全**：自动检查 `CanTransformIndependently`，避免对共享字簇触发几何分区异常。
