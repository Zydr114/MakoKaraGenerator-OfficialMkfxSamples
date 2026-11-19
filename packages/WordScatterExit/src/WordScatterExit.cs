using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record WordScatterExitData(
    long ExitUs,
    double FallPx,
    double DriftPx,
    double SpinDegrees,
    double EndScale,
    double MaxBlurSigmaPx,
    double RubyScatterPx,
    double RubySpinDegrees,
    CurveValue ExitCurve,
    CurveValue OpacityCurve,
    bool ExitGlow,
    ColorRgba GlowColor,
    double GlowBlurPx,
    double GlowOpacity,
    ImmutableArray<GraphBindingValue> BlurBindings,
    ImmutableDictionary<TokenId, ulong> TokenSeeds) : PreparedEffect;

/// <summary>逐字散落退出：每个字（含独立飘散的注音字簇）在演唱完成后自行旋转、缩小、下落、淡出并叠加高斯模糊与余晖。</summary>
public sealed class WordScatterExit : IPureKaraokeEffect<WordScatterExitData>
{
    public WordScatterExitData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var seedBuilder = ImmutableDictionary.CreateBuilder<TokenId, ulong>();
        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            seedBuilder[tokens[i].Id] = (ulong)i;
        }

        var blurGraph = source.Resources.GetGraph("token-blur");
        var canvasTransforms = new GraphTransformValues([
            new("fit", new GraphTransformValue(Affine2.Identity, new Rect2(0, 0, source.CanvasSize.X, source.CanvasSize.Y)))
        ]);
        double maxBlurSigmaPx = Math.Clamp(p.GetNumber("maxBlurSigmaPx"), 0.0, 24.0);
        const int blurSteps = 64;
        var blurBuilder = ImmutableArray.CreateBuilder<GraphBindingValue>(blurSteps + 1);
        for (int step = 0; step <= blurSteps; step++)
        {
            double sigma = maxBlurSigmaPx * (step / (double)blurSteps);
            blurBuilder.Add(new GraphBindingValue(
                blurGraph,
                new ParameterValues([new("blurSigmaPx", new ParameterValue.Number(sigma))]),
                GraphUniformValues.Empty,
                canvasTransforms));
        }

        return new WordScatterExitData(
            p.GetDuration("exitUs"),
            p.GetNumber("fallPx"),
            p.GetNumber("driftPx"),
            p.GetNumber("spinDegrees"),
            p.GetNumber("endScale"),
            maxBlurSigmaPx,
            p.GetNumber("rubyScatterPx"),
            p.GetNumber("rubySpinDegrees"),
            p.GetCurve("exitCurve"),
            p.GetCurve("opacityCurve"),
            p.GetBoolean("exitGlow"),
            p.GetColor("glowColor"),
            p.GetNumber("glowBlurPx"),
            p.GetNumber("glowOpacity"),
            blurBuilder.ToImmutable(),
            seedBuilder.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, WordScatterExitData data, TextOutput output)
    {
        var geometries = frame.Geometry;
        var loads = frame.LineLoads;

        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var geometry = FindGeometry(geometries, token.WithRuby);
            if (geometry is null || !geometry.BaseGeometry.CanTransformIndependently)
                continue;

            var load = FindLineLoad(loads, token.BindingId);
            double progress = ResolveExitProgress(frame.TimeUs, token.Performance, load, data.ExitUs);
            if (progress < 0.0)
                continue;

            double opacityProgress = Math.Clamp(data.OpacityCurve.Evaluate(progress), 0.0, 1.0);
            double opacity = Math.Clamp(1.0 - opacityProgress, 0.0, 1.0);
            if (progress >= 1.0 || opacity <= 0.002)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            double motion = Math.Max(0.0, data.ExitCurve.Evaluate(progress));
            output.MultiplyOpacity(token.WithRuby, opacity);

            ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : 0UL;
            double rFall = frame.Random.GetUnitDouble("exit-fall", seed);
            double rDrift = frame.Random.GetUnitDouble("exit-drift", seed);
            double rSpin = frame.Random.GetUnitDouble("exit-spin", seed);

            double baseScale = Math.Max(0.05, 1.0 + (data.EndScale - 1.0) * motion);
            double baseAngle = (data.SpinDegrees * Math.PI / 180.0) * (0.75 + 0.50 * rSpin) * motion;
            double baseDx = (data.DriftPx + (rDrift - 0.5) * 14.0) * motion;
            double baseDy = data.FallPx * (0.85 + 0.30 * rFall) * motion;

            output.AppendTransform(
                token.BaseOnly,
                BuildTransform(geometry.BaseGeometry.AdvanceBounds.Center, baseScale, baseAngle, baseDx, baseDy));

            var clusters = geometry.Clusters;
            for (int i = 0; i < clusters.Length; i++)
            {
                var cluster = clusters[i];
                if (cluster.Target.RubyId is null || !cluster.Geometry.CanTransformIndependently)
                    continue;

                ulong cSeed = unchecked(seed * 131UL + (ulong)(cluster.Target.UnitIndex + 1));
                double cRandX = frame.Random.GetUnitDouble("ruby-x", cSeed);
                double cRandY = frame.Random.GetUnitDouble("ruby-y", cSeed);
                double cRandSpin = frame.Random.GetUnitDouble("ruby-spin", cSeed);
                double cRandScale = frame.Random.GetUnitDouble("ruby-scale", cSeed);

                double rubyScale = Math.Max(0.05, 1.0 + (data.EndScale * (0.88 + 0.22 * cRandScale) - 1.0) * motion);
                double rubyAngle = ((data.SpinDegrees * 0.45) + (cRandSpin - 0.5) * 2.0 * data.RubySpinDegrees) * (Math.PI / 180.0) * motion;
                double rubyDx = (data.DriftPx * 0.8 + (cRandX - 0.5) * 2.0 * data.RubyScatterPx) * motion;
                double rubyDy = (data.FallPx * 0.45 + (cRandY - 0.65) * data.RubyScatterPx) * motion;

                output.AppendTransform(
                    cluster.Target,
                    BuildTransform(cluster.Geometry.AdvanceBounds.Center, rubyScale, rubyAngle, rubyDx, rubyDy));
            }

            if (data.ExitGlow && data.GlowOpacity > 0.0 && data.GlowBlurPx > 0.0)
            {
                double glowRamp = Math.Clamp(progress / 0.2, 0.35, 1.0);
                double glowAlpha = Math.Clamp(data.GlowOpacity * glowRamp, 0.0, 1.0);
                if (glowAlpha > 0.001)
                {
                    output.SetGlow(
                        token.WithRuby,
                        TextState.Sung,
                        new GlowValue(data.GlowColor, glowAlpha, data.GlowBlurPx, spreadPx: 2.0 * motion));
                }
            }

            if (data.MaxBlurSigmaPx > 0.05 && data.BlurBindings.Length > 1)
            {
                int blurIndex = (int)Math.Round(Math.Clamp(motion, 0.0, 1.0) * (data.BlurBindings.Length - 1));
                double sigma = data.MaxBlurSigmaPx * (blurIndex / (double)(data.BlurBindings.Length - 1));
                if (sigma > 0.05)
                {
                    output.BindGraph(token.WithRuby, data.BlurBindings[blurIndex]);
                }
            }
        }
    }

    private static double ResolveExitProgress(long timeUs, PerformanceSample perf, TextLineLoad? load, long exitUs)
    {
        if (perf.ProgressSource == ProgressSource.Token && perf.TimeRange is { } range)
        {
            long elapsedAfterEnd = timeUs - range.EndUs;
            if (elapsedAfterEnd >= 0)
            {
                return exitUs <= 0 ? 1.0 : Math.Clamp(elapsedAfterEnd / (double)exitUs, 0.0, 1.0);
            }

            if (load is { Phase: TextLinePhase.ExitReplica })
            {
                return Math.Clamp(load.ExitProgress, 0.0, 1.0);
            }

            return -1.0;
        }

        if (load is { Phase: TextLinePhase.ExitReplica })
        {
            return Math.Clamp(load.ExitProgress, 0.0, 1.0);
        }

        return -1.0;
    }

    private static TokenGeometrySample? FindGeometry(ImmutableArray<TokenGeometrySample> geometries, TokenFamilyTarget target)
    {
        for (int i = 0; i < geometries.Length; i++)
        {
            if (geometries[i].WithRuby == target)
                return geometries[i];
        }
        return null;
    }

    private static TextLineLoad? FindLineLoad(ImmutableArray<TextLineLoad> loads, BindingId bindingId)
    {
        for (int i = 0; i < loads.Length; i++)
        {
            if (loads[i].Line.BindingId == bindingId)
                return loads[i];
        }
        return null;
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
}
