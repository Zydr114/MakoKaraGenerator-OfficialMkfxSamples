using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct RepeatImpactState(
    bool Active,
    int Level,
    double Tier01,
    double ElapsedSec,
    double DecayT,
    double Envelope,
    double AttackFactor,
    double Spring);

public sealed record RepeatCadenceData(
    int MaxEscalation,
    double BasePopScale,
    double ScaleStep,
    double TremorPx,
    double TiltDeg,
    double SquashAmount,
    long AttackUs,
    long DecayUs,
    string AnchorMode,
    bool GlowEnabled,
    ColorRgba LowRepeatColor,
    ColorRgba HighRepeatColor,
    bool ShockwaveRings,
    ImmutableDictionary<OccurrenceId, int> OccurrenceOrdinals,
    ImmutableDictionary<TokenId, int> TokenOrdinals,
    ImmutableDictionary<TokenId, ulong> TokenSeeds) : PreparedEffect;

/// <summary>重复词素递进强调：统计词素在之前歌词中的出现次数，随重复次数逐级增强鼓点弹性缩放、挤压拉伸、高频震颤抖动、热感辉光与扩散冲击波环。</summary>
public sealed class RepeatCadenceEmphasis : IPureKaraokeEffect<RepeatCadenceData>, IPureVisualEffect<RepeatCadenceData>
{
    private static readonly OutputId RingsOutputId = new("impact-rings");
    private static readonly OutputId TicksOutputId = new("impact-ticks");
    private static readonly OutputId SparksOutputId = new("impact-sparks");

    public RepeatCadenceData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        bool branchScope = string.Equals(p.GetEnum("scope"), "branch", StringComparison.Ordinal);
        bool caseSensitive = p.GetBoolean("caseSensitive");
        bool ignorePunctuation = p.GetBoolean("ignorePunctuation");

        var occOrdinals = ImmutableDictionary.CreateBuilder<OccurrenceId, int>();
        var tokOrdinals = ImmutableDictionary.CreateBuilder<TokenId, int>();
        var tokSeeds = ImmutableDictionary.CreateBuilder<TokenId, ulong>();

        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            tokSeeds[tokens[i].Id] = (ulong)(i + 1);
        }

        var allStartedSelector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
        int eventCount = source.Timeline.Count(allStartedSelector);

        if (caseSensitive)
        {
            // 精确匹配模式：直接使用 SDK 原生 TimelineIndex.Ordinal 统计重复序号
            for (int i = 0; i < eventCount; i++)
            {
                if (source.Timeline.Nth(allStartedSelector, i) is not TokenStarted ev)
                    continue;

                string rawText = ev.Source.Text;
                if (!ShouldTrackText(rawText, ignorePunctuation))
                    continue;

                TimelineScope scope = branchScope
                    ? new TimelineScope.Branch(ev.BranchId)
                    : new TimelineScope.Project();
                var textSelector = new TimelineSelector(TimelineEventKind.TokenStarted, scope, rawText);
                int ordinal = source.Timeline.Ordinal(textSelector, ev.OccurrenceId) ?? 1;

                occOrdinals[ev.OccurrenceId] = ordinal;
                tokOrdinals[ev.TokenId] = ordinal;
            }
        }
        else
        {
            // 忽略大小写与首尾空白模式：沿时间线顺序统计规范化词素的累计出现次数
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < eventCount; i++)
            {
                if (source.Timeline.Nth(allStartedSelector, i) is not TokenStarted ev)
                    continue;

                string rawText = ev.Source.Text;
                if (!ShouldTrackText(rawText, ignorePunctuation))
                    continue;

                string normalized = rawText.Trim().ToLowerInvariant();
                string key = branchScope
                    ? string.Concat(ev.BranchId.Value, "\u001F", normalized)
                    : normalized;

                int nextCount = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                counts[key] = nextCount;

                occOrdinals[ev.OccurrenceId] = nextCount;
                tokOrdinals[ev.TokenId] = nextCount;
            }
        }

        return new RepeatCadenceData(
            (int)p.GetInteger("maxEscalation"),
            p.GetNumber("basePopScale"),
            p.GetNumber("scaleStep"),
            p.GetNumber("tremorPx"),
            p.GetNumber("tiltDeg"),
            p.GetNumber("squashAmount"),
            p.GetDuration("attackUs"),
            p.GetDuration("decayUs"),
            p.GetEnum("anchor"),
            p.GetBoolean("glowEnabled"),
            p.GetColor("lowRepeatColor"),
            p.GetColor("highRepeatColor"),
            p.GetBoolean("shockwaveRings"),
            occOrdinals.ToImmutable(),
            tokOrdinals.ToImmutable(),
            tokSeeds.ToImmutable())
        {
            VisualOutputs =
            [
                new VisualOutputDeclaration(RingsOutputId, VisualPrimitiveKind.Trails, OverlayLayer.BehindText),
                new VisualOutputDeclaration(TicksOutputId, VisualPrimitiveKind.Segments, OverlayLayer.BehindText),
                new VisualOutputDeclaration(SparksOutputId, VisualPrimitiveKind.Dots, OverlayLayer.BehindText),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, RepeatCadenceData data, TextOutput output)
    {
        var tokens = frame.Presentations.TokensInBinding;
        if (tokens.Length == 0)
            return;

        var geometries = frame.Geometry;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            int ordinal = ResolveOrdinal(token, data);
            if (ordinal < 2)
                continue;

            var impact = ResolveImpact(frame.TimeUs, token.Performance, ordinal, data);
            if (!impact.Active)
                continue;

            var geometry = FindGeometry(geometries, token.WithRuby);
            if (geometry is null || !geometry.BaseGeometry.CanTransformIndependently)
                continue;

            ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);
            double seedPhase = frame.Random.GetUnitDouble("repeat-phase", seed) * (Math.PI * 2.0);
            double dirSign = frame.Random.GetUnitDouble("repeat-sign", seed) < 0.5 ? -1.0 : 1.0;

            // 1. 随重复次数递进的鼓点缩放与鼓面挤压拉伸
            double peakScale = data.BasePopScale + (impact.Level - 1) * data.ScaleStep;
            double aftershock = 0.0;
            if (impact.Level >= 3)
            {
                // 高阶重复附加轻微二次余震回击
                double hit2 = (impact.DecayT - 0.24) / 0.14;
                aftershock = 0.18 * impact.Tier01 * Math.Exp(-hit2 * hit2);
            }

            double scaleEnv = Math.Clamp(impact.Envelope + aftershock, 0.0, 1.35);
            double mainScale = 1.0 + (peakScale - 1.0) * scaleEnv;

            double squashAmp = data.SquashAmount * (0.48 + 0.52 * impact.Tier01);
            double squashWave = squashAmp * impact.Spring * impact.AttackFactor;
            double sx = Math.Max(0.25, mainScale * (1.0 - squashWave));
            double sy = Math.Max(0.25, mainScale * (1.0 + squashWave));

            // 2. 随重复次数增强的高频阻尼震颤与微角度抖动
            double tremorDecay = Math.Exp(-4.6 * impact.DecayT) * (1.0 - 0.15 * impact.DecayT);
            double tremorGain = (0.34 + 0.66 * impact.Tier01) * Math.Max(0.0, tremorDecay) * impact.AttackFactor;
            if (aftershock > 0.01)
                tremorGain = Math.Min(1.25, tremorGain + aftershock * 1.4);

            double freqScale = 1.0 + 0.35 * impact.Tier01;
            double sec = impact.ElapsedSec;
            double ampPx = data.TremorPx * tremorGain;
            double dx = ampPx * (0.70 * Math.Sin(sec * 168.0 * freqScale + seedPhase) + 0.30 * Math.Cos(sec * 247.0 * freqScale + seedPhase * 1.6));
            double dyTremor = ampPx * 0.62 * (0.68 * Math.Cos(sec * 191.0 * freqScale + seedPhase * 1.2) + 0.32 * Math.Sin(sec * 283.0 * freqScale + seedPhase * 0.7));
            double liftY = -(3.5 + 7.5 * impact.Tier01) * scaleEnv;
            double dy = dyTremor + liftY;

            double tiltRad = (data.TiltDeg * Math.PI / 180.0) * tremorGain * dirSign
                * (0.72 * Math.Sin(sec * 136.0 * freqScale + seedPhase * 1.9) + 0.28 * Math.Cos(sec * 212.0 * freqScale + seedPhase));

            Point2 pivot = ResolvePivot(geometry.BaseGeometry, data.AnchorMode);
            output.AppendTransform(token.WithRuby, BuildKineticTransform(pivot, sx, sy, tiltRad, dx, dy));

            // 3. 随重复次数升温的脉冲辉光（从暖金渐进至炽红/洋红；作用于 Both 避免扫字交界产生竖直辉光切边）
            if (data.GlowEnabled)
            {
                ColorRgba glowColor = LerpColor(data.LowRepeatColor, data.HighRepeatColor, impact.Tier01, 1.0);
                double glowAlpha = Math.Clamp((0.52 + 0.44 * impact.Tier01) * Math.Min(1.0, scaleEnv * 1.15) * glowColor.Alpha, 0.0, 1.0);
                if (glowAlpha > 0.01)
                {
                    double blurPx = (8.0 + 13.0 * impact.Tier01) * (0.55 + 0.45 * Math.Min(1.0, scaleEnv));
                    double spreadPx = (1.0 + 3.0 * impact.Tier01) * Math.Min(1.0, scaleEnv);
                    output.SetGlow(token.WithRuby, TextState.Both, new GlowValue(glowColor, glowAlpha, blurPx, spreadPx));
                }
            }
        }
    }

    public void EvaluateVisuals(in VisualFrame frame, RepeatCadenceData data, VisualOutput output)
    {
        if (!data.ShockwaveRings)
        {
            output.DrawTrails(RingsOutputId, ImmutableArray<Trail>.Empty, OverlayLayer.BehindText);
            output.DrawSegments(TicksOutputId, ImmutableArray<Segment>.Empty, OverlayLayer.BehindText);
            output.DrawDots(SparksOutputId, ImmutableArray<Dot>.Empty, OverlayLayer.BehindText);
            return;
        }

        var tokens = frame.FinalGeometry.Presentations.TokensInBinding;
        var tokenGeoms = frame.FinalGeometry.Tokens;
        if (tokens.Length == 0 || tokenGeoms.Length == 0)
        {
            output.DrawTrails(RingsOutputId, ImmutableArray<Trail>.Empty, OverlayLayer.BehindText);
            output.DrawSegments(TicksOutputId, ImmutableArray<Segment>.Empty, OverlayLayer.BehindText);
            output.DrawDots(SparksOutputId, ImmutableArray<Dot>.Empty, OverlayLayer.BehindText);
            return;
        }

        var rings = ImmutableArray.CreateBuilder<Trail>();
        var ticks = ImmutableArray.CreateBuilder<Segment>();
        var sparks = ImmutableArray.CreateBuilder<Dot>();

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            int ordinal = ResolveOrdinal(token, data);
            if (ordinal < 2)
                continue;

            var impact = ResolveImpact(frame.TimeUs, token.Performance, ordinal, data);
            if (!impact.Active)
                continue;

            var geom = FindGeometry(tokenGeoms, token.WithRuby);
            if (geom is null)
                continue;

            Rect2 worldBox = MapTokenBox(geom);
            Point2 center = worldBox.Center;
            double baseRadius = Math.Max(22.0, Math.Max(worldBox.Width, worldBox.Height) * 0.56);
            ColorRgba tierColor = LerpColor(data.LowRepeatColor, data.HighRepeatColor, impact.Tier01, 1.0);
            ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);

            // 1. 随重复阶数增加同心鼓点冲击波环（第2次1环、第3次2环、第4次及以上3环）
            int ringCount = Math.Clamp(impact.Level, 1, 3);
            for (int r = 0; r < ringCount; r++)
            {
                double stagger = r * 0.11;
                double ringT = (impact.DecayT - stagger) / Math.Max(0.25, 0.88 - stagger);
                if (ringT <= 0.0 || ringT >= 1.0)
                    continue;

                double eased = Curves.EaseOut(ringT);
                double rx = baseRadius * (0.82 + (1.05 + 0.34 * r + 0.28 * impact.Tier01) * eased);
                double ry = rx * (0.86 + 0.10 * eased);
                double ringFade = (1.0 - Curves.EaseOut(ringT)) * Math.Clamp(ringT / 0.06, 0.0, 1.0);
                double ringAlpha = Math.Clamp((0.86 - 0.12 * r) * ringFade * tierColor.Alpha, 0.0, 1.0);
                if (ringAlpha <= 0.015)
                    continue;

                double strokeW = Math.Max(1.4, (4.8 + 2.8 * impact.Tier01 - 0.7 * r) * (1.0 - 0.62 * eased));
                ColorRgba ringCol = new(tierColor.Red, tierColor.Green, tierColor.Blue, ringAlpha);

                const int segs = 44;
                var pts = ImmutableArray.CreateBuilder<TrailPoint>(segs + 1);
                for (int s = 0; s <= segs; s++)
                {
                    double a = (s / (double)segs) * (Math.PI * 2.0);
                    double wobble = 1.0 + 0.025 * impact.Tier01 * Math.Sin(a * 6.0 + impact.ElapsedSec * 24.0);
                    Point2 pt = new(center.X + Math.Cos(a) * rx * wobble, center.Y + Math.Sin(a) * ry * wobble);
                    pts.Add(new TrailPoint(pt, strokeW, ringCol));
                }
                rings.Add(new Trail(pts.MoveToImmutable()));
            }

            // 2. 第 3 次及以上重复额外释放放射状节拍刻度与爆裂微点
            if (impact.Level >= 2 && impact.DecayT < 0.80)
            {
                double tickT = Math.Clamp(impact.DecayT / 0.80, 0.0, 1.0);
                double eased = Curves.EaseOut(tickT);
                double tickAlpha = Math.Clamp((1.0 - eased) * Math.Clamp(tickT / 0.06, 0.0, 1.0) * (0.78 + 0.22 * impact.Tier01), 0.0, 1.0);
                if (tickAlpha > 0.02)
                {
                    int tickCount = 6 + Math.Min(impact.Level, 4) * 2;
                    double baseAngle = frame.Random.GetUnitDouble("repeat-tick-ang", seed) * (Math.PI * 2.0);
                    ColorRgba tickCol = LerpColor(tierColor, new ColorRgba(1, 1, 1, 1), 0.26, tickAlpha);

                    for (int k = 0; k < tickCount; k++)
                    {
                        ulong kSeed = unchecked(seed * 193UL + (ulong)(k + 1));
                        double rLen = frame.Random.GetUnitDouble("repeat-tick-len", kSeed);
                        double angle = baseAngle + (k / (double)tickCount) * (Math.PI * 2.0);
                        double dx = Math.Cos(angle);
                        double dy = Math.Sin(angle);

                        double innerR = baseRadius * (0.95 + (0.86 + 0.28 * impact.Tier01) * eased);
                        double len = (13.0 + 16.0 * impact.Tier01) * (0.65 + 0.55 * rLen) * (1.0 - 0.45 * eased);
                        double width = Math.Max(1.6, (2.6 + 1.8 * impact.Tier01) * (1.0 - 0.50 * eased));

                        Point2 p0 = new(center.X + dx * innerR, center.Y + dy * innerR);
                        Point2 p1 = new(center.X + dx * (innerR + len), center.Y + dy * (innerR + len));
                        ticks.Add(new Segment(p0, p1, width, tickCol));

                        if (impact.Level >= 3 && (k & 1) == 0)
                        {
                            double dotR = Math.Max(1.5, (2.8 + 2.0 * impact.Tier01) * (1.0 - 0.55 * eased));
                            Point2 dp = new(center.X + dx * (innerR + len + 8.0 + 9.0 * eased), center.Y + dy * (innerR + len + 8.0 + 9.0 * eased));
                            sparks.Add(new Dot(dp, dotR, tickCol));
                        }
                    }
                }
            }
        }

        output.DrawTrails(RingsOutputId, rings.ToImmutable(), OverlayLayer.BehindText);
        output.DrawSegments(TicksOutputId, ticks.ToImmutable(), OverlayLayer.BehindText);
        output.DrawDots(SparksOutputId, sparks.ToImmutable(), OverlayLayer.BehindText);
    }

    private static int ResolveOrdinal(FrameToken token, RepeatCadenceData data)
    {
        if (token.OccurrenceId is { } occId && data.OccurrenceOrdinals.ContainsKey(occId))
            return data.OccurrenceOrdinals[occId];
        if (data.TokenOrdinals.ContainsKey(token.TokenId))
            return data.TokenOrdinals[token.TokenId];
        return 1;
    }

    private static RepeatImpactState ResolveImpact(long timeUs, PerformanceSample perf, int ordinal, RepeatCadenceData data)
    {
        if (perf.ProgressSource != ProgressSource.Token || perf.TimeRange is not { IsValid: true } range)
            return new RepeatImpactState(false, 0, 0, 0, 0, 0, 0, 0);

        long elapsedUs = timeUs - range.StartUs;
        if (elapsedUs < 0)
            return new RepeatImpactState(false, 0, 0, 0, 0, 0, 0, 0);

        long attackUs = Math.Max(1L, data.AttackUs);
        long decayUs = Math.Max(1L, data.DecayUs);
        long totalUs = attackUs + decayUs;

        if (elapsedUs >= totalUs && !perf.IsCurrent)
            return new RepeatImpactState(false, 0, 0, 0, 0, 0, 0, 0);

        int repeatCount = ordinal - 1;
        int level = Math.Clamp(repeatCount, 1, Math.Max(1, data.MaxEscalation));
        double tier01 = data.MaxEscalation <= 1 ? 1.0 : (level - 1.0) / (double)(data.MaxEscalation - 1);
        double elapsedSec = elapsedUs / 1_000_000.0;

        if (elapsedUs < attackUs)
        {
            double a = Curves.EaseOut(elapsedUs / (double)attackUs);
            return new RepeatImpactState(true, level, tier01, elapsedSec, 0.0, a, a, a);
        }

        long postAttack = elapsedUs - attackUs;
        if (postAttack < decayUs)
        {
            double decayT = Math.Clamp(postAttack / (double)decayUs, 0.0, 1.0);
            double env = Math.Exp(-3.1 * decayT) * (1.0 - 0.22 * decayT);
            double bounceFreq = 2.4 + 1.6 * tier01;
            double spring = Math.Cos(decayT * Math.PI * bounceFreq) * Math.Exp(-4.0 * decayT);
            return new RepeatImpactState(true, level, tier01, elapsedSec, decayT, Math.Max(0.0, env), 1.0, spring);
        }

        // 若该重复词素为长音且仍在演唱中，保持轻微余韵脉动
        double sustainPulse = (0.14 + 0.14 * tier01) * (0.75 + 0.25 * Math.Sin(elapsedSec * 14.0));
        return new RepeatImpactState(true, level, tier01, elapsedSec, 1.0, sustainPulse, 1.0, 0.0);
    }

    private static bool ShouldTrackText(string text, bool ignorePunctuation)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!ignorePunctuation)
            return true;

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
                return true;
        }
        return false;
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

    private static Rect2 MapTokenBox(TokenGeometrySample geom)
    {
        var g = geom.BaseGeometry;
        Rect2 b = g.InkBounds is { Width: > 1.0, Height: > 1.0 } ink ? ink : g.AdvanceBounds;
        Point2 p0 = g.MapPoint(new Point2(b.Left, b.Top));
        Point2 p1 = g.MapPoint(new Point2(b.Right, b.Top));
        Point2 p2 = g.MapPoint(new Point2(b.Right, b.Bottom));
        Point2 p3 = g.MapPoint(new Point2(b.Left, b.Bottom));
        double left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        double top = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        double right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        double bottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new Rect2(left, top, Math.Max(left + 1.0, right), Math.Max(top + 1.0, bottom));
    }

    private static Affine2 BuildKineticTransform(Point2 pivot, double sx, double sy, double angleRad, double dx, double dy)
    {
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        double a = cos * sx;
        double b = sin * sx;
        double c = -sin * sy;
        double d = cos * sy;
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
}
