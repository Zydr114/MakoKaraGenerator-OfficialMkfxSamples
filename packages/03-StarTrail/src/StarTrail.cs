using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record StarTrailData(
    VectorHandle PrimaryStar,
    VectorHandle SparkleStar,
    long TrailUs,
    double StarSizePx,
    double RibbonWidthPx,
    double SpinHz,
    ColorRgba PrimaryColor,
    ColorRgba TailColor) : PreparedEffect;

/// <summary>播放头长星轨效果：spine 发射器记录精准播放头轨迹拉出双层流光长彗星光带，dust 发射器播撒旋转闪烁双形星尘与微粒尾迹。</summary>
public sealed class StarTrail : IPureKaraokeEffect<StarTrailData>, IPureVisualEffect<StarTrailData>
{
    private static readonly EmitterId SpineEmitterId = new("spine");
    private static readonly EmitterId DustEmitterId = new("dust");

    public StarTrailData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long end = Math.Max(1L, source.ProjectDurationUs ?? 1L);
        foreach (var line in source.Content.Lines)
            if (line.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        foreach (var token in source.Content.Tokens)
            if (token.TimeRange is { } range) end = Math.Max(end, range.EndUs);

        double rate = p.GetNumber("rate");
        long lifeUs = p.GetDuration("lifeUs");
        long trailUs = p.GetDuration("trailUs");
        double starSizePx = p.GetNumber("starSizePx");
        ColorRgba primaryColor = p.GetColor("primaryColor");

        var bridge = p.GetBoolean("bridgeLines")
            ? new BridgePolicy(TransitionMode.Bridge, p.GetDuration("bridgeUs"), p.GetDuration("bridgeGapLimitUs"), Curves.EaseInOutCurve)
            : BridgePolicy.Jump;

        var fullRange = new TimeRange(0, end);

        var spineParticle = new ParticleSpec
        {
            LifetimeUs = Math.Max(1L, Math.Min(5_000_000L, trailUs + 80_000L)),
            SizePx = 2.0,
            Color = primaryColor,
            SpreadPx = 0,
            TrailDurationUs = 0,
            Velocity = Vector2.Zero,
            Rendering = ParticleRenderMode.Custom,
            Layer = OverlayLayer.AboveText,
            SeedStreamKey = "sample-star-spine",
            Motion = ParticleMotion.ConstantVelocity,
        };
        var spinePlan = new CursorEmissionPlan(
            SpineEmitterId,
            new RateTrack([new RateSegment(fullRange, 60, 60)], [fullRange]),
            spineParticle,
            CursorAnchorMode.AdvanceCenter,
            bridge);

        var dustParticle = new ParticleSpec
        {
            LifetimeUs = lifeUs,
            SizePx = Math.Max(2.0, starSizePx * 0.78),
            Color = primaryColor,
            SpreadPx = p.GetNumber("spreadPx"),
            TrailDurationUs = Math.Min(lifeUs, 650_000L),
            Velocity = new Vector2(-28, -12),
            Rendering = ParticleRenderMode.Custom,
            Layer = OverlayLayer.AboveText,
            SeedStreamKey = "sample-star-dust",
            Motion = new ParticleMotion
            {
                Acceleration = new Vector2(0, 18),
                Opacity = new ParticleAgeCurve(1, 0, Curves.EaseOutCurve),
                Size = new ParticleAgeCurve(1, 0.22, Curves.LinearCurve),
            },
        };
        var dustPlan = new CursorEmissionPlan(
            DustEmitterId,
            new RateTrack([new RateSegment(fullRange, rate, rate)], [fullRange]),
            dustParticle,
            CursorAnchorMode.AdvanceCenter,
            bridge);

        return new StarTrailData(
            source.Resources.GetVector("star-primary"),
            source.Resources.GetVector("star-sparkle"),
            trailUs,
            starSizePx,
            p.GetNumber("ribbonWidthPx"),
            p.GetNumber("spinHz"),
            primaryColor,
            p.GetColor("tailColor"))
        {
            Particles = new ParticleSchedule([spinePlan, dustPlan], []),
            VisualOutputs =
            [
                new VisualOutputDeclaration(new OutputId("ribbons"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
                new VisualOutputDeclaration(new OutputId("stars"), VisualPrimitiveKind.Paths, OverlayLayer.AboveText, VisualResourceKind.SvgVector),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, StarTrailData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, StarTrailData d, VisualOutput output)
    {
        var ribbons = ImmutableArray.CreateBuilder<Trail>();
        var stars = ImmutableArray.CreateBuilder<VectorVisual>();
        long now = frame.TimeUs;
        double seconds = now / 1_000_000d;
        double baseSpin = 2.0 * Math.PI * d.SpinHz * seconds;

        // A. 从 spine 历史出生点按轨道串联跨越字与行的长光带（外圈流光 + 内芯高光）
        if (d.TrailUs > 0)
        {
            var spineBirths = new List<ParticleBirth>();
            foreach (var birth in frame.Particles.Births.Births)
            {
                if (birth.Id.EmitterId != SpineEmitterId)
                    continue;
                if (now - birth.BirthUs > d.TrailUs || birth.BirthUs > now)
                    continue;
                spineBirths.Add(birth);
            }

            if (spineBirths.Count >= 2)
            {
                spineBirths.Sort(CompareBirths);

                ColorRgba coreWhite = new ColorRgba(
                    Math.Min(1.0, d.PrimaryColor.Red * 0.4 + 0.6),
                    Math.Min(1.0, d.PrimaryColor.Green * 0.4 + 0.6),
                    Math.Min(1.0, d.PrimaryColor.Blue * 0.4 + 0.6),
                    1.0);

                var outer = ImmutableArray.CreateBuilder<TrailPoint>();
                var inner = ImmutableArray.CreateBuilder<TrailPoint>();
                string currentKey = TrackKey(spineBirths[0]);

                for (int i = 0; i < spineBirths.Count; i++)
                {
                    var b = spineBirths[i];
                    string key = TrackKey(b);
                    if (!string.Equals(key, currentKey, StringComparison.Ordinal))
                    {
                        if (outer.Count >= 2) ribbons.Add(new Trail(outer.ToImmutable()));
                        if (inner.Count >= 2) ribbons.Add(new Trail(inner.ToImmutable()));
                        outer.Clear();
                        inner.Clear();
                        currentKey = key;
                    }

                    double ageFrac = Math.Clamp((now - b.BirthUs) / (double)d.TrailUs, 0.0, 1.0);
                    double w = 1.0 - ageFrac;
                    double fade = w * w;
                    ColorRgba outerCol = LerpColor(d.TailColor, d.PrimaryColor, w, d.TailColor.Alpha * fade * 0.7);
                    ColorRgba innerCol = LerpColor(d.PrimaryColor, coreWhite, w, d.PrimaryColor.Alpha * w * 0.96);
                    outer.Add(new TrailPoint(b.BirthPosition, d.RibbonWidthPx * 2.1 * w, outerCol));
                    inner.Add(new TrailPoint(b.BirthPosition, d.RibbonWidthPx * 0.85 * w, innerCol));
                }

                if (outer.Count >= 2) ribbons.Add(new Trail(outer.ToImmutable()));
                if (inner.Count >= 2) ribbons.Add(new Trail(inner.ToImmutable()));
            }
        }

        // B. 漂浮星星粒子与个体小拖尾
        foreach (var particle in frame.Particles.Alive)
        {
            if (particle.Id.EmitterId != DustEmitterId)
                continue;
            if (particle.SizePx <= 0.1 || particle.Color.Alpha <= 0.001)
                continue;

            ulong seed = ParticleSeed(particle);
            double kindPick = frame.Random.GetUnitDouble("star-kind", seed);
            double phase = frame.Random.GetUnitDouble("star-phase", seed);
            double speedFactor = 0.65 + 0.85 * frame.Random.GetUnitDouble("star-spin", seed);
            double sizeJitter = 0.65 + 0.65 * frame.Random.GetUnitDouble("star-size", seed);

            long life = Math.Max(1L, particle.DeathUs - particle.BirthUs);
            double age01 = Math.Clamp((now - particle.BirthUs) / (double)life, 0.0, 1.0);

            var trail = frame.Particles.GetTrail(particle.Id);
            if (trail.Length >= 2)
            {
                long trailSpan = Math.Max(1L, trail[^1].TimeUs - trail[0].TimeUs);
                var points = ImmutableArray.CreateBuilder<TrailPoint>(trail.Length);
                for (int i = 0; i < trail.Length; i++)
                {
                    var sample = trail[i];
                    double w = (sample.TimeUs - trail[0].TimeUs) / (double)trailSpan;
                    double tipFade = w * w;
                    ColorRgba col = LerpColor(d.TailColor, d.PrimaryColor, w, particle.Color.Alpha * tipFade * 0.6);
                    double width = d.RibbonWidthPx * 0.6 * (1.0 - age01 * 0.7) * w;
                    points.Add(new TrailPoint(sample.Position, width, col));
                }
                ribbons.Add(new Trail(points.ToImmutable()));
            }

            double twinkle = 0.65 + 0.35 * Math.Sin((now - particle.BirthUs) / 80_000d + phase * 6.2831853);
            double size = particle.SizePx * sizeJitter * twinkle;
            if (size <= 0.5)
                continue;

            ColorRgba starColor = LerpColor(d.PrimaryColor, d.TailColor, age01 * 0.85, particle.Color.Alpha * twinkle);
            VectorHandle handle = kindPick < 0.48 ? d.PrimaryStar : d.SparkleStar;
            double angle = baseSpin * speedFactor + phase * 6.2831853;
            stars.Add(new VectorVisual(handle, Spin(particle.Position, size, angle), starColor));
        }

        // C. 播放头主星（双层反旋星芒）
        var playback = frame.FinalGeometry.Playback;
        if (playback.IsAvailable)
        {
            foreach (var cursor in playback.Cursors)
            {
                Point2 head = cursor.At(CursorAnchorMode.AdvanceCenter).Position;
                double pulse = 1.0 + 0.14 * Math.Sin(seconds * 10.0);
                stars.Add(new VectorVisual(d.PrimaryStar, Spin(head, d.StarSizePx * 1.35 * pulse, baseSpin), d.PrimaryColor));
                stars.Add(new VectorVisual(d.SparkleStar, Spin(head, d.StarSizePx * 0.95 * pulse, -baseSpin * 1.3),
                    new ColorRgba(1, 1, 1, Math.Clamp(d.PrimaryColor.Alpha, 0, 1))));
            }
        }

        output.DrawTrails(new OutputId("ribbons"), ribbons.ToImmutable(), OverlayLayer.AboveText);
        output.DrawPaths(new OutputId("stars"), stars.ToImmutable(), OverlayLayer.AboveText);
    }

    private static string TrackKey(ParticleBirth birth)
        => birth.Source.TrackId is { IsValid: true } tid ? tid.Value : birth.Source.BindingId.Value;

    private static int CompareBirths(ParticleBirth a, ParticleBirth b)
    {
        int trackCmp = StringComparer.Ordinal.Compare(TrackKey(a), TrackKey(b));
        if (trackCmp != 0) return trackCmp;
        int timeCmp = a.BirthUs.CompareTo(b.BirthUs);
        if (timeCmp != 0) return timeCmp;
        ulong sa = a.Id.Origin is ParticleOrigin.Continuous ca ? ca.SequenceStartingAt1 : 0;
        ulong sb = b.Id.Origin is ParticleOrigin.Continuous cb ? cb.SequenceStartingAt1 : 0;
        return sa.CompareTo(sb);
    }

    private static ulong ParticleSeed(ParticleView particle)
    {
        ulong birth = unchecked((ulong)particle.BirthUs);
        if (particle.Id.Origin is ParticleOrigin.Continuous cont)
            return birth ^ (cont.SequenceStartingAt1 * 0x9E3779B97F4A7C15UL);
        return birth;
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

    private static Affine2 Spin(Point2 center, double size, double angle)
    {
        double a = Math.Cos(angle) * size;
        double b = Math.Sin(angle) * size;
        double c = -b;
        double e = a;
        return new Affine2(a, b, c, e, center.X - (a + c) / 2.0, center.Y - (b + e) / 2.0);
    }
}
