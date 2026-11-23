using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record ChromaFireflyData(
    SpriteHandle BokehSmall,
    SpriteHandle BokehMedium,
    SpriteHandle BokehLarge,
    MediaQueryId AmbientQuery,
    Vector2 CanvasSize,
    long TrailUs,
    double FireflySizePx,
    double BokehSizePx,
    double DriftPx,
    double SceneColorMix,
    ColorRgba AccentTint,
    ColorRgba SecondaryTint) : PreparedEffect;

/// <summary>环境取色萤火与焦外散景：实时采样活跃行所在画布三分区背景色相、亮度与杂乱度，驱动 BehindText 焦外散景光斑与 AboveText 萤火及柔光拖尾。</summary>
public sealed class ChromaFireflyBokeh : IPureKaraokeEffect<ChromaFireflyData>, IPureVisualEffect<ChromaFireflyData>
{
    private readonly record struct ZoneAtmosphere(
        bool Available,
        ColorRgba Tint,
        double AlphaScale,
        double SizeScale,
        double CoreHighlight);

    private readonly record struct ThreeZoneAtmosphere(
        ZoneAtmosphere Left,
        ZoneAtmosphere Center,
        ZoneAtmosphere Right);

    private static readonly EmitterId FirefliesEmitterId = new("fireflies");
    private static readonly EmitterId BokehLineBurstId = new("bokeh-line-burst");
    private static readonly EmitterId BokehTokenBurstId = new("bokeh-token-burst");
    private static readonly OutputId BokehBehindOutputId = new("bokeh-behind");
    private static readonly OutputId FireflyTrailsOutputId = new("firefly-trails");
    private static readonly OutputId FireflyAboveOutputId = new("firefly-above");

    public ChromaFireflyData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long endUs = Math.Max(1L, source.ProjectDurationUs ?? 1L);
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { } range)
                endUs = Math.Max(endUs, range.EndUs);
        }
        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].TimeRange is { } range)
                endUs = Math.Max(endUs, range.EndUs);
        }

        double fireflyRate = p.GetNumber("fireflyRate");
        int bokehBurstCount = (int)p.GetInteger("bokehBurstCount");
        long lifeUs = p.GetDuration("lifeUs");
        long trailUs = p.GetDuration("trailUs");
        double fireflySizePx = p.GetNumber("fireflySizePx");
        double bokehSizePx = p.GetNumber("bokehSizePx");
        double driftPx = p.GetNumber("driftPx");
        double sceneColorMix = p.GetNumber("sceneColorMix");
        ColorRgba accentTint = p.GetColor("accentTint");
        ColorRgba secondaryTint = p.GetColor("secondaryTint");
        bool bridgeLines = p.GetBoolean("bridgeLines");

        var fullRange = new TimeRange(0, endUs);
        var bridge = bridgeLines
            ? new BridgePolicy(TransitionMode.Bridge, 320_000L, 2_500_000L, Curves.EaseInOutCurve)
            : BridgePolicy.Jump;

        var fireflySpec = new ParticleSpec
        {
            LifetimeUs = lifeUs,
            SizePx = fireflySizePx,
            Color = accentTint,
            SpreadPx = 18.0,
            TrailDurationUs = Math.Min(lifeUs, trailUs),
            Velocity = new Vector2(0.0, -22.0),
            Rendering = ParticleRenderMode.Custom,
            Layer = OverlayLayer.AboveText,
            SeedStreamKey = "chroma-firefly-cursor",
            Motion = new ParticleMotion
            {
                Acceleration = new Vector2(0.0, -6.0),
                Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseOutCurve),
                Size = new ParticleAgeCurve(0.95, 0.55, Curves.LinearCurve),
            },
        };

        var fireflyPlan = new CursorEmissionPlan(
            FirefliesEmitterId,
            new RateTrack([new RateSegment(fullRange, fireflyRate, fireflyRate)], [fullRange]),
            fireflySpec,
            CursorAnchorMode.AdvanceCenter,
            bridge);

        long bokehLifeUs = Math.Min(5_000_000L, (long)(lifeUs * 1.25));

        var lineSelector = new TimelineSelector(TimelineEventKind.BindingEntered, new TimelineScope.Project());
        var lineRows = ImmutableArray.CreateBuilder<BurstEventRow>();
        if (bokehBurstCount > 0)
        {
            var lineBokehSpec = new ParticleSpec
            {
                LifetimeUs = bokehLifeUs,
                SizePx = bokehSizePx * 1.25,
                Color = accentTint,
                SpreadPx = 140.0,
                TrailDurationUs = 0,
                Velocity = new Vector2(0.0, -16.0),
                Rendering = ParticleRenderMode.Custom,
                Layer = OverlayLayer.BehindText,
                SeedStreamKey = "chroma-bokeh-line",
                Motion = new ParticleMotion
                {
                    Acceleration = new Vector2(0.0, -4.0),
                    Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseInOutCurve),
                    Size = new ParticleAgeCurve(0.85, 1.15, Curves.LinearCurve),
                },
            };

            int lineEventCount = source.Timeline.Count(lineSelector);
            int lineBurstAmount = Math.Clamp(bokehBurstCount + 2, 2, 10);
            for (int i = 0; i < lineEventCount; i++)
            {
                if (source.Timeline.Nth(lineSelector, i) is BindingEntered ev)
                {
                    lineRows.Add(new BurstEventRow(
                        ev.EventId,
                        lineBurstAmount,
                        new BurstAnchor.SourceLine(ev.Binding.LineId),
                        lineBokehSpec));
                }
            }
        }
        var lineBurstPlan = new EventBurstPlan(BokehLineBurstId, lineSelector, lineRows.ToImmutable());

        var tokenSelector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
        var tokenRows = ImmutableArray.CreateBuilder<BurstEventRow>();
        if (bokehBurstCount > 0)
        {
            var tokenBokehSpec = new ParticleSpec
            {
                LifetimeUs = bokehLifeUs,
                SizePx = bokehSizePx,
                Color = secondaryTint,
                SpreadPx = 42.0,
                TrailDurationUs = 0,
                Velocity = new Vector2(0.0, -24.0),
                Rendering = ParticleRenderMode.Custom,
                Layer = OverlayLayer.BehindText,
                SeedStreamKey = "chroma-bokeh-token",
                Motion = new ParticleMotion
                {
                    Acceleration = new Vector2(0.0, -5.0),
                    Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseOutCurve),
                    Size = new ParticleAgeCurve(0.78, 1.18, Curves.LinearCurve),
                },
            };

            int tokenEventCount = source.Timeline.Count(tokenSelector);
            for (int i = 0; i < tokenEventCount; i++)
            {
                if (source.Timeline.Nth(tokenSelector, i) is TokenStarted ev)
                {
                    tokenRows.Add(new BurstEventRow(
                        ev.EventId,
                        bokehBurstCount,
                        new BurstAnchor.SourceToken(ev.TokenId),
                        tokenBokehSpec));
                }
            }
        }
        var tokenBurstPlan = new EventBurstPlan(BokehTokenBurstId, tokenSelector, tokenRows.ToImmutable());

        return new ChromaFireflyData(
            source.Resources.GetSprite("bokeh-orb", "small"),
            source.Resources.GetSprite("bokeh-orb", "medium"),
            source.Resources.GetSprite("bokeh-orb", "large"),
            source.Media.GetId("ambient"),
            source.CanvasSize,
            trailUs,
            fireflySizePx,
            bokehSizePx,
            driftPx,
            sceneColorMix,
            accentTint,
            secondaryTint)
        {
            Particles = new ParticleSchedule([fireflyPlan], [lineBurstPlan, tokenBurstPlan]),
            VisualOutputs =
            [
                new VisualOutputDeclaration(BokehBehindOutputId, VisualPrimitiveKind.Sprites, OverlayLayer.BehindText, VisualResourceKind.SvgAtlas),
                new VisualOutputDeclaration(FireflyTrailsOutputId, VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
                new VisualOutputDeclaration(FireflyAboveOutputId, VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, ChromaFireflyData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, ChromaFireflyData d, VisualOutput output)
    {
        var alive = frame.Particles.Alive;
        if (alive.Length == 0)
        {
            output.DrawAtlasSprites(BokehBehindOutputId, ImmutableArray<AtlasSprite>.Empty, OverlayLayer.BehindText);
            output.DrawTrails(FireflyTrailsOutputId, ImmutableArray<Trail>.Empty, OverlayLayer.AboveText);
            output.DrawDots(FireflyAboveOutputId, ImmutableArray<Dot>.Empty, OverlayLayer.AboveText);
            return;
        }

        ThreeZoneAtmosphere zones = SampleThreeZones(frame, d);

        var bokehSprites = ImmutableArray.CreateBuilder<AtlasSprite>(alive.Length);
        var trails = ImmutableArray.CreateBuilder<Trail>(alive.Length);
        var dots = ImmutableArray.CreateBuilder<Dot>(alive.Length * 2);

        long now = frame.TimeUs;
        double canvasW = Math.Max(1.0, d.CanvasSize.X);

        for (int i = 0; i < alive.Length; i++)
        {
            var particle = alive[i];
            long ageUs = now - particle.BirthUs;
            if (ageUs < 0)
                continue;

            long life = Math.Max(1L, particle.DeathUs - particle.BirthUs);
            double age01 = Math.Clamp(ageUs / (double)life, 0.0, 1.0);
            double ageSec = ageUs / 1_000_000.0;

            ulong seed = ParticleSeed(particle);
            double rPhase = frame.Random.GetUnitDouble("cf-phase", seed);
            double rFreq = frame.Random.GetUnitDouble("cf-freq", seed);
            double rSize = frame.Random.GetUnitDouble("cf-size", seed);
            double rTint = frame.Random.GetUnitDouble("cf-tint", seed);
            double rBlink = frame.Random.GetUnitDouble("cf-blink", seed);
            double rAngle = frame.Random.GetUnitDouble("cf-angle", seed);

            bool isFirefly = particle.Id.EmitterId == FirefliesEmitterId;
            double driftAmp = isFirefly ? d.DriftPx : d.DriftPx * 0.55;
            Vector2 disp = BrownianDrift(ageSec, age01, driftAmp, rFreq, rPhase, rAngle);
            Point2 worldPos = new Point2(particle.Position.X + disp.X, particle.Position.Y + disp.Y);

            ZoneAtmosphere zone = InterpolateZone(zones, Math.Clamp(worldPos.X / canvasW, 0.0, 1.0));
            ColorRgba baseTint = zone.Available
                ? LerpColor(zone.Tint, d.SecondaryTint, rTint * 0.25, 1.0)
                : LerpColor(d.AccentTint, d.SecondaryTint, rTint, 1.0);

            if (isFirefly)
            {
                double fadeIn = Math.Clamp(ageUs / 90_000.0, 0.0, 1.0);
                double fadeOut = 1.0 - Curves.EaseOut(Math.Clamp((age01 - 0.35) / 0.65, 0.0, 1.0));
                double blink = 0.48 + 0.52 * (0.5 + 0.5 * Math.Sin(ageSec * (3.2 + 2.8 * rBlink) + rPhase * 6.2831853));
                double alpha = Math.Clamp(fadeIn * fadeOut * blink * zone.AlphaScale * d.AccentTint.Alpha, 0.0, 1.0);
                if (alpha <= 0.01)
                    continue;

                if (d.TrailUs > 0)
                {
                    var trailSamples = frame.Particles.GetTrail(particle.Id);
                    if (trailSamples.Length >= 2)
                    {
                        long trailSpan = Math.Max(1L, trailSamples[^1].TimeUs - trailSamples[0].TimeUs);
                        var points = ImmutableArray.CreateBuilder<TrailPoint>(trailSamples.Length);
                        double headWidth = Math.Max(1.4, d.FireflySizePx * 0.48 * (0.65 + 0.5 * rSize));
                        for (int t = 0; t < trailSamples.Length; t++)
                        {
                            var sample = trailSamples[t];
                            long sAgeUs = Math.Max(0L, sample.TimeUs - particle.BirthUs);
                            double sAge01 = Math.Clamp(sAgeUs / (double)life, 0.0, 1.0);
                            double sAgeSec = sAgeUs / 1_000_000.0;
                            Vector2 sDisp = BrownianDrift(sAgeSec, sAge01, driftAmp, rFreq, rPhase, rAngle);
                            Point2 sPos = new Point2(sample.Position.X + sDisp.X, sample.Position.Y + sDisp.Y);

                            double wFrac = (sample.TimeUs - trailSamples[0].TimeUs) / (double)trailSpan;
                            ColorRgba tCol = new ColorRgba(baseTint.Red, baseTint.Green, baseTint.Blue, Math.Clamp(alpha * wFrac * wFrac * 0.72, 0.0, 1.0));
                            points.Add(new TrailPoint(sPos, headWidth * wFrac, tCol));
                        }
                        trails.Add(new Trail(points.ToImmutable()));
                    }
                }

                double radius = Math.Max(1.6, d.FireflySizePx * 0.5 * (0.68 + 0.72 * rSize) * (0.58 + 0.42 * blink));
                ColorRgba haloCol = new ColorRgba(baseTint.Red, baseTint.Green, baseTint.Blue, Math.Clamp(alpha * 0.42, 0.0, 1.0));
                dots.Add(new Dot(worldPos, radius * 1.95, haloCol));

                double coreWhiteMix = Math.Clamp(0.45 + 0.35 * zone.CoreHighlight, 0.0, 0.85);
                ColorRgba coreCol = LerpColor(baseTint, new ColorRgba(1.0, 1.0, 1.0, 1.0), coreWhiteMix, Math.Clamp(alpha * 0.98, 0.0, 1.0));
                dots.Add(new Dot(worldPos, Math.Max(1.2, radius * 0.65), coreCol));
            }
            else
            {
                double breathe = Math.Sin(age01 * Math.PI);
                double pulse = 0.84 + 0.16 * Math.Sin(ageSec * (2.0 + 1.5 * rBlink) + rPhase * 6.2831853);
                double baseAlpha = particle.Id.EmitterId == BokehLineBurstId ? 0.48 : 0.58;
                double alpha = Math.Clamp(baseAlpha * breathe * pulse * zone.AlphaScale * d.AccentTint.Alpha, 0.0, 1.0);
                if (alpha <= 0.01)
                    continue;

                double size = Math.Max(8.0, particle.SizePx * (0.65 + 0.75 * rSize) * zone.SizeScale * (0.85 + 0.25 * breathe));
                ColorRgba orbTint = new ColorRgba(baseTint.Red, baseTint.Green, baseTint.Blue, alpha);
                SpriteHandle orbHandle = PickBokehVariant(size, d.BokehSmall, d.BokehMedium, d.BokehLarge);
                bokehSprites.Add(new AtlasSprite(orbHandle, BuildSquareTransform(worldPos, size, rAngle * 6.2831853), orbTint));
            }
        }

        output.DrawAtlasSprites(BokehBehindOutputId, bokehSprites.ToImmutable(), OverlayLayer.BehindText);
        output.DrawTrails(FireflyTrailsOutputId, trails.ToImmutable(), OverlayLayer.AboveText);
        output.DrawDots(FireflyAboveOutputId, dots.ToImmutable(), OverlayLayer.AboveText);
    }

    private static ThreeZoneAtmosphere SampleThreeZones(in VisualFrame frame, ChromaFireflyData d)
    {
        var fallback = new ZoneAtmosphere(false, d.AccentTint, 1.0, 1.0, 0.5);
        if (frame.OutputMode != OutputMode.Opaque ||
            frame.Media.Catalog.Input?.State != MediaInputState.Available)
        {
            return new ThreeZoneAtmosphere(fallback, fallback, fallback);
        }

        double w = Math.Max(3.0, d.CanvasSize.X);
        double h = Math.Max(3.0, d.CanvasSize.Y);

        double bandTop = h;
        double bandBottom = 0.0;
        bool hasLine = false;
        var lines = frame.FinalGeometry.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            var box = MapWorldBox(lines[i].BaseGeometry);
            if (!hasLine)
            {
                bandTop = box.Top;
                bandBottom = box.Bottom;
                hasLine = true;
            }
            else
            {
                bandTop = Math.Min(bandTop, box.Top);
                bandBottom = Math.Max(bandBottom, box.Bottom);
            }
        }

        if (!hasLine)
        {
            bandTop = h * 0.45;
            bandBottom = h * 0.90;
        }
        else
        {
            bandTop = Math.Clamp(bandTop - 48.0, 0.0, h - 16.0);
            bandBottom = Math.Clamp(bandBottom + 48.0, bandTop + 16.0, h);
        }

        double third = w / 3.0;
        ZoneAtmosphere z0 = SampleOneZone(frame.Media, d, new Rect2(0.0, bandTop, third, bandBottom), frame.TimeUs);
        ZoneAtmosphere z1 = SampleOneZone(frame.Media, d, new Rect2(third, bandTop, third * 2.0, bandBottom), frame.TimeUs);
        ZoneAtmosphere z2 = SampleOneZone(frame.Media, d, new Rect2(third * 2.0, bandTop, w, bandBottom), frame.TimeUs);
        return new ThreeZoneAtmosphere(z0, z1, z2);
    }

    private static ZoneAtmosphere SampleOneZone(MediaFeatures media, ChromaFireflyData d, Rect2 rect, long timeUs)
    {
        var sample = media.Sample(d.AmbientQuery, rect, timeUs);
        if (sample.Input.State != MediaInputState.Available || sample.CanvasValues is not { } cv)
            return new ZoneAtmosphere(false, d.AccentTint, 1.0, 1.0, 0.5);

        double a = Math.Max(1e-4, cv.Mean.Alpha);
        double sr = LinearToSrgb(cv.Mean.R / a);
        double sg = LinearToSrgb(cv.Mean.G / a);
        double sb = LinearToSrgb(cv.Mean.B / a);

        ColorRgba purified = PurifySceneColor(sr, sg, sb, cv.MeanPremulY);
        ColorRgba mixed = LerpColor(d.AccentTint, purified, d.SceneColorMix, 1.0);

        double luma = Math.Clamp(cv.MeanPremulY, 0.0, 1.0);
        double variance = Math.Clamp(cv.PremulYVariance, 0.0, 0.25);

        // 暗画面保持柔润不过曝，亮画面适当提升显色与不透明度
        double alphaScale = luma < 0.25
            ? 0.78 + (luma / 0.25) * 0.14
            : 0.92 + Math.Clamp((luma - 0.25) / 0.65, 0.0, 1.0) * 0.24;

        // 背景杂乱度越高，适度缩小大散景尺寸以保障歌词可读性
        double clutterT = Math.Clamp(variance / 0.055, 0.0, 1.0);
        double sizeScale = 1.14 - 0.44 * clutterT;
        double coreHighlight = Math.Clamp(1.0 - luma * 0.55, 0.25, 0.95);

        return new ZoneAtmosphere(true, mixed, alphaScale, sizeScale, coreHighlight);
    }

    private static ZoneAtmosphere InterpolateZone(ThreeZoneAtmosphere zones, double u)
    {
        if (u <= 0.5)
        {
            double t = u * 2.0;
            return BlendAtmosphere(zones.Left, zones.Center, t);
        }
        else
        {
            double t = (u - 0.5) * 2.0;
            return BlendAtmosphere(zones.Center, zones.Right, t);
        }
    }

    private static ZoneAtmosphere BlendAtmosphere(ZoneAtmosphere a, ZoneAtmosphere b, double t)
    {
        if (!a.Available && !b.Available)
            return a;
        double k = Math.Clamp(t, 0.0, 1.0);
        return new ZoneAtmosphere(
            true,
            LerpColor(a.Tint, b.Tint, k, 1.0),
            a.AlphaScale + (b.AlphaScale - a.AlphaScale) * k,
            a.SizeScale + (b.SizeScale - a.SizeScale) * k,
            a.CoreHighlight + (b.CoreHighlight - a.CoreHighlight) * k);
    }

    private static ColorRgba PurifySceneColor(double r, double g, double b, double meanPremulY)
    {
        double maxC = Math.Max(r, Math.Max(g, b));
        double minC = Math.Min(r, Math.Min(g, b));
        double mid = 0.5 * (maxC + minC);

        double satBoost = 2.4;
        double pr = mid + (r - mid) * satBoost;
        double pg = mid + (g - mid) * satBoost;
        double pb = mid + (b - mid) * satBoost;

        double pMax = Math.Max(0.001, Math.Max(pr, Math.Max(pg, pb)));
        double targetPeak = meanPremulY > 0.55 ? 0.82 : 0.96;
        double lift = Math.Clamp(targetPeak / pMax, 0.65, 2.2);

        return new ColorRgba(
            Math.Clamp(pr * lift, 0.0, 1.0),
            Math.Clamp(pg * lift, 0.0, 1.0),
            Math.Clamp(pb * lift, 0.0, 1.0),
            1.0);
    }

    private static double LinearToSrgb(double c)
    {
        double clamped = Math.Clamp(c, 0.0, 1.0);
        return clamped <= 0.0031308
            ? 12.92 * clamped
            : 1.055 * Math.Pow(clamped, 1.0 / 2.4) - 0.055;
    }

    private static Vector2 BrownianDrift(double ageSec, double age01, double driftPx, double rFreq, double rPhase, double rAngle)
    {
        double env = Curves.EaseOut(Math.Clamp(age01 * 1.4, 0.0, 1.0));
        double p1 = rPhase * 6.2831853;
        double p2 = rAngle * 6.2831853;
        double w1 = 2.1 + 1.7 * rFreq;
        double w2 = 1.4 + 1.3 * rAngle;
        double dx = driftPx * env * (0.62 * Math.Sin(ageSec * w1 + p1) + 0.38 * Math.Cos(ageSec * w2 + p2));
        double dy = driftPx * 0.45 * env * (0.58 * Math.Cos(ageSec * (w1 * 0.82) + p2) + 0.42 * Math.Sin(ageSec * w2 + p1));
        return new Vector2(dx, dy);
    }

    private static SpriteHandle PickBokehVariant(double sizePx, SpriteHandle small, SpriteHandle medium, SpriteHandle large)
        => sizePx < 36.0 ? small : (sizePx < 92.0 ? medium : large);

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

    private static ulong ParticleSeed(ParticleView particle)
    {
        ulong seed = unchecked((ulong)particle.BirthUs);
        if (particle.Id.Origin is ParticleOrigin.Continuous cont)
            seed ^= (cont.SequenceStartingAt1 * 0x9E3779B97F4A7C15UL) ^ unchecked((ulong)cont.EpisodeStartUs);
        else if (particle.Id.Origin is ParticleOrigin.Burst burst)
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

    private static Affine2 BuildSquareTransform(Point2 center, double size, double angleRad)
    {
        double s = Math.Max(1.0, size);
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        double a = cos * s;
        double b = sin * s;
        double c = -sin * s;
        double d = cos * s;
        return new Affine2(a, b, c, d, center.X - (a + c) * 0.5, center.Y - (b + d) * 0.5);
    }
}
