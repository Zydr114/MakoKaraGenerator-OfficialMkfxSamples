using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record PetalFallData(
    VectorHandle PetalA,
    VectorHandle PetalB,
    ImmutableDictionary<LineId, TimeRange> LineTimes,
    int AmbientCount,
    long LifeUs,
    double SizePx,
    double FallDistancePx,
    double WindPxPerSec,
    double SwayPx,
    double MarginPx,
    ColorRgba ColorA,
    ColorRgba ColorB,
    OverlayLayer Layer) : PreparedEffect;

/// <summary>花瓣飘落：随播放进度在歌词行周围飘洒立体翻转花瓣，并在唱到每个字时轻盈惊落花瓣粒子。</summary>
public sealed class PetalFall : IPureKaraokeEffect<PetalFallData>, IPureVisualEffect<PetalFallData>
{
    public PetalFallData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long lifeUs = p.GetDuration("lifeUs");
        double sizePx = p.GetNumber("sizePx");
        double fallSpeed = p.GetNumber("fallSpeedPx");
        double wind = p.GetNumber("windPx");
        int burstCount = (int)p.GetInteger("burstCount");
        ColorRgba colorA = p.GetColor("petalColorA");
        OverlayLayer layer = string.Equals(p.GetEnum("layer"), "behind", StringComparison.Ordinal)
            ? OverlayLayer.BehindText
            : OverlayLayer.AboveText;

        var lineTimes = ImmutableDictionary.CreateBuilder<LineId, TimeRange>();
        foreach (var line in source.Content.Lines)
            if (line.TimeRange is { } range) lineTimes[line.Id] = range;

        var selector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
        var rows = ImmutableArray.CreateBuilder<BurstEventRow>();
        if (burstCount > 0)
        {
            var burstSpec = new ParticleSpec
            {
                LifetimeUs = lifeUs,
                SizePx = sizePx,
                Color = colorA,
                SpreadPx = 28,
                Velocity = new Vector2(wind * 0.65, fallSpeed * 0.55),
                Rendering = ParticleRenderMode.Custom,
                Layer = layer,
                SeedStreamKey = "sample-petal-burst",
                Motion = new ParticleMotion
                {
                    Acceleration = new Vector2(wind * 0.15, 22),
                    Opacity = new ParticleAgeCurve(1, 0, Curves.EaseOutCurve),
                    Size = new ParticleAgeCurve(0.85, 1.05, Curves.LinearCurve),
                },
            };

            int count = source.Timeline.Count(selector);
            for (int i = 0; i < count; i++)
            {
                if (source.Timeline.Nth(selector, i) is TokenStarted token)
                {
                    rows.Add(new BurstEventRow(
                        token.EventId,
                        burstCount,
                        new BurstAnchor.SourceToken(token.TokenId),
                        burstSpec));
                }
            }
        }

        var burstPlan = new EventBurstPlan(new EmitterId("petal-burst"), selector, rows.ToImmutable());

        return new PetalFallData(
            source.Resources.GetVector("petal-a"),
            source.Resources.GetVector("petal-b"),
            lineTimes.ToImmutable(),
            (int)p.GetInteger("lineDensity"),
            lifeUs,
            sizePx,
            fallSpeed * (lifeUs / 1_000_000d),
            wind,
            p.GetNumber("swayPx"),
            p.GetNumber("marginPx"),
            colorA,
            p.GetColor("petalColorB"),
            layer)
        {
            Particles = new ParticleSchedule([], [burstPlan]),
            VisualOutputs =
            [
                new VisualOutputDeclaration(new OutputId("petals"), VisualPrimitiveKind.Paths, layer, VisualResourceKind.SvgVector),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, PetalFallData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, PetalFallData d, VisualOutput output)
    {
        var visuals = ImmutableArray.CreateBuilder<VectorVisual>();
        long now = frame.TimeUs;
        double seconds = now / 1_000_000d;

        // 1. 随播放进度在歌词行周围生成的环绕飘落花瓣
        if (d.AmbientCount > 0)
        {
            var lines = frame.FinalGeometry.Lines;
            foreach (var line in frame.FinalGeometry.Presentations.LinesInBinding)
            {
                LineGeometrySample? geom = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Target == line.Target)
                    {
                        geom = lines[i];
                        break;
                    }
                }
                if (geom is null)
                    continue;

                Rect2 worldBox = MapWorldBox(geom.BaseGeometry);
                double width = Math.Max(40.0, worldBox.Width);
                double height = Math.Max(24.0, worldBox.Height);

                double lineProgress = 0.5;
                double envelope;
                if (d.LineTimes.ContainsKey(line.LineId))
                {
                    var range = d.LineTimes[line.LineId];
                    const long fadeInUs = 350_000;
                    const long fadeOutUs = 1_200_000;
                    if (now < range.StartUs - fadeInUs || now > range.EndUs + fadeOutUs)
                    {
                        envelope = line.DisplayPhase == DisplayPhase.Performing ? 0.35 : 0.0;
                    }
                    else if (now < range.StartUs)
                    {
                        envelope = Curves.EaseOut((now - (range.StartUs - fadeInUs)) / (double)fadeInUs);
                    }
                    else if (now <= range.EndUs)
                    {
                        envelope = 1.0;
                    }
                    else
                    {
                        envelope = 1.0 - Curves.EaseOut((now - range.EndUs) / (double)fadeOutUs);
                    }
                    lineProgress = Math.Clamp((now - range.StartUs) / (double)Math.Max(1L, range.DurationUs), 0.0, 1.0);
                }
                else
                {
                    envelope = line.DisplayPhase == DisplayPhase.Performing ? 1.0 : 0.45;
                }

                envelope *= Math.Clamp(line.EnterProgress, 0.0, 1.0);
                if (envelope <= 0.01)
                    continue;

                ulong lineKey = HashText(line.LineId.Value, HashText(line.BindingId.Value, 14695981039346656037UL));
                double cycleSec = Math.Max(0.25, d.LifeUs / 1_000_000d);

                for (int slot = 0; slot < d.AmbientCount; slot++)
                {
                    ulong seed = lineKey ^ ((ulong)(slot + 1) * 0x9E3779B97F4A7C15UL);
                    double rX = frame.Random.GetUnitDouble("petal-x", seed);
                    double rY = frame.Random.GetUnitDouble("petal-y", seed);
                    double rPhase = frame.Random.GetUnitDouble("petal-phase", seed);
                    double rSize = frame.Random.GetUnitDouble("petal-size", seed);
                    double rSpin = frame.Random.GetUnitDouble("petal-spin", seed);
                    double rKind = frame.Random.GetUnitDouble("petal-kind", seed);
                    double rTint = frame.Random.GetUnitDouble("petal-tint", seed);

                    // 随播放进度逐步激活更多花瓣，并轻度聚焦在当前演唱位置周围
                    double activeThreshold = (slot % 4) * 0.14;
                    double progressBoost = Math.Clamp((lineProgress + 0.25 - activeThreshold) / 0.25, 0.25, 1.0);

                    double lifePhase = (seconds / cycleSec + rPhase) % 1.0;
                    if (lifePhase < 0) lifePhase += 1.0;

                    double alphaWave = Math.Sin(lifePhase * Math.PI);
                    double alpha = envelope * progressBoost * alphaWave;
                    if (alpha <= 0.01)
                        continue;

                    double spanX = width + 2.0 * d.MarginPx;
                    double startX = worldBox.Left - d.MarginPx + rX * spanX;
                    double startY = worldBox.Top - d.MarginPx * 0.65 + (rY - 0.5) * height * 0.9;

                    double sway = d.SwayPx * Math.Sin(lifePhase * Math.PI * 3.2 + rPhase * 6.2831853);
                    double x = startX + sway + d.WindPxPerSec * cycleSec * lifePhase * 0.45;
                    double y = startY + d.FallDistancePx * lifePhase;

                    double size = d.SizePx * (0.65 + 0.65 * rSize);
                    double angle = (rSpin - 0.5) * 4.5 * lifePhase + rPhase * 6.2831853;
                    double tilt = 0.30 + 0.70 * Math.Abs(Math.Cos(lifePhase * Math.PI * 2.6 + rSpin * 6.2831853));

                    ColorRgba tint = LerpColor(d.ColorA, d.ColorB, rTint, alpha * d.ColorA.Alpha);
                    VectorHandle handle = rKind < 0.55 ? d.PetalA : d.PetalB;
                    visuals.Add(new VectorVisual(handle, PetalTransform(new Point2(x, y), size, angle, tilt), tint));
                }
            }
        }

        // 2. 唱到具体歌词字时散落的爆发花瓣粒子
        foreach (var particle in frame.Particles.Alive)
        {
            if (particle.SizePx <= 0.1 || particle.Color.Alpha <= 0.005)
                continue;

            ulong seed = unchecked((ulong)particle.BirthUs);
            if (particle.Id.Origin is ParticleOrigin.Burst burst)
                seed ^= ((ulong)(burst.ParticleOrdinal + 1) * 0xBF58476D1CE4E5B9UL);

            double rPhase = frame.Random.GetUnitDouble("burst-phase", seed);
            double rSize = frame.Random.GetUnitDouble("burst-size", seed);
            double rSpin = frame.Random.GetUnitDouble("burst-spin", seed);
            double rKind = frame.Random.GetUnitDouble("burst-kind", seed);
            double rTint = frame.Random.GetUnitDouble("burst-tint", seed);

            double ageSec = (now - particle.BirthUs) / 1_000_000d;
            double swayX = d.SwayPx * 0.75 * Math.Sin(ageSec * 4.2 + rPhase * 6.2831853);
            double swayY = d.SwayPx * 0.18 * Math.Cos(ageSec * 3.1 + rSpin * 6.2831853);
            Point2 pos = new Point2(particle.Position.X + swayX, particle.Position.Y + swayY);

            double size = particle.SizePx * (0.7 + 0.55 * rSize);
            double angle = (rSpin - 0.5) * 3.8 * ageSec + rPhase * 6.2831853;
            double tilt = 0.28 + 0.72 * Math.Abs(Math.Cos(ageSec * 3.5 + rSpin * 6.2831853));

            ColorRgba tint = LerpColor(d.ColorA, d.ColorB, rTint, particle.Color.Alpha);
            VectorHandle handle = rKind < 0.55 ? d.PetalA : d.PetalB;
            visuals.Add(new VectorVisual(handle, PetalTransform(pos, size, angle, tilt), tint));
        }

        output.DrawPaths(new OutputId("petals"), visuals.ToImmutable(), d.Layer);
    }

    private static Rect2 MapWorldBox(GeometrySample geometry)
    {
        var b = geometry.AdvanceBounds;
        Point2 p0 = geometry.MapPoint(new Point2(b.Left, b.Top));
        Point2 p1 = geometry.MapPoint(new Point2(b.Right, b.Top));
        Point2 p2 = geometry.MapPoint(new Point2(b.Right, b.Bottom));
        Point2 p3 = geometry.MapPoint(new Point2(b.Left, b.Bottom));
        double left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        double top = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        double right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        double bottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new Rect2(left, top, Math.Max(left, right), Math.Max(top, bottom));
    }

    private static ulong HashText(string text, ulong seed)
    {
        ulong h = seed;
        for (int i = 0; i < text.Length; i++)
        {
            h ^= text[i];
            h *= 1099511628211UL;
        }
        return h;
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

    private static Affine2 PetalTransform(Point2 center, double size, double angle, double tiltX)
    {
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        double sx = size * tiltX;
        double sy = size;
        // 先绕中心缩放(sx, sy)模拟3D翻转，再旋转angle并平移至center
        double a = cos * sx;
        double b = sin * sx;
        double c = -sin * sy;
        double d = cos * sy;
        return new Affine2(a, b, c, d, center.X - (a + c) / 2.0, center.Y - (b + d) / 2.0);
    }
}
