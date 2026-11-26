using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct WindTokenOrder(int Rank, int Count);

public readonly record struct WindSchedule(long CharUs, long StaggerUs);

public readonly record struct WindPointVel(Point2 Position, Vector2 Velocity);

public sealed record WindParticleData(
    GraphHandle WindGraph,
    GraphTransformValues CanvasFitTransform,
    SpriteHandle GrainSmall,
    SpriteHandle GrainMedium,
    SpriteHandle GrainLarge,
    long EnterCharUs,
    long EnterStaggerUs,
    double EnterReachPx,
    long ExitUs,
    long ParticleLifeUs,
    double ExitDirX,
    double WindSpeedPx,
    double WindLiftPx,
    double TurbulencePx,
    double GrainSizePx,
    double ParticleSizePx,
    int ParticlesPerToken,
    double WindRibbonOpacity,
    ColorRgba ParticleColor,
    ColorRgba AccentColor,
    bool DissolveGlow,
    ImmutableDictionary<LineId, long> LineStarts,
    ImmutableDictionary<TokenId, ulong> TokenSeeds,
    ImmutableHashSet<TokenId> BurstHandledTokens) : PreparedEffect;

/// <summary>风粒子聚散出入场：行入场时风流与自选色粒子从左刮入聚合成清晰句段；已唱完的词素逐字在风中碎裂成颗粒与流光风丝向后飘散。</summary>
public sealed class WindParticleTransition : IPureKaraokeEffect<WindParticleData>, IPureVisualEffect<WindParticleData>
{
    private static readonly EmitterId ExitBurstEmitterId = new("exit-dust-burst");
    private static readonly OutputId StreamsOutputId = new("wind-streams");
    private static readonly OutputId ShardsOutputId = new("wind-shards");
    private static readonly OutputId MotesOutputId = new("wind-motes");

    public WindParticleData Declare(DeclareContext source)
    {
        var p = source.Parameters;

        long enterCharUs = p.GetDuration("enterCharUs");
        long enterStaggerUs = p.GetDuration("enterStaggerUs");
        double enterReachPx = p.GetNumber("enterReachPx");
        long exitUs = p.GetDuration("exitUs");
        long particleLifeUs = p.GetDuration("particleLifeUs");
        double exitDirX = string.Equals(p.GetEnum("exitWindDirection"), "right", StringComparison.Ordinal) ? 1.0 : -1.0;
        double windSpeedPx = p.GetNumber("windSpeedPx");
        double windLiftPx = p.GetNumber("windLiftPx");
        double turbulencePx = p.GetNumber("turbulencePx");
        double grainSizePx = p.GetNumber("grainSizePx");
        double particleSizePx = p.GetNumber("particleSizePx");
        int particlesPerToken = (int)p.GetInteger("particlesPerToken");
        double windRibbonOpacity = p.GetNumber("windRibbonOpacity");
        ColorRgba particleColor = p.GetColor("particleColor");
        ColorRgba accentColor = p.GetColor("accentColor");
        bool dissolveGlow = p.GetBoolean("dissolveGlow");

        long endUs = Math.Max(1L, source.ProjectDurationUs ?? 1L);
        var lineStarts = ImmutableDictionary.CreateBuilder<LineId, long>();
        var lines = source.Content.Lines;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TimeRange is { IsValid: true } range)
            {
                lineStarts[lines[i].Id] = range.StartUs;
                endUs = Math.Max(endUs, range.EndUs);
            }
        }

        var tokenSeeds = ImmutableDictionary.CreateBuilder<TokenId, ulong>();
        var tokens = source.Content.Tokens;
        for (int i = 0; i < tokens.Length; i++)
        {
            tokenSeeds[tokens[i].Id] = (ulong)(i + 1);
            if (tokens[i].TimeRange is { IsValid: true } range)
                endUs = Math.Max(endUs, range.EndUs);
        }

        var fullRange = new TimeRange(0, endUs + 2_000_000L);
        var presentationLoads = source.Indexes.Presentations.GetRange(fullRange, new SourceFilter.Project());

        // 为每个在可见装载区间内唱完的词素注册 TokenCompleted 爆发；行尾末字若恰好落在 HideTimeUs 边界，
        // 则由 EvaluateVisuals 在 1.8s 的 ExitReplica 窗口内解析接续飘散，避免边界缺少装载锚点。
        var selector = new TimelineSelector(TimelineEventKind.TokenCompleted, new TimelineScope.Project());
        var rows = ImmutableArray.CreateBuilder<BurstEventRow>();
        var burstHandled = ImmutableHashSet.CreateBuilder<TokenId>();
        if (particlesPerToken > 0)
        {
            long burstLifeUs = Math.Clamp(particleLifeUs + (long)(exitUs * 0.45), 250_000L, 4_800_000L);
            var burstSpec = new ParticleSpec
            {
                LifetimeUs = burstLifeUs,
                SizePx = Math.Max(2.0, particleSizePx),
                Color = particleColor,
                SpreadPx = 24.0,
                TrailDurationUs = 0,
                Velocity = Vector2.Zero,
                Rendering = ParticleRenderMode.Custom,
                Layer = OverlayLayer.AboveText,
                SeedStreamKey = "wind-exit-dust",
                Motion = ParticleMotion.ConstantVelocity,
            };

            int evCount = source.Timeline.Count(selector);
            for (int i = 0; i < evCount; i++)
            {
                if (source.Timeline.Nth(selector, i) is TokenCompleted ev &&
                    HasActivePresentationLoad(presentationLoads, ev.LineId, ev.TimeUs))
                {
                    rows.Add(new BurstEventRow(
                        ev.EventId,
                        particlesPerToken,
                        new BurstAnchor.SourceToken(ev.TokenId),
                        burstSpec));
                    burstHandled.Add(ev.TokenId);
                }
            }
        }

        var exitBurstPlan = new EventBurstPlan(ExitBurstEmitterId, selector, rows.ToImmutable());

        var fitTransform = new GraphTransformValues([
            new("fit", new GraphTransformValue(
                Affine2.Identity,
                new Rect2(0, 0, Math.Max(1.0, source.CanvasSize.X), Math.Max(1.0, source.CanvasSize.Y))))
        ]);

        return new WindParticleData(
            source.Resources.GetGraph("wind-token"),
            fitTransform,
            source.Resources.GetSprite("wind-grain", "small"),
            source.Resources.GetSprite("wind-grain", "medium"),
            source.Resources.GetSprite("wind-grain", "large"),
            enterCharUs,
            enterStaggerUs,
            enterReachPx,
            exitUs,
            particleLifeUs,
            exitDirX,
            windSpeedPx,
            windLiftPx,
            turbulencePx,
            grainSizePx,
            particleSizePx,
            particlesPerToken,
            windRibbonOpacity,
            particleColor,
            accentColor,
            dissolveGlow,
            lineStarts.ToImmutable(),
            tokenSeeds.ToImmutable(),
            burstHandled.ToImmutable())
        {
            Particles = new ParticleSchedule([], [exitBurstPlan]),
            VisualOutputs =
            [
                new VisualOutputDeclaration(StreamsOutputId, VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
                new VisualOutputDeclaration(ShardsOutputId, VisualPrimitiveKind.Sprites, OverlayLayer.AboveText, VisualResourceKind.SvgAtlas),
                new VisualOutputDeclaration(MotesOutputId, VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, WindParticleData data, TextOutput output)
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
            ulong seed = data.TokenSeeds.ContainsKey(token.TokenId) ? data.TokenSeeds[token.TokenId] : (ulong)(i + 1);
            double seed01 = frame.Random.GetUnitDouble("wind-token-seed", seed);

            // 1. 优先处理已唱完词素的碎裂飘散退场（含句尾 ExitReplica）
            double exitProgress = ResolveExitProgress(frame.TimeUs, token.Performance, load, data.ExitUs);
            if (exitProgress >= 0.0)
            {
                if (exitProgress >= 0.995)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                if (exitProgress <= 0.002)
                    continue;

                double easedExit = Curves.EaseOut(exitProgress);
                double opacity = Math.Clamp(1.0 - Math.Pow(exitProgress, 1.85), 0.0, 1.0);
                if (opacity <= 0.004)
                {
                    output.MultiplyOpacity(token.WithRuby, 0.0);
                    continue;
                }

                output.MultiplyOpacity(token.WithRuby, opacity);

                // 轻度顺风拉扯位移与微倾，强化字形随气流被剥离的张力
                double microDriftX = data.ExitDirX * 14.0 * easedExit * easedExit;
                double microLiftY = -data.WindLiftPx * 0.14 * easedExit * easedExit;
                double microShearX = -data.ExitDirX * 0.12 * easedExit;
                output.AppendTransform(
                    token.WithRuby,
                    BuildWindTransform(geometry.BaseGeometry.AdvanceBounds.Center, microDriftX, microLiftY, microShearX));

                if (data.DissolveGlow)
                {
                    double glowEnv = Math.Clamp(Math.Sin(exitProgress * Math.PI) * 0.72, 0.0, 1.0);
                    if (glowEnv > 0.02)
                    {
                        // 辉光半径收束在 shader 节点外扩安全范围内（3 * 7.5 = 22.5 <= 28px），杜绝透明导出出现边界切线
                        output.SetGlow(
                            token.WithRuby,
                            TextState.Both,
                            new GlowValue(data.ParticleColor, glowEnv * data.ParticleColor.Alpha, 4.5 + 3.0 * easedExit, 0.8 * glowEnv));
                    }
                }

                Rect2 canvasRect = MapTokenUnionToCanvas(geometry);
                BindWindShader(output, token.WithRuby, data, 1.0, exitProgress, seed01, canvasRect);
                continue;
            }

            // 2. 处理行显现时从左向右的风粒子聚合成句入场
            var order = ComputeHorizontalOrder(tokens, geometries, i, token.BindingId, geometry.BaseGeometry.AdvanceBounds.Center.X);
            int rank = order.Rank;
            int count = order.Count > 0 ? order.Count : 1;

            long revealUs = presLoad is not null
                ? presLoad.RevealTimeUs
                : (data.LineStarts.ContainsKey(token.LineId) ? data.LineStarts[token.LineId] : 0L);

            long activeSpanUs = presLoad?.HideTimeUs is { } hideUs && hideUs > revealUs
                ? (long)((hideUs - revealUs) * 0.80)
                : 0L;

            var schedule = ResolveSchedule(count, data.EnterCharUs, data.EnterStaggerUs, activeSpanUs);
            long enterStartUs = rank * schedule.StaggerUs;
            long enterElapsedUs = frame.TimeUs - revealUs;

            if (enterElapsedUs < enterStartUs)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            if (enterElapsedUs >= enterStartUs + schedule.CharUs)
                continue;

            double enterT = Math.Clamp((enterElapsedUs - enterStartUs) / (double)schedule.CharUs, 0.0, 1.0);
            double enterEase = Curves.EaseOut(enterT);
            double enterAlpha = Math.Clamp(Math.Pow(enterT, 0.55), 0.0, 1.0);
            if (enterAlpha <= 0.004)
            {
                output.MultiplyOpacity(token.WithRuby, 0.0);
                continue;
            }

            output.MultiplyOpacity(token.WithRuby, enterAlpha);

            double remain = 1.0 - enterEase;
            double enterShiftX = -22.0 * remain * remain;
            double enterShiftY = (seed01 - 0.5) * 10.0 * remain;
            double enterShearX = 0.16 * remain;
            output.AppendTransform(
                token.WithRuby,
                BuildWindTransform(geometry.BaseGeometry.AdvanceBounds.Center, enterShiftX, enterShiftY, enterShearX));

            if (data.DissolveGlow)
            {
                double glowEnv = Math.Clamp(Math.Sin(enterT * Math.PI) * 0.68, 0.0, 1.0);
                if (glowEnv > 0.02)
                {
                    output.SetGlow(
                        token.WithRuby,
                        TextState.Both,
                        new GlowValue(data.ParticleColor, glowEnv * data.ParticleColor.Alpha, 3.5 + 4.0 * remain, 0.8 * glowEnv));
                }
            }

            Rect2 enterRect = MapTokenUnionToCanvas(geometry);
            BindWindShader(output, token.WithRuby, data, -1.0, enterT, seed01, enterRect);
        }
    }

    public void EvaluateVisuals(in VisualFrame frame, WindParticleData d, VisualOutput output)
    {
        var streams = ImmutableArray.CreateBuilder<Trail>();
        var shards = ImmutableArray.CreateBuilder<AtlasSprite>();
        var motes = ImmutableArray.CreateBuilder<Dot>();

        long now = frame.TimeUs;
        double seconds = now / 1_000_000.0;

        // 阶段 A：入场风流与从左刮入聚合成句的粒子
        EmitEnterWindVisuals(frame, d, now, seconds, streams, shards, motes);

        // 阶段 B：已唱完词素碎裂后沿风向飘散的粒子与风丝（由 TokenCompleted 爆发托管，行退场后继续自然飘散）
        EmitExitBurstVisuals(frame, d, now, seconds, streams, shards, motes);

        // 阶段 B2：行尾末字（恰好在 HideTimeUs 结束）在 ExitReplica 窗口内以同款风场解析飘散，保证整句末字同样碎成粒子向后飘散
        EmitBoundaryExitParticles(frame, d, now, seconds, streams, shards, motes);

        // 阶段 C：为正在碎裂的词素叠加局部气流风带，强化风动层次
        EmitActiveExitWindRibbons(frame, d, now, seconds, streams);

        output.DrawTrails(StreamsOutputId, streams.ToImmutable(), OverlayLayer.AboveText);
        output.DrawAtlasSprites(ShardsOutputId, shards.ToImmutable(), OverlayLayer.AboveText);
        output.DrawDots(MotesOutputId, motes.ToImmutable(), OverlayLayer.AboveText);
    }

    private static void EmitEnterWindVisuals(
        in VisualFrame frame,
        WindParticleData d,
        long now,
        double seconds,
        ImmutableArray<Trail>.Builder streams,
        ImmutableArray<AtlasSprite>.Builder shards,
        ImmutableArray<Dot>.Builder motes)
    {
        var lines = frame.FinalGeometry.Presentations.LinesInBinding;
        var tokens = frame.FinalGeometry.Presentations.TokensInBinding;
        var tokenGeoms = frame.FinalGeometry.Tokens;
        if (lines.Length == 0 || tokens.Length == 0)
            return;

        for (int l = 0; l < lines.Length; l++)
        {
            var line = lines[l];
            var presLoad = frame.Indexes.Presentations.Get(line.BindingId);
            long revealUs = presLoad is not null
                ? presLoad.RevealTimeUs
                : (d.LineStarts.ContainsKey(line.LineId) ? d.LineStarts[line.LineId] : 0L);

            long enterElapsedUs = now - revealUs;
            if (enterElapsedUs < 0)
                continue;

            int lineTokenCount = 0;
            bool hasLineBox = false;
            Rect2 lineBox = new(0, 0, 0, 0);

            for (int t = 0; t < tokens.Length; t++)
            {
                if (tokens[t].BindingId != line.BindingId)
                    continue;
                var tg = FindGeometry(tokenGeoms, tokens[t].WithRuby);
                if (tg is null)
                    continue;
                lineTokenCount++;
                Rect2 tb = MapTokenUnionToCanvas(tg);
                lineBox = hasLineBox ? Union(lineBox, tb) : tb;
                hasLineBox = true;
            }

            if (lineTokenCount == 0 || !hasLineBox)
                continue;

            long activeSpanUs = presLoad?.HideTimeUs is { } hideUs && hideUs > revealUs
                ? (long)((hideUs - revealUs) * 0.80)
                : 0L;

            var schedule = ResolveSchedule(lineTokenCount, d.EnterCharUs, d.EnterStaggerUs, activeSpanUs);
            long totalEnterUs = schedule.CharUs + Math.Max(0, lineTokenCount - 1) * schedule.StaggerUs;
            if (enterElapsedUs > totalEnterUs + 140_000L)
                continue;

            double lineEnterU = Math.Clamp(enterElapsedUs / (double)Math.Max(1L, totalEnterUs), 0.0, 1.0);
            double gustEnvelope = Math.Clamp(Math.Sin(lineEnterU * Math.PI), 0.0, 1.0);

            // A1. 从左侧横扫整行的丝带风流（一眼传达风从左刮入的流场动势）
            if (d.WindRibbonOpacity > 0.01 && gustEnvelope > 0.02)
            {
                ulong lineSeed = HashText(line.BindingId.Value, 0x517CC1B727220A95UL);
                int ribbonCount = 7;
                double frontX = lineBox.Left + lineBox.Width * Math.Clamp(lineEnterU * 1.18, 0.0, 1.12);
                double ribbonSpan = Math.Max(160.0, d.EnterReachPx * 1.15);

                for (int r = 0; r < ribbonCount; r++)
                {
                    ulong rSeed = lineSeed ^ ((ulong)(r + 1) * 0x9E3779B97F4A7C15UL);
                    double rY = frame.Random.GetUnitDouble("enter-rib-y", rSeed);
                    double rPhase = frame.Random.GetUnitDouble("enter-rib-p", rSeed);
                    double rLen = frame.Random.GetUnitDouble("enter-rib-l", rSeed);
                    double rCol = frame.Random.GetUnitDouble("enter-rib-c", rSeed);

                    double headX = frontX + (rLen - 0.45) * 90.0;
                    double length = ribbonSpan * (0.65 + 0.55 * rLen);
                    double baseY = lineBox.Top - 16.0 + (lineBox.Height + 32.0) * rY;
                    double waveAmp = (8.0 + d.TurbulencePx * 0.48) * (0.55 + 0.55 * rPhase);
                    double baseAlpha = d.WindRibbonOpacity * gustEnvelope * (0.42 + 0.36 * rLen);

                    const int segs = 22;
                    var pts = ImmutableArray.CreateBuilder<TrailPoint>(segs + 1);
                    for (int s = 0; s <= segs; s++)
                    {
                        double f = s / (double)segs;
                        double x = headX - length * (1.0 - f);
                        double y = baseY + waveAmp * Math.Sin(f * Math.PI * 2.2 - seconds * 7.5 + rPhase * 6.2831853);
                        double bell = Math.Sin(f * Math.PI);
                        double w = (1.8 + 3.0 * rLen) * bell * (0.45 + 0.55 * gustEnvelope);
                        ColorRgba col = LerpColor(d.ParticleColor, d.AccentColor, rCol * 0.55 + f * 0.35, Math.Clamp(baseAlpha * bell * 1.15, 0.0, 1.0));
                        pts.Add(new TrailPoint(new Point2(x, y), w, col));
                    }
                    streams.Add(new Trail(pts.MoveToImmutable()));
                }
            }

            // A2. 每个正在凝聚的字对应的汇聚粒子与速度风丝
            int enterParticles = Math.Clamp((int)Math.Round(d.ParticlesPerToken * 0.72), 6, 36);
            for (int t = 0; t < tokens.Length; t++)
            {
                var token = tokens[t];
                if (token.BindingId != line.BindingId)
                    continue;
                var tg = FindGeometry(tokenGeoms, token.WithRuby);
                if (tg is null)
                    continue;

                var order = ComputeHorizontalOrder(tokens, tokenGeoms, t, token.BindingId, tg.BaseGeometry.AdvanceBounds.Center.X);
                long tokenStartUs = order.Rank * schedule.StaggerUs;
                double rawT = (enterElapsedUs - tokenStartUs) / (double)Math.Max(1L, schedule.CharUs);
                if (rawT < -0.42 || rawT > 1.02)
                    continue;

                Rect2 box = MapTokenUnionToCanvas(tg);
                ulong tokSeed = d.TokenSeeds.ContainsKey(token.TokenId) ? d.TokenSeeds[token.TokenId] : (ulong)(t + 1);

                for (int k = 0; k < enterParticles; k++)
                {
                    ulong pSeed = unchecked(tokSeed * 1315423911UL + (ulong)(k + 1) * 0xBF58476D1CE4E5B9UL);
                    double rx = frame.Random.GetUnitDouble("enter-px", pSeed);
                    double ry = frame.Random.GetUnitDouble("enter-py", pSeed);
                    double rReach = frame.Random.GetUnitDouble("enter-reach", pSeed);
                    double rSize = frame.Random.GetUnitDouble("enter-size", pSeed);
                    double rPhase = frame.Random.GetUnitDouble("enter-phase", pSeed);
                    double rCol = frame.Random.GetUnitDouble("enter-col", pSeed);
                    double rDelay = frame.Random.GetUnitDouble("enter-delay", pSeed);

                    // 左侧字符先到位，右侧粒子稍晚收敛，保证汇聚有层次感
                    double startShift = -0.35 + (rx * 0.32 + rDelay * 0.28);
                    double span = 0.74 + 0.24 * rReach;
                    double u = (rawT - startShift) / span;
                    if (u <= 0.0 || u >= 1.0)
                        continue;

                    double ease = Curves.EaseOut(u);
                    double remain = 1.0 - ease;

                    Point2 target = new(
                        box.Left + box.Width * (0.10 + 0.80 * rx),
                        box.Top + box.Height * (0.10 + 0.80 * ry));

                    double reach = d.EnterReachPx * (0.58 + 0.82 * rReach);
                    double swirlY = (d.TurbulencePx * 0.85 + 14.0) * (rPhase - 0.5) * 2.0;

                    Point2 pos = EvalEnterParticlePos(target, reach, swirlY, d.WindLiftPx, rPhase, remain);
                    Point2 prev = EvalEnterParticlePos(target, reach, swirlY, d.WindLiftPx, rPhase, Math.Min(1.0, remain + 0.09));

                    double fadeIn = Math.Clamp(u / 0.16, 0.0, 1.0);
                    double fadeOut = 1.0 - Curves.EaseOut(Math.Clamp((u - 0.68) / 0.32, 0.0, 1.0));
                    double alpha = Math.Clamp(fadeIn * fadeOut * d.ParticleColor.Alpha * 0.95, 0.0, 1.0);
                    if (alpha <= 0.015)
                        continue;

                    double vx = pos.X - prev.X;
                    double vy = pos.Y - prev.Y;
                    double angle = Math.Atan2(vy, vx);
                    double speedStretch = Math.Clamp(Math.Sqrt(vx * vx + vy * vy) / 14.0, 1.0, 2.8);

                    ColorRgba col = LerpColor(d.ParticleColor, d.AccentColor, rCol, alpha);
                    double pSize = Math.Max(2.5, d.ParticleSizePx * (0.48 + 0.78 * rSize) * (0.65 + 0.45 * remain));

                    if (k % 3 == 0)
                    {
                        motes.Add(new Dot(pos, Math.Max(1.1, pSize * 0.26), col));
                    }
                    else
                    {
                        double w = pSize * (0.75 + 0.55 * speedStretch);
                        double h = pSize * 0.62;
                        SpriteHandle handle = PickVariant(Math.Max(w, h), d.GrainSmall, d.GrainMedium, d.GrainLarge);
                        shards.Add(new AtlasSprite(handle, BuildRectTransform(pos, w, h, angle), col));
                    }

                    // 为部分高速汇聚粒子附加轻盈速度拖尾
                    if (d.WindRibbonOpacity > 0.02 && (k & 3) == 0 && remain > 0.12)
                    {
                        Point2 tail1 = EvalEnterParticlePos(target, reach, swirlY, d.WindLiftPx, rPhase, Math.Min(1.0, remain + 0.08));
                        Point2 tail2 = EvalEnterParticlePos(target, reach, swirlY, d.WindLiftPx, rPhase, Math.Min(1.0, remain + 0.18));
                        double tw = Math.Max(1.1, pSize * 0.24);
                        ColorRgba headC = new(col.Red, col.Green, col.Blue, alpha * 0.72 * d.WindRibbonOpacity);
                        ColorRgba midC = new(col.Red, col.Green, col.Blue, alpha * 0.34 * d.WindRibbonOpacity);
                        ColorRgba endC = new(col.Red, col.Green, col.Blue, 0.0);
                        streams.Add(new Trail([
                            new TrailPoint(tail2, 0.0, endC),
                            new TrailPoint(tail1, tw * 0.55, midC),
                            new TrailPoint(pos, tw, headC),
                        ]));
                    }
                }
            }
        }
    }

    private static void EmitExitBurstVisuals(
        in VisualFrame frame,
        WindParticleData d,
        long now,
        double seconds,
        ImmutableArray<Trail>.Builder streams,
        ImmutableArray<AtlasSprite>.Builder shards,
        ImmutableArray<Dot>.Builder motes)
    {
        var alive = frame.Particles.Alive;
        if (alive.Length == 0)
            return;

        double exitSec = Math.Max(0.15, d.ExitUs / 1_000_000.0);
        double flyLifeSec = Math.Max(0.20, d.ParticleLifeUs / 1_000_000.0);

        for (int i = 0; i < alive.Length; i++)
        {
            var particle = alive[i];
            if (particle.Id.EmitterId != ExitBurstEmitterId)
                continue;

            long ageUs = now - particle.BirthUs;
            if (ageUs < 0)
                continue;

            ulong seed = ParticleSeed(particle);
            double rEdge = frame.Random.GetUnitDouble("wind-edge", seed);
            double rJx = frame.Random.GetUnitDouble("wind-jx", seed);
            double rJy = frame.Random.GetUnitDouble("wind-jy", seed);
            double rSpeed = frame.Random.GetUnitDouble("wind-spd", seed);
            double rAccel = frame.Random.GetUnitDouble("wind-acc", seed);
            double rLift = frame.Random.GetUnitDouble("wind-lift", seed);
            double rTurb = frame.Random.GetUnitDouble("wind-turb", seed);
            double rFreq = frame.Random.GetUnitDouble("wind-freq", seed);
            double rPhase = frame.Random.GetUnitDouble("wind-phase", seed);
            double rSize = frame.Random.GetUnitDouble("wind-size", seed);
            double rCol = frame.Random.GetUnitDouble("wind-col", seed);
            double rKind = frame.Random.GetUnitDouble("wind-kind", seed);

            // 粒子剥离时间与着色器风蚀推进方向同步：顺风边先碎，逆风边稍晚碎
            double delaySec = exitSec * 0.42 * rEdge;
            double ageSec = ageUs / 1_000_000.0;
            double flySec = ageSec - delaySec;
            if (flySec <= 0.0 || flySec >= flyLifeSec)
                continue;

            double fly01 = Math.Clamp(flySec / flyLifeSec, 0.0, 1.0);

            // 以主机固化的 BirthPosition 为基准叠加细粒度字符区分布（跨行隐藏零跳变）
            double edgeSign = d.ExitDirX < 0.0 ? (rEdge - 0.5) : (0.5 - rEdge);
            Point2 origin = new(
                particle.Birth.BirthPosition.X + edgeSign * 26.0 + (rJx - 0.5) * 16.0,
                particle.Birth.BirthPosition.Y + (rJy - 0.5) * 44.0);

            EmitSingleExitParticle(
                origin, flySec, fly01, seconds, d,
                rSpeed, rAccel, rLift, rTurb, rFreq, rPhase, rSize, rCol, rKind,
                streams, shards, motes);
        }
    }

    private static void EmitBoundaryExitParticles(
        in VisualFrame frame,
        WindParticleData d,
        long now,
        double seconds,
        ImmutableArray<Trail>.Builder streams,
        ImmutableArray<AtlasSprite>.Builder shards,
        ImmutableArray<Dot>.Builder motes)
    {
        if (d.ParticlesPerToken <= 0)
            return;

        var tokens = frame.FinalGeometry.Presentations.TokensInBinding;
        var tokenGeoms = frame.FinalGeometry.Tokens;
        if (tokens.Length == 0 || tokenGeoms.Length == 0)
            return;

        double exitSec = Math.Max(0.15, d.ExitUs / 1_000_000.0);
        double flyLifeSec = Math.Max(0.20, d.ParticleLifeUs / 1_000_000.0);

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (d.BurstHandledTokens.Contains(token.TokenId))
                continue;
            if (token.Performance.ProgressSource != ProgressSource.Token || token.Performance.TimeRange is not { IsValid: true } range)
                continue;

            long ageUs = now - range.EndUs;
            if (ageUs <= 0)
                continue;

            var tg = FindGeometry(tokenGeoms, token.WithRuby);
            if (tg is null)
                continue;

            // 扣除 Evaluate 阶段施加的微位移，使出生锚点在退场全过程严格静止
            double exitProg = d.ExitUs <= 0 ? 1.0 : Math.Clamp(ageUs / (double)d.ExitUs, 0.0, 1.0);
            double undoX = 0.0;
            double undoY = 0.0;
            if (exitProg > 0.002 && exitProg < 0.995)
            {
                double easedExit = Curves.EaseOut(exitProg);
                undoX = d.ExitDirX * 14.0 * easedExit * easedExit;
                undoY = -d.WindLiftPx * 0.14 * easedExit * easedExit;
            }

            Rect2 box = MapTokenUnionToCanvas(tg);
            Point2 baseCenter = new(box.Center.X - undoX, box.Center.Y - undoY);
            double halfW = Math.Max(18.0, box.Width * 0.44);
            double halfH = Math.Max(22.0, box.Height * 0.44);

            ulong tokSeed = d.TokenSeeds.ContainsKey(token.TokenId) ? d.TokenSeeds[token.TokenId] : (ulong)(i + 1);
            double ageSec = ageUs / 1_000_000.0;

            for (int k = 0; k < d.ParticlesPerToken; k++)
            {
                ulong seed = unchecked((ulong)range.EndUs) ^ (tokSeed * 0x9E3779B97F4A7C15UL) ^ ((ulong)(k + 1) * 0xBF58476D1CE4E5B9UL);
                double rEdge = frame.Random.GetUnitDouble("wind-edge", seed);
                double rJx = frame.Random.GetUnitDouble("wind-jx", seed);
                double rJy = frame.Random.GetUnitDouble("wind-jy", seed);
                double rSpeed = frame.Random.GetUnitDouble("wind-spd", seed);
                double rAccel = frame.Random.GetUnitDouble("wind-acc", seed);
                double rLift = frame.Random.GetUnitDouble("wind-lift", seed);
                double rTurb = frame.Random.GetUnitDouble("wind-turb", seed);
                double rFreq = frame.Random.GetUnitDouble("wind-freq", seed);
                double rPhase = frame.Random.GetUnitDouble("wind-phase", seed);
                double rSize = frame.Random.GetUnitDouble("wind-size", seed);
                double rCol = frame.Random.GetUnitDouble("wind-col", seed);
                double rKind = frame.Random.GetUnitDouble("wind-kind", seed);

                double delaySec = exitSec * 0.42 * rEdge;
                double effectiveLife = Math.Min(flyLifeSec, Math.Max(0.28, 1.76 - delaySec));
                double flySec = ageSec - delaySec;
                if (flySec <= 0.0 || flySec >= effectiveLife)
                    continue;

                double fly01 = Math.Clamp(flySec / effectiveLife, 0.0, 1.0);
                double edgeSign = d.ExitDirX < 0.0 ? (rEdge - 0.5) : (0.5 - rEdge);
                Point2 origin = new(
                    baseCenter.X + edgeSign * halfW * 1.45 + (rJx - 0.5) * 14.0,
                    baseCenter.Y + (rJy - 0.5) * halfH * 1.85);

                EmitSingleExitParticle(
                    origin, flySec, fly01, seconds, d,
                    rSpeed, rAccel, rLift, rTurb, rFreq, rPhase, rSize, rCol, rKind,
                    streams, shards, motes);
            }
        }
    }

    private static void EmitSingleExitParticle(
        Point2 origin,
        double flySec,
        double fly01,
        double seconds,
        WindParticleData d,
        double rSpeed,
        double rAccel,
        double rLift,
        double rTurb,
        double rFreq,
        double rPhase,
        double rSize,
        double rCol,
        double rKind,
        ImmutableArray<Trail>.Builder streams,
        ImmutableArray<AtlasSprite>.Builder shards,
        ImmutableArray<Dot>.Builder motes)
    {
        var state = EvalExitParticle(origin, flySec, seconds, d, rSpeed, rAccel, rLift, rTurb, rFreq, rPhase);
        Point2 pos = state.Position;
        Vector2 vel = state.Velocity;

        double popIn = Math.Clamp(flySec / 0.055, 0.0, 1.0);
        double fadeOut = 1.0 - Curves.EaseOut(Math.Clamp((fly01 - 0.25) / 0.75, 0.0, 1.0));
        double twinkle = 0.78 + 0.22 * Math.Sin(flySec * (11.0 + 9.0 * rFreq) + rPhase * 6.2831853);
        double alpha = Math.Clamp(popIn * fadeOut * twinkle * d.ParticleColor.Alpha, 0.0, 1.0);
        if (alpha <= 0.012)
            return;

        double colorMix = Math.Clamp(rCol * 0.75 + fly01 * 0.30, 0.0, 1.0);
        ColorRgba col = LerpColor(d.ParticleColor, d.AccentColor, colorMix, alpha);
        double size = Math.Max(2.2, d.ParticleSizePx * (0.42 + 0.88 * rSize) * (1.05 - 0.45 * fly01));

        double speed = Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
        double angle = Math.Atan2(vel.Y, vel.X);

        if (rKind < 0.36)
        {
            motes.Add(new Dot(pos, Math.Max(1.0, size * 0.28), col));
        }
        else
        {
            double stretch = Math.Clamp(1.0 + speed / 340.0, 1.05, 2.35);
            double w = size * stretch;
            double h = size * (0.52 + 0.26 * rSize);
            SpriteHandle handle = PickVariant(Math.Max(w, h), d.GrainSmall, d.GrainMedium, d.GrainLarge);
            shards.Add(new AtlasSprite(handle, BuildRectTransform(pos, w, h, angle), col));
        }

        // 为高速飞散粒子绘制弯曲流线拖尾，增强疾风席卷质感
        if (d.WindRibbonOpacity > 0.02 && rKind >= 0.45 && flySec > 0.035)
        {
            double trailDt = Math.Min(flySec, 0.15 + 0.09 * rSpeed);
            const int trailSteps = 6;
            var pts = ImmutableArray.CreateBuilder<TrailPoint>(trailSteps + 1);
            double headW = Math.Max(1.2, size * 0.25);
            for (int s = 0; s <= trailSteps; s++)
            {
                double frac = s / (double)trailSteps;
                double sampleT = Math.Max(0.0, flySec - trailDt * (1.0 - frac));
                var sampleState = EvalExitParticle(origin, sampleT, seconds - (flySec - sampleT), d, rSpeed, rAccel, rLift, rTurb, rFreq, rPhase);
                double tAlpha = Math.Clamp(alpha * frac * frac * 0.85 * d.WindRibbonOpacity, 0.0, 1.0);
                ColorRgba tc = LerpColor(d.AccentColor, d.ParticleColor, frac, tAlpha);
                pts.Add(new TrailPoint(sampleState.Position, headW * frac, tc));
            }
            streams.Add(new Trail(pts.MoveToImmutable()));
        }
    }

    private static void EmitActiveExitWindRibbons(
        in VisualFrame frame,
        WindParticleData d,
        long now,
        double seconds,
        ImmutableArray<Trail>.Builder streams)
    {
        if (d.WindRibbonOpacity <= 0.01)
            return;

        var tokens = frame.FinalGeometry.Presentations.TokensInBinding;
        var tokenGeoms = frame.FinalGeometry.Tokens;
        if (tokens.Length == 0)
            return;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Performance.ProgressSource != ProgressSource.Token || token.Performance.TimeRange is not { IsValid: true } range)
                continue;

            long afterEnd = now - range.EndUs;
            if (afterEnd <= 0 || afterEnd >= d.ExitUs + 120_000L)
                continue;

            var tg = FindGeometry(tokenGeoms, token.WithRuby);
            if (tg is null)
                continue;

            double u = Math.Clamp(afterEnd / (double)Math.Max(1L, d.ExitUs), 0.0, 1.0);
            double env = Math.Sin(u * Math.PI);
            if (env <= 0.03)
                continue;

            Rect2 box = MapTokenUnionToCanvas(tg);
            ulong tokSeed = d.TokenSeeds.ContainsKey(token.TokenId) ? d.TokenSeeds[token.TokenId] : (ulong)(i + 1);

            for (int r = 0; r < 2; r++)
            {
                ulong rSeed = unchecked(tokSeed * 313UL + (ulong)(r + 1));
                double ry = frame.Random.GetUnitDouble("exit-rib-y", rSeed);
                double rPhase = frame.Random.GetUnitDouble("exit-rib-p", rSeed);
                double rLen = frame.Random.GetUnitDouble("exit-rib-l", rSeed);

                double reach = (95.0 + d.WindSpeedPx * 0.48) * (0.65 + 0.55 * rLen) * (0.45 + 0.75 * u);
                double startX = box.Center.X + d.ExitDirX * (box.Width * 0.12 + 24.0 * u);
                double startY = box.Top + box.Height * (0.20 + 0.60 * ry);
                double amp = 6.0 + d.TurbulencePx * 0.36;

                const int segs = 16;
                var pts = ImmutableArray.CreateBuilder<TrailPoint>(segs + 1);
                for (int s = 0; s <= segs; s++)
                {
                    double f = s / (double)segs;
                    double x = startX + d.ExitDirX * (reach * f);
                    double y = startY - d.WindLiftPx * 0.35 * f * u + amp * Math.Sin(f * Math.PI * 1.9 - seconds * 7.2 + rPhase * 6.2831853);
                    double bell = Math.Sin(f * Math.PI);
                    double w = (1.6 + 2.5 * rLen) * bell * env;
                    ColorRgba col = LerpColor(d.ParticleColor, d.AccentColor, f * 0.85, Math.Clamp(d.WindRibbonOpacity * env * bell * 0.72, 0.0, 1.0));
                    pts.Add(new TrailPoint(new Point2(x, y), w, col));
                }
                streams.Add(new Trail(pts.MoveToImmutable()));
            }
        }
    }

    private static Point2 EvalEnterParticlePos(
        Point2 target,
        double reachPx,
        double swirlAmpY,
        double windLiftPx,
        double phase,
        double remain)
    {
        double r2 = remain * remain;
        double x = target.X - reachPx * (0.35 * remain + 0.65 * r2);
        double wave = Math.Sin(remain * Math.PI * 2.1 + phase * 6.2831853) * swirlAmpY * remain;
        double liftArc = windLiftPx * 0.25 * r2 * (phase - 0.35);
        double y = target.Y + wave + liftArc;
        return new Point2(x, y);
    }

    private static WindPointVel EvalExitParticle(
        Point2 origin,
        double flySec,
        double globalSec,
        WindParticleData d,
        double rSpeed,
        double rAccel,
        double rLift,
        double rTurb,
        double rFreq,
        double rPhase)
    {
        double v0x = d.ExitDirX * d.WindSpeedPx * (0.42 + 0.72 * rSpeed);
        double ax = d.ExitDirX * d.WindSpeedPx * 1.25 * (0.60 + 0.75 * rAccel);
        double omega = 3.4 + 2.8 * rFreq;
        double phase = rPhase * 6.2831853;
        double gustRamp = Math.Min(1.0, flySec * 3.8);

        double gustX = d.TurbulencePx * 0.28 * Math.Sin(flySec * (omega * 1.15) + phase + globalSec * 0.4) * gustRamp;
        double x = origin.X + v0x * flySec + 0.5 * ax * flySec * flySec + gustX;

        double liftV0 = -d.WindLiftPx * (0.35 + 0.85 * rLift);
        double liftA = -d.WindLiftPx * 0.55 * (0.5 + rLift);
        double eddyAmp = d.TurbulencePx * (0.38 + 0.85 * rTurb);
        double eddyY = eddyAmp * Math.Sin(flySec * omega + phase) * gustRamp;
        double y = origin.Y + liftV0 * flySec + 0.5 * liftA * flySec * flySec + eddyY;

        double vx = v0x + ax * flySec + d.TurbulencePx * 0.28 * (omega * 1.15) * Math.Cos(flySec * (omega * 1.15) + phase) * gustRamp;
        double vy = liftV0 + liftA * flySec + eddyAmp * omega * Math.Cos(flySec * omega + phase) * gustRamp;
        return new WindPointVel(new Point2(x, y), new Vector2(vx, vy));
    }

    private static void BindWindShader(
        TextOutput output,
        TokenFamilyTarget target,
        WindParticleData data,
        double mode,
        double progress,
        double seed01,
        Rect2 canvasRect)
    {
        var uniforms = new GraphUniformValues([
            new(new ValueTuple<string, string>("shatter", "mode"), UniformValue.Float(CleanFloat(mode))),
            new(new ValueTuple<string, string>("shatter", "progress"), UniformValue.Float(CleanFloat(progress))),
            new(new ValueTuple<string, string>("shatter", "windDirX"), UniformValue.Float(CleanFloat(data.ExitDirX))),
            new(new ValueTuple<string, string>("shatter", "windLift"), UniformValue.Float(CleanFloat(data.WindLiftPx))),
            new(new ValueTuple<string, string>("shatter", "grainSizePx"), UniformValue.Float(CleanFloat(data.GrainSizePx))),
            new(new ValueTuple<string, string>("shatter", "windSeed"), UniformValue.Float(CleanFloat(seed01))),
            new(new ValueTuple<string, string>("shatter", "tokenRect"), UniformValue.Float4(
                CleanFloat(canvasRect.Left),
                CleanFloat(canvasRect.Top),
                CleanFloat(canvasRect.Right),
                CleanFloat(canvasRect.Bottom))),
            new(new ValueTuple<string, string>("shatter", "particleColor"), UniformValue.Color(data.ParticleColor)),
            new(new ValueTuple<string, string>("shatter", "accentColor"), UniformValue.Color(data.AccentColor)),
        ]);

        output.BindGraph(
            target,
            new GraphBindingValue(
                data.WindGraph,
                ParameterValues.Empty,
                uniforms,
                data.CanvasFitTransform));
    }

    private static double ResolveExitProgress(long timeUs, PerformanceSample perf, TextLineLoad? load, long exitUs)
    {
        if (perf.ProgressSource == ProgressSource.Token && perf.TimeRange is { IsValid: true } range)
        {
            long elapsedAfterEnd = timeUs - range.EndUs;
            if (elapsedAfterEnd >= 0)
            {
                return exitUs <= 0 ? 1.0 : Math.Clamp(elapsedAfterEnd / (double)exitUs, 0.0, 1.0);
            }

            if (load is { Phase: TextLinePhase.ExitReplica })
                return Math.Clamp(load.ExitProgress, 0.0, 1.0);

            return -1.0;
        }

        if (load is { Phase: TextLinePhase.ExitReplica })
            return Math.Clamp(load.ExitProgress, 0.0, 1.0);

        return -1.0;
    }

    private static WindSchedule ResolveSchedule(int count, long charUs, long staggerUs, long maxSpanUs)
    {
        long safeCharUs = Math.Max(1L, charUs);
        long safeStaggerUs = Math.Max(0L, staggerUs);
        long totalUs = safeCharUs + Math.Max(0, count - 1) * safeStaggerUs;
        if (maxSpanUs <= 0 || totalUs <= maxSpanUs)
            return new WindSchedule(safeCharUs, safeStaggerUs);

        double ratio = maxSpanUs / (double)totalUs;
        return new WindSchedule(
            Math.Max(1L, (long)Math.Floor(safeCharUs * ratio)),
            Math.Max(0L, (long)Math.Floor(safeStaggerUs * ratio)));
    }

    private static WindTokenOrder ComputeHorizontalOrder(
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
                rank++;
        }
        return new WindTokenOrder(rank, count);
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

    private static bool HasActivePresentationLoad(ImmutableArray<SourcePresentationLoad> loads, LineId lineId, long timeUs)
    {
        for (int i = 0; i < loads.Length; i++)
        {
            if (loads[i].Binding.LineId == lineId && loads[i].Contains(timeUs))
                return true;
        }
        return false;
    }

    private static Rect2 MapTokenUnionToCanvas(TokenGeometrySample geom)
    {
        Rect2 box = MapGeometryBox(geom.FamilyGeometry);
        if (box.Width < 1.0 || box.Height < 1.0)
            box = MapGeometryBox(geom.BaseGeometry);
        return box;
    }

    private static Rect2 MapGeometryBox(GeometrySample geometry)
    {
        Rect2 b = geometry.InkBounds is { Width: > 1.0, Height: > 1.0 } ink ? ink : geometry.AdvanceBounds;
        Point2 p0 = geometry.MapPoint(new Point2(b.Left, b.Top));
        Point2 p1 = geometry.MapPoint(new Point2(b.Right, b.Top));
        Point2 p2 = geometry.MapPoint(new Point2(b.Right, b.Bottom));
        Point2 p3 = geometry.MapPoint(new Point2(b.Left, b.Bottom));
        double left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        double top = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        double right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        double bottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new Rect2(left, top, Math.Max(left + 1.0, right), Math.Max(top + 1.0, bottom));
    }

    private static Rect2 Union(Rect2 a, Rect2 b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static Affine2 BuildWindTransform(Point2 pivot, double dx, double dy, double shearX)
    {
        // 行列式恒为 1，始终可逆
        const double a = 1.0;
        const double b = 0.0;
        double c = Math.Clamp(shearX, -0.45, 0.45);
        const double d = 1.0;
        double tx = pivot.X + dx - (a * pivot.X + c * pivot.Y);
        double ty = pivot.Y + dy - (b * pivot.X + d * pivot.Y);
        return new Affine2(a, b, c, d, tx, ty);
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

    private static SpriteHandle PickVariant(double pixelSize, SpriteHandle small, SpriteHandle medium, SpriteHandle large)
        => pixelSize < 20.0 ? small : (pixelSize < 46.0 ? medium : large);

    private static ulong ParticleSeed(ParticleView particle)
    {
        ulong seed = unchecked((ulong)particle.BirthUs);
        if (particle.Id.Origin is ParticleOrigin.Burst burst)
            seed ^= ((ulong)(burst.ParticleOrdinal + 1) * 0xBF58476D1CE4E5B9UL) ^ ((ulong)burst.PlanOrdinal * 0x94D049BB133111EBUL);
        return seed;
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

    private static double CleanFloat(double v) => double.IsFinite(v) ? v : 0.0;
}
