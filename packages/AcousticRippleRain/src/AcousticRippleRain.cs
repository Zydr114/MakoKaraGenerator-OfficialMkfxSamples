using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct TokenImpactEvent(long TimeUs, TokenId TokenId, LineId LineId, double Energy);

public readonly record struct LeadInImpactEvent(long TimeUs, LineId LineId, int ElapsedMarks, int MarkCount, double Energy);

public sealed record AcousticRippleData(
    ImmutableArray<TokenImpactEvent> TokenImpacts,
    ImmutableArray<LeadInImpactEvent> LeadInImpacts,
    double RainAngleRad,
    long FallUs,
    double RainLengthPx,
    long RippleUs,
    int RippleRings,
    double RippleRadiusPx,
    bool EnableTextWarp,
    double WarpAmplitudePx,
    ColorRgba RainColor,
    ColorRgba RippleAccentColor) : PreparedEffect;

/// <summary>声波雨滴与文字水纹涟漪：在音节起唱瞬间由斜落雨滴精确击中字符顶缘，激起音频调制的椭圆水波纹、抛物线水花微珠与 4x2 GridWarp 文字阻尼微震。</summary>
public sealed class AcousticRippleRain : IPureKaraokeEffect<AcousticRippleData>, IPureVisualEffect<AcousticRippleData>
{
    private const int WarpCols = 4;
    private const int WarpRows = 2;
    private const int RippleSegments = 24;
    private static readonly EmitterId SplashEmitterId = new("splash");
    private static readonly OutputId RainAndRipplesOutputId = new("rain-and-ripples");
    private static readonly OutputId SplashDotsOutputId = new("splash-dots");

    public AcousticRippleData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        double rainAngleRad = p.GetNumber("rainAngleDeg") * (Math.PI / 180.0);
        long fallUs = p.GetDuration("fallUs");
        double rainLengthPx = p.GetNumber("rainLengthPx");
        long rippleUs = p.GetDuration("rippleUs");
        int rippleRings = (int)p.GetInteger("rippleRings");
        double rippleRadiusPx = p.GetNumber("rippleRadiusPx");
        int splashCount = (int)p.GetInteger("splashCount");
        bool enableTextWarp = p.GetBoolean("enableTextWarp");
        double warpAmplitudePx = p.GetNumber("warpAmplitudePx");
        ColorRgba rainColor = p.GetColor("rainColor");
        ColorRgba rippleAccentColor = p.GetColor("rippleAccentColor");
        double fallbackBpm = p.GetNumber("fallbackBpm");

        var tokenSelector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
        int tokenCount = source.Timeline.Count(tokenSelector);
        var tokenImpacts = new List<TokenImpactEvent>(tokenCount);
        var splashRows = ImmutableArray.CreateBuilder<BurstEventRow>(tokenCount);

        long splashLifeUs = Math.Clamp(Math.Min(rippleUs, 680_000L), 150_000L, 3_000_000L);
        var splashSpec = new ParticleSpec
        {
            LifetimeUs = splashLifeUs,
            SizePx = 4.2,
            Color = rippleAccentColor,
            SpreadPx = 6.0,
            TrailDurationUs = 0,
            Velocity = new Vector2(0.0, -70.0),
            Rendering = ParticleRenderMode.Custom,
            Layer = OverlayLayer.AboveText,
            SeedStreamKey = "acoustic-ripple-splash",
            Motion = new ParticleMotion
            {
                Acceleration = new Vector2(0.0, 340.0),
                Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseOutCurve),
                Size = new ParticleAgeCurve(1.0, 0.4, Curves.LinearCurve),
            },
        };

        for (int i = 0; i < tokenCount; i++)
        {
            if (source.Timeline.Nth(tokenSelector, i) is TokenStarted ev)
            {
                double energy = SampleAudioEnergy(source.Audio, ev.TimeUs, fallbackBpm);
                tokenImpacts.Add(new TokenImpactEvent(ev.TimeUs, ev.TokenId, ev.LineId, energy));

                if (splashCount > 0)
                {
                    int rowCount = Math.Clamp((int)Math.Round(splashCount * (0.65 + 0.55 * energy)), 1, 20);
                    splashRows.Add(new BurstEventRow(
                        ev.EventId,
                        rowCount,
                        new BurstAnchor.SourceToken(ev.TokenId),
                        splashSpec));
                }
            }
        }

        var tokenImpactArray = SortTokenImpacts(tokenImpacts);
        var splashPlan = new EventBurstPlan(SplashEmitterId, tokenSelector, splashRows.ToImmutable());

        var leadSelector = new TimelineSelector(TimelineEventKind.LeadInStepChanged, new TimelineScope.Project());
        int leadCount = source.Timeline.Count(leadSelector);
        var leadImpacts = new List<LeadInImpactEvent>(leadCount);
        for (int i = 0; i < leadCount; i++)
        {
            if (source.Timeline.Nth(leadSelector, i) is LeadInStepChanged ev)
            {
                double energy = SampleAudioEnergy(source.Audio, ev.TimeUs, fallbackBpm);
                leadImpacts.Add(new LeadInImpactEvent(
                    ev.TimeUs,
                    ev.Target.Target.LineId,
                    ev.ElapsedMarks,
                    ev.Target.MarkCount,
                    energy));
            }
        }
        var leadImpactArray = SortLeadInImpacts(leadImpacts);

        return new AcousticRippleData(
            tokenImpactArray,
            leadImpactArray,
            rainAngleRad,
            fallUs,
            rainLengthPx,
            rippleUs,
            rippleRings,
            rippleRadiusPx,
            enableTextWarp,
            warpAmplitudePx,
            rainColor,
            rippleAccentColor)
        {
            Particles = new ParticleSchedule([], [splashPlan]),
            VisualOutputs =
            [
                new VisualOutputDeclaration(RainAndRipplesOutputId, VisualPrimitiveKind.Segments, OverlayLayer.AboveText),
                new VisualOutputDeclaration(SplashDotsOutputId, VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, AcousticRippleData data, TextOutput output)
    {
        if (!data.EnableTextWarp || data.WarpAmplitudePx <= 0.05 || data.TokenImpacts.IsDefaultOrEmpty)
            return;

        long now = frame.TimeUs;
        long warpWindowUs = Math.Min(data.RippleUs, 680_000L);
        if (warpWindowUs <= 0)
            return;

        int startIdx = LowerBoundTokenImpact(data.TokenImpacts, now - warpWindowUs);
        int endIdx = LowerBoundTokenImpact(data.TokenImpacts, now + 1L);
        if (startIdx >= endIdx)
            return;

        var geometries = frame.Geometry;
        for (int g = 0; g < geometries.Length; g++)
        {
            var geom = geometries[g];
            if (!geom.BaseGeometry.CanTransformIndependently)
                continue;

            TokenId tokenId = geom.WithRuby.TokenId;
            int hitIdx = -1;
            for (int k = endIdx - 1; k >= startIdx; k--)
            {
                if (data.TokenImpacts[k].TokenId == tokenId)
                {
                    hitIdx = k;
                    break;
                }
            }
            if (hitIdx < 0)
                continue;

            var ev = data.TokenImpacts[hitIdx];
            long elapsedUs = now - ev.TimeUs;
            if (elapsedUs < 0 || elapsedUs >= warpWindowUs)
                continue;

            double tNorm = elapsedUs / (double)warpWindowUs;
            double tSec = elapsedUs / 1_000_000.0;
            double attack = Math.Clamp(elapsedUs / 22_000.0, 0.0, 1.0);
            double env = attack * (1.0 - tNorm) * Math.Exp(-tSec * 4.5);
            if (env <= 0.008)
                continue;

            Rect2 domain = ComputeSafeWarpDomain(geom);
            double cellW = domain.Width / WarpCols;
            double cellH = domain.Height / WarpRows;
            double maxDx = cellW * 0.22;
            double maxDy = cellH * 0.22;
            double amp = data.WarpAmplitudePx * (0.75 + 0.65 * ev.Energy) * env;

            var dest = ImmutableArray.CreateBuilder<Point2>((WarpCols + 1) * (WarpRows + 1));
            for (int r = 0; r <= WarpRows; r++)
            {
                double v = r / (double)WarpRows;
                double sy = domain.Top + v * domain.Height;
                for (int c = 0; c <= WarpCols; c++)
                {
                    double u = c / (double)WarpCols;
                    double sx = domain.Left + u * domain.Width;

                    double dx = u - 0.5;
                    double dy = v - 0.28;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double atten = 0.35 + 0.65 * Math.Exp(-dist * 1.45);
                    double waveY = Math.Sin(dist * 13.5 - tSec * 26.0);
                    double waveX = Math.Cos(dist * 11.5 - tSec * 24.0);

                    double offX = Math.Clamp(amp * 0.42 * (dx / Math.Max(0.16, dist)) * waveX * atten, -maxDx, maxDx);
                    double offY = Math.Clamp(amp * waveY * atten, -maxDy, maxDy);
                    dest.Add(new Point2(sx + offX, sy + offY));
                }
            }

            output.SetWarp(geom.WithRuby, new GridWarp(domain, WarpCols, WarpRows, dest.MoveToImmutable()));
        }
    }

    public void EvaluateVisuals(in VisualFrame frame, AcousticRippleData d, VisualOutput output)
    {
        var segments = ImmutableArray.CreateBuilder<Segment>(128);
        var dots = ImmutableArray.CreateBuilder<Dot>(64);

        long now = frame.TimeUs;
        var tokenGeoms = frame.FinalGeometry.Tokens;
        var lineGeoms = frame.FinalGeometry.Lines;

        Vector2 rainDir = new Vector2(Math.Sin(d.RainAngleRad), Math.Cos(d.RainAngleRad));

        // 1. 音节起唱雨滴下落与水波椭圆涟漪
        if (!d.TokenImpacts.IsDefaultOrEmpty && tokenGeoms.Length > 0)
        {
            int startIdx = LowerBoundTokenImpact(d.TokenImpacts, now - d.RippleUs);
            int endIdx = LowerBoundTokenImpact(d.TokenImpacts, now + d.FallUs + 1L);

            for (int i = startIdx; i < endIdx; i++)
            {
                var ev = d.TokenImpacts[i];
                for (int g = 0; g < tokenGeoms.Length; g++)
                {
                    var tok = tokenGeoms[g];
                    if (tok.WithRuby.TokenId != ev.TokenId)
                        continue;

                    Point2 impactWorld = ComputeTokenImpactPoint(tok);
                    EmitRainOrRipple(segments, impactWorld, now, ev.TimeUs, ev.Energy, rainDir, d);
                }
            }
        }

        // 2. 前奏 LeadIn 节拍雨滴与涟漪
        if (!d.LeadInImpacts.IsDefaultOrEmpty && lineGeoms.Length > 0)
        {
            int leadStart = LowerBoundLeadImpact(d.LeadInImpacts, now - d.RippleUs);
            int leadEnd = LowerBoundLeadImpact(d.LeadInImpacts, now + d.FallUs + 1L);

            for (int i = leadStart; i < leadEnd; i++)
            {
                var ev = d.LeadInImpacts[i];
                for (int l = 0; l < lineGeoms.Length; l++)
                {
                    var line = lineGeoms[l];
                    if (line.Target.LineId != ev.LineId)
                        continue;

                    Rect2 adv = line.BaseGeometry.AdvanceBounds;
                    double frac = ev.MarkCount > 0
                        ? Math.Clamp((ev.ElapsedMarks - 0.5) / ev.MarkCount, 0.08, 0.92)
                        : 0.5;
                    double topY = line.BaseGeometry.InkBounds is { Height: > 1.0 } ink ? ink.Top : adv.Top;
                    Point2 impactWorld = line.BaseGeometry.MapPoint(new Point2(adv.Left + adv.Width * frac, topY));
                    EmitRainOrRipple(segments, impactWorld, now, ev.TimeUs, ev.Energy, rainDir, d);
                }
            }
        }

        // 3. 击中瞬间飞溅抛物线水花微珠
        var alive = frame.Particles.Alive;
        for (int i = 0; i < alive.Length; i++)
        {
            var particle = alive[i];
            if (particle.Id.EmitterId != SplashEmitterId)
                continue;

            long ageUs = now - particle.BirthUs;
            if (ageUs < 0)
                continue;

            long life = Math.Max(1L, particle.DeathUs - particle.BirthUs);
            double age01 = Math.Clamp(ageUs / (double)life, 0.0, 1.0);
            double ageSec = ageUs / 1_000_000.0;

            Vector2 topOffset = new Vector2(0.0, -40.0);
            if (particle.Source is ParticleSource.Token tokSrc)
            {
                var tokGeom = FindTokenGeometry(tokenGeoms, tokSrc.Target);
                if (tokGeom is not null)
                {
                    Point2 advCenter = tokGeom.MapPoint(tokGeom.BaseGeometry.AdvanceBounds.Center);
                    Point2 topPt = ComputeTokenImpactPoint(tokGeom);
                    topOffset = new Vector2(topPt.X - advCenter.X, topPt.Y - advCenter.Y);
                }
            }

            ulong seed = ParticleSeed(particle);
            double rAngle = frame.Random.GetUnitDouble("splash-ang", seed);
            double rSpeed = frame.Random.GetUnitDouble("splash-spd", seed);
            double rSize = frame.Random.GetUnitDouble("splash-sz", seed);

            // 扇形向斜上方飞溅并受重力回落
            double launchAngle = -Math.PI * 0.5 + (rAngle - 0.5) * 2.15 + d.RainAngleRad * 0.25;
            double speed = 85.0 + 110.0 * rSpeed;
            double vx = Math.Cos(launchAngle) * speed;
            double vy = Math.Sin(launchAngle) * speed;
            const double gravity = 390.0;

            Point2 origin = new Point2(
                particle.Birth.BirthPosition.X + topOffset.X,
                particle.Birth.BirthPosition.Y + topOffset.Y);
            Point2 pos = new Point2(
                origin.X + vx * ageSec,
                origin.Y + vy * ageSec + 0.5 * gravity * ageSec * ageSec);

            double alpha = Math.Clamp((1.0 - Curves.EaseOut(age01)) * d.RippleAccentColor.Alpha, 0.0, 1.0);
            if (alpha <= 0.01)
                continue;

            double radius = Math.Max(1.2, (1.8 + 2.6 * rSize) * (1.0 - 0.40 * age01));
            ColorRgba dropColor = LerpColor(d.RippleAccentColor, d.RainColor, 0.35 + age01 * 0.55, alpha);
            dots.Add(new Dot(pos, radius, dropColor));
        }

        output.DrawSegments(RainAndRipplesOutputId, segments.ToImmutable(), OverlayLayer.AboveText);
        output.DrawDots(SplashDotsOutputId, dots.ToImmutable(), OverlayLayer.AboveText);
    }

    private static void EmitRainOrRipple(
        ImmutableArray<Segment>.Builder segments,
        Point2 impactWorld,
        long now,
        long eventTimeUs,
        double energy,
        Vector2 rainDir,
        AcousticRippleData d)
    {
        if (now < eventTimeUs)
        {
            long remainUs = eventTimeUs - now;
            if (remainUs > d.FallUs || d.FallUs <= 0)
                return;

            double u = Math.Clamp(remainUs / (double)d.FallUs, 0.0, 1.0);
            double fadeIn = Curves.EaseOut(Math.Clamp((1.0 - u) / 0.28, 0.0, 1.0));
            double fallDistance = 220.0 + 75.0 * energy;

            Point2 tip = new Point2(
                impactWorld.X - rainDir.X * fallDistance * u,
                impactWorld.Y - rainDir.Y * fallDistance * u);
            double len = d.RainLengthPx * (0.78 + 0.45 * energy) * (0.72 + 0.28 * u);
            Point2 tail = new Point2(tip.X - rainDir.X * len, tip.Y - rainDir.Y * len);
            Point2 mid = new Point2(tip.X - rainDir.X * (len * 0.48), tip.Y - rainDir.Y * (len * 0.48));

            double outerAlpha = Math.Clamp(0.42 * fadeIn * d.RainColor.Alpha, 0.0, 1.0);
            double innerAlpha = Math.Clamp(0.92 * fadeIn * d.RippleAccentColor.Alpha, 0.0, 1.0);
            double outerW = 2.4 + 1.1 * energy;
            double innerW = 1.2 + 0.55 * energy;

            segments.Add(new Segment(tail, tip, outerW, new ColorRgba(d.RainColor.Red, d.RainColor.Green, d.RainColor.Blue, outerAlpha)));
            segments.Add(new Segment(mid, tip, innerW, new ColorRgba(d.RippleAccentColor.Red, d.RippleAccentColor.Green, d.RippleAccentColor.Blue, innerAlpha)));
            return;
        }

        long elapsedUs = now - eventTimeUs;
        if (elapsedUs < 0 || elapsedUs >= d.RippleUs || d.RippleUs <= 0)
            return;

        double t = Math.Clamp(elapsedUs / (double)d.RippleUs, 0.0, 1.0);
        double maxR = d.RippleRadiusPx * (0.76 + 0.68 * energy);
        Point2 center = new Point2(impactWorld.X, impactWorld.Y + 2.0);

        for (int ring = 0; ring < d.RippleRings; ring++)
        {
            double delay = ring * 0.19;
            double rt = (t - delay) / (1.0 - delay);
            if (rt <= 0.0 || rt >= 1.0)
                continue;

            double eased = Curves.EaseOut(rt);
            double rx = Math.Max(4.0, maxR * eased * (1.0 - ring * 0.12));
            double ry = rx * 0.35;
            double width = Math.Max(1.0, (3.1 + 1.8 * energy) * (1.0 - 0.58 * rt) * (1.0 - ring * 0.14));
            double alpha = Math.Clamp((1.0 - rt) * (1.0 - rt) * (0.96 - ring * 0.14) * d.RainColor.Alpha, 0.0, 1.0);
            if (alpha <= 0.01)
                continue;

            ColorRgba ringColor = LerpColor(d.RippleAccentColor, d.RainColor, Math.Clamp(0.28 + rt * 0.65 + ring * 0.12, 0.0, 1.0), alpha);

            Point2 prev = new Point2(center.X + rx, center.Y);
            for (int s = 1; s <= RippleSegments; s++)
            {
                double ang = (s / (double)RippleSegments) * (Math.PI * 2.0);
                Point2 next = new Point2(center.X + rx * Math.Cos(ang), center.Y + ry * Math.Sin(ang));
                segments.Add(new Segment(prev, next, width, ringColor));
                prev = next;
            }
        }
    }

    private static Point2 ComputeTokenImpactPoint(TokenGeometrySample tok)
    {
        var g = tok.BaseGeometry;
        Rect2 adv = g.AdvanceBounds;
        double topY = g.InkBounds is { Height: > 1.0 } ink ? ink.Top : adv.Top;
        return tok.MapPoint(new Point2(adv.Center.X, topY));
    }

    private static Rect2 ComputeSafeWarpDomain(TokenGeometrySample geom)
    {
        Rect2 box = UnionRect(geom.BaseGeometry.AdvanceBounds, geom.FamilyGeometry.AdvanceBounds);
        if (geom.BaseGeometry.InkBounds is { } baseInk)
            box = UnionRect(box, baseInk);
        if (geom.FamilyGeometry.InkBounds is { } famInk)
            box = UnionRect(box, famInk);
        box = IncludePoint(box, geom.BaseGeometry.Baseline);
        box = IncludePoint(box, geom.FamilyGeometry.Baseline);

        var rubies = geom.Ruby;
        for (int i = 0; i < rubies.Length; i++)
        {
            var rg = rubies[i].Geometry;
            box = UnionRect(box, rg.AdvanceBounds);
            if (rg.InkBounds is { } rInk)
                box = UnionRect(box, rInk);
        }

        double pad = Math.Max(48.0, Math.Max(box.Width, box.Height) * 0.55);
        return new Rect2(box.Left - pad, box.Top - pad, box.Right + pad, box.Bottom + pad);
    }

    private static Rect2 UnionRect(Rect2 a, Rect2 b)
        => new Rect2(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));

    private static Rect2 IncludePoint(Rect2 r, Point2 p)
        => new Rect2(
            Math.Min(r.Left, p.X),
            Math.Min(r.Top, p.Y),
            Math.Max(r.Right, p.X),
            Math.Max(r.Bottom, p.Y));

    private static TokenGeometrySample? FindTokenGeometry(ImmutableArray<TokenGeometrySample> tokens, TokenFamilyTarget target)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].WithRuby == target)
                return tokens[i];
        }
        return null;
    }

    private static double SampleAudioEnergy(AudioFeatures audio, long timeUs, double fallbackBpm)
    {
        long clamped = Math.Max(0L, timeUs);
        var sample = audio.GetAt(clamped);
        if (sample.Availability == AudioAvailability.Available && sample.Values is { } values)
            return Math.Clamp(0.55 * values.Pulse + 0.45 * values.BassLevel, 0.0, 1.0);

        if (fallbackBpm > 0.0)
        {
            double beats = (clamped / 1_000_000.0) * (fallbackBpm / 60.0);
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.2);
            return Math.Clamp(0.22 + 0.76 * impulse, 0.0, 1.0);
        }

        return 0.45;
    }

    private static ImmutableArray<TokenImpactEvent> SortTokenImpacts(List<TokenImpactEvent> items)
    {
        items.Sort(CompareTokenImpact);
        return ImmutableArray.CreateRange(items);
    }

    private static int CompareTokenImpact(TokenImpactEvent a, TokenImpactEvent b)
        => a.TimeUs.CompareTo(b.TimeUs);

    private static ImmutableArray<LeadInImpactEvent> SortLeadInImpacts(List<LeadInImpactEvent> items)
    {
        items.Sort(CompareLeadImpact);
        return ImmutableArray.CreateRange(items);
    }

    private static int CompareLeadImpact(LeadInImpactEvent a, LeadInImpactEvent b)
        => a.TimeUs.CompareTo(b.TimeUs);

    private static int LowerBoundTokenImpact(ImmutableArray<TokenImpactEvent> items, long targetTimeUs)
    {
        int lo = 0;
        int hi = items.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (items[mid].TimeUs < targetTimeUs)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int LowerBoundLeadImpact(ImmutableArray<LeadInImpactEvent> items, long targetTimeUs)
    {
        int lo = 0;
        int hi = items.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (items[mid].TimeUs < targetTimeUs)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static ulong ParticleSeed(ParticleView particle)
    {
        ulong seed = unchecked((ulong)particle.BirthUs);
        if (particle.Id.Origin is ParticleOrigin.Burst burst)
            seed ^= ((ulong)(burst.ParticleOrdinal + 1) * 0xBF58476D1CE4E5B9UL) ^ ((ulong)burst.PlanOrdinal * 0x94D049BB133111EBUL);
        return seed;
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
