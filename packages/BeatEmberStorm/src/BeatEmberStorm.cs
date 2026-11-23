using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record BeatEmberData(
    SpriteHandle EmberSmall,
    SpriteHandle EmberMedium,
    SpriteHandle EmberLarge,
    SpriteHandle SmokeSmall,
    SpriteHandle SmokeMedium,
    SpriteHandle SmokeLarge,
    double UpdraftPxPerSec,
    double TurbulencePx,
    double EmberSizePx,
    long TrailUs,
    ColorRgba HotColor,
    ColorRgba EmberColor,
    ColorRgba SmokeColor) : PreparedEffect;

/// <summary>重低音炽焰余烬风暴：在 Declare 阶段扫描全曲音频构建动态变速火星发射轨与双事件音节爆发，结合 SVG 图集绘制热烟、火星与流光火丝。</summary>
public sealed class BeatEmberStorm : IPureKaraokeEffect<BeatEmberData>, IPureVisualEffect<BeatEmberData>
{
    private const int MaxRateSlices = 1020;
    private static readonly EmitterId CursorEmbersId = new("cursor-embers");
    private static readonly EmitterId TokenBurstId = new("token-burst");
    private static readonly EmitterId TailBurstId = new("tail-burst");
    private static readonly OutputId SmokeOutputId = new("smoke");
    private static readonly OutputId TrailsOutputId = new("trails");
    private static readonly OutputId EmbersOutputId = new("embers");

    public BeatEmberData Declare(DeclareContext source)
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

        double baseRate = p.GetNumber("baseRate");
        double bassBoost = p.GetNumber("bassBoostMultiplier");
        int burstMaxCount = (int)p.GetInteger("burstMaxCount");
        double updraftPx = p.GetNumber("updraftPxPerSec");
        double turbulencePx = p.GetNumber("turbulencePx");
        double emberSizePx = p.GetNumber("emberSizePx");
        long trailUs = p.GetDuration("trailUs");
        long lifeUs = p.GetDuration("lifeUs");
        ColorRgba hotColor = p.GetColor("hotColor");
        ColorRgba emberColor = p.GetColor("emberColor");
        ColorRgba smokeColor = p.GetColor("smokeColor");
        bool bridgeLines = p.GetBoolean("bridgeLines");
        double fallbackBpm = p.GetNumber("fallbackBpm");

        var fullRange = new TimeRange(0, endUs);
        var presentationLoads = source.Indexes.Presentations.GetRange(fullRange, new SourceFilter.Project());
        var rateTrack = BuildDynamicRateTrack(source.Audio, endUs, baseRate, bassBoost, fallbackBpm);

        var bridge = bridgeLines
            ? new BridgePolicy(TransitionMode.Bridge, 280_000L, 2_200_000L, Curves.EaseInOutCurve)
            : BridgePolicy.Jump;

        var cursorSpec = new ParticleSpec
        {
            LifetimeUs = lifeUs,
            SizePx = emberSizePx,
            Color = hotColor,
            SpreadPx = 10.0,
            TrailDurationUs = Math.Min(lifeUs, trailUs),
            Velocity = new Vector2(0.0, -updraftPx * 0.72),
            Rendering = ParticleRenderMode.Custom,
            Layer = OverlayLayer.AboveText,
            SeedStreamKey = "beat-ember-cursor",
            Motion = new ParticleMotion
            {
                Acceleration = new Vector2(0.0, -updraftPx * 0.38),
                Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseOutCurve),
                Size = new ParticleAgeCurve(1.0, 0.25, Curves.LinearCurve),
            },
        };

        var cursorPlan = new CursorEmissionPlan(
            CursorEmbersId,
            rateTrack,
            cursorSpec,
            CursorAnchorMode.AdvanceCenter,
            bridge);

        var tokenSelector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
        var tokenRows = ImmutableArray.CreateBuilder<BurstEventRow>();
        if (burstMaxCount > 0)
        {
            var burstSpec = new ParticleSpec
            {
                LifetimeUs = lifeUs,
                SizePx = emberSizePx * 1.12,
                Color = hotColor,
                SpreadPx = 12.0,
                TrailDurationUs = Math.Min(lifeUs, trailUs),
                Velocity = new Vector2(0.0, -updraftPx * 0.96),
                Rendering = ParticleRenderMode.Custom,
                Layer = OverlayLayer.AboveText,
                SeedStreamKey = "beat-ember-burst",
                Motion = new ParticleMotion
                {
                    Acceleration = new Vector2(0.0, -updraftPx * 0.45),
                    Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseOutCurve),
                    Size = new ParticleAgeCurve(1.15, 0.20, Curves.LinearCurve),
                },
            };

            int eventCount = source.Timeline.Count(tokenSelector);
            for (int i = 0; i < eventCount; i++)
            {
                if (source.Timeline.Nth(tokenSelector, i) is TokenStarted ev &&
                    HasActivePresentationLoad(presentationLoads, ev.LineId, ev.TimeUs))
                {
                    double energy = SampleAudioEnergy(source.Audio, ev.TimeUs, fallbackBpm);
                    int count = burstMaxCount == 1
                        ? 1
                        : Math.Clamp((int)Math.Round(2.0 + (burstMaxCount - 2.0) * Math.Pow(energy, 0.85)), 1, burstMaxCount);
                    tokenRows.Add(new BurstEventRow(ev.EventId, count, new BurstAnchor.SourceToken(ev.TokenId), burstSpec));
                }
            }
        }
        var tokenBurstPlan = new EventBurstPlan(TokenBurstId, tokenSelector, tokenRows.ToImmutable());

        var tailSelector = new TimelineSelector(TimelineEventKind.TokenCompleted, new TimelineScope.Project());
        var tailRows = ImmutableArray.CreateBuilder<BurstEventRow>();
        if (burstMaxCount > 0)
        {
            long tailLifeUs = Math.Min(5_000_000L, (long)(lifeUs * 1.15));
            var tailSpec = new ParticleSpec
            {
                LifetimeUs = tailLifeUs,
                SizePx = emberSizePx * 1.55,
                Color = smokeColor,
                SpreadPx = 16.0,
                TrailDurationUs = 0,
                Velocity = new Vector2(0.0, -updraftPx * 0.52),
                Rendering = ParticleRenderMode.Custom,
                Layer = OverlayLayer.BehindText,
                SeedStreamKey = "beat-ember-tail",
                Motion = new ParticleMotion
                {
                    Acceleration = new Vector2(0.0, -updraftPx * 0.16),
                    Opacity = new ParticleAgeCurve(1.0, 0.0, Curves.EaseOutCurve),
                    Size = new ParticleAgeCurve(0.85, 1.45, Curves.LinearCurve),
                },
            };

            int tailMax = Math.Max(1, Math.Min(5, burstMaxCount / 3 + 1));
            int tailCount = source.Timeline.Count(tailSelector);
            for (int i = 0; i < tailCount; i++)
            {
                if (source.Timeline.Nth(tailSelector, i) is TokenCompleted ev &&
                    HasActivePresentationLoad(presentationLoads, ev.LineId, ev.TimeUs))
                {
                    double energy = SampleAudioEnergy(source.Audio, ev.TimeUs, fallbackBpm);
                    int count = Math.Clamp((int)Math.Round(1.0 + (tailMax - 1.0) * energy), 1, tailMax);
                    tailRows.Add(new BurstEventRow(ev.EventId, count, new BurstAnchor.SourceToken(ev.TokenId), tailSpec));
                }
            }
        }
        var tailBurstPlan = new EventBurstPlan(TailBurstId, tailSelector, tailRows.ToImmutable());

        return new BeatEmberData(
            source.Resources.GetSprite("ember-shard", "small"),
            source.Resources.GetSprite("ember-shard", "medium"),
            source.Resources.GetSprite("ember-shard", "large"),
            source.Resources.GetSprite("smoke-puff", "small"),
            source.Resources.GetSprite("smoke-puff", "medium"),
            source.Resources.GetSprite("smoke-puff", "large"),
            updraftPx,
            turbulencePx,
            emberSizePx,
            trailUs,
            hotColor,
            emberColor,
            smokeColor)
        {
            Particles = new ParticleSchedule([cursorPlan], [tokenBurstPlan, tailBurstPlan]),
            VisualOutputs =
            [
                new VisualOutputDeclaration(SmokeOutputId, VisualPrimitiveKind.Sprites, OverlayLayer.BehindText, VisualResourceKind.SvgAtlas),
                new VisualOutputDeclaration(TrailsOutputId, VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
                new VisualOutputDeclaration(EmbersOutputId, VisualPrimitiveKind.Sprites, OverlayLayer.AboveText, VisualResourceKind.SvgAtlas),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, BeatEmberData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, BeatEmberData d, VisualOutput output)
    {
        var alive = frame.Particles.Alive;
        if (alive.Length == 0)
        {
            output.DrawAtlasSprites(SmokeOutputId, ImmutableArray<AtlasSprite>.Empty, OverlayLayer.BehindText);
            output.DrawTrails(TrailsOutputId, ImmutableArray<Trail>.Empty, OverlayLayer.AboveText);
            output.DrawAtlasSprites(EmbersOutputId, ImmutableArray<AtlasSprite>.Empty, OverlayLayer.AboveText);
            return;
        }

        var smokeSprites = ImmutableArray.CreateBuilder<AtlasSprite>(alive.Length);
        var trails = ImmutableArray.CreateBuilder<Trail>(alive.Length);
        var emberSprites = ImmutableArray.CreateBuilder<AtlasSprite>(alive.Length);

        long now = frame.TimeUs;

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
            double rSize = frame.Random.GetUnitDouble("ember-size", seed);
            double rPhase = frame.Random.GetUnitDouble("ember-phase", seed);
            double rFreq = frame.Random.GetUnitDouble("ember-freq", seed);
            double rDir = frame.Random.GetUnitDouble("ember-dir", seed);
            double rSpin = frame.Random.GetUnitDouble("ember-spin", seed);
            double rTurbulence = frame.Random.GetUnitDouble("ember-turb", seed);
            double rSmoke = frame.Random.GetUnitDouble("ember-smoke", seed);

            bool isTailPuff = particle.Id.EmitterId == TailBurstId;

            Vector2 disp = ThermalDisplacement(ageSec, age01, d.TurbulencePx, d.UpdraftPxPerSec, rFreq, rPhase, rDir, rTurbulence);
            Point2 worldPos = new Point2(particle.Position.X + disp.X, particle.Position.Y + disp.Y);

            double popIn = Math.Clamp(ageUs / 55_000.0, 0.0, 1.0);
            double lifeFade = 1.0 - Curves.EaseOut(Math.Clamp((age01 - 0.18) / 0.82, 0.0, 1.0));

            if (isTailPuff || rSmoke < 0.38)
            {
                double puffFadeIn = Math.Clamp(age01 / 0.14, 0.0, 1.0);
                double puffFadeOut = 1.0 - Curves.EaseOut(age01);
                double puffAlpha = d.SmokeColor.Alpha * puffFadeIn * puffFadeOut * (isTailPuff ? 0.92 : 0.56);
                if (puffAlpha > 0.01)
                {
                    double puffSize = Math.Max(8.0, d.EmberSizePx * (isTailPuff ? 2.1 : 1.55) * (0.7 + 0.55 * rSize) * (0.75 + 1.35 * age01));
                    ColorRgba smokeTint = LerpColor(d.EmberColor, d.SmokeColor, Math.Clamp(age01 * 1.4, 0.0, 1.0), puffAlpha);
                    SpriteHandle smokeHandle = PickVariant(puffSize, d.SmokeSmall, d.SmokeMedium, d.SmokeLarge);
                    double smokeAngle = rPhase * 6.2831853 + (rSpin - 0.5) * 1.4 * ageSec;
                    smokeSprites.Add(new AtlasSprite(smokeHandle, BuildRectTransform(worldPos, puffSize, puffSize, smokeAngle), smokeTint));
                }
            }

            if (isTailPuff && rSmoke >= 0.45)
                continue;

            double flicker = 0.76 + 0.24 * Math.Sin(ageSec * (14.0 + 10.0 * rFreq) + rPhase * 6.2831853);
            double emberAlpha = Math.Clamp(popIn * lifeFade * flicker * d.HotColor.Alpha, 0.0, 1.0);
            if (emberAlpha <= 0.01)
                continue;

            ColorRgba shardColor = age01 < 0.36
                ? LerpColor(d.HotColor, d.EmberColor, age01 / 0.36, emberAlpha)
                : LerpColor(d.EmberColor, d.SmokeColor, (age01 - 0.36) / 0.64, emberAlpha);

            if (d.TrailUs > 0 && !isTailPuff)
            {
                var trailSamples = frame.Particles.GetTrail(particle.Id);
                if (trailSamples.Length >= 2)
                {
                    long trailSpan = Math.Max(1L, trailSamples[^1].TimeUs - trailSamples[0].TimeUs);
                    var points = ImmutableArray.CreateBuilder<TrailPoint>(trailSamples.Length);
                    double headWidth = Math.Max(1.6, d.EmberSizePx * 0.32 * (0.65 + 0.5 * rSize) * (1.0 - 0.40 * age01));
                    for (int t = 0; t < trailSamples.Length; t++)
                    {
                        var sample = trailSamples[t];
                        long sAgeUs = Math.Max(0L, sample.TimeUs - particle.BirthUs);
                        double sAge01 = Math.Clamp(sAgeUs / (double)life, 0.0, 1.0);
                        double sAgeSec = sAgeUs / 1_000_000.0;
                        Vector2 sDisp = ThermalDisplacement(sAgeSec, sAge01, d.TurbulencePx, d.UpdraftPxPerSec, rFreq, rPhase, rDir, rTurbulence);
                        Point2 sPos = new Point2(sample.Position.X + sDisp.X, sample.Position.Y + sDisp.Y);

                        double wFrac = (sample.TimeUs - trailSamples[0].TimeUs) / (double)trailSpan;
                        double wCurve = wFrac * wFrac;
                        ColorRgba tCol = LerpColor(d.EmberColor, d.HotColor, wFrac, emberAlpha * wCurve * 0.90);
                        points.Add(new TrailPoint(sPos, headWidth * wFrac, tCol));
                    }
                    trails.Add(new Trail(points.ToImmutable()));
                }
            }

            double baseSize = Math.Max(3.0, particle.SizePx * (0.58 + 0.72 * rSize) * (0.55 + 0.45 * popIn));
            double emberW = baseSize * 0.68;
            double emberH = baseSize * 1.28;
            double tangentAngle = Math.Atan2(
                particle.Birth.InitialVelocity.Y - d.UpdraftPxPerSec * 0.35 * ageSec,
                (rDir - 0.5) * d.UpdraftPxPerSec * 0.8 + Math.Cos(ageSec * 4.0 + rPhase * 6.2831853) * d.TurbulencePx) + Math.PI * 0.5;
            double shardAngle = tangentAngle + (rSpin - 0.5) * 1.8 * ageSec;

            SpriteHandle emberHandle = PickVariant(emberH, d.EmberSmall, d.EmberMedium, d.EmberLarge);
            emberSprites.Add(new AtlasSprite(emberHandle, BuildRectTransform(worldPos, emberW, emberH, shardAngle), shardColor));
        }

        output.DrawAtlasSprites(SmokeOutputId, smokeSprites.ToImmutable(), OverlayLayer.BehindText);
        output.DrawTrails(TrailsOutputId, trails.ToImmutable(), OverlayLayer.AboveText);
        output.DrawAtlasSprites(EmbersOutputId, emberSprites.ToImmutable(), OverlayLayer.AboveText);
    }

    private static RateTrack BuildDynamicRateTrack(
        AudioFeatures audio,
        long endUs,
        double baseRate,
        double bassBoostMultiplier,
        double fallbackBpm)
    {
        long sliceUs = Math.Max(200_000L, (endUs + (MaxRateSlices - 1)) / MaxRateSlices);
        int sliceCount = (int)Math.Max(1L, Math.Min(MaxRateSlices, (endUs + sliceUs - 1L) / sliceUs));

        var audioRange = audio.GetRange(new TimeRange(0, endUs));
        var points = audioRange.Points;
        bool hasPoints = !points.IsDefaultOrEmpty &&
                         (audioRange.Availability == AudioAvailability.Available ||
                          audioRange.Availability == AudioAvailability.OutsideRange);

        var segments = ImmutableArray.CreateBuilder<RateSegment>(sliceCount);
        int ptIndex = 0;
        double prevEnergy = hasPoints
            ? SampleAudioEnergy(audio, 0L, fallbackBpm)
            : SampleFallbackEnergy(0L, fallbackBpm);

        for (int k = 0; k < sliceCount; k++)
        {
            long t0 = k * sliceUs;
            long t1 = k == sliceCount - 1 ? endUs : Math.Min(endUs, (k + 1) * sliceUs);
            if (t1 <= t0)
                break;

            double nextEnergy;
            if (hasPoints)
            {
                while (ptIndex < points.Length && points[ptIndex].TimeUs < t0)
                    ptIndex++;

                double sumEnergy = 0.0;
                double peakEnergy = 0.0;
                int hits = 0;
                int scan = ptIndex;
                while (scan < points.Length && points[scan].TimeUs < t1)
                {
                    var vals = points[scan].Values;
                    double e = Math.Clamp(0.62 * vals.BassLevel + 0.38 * vals.Pulse, 0.0, 1.0);
                    sumEnergy += e;
                    if (e > peakEnergy) peakEnergy = e;
                    hits++;
                    scan++;
                }
                ptIndex = scan;

                if (hits > 0)
                    nextEnergy = 0.55 * (sumEnergy / hits) + 0.45 * peakEnergy;
                else
                    nextEnergy = SampleAudioEnergy(audio, t1 - 1L, fallbackBpm);
            }
            else
            {
                nextEnergy = SampleFallbackEnergy(t1, fallbackBpm);
            }

            double r0 = ComputeEmissionRate(baseRate, bassBoostMultiplier, prevEnergy);
            double r1 = ComputeEmissionRate(baseRate, bassBoostMultiplier, nextEnergy);
            segments.Add(new RateSegment(new TimeRange(t0, t1), r0, r1));
            prevEnergy = nextEnergy;
        }

        if (segments.Count == 0)
            segments.Add(new RateSegment(new TimeRange(0, endUs), baseRate, baseRate));

        return new RateTrack(segments.ToImmutable(), [new TimeRange(0, endUs)]);
    }

    private static double ComputeEmissionRate(double baseRate, double bassBoostMultiplier, double energy)
        => Math.Max(0.5, baseRate * (0.45 + (0.65 + bassBoostMultiplier) * Math.Pow(Math.Clamp(energy, 0.0, 1.0), 1.25)));

    private static double SampleAudioEnergy(AudioFeatures audio, long timeUs, double fallbackBpm)
    {
        long clamped = Math.Max(0L, timeUs);
        var sample = audio.GetAt(clamped);
        if (sample.Availability == AudioAvailability.Available && sample.Values is { } values)
            return Math.Clamp(0.62 * values.BassLevel + 0.38 * values.Pulse, 0.0, 1.0);
        return SampleFallbackEnergy(clamped, fallbackBpm);
    }

    private static double SampleFallbackEnergy(long timeUs, double fallbackBpm)
    {
        if (fallbackBpm <= 0.0)
            return 0.35;
        double beats = (timeUs / 1_000_000.0) * (fallbackBpm / 60.0);
        double phase = beats - Math.Floor(beats);
        double impulse = Math.Pow(1.0 - phase, 2.2);
        return Math.Clamp(0.20 + 0.78 * impulse, 0.0, 1.0);
    }

    private static Vector2 ThermalDisplacement(
        double ageSec,
        double age01,
        double turbulencePx,
        double updraftPxPerSec,
        double rFreq,
        double rPhase,
        double rDir,
        double rTurbulence)
    {
        double amp = turbulencePx * (0.45 + 0.75 * rTurbulence) * Curves.EaseOut(age01);
        double swirlX = amp * Math.Sin(ageSec * (3.2 + 2.4 * rFreq) + rPhase * 6.2831853);
        double driftX = (rDir - 0.5) * 2.0 * updraftPxPerSec * 0.42 * ageSec;
        double swirlY = turbulencePx * 0.22 * Math.Cos(ageSec * 2.7 + rPhase * 6.2831853);
        return new Vector2(swirlX + driftX, swirlY);
    }

    private static bool HasActivePresentationLoad(ImmutableArray<SourcePresentationLoad> loads, LineId lineId, long timeUs)
    {
        for (int i = 0; i < loads.Length; i++)
        {
            if (loads[i].Binding.LineId == lineId && loads[i].Contains(timeUs))
                return true;
        }
        return false;
    }

    private static SpriteHandle PickVariant(double pixelSize, SpriteHandle small, SpriteHandle medium, SpriteHandle large)
        => pixelSize < 22.0 ? small : (pixelSize < 52.0 ? medium : large);

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

    private static Affine2 BuildRectTransform(Point2 center, double width, double height, double angleRad)
    {
        double w = Math.Max(1.0, width);
        double h = Math.Max(1.0, height);
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        double a = cos * w;
        double b = sin * w;
        double c = -sin * h;
        double d = cos * h;
        return new Affine2(a, b, c, d, center.X - (a + c) * 0.5, center.Y - (b + d) * 0.5);
    }
}
