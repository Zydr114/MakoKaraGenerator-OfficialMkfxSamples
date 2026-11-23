using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

/// <summary>一个包的一种验证场景：如何将包挂入基准工程、是否在基准工程附带音频分析、以及在哪些歌曲时刻采样。</summary>
internal sealed record Scenario(
    string Package,
    string Variant,
    Func<Project, EffectPackageContents, Project> Apply,
    long[] TimesUs,
    bool WithAudioAnalysis = false);

internal static class Scenarios
{
    // 基准工程第 0 行 2.0~5.0s，第 1 行 5.0~8.0s，每字 0.5s；采样点覆盖起唱瞬间、字中段、行尾、跨行衔接与后续行。
    private static readonly long[] DefaultTimes = [2_100_000, 3_250_000, 4_900_000, 6_400_000];
    private static readonly long[] TrailTimes = [2_100_000, 3_250_000, 4_900_000, 5_050_000, 6_400_000];
    private static readonly long[] ExitTimes = [2_750_000, 3_250_000, 4_900_000, 5_200_000, 6_400_000];
    private static readonly long[] RubySwellTimes = [2_180_000, 2_400_000, 3_120_000, 3_320_000, 3_450_000];
    private static readonly long[] RubyBurstTimes = [2_060_000, 2_310_000, 3_060_000, 3_250_000];
    private static readonly long[] FlipTimes = [2_250_000, 2_420_000, 3_250_000, 5_200_000, 5_400_000, 6_400_000];
    private static readonly long[] RippleTimes = [2_380_000, 2_560_000, 3_120_000, 4_900_000, 6_400_000];
    private static readonly long[] CrtExitTimes = [2_100_000, 3_250_000, 5_180_000, 5_420_000, 6_400_000];
    private static readonly long[] EmberExitTimes = [2_750_000, 3_250_000, 4_900_000, 5_650_000, 6_400_000];

    internal static IReadOnlyList<Scenario> All { get; } =
    [
        new("UtopiaEmphasis", "default",
            (project, package) => WithInstance(project, package, "utopia-emphasis"),
            DefaultTimes),
        new("UtopiaEmphasis", "token-attack",
            (project, package) => WithInstance(project, package, "utopia-emphasis",
                """{"attackUs":60000,"decayMode":"token"}"""),
            DefaultTimes),

        new("WordSwellEmphasis", "default",
            (project, package) => WithInstance(project, package, "word-swell-emphasis"),
            DefaultTimes),

        new("StarTrail", "default",
            (project, package) => WithInstance(project, package, "star-trail"),
            TrailTimes),

        new("PetalFall", "above",
            (project, package) => WithInstance(project, package, "petal-fall"),
            DefaultTimes),
        new("PetalFall", "behind",
            (project, package) => WithInstance(project, package, "petal-fall",
                """{"layer":"behind"}"""),
            DefaultTimes),

        new("LiquidGlassMaterial", "media-and-stroke",
            (project, package) => WithMaterial(project, package, "liquid-glass", unsung: true, sung: true, stroke: true),
            DefaultTimes),
        new("LiquidGlassMaterial", "below-and-stroke",
            (project, package) => WithMaterial(project, package, "liquid-glass", unsung: true, sung: true, stroke: true,
                """{"backdropSource":"below"}"""),
            DefaultTimes),

        new("FrostedGlassMaterial", "media-and-stroke",
            (project, package) => WithMaterial(project, package, "frosted-glass", unsung: true, sung: true, stroke: true),
            DefaultTimes),
        new("FrostedGlassMaterial", "below-and-stroke",
            (project, package) => WithMaterial(project, package, "frosted-glass", unsung: true, sung: true, stroke: true,
                """{"backdropSource":"below"}"""),
            DefaultTimes),

        new("BeatPulseNeon", "fallback-bpm",
            (project, package) => WithInstance(project, package, "beat-pulse-neon"),
            DefaultTimes),
        new("BeatPulseNeon", "analyzed-audio",
            (project, package) => WithInstance(project, package, "beat-pulse-neon", """{"fallbackBpm":0}"""),
            DefaultTimes,
            WithAudioAnalysis: true),

        new("HoloPrismGlow", "default",
            (project, package) => WithInstance(project, package, "holo-prism-glow"),
            DefaultTimes),

        new("WordScatterExit", "default",
            (project, package) => WithInstance(project, package, "word-scatter-exit"),
            ExitTimes),
        new("WordScatterExit", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "word-scatter-exit"),
            ExitTimes),

        new("RubySplitEmphasis", "default",
            (project, package) => WithInstance(project, package, "ruby-split-emphasis"),
            DefaultTimes),
        new("RubySplitEmphasis", "ruby-swell",
            (project, package) => WithInstance(WithSampleRuby(project), package, "ruby-split-emphasis"),
            RubySwellTimes),
        new("RubySplitEmphasis", "ruby-burst",
            (project, package) => WithInstance(WithSampleRuby(project), package, "ruby-split-emphasis",
                """{"emphasisMode":"burst","basePeakScale":1.42,"rubyPeakScale":1.65,"returnUs":180000}"""),
            RubyBurstTimes),

        new("WordFlipTransition", "default",
            (project, package) => WithInstance(project, package, "word-flip-transition"),
            FlipTimes),
        new("WordFlipTransition", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "word-flip-transition"),
            FlipTimes),
        new("WordFlipTransition", "rtl-exit",
            (project, package) => WithInstance(project, package, "word-flip-transition",
                """{"exitOrder":"rtl","exitMirrorAngles":false}"""),
            FlipTimes),

        new("SparkleWake", "default",
            (project, package) => WithInstance(project, package, "sparkle-wake"),
            TrailTimes),
        new("SparkleWake", "tilted-short",
            (project, package) => WithInstance(project, package, "sparkle-wake",
                """{"lengthPx":110,"maxTiltDeg":22,"spinHz":0.6,"driftSpeedPx":8,"layer":"behind"}"""),
            TrailTimes),

        new("RefractivePrismCrystal", "default",
            (project, package) => WithInstance(project, package, "refractive-prism-crystal"),
            DefaultTimes),
        new("RefractivePrismCrystal", "analyzed-audio",
            (project, package) => WithInstance(project, package, "refractive-prism-crystal"),
            ExitTimes,
            WithAudioAnalysis: true),
        new("RefractivePrismCrystal", "below-ruby-exit",
            (project, package) => WithInstance(WithSampleRuby(project), package, "refractive-prism-crystal",
                """{"backdropSource":"below"}"""),
            ExitTimes),

        new("OpticalInkErosion", "default",
            (project, package) => WithInstance(project, package, "optical-ink-erosion"),
            DefaultTimes),
        new("OpticalInkErosion", "analyzed-audio",
            (project, package) => WithInstance(project, package, "optical-ink-erosion"),
            ExitTimes,
            WithAudioAnalysis: true),
        new("OpticalInkErosion", "below-ruby-exit",
            (project, package) => WithInstance(WithSampleRuby(project), package, "optical-ink-erosion",
                """{"backdropSource":"below"}"""),
            ExitTimes,
            WithAudioAnalysis: true),

        new("SceneWaterReflection", "default",
            (project, package) => WithInstance(project, package, "scene-water-reflection"),
            DefaultTimes),
        new("SceneWaterReflection", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "scene-water-reflection"),
            DefaultTimes),

        new("BeatKineticEcho", "default",
            (project, package) => WithInstance(project, package, "beat-kinetic-echo"),
            DefaultTimes),
        new("BeatKineticEcho", "analyzed-audio",
            (project, package) => WithInstance(project, package, "beat-kinetic-echo",
                """{"fallbackBpm":0,"sungOnly":true}"""),
            DefaultTimes,
            WithAudioAnalysis: true),
        new("BeatKineticEcho", "horizontalShear",
            (project, package) => WithInstance(project, package, "beat-kinetic-echo",
                """{"echoMode":"horizontalShear"}"""),
            DefaultTimes,
            WithAudioAnalysis: true),
        new("BeatKineticEcho", "orbitalSplit",
            (project, package) => WithInstance(project, package, "beat-kinetic-echo",
                """{"echoMode":"orbitalSplit"}"""),
            DefaultTimes,
            WithAudioAnalysis: true),

        new("BeatEmberStorm", "default",
            (project, package) => WithInstance(project, package, "beat-ember-storm"),
            TrailTimes),
        new("BeatEmberStorm", "analyzed-audio",
            (project, package) => WithInstance(project, package, "beat-ember-storm",
                """{"fallbackBpm":0}"""),
            TrailTimes,
            WithAudioAnalysis: true),

        new("ChromaFireflyBokeh", "default",
            (project, package) => WithInstance(project, package, "chroma-firefly-bokeh"),
            TrailTimes),
        new("ChromaFireflyBokeh", "analyzed-audio",
            (project, package) => WithInstance(project, package, "chroma-firefly-bokeh"),
            TrailTimes,
            WithAudioAnalysis: true),

        new("AcousticRippleRain", "default",
            (project, package) => WithInstance(project, package, "acoustic-ripple-rain"),
            RippleTimes),
        new("AcousticRippleRain", "analyzed-audio",
            (project, package) => WithInstance(project, package, "acoustic-ripple-rain",
                """{"fallbackBpm":0}"""),
            RippleTimes,
            WithAudioAnalysis: true),
        new("AcousticRippleRain", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "acoustic-ripple-rain"),
            RippleTimes),

        new("CyberCrtGlitch", "default",
            (project, package) => WithInstance(project, package, "cyber-crt-glitch"),
            DefaultTimes),
        new("CyberCrtGlitch", "analyzed-audio",
            (project, package) => WithInstance(project, package, "cyber-crt-glitch", """{"fallbackBpm":0}"""),
            CrtExitTimes,
            WithAudioAnalysis: true),

        new("EmberIgniteBurn", "default",
            (project, package) => WithInstance(project, package, "ember-ignite-burn"),
            DefaultTimes),
        new("EmberIgniteBurn", "analyzed-audio",
            (project, package) => WithInstance(project, package, "ember-ignite-burn", """{"fallbackBpm":0}"""),
            EmberExitTimes,
            WithAudioAnalysis: true),
        new("EmberIgniteBurn", "charred-ember-exit",
            (project, package) => WithInstance(project, package, "ember-ignite-burn",
                """{"burnStyle":"charredEmber"}"""),
            EmberExitTimes,
            WithAudioAnalysis: true),

        new("MangaHalftonePop", "default",
            (project, package) => WithInstance(project, package, "manga-halftone-pop"),
            DefaultTimes),
        new("MangaHalftonePop", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "manga-halftone-pop"),
            RubySwellTimes),

        new("AdaptiveChromeFoil", "default",
            (project, package) => WithInstance(project, package, "adaptive-chrome-foil"),
            DefaultTimes),
        new("AdaptiveChromeFoil", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "adaptive-chrome-foil"),
            DefaultTimes),
        new("AdaptiveChromeFoil", "material-standalone",
            (project, package) => WithMaterial(project, package, "chrome-material", unsung: true, sung: true, stroke: true),
            DefaultTimes),

        new("SceneLitCastShadow", "default",
            (project, package) => WithInstance(project, package, "scene-lit-cast-shadow"),
            DefaultTimes),
        new("SceneLitCastShadow", "with-ruby",
            (project, package) => WithInstance(WithSampleRuby(project), package, "scene-lit-cast-shadow",
                """{"defaultLightAngleDeg":-45,"maxSkew":1.15,"shadowOpacity":0.82}"""),
            DefaultTimes),
        new("SceneLitCastShadow", "graph-standalone",
            (project, package) => WithInstance(project, package, "cast-shadow-line"),
            DefaultTimes),
    ];

    internal static ProjectAudioAnalysis CreateSyntheticAudioAnalysis(long durationUs)
    {
        long sampleCount = durationUs * AudioAnalysisProfile.SampleRate / 1_000_000L;
        long hopCount = sampleCount / AudioAnalysisProfile.HopSamples +
                        (sampleCount % AudioAnalysisProfile.HopSamples == 0 ? 0 : 1);
        var points = new List<AudioAnalysisPoint>((int)hopCount);
        for (long i = 0; i < hopCount; i++)
        {
            long timeUs = i * AudioAnalysisProfile.HopUs;
            double beats = (timeUs / 1_000_000d) * 2.0; // 120 BPM
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.2);
            double bass = Math.Clamp(0.25 + 0.70 * impulse, 0.0, 1.0);
            double pulse = Math.Clamp(0.20 + 0.75 * impulse, 0.0, 1.0);
            points.Add(new AudioAnalysisPoint(timeUs, bass, pulse, phase, 0.92));
        }

        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("verify-audio-source")));
        string pcmHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("verify-audio-pcm")));
        return ProjectAudioAnalysis.Available(new AudioAnalysisRevision(sourceHash, pcmHash, sampleCount, points));
    }

    private static Project WithInstance(Project project, EffectPackageContents package, string exportId, string overrides = "{}")
        => project with
        {
            Effects = new([package], [new ProjectEffectInstance
            {
                Id = "verify-" + exportId,
                Name = exportId,
                PackageId = package.PackageId,
                Revision = package.Revision,
                ExportId = exportId,
                Binding = new(ProjectEffectBindingKind.Project),
                Subscription = new("project"),
                Seed = 7,
                Parameters = EffectParameterOverrides.Parse(overrides),
            }]),
        };

    private static Project WithMaterial(Project project, EffectPackageContents package, string exportId,
        bool unsung = false, bool sung = false, bool stroke = false, string overrides = "{}")
    {
        var material = new MaterialSpec.PackageMaterial(
            package.PackageId,
            package.Revision,
            exportId,
            EffectParameterOverrides.Parse(overrides));
        var style = project.LyricStyle;
        if (unsung) style = style with { UnsungFill = new() { Material = material } };
        if (sung) style = style with { SungFill = new() { Material = material } };
        if (stroke) style = style with { Stroke = new() { Material = material, WidthPx = 6 } };
        return project with { LyricStyle = style, Effects = new([package]) };
    }

    private static Project WithSampleRuby(Project project)
    {
        var branch = project.Lyrics.Branches[0];
        var line0 = branch.Lines[0];
        // "朝の光浴びて":
        // r0t0="朝"(2.0~2.5s) -> 分段 Ruby "あ"(2.0~2.25s) + "さ"(2.25~2.5s)
        // r0t2="光"(3.0~3.5s) -> 未分段 Ruby "ひかり"(Missing，测试兜底按字簇宽度切片)
        // r0t3="浴"(3.5~4.0s) -> 单段 Ruby "あ"(3.5~4.0s)
        var ruby0 = new RubyAnnotation
        {
            Id = "ruby-r0t0",
            BaseTokenId = "r0t0",
            Text = "あさ",
            Segments =
            [
                RubyTextSegment.Create("ruby-r0t0-seg-1", "あ", new TimeRange(2_000_000, 2_250_000), TimingStatus.Edited),
                RubyTextSegment.Create("ruby-r0t0-seg-2", "さ", new TimeRange(2_250_000, 2_500_000), TimingStatus.Edited),
            ],
            Language = "ja",
            RelativeScale = 0.5,
            GapPx = 2,
            Alignment = RubyAlignment.Center,
        };
        var ruby2 = RubyAnnotation.Create("ruby-r0t2", "r0t2", "ひかり", language: "ja");
        var ruby3 = new RubyAnnotation
        {
            Id = "ruby-r0t3",
            BaseTokenId = "r0t3",
            Text = "あ",
            Segments =
            [
                RubyTextSegment.Create("ruby-r0t3-seg-1", "あ", new TimeRange(3_500_000, 4_000_000), TimingStatus.Edited),
            ],
            Language = "ja",
            RelativeScale = 0.5,
            GapPx = 2,
            Alignment = RubyAlignment.Center,
        };
        var updatedLine0 = line0 with { RubyAnnotations = [ruby0, ruby2, ruby3] };
        var updatedBranch = branch with { Lines = branch.Lines.SetItem(0, updatedLine0) };
        var updatedLyrics = project.Lyrics with { Branches = project.Lyrics.Branches.SetItem(0, updatedBranch) };
        return project with { Lyrics = updatedLyrics };
    }
}
