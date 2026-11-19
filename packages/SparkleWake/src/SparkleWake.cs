using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct GlyphVerticalSpan(double TopOffsetLocal, double BottomOffsetLocal);

public readonly record struct ScaledSpan(Vector2 Top, Vector2 Bottom);

public readonly record struct HeadReference(bool Found, Point2 Position, Vector2 SweepDirection);

public sealed record SparkleWakeData(
    VectorHandle SparkleVector,
    long LifeUs,
    double LengthPx,
    double StarSizePx,
    double HeightScale,
    double SpreadXPx,
    double DriftSpeedPx,
    double MaxTiltRad,
    double SpinHz,
    double TwinkleStrength,
    ColorRgba ColorA,
    ColorRgba ColorB,
    double ColorRatio,
    OverlayLayer Layer) : PreparedEffect;

/// <summary>星芒尾迹：随播放头冒出两种可配颜色的纤巧四角星芒，在高度上分布于完整字形区间，按设定分布长度与寿命平稳闪烁渐隐，生成后几乎静止停留。</summary>
public sealed class SparkleWake : IPureKaraokeEffect<SparkleWakeData>, IPureVisualEffect<SparkleWakeData>
{
    private static readonly EmitterId SparklesEmitterId = new("sparkles");
    private static readonly OutputId StarsOutputId = new("stars");

    public SparkleWakeData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long end = Math.Max(1L, source.ProjectDurationUs ?? 1L);
        var sourceLines = source.Content.Lines;
        for (int i = 0; i < sourceLines.Length; i++)
        {
            if (sourceLines[i].TimeRange is { } range)
                end = Math.Max(end, range.EndUs);
        }
        var sourceTokens = source.Content.Tokens;
        for (int i = 0; i < sourceTokens.Length; i++)
        {
            if (sourceTokens[i].TimeRange is { } range)
                end = Math.Max(end, range.EndUs);
        }

        double rate = p.GetNumber("rate");
        long lifeUs = p.GetDuration("lifeUs");
        double lengthPx = p.GetNumber("lengthPx");
        double starSizePx = p.GetNumber("starSizePx");
        double heightScale = p.GetNumber("heightScale");
        double spreadXPx = p.GetNumber("spreadXPx");
        double driftSpeedPx = p.GetNumber("driftSpeedPx");
        double maxTiltRad = p.GetNumber("maxTiltDeg") * (Math.PI / 180.0);
        double spinHz = p.GetNumber("spinHz");
        double twinkleStrength = p.GetNumber("twinkleStrength");
        ColorRgba colorA = p.GetColor("colorA");
        ColorRgba colorB = p.GetColor("colorB");
        double colorRatio = p.GetNumber("colorRatio");
        OverlayLayer layer = string.Equals(p.GetEnum("layer"), "behind", StringComparison.Ordinal)
            ? OverlayLayer.BehindText
            : OverlayLayer.AboveText;

        var fullRange = new TimeRange(0, end);

        var sparkleSpec = new ParticleSpec
        {
            LifetimeUs = lifeUs,
            SizePx = starSizePx,
            Color = colorA,
            SpreadPx = 0,
            TrailDurationUs = 0,
            Velocity = Vector2.Zero,
            Rendering = ParticleRenderMode.Custom,
            Layer = layer,
            SeedStreamKey = "sparkle-wake",
            Motion = ParticleMotion.ConstantVelocity,
        };

        var emissionPlan = new CursorEmissionPlan(
            SparklesEmitterId,
            new RateTrack([new RateSegment(fullRange, rate, rate)], [fullRange]),
            sparkleSpec,
            CursorAnchorMode.AdvanceCenter,
            BridgePolicy.Jump);

        return new SparkleWakeData(
            source.Resources.GetVector("sparkle"),
            lifeUs,
            lengthPx,
            starSizePx,
            heightScale,
            spreadXPx,
            driftSpeedPx,
            maxTiltRad,
            spinHz,
            twinkleStrength,
            colorA,
            colorB,
            colorRatio,
            layer)
        {
            Particles = new ParticleSchedule([emissionPlan], []),
            VisualOutputs =
            [
                new VisualOutputDeclaration(StarsOutputId, VisualPrimitiveKind.Paths, layer, VisualResourceKind.SvgVector),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, SparkleWakeData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, SparkleWakeData d, VisualOutput output)
    {
        var alive = frame.Particles.Alive;
        var lines = frame.FinalGeometry.Lines;
        if (alive.Length == 0 || lines.Length == 0)
        {
            output.DrawPaths(StarsOutputId, ImmutableArray<VectorVisual>.Empty, d.Layer);
            return;
        }

        var stars = ImmutableArray.CreateBuilder<VectorVisual>(alive.Length);
        long now = frame.TimeUs;
        var cursors = frame.FinalGeometry.Playback.IsAvailable
            ? frame.FinalGeometry.Playback.Cursors
            : ImmutableArray<CursorGeometry>.Empty;
        var tokens = frame.FinalGeometry.Tokens;
        var presentedTokens = frame.FinalGeometry.Presentations.TokensInBinding;

        for (int i = 0; i < alive.Length; i++)
        {
            var particle = alive[i];
            if (particle.Id.EmitterId != SparklesEmitterId)
                continue;
            if (particle.Source is not ParticleSource.Cursor cursorSrc)
                continue;

            long ageUs = now - particle.BirthUs;
            if (ageUs < 0)
                continue;

            // 仅在所属歌词行仍呈现于画布时渲染，避免行隐藏后旧星芒残留并覆盖到后续新行
            BindingId bindingId = cursorSrc.BindingId;
            var lineGeom = FindLineGeometry(lines, bindingId);
            if (lineGeom is null)
                continue;

            long life = Math.Max(1L, particle.DeathUs - particle.BirthUs);
            double age01 = Math.Clamp(ageUs / (double)life, 0.0, 1.0);
            double ageSec = ageUs / 1_000_000.0;

            CursorTrackId trackId = cursorSrc.Track;
            ulong seed = ParticleSeed(particle);

            double rX = frame.Random.GetUnitDouble("sparkle-x", seed);
            double rY = frame.Random.GetUnitDouble("sparkle-y", seed);
            double rSize = frame.Random.GetUnitDouble("sparkle-size", seed);
            double rColor = frame.Random.GetUnitDouble("sparkle-color", seed);
            double rPhase = frame.Random.GetUnitDouble("sparkle-phase", seed);
            double rTwinkleFreq = frame.Random.GetUnitDouble("sparkle-twinkle", seed);
            double rTilt = frame.Random.GetUnitDouble("sparkle-tilt", seed);
            double rSpinDir = frame.Random.GetUnitDouble("sparkle-spin-dir", seed);
            double rDriftDir = frame.Random.GetUnitDouble("sparkle-drift-dir", seed);

            var span = ComputeVerticalSpan(lineGeom.BaseGeometry);
            Point2 advCenter = lineGeom.BaseGeometry.AdvanceBounds.Center;
            Point2 anchorLocal = new Point2(advCenter.X, lineGeom.ProgressPath.CenterY);
            Vector2 topOffsetWorld = lineGeom.BaseGeometry.MapVectorAt(anchorLocal, new Vector2(0.0, span.TopOffsetLocal));
            Vector2 bottomOffsetWorld = lineGeom.BaseGeometry.MapVectorAt(anchorLocal, new Vector2(0.0, span.BottomOffsetLocal));
            Vector2 xJitterWorld = d.SpreadXPx > 0.001
                ? lineGeom.BaseGeometry.MapVectorAt(anchorLocal, new Vector2((rX - 0.5) * 2.0 * d.SpreadXPx, 0.0))
                : Vector2.Zero;

            var scaled = ScaleAroundMid(topOffsetWorld, bottomOffsetWorld, d.HeightScale);
            Vector2 yOffsetWorld = new Vector2(
                scaled.Top.X + (scaled.Bottom.X - scaled.Top.X) * rY,
                scaled.Top.Y + (scaled.Bottom.Y - scaled.Top.Y) * rY);

            Point2 spawnPos = new Point2(
                particle.Birth.BirthPosition.X + xJitterWorld.X + yOffsetWorld.X,
                particle.Birth.BirthPosition.Y + xJitterWorld.Y + yOffsetWorld.Y);

            Point2 starPos = spawnPos;
            if (d.DriftSpeedPx > 0.001)
            {
                double driftAngle = rDriftDir * (Math.PI * 2.0);
                double driftDist = d.DriftSpeedPx * ageSec;
                starPos = new Point2(
                    spawnPos.X + Math.Cos(driftAngle) * driftDist,
                    spawnPos.Y + Math.Sin(driftAngle) * driftDist);
            }

            var headRef = ResolveHeadReference(
                cursors,
                presentedTokens,
                tokens,
                lineGeom,
                bindingId,
                trackId,
                now);

            double distFade = 1.0;
            if (headRef.Found && d.LengthPx > 0.001)
            {
                double alongDist =
                    (headRef.Position.X - starPos.X) * headRef.SweepDirection.X +
                    (headRef.Position.Y - starPos.Y) * headRef.SweepDirection.Y;
                double distFrac = Math.Clamp(Math.Max(0.0, alongDist) / d.LengthPx, 0.0, 1.0);
                if (distFrac >= 0.999)
                    continue;
                distFade = 1.0 - Math.Pow(distFrac, 1.45);
            }

            double popIn = Curves.EaseOut(Math.Clamp(ageUs / 70_000.0, 0.0, 1.0));
            double ageFade = 1.0 - Curves.EaseOut(Math.Clamp((age01 - 0.55) / 0.45, 0.0, 1.0));
            double visibility = popIn * distFade * ageFade;
            if (visibility <= 0.01)
                continue;

            double twinkleOsc = 0.5 + 0.5 * Math.Sin(ageSec * (Math.PI * 2.0) * (1.8 + 1.4 * rTwinkleFreq) + rPhase * (Math.PI * 2.0));
            double twinkleAlpha = 1.0 - d.TwinkleStrength * (1.0 - twinkleOsc);
            double twinkleScale = 1.0 - (d.TwinkleStrength * 0.34) * (1.0 - twinkleOsc);

            double sizeFactor = 0.72 + 0.56 * rSize;
            double size = d.StarSizePx * sizeFactor * (0.35 + 0.65 * popIn) * (0.72 + 0.28 * distFade * ageFade) * twinkleScale;
            if (size <= 0.6)
                continue;

            ColorRgba pickedColor = rColor < d.ColorRatio ? d.ColorA : d.ColorB;
            double alpha = Math.Clamp(pickedColor.Alpha * visibility * twinkleAlpha, 0.0, 1.0);
            if (alpha <= 0.01)
                continue;

            double tiltRad = (rTilt - 0.5) * 2.0 * d.MaxTiltRad;
            double spinDir = rSpinDir < 0.5 ? -1.0 : 1.0;
            double angleRad = tiltRad + spinDir * (Math.PI * 2.0) * d.SpinHz * ageSec;

            ColorRgba tint = new ColorRgba(pickedColor.Red, pickedColor.Green, pickedColor.Blue, alpha);
            stars.Add(new VectorVisual(d.SparkleVector, BuildStarTransform(starPos, size, angleRad), tint));
        }

        output.DrawPaths(StarsOutputId, stars.ToImmutable(), d.Layer);
    }

    private static HeadReference ResolveHeadReference(
        ImmutableArray<CursorGeometry> cursors,
        ImmutableArray<FrameToken> presentedTokens,
        ImmutableArray<TokenGeometrySample> tokens,
        LineGeometrySample lineGeom,
        BindingId bindingId,
        CursorTrackId trackId,
        long now)
    {
        if (!cursors.IsDefaultOrEmpty)
        {
            CursorGeometry? matched = null;
            for (int i = 0; i < cursors.Length; i++)
            {
                var c = cursors[i];
                if (c.Identity.BindingId != bindingId)
                    continue;
                if (c.TrackId == trackId)
                {
                    matched = c;
                    break;
                }
                matched ??= c;
            }

            if (matched is not null)
            {
                var head = matched.At(CursorAnchorMode.AdvanceCenter);
                return new HeadReference(true, head.Position, NormalizeOrDefault(head.Direction));
            }
        }

        FrameToken? lastToken = null;
        for (int i = 0; i < presentedTokens.Length; i++)
        {
            var token = presentedTokens[i];
            if (token.BindingId != bindingId)
                continue;
            var perf = token.Performance;
            if (perf.IsSung || perf.IsCurrent || (perf.TimeRange is { IsValid: true } tr && tr.StartUs <= now))
            {
                lastToken = token;
            }
        }

        if (lastToken is not null)
        {
            var tokenGeom = FindTokenGeometry(tokens, lastToken.WithRuby);
            if (tokenGeom is not null && tokenGeom.ProgressPath is { } tokenPath)
            {
                double progress = lastToken.Performance.IsSung
                    ? 1.0
                    : Math.Clamp(lastToken.Performance.Progress ?? 0.0, 0.0, 1.0);
                Point2 localPt = tokenPath.At(progress, CursorAnchorMode.AdvanceCenter);
                Point2 worldPt = tokenGeom.BaseGeometry.MapPoint(localPt);
                Vector2 dir = NormalizeOrDefault(tokenGeom.BaseGeometry.MapVectorAt(localPt, new Vector2(1.0, 0.0)));
                return new HeadReference(true, worldPt, dir);
            }
        }

        Point2 localEnd = lineGeom.ProgressPath.At(1.0, CursorAnchorMode.AdvanceCenter);
        Point2 worldEnd = lineGeom.BaseGeometry.MapPoint(localEnd);
        Vector2 endDir = NormalizeOrDefault(lineGeom.BaseGeometry.MapVectorAt(localEnd, new Vector2(1.0, 0.0)));
        return new HeadReference(true, worldEnd, endDir);
    }

    private static GlyphVerticalSpan ComputeVerticalSpan(GeometrySample geometry)
    {
        Rect2 adv = geometry.AdvanceBounds;
        Rect2 ink = geometry.InkBounds is { Height: > 1.0 } inkBox ? inkBox : adv;

        double top = ink.Top;
        double bottom = ink.Bottom;
        double minHeight = Math.Max(8.0, adv.Height * 0.65);
        double height = bottom - top;
        if (height < minHeight)
        {
            double mid = 0.5 * (top + bottom);
            top = mid - 0.5 * minHeight;
            bottom = mid + 0.5 * minHeight;
        }

        double centerY = adv.Center.Y;
        return new GlyphVerticalSpan(top - centerY, bottom - centerY);
    }

    private static ScaledSpan ScaleAroundMid(Vector2 top, Vector2 bottom, double scale)
    {
        double midX = 0.5 * (top.X + bottom.X);
        double midY = 0.5 * (top.Y + bottom.Y);
        double halfX = 0.5 * (bottom.X - top.X) * scale;
        double halfY = 0.5 * (bottom.Y - top.Y) * scale;
        return new ScaledSpan(
            new Vector2(midX - halfX, midY - halfY),
            new Vector2(midX + halfX, midY + halfY));
    }

    private static LineGeometrySample? FindLineGeometry(ImmutableArray<LineGeometrySample> lines, BindingId bindingId)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Target.BindingId == bindingId)
                return lines[i];
        }
        return null;
    }

    private static TokenGeometrySample? FindTokenGeometry(ImmutableArray<TokenGeometrySample> tokens, TokenFamilyTarget target)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].WithRuby == target)
                return tokens[i];
        }
        return null;
    }

    private static Vector2 NormalizeOrDefault(Vector2 v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len <= 1e-6)
            return new Vector2(1.0, 0.0);
        return new Vector2(v.X / len, v.Y / len);
    }

    private static ulong ParticleSeed(ParticleView particle)
    {
        ulong birth = unchecked((ulong)particle.BirthUs);
        if (particle.Id.Origin is ParticleOrigin.Continuous cont)
            return birth ^ (cont.SequenceStartingAt1 * 0x9E3779B97F4A7C15UL);
        return birth;
    }

    private static Affine2 BuildStarTransform(Point2 center, double size, double angleRad)
    {
        double a = Math.Cos(angleRad) * size;
        double b = Math.Sin(angleRad) * size;
        double c = -b;
        double d = a;
        return new Affine2(a, b, c, d, center.X - (a + c) / 2.0, center.Y - (b + d) / 2.0);
    }
}
