using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct TokenHorizontalOrder(int Rank, int Count);

public readonly record struct FlipSchedule(long CharUs, long StaggerUs);

public sealed record WordFlipData(
    long EnterCharUs,
    long EnterStaggerUs,
    bool EnterRtl,
    CurveValue EnterCurve,
    CurveValue EnterOpacityCurve,
    long ExitCharUs,
    long ExitStaggerUs,
    bool ExitRtl,
    CurveValue ExitCurve,
    CurveValue ExitOpacityCurve,
    bool ExitMirrorAngles,
    double RotateXDeg,
    double RotateYDeg,
    double RotateZDeg,
    double EdgeScale,
    string AnchorMode,
    ImmutableDictionary<LineId, long> LineStarts) : PreparedEffect;

/// <summary>逐字翻转出入场：行显现时从左往右逐字（含注音）进行三维空间旋转展开到位；行退场时通过退出副本逐字反向翻转并淡出。</summary>
public sealed class WordFlipTransition : IPureKaraokeEffect<WordFlipData>
{
    public WordFlipData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var lineStarts = ImmutableDictionary.CreateBuilder<LineId, long>();
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TimeRange is { IsValid: true } range)
            {
                lineStarts[line.Id] = range.StartUs;
            }
        }

        return new WordFlipData(
            p.GetDuration("enterCharUs"),
            p.GetDuration("enterStaggerUs"),
            string.Equals(p.GetEnum("enterOrder"), "rtl", StringComparison.Ordinal),
            p.GetCurve("enterCurve"),
            p.GetCurve("enterOpacityCurve"),
            p.GetDuration("exitCharUs"),
            p.GetDuration("exitStaggerUs"),
            string.Equals(p.GetEnum("exitOrder"), "rtl", StringComparison.Ordinal),
            p.GetCurve("exitCurve"),
            p.GetCurve("exitOpacityCurve"),
            p.GetBoolean("exitMirrorAngles"),
            p.GetNumber("rotateXDeg"),
            p.GetNumber("rotateYDeg"),
            p.GetNumber("rotateZDeg"),
            p.GetNumber("edgeScale"),
            p.GetEnum("anchor"),
            lineStarts.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, WordFlipData data, TextOutput output)
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

            var order = ComputeHorizontalOrder(tokens, geometries, i, token.BindingId, geometry.BaseGeometry.AdvanceBounds.Center.X);
            int rank = order.Rank;
            int count = order.Count > 0 ? order.Count : 1;

            if (load is { Phase: TextLinePhase.ExitReplica })
            {
                long hideUs = load.HideTimeUs ?? presLoad?.HideTimeUs ?? frame.TimeUs;
                long exitElapsedUs = Math.Max(0L, frame.TimeUs - hideUs);
                long windowUs = load.ExitDurationUs;

                var schedule = ResolveSchedule(count, data.ExitCharUs, data.ExitStaggerUs, windowUs);
                long charUs = schedule.CharUs;
                long staggerUs = schedule.StaggerUs;
                int orderIndex = data.ExitRtl ? (count - 1 - rank) : rank;
                long startUs = orderIndex * staggerUs;

                if (exitElapsedUs < startUs)
                    continue;

                if (exitElapsedUs >= startUs + charUs)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                double rawT = Math.Clamp((exitElapsedUs - startUs) / (double)charUs, 0.0, 1.0);
                double motion = data.ExitCurve.Evaluate(rawT);
                double fade = Math.Clamp(data.ExitOpacityCurve.Evaluate(rawT), 0.0, 1.0);
                double opacity = Math.Clamp(1.0 - fade, 0.0, 1.0);
                if (opacity <= 0.002)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                output.MultiplyOpacity(token.WithRuby, opacity);
                if (Math.Abs(motion) > 0.0005 || Math.Abs(data.EdgeScale - 1.0) > 0.0005)
                {
                    double angleSign = data.ExitMirrorAngles ? -1.0 : 1.0;
                    Point2 pivot = ResolvePivot(geometry.BaseGeometry, data.AnchorMode);
                    output.AppendTransform(
                        token.WithRuby,
                        BuildFlipTransform(
                            pivot,
                            angleSign * data.RotateXDeg,
                            angleSign * data.RotateYDeg,
                            angleSign * data.RotateZDeg,
                            data.EdgeScale,
                            motion));
                }
                continue;
            }

            long revealUs = presLoad is not null
                ? presLoad.RevealTimeUs
                : (data.LineStarts.ContainsKey(token.LineId) ? data.LineStarts[token.LineId] : 0L);

            long? activeSpan = presLoad?.HideTimeUs is { } lineHideUs && lineHideUs > revealUs
                ? lineHideUs - revealUs
                : null;

            var enterSchedule = ResolveSchedule(
                count,
                data.EnterCharUs,
                data.EnterStaggerUs,
                activeSpan ?? 0L);
            long enterCharUs = enterSchedule.CharUs;
            long enterStaggerUs = enterSchedule.StaggerUs;

            int enterOrderIndex = data.EnterRtl ? (count - 1 - rank) : rank;
            long enterStartUs = enterOrderIndex * enterStaggerUs;
            long enterElapsedUs = frame.TimeUs - revealUs;

            if (enterElapsedUs < enterStartUs)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            if (enterElapsedUs >= enterStartUs + enterCharUs)
                continue;

            double enterT = Math.Clamp((enterElapsedUs - enterStartUs) / (double)enterCharUs, 0.0, 1.0);
            double enterMotion = data.EnterCurve.Evaluate(enterT);
            double enterFade = Math.Clamp(data.EnterOpacityCurve.Evaluate(enterT), 0.0, 1.0);
            if (enterFade <= 0.002)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            output.MultiplyOpacity(token.WithRuby, enterFade);
            double u = 1.0 - enterMotion;
            if (Math.Abs(u) > 0.0005 || Math.Abs(data.EdgeScale - 1.0) > 0.0005)
            {
                Point2 pivot = ResolvePivot(geometry.BaseGeometry, data.AnchorMode);
                output.AppendTransform(
                    token.WithRuby,
                    BuildFlipTransform(
                        pivot,
                        data.RotateXDeg,
                        data.RotateYDeg,
                        data.RotateZDeg,
                        data.EdgeScale,
                        u));
            }
        }
    }

    private static FlipSchedule ResolveSchedule(int count, long charUs, long staggerUs, long maxSpanUs)
    {
        long safeCharUs = Math.Max(1L, charUs);
        long safeStaggerUs = Math.Max(0L, staggerUs);
        long totalUs = safeCharUs + Math.Max(0, count - 1) * safeStaggerUs;
        if (maxSpanUs <= 0 || totalUs <= maxSpanUs)
            return new FlipSchedule(safeCharUs, safeStaggerUs);

        double ratio = maxSpanUs / (double)totalUs;
        long fittedChar = Math.Max(1L, (long)Math.Floor(safeCharUs * ratio));
        long fittedStagger = Math.Max(0L, (long)Math.Floor(safeStaggerUs * ratio));
        return new FlipSchedule(fittedChar, fittedStagger);
    }

    private static TokenHorizontalOrder ComputeHorizontalOrder(
        ImmutableArray<FrameToken> tokens,
        ImmutableArray<TokenGeometrySample> geometries,
        int targetIndex,
        BindingId bindingId,
        double targetCenterX)
    {
        int rank = 0;
        int count = 0;

        for (int j = 0; j < tokens.Length; j++)
        {
            if (tokens[j].BindingId != bindingId)
                continue;

            var peerGeom = FindGeometry(geometries, tokens[j].WithRuby);
            if (peerGeom is null)
                continue;

            count++;
            double peerX = peerGeom.BaseGeometry.AdvanceBounds.Center.X;
            if (peerX < targetCenterX - 0.0001 || (Math.Abs(peerX - targetCenterX) <= 0.0001 && j < targetIndex))
            {
                rank++;
            }
        }

        return new TokenHorizontalOrder(rank, count);
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

    private static Point2 ResolvePivot(GeometrySample geometry, string anchorMode)
    {
        var bounds = geometry.AdvanceBounds;
        return anchorMode switch
        {
            "baseline" => new Point2(bounds.Center.X, geometry.Baseline.Y),
            "bottom" => new Point2(bounds.Center.X, bounds.Bottom),
            _ => bounds.Center,
        };
    }

    private static Affine2 BuildFlipTransform(
        Point2 pivot,
        double rotateXDeg,
        double rotateYDeg,
        double rotateZDeg,
        double edgeScale,
        double u)
    {
        const double degToRad = Math.PI / 180.0;
        double rx = rotateXDeg * degToRad * u;
        double ry = rotateYDeg * degToRad * u;
        double rz = rotateZDeg * degToRad * u;

        double cosX = Math.Cos(rx);
        double sinX = Math.Sin(rx);
        double cosY = Math.Cos(ry);
        double sinY = Math.Sin(ry);
        double cosZ = Math.Cos(rz);
        double sinZ = Math.Sin(rz);

        // 保证 2D 投影在 float32 精度下始终可逆，避免 ±90° 侧立时出现奇异矩阵
        const double minProj = 0.05;
        if (Math.Abs(cosX) < minProj)
            cosX = cosX >= 0.0 ? minProj : -minProj;
        if (Math.Abs(cosY) < minProj)
            cosY = cosY >= 0.0 ? minProj : -minProj;

        double s = Math.Max(0.1, 1.0 + (edgeScale - 1.0) * u);

        // Orthographic projection of Rx(rx) * Ry(ry) * Rz(rz)
        double a = s * (cosY * cosZ);
        double c = s * (-cosY * sinZ);
        double b = s * (sinZ * cosX + cosZ * sinY * sinX);
        double d = s * (cosZ * cosX - sinZ * sinY * sinX);

        double tx = pivot.X - (a * pivot.X + c * pivot.Y);
        double ty = pivot.Y - (b * pivot.X + d * pivot.Y);
        return new Affine2(a, b, c, d, tx, ty);
    }
}
