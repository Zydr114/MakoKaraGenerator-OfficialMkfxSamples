using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct CrtAudioState(
    double Bass,
    double Pulse,
    double Decay,
    double Phase,
    double OnsetBoost);

public sealed record CyberCrtData(
    GraphHandle CrtGraph,
    ParameterValues GraphParameters,
    double ScanlineIntensity,
    double GlitchStrength,
    double AberrationPx,
    ColorRgba PhosphorTint,
    bool CollapseExit,
    double FallbackBpm,
    ImmutableDictionary<LineId, long> LineStarts) : PreparedEffect;

/// <summary>赛博 CRT 荧光与故障撕裂：音频脉冲与节拍驱动行级扫描线、RGB 荧光粉格栅、块状故障色散与 CRT 关机坍缩退场。</summary>
public sealed class CyberCrtGlitch : IPureKaraokeEffect<CyberCrtData>
{
    private const long ExitDurationCapUs = 600_000L;

    public CyberCrtData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var graph = source.Resources.GetGraph("cyber-crt-line");
        var graphParameters = new ParameterValues([
            new("bloomSigmaPx", new ParameterValue.Number(p.GetNumber("bloomSigmaPx")))
        ]);

        var lineStarts = ImmutableDictionary.CreateBuilder<LineId, long>();
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { IsValid: true } range)
                lineStarts[lines[i].Id] = range.StartUs;
        }

        return new CyberCrtData(
            graph,
            graphParameters,
            p.GetNumber("scanlineIntensity"),
            p.GetNumber("glitchStrength"),
            p.GetNumber("aberrationPx"),
            p.GetColor("phosphorTint"),
            p.GetBoolean("collapseExit"),
            p.GetNumber("fallbackBpm"),
            lineStarts.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, CyberCrtData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        var tokens = frame.Presentations.TokensInBinding;
        var geometries = frame.Geometry;
        var lineGeometries = frame.LineGeometry;

        long timeStep = Math.Max(0L, frame.TimeUs / 60_000L);
        double stepRand = frame.Random.GetUnitDouble("crt-glitch-step", (ulong)timeStep);

        for (int l = 0; l < loads.Length; l++)
        {
            var load = loads[l];
            var lineGeom = FindLineGeometry(lineGeometries, load.Target);
            if (lineGeom is null)
                continue;

            var presLoad = frame.Indexes.Presentations.Get(load.Line.BindingId);
            double lineOnset = ComputeLineTokenOnset(tokens, load.Line.BindingId, frame.TimeUs);
            CrtAudioState audio = SampleAudio(frame.Audio, frame.TimeUs, data.FallbackBpm, lineOnset);

            double exitProgress = ResolveExitProgress(frame.TimeUs, load, presLoad);
            Rect2 lineCanvasBounds = ComputeLineCanvasBounds(lineGeom, tokens, geometries, load.Line.BindingId);

            double burstRaw = Math.Max(audio.Pulse * 0.95, 0.55 * audio.Decay + 0.45 * audio.Bass);
            double glitchBurst = Math.Clamp(burstRaw * (0.62 + 0.58 * stepRand), 0.0, 1.0);
            if (exitProgress > 0.01)
                glitchBurst = Math.Max(glitchBurst, 0.50 * (1.0 - exitProgress));

            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>("crtGlitch", "scanlineIntensity"), UniformValue.Float(CleanFloat(data.ScanlineIntensity))),
                new(new ValueTuple<string, string>("crtGlitch", "glitchStrength"), UniformValue.Float(CleanFloat(data.GlitchStrength))),
                new(new ValueTuple<string, string>("crtGlitch", "aberrationPx"), UniformValue.Float(CleanFloat(data.AberrationPx))),
                new(new ValueTuple<string, string>("crtGlitch", "phosphorTint"), UniformValue.Color(data.PhosphorTint)),
                new(new ValueTuple<string, string>("crtGlitch", "glitchBurst"), UniformValue.Float(CleanFloat(glitchBurst))),
                new(new ValueTuple<string, string>("crtGlitch", "bassLevel"), UniformValue.Float(CleanFloat(audio.Bass))),
                new(new ValueTuple<string, string>("crtGlitch", "beatPhase"), UniformValue.Float(CleanFloat(audio.Phase))),
                new(new ValueTuple<string, string>("crtGlitch", "glitchSeed"), UniformValue.Float(CleanFloat(stepRand))),
                new(new ValueTuple<string, string>("crtGlitch", "exitProgress"), UniformValue.Float(CleanFloat(exitProgress))),
                new(new ValueTuple<string, string>("crtGlitch", "collapseExit"), UniformValue.Float(data.CollapseExit ? 1.0 : 0.0)),
                new(new ValueTuple<string, string>("crtGlitch", "lineBounds"), UniformValue.Float4(
                    CleanFloat(lineCanvasBounds.Left),
                    CleanFloat(lineCanvasBounds.Top),
                    CleanFloat(lineCanvasBounds.Right),
                    CleanFloat(lineCanvasBounds.Bottom))),
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.CrtGraph,
                data.GraphParameters,
                uniforms,
                GraphTransformValues.Empty));
        }
    }

    private static CrtAudioState SampleAudio(AudioFeatures audio, long timeUs, double fallbackBpm, double onsetBoost)
    {
        long t0 = Math.Max(0L, timeUs);
        var s0 = audio.GetAt(t0);
        if (s0.Availability == AudioAvailability.Available && s0.Values is { } v0)
        {
            double p1 = SamplePulseAt(audio, Math.Max(0L, t0 - 45_000L));
            double p2 = SamplePulseAt(audio, Math.Max(0L, t0 - 90_000L));
            double decay = Math.Clamp(Math.Max(v0.Pulse, Math.Max(p1 * 0.72, p2 * 0.42)), 0.0, 1.0);
            return new CrtAudioState(
                Math.Clamp(v0.BassLevel, 0.0, 1.0),
                Math.Clamp(v0.Pulse, 0.0, 1.0),
                Math.Max(decay, onsetBoost * 0.45),
                v0.BeatPhase ?? ((t0 / 1_000_000d) % 1.0),
                onsetBoost);
        }

        if (fallbackBpm > 0.0)
        {
            double beats = (t0 / 1_000_000d) * (fallbackBpm / 60.0);
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.6);
            return new CrtAudioState(
                Math.Clamp(0.24 + 0.68 * impulse, 0.0, 1.0),
                Math.Clamp(impulse, 0.0, 1.0),
                Math.Max(impulse, onsetBoost * 0.55),
                phase,
                onsetBoost);
        }

        return new CrtAudioState(
            Math.Clamp(0.25 * onsetBoost, 0.0, 1.0),
            Math.Clamp(onsetBoost, 0.0, 1.0),
            Math.Clamp(onsetBoost, 0.0, 1.0),
            (t0 / 1_000_000d) % 1.0,
            onsetBoost);
    }

    private static double SamplePulseAt(AudioFeatures audio, long timeUs)
    {
        var s = audio.GetAt(timeUs);
        return s.Availability == AudioAvailability.Available && s.Values is { } v ? v.Pulse : 0.0;
    }

    private static double ComputeLineTokenOnset(ImmutableArray<FrameToken> tokens, BindingId bindingId, long timeUs)
    {
        double best = 0.0;
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            if (token.Performance.IsCurrent && token.Performance.TimeRange is { IsValid: true } range)
            {
                long elapsed = Math.Max(0L, timeUs - range.StartUs);
                double spike = Math.Exp(-(elapsed / 1_000_000.0) * 11.0);
                if (spike > best)
                    best = spike;
            }
        }
        return Math.Clamp(best, 0.0, 1.0);
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

    private static Rect2 ComputeLineCanvasBounds(
        LineGeometrySample lineGeom,
        ImmutableArray<FrameToken> tokens,
        ImmutableArray<TokenGeometrySample> geometries,
        BindingId bindingId)
    {
        Rect2 bounds = MapBoundsToCanvas(lineGeom.BaseGeometry);
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            var geom = FindTokenGeometry(geometries, token.WithRuby);
            if (geom is null)
                continue;

            bounds = Union(bounds, MapTokenUnionToCanvas(geom));
        }
        return bounds;
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
