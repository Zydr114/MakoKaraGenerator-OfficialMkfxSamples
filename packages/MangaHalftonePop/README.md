# 漫画网点与孔版错印（SDK v2）

纯图（`kind: "graph"`、`target: "line"`，零 C# 控制器）`packageVersion: 3` 效果包，将复古日系漫画网点纸与 Risograph 双色孔版套印融入卡拉 OK 歌词渲染：

- **45° / 15° 半调网点矩阵**：未唱字形内部叠加 45° 细密印刷网点保留清晰可读性；已唱字形切换为 15° 放大波普网点并由 `host:timeSeconds` 驱动微幅呼吸移相。
- **Risograph 双色错版偏移**：针对 `SungCoverage` 沿正反斜向偏移生成主/辅双色网点套印重影，叠加柔和荧光底晕。
- **漫画硬边斜线投影**：基于 `GlyphCoverage` 投射带 45° 速度斜线纹理的硬边漫画阴影。
- **零背景依赖**：所有输入 `backgroundRequirement` 均为 `"none"`，不透明与透明背景导出均可 100% 完整保留。
