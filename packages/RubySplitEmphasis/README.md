# 主字注音分离强调（SDK v2）

- **主字与注音独立缩放**：主字使用 `BaseTokenTarget`（`token.BaseOnly`）单独缩放，不再带动上方注音整体同步膨胀；不做位置避让，保持各自支点原位缩放。
- **注音只放大唱到的那一个**：读取 `SourceRuby.Segments` 的分段时间并在 `Declare` 阶段构建 UTF-16 映射表，在 `Evaluate` 阶段通过 `TextClusterTarget`（`cluster.Target`）仅对当前演唱（及回落期）的注音字簇施加独立缩放。
- **未分段注音宽度切片兜底**：启用 `rubyFallbackSlice`（默认开启）时，对于未分段或未单独定时的注音（以及包含多个字簇的单个注音片段），自动按字簇 `AdvanceBounds` 宽度占比切分主字/整段的时间区间，保证单假名依然依次轮流缩放。
- **双动画模式**：支持 `swell`（随唱渐涨回落）与 `burst`（起唱突放衰减）两种强调手感，并可为主字单独叠加脉冲辉光。
