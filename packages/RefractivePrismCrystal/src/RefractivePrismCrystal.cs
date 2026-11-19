using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct TokenCanvasBox(
    int Index,
    Rect2 CanvasRect,
    double ActiveEnv,
    double Solidify,
    double BoilingEnv,
    double Seed01);

public sealed record RefractivePrismData(
    GraphHandle PrismGraph,
    ParameterValues GraphParameters,
    double RefractionPx,
    double DispersionPx,
    double ChromaBoost,
    double CausticStrength,
    double BoilingStrength,
    long LongNoteUs,
    double StrokeWidthPx,
    double ContrastThreshold,
    double FrostOpacity,
    long SolidifyUs,
    long EnterFadeUs,
    double DissolveScatterPx,
    ImmutableDictionary<LineId, long> LineStarts,
    ImmutableDictionary<TokenId, ulong> TokenSeeds,
    UniformValue ZeroRect,
    UniformValue ZeroState,
    MaterialValue FallbackWaitingFillMat,
    MaterialValue FallbackActiveFillMat,
    MaterialValue FallbackSungFillMat,
    StrokeValue FallbackWaitingStrokeVal,
    StrokeValue FallbackSungStrokeVal,
    ImmutableArray<GlowValue> FallbackActiveGlows) : PreparedEffect;

/// <summary>拟态折射棱镜：结合 C# 音节时序/音频脉冲控制与整行 SkSL 光学玻璃、局部自适应描边、聚光色散与边缘碎裂消融。</summary>
public sealed class RefractivePrismCrystal : IPureKaraokeEffect<RefractivePrismData>
{
    private const int MaxSlots = 9;
    private const int GlowSteps = 16;
    private static readonly ColorRgba FallbackWaitingFill = new(0.72, 0.88, 0.98, 0.30);
    private static readonly ColorRgba FallbackWaitingStroke = new(0.28, 0.44, 0.62, 0.84);
    private static readonly ColorRgba FallbackActiveFill = new(0.45, 0.90, 1.0, 0.96);
    private static readonly ColorRgba FallbackActiveGlow = new(0.26, 0.78, 1.0, 0.90);
    private static readonly ColorRgba FallbackSungFill = new(0.84, 0.90, 0.96, 0.62);
    private static readonly ColorRgba FallbackSungStroke = new(0.42, 0.54, 0.68, 0.82);

    public RefractivePrismData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var graph = source.Resources.GetGraph("refractive-prism-line");
        var graphParams = new ParameterValues([
            new("backdropSource", new ParameterValue.Choice(p.GetEnum("backdropSource"))),
            new("frostBlurPx", new ParameterValue.Number(p.GetNumber("frostBlurPx")))
        ]);

        var lineStarts = ImmutableDictionary.CreateBuilder<LineId, long>();
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { IsValid: true } range)
            {
                lineStarts[lines[i].Id] = range.StartUs;
            }
        }

        var tokenSeeds = ImmutableDictionary.CreateBuilder<TokenId, ulong>();
        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            tokenSeeds[tokens[i].Id] = (ulong)(i + 1);
        }

        double strokeWidth = p.GetNumber("strokeWidthPx");
        double caustic = p.GetNumber("causticStrength");
        var glowBuilder = ImmutableArray.CreateBuilder<GlowValue>(GlowSteps + 1);
        for (int step = 0; step <= GlowSteps; step++)
        {
            double energy = step / (double)GlowSteps;
            double blur = 6.0 + 6.0 * energy + 4.0 * caustic;
            glowBuilder.Add(new GlowValue(FallbackActiveGlow, 0.85, blur, 1.5));
        }

        return new RefractivePrismData(
            graph,
            graphParams,
            p.GetNumber("refractionPx"),
            p.GetNumber("dispersionPx"),
            p.GetNumber("chromaBoost"),
            caustic,
            p.GetNumber("boilingStrength"),
            p.GetDuration("longNoteUs"),
            strokeWidth,
            p.GetNumber("contrastThreshold"),
            p.GetNumber("frostOpacity"),
            p.GetDuration("solidifyUs"),
            p.GetDuration("enterFadeUs"),
            p.GetNumber("dissolveScatterPx"),
            lineStarts.ToImmutable(),
            tokenSeeds.ToImmutable(),
            UniformValue.Float4(0, 0, 0, 0),
            UniformValue.Float4(0, 0, 0, 0),
            MaterialValue.Solid(FallbackWaitingFill),
            MaterialValue.Solid(FallbackActiveFill),
            MaterialValue.Solid(FallbackSungFill),
            new StrokeValue(strokeWidth, MaterialValue.Solid(FallbackWaitingStroke)),
            new StrokeValue(strokeWidth, MaterialValue.Solid(FallbackSungStroke)),
            glowBuilder.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, RefractivePrismData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        double audioEnergy = SampleSmoothedAudio(frame.Audio, frame.TimeUs);
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
            long revealUs = presLoad is not null
                ? presLoad.RevealTimeUs
                : (data.LineStarts.ContainsKey(load.Line.LineId) ? data.LineStarts[load.Line.LineId] : 0L);

            double enterAlpha = data.EnterFadeUs <= 0
                ? 1.0
                : Math.Clamp((frame.TimeUs - revealUs) / (double)data.EnterFadeUs, 0.0, 1.0);

            double exitProgress = load.Phase == TextLinePhase.ExitReplica
                ? Math.Clamp(load.ExitProgress, 0.0, 1.0)
                : 0.0;

            if (exitProgress >= 0.999)
            {
                HideLineTokens(tokens, geometries, load.Line.BindingId, output);
                continue;
            }

            if (frame.OutputMode == OutputMode.Transparent)
            {
                ApplyTransparentFallback(
                    frame,
                    data,
                    tokens,
                    geometries,
                    load.Line.BindingId,
                    enterAlpha,
                    exitProgress,
                    audioEnergy,
                    output);
                continue;
            }

            Rect2 lineCanvasBounds = MapBoundsToCanvas(lineGeom.BaseGeometry);
            double sungCutoffX = -10000.0;
            bool hasTimedTokens = false;
            bool allTimedFinished = true;

            var activeBoxes = ImmutableArray.CreateBuilder<TokenCanvasBox>(MaxSlots);
            var transitionBoxes = ImmutableArray.CreateBuilder<TokenCanvasBox>(MaxSlots);
            var otherBoxes = ImmutableArray.CreateBuilder<TokenCanvasBox>(MaxSlots);

            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.BindingId != load.Line.BindingId)
                    continue;

                var tokenGeom = FindTokenGeometry(geometries, token.WithRuby);
                if (tokenGeom is null)
                    continue;

                Rect2 tokenCanvas = MapTokenUnionToCanvas(tokenGeom);
                lineCanvasBounds = Union(lineCanvasBounds, tokenCanvas);

                double activeEnv = 0.0;
                double solidify = 0.0;
                double boilingEnv = 0.0;

                if (token.Performance.ProgressSource == ProgressSource.Token && token.Performance.TimeRange is { IsValid: true } range)
                {
                    hasTimedTokens = true;
                    long startUs = range.StartUs;
                    long endUs = range.EndUs;
                    long durationUs = Math.Max(1L, endUs - startUs);

                    if (frame.TimeUs < startUs)
                    {
                        allTimedFinished = false;
                    }
                    else if (frame.TimeUs < endUs)
                    {
                        allTimedFinished = false;
                        long elapsedUs = frame.TimeUs - startUs;
                        long remainUs = endUs - frame.TimeUs;
                        double attack = Math.Clamp(elapsedUs / 60_000.0, 0.0, 1.0);
                        double release = Math.Clamp(remainUs / 60_000.0, 0.35, 1.0);
                        activeEnv = SmoothStep(attack) * release;

                        double sustainGate = Math.Clamp((durationUs - data.LongNoteUs * 0.55) / Math.Max(1.0, data.LongNoteUs), 0.0, 1.0);
                        double sustainRamp = Math.Clamp(elapsedUs / Math.Max(1.0, data.LongNoteUs * 0.35), 0.0, 1.0);
                        double sustainEnergy = sustainGate * sustainRamp;
                        boilingEnv = activeEnv * Math.Clamp(0.40 * audioEnergy + 0.85 * sustainEnergy * (0.45 + 0.55 * audioEnergy), 0.0, 1.0);
                    }
                    else
                    {
                        long afterUs = frame.TimeUs - endUs;
                        double tailFade = Math.Clamp(1.0 - afterUs / 90_000.0, 0.0, 1.0);
                        activeEnv = 0.35 * tailFade * tailFade;
                        solidify = data.SolidifyUs <= 0 ? 1.0 : Math.Clamp(afterUs / (double)data.SolidifyUs, 0.0, 1.0);
                        if (solidify < 0.999)
                        {
                            allTimedFinished = false;
                        }
                        else
                        {
                            sungCutoffX = Math.Max(sungCutoffX, tokenCanvas.Right);
                        }
                    }
                }
                else
                {
                    activeEnv = exitProgress > 0.0 ? 0.0 : 0.25;
                    solidify = exitProgress > 0.0 ? 1.0 : 0.0;
                }

                ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);
                double seed01 = frame.Random.GetUnitDouble("prism-slot", seed);
                var box = new TokenCanvasBox(i, tokenCanvas, activeEnv, solidify, boilingEnv, seed01);

                if (activeEnv > 0.01)
                {
                    if (activeBoxes.Count < MaxSlots)
                        activeBoxes.Add(box);
                }
                else if (solidify > 0.01 && solidify < 0.999)
                {
                    if (transitionBoxes.Count < MaxSlots)
                        transitionBoxes.Add(box);
                }
                else if (otherBoxes.Count < MaxSlots)
                {
                    otherBoxes.Add(box);
                }
            }

            var chosen = ImmutableArray.CreateBuilder<TokenCanvasBox>(MaxSlots);
            AppendUpTo(chosen, activeBoxes, MaxSlots);
            AppendUpTo(chosen, transitionBoxes, MaxSlots);
            AppendUpTo(chosen, otherBoxes, MaxSlots);

            int slotCount = chosen.Count;
            var rectValues = ImmutableArray.CreateBuilder<UniformValue>(MaxSlots);
            var stateValues = ImmutableArray.CreateBuilder<UniformValue>(MaxSlots);
            for (int i = 0; i < MaxSlots; i++)
            {
                if (i < slotCount)
                {
                    var b = chosen[i];
                    rectValues.Add(UniformValue.Float4(
                        CleanFloat(b.CanvasRect.Left),
                        CleanFloat(b.CanvasRect.Top),
                        CleanFloat(b.CanvasRect.Right),
                        CleanFloat(b.CanvasRect.Bottom)));
                    stateValues.Add(UniformValue.Float4(
                        CleanFloat(b.ActiveEnv),
                        CleanFloat(b.Solidify),
                        CleanFloat(b.BoilingEnv),
                        CleanFloat(b.Seed01)));
                }
                else
                {
                    rectValues.Add(data.ZeroRect);
                    stateValues.Add(data.ZeroState);
                }
            }

            double allSungFlag = (hasTimedTokens && allTimedFinished) || load.Phase == TextLinePhase.ExitReplica ? 1.0 : 0.0;
            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>("prism", "enterAlpha"), UniformValue.Float(CleanFloat(enterAlpha))),
                new(new ValueTuple<string, string>("prism", "exitProgress"), UniformValue.Float(CleanFloat(exitProgress))),
                new(new ValueTuple<string, string>("prism", "allSung"), UniformValue.Float(CleanFloat(allSungFlag))),
                new(new ValueTuple<string, string>("prism", "refractionPx"), UniformValue.Float(CleanFloat(data.RefractionPx))),
                new(new ValueTuple<string, string>("prism", "dispersionPx"), UniformValue.Float(CleanFloat(data.DispersionPx))),
                new(new ValueTuple<string, string>("prism", "chromaBoost"), UniformValue.Float(CleanFloat(data.ChromaBoost))),
                new(new ValueTuple<string, string>("prism", "causticStrength"), UniformValue.Float(CleanFloat(data.CausticStrength))),
                new(new ValueTuple<string, string>("prism", "boilingStrength"), UniformValue.Float(CleanFloat(data.BoilingStrength))),
                new(new ValueTuple<string, string>("prism", "strokeWidthPx"), UniformValue.Float(CleanFloat(data.StrokeWidthPx))),
                new(new ValueTuple<string, string>("prism", "contrastThreshold"), UniformValue.Float(CleanFloat(data.ContrastThreshold))),
                new(new ValueTuple<string, string>("prism", "frostOpacity"), UniformValue.Float(CleanFloat(data.FrostOpacity))),
                new(new ValueTuple<string, string>("prism", "dissolveScatterPx"), UniformValue.Float(CleanFloat(data.DissolveScatterPx))),
                new(new ValueTuple<string, string>("prism", "audioEnergy"), UniformValue.Float(CleanFloat(audioEnergy))),
                new(new ValueTuple<string, string>("prism", "sungCutoffX"), UniformValue.Float(CleanFloat(sungCutoffX))),
                new(new ValueTuple<string, string>("prism", "lineBounds"), UniformValue.Float4(
                    CleanFloat(lineCanvasBounds.Left),
                    CleanFloat(lineCanvasBounds.Top),
                    CleanFloat(lineCanvasBounds.Right),
                    CleanFloat(lineCanvasBounds.Bottom))),
                new(new ValueTuple<string, string>("prism", "slotCount"), UniformValue.Int(slotCount)),
                new(new ValueTuple<string, string>("prism", "slotRects"), UniformValue.Array(rectValues.MoveToImmutable())),
                new(new ValueTuple<string, string>("prism", "slotStates"), UniformValue.Array(stateValues.MoveToImmutable())),
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.PrismGraph,
                data.GraphParameters,
                uniforms,
                GraphTransformValues.Empty));
        }
    }

    private static void ApplyTransparentFallback(
        in TextFrame frame,
        RefractivePrismData data,
        ImmutableArray<FrameToken> tokens,
        ImmutableArray<TokenGeometrySample> geometries,
        BindingId bindingId,
        double enterAlpha,
        double exitProgress,
        double audioEnergy,
        TextOutput output)
    {
        double lineOpacity = Math.Clamp(enterAlpha * (1.0 - exitProgress), 0.0, 1.0);
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            var tokenGeom = FindTokenGeometry(geometries, token.WithRuby);
            if (tokenGeom is null || !tokenGeom.BaseGeometry.CanTransformIndependently)
                continue;

            output.MultiplyOpacity(token.WithRuby, lineOpacity);
            if (lineOpacity <= 0.002)
                continue;

            bool isActive = token.Performance.IsCurrent;
            bool isSung = token.Performance.IsSung && !isActive;

            if (isActive)
            {
                output.SetFill(token.WithRuby, TextState.Both, data.FallbackActiveFillMat);
                output.SetStrokes(token.WithRuby, TextState.Both, data.FallbackWaitingStrokeVal);
                int glowIdx = (int)Math.Round(Math.Clamp(audioEnergy, 0.0, 1.0) * GlowSteps);
                output.SetGlow(token.WithRuby, TextState.Both, data.FallbackActiveGlows[glowIdx]);
            }
            else if (isSung)
            {
                output.SetFill(token.WithRuby, TextState.Both, data.FallbackSungFillMat);
                output.SetStrokes(token.WithRuby, TextState.Both, data.FallbackSungStrokeVal);
                output.SetGlow(token.WithRuby, TextState.Both, null);
            }
            else
            {
                output.SetFill(token.WithRuby, TextState.Both, data.FallbackWaitingFillMat);
                output.SetStrokes(token.WithRuby, TextState.Both, data.FallbackWaitingStrokeVal);
                output.SetGlow(token.WithRuby, TextState.Both, null);
            }
        }
    }

    private static void HideLineTokens(
        ImmutableArray<FrameToken> tokens,
        ImmutableArray<TokenGeometrySample> geometries,
        BindingId bindingId,
        TextOutput output)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            var geom = FindTokenGeometry(geometries, token.WithRuby);
            if (geom is not null && geom.BaseGeometry.CanTransformIndependently)
                output.MultiplyOpacity(token.WithRuby, 0.0);
        }
    }

    private static double SampleSmoothedAudio(AudioFeatures audio, long timeUs)
    {
        long t0 = Math.Max(0L, timeUs);
        long t1 = Math.Max(0L, timeUs - 70_000L);
        long t2 = Math.Max(0L, timeUs - 140_000L);

        double e0 = SampleSingleAudio(audio, t0);
        double e1 = SampleSingleAudio(audio, t1);
        double e2 = SampleSingleAudio(audio, t2);
        return Math.Clamp(0.50 * e0 + 0.30 * e1 + 0.20 * e2, 0.0, 1.0);
    }

    private static double SampleSingleAudio(AudioFeatures audio, long timeUs)
    {
        var sample = audio.GetAt(timeUs);
        if (sample.Availability == AudioAvailability.Available && sample.Values is { } v)
            return Math.Clamp(0.55 * v.Pulse + 0.45 * v.BassLevel, 0.0, 1.0);
        return 0.0;
    }

    private static void AppendUpTo(
        ImmutableArray<TokenCanvasBox>.Builder target,
        ImmutableArray<TokenCanvasBox>.Builder source,
        int maxCount)
    {
        for (int i = 0; i < source.Count && target.Count < maxCount; i++)
            target.Add(source[i]);
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

    private static double SmoothStep(double x)
    {
        double t = Math.Clamp(x, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static double CleanFloat(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < 1e-6)
            return 0.0;
        return value;
    }
}
