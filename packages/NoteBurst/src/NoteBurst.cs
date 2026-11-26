using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record NoteBurstData(
    VectorHandle SingleNote,
    VectorHandle BeamedNote,
    int BurstCount,
    long LifeUs,
    double NoteSizePx,
    double BurstSpeedPx,
    double Drag,
    double FanHalfRad,
    double SpawnLiftPx,
    double SwayPx,
    double MaxTiltRad,
    ColorRgba ColorA,
    ColorRgba ColorB,
    ColorRgba ColorC,
    bool SparkleDots,
    OverlayLayer Layer) : PreparedEffect;

/// <summary>音符随唱爆发：每个词素开唱瞬间向正上方扇形爆出单音符（🎵）与连音符（🎶）矢量粒子，轻快弹跳后短时间自然消散。</summary>
public sealed class NoteBurst : IPureKaraokeEffect<NoteBurstData>, IPureVisualEffect<NoteBurstData>
{
    private static readonly EmitterId BurstEmitterId = new("note-burst");
    private static readonly OutputId NotesOutputId = new("notes");
    private static readonly OutputId SparklesOutputId = new("sparkles");

    public NoteBurstData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        int burstCount = (int)p.GetInteger("burstCount");
        long lifeUs = p.GetDuration("lifeUs");
        double noteSizePx = p.GetNumber("noteSizePx");
        double burstSpeedPx = p.GetNumber("burstSpeedPx");
        double drag = p.GetNumber("drag");
        double fanHalfRad = p.GetNumber("fanAngleDeg") * (Math.PI / 180.0);
        double spawnLiftPx = p.GetNumber("spawnLiftPx");
        double swayPx = p.GetNumber("swayPx");
        double maxTiltRad = p.GetNumber("maxTiltDeg") * (Math.PI / 180.0);
        ColorRgba colorA = p.GetColor("noteColorA");
        ColorRgba colorB = p.GetColor("noteColorB");
        ColorRgba colorC = p.GetColor("noteColorC");
        bool sparkleDots = p.GetBoolean("sparkleDots");
        OverlayLayer layer = string.Equals(p.GetEnum("layer"), "behind", StringComparison.Ordinal)
            ? OverlayLayer.BehindText
            : OverlayLayer.AboveText;

        long endUs = Math.Max(1L, source.ProjectDurationUs ?? 1L);
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { IsValid: true } range)
                endUs = Math.Max(endUs, range.EndUs);
        }
        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].TimeRange is { IsValid: true } range)
                endUs = Math.Max(endUs, range.EndUs);
        }

        var fullRange = new TimeRange(0, endUs + 1_000_000L);
        var presentationLoads = source.Indexes.Presentations.GetRange(fullRange, new SourceFilter.Project());

        var selector = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Project());
        var rows = ImmutableArray.CreateBuilder<BurstEventRow>();
        if (burstCount > 0)
        {
            var spec = new ParticleSpec
            {
                LifetimeUs = Math.Clamp(lifeUs, 150_000L, 4_800_000L),
                SizePx = Math.Max(4.0, noteSizePx),
                Color = colorA,
                SpreadPx = 12.0,
                TrailDurationUs = 0,
                Velocity = Vector2.Zero,
                Rendering = ParticleRenderMode.Custom,
                Layer = layer,
                SeedStreamKey = "note-burst-token",
                Motion = ParticleMotion.ConstantVelocity,
            };

            int evCount = source.Timeline.Count(selector);
            for (int i = 0; i < evCount; i++)
            {
                if (source.Timeline.Nth(selector, i) is TokenStarted ev &&
                    !string.IsNullOrWhiteSpace(ev.Source.Text) &&
                    HasActivePresentationLoad(presentationLoads, ev.LineId, ev.TimeUs))
                {
                    rows.Add(new BurstEventRow(
                        ev.EventId,
                        burstCount,
                        new BurstAnchor.SourceToken(ev.TokenId),
                        spec));
                }
            }
        }

        var burstPlan = new EventBurstPlan(BurstEmitterId, selector, rows.ToImmutable());

        return new NoteBurstData(
            source.Resources.GetVector("note-single"),
            source.Resources.GetVector("note-beamed"),
            burstCount,
            lifeUs,
            noteSizePx,
            burstSpeedPx,
            drag,
            fanHalfRad,
            spawnLiftPx,
            swayPx,
            maxTiltRad,
            colorA,
            colorB,
            colorC,
            sparkleDots,
            layer)
        {
            Particles = new ParticleSchedule([], [burstPlan]),
            VisualOutputs =
            [
                new VisualOutputDeclaration(NotesOutputId, VisualPrimitiveKind.Paths, layer, VisualResourceKind.SvgVector),
                new VisualOutputDeclaration(SparklesOutputId, VisualPrimitiveKind.Dots, layer),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, NoteBurstData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, NoteBurstData d, VisualOutput output)
    {
        var alive = frame.Particles.Alive;
        if (alive.Length == 0)
        {
            output.DrawPaths(NotesOutputId, ImmutableArray<VectorVisual>.Empty, d.Layer);
            output.DrawDots(SparklesOutputId, ImmutableArray<Dot>.Empty, d.Layer);
            return;
        }

        var notes = ImmutableArray.CreateBuilder<VectorVisual>(alive.Length);
        var sparkles = ImmutableArray.CreateBuilder<Dot>(d.SparkleDots ? alive.Length : 0);

        long now = frame.TimeUs;
        double drag = Math.Max(0.1, d.Drag);
        int burstCount = Math.Max(1, d.BurstCount);

        for (int i = 0; i < alive.Length; i++)
        {
            var particle = alive[i];
            if (particle.Id.EmitterId != BurstEmitterId)
                continue;

            long ageUs = now - particle.BirthUs;
            if (ageUs < 0)
                continue;

            long life = Math.Max(1L, particle.DeathUs - particle.BirthUs);
            double age01 = Math.Clamp(ageUs / (double)life, 0.0, 1.0);
            double ageSec = ageUs / 1_000_000.0;

            int ordinal = particle.Id.Origin is ParticleOrigin.Burst b ? b.ParticleOrdinal : i;
            ulong eventSeed = unchecked((ulong)particle.BirthUs) * 0x9E3779B97F4A7C15UL;
            ulong seed = eventSeed ^ ((ulong)(ordinal + 1) * 0xBF58476D1CE4E5B9UL);

            double rAngle = frame.Random.GetUnitDouble("note-angle", seed);
            double rSpeed = frame.Random.GetUnitDouble("note-speed", seed);
            double rSize = frame.Random.GetUnitDouble("note-size", seed);
            double rPhase = frame.Random.GetUnitDouble("note-phase", seed);
            double rFreq = frame.Random.GetUnitDouble("note-freq", seed);
            double rTilt = frame.Random.GetUnitDouble("note-tilt", seed);
            double rJitterX = frame.Random.GetUnitDouble("note-jx", seed);
            double rCol = frame.Random.GetUnitDouble("note-col", seed);

            // 分层扇形分布 + 中央略高的喷泉弧线，避免多颗音符重叠成团
            double slotFrac = burstCount == 1 ? 0.5 : ordinal / (double)(burstCount - 1);
            double normOffset = slotFrac * 2.0 - 1.0;
            double baseAngle = normOffset * d.FanHalfRad;
            double jitterAngle = (rAngle - 0.5) * (d.FanHalfRad * 0.42 / Math.Max(1.0, burstCount * 0.5));
            double launchAngle = baseAngle + jitterAngle;

            double fountainBoost = 1.0 + 0.22 * (1.0 - Math.Abs(normOffset));
            double speed = d.BurstSpeedPx * fountainBoost * (0.78 + 0.44 * rSpeed);
            double travel = (1.0 - Math.Exp(-drag * ageSec)) / drag;

            double swayX = d.SwayPx * Math.Sin(ageSec * (5.5 + 3.2 * rFreq) + rPhase * 6.2831853) * Curves.EaseOut(age01);
            double spawnX = particle.Birth.BirthPosition.X + normOffset * 16.0 + (rJitterX - 0.5) * 14.0;
            double spawnY = particle.Birth.BirthPosition.Y - d.SpawnLiftPx;

            double x = spawnX + Math.Sin(launchAngle) * speed * travel + swayX;
            double y = spawnY - Math.Cos(launchAngle) * speed * travel + 16.0 * ageSec * ageSec;

            // 干脆弹跳入场（前 75ms 快速冲顶微过冲）+ 末段迅速淡出消散
            double popScale;
            if (age01 < 0.14)
            {
                double tIn = age01 / 0.14;
                popScale = 0.25 + 0.93 * Curves.EaseOut(tIn);
            }
            else if (age01 < 0.30)
            {
                double tSettle = (age01 - 0.14) / 0.16;
                popScale = 1.18 - 0.18 * Curves.EaseInOutCurve.Evaluate(tSettle);
            }
            else
            {
                double tTail = (age01 - 0.30) / 0.70;
                popScale = 1.0 - 0.28 * tTail;
            }

            double alphaIn = Math.Clamp(age01 / 0.10, 0.0, 1.0);
            double alphaOut = 1.0 - Curves.EaseOut(Math.Clamp((age01 - 0.52) / 0.48, 0.0, 1.0));
            double visibility = Math.Clamp(alphaIn * alphaOut, 0.0, 1.0);
            if (visibility <= 0.015)
                continue;

            ColorRgba picked = PickPaletteColor(ordinal, rCol, d.ColorA, d.ColorB, d.ColorC);
            double finalAlpha = Math.Clamp(picked.Alpha * visibility, 0.0, 1.0);
            if (finalAlpha <= 0.015)
                continue;

            double size = Math.Max(4.0, d.NoteSizePx * (0.76 + 0.44 * rSize) * popScale);
            double baseTilt = (rTilt - 0.5) * 1.15 * d.MaxTiltRad + launchAngle * 0.28;
            double bobTilt = d.MaxTiltRad * 0.45 * Math.Sin(ageSec * (6.2 + 2.8 * rFreq) + rPhase * 6.2831853);
            double angleRad = baseTilt + bobTilt;

            // 单音符（🎵）与连音符（🎶）交替出现，保持每次爆发都有丰富音乐符号层次
            bool isBeamed = ((ordinal + (int)((eventSeed >> 4) & 1UL)) & 1) == 1;
            VectorHandle handle = isBeamed ? d.BeamedNote : d.SingleNote;
            ColorRgba tint = new(picked.Red, picked.Green, picked.Blue, finalAlpha);

            notes.Add(new VectorVisual(handle, BuildNoteTransform(new Point2(x, y), size, angleRad), tint));

            if (d.SparkleDots && age01 < 0.78)
            {
                double sparkT = age01 / 0.78;
                double sparkFade = Math.Clamp(alphaIn * (1.0 - Curves.EaseOut(sparkT)), 0.0, 1.0);
                double sparkTwinkle = 0.65 + 0.35 * Math.Sin(ageSec * 24.0 + rPhase * 6.2831853);
                double sparkAlpha = Math.Clamp(sparkFade * sparkTwinkle * 0.92, 0.0, 1.0);
                if (sparkAlpha > 0.02)
                {
                    double sparkSpeed = speed * (1.18 + 0.22 * rTilt);
                    double sx = spawnX + Math.Sin(launchAngle * 1.18) * sparkSpeed * travel + swayX * 0.5;
                    double sy = spawnY - Math.Cos(launchAngle * 1.18) * sparkSpeed * travel - 6.0;
                    double dotR = Math.Max(1.1, (2.2 + 2.0 * rSize) * (1.0 - 0.55 * sparkT));
                    ColorRgba dotCol = LerpColor(picked, new ColorRgba(1, 1, 1, 1), 0.45, sparkAlpha);
                    sparkles.Add(new Dot(new Point2(sx, sy), dotR, dotCol));
                }
            }
        }

        output.DrawPaths(NotesOutputId, notes.ToImmutable(), d.Layer);
        output.DrawDots(SparklesOutputId, sparkles.ToImmutable(), d.Layer);
    }

    private static ColorRgba PickPaletteColor(int ordinal, double rand01, ColorRgba a, ColorRgba b, ColorRgba c)
    {
        int slot = (ordinal + (rand01 > 0.66 ? 1 : 0)) % 3;
        return slot switch
        {
            0 => a,
            1 => b,
            _ => c,
        };
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

    private static ColorRgba LerpColor(ColorRgba a, ColorRgba b, double t, double alpha)
    {
        double k = Math.Clamp(t, 0.0, 1.0);
        return new ColorRgba(
            Math.Clamp(a.Red + (b.Red - a.Red) * k, 0.0, 1.0),
            Math.Clamp(a.Green + (b.Green - a.Green) * k, 0.0, 1.0),
            Math.Clamp(a.Blue + (b.Blue - a.Blue) * k, 0.0, 1.0),
            Math.Clamp(alpha, 0.0, 1.0));
    }

    private static Affine2 BuildNoteTransform(Point2 center, double size, double angleRad)
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
