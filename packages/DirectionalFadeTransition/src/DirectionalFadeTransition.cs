using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record DirectionalFadeData(
    long EnterDurationUs,
    Vector2 EnterStartOffset,
    CurveValue EnterCurve,
    long ExitDurationUs,
    Vector2 ExitEndOffset,
    CurveValue ExitCurve,
    ImmutableDictionary<LineId, long> LineStarts) : PreparedEffect;

/// <summary>方向淡入淡出：简洁轻盈的整行出入场过渡，支持分别配置入场与出场的滑动方向（原地/左/右/上/下）、距离、时长与缓动曲线。</summary>
public sealed class DirectionalFadeTransition : IPureKaraokeEffect<DirectionalFadeData>
{
    public DirectionalFadeData Declare(DeclareContext source)
    {
        var p = source.Parameters;

        long enterDurationUs = p.GetDuration("enterDurationUs");
        double enterDistancePx = p.GetNumber("enterDistancePx");
        Vector2 enterStartOffset = ResolveEnterOffset(p.GetEnum("enterDirection"), enterDistancePx);
        CurveValue enterCurve = p.GetCurve("enterCurve");

        long exitDurationUs = p.GetDuration("exitDurationUs");
        double exitDistancePx = p.GetNumber("exitDistancePx");
        Vector2 exitEndOffset = ResolveExitOffset(p.GetEnum("exitDirection"), exitDistancePx);
        CurveValue exitCurve = p.GetCurve("exitCurve");

        var lineStarts = ImmutableDictionary.CreateBuilder<LineId, long>();
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { IsValid: true } range)
                lineStarts[lines[i].Id] = range.StartUs;
        }

        return new DirectionalFadeData(
            enterDurationUs,
            enterStartOffset,
            enterCurve,
            exitDurationUs,
            exitEndOffset,
            exitCurve,
            lineStarts.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, DirectionalFadeData data, TextOutput output)
    {
        var tokens = frame.Presentations.TokensInBinding;
        if (tokens.Length == 0)
            return;

        var geometries = frame.Geometry;
        var loads = frame.LineLoads;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var geometry = FindGeometry(geometries, token.WithRuby);
            if (geometry is null || !geometry.BaseGeometry.CanTransformIndependently)
                continue;

            var load = FindLineLoad(loads, token.BindingId);
            var presLoad = frame.Indexes.Presentations.Get(token.BindingId);

            // 1. 出场阶段（ExitReplica）：从原位按指定方向滑出并淡出，完成后持续保持隐藏
            if (load is { Phase: TextLinePhase.ExitReplica })
            {
                long maxWindowUs = load.ExitDurationUs;
                long effectiveExitUs = maxWindowUs > 0
                    ? Math.Min(data.ExitDurationUs, maxWindowUs)
                    : data.ExitDurationUs;

                if (effectiveExitUs <= 0)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                long hideUs = load.HideTimeUs ?? presLoad?.HideTimeUs ?? frame.TimeUs;
                long exitElapsedUs = Math.Max(0L, frame.TimeUs - hideUs);
                if (exitElapsedUs >= effectiveExitUs)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                double rawT = Math.Clamp(exitElapsedUs / (double)effectiveExitUs, 0.0, 1.0);
                double motion = data.ExitCurve.Evaluate(rawT);
                double opacity = Math.Clamp(1.0 - motion, 0.0, 1.0);
                if (opacity <= 0.002)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                output.MultiplyOpacity(token.WithRuby, opacity);

                double dx = data.ExitEndOffset.X * motion;
                double dy = data.ExitEndOffset.Y * motion;
                if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
                {
                    output.AppendTransform(token.WithRuby, Affine2.Translation(dx, dy));
                }
                continue;
            }

            // 2. 入场阶段：从指定方向滑入到位并柔和淡入
            if (data.EnterDurationUs <= 0)
                continue;

            long revealUs = presLoad is not null
                ? presLoad.RevealTimeUs
                : (data.LineStarts.ContainsKey(token.LineId) ? data.LineStarts[token.LineId] : 0L);

            long effectiveEnterUs = data.EnterDurationUs;
            if (presLoad?.HideTimeUs is { } lineHideUs && lineHideUs > revealUs)
            {
                effectiveEnterUs = Math.Min(effectiveEnterUs, lineHideUs - revealUs);
            }
            if (effectiveEnterUs <= 0)
                continue;

            long enterElapsedUs = frame.TimeUs - revealUs;
            if (enterElapsedUs < 0)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            if (enterElapsedUs >= effectiveEnterUs)
                continue;

            double enterT = Math.Clamp(enterElapsedUs / (double)effectiveEnterUs, 0.0, 1.0);
            double enterMotion = data.EnterCurve.Evaluate(enterT);
            double enterOpacity = Math.Clamp(enterMotion, 0.0, 1.0);
            if (enterOpacity <= 0.002)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            output.MultiplyOpacity(token.WithRuby, enterOpacity);

            double remain = 1.0 - enterMotion;
            double enterDx = data.EnterStartOffset.X * remain;
            double enterDy = data.EnterStartOffset.Y * remain;
            if (Math.Abs(enterDx) > 0.01 || Math.Abs(enterDy) > 0.01)
            {
                output.AppendTransform(token.WithRuby, Affine2.Translation(enterDx, enterDy));
            }
        }
    }

    private static Vector2 ResolveEnterOffset(string direction, double distancePx)
    {
        double dist = Math.Max(0.0, distancePx);
        return direction switch
        {
            "fromLeft" => new Vector2(-dist, 0.0),
            "fromRight" => new Vector2(dist, 0.0),
            "fromTop" => new Vector2(0.0, -dist),
            "fromBottom" => new Vector2(0.0, dist),
            _ => Vector2.Zero,
        };
    }

    private static Vector2 ResolveExitOffset(string direction, double distancePx)
    {
        double dist = Math.Max(0.0, distancePx);
        return direction switch
        {
            "toLeft" => new Vector2(-dist, 0.0),
            "toRight" => new Vector2(dist, 0.0),
            "toTop" => new Vector2(0.0, -dist),
            "toBottom" => new Vector2(0.0, dist),
            _ => Vector2.Zero,
        };
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
}
