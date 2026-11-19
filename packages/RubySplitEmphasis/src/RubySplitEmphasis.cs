using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct RubySegmentSpan(int Utf16Start, int Utf16End, TimeRange? TimeRange);

public sealed record RubySplitData(
    double BasePeakScale,
    double RubyPeakScale,
    bool BurstMode,
    long AttackUs,
    long ReturnUs,
    CurveValue GrowCurve,
    CurveValue ReturnCurve,
    string BaseAnchor,
    string RubyAnchor,
    bool RubyFallbackSlice,
    bool FlashGlow,
    ColorRgba GlowColor,
    double GlowBlurPx,
    double GlowOpacity,
    ImmutableDictionary<RubyId, ImmutableArray<RubySegmentSpan>> RubySegments) : PreparedEffect;

/// <summary>主字注音分离强调：主字与注音各自独立缩放；注音仅放大当前唱到的片段/字簇，未分段时可按宽度切片兜底，不做位置避让。</summary>
public sealed class RubySplitEmphasis : IPureKaraokeEffect<RubySplitData>
{
    public RubySplitData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        var rubyMap = ImmutableDictionary.CreateBuilder<RubyId, ImmutableArray<RubySegmentSpan>>();

        foreach (var ruby in source.Content.Ruby)
        {
            var spans = ImmutableArray.CreateBuilder<RubySegmentSpan>(ruby.Segments.Length);
            int offset = 0;
            for (int i = 0; i < ruby.Segments.Length; i++)
            {
                var segment = ruby.Segments[i];
                int len = segment.Text.Length;
                TimeRange? usable = segment.TimingStatus is SourceTimingStatus.Imported
                    or SourceTimingStatus.Estimated
                    or SourceTimingStatus.Edited
                    or SourceTimingStatus.Confirmed
                    && segment.TimeRange is { IsValid: true } range
                        ? range
                        : null;
                spans.Add(new RubySegmentSpan(offset, offset + len, usable));
                offset += len;
            }
            rubyMap[ruby.Id] = spans.ToImmutable();
        }

        return new RubySplitData(
            p.GetNumber("basePeakScale"),
            p.GetNumber("rubyPeakScale"),
            string.Equals(p.GetEnum("emphasisMode"), "burst", StringComparison.Ordinal),
            p.GetDuration("attackUs"),
            p.GetDuration("returnUs"),
            p.GetCurve("growCurve"),
            p.GetCurve("returnCurve"),
            p.GetEnum("baseAnchor"),
            p.GetEnum("rubyAnchor"),
            p.GetBoolean("rubyFallbackSlice"),
            p.GetBoolean("flashGlow"),
            p.GetColor("glowColor"),
            p.GetNumber("glowBlurPx"),
            p.GetNumber("glowOpacity"),
            rubyMap.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, RubySplitData data, TextOutput output)
    {
        var geometries = frame.Geometry;

        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var perf = token.Performance;
            TimeRange? tokenRange = perf.ProgressSource == ProgressSource.Token ? perf.TimeRange : null;

            var geometry = FindGeometry(geometries, token.WithRuby);
            if (geometry is null)
                continue;

            // 1. 主字单独缩放与辉光（仅作用于 BaseOnly，不连带 Ruby）
            if (tokenRange is { } baseRange && geometry.BaseGeometry.CanTransformIndependently)
            {
                double baseWeight = ComputeWeight(frame.TimeUs, baseRange, data);
                if (baseWeight > 0.0005)
                {
                    double baseScale = Math.Max(0.1, 1.0 + (data.BasePeakScale - 1.0) * baseWeight);
                    if (Math.Abs(baseScale - 1.0) > 0.0005)
                    {
                        output.AppendTransform(
                            token.BaseOnly,
                            Affine2.ScaleAt(baseScale, ResolvePivot(geometry.BaseGeometry, data.BaseAnchor)));
                    }

                    if (data.FlashGlow && data.GlowOpacity > 0.0 && data.GlowBlurPx > 0.0)
                    {
                        double alpha = Math.Clamp(data.GlowOpacity * baseWeight, 0.0, 1.0);
                        if (alpha > 0.001)
                        {
                            output.SetGlow(
                                token.BaseOnly,
                                TextState.Sung,
                                new GlowValue(data.GlowColor, alpha, data.GlowBlurPx, spreadPx: 2.0 * baseWeight));
                        }
                    }
                }
            }

            // 2. Ruby 逐个字簇独立缩放（只缩放当前唱到/回落中的那一个）
            var rubies = geometry.Ruby;
            var clusters = geometry.Clusters;
            for (int r = 0; r < rubies.Length; r++)
            {
                RubyId rubyId = rubies[r].RubyId;
                ImmutableArray<RubySegmentSpan> segments = data.RubySegments.ContainsKey(rubyId)
                    ? data.RubySegments[rubyId]
                    : ImmutableArray<RubySegmentSpan>.Empty;

                int rubyClusterCount = 0;
                double totalRubyWidth = 0.0;
                for (int c = 0; c < clusters.Length; c++)
                {
                    var cluster = clusters[c];
                    if (cluster.Target.RubyId == rubyId)
                    {
                        rubyClusterCount++;
                        totalRubyWidth += Math.Max(0.0, cluster.Geometry.AdvanceBounds.Width);
                    }
                }

                if (rubyClusterCount == 0)
                    continue;

                int rubyOrd = 0;
                double rubyWidthBefore = 0.0;
                for (int c = 0; c < clusters.Length; c++)
                {
                    var cluster = clusters[c];
                    if (cluster.Target.RubyId != rubyId)
                        continue;

                    double clusterWidth = Math.Max(0.0, cluster.Geometry.AdvanceBounds.Width);
                    int segIdx = FindSegmentIndex(segments, cluster.Utf16Start);

                    TimeRange? clusterRange = null;
                    if (segIdx >= 0 && segments[segIdx].TimeRange is { } segRange)
                    {
                        int segCount = 0;
                        int segOrd = 0;
                        double segWidthBefore = 0.0;
                        double segTotalWidth = 0.0;

                        for (int k = 0; k < clusters.Length; k++)
                        {
                            var peer = clusters[k];
                            if (peer.Target.RubyId != rubyId)
                                continue;
                            if (FindSegmentIndex(segments, peer.Utf16Start) != segIdx)
                                continue;

                            double w = Math.Max(0.0, peer.Geometry.AdvanceBounds.Width);
                            if (k == c)
                            {
                                segOrd = segCount;
                                segWidthBefore = segTotalWidth;
                            }
                            segCount++;
                            segTotalWidth += w;
                        }

                        clusterRange = segCount <= 1 || !data.RubyFallbackSlice
                            ? segRange
                            : SliceRange(segRange, segOrd, segCount, segWidthBefore, clusterWidth, segTotalWidth);
                    }
                    else if (tokenRange is { } fallbackRange)
                    {
                        clusterRange = !data.RubyFallbackSlice
                            ? fallbackRange
                            : SliceRange(fallbackRange, rubyOrd, rubyClusterCount, rubyWidthBefore, clusterWidth, totalRubyWidth);
                    }

                    rubyOrd++;
                    rubyWidthBefore += clusterWidth;

                    if (clusterRange is not { } activeRange || !cluster.Geometry.CanTransformIndependently)
                        continue;

                    double rubyWeight = ComputeWeight(frame.TimeUs, activeRange, data);
                    if (rubyWeight <= 0.0005)
                        continue;

                    double rubyScale = Math.Max(0.1, 1.0 + (data.RubyPeakScale - 1.0) * rubyWeight);
                    if (Math.Abs(rubyScale - 1.0) > 0.0005)
                    {
                        output.AppendTransform(
                            cluster.Target,
                            Affine2.ScaleAt(rubyScale, ResolvePivot(cluster.Geometry, data.RubyAnchor)));
                    }
                }
            }
        }
    }

    private static double ComputeWeight(long timeUs, TimeRange range, RubySplitData data)
    {
        if (data.BurstMode)
        {
            long elapsed = timeUs - range.StartUs;
            if (elapsed < 0)
                return 0.0;

            long effectiveAttack = Math.Min(data.AttackUs, range.DurationUs);
            if (effectiveAttack > 0 && elapsed < effectiveAttack)
            {
                return Curves.EaseOut(elapsed / (double)effectiveAttack);
            }

            if (data.ReturnUs <= 0)
                return 0.0;

            long decayElapsed = elapsed - effectiveAttack;
            if (decayElapsed >= data.ReturnUs)
                return 0.0;

            double u = Math.Clamp(decayElapsed / (double)data.ReturnUs, 0.0, 1.0);
            return Math.Max(0.0, 1.0 - data.ReturnCurve.Evaluate(u));
        }

        if (range.Contains(timeUs))
        {
            double progress = Math.Clamp((timeUs - range.StartUs) / (double)Math.Max(1L, range.DurationUs), 0.0, 1.0);
            return Math.Max(0.0, data.GrowCurve.Evaluate(progress));
        }

        if (timeUs >= range.EndUs && data.ReturnUs > 0)
        {
            long elapsedAfterEnd = timeUs - range.EndUs;
            if (elapsedAfterEnd < data.ReturnUs)
            {
                double u = Math.Clamp(elapsedAfterEnd / (double)data.ReturnUs, 0.0, 1.0);
                return Math.Max(0.0, 1.0 - data.ReturnCurve.Evaluate(u));
            }
        }

        return 0.0;
    }

    private static int FindSegmentIndex(ImmutableArray<RubySegmentSpan> segments, int utf16Start)
    {
        if (segments.IsDefaultOrEmpty)
            return -1;

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (utf16Start >= seg.Utf16Start && utf16Start < seg.Utf16End)
                return i;
        }

        return segments.Length - 1;
    }

    private static TimeRange SliceRange(TimeRange parent, int index, int count, double widthBefore, double clusterWidth, double totalWidth)
    {
        if (count <= 1)
            return parent;

        double f0;
        double f1;
        if (totalWidth > 0.001 && clusterWidth > 0.0)
        {
            f0 = Math.Clamp(widthBefore / totalWidth, 0.0, 1.0);
            f1 = Math.Clamp((widthBefore + clusterWidth) / totalWidth, f0, 1.0);
        }
        else
        {
            f0 = index / (double)count;
            f1 = (index + 1) / (double)count;
        }

        long duration = parent.DurationUs;
        long start = parent.StartUs + (long)Math.Round(duration * f0);
        long end = parent.StartUs + (long)Math.Round(duration * f1);
        if (end <= start)
        {
            end = Math.Min(parent.EndUs, start + 1L);
            if (end <= start)
                start = Math.Max(parent.StartUs, end - 1L);
        }

        return new TimeRange(start, end);
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
}
