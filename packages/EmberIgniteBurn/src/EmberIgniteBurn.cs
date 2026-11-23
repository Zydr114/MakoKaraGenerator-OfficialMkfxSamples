using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct EmberAudioState(
    double Bass,
    double Pulse,
    double Decay,
    double OnsetBoost);

public sealed record EmberIgniteData(
    GraphHandle EmberGraph,
    ParameterValues GraphParameters,
    int BurnStyleIndex,
    double FlameHeightPx,
    double HeatDistortionPx,
    ColorRgba CoreColor,
    ColorRgba FlameColor,
    double FallbackBpm) : PreparedEffect;

/// <summary>炽焰燃边与热浪碳化：提取 SungCoverage 多向梯度燃烧前沿，配合音频驱动 FBM 火舌、热浪扭曲及燃尽化灰退场。</summary>
public sealed class EmberIgniteBurn : IPureKaraokeEffect<EmberIgniteData>
{
    private const long ExitDurationCapUs = 1_200_000L;

    public EmberIgniteData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var graph = source.Resources.GetGraph("ember-burn-line");
        var graphParameters = new ParameterValues([
            new("bloomSigmaPx", new ParameterValue.Number(p.GetNumber("bloomSigmaPx")))
        ]);

        string style = p.GetEnum("burnStyle");
        int styleIndex = string.Equals(style, "charredEmber", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        return new EmberIgniteData(
            graph,
            graphParameters,
            styleIndex,
            p.GetNumber("flameHeightPx"),
            p.GetNumber("heatDistortionPx"),
            p.GetColor("coreColor"),
            p.GetColor("flameColor"),
            p.GetNumber("fallbackBpm"));
    }

    public void Evaluate(in TextFrame frame, EmberIgniteData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        var tokens = frame.Presentations.TokensInBinding;
        var geometries = frame.Geometry;
        var lineGeometries = frame.LineGeometry;

        for (int l = 0; l < loads.Length; l++)
        {
            var load = loads[l];
            var lineGeom = FindLineGeometry(lineGeometries, load.Target);
            if (lineGeom is null)
                continue;

            var presLoad = frame.Indexes.Presentations.Get(load.Line.BindingId);
            double exitProgress = ResolveExitProgress(frame.TimeUs, load, presLoad);

            double minActiveX = 1e6;
            double maxActiveX = -1e6;
            bool hasActive = false;
            double onsetBoost = 0.0;

            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.BindingId != load.Line.BindingId || !token.Performance.IsCurrent)
                    continue;

                if (token.Performance.TimeRange is { IsValid: true } range)
                {
                    long elapsed = Math.Max(0L, frame.TimeUs - range.StartUs);
                    double spike = Math.Exp(-(elapsed / 1_000_000.0) * 8.0);
                    if (spike > onsetBoost)
                        onsetBoost = spike;
                }

                var geom = FindTokenGeometry(geometries, token.WithRuby);
                if (geom is not null)
                {
                    Rect2 box = MapTokenUnionToCanvas(geom);
                    minActiveX = Math.Min(minActiveX, box.Left);
                    maxActiveX = Math.Max(maxActiveX, box.Right);
                    hasActive = true;
                }
            }

            EmberAudioState audio = SampleAudio(frame.Audio, frame.TimeUs, data.FallbackBpm, onsetBoost);
            double flameEnergy = Math.Clamp(0.42 * audio.Bass + 0.48 * audio.Decay + 0.25 * onsetBoost, 0.0, 1.0);
            double heatTurbulence = Math.Clamp(0.55 * audio.Bass + 0.45 * audio.Pulse, 0.0, 1.0);

            if (exitProgress > 0.01)
            {
                flameEnergy = Math.Max(flameEnergy, 0.65 * (1.0 - exitProgress));
                heatTurbulence = Math.Max(heatTurbulence, 0.70 * (1.0 - exitProgress * 0.5));
            }

            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>("emberBurn", "burnStyle"), UniformValue.Int(data.BurnStyleIndex)),
                new(new ValueTuple<string, string>("emberBurn", "flameHeightPx"), UniformValue.Float(CleanFloat(data.FlameHeightPx))),
                new(new ValueTuple<string, string>("emberBurn", "heatDistortionPx"), UniformValue.Float(CleanFloat(data.HeatDistortionPx))),
                new(new ValueTuple<string, string>("emberBurn", "coreColor"), UniformValue.Color(data.CoreColor)),
                new(new ValueTuple<string, string>("emberBurn", "flameColor"), UniformValue.Color(data.FlameColor)),
                new(new ValueTuple<string, string>("emberBurn", "flameEnergy"), UniformValue.Float(CleanFloat(flameEnergy))),
                new(new ValueTuple<string, string>("emberBurn", "heatTurbulence"), UniformValue.Float(CleanFloat(heatTurbulence))),
                new(new ValueTuple<string, string>("emberBurn", "exitProgress"), UniformValue.Float(CleanFloat(exitProgress))),
                new(new ValueTuple<string, string>("emberBurn", "activeSpanX"), UniformValue.Float4(
                    hasActive ? CleanFloat(minActiveX) : 0.0,
                    hasActive ? CleanFloat(maxActiveX) : 0.0,
                    hasActive ? 1.0 : 0.0,
                    0.0)),
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.EmberGraph,
                data.GraphParameters,
                uniforms,
                GraphTransformValues.Empty));
        }
    }

    private static EmberAudioState SampleAudio(AudioFeatures audio, long timeUs, double fallbackBpm, double onsetBoost)
    {
        long t0 = Math.Max(0L, timeUs);
        var s0 = audio.GetAt(t0);
        if (s0.Availability == AudioAvailability.Available && s0.Values is { } v0)
        {
            double p1 = SamplePulseAt(audio, Math.Max(0L, t0 - 50_000L));
            double p2 = SamplePulseAt(audio, Math.Max(0L, t0 - 100_000L));
            double decay = Math.Clamp(Math.Max(v0.Pulse, Math.Max(p1 * 0.72, p2 * 0.45)), 0.0, 1.0);
            return new EmberAudioState(
                Math.Clamp(v0.BassLevel, 0.0, 1.0),
                Math.Clamp(v0.Pulse, 0.0, 1.0),
                Math.Max(decay, onsetBoost * 0.45),
                onsetBoost);
        }

        if (fallbackBpm > 0.0)
        {
            double beats = (t0 / 1_000_000d) * (fallbackBpm / 60.0);
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.4);
            return new EmberAudioState(
                Math.Clamp(0.28 + 0.62 * impulse, 0.0, 1.0),
                Math.Clamp(impulse, 0.0, 1.0),
                Math.Max(impulse, onsetBoost * 0.55),
                onsetBoost);
        }

        return new EmberAudioState(
            Math.Clamp(0.30 * onsetBoost, 0.0, 1.0),
            Math.Clamp(onsetBoost, 0.0, 1.0),
            Math.Clamp(0.25 + 0.75 * onsetBoost, 0.0, 1.0),
            onsetBoost);
    }

    private static double SamplePulseAt(AudioFeatures audio, long timeUs)
    {
        var s = audio.GetAt(timeUs);
        return s.Availability == AudioAvailability.Available && s.Values is { } v ? v.Pulse : 0.0;
    }

    private static double ResolveExitProgress(long timeUs, TextLineLoad load, SourcePresentationLoad? presLoad)
    {
        if (load.Phase != TextLinePhase.ExitReplica)
            return 0.0;

        long hideUs = load.HideTimeUs ?? presLoad?.HideTimeUs ?? timeUs;
        if (load.ExitDurationUs > 0)
        {
            long effUs = Math.Min(ExitDurationCapUs, Math.Max(1L, load.ExitDurationUs));
            return Math.Clamp((timeUs - hideUs) / (double)effUs, 0.0, 1.0);
        }
        return Math.Clamp(load.ExitProgress, 0.0, 1.0);
    }

    private static LineGeometrySample? FindLineGeometry(ImmutableArray<LineGeometrySample> lines, LineTarget target)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Target == target)
                return lines[i];
        }
        return null;
    }

    private static TokenGeometrySample? FindTokenGeometry(ImmutableArray<TokenGeometrySample> tokens, TokenFamilyTarget target)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].WithRuby == target)
                return tokens[i];
        }
        return null;
    }

    private static Rect2 MapTokenUnionToCanvas(TokenGeometrySample token)
    {
        Rect2 result = MapBoundsToCanvas(token.BaseGeometry);
        var rubies = token.Ruby;
        for (int i = 0; i < rubies.Length; i++)
            result = Union(result, MapBoundsToCanvas(rubies[i].Geometry));
        return result;
    }

    private static Rect2 MapBoundsToCanvas(GeometrySample geometry)
    {
        var b = geometry.InkBounds ?? geometry.AdvanceBounds;
        Point2 p0 = geometry.MapPoint(new Point2(b.Left, b.Top));
        Point2 p1 = geometry.MapPoint(new Point2(b.Right, b.Top));
        Point2 p2 = geometry.MapPoint(new Point2(b.Right, b.Bottom));
        Point2 p3 = geometry.MapPoint(new Point2(b.Left, b.Bottom));
        double left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        double top = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        double right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        double bottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new Rect2(left, top, Math.Max(left + 1.0, right), Math.Max(top + 1.0, bottom));
    }

    private static Rect2 Union(Rect2 a, Rect2 b) => new(
        Math.Min(a.Left, b.Left),
        Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Max(a.Bottom, b.Bottom));

    private static double CleanFloat(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < 1e-6)
            return 0.0;
        return value;
    }
}
