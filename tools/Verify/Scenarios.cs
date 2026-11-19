using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

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

    internal static IReadOnlyList<Scenario> All { get; } =
    [
        new("01-UtopiaEmphasis", "default",
            (project, package) => WithInstance(project, package, "utopia-emphasis"),
            DefaultTimes),
        new("01-UtopiaEmphasis", "token-attack",
            (project, package) => WithInstance(project, package, "utopia-emphasis",
                """{"attackUs":60000,"decayMode":"token"}"""),
            DefaultTimes),

        new("02-WordSwellEmphasis", "default",
            (project, package) => WithInstance(project, package, "word-swell-emphasis"),
            DefaultTimes),

        new("03-StarTrail", "default",
            (project, package) => WithInstance(project, package, "star-trail"),
            TrailTimes),

        new("04-PetalFall", "above",
            (project, package) => WithInstance(project, package, "petal-fall"),
            DefaultTimes),
        new("04-PetalFall", "behind",
            (project, package) => WithInstance(project, package, "petal-fall",
                """{"layer":"behind"}"""),
            DefaultTimes),

        new("05-LiquidGlassMaterial", "media-and-stroke",
            (project, package) => WithMaterial(project, package, "liquid-glass", unsung: true, sung: true, stroke: true),
            DefaultTimes),
        new("05-LiquidGlassMaterial", "below-and-stroke",
            (project, package) => WithMaterial(project, package, "liquid-glass", unsung: true, sung: true, stroke: true,
                """{"backdropSource":"below"}"""),
            DefaultTimes),

        new("06-FrostedGlassMaterial", "media-and-stroke",
            (project, package) => WithMaterial(project, package, "frosted-glass", unsung: true, sung: true, stroke: true),
            DefaultTimes),
        new("06-FrostedGlassMaterial", "below-and-stroke",
            (project, package) => WithMaterial(project, package, "frosted-glass", unsung: true, sung: true, stroke: true,
                """{"backdropSource":"below"}"""),
            DefaultTimes),

        new("07-BeatPulseNeon", "fallback-bpm",
            (project, package) => WithInstance(project, package, "beat-pulse-neon"),
            DefaultTimes),
        new("07-BeatPulseNeon", "analyzed-audio",
            (project, package) => WithInstance(project, package, "beat-pulse-neon", """{"fallbackBpm":0}"""),
            DefaultTimes,
            WithAudioAnalysis: true),

        new("08-HoloPrismGlow", "default",
            (project, package) => WithInstance(project, package, "holo-prism-glow"),
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
}
