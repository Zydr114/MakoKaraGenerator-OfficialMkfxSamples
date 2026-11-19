using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct Row8(
    double C0, double C1, double C2, double C3,
    double C4, double C5, double C6, double C7)
{
    public double Get(int col) => col switch
    {
        0 => C0,
        1 => C1,
        2 => C2,
        3 => C3,
        4 => C4,
        5 => C5,
        6 => C6,
        _ => C7,
    };
}

public readonly record struct LumaGrid8x4(Row8 R0, Row8 R1, Row8 R2, Row8 R3)
{
    public double Get(int row, int col) => row switch
    {
        0 => R0.Get(col),
        1 => R1.Get(col),
        2 => R2.Get(col),
        _ => R3.Get(col),
    };
}

public readonly record struct ZoneFlow(double Vx, double Vy, double Contrast, double Energy);

public readonly record struct OpticalFlowSummary(
    bool HasMediaFlow,
    double GlobalVx,
    double GlobalVy,
    double Divergence,
    double Activity,
    ZoneFlow Z0,
    ZoneFlow Z1,
    ZoneFlow Z2,
    ZoneFlow Z3);

public readonly record struct AudioBurstState(double Bass, double Pulse, double Decay, double Phase);

public readonly record struct TokenInkBox(
    int Index,
    Rect2 CanvasRect,
    double ActiveEnv,
    double SungProgress,
    double BurstPulse,
    double Seed01);

public readonly record struct LineExitTiming(double PostSungLoosen, double ExitProgress);

public sealed record OpticalInkData(
    GraphHandle InkGraph,
    ParameterValues GraphParameters,
    MediaQueryId FlowQuery,
    Vector2 CanvasSize,
    ColorRgba InkTint,
    ColorRgba MetalTint,
    double InvertThreshold,
    double EdgeSensitivity,
    double InkTailLengthPx,
    double BurstRadiusPx,
    double ClimbStrength,
    double ExitWashPx,
    long EnterCutUs,
    long ExitUs,
    double TrailIntensity,
    double FallbackBpm,
    ImmutableDictionary<LineId, long> LineStarts,
    ImmutableDictionary<TokenId, ulong> TokenSeeds,
    UniformValue ZeroRect,
    UniformValue ZeroState,
    MaterialValue FallbackInkFill,
    MaterialValue FallbackActiveFill,
    StrokeValue FallbackRimStroke,
    StrokeValue FallbackActiveStroke,
    ImmutableArray<GlowValue> FallbackGlows) : PreparedEffect;

/// <summary>光流墨迹与负空间侵蚀：结合粗粒度 Lucas-Kanade 画面风场估计、高反差正负片翻转、鼓点爆墨轮廓攀爬与急流冲刷稀释。</summary>
public sealed class OpticalInkErosion : IPureKaraokeEffect<OpticalInkData>, IPureVisualEffect<OpticalInkData>
{
    private const int MaxSlots = 8;
    private const int GlowSteps = 16;
    private const long FlowLookbackUs = 50_000L;
    private const long TrailStepUs = 60_000L;
    private static readonly OutputId TrailsOutputId = new("ink-trails");
    private static readonly OutputId DotsOutputId = new("ink-dots");

    public OpticalInkData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var graph = source.Resources.GetGraph("optical-ink-line");
        var graphParams = new ParameterValues([
            new("backdropSource", new ParameterValue.Choice(p.GetEnum("backdropSource")))
        ]);

        var lineStarts = ImmutableDictionary.CreateBuilder<LineId, long>();
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { IsValid: true } range)
                lineStarts[lines[i].Id] = range.StartUs;
        }

        var tokenSeeds = ImmutableDictionary.CreateBuilder<TokenId, ulong>();
        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
            tokenSeeds[tokens[i].Id] = (ulong)(i + 1);

        ColorRgba inkTint = p.GetColor("inkTint");
        ColorRgba metalTint = p.GetColor("metalTint");

        var glowBuilder = ImmutableArray.CreateBuilder<GlowValue>(GlowSteps + 1);
        for (int s = 0; s <= GlowSteps; s++)
        {
            double k = s / (double)GlowSteps;
            glowBuilder.Add(new GlowValue(metalTint, 0.30 + 0.55 * k, 4.0 + 10.0 * k, 1.0 + 1.5 * k));
        }

        return new OpticalInkData(
            graph,
            graphParams,
            source.Media.GetId("flow"),
            source.CanvasSize,
            inkTint,
            metalTint,
            p.GetNumber("invertThreshold"),
            p.GetNumber("edgeSensitivity"),
            p.GetNumber("inkTailLengthPx"),
            p.GetNumber("burstRadiusPx"),
            p.GetNumber("climbStrength"),
            p.GetNumber("exitWashPx"),
            p.GetDuration("enterCutUs"),
            p.GetDuration("exitUs"),
            p.GetNumber("trailIntensity"),
            p.GetNumber("fallbackBpm"),
            lineStarts.ToImmutable(),
            tokenSeeds.ToImmutable(),
            UniformValue.Float4(0, 0, 0, 0),
            UniformValue.Float4(0, 0, 0, 0),
            MaterialValue.Solid(inkTint),
            MaterialValue.Solid(LerpColor(inkTint, metalTint, 0.35, 1.0)),
            new StrokeValue(2.2, MaterialValue.Solid(metalTint)),
            new StrokeValue(2.8, MaterialValue.Solid(metalTint)),
            glowBuilder.ToImmutable())
        {
            VisualOutputs =
            [
                new VisualOutputDeclaration(TrailsOutputId, VisualPrimitiveKind.Trails, OverlayLayer.BehindText),
                new VisualOutputDeclaration(DotsOutputId, VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, OpticalInkData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        AudioBurstState audio = SampleAudioBurst(frame.Audio, frame.TimeUs, data.FallbackBpm);
        OpticalFlowSummary flow = frame.OutputMode == OutputMode.Opaque
            ? EstimateOpticalFlow(frame.Media, data.FlowQuery, data.CanvasSize, frame.TimeUs, FlowLookbackUs)
            : EmptyFlow();

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

            double enterProgress = data.EnterCutUs <= 0
                ? 1.0
                : Math.Clamp((frame.TimeUs - revealUs) / (double)data.EnterCutUs, 0.0, 1.0);

            long lastTokenEndUs = FindLastTokenEndUs(tokens, load.Line.BindingId);
            LineExitTiming exitTiming = ResolveLineExit(frame.TimeUs, load, presLoad, lastTokenEndUs, data.ExitUs);

            if (exitTiming.ExitProgress >= 0.999)
            {
                HideLineTokens(tokens, geometries, load.Line.BindingId, output);
                continue;
            }

            if (frame.OutputMode == OutputMode.Transparent)
            {
                ApplyTransparentFallback(
                    frame, data, tokens, geometries, load.Line.BindingId,
                    enterProgress, exitTiming.ExitProgress, audio, output);
                continue;
            }

            Rect2 lineCanvasBounds = MapBoundsToCanvas(lineGeom.BaseGeometry);
            double sungCutoffX = -10000.0;
            bool hasTimedTokens = false;
            bool allTimedFinished = true;

            var activeBoxes = ImmutableArray.CreateBuilder<TokenInkBox>(MaxSlots);
            var transitionBoxes = ImmutableArray.CreateBuilder<TokenInkBox>(MaxSlots);
            var otherBoxes = ImmutableArray.CreateBuilder<TokenInkBox>(MaxSlots);

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
                double sungProgress = 0.0;
                double burstPulse = 0.0;

                if (token.Performance.ProgressSource == ProgressSource.Token && token.Performance.TimeRange is { IsValid: true } range)
                {
                    hasTimedTokens = true;
                    long startUs = range.StartUs;
                    long endUs = range.EndUs;

                    if (frame.TimeUs < startUs)
                    {
                        allTimedFinished = false;
                    }
                    else if (frame.TimeUs < endUs)
                    {
                        allTimedFinished = false;
                        long elapsedUs = frame.TimeUs - startUs;
                        long remainUs = endUs - frame.TimeUs;
                        double attack = SmoothStep(Math.Clamp(elapsedUs / 45_000.0, 0.0, 1.0));
                        double release = Math.Clamp(remainUs / 55_000.0, 0.40, 1.0);
                        activeEnv = attack * release;

                        // 起唱前 180ms 瞬态脉冲（配合音频瞬态或作为无音频时替补爆墨触发）
                        double onsetSpike = Math.Exp(-(elapsedUs / 1_000_000.0) * 14.0);
                        burstPulse = Math.Clamp(Math.Max(onsetSpike * 0.72, audio.Decay * activeEnv), 0.0, 1.0);
                    }
                    else
                    {
                        long afterUs = frame.TimeUs - endUs;
                        double tailFade = Math.Clamp(1.0 - afterUs / 140_000.0, 0.0, 1.0);
                        activeEnv = 0.30 * tailFade * tailFade;
                        sungProgress = Math.Clamp(afterUs / 180_000.0, 0.25, 1.0);
                        burstPulse = Math.Clamp(0.25 * tailFade * audio.Decay, 0.0, 1.0);
                        sungCutoffX = Math.Max(sungCutoffX, tokenCanvas.Right);
                    }
                }
                else
                {
                    activeEnv = exitTiming.ExitProgress > 0.0 ? 0.0 : 0.35;
                    sungProgress = exitTiming.ExitProgress > 0.0 ? 1.0 : 0.0;
                    burstPulse = audio.Decay * 0.5;
                }

                ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);
                double seed01 = frame.Random.GetUnitDouble("ink-slot", seed);
                var box = new TokenInkBox(i, tokenCanvas, activeEnv, sungProgress, burstPulse, seed01);

                if (activeEnv > 0.02)
                {
                    if (activeBoxes.Count < MaxSlots) activeBoxes.Add(box);
                }
                else if (sungProgress > 0.01 && sungProgress < 0.999)
                {
                    if (transitionBoxes.Count < MaxSlots) transitionBoxes.Add(box);
                }
                else if (otherBoxes.Count < MaxSlots)
                {
                    otherBoxes.Add(box);
                }
            }

            var chosen = ImmutableArray.CreateBuilder<TokenInkBox>(MaxSlots);
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
                        CleanFloat(b.SungProgress),
                        CleanFloat(b.BurstPulse),
                        CleanFloat(b.Seed01)));
                }
                else
                {
                    rectValues.Add(data.ZeroRect);
                    stateValues.Add(data.ZeroState);
                }
            }

            double allSungFlag = (hasTimedTokens && allTimedFinished) || load.Phase == TextLinePhase.ExitReplica ? 1.0 : 0.0;
            var localWinds = ImmutableArray.CreateBuilder<UniformValue>(4);
            localWinds.Add(UniformValue.Float4(CleanFloat(flow.Z0.Vx), CleanFloat(flow.Z0.Vy), CleanFloat(flow.Z0.Contrast), CleanFloat(flow.Z0.Energy)));
            localWinds.Add(UniformValue.Float4(CleanFloat(flow.Z1.Vx), CleanFloat(flow.Z1.Vy), CleanFloat(flow.Z1.Contrast), CleanFloat(flow.Z1.Energy)));
            localWinds.Add(UniformValue.Float4(CleanFloat(flow.Z2.Vx), CleanFloat(flow.Z2.Vy), CleanFloat(flow.Z2.Contrast), CleanFloat(flow.Z2.Energy)));
            localWinds.Add(UniformValue.Float4(CleanFloat(flow.Z3.Vx), CleanFloat(flow.Z3.Vy), CleanFloat(flow.Z3.Contrast), CleanFloat(flow.Z3.Energy)));

            double stillBoost = flow.Activity < 0.08 ? 1.0 : Math.Clamp(1.0 - flow.Activity, 0.0, 1.0);

            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>("ink", "enterProgress"), UniformValue.Float(CleanFloat(enterProgress))),
                new(new ValueTuple<string, string>("ink", "exitProgress"), UniformValue.Float(CleanFloat(exitTiming.ExitProgress))),
                new(new ValueTuple<string, string>("ink", "allSung"), UniformValue.Float(CleanFloat(allSungFlag))),
                new(new ValueTuple<string, string>("ink", "sungCutoffX"), UniformValue.Float(CleanFloat(sungCutoffX))),
                new(new ValueTuple<string, string>("ink", "lineBounds"), UniformValue.Float4(
                    CleanFloat(lineCanvasBounds.Left),
                    CleanFloat(lineCanvasBounds.Top),
                    CleanFloat(lineCanvasBounds.Right),
                    CleanFloat(lineCanvasBounds.Bottom))),
                new(new ValueTuple<string, string>("ink", "windGlobal"), UniformValue.Float4(
                    CleanFloat(flow.GlobalVx),
                    CleanFloat(flow.GlobalVy),
                    CleanFloat(flow.Divergence),
                    CleanFloat(flow.Activity))),
                new(new ValueTuple<string, string>("ink", "windLocal"), UniformValue.Array(localWinds.MoveToImmutable())),
                new(new ValueTuple<string, string>("ink", "audioState"), UniformValue.Float4(
                    CleanFloat(audio.Bass),
                    CleanFloat(audio.Pulse),
                    CleanFloat(audio.Decay),
                    CleanFloat(stillBoost))),
                new(new ValueTuple<string, string>("ink", "inkTint"), UniformValue.Color(data.InkTint)),
                new(new ValueTuple<string, string>("ink", "metalTint"), UniformValue.Color(data.MetalTint)),
                new(new ValueTuple<string, string>("ink", "invertThreshold"), UniformValue.Float(CleanFloat(data.InvertThreshold))),
                new(new ValueTuple<string, string>("ink", "edgeSensitivity"), UniformValue.Float(CleanFloat(data.EdgeSensitivity))),
                new(new ValueTuple<string, string>("ink", "inkTailLengthPx"), UniformValue.Float(CleanFloat(data.InkTailLengthPx))),
                new(new ValueTuple<string, string>("ink", "burstRadiusPx"), UniformValue.Float(CleanFloat(data.BurstRadiusPx))),
                new(new ValueTuple<string, string>("ink", "climbStrength"), UniformValue.Float(CleanFloat(data.ClimbStrength))),
                new(new ValueTuple<string, string>("ink", "exitWashPx"), UniformValue.Float(CleanFloat(data.ExitWashPx))),
                new(new ValueTuple<string, string>("ink", "slotCount"), UniformValue.Int(slotCount)),
                new(new ValueTuple<string, string>("ink", "slotRects"), UniformValue.Array(rectValues.MoveToImmutable())),
                new(new ValueTuple<string, string>("ink", "slotStates"), UniformValue.Array(stateValues.MoveToImmutable())),
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.InkGraph,
                data.GraphParameters,
                uniforms,
                GraphTransformValues.Empty));
        }
    }

    public void EvaluateVisuals(in VisualFrame frame, OpticalInkData data, VisualOutput output)
    {
        var trails = ImmutableArray.CreateBuilder<Trail>();
        var dots = ImmutableArray.CreateBuilder<Dot>();

        if (data.TrailIntensity <= 0.01)
        {
            output.DrawTrails(TrailsOutputId, ImmutableArray<Trail>.Empty, OverlayLayer.BehindText);
            output.DrawDots(DotsOutputId, ImmutableArray<Dot>.Empty, OverlayLayer.AboveText);
            return;
        }

        AudioBurstState audio = SampleAudioBurst(frame.Audio, frame.TimeUs, data.FallbackBpm);
        bool canSampleFlow = frame.OutputMode == OutputMode.Opaque
            && frame.Media.Catalog.Input?.State == MediaInputState.Available;

        // 读取 5 个历史时刻的粗网格以一次计算 4 段弯曲光流风场
        OpticalFlowSummary flow0 = EmptyFlow();
        OpticalFlowSummary flow1 = EmptyFlow();
        OpticalFlowSummary flow2 = EmptyFlow();
        OpticalFlowSummary flow3 = EmptyFlow();

        if (canSampleFlow && frame.TimeUs > 0)
        {
            long t0 = frame.TimeUs;
            long t1 = Math.Max(0L, t0 - TrailStepUs);
            long t2 = Math.Max(0L, t0 - 2 * TrailStepUs);
            long t3 = Math.Max(0L, t0 - 3 * TrailStepUs);
            long t4 = Math.Max(0L, t0 - 4 * TrailStepUs);

            LumaGrid8x4 g0 = SampleLumaGrid(frame.Media, data.FlowQuery, data.CanvasSize, t0);
            LumaGrid8x4 g1 = SampleLumaGrid(frame.Media, data.FlowQuery, data.CanvasSize, t1);
            LumaGrid8x4 g2 = SampleLumaGrid(frame.Media, data.FlowQuery, data.CanvasSize, t2);
            LumaGrid8x4 g3 = SampleLumaGrid(frame.Media, data.FlowQuery, data.CanvasSize, t3);
            LumaGrid8x4 g4 = SampleLumaGrid(frame.Media, data.FlowQuery, data.CanvasSize, t4);

            flow0 = SolveLucasKanade(g0, g1, data.CanvasSize);
            flow1 = SolveLucasKanade(g1, g2, data.CanvasSize);
            flow2 = SolveLucasKanade(g2, g3, data.CanvasSize);
            flow3 = SolveLucasKanade(g3, g4, data.CanvasSize);
        }

        var tokens = frame.FinalGeometry.Presentations.TokensInBinding;
        var tokenGeoms = frame.FinalGeometry.Tokens;
        var lines = frame.FinalGeometry.Presentations.LinesInBinding;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var geom = FindTokenGeometry(tokenGeoms, token.WithRuby);
            if (geom is null)
                continue;

            var line = FindFrameLine(lines, token.BindingId);
            if (line is not null && line.EnterProgress <= 0.05)
                continue;

            bool isActive = token.Performance.IsCurrent;
            long elapsedUs = token.Performance.ElapsedUs ?? 0L;
            long? endUs = token.Performance.TimeRange is { IsValid: true } tr ? tr.EndUs : null;
            long exitWindowUs = Math.Max(450_000L, Math.Min(data.ExitUs, 1_400_000L));
            bool isRecentSung = endUs.HasValue && frame.TimeUs >= endUs.Value && frame.TimeUs - endUs.Value < exitWindowUs;

            if (!isActive && !isRecentSung)
                continue;

            Rect2 box = MapTokenUnionToCanvas(geom);
            if (box.Width < 2 || box.Height < 2)
                continue;

            ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);
            double flowSpeed = Math.Sqrt(flow0.GlobalVx * flow0.GlobalVx + flow0.GlobalVy * flow0.GlobalVy);
            double attackBoost = isActive ? Math.Exp(-(Math.Max(0L, elapsedUs) / 1_000_000.0) * 9.0) : 0.0;
            double fadeOut = isRecentSung && endUs.HasValue ? Math.Clamp(1.0 - (frame.TimeUs - endUs.Value) / (double)exitWindowUs, 0.0, 1.0) : 1.0;

            double burstEnergy = Math.Clamp(Math.Max(audio.Decay, attackBoost * 0.85) * fadeOut, 0.0, 1.0);
            int strandCount = Math.Clamp((int)Math.Round(3.0 * data.TrailIntensity), 1, 6);

            if (flowSpeed > 2.2)
            {
                // 画面运动主导：顺着历史 4 步光流轨迹绘制平滑弯曲墨丝（突破单层材质边界，形成连贯墨丝与离散飞墨点）
                double totalReach = Math.Clamp(Math.Tanh(flowSpeed / 32.0) * 195.0 * data.TrailIntensity, 42.0, 230.0);
                double baseStepLen = totalReach / 5.0;

                for (int s = 0; s < strandCount; s++)
                {
                    ulong sKey = unchecked(seed * 97UL + (ulong)(s + 1));
                    double rx = frame.Random.GetUnitDouble("ink-trail-x", sKey);
                    double ry = frame.Random.GetUnitDouble("ink-trail-y", sKey);
                    double rw = frame.Random.GetUnitDouble("ink-trail-w", sKey);
                    double rColor = frame.Random.GetUnitDouble("ink-trail-c", sKey);

                    Point2 p0 = new(
                        box.Left + box.Width * (0.18 + 0.64 * rx),
                        box.Top + box.Height * (0.15 + 0.70 * ry));

                    double lenScale = 0.72 + 0.56 * rw;
                    double stepLen = baseStepLen * lenScale;
                    double curl = (rw - 0.5) * 11.0;

                    Point2 p1 = AdvanceFlowStep(p0, flow0, stepLen * 0.90, curl * 0.35);
                    Point2 p2 = AdvanceFlowStep(p1, flow1, stepLen * 1.00, curl * 0.65);
                    Point2 p3 = AdvanceFlowStep(p2, flow2, stepLen * 1.05, curl * 0.30);
                    Point2 p4 = AdvanceFlowStep(p3, flow3, stepLen * 1.10, -curl * 0.45);
                    Point2 p5 = AdvanceFlowStep(p4, flow3, stepLen * 0.95, curl * 0.25);

                    ColorRgba headCol = LerpColor(data.InkTint, data.MetalTint, rColor * 0.35, 0.90 * fadeOut);
                    ColorRgba midCol = new(headCol.Red, headCol.Green, headCol.Blue, 0.48 * fadeOut);
                    ColorRgba tipCol = new(headCol.Red, headCol.Green, headCol.Blue, 0.0);

                    double w0 = Math.Max(1.8, (3.8 + 3.6 * rw) * fadeOut);
                    double w1 = w0 * 0.80;
                    double w2 = w0 * 0.56;
                    double w3 = w0 * 0.32;
                    double w4 = w0 * 0.14;

                    trails.Add(new Trail([
                        new TrailPoint(p0, w0, headCol),
                        new TrailPoint(p1, w1, headCol),
                        new TrailPoint(p2, w2, midCol),
                        new TrailPoint(p3, w3, midCol),
                        new TrailPoint(p4, w4, tipCol),
                        new TrailPoint(p5, 0.0, tipCol),
                    ]));

                    if (s < 2)
                    {
                        double dotR = Math.Clamp((1.8 + 2.4 * rw) * fadeOut, 0.0, 5.5);
                        if (dotR >= 0.45)
                            dots.Add(new Dot(p3, dotR, headCol));
                    }
                }
            }
            else if (burstEnergy > 0.14)
            {
                // 静止背景 + 鼓点/起唱瞬态：向四周放射状炸开墨刺与溅射墨点
                int spikeCount = Math.Clamp((int)Math.Round(7.0 * data.TrailIntensity), 4, 10);
                double maxLen = Math.Max(22.0, data.BurstRadiusPx * (0.50 + 0.90 * burstEnergy));
                Point2 center = box.Center;

                for (int s = 0; s < spikeCount; s++)
                {
                    ulong sKey = unchecked(seed * 131UL + (ulong)(s + 1));
                    double rAng = frame.Random.GetUnitDouble("ink-burst-ang", sKey);
                    double rLen = frame.Random.GetUnitDouble("ink-burst-len", sKey);
                    double rW = frame.Random.GetUnitDouble("ink-burst-w", sKey);

                    double angle = (s / (double)spikeCount) * (Math.PI * 2.0) + (rAng - 0.5) * 0.55;
                    double len = maxLen * (0.55 + 0.68 * rLen);
                    double dx = Math.Cos(angle);
                    double dy = Math.Sin(angle);
                    double px = -dy;
                    double py = dx;

                    Point2 startPt = new(
                        center.X + dx * (box.Width * 0.22),
                        center.Y + dy * (box.Height * 0.22));
                    Point2 midPt = new(
                        startPt.X + dx * (len * 0.50) + px * ((rLen - 0.5) * 11.0),
                        startPt.Y + dy * (len * 0.50) + py * ((rLen - 0.5) * 11.0));
                    Point2 endPt = new(
                        startPt.X + dx * len,
                        startPt.Y + dy * len);

                    double alpha = Math.Clamp(0.92 * burstEnergy, 0.0, 1.0);
                    ColorRgba spikeHead = LerpColor(data.InkTint, data.MetalTint, 0.22 * rW, alpha);
                    ColorRgba spikeTail = new(spikeHead.Red, spikeHead.Green, spikeHead.Blue, 0.0);
                    double baseW = Math.Max(1.8, (4.4 + 4.8 * rW) * burstEnergy);

                    trails.Add(new Trail([
                        new TrailPoint(startPt, baseW, spikeHead),
                        new TrailPoint(midPt, baseW * 0.52, spikeHead),
                        new TrailPoint(endPt, 0.0, spikeTail),
                    ]));

                    double dotR = Math.Clamp((2.0 + 3.4 * rW) * burstEnergy, 0.0, 6.2);
                    if (dotR >= 0.45)
                    {
                        Point2 midDrop = new(
                            midPt.X + px * ((rW - 0.5) * 9.0),
                            midPt.Y + py * ((rW - 0.5) * 9.0));
                        dots.Add(new Dot(midDrop, dotR * 0.72, spikeHead));

                        Point2 dropPt = new(
                            endPt.X + dx * (6.0 + 11.0 * rLen),
                            endPt.Y + dy * (6.0 + 11.0 * rLen));
                        dots.Add(new Dot(dropPt, dotR, spikeHead));
                    }
                }
            }
        }

        output.DrawTrails(TrailsOutputId, trails.ToImmutable(), OverlayLayer.BehindText);
        output.DrawDots(DotsOutputId, dots.ToImmutable(), OverlayLayer.AboveText);
    }

    private static Point2 AdvanceFlowStep(Point2 origin, OpticalFlowSummary flow, double stepLengthPx, double perpJitter)
    {
        double vx = flow.GlobalVx;
        double vy = flow.GlobalVy;
        double len = Math.Sqrt(vx * vx + vy * vy);
        double ux = len > 0.05 ? vx / len : 1.0;
        double uy = len > 0.05 ? vy / len : 0.0;
        double px = -uy;
        double py = ux;
        return new Point2(
            origin.X + ux * stepLengthPx + px * perpJitter,
            origin.Y + uy * stepLengthPx + py * perpJitter);
    }

    private static OpticalFlowSummary EstimateOpticalFlow(
        MediaFeatures media,
        MediaQueryId query,
        Vector2 canvasSize,
        long timeUs,
        long lookbackUs)
    {
        if (media.Catalog.Input?.State != MediaInputState.Available || timeUs <= 0)
            return EmptyFlow();

        long t0 = timeUs;
        long t1 = Math.Max(0L, timeUs - lookbackUs);
        if (t0 == t1)
            return EmptyFlow();

        // 按时刻顺序整张读取，以命中 MediaFeatureSampler 的单槽时间缓存
        LumaGrid8x4 g0 = SampleLumaGrid(media, query, canvasSize, t0);
        LumaGrid8x4 g1 = SampleLumaGrid(media, query, canvasSize, t1);
        return SolveLucasKanade(g0, g1, canvasSize);
    }

    private static LumaGrid8x4 SampleLumaGrid(
        MediaFeatures media,
        MediaQueryId query,
        Vector2 canvasSize,
        long timeUs)
    {
        double cw = canvasSize.X / 8.0;
        double ch = canvasSize.Y / 4.0;
        Row8 r0 = SampleRow(media, query, cw, ch, 0, canvasSize, timeUs);
        Row8 r1 = SampleRow(media, query, cw, ch, 1, canvasSize, timeUs);
        Row8 r2 = SampleRow(media, query, cw, ch, 2, canvasSize, timeUs);
        Row8 r3 = SampleRow(media, query, cw, ch, 3, canvasSize, timeUs);
        return new LumaGrid8x4(r0, r1, r2, r3);
    }

    private static Row8 SampleRow(
        MediaFeatures media,
        MediaQueryId query,
        double cw,
        double ch,
        int row,
        Vector2 canvasSize,
        long timeUs)
    {
        double top = Math.Clamp(row * ch, 0.0, Math.Max(0.0, canvasSize.Y - 1.0));
        double bottom = Math.Clamp((row + 1) * ch, top + 0.5, canvasSize.Y);
        double c0 = SampleCell(media, query, 0, cw, top, bottom, canvasSize.X, timeUs);
        double c1 = SampleCell(media, query, 1, cw, top, bottom, canvasSize.X, timeUs);
        double c2 = SampleCell(media, query, 2, cw, top, bottom, canvasSize.X, timeUs);
        double c3 = SampleCell(media, query, 3, cw, top, bottom, canvasSize.X, timeUs);
        double c4 = SampleCell(media, query, 4, cw, top, bottom, canvasSize.X, timeUs);
        double c5 = SampleCell(media, query, 5, cw, top, bottom, canvasSize.X, timeUs);
        double c6 = SampleCell(media, query, 6, cw, top, bottom, canvasSize.X, timeUs);
        double c7 = SampleCell(media, query, 7, cw, top, bottom, canvasSize.X, timeUs);
        return new Row8(c0, c1, c2, c3, c4, c5, c6, c7);
    }

    private static double SampleCell(
        MediaFeatures media,
        MediaQueryId query,
        int col,
        double cw,
        double top,
        double bottom,
        double maxW,
        long timeUs)
    {
        double left = Math.Clamp(col * cw, 0.0, Math.Max(0.0, maxW - 1.0));
        double right = Math.Clamp((col + 1) * cw, left + 0.5, maxW);
        var sample = media.Sample(query, new Rect2(left, top, right, bottom), timeUs);
        if (sample.CanvasValues is not { } vals)
            return 0.0;
        return Math.Sqrt(Math.Max(0.0, vals.MeanPremulY));
    }

    private static OpticalFlowSummary SolveLucasKanade(LumaGrid8x4 g0, LumaGrid8x4 g1, Vector2 canvasSize)
    {
        double cellW = canvasSize.X / 8.0;
        double cellH = canvasSize.Y / 4.0;
        // 强 Tikhonov 正则化抑制大网格尺度下的孔径问题与弱轴噪声放大
        const double globalReg = 0.36;
        const double zoneReg = 0.24;

        double gSxx = globalReg;
        double gSyy = globalReg;
        double gSxy = 0.0;
        double gBx = 0.0;
        double gBy = 0.0;
        double diffSum = 0.0;

        ZoneFlow z0 = SolveZone(g0, g1, 1, 2, cellW, cellH, zoneReg);
        ZoneFlow z1 = SolveZone(g0, g1, 2, 3, cellW, cellH, zoneReg);
        ZoneFlow z2 = SolveZone(g0, g1, 4, 5, cellW, cellH, zoneReg);
        ZoneFlow z3 = SolveZone(g0, g1, 5, 6, cellW, cellH, zoneReg);

        double divAccum = 0.0;
        int divCount = 0;

        for (int r = 1; r <= 2; r++)
        {
            for (int c = 1; c <= 6; c++)
            {
                double ix = ((g0.Get(r, c + 1) - g0.Get(r, c - 1)) + (g1.Get(r, c + 1) - g1.Get(r, c - 1))) * 0.25;
                double iy = ((g0.Get(r + 1, c) - g0.Get(r - 1, c)) + (g1.Get(r + 1, c) - g1.Get(r - 1, c))) * 0.25;
                double it = g0.Get(r, c) - g1.Get(r, c);

                gSxx += ix * ix;
                gSyy += iy * iy;
                gSxy += ix * iy;
                gBx -= ix * it;
                gBy -= iy * it;
                diffSum += Math.Abs(it);

                double lSxx = ix * ix + 0.08;
                double lSyy = iy * iy + 0.08;
                double lDet = lSxx * lSyy - (ix * iy) * (ix * iy);
                if (lDet > 1e-6)
                {
                    double lu = (lSyy * (-ix * it) - (ix * iy) * (-iy * it)) / lDet;
                    double lv = (lSxx * (-iy * it) - (ix * iy) * (-ix * it)) / lDet;
                    double rx = (c + 0.5) - 4.0;
                    double ry = (r + 0.5) - 2.0;
                    double rLen = Math.Sqrt(rx * rx + ry * ry);
                    if (rLen > 0.1)
                    {
                        divAccum += (lu * rx + lv * ry) / rLen;
                        divCount++;
                    }
                }
            }
        }

        double det = gSxx * gSyy - gSxy * gSxy;
        double u = det > 1e-6 ? (gSyy * gBx - gSxy * gBy) / det : 0.0;
        double v = det > 1e-6 ? (gSxx * gBy - gSxy * gBx) / det : 0.0;

        double rawVx = u * cellW;
        double rawVy = v * cellH;
        double rawSpeed = Math.Sqrt(rawVx * rawVx + rawVy * rawVy);
        double softSpeed = rawSpeed > 1e-4 ? Math.Tanh(rawSpeed / 48.0) * 68.0 : 0.0;
        double vxPx = rawSpeed > 1e-4 ? rawVx * (softSpeed / rawSpeed) : 0.0;
        double vyPx = rawSpeed > 1e-4 ? rawVy * (softSpeed / rawSpeed) : 0.0;

        double div = divCount > 0 ? Math.Clamp((divAccum / divCount) * cellW, -60.0, 60.0) : 0.0;
        double meanDiff = diffSum / 12.0;
        double activity = Math.Clamp(Math.Max(softSpeed / 22.0, meanDiff * 4.5), 0.0, 1.0);

        return new OpticalFlowSummary(
            activity > 0.02,
            vxPx,
            vyPx,
            div,
            activity,
            z0,
            z1,
            z2,
            z3);
    }

    private static ZoneFlow SolveZone(
        LumaGrid8x4 g0,
        LumaGrid8x4 g1,
        int colA,
        int colB,
        double cellW,
        double cellH,
        double reg)
    {
        double sxx = reg;
        double syy = reg;
        double sxy = 0.0;
        double bx = 0.0;
        double by = 0.0;
        double gradSum = 0.0;
        double diffSum = 0.0;

        for (int r = 1; r <= 2; r++)
        {
            for (int c = colA; c <= colB; c++)
            {
                double ix = ((g0.Get(r, c + 1) - g0.Get(r, c - 1)) + (g1.Get(r, c + 1) - g1.Get(r, c - 1))) * 0.25;
                double iy = ((g0.Get(r + 1, c) - g0.Get(r - 1, c)) + (g1.Get(r + 1, c) - g1.Get(r - 1, c))) * 0.25;
                double it = g0.Get(r, c) - g1.Get(r, c);

                sxx += ix * ix;
                syy += iy * iy;
                sxy += ix * iy;
                bx -= ix * it;
                by -= iy * it;
                gradSum += Math.Sqrt(ix * ix + iy * iy);
                diffSum += Math.Abs(it);
            }
        }

        double det = sxx * syy - sxy * sxy;
        double u = det > 1e-8 ? (syy * bx - sxy * by) / det : 0.0;
        double v = det > 1e-8 ? (sxx * by - sxy * bx) / det : 0.0;
        double vxPx = Math.Clamp(u * cellW * 0.45, -90.0, 90.0);
        double vyPx = Math.Clamp(v * cellH * 0.45, -90.0, 90.0);
        double contrast = Math.Clamp(gradSum * 0.55, 0.0, 1.0);
        double energy = Math.Clamp(Math.Max(Math.Sqrt(vxPx * vxPx + vyPx * vyPx) / 22.0, diffSum * 1.1), 0.0, 1.0);
        return new ZoneFlow(vxPx, vyPx, contrast, energy);
    }

    private static OpticalFlowSummary EmptyFlow()
    {
        var z = new ZoneFlow(0, 0, 0, 0);
        return new OpticalFlowSummary(false, 0, 0, 0, 0, z, z, z, z);
    }

    private static AudioBurstState SampleAudioBurst(AudioFeatures audio, long timeUs, double fallbackBpm)
    {
        long t0 = Math.Max(0L, timeUs);
        var s0 = audio.GetAt(t0);
        if (s0.Availability == AudioAvailability.Available && s0.Values is { } v0)
        {
            double p1 = SamplePulseAt(audio, Math.Max(0L, t0 - 40_000L));
            double p2 = SamplePulseAt(audio, Math.Max(0L, t0 - 80_000L));
            double p3 = SamplePulseAt(audio, Math.Max(0L, t0 - 120_000L));
            double decay = Math.Clamp(Math.Max(v0.Pulse, Math.Max(p1 * 0.72, Math.Max(p2 * 0.46, p3 * 0.25))), 0.0, 1.0);
            return new AudioBurstState(
                Math.Clamp(v0.BassLevel, 0.0, 1.0),
                Math.Clamp(v0.Pulse, 0.0, 1.0),
                decay,
                v0.BeatPhase ?? ((t0 / 1_000_000d) % 1.0));
        }

        if (fallbackBpm > 0.0)
        {
            double beats = (t0 / 1_000_000d) * (fallbackBpm / 60.0);
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.8);
            return new AudioBurstState(0.25 + 0.65 * impulse, impulse, impulse, phase);
        }

        return new AudioBurstState(0.0, 0.0, 0.0, 0.0);
    }

    private static double SamplePulseAt(AudioFeatures audio, long timeUs)
    {
        var s = audio.GetAt(timeUs);
        return s.Availability == AudioAvailability.Available && s.Values is { } v ? v.Pulse : 0.0;
    }

    private static LineExitTiming ResolveLineExit(
        long timeUs,
        TextLineLoad load,
        SourcePresentationLoad? presLoad,
        long lastTokenEndUs,
        long exitUs)
    {
        double postSung = 0.0;
        if (lastTokenEndUs > 0 && timeUs > lastTokenEndUs)
            postSung = Math.Clamp((timeUs - lastTokenEndUs) / 600_000.0, 0.0, 0.30);

        if (load.Phase != TextLinePhase.ExitReplica)
            return new LineExitTiming(postSung, postSung);

        long hideUs = load.HideTimeUs ?? presLoad?.HideTimeUs ?? timeUs;
        long effectiveExitUs = exitUs > 0 ? Math.Min(exitUs, Math.Max(1L, load.ExitDurationUs)) : Math.Max(1L, load.ExitDurationUs);
        double replicaProg = load.ExitDurationUs > 0
            ? Math.Clamp((timeUs - hideUs) / (double)effectiveExitUs, 0.0, 1.0)
            : Math.Clamp(load.ExitProgress, 0.0, 1.0);

        double baseAtHide = lastTokenEndUs > 0 && hideUs > lastTokenEndUs
            ? Math.Clamp((hideUs - lastTokenEndUs) / 600_000.0, 0.0, 0.30)
            : 0.0;
        double blended = baseAtHide + (1.0 - baseAtHide) * replicaProg;
        return new LineExitTiming(baseAtHide, Math.Clamp(blended, 0.0, 1.0));
    }

    private static void ApplyTransparentFallback(
        in TextFrame frame,
        OpticalInkData data,
        ImmutableArray<FrameToken> tokens,
        ImmutableArray<TokenGeometrySample> geometries,
        BindingId bindingId,
        double enterProgress,
        double exitProgress,
        AudioBurstState audio,
        TextOutput output)
    {
        double enterOpacity = Math.Clamp(enterProgress * 2.2, 0.0, 1.0);
        double exitFade = exitProgress > 0.001 ? Math.Pow(Math.Clamp(1.0 - exitProgress, 0.0, 1.0), 1.65) : 1.0;
        double lineOpacity = Math.Clamp(enterOpacity * exitFade, 0.0, 1.0);
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            var geom = FindTokenGeometry(geometries, token.WithRuby);
            if (geom is null || !geom.BaseGeometry.CanTransformIndependently)
                continue;

            output.MultiplyOpacity(token.WithRuby, lineOpacity);
            if (lineOpacity <= 0.002)
                continue;

            ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);
            double rDir = frame.Random.GetUnitDouble("ink-tr-dir", seed);

            if (exitProgress > 0.01)
            {
                double tOut = Math.Pow(exitProgress, 0.68);
                double dx = (rDir - 0.5) * 96.0 * tOut + 56.0 * tOut;
                double dy = (rDir - 0.45) * 110.0 * tOut - 34.0 * tOut;
                double scale = Math.Max(0.15, 1.0 - 0.58 * tOut);
                output.AppendTransform(token.WithRuby, BuildTransform(geom.BaseGeometry.AdvanceBounds.Center, scale, (rDir - 0.5) * 0.55 * tOut, dx, dy));
            }
            else if (token.Performance.IsCurrent)
            {
                double pulseScale = Math.Max(0.15, 1.0 + 0.08 * Math.Max(audio.Decay, 0.4));
                output.AppendTransform(token.WithRuby, Affine2.ScaleAt(pulseScale, geom.BaseGeometry.AdvanceBounds.Center));
            }

            if (token.Performance.IsCurrent)
            {
                output.SetFill(token.WithRuby, TextState.Both, data.FallbackActiveFill);
                output.SetStrokes(token.WithRuby, TextState.Both, data.FallbackActiveStroke);
                int gIdx = (int)Math.Round(Math.Clamp(audio.Decay, 0.0, 1.0) * GlowSteps);
                output.SetGlow(token.WithRuby, TextState.Both, data.FallbackGlows[gIdx]);
            }
            else
            {
                output.SetFill(token.WithRuby, TextState.Both, data.FallbackInkFill);
                output.SetStrokes(token.WithRuby, TextState.Both, data.FallbackRimStroke);
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

    private static long FindLastTokenEndUs(ImmutableArray<FrameToken> tokens, BindingId bindingId)
    {
        long maxEnd = 0L;
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;
            if (token.Performance.ProgressSource == ProgressSource.Token && token.Performance.TimeRange is { IsValid: true } range)
                maxEnd = Math.Max(maxEnd, range.EndUs);
        }
        return maxEnd;
    }

    private static FrameLine? FindFrameLine(ImmutableArray<FrameLine> lines, BindingId bindingId)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].BindingId == bindingId)
                return lines[i];
        }
        return null;
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

    private static void AppendUpTo(
        ImmutableArray<TokenInkBox>.Builder target,
        ImmutableArray<TokenInkBox>.Builder source,
        int maxCount)
    {
        for (int i = 0; i < source.Count && target.Count < maxCount; i++)
            target.Add(source[i]);
    }

    private static Affine2 BuildTransform(Point2 pivot, double scale, double angleRad, double dx, double dy)
    {
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        double a = scale * cos;
        double b = scale * sin;
        double c = -scale * sin;
        double d = scale * cos;
        double tx = pivot.X + dx - (a * pivot.X + c * pivot.Y);
        double ty = pivot.Y + dy - (b * pivot.X + d * pivot.Y);
        return new Affine2(a, b, c, d, tx, ty);
    }

    private static ColorRgba LerpColor(ColorRgba a, ColorRgba b, double t, double alpha)
    {
        double k = Math.Clamp(t, 0.0, 1.0);
        return new ColorRgba(
            Math.Clamp(a.Red + (b.Red - a.Red) * k, 0.0, 1.0),
            Math.Clamp(a.Green + (b.Green - a.Green) * k, 0.0, 1.0),
            Math.Clamp(a.Blue + (b.Blue - a.Blue) * k, 0.0, 1.0),
            Math.Clamp(alpha, 0.0, 1.0));
    }

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
