using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct ChromeEnvSample(
    ColorRgba SkyColor,
    ColorRgba GroundColor,
    double BgLuma,
    double VarianceNorm);

public sealed record AdaptiveChromeFoilData(
    MaterialValue ChromeMaterial,
    MaterialHandle ChromeMaterialHandle,
    MediaQueryId EnvQuery,
    Vector2 CanvasSize,
    double EnvBlend,
    ColorRgba FallbackSkyColor,
    ColorRgba FallbackGroundColor,
    ImmutableArray<StrokeValue> AdaptiveStrokes) : PreparedEffect;

/// <summary>环境光自适应液态镀铬：采样字族上下方背景媒体时域统计，逐字注入天地反射色与流光状态驱动液态金属箔着色。</summary>
public sealed class AdaptiveChromeFoil : IPureKaraokeEffect<AdaptiveChromeFoilData>
{
    private const int StrokeSteps = 8;
    private const string MaterialNodeId = "chrome";

    public AdaptiveChromeFoilData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        double liquidWarp = p.GetNumber("liquidWarp");
        double metallicContrast = p.GetNumber("metallicContrast");
        double sheenSpeed = p.GetNumber("sheenSpeed");
        double envBlend = p.GetNumber("envBlend");
        ColorRgba fallbackSky = p.GetColor("fallbackSkyColor");
        ColorRgba fallbackGround = p.GetColor("fallbackGroundColor");

        MaterialHandle materialHandle = source.Resources.GetMaterial("chrome-material");
        var materialParams = new ParameterValues([
            new("liquidWarp", new ParameterValue.Number(liquidWarp)),
            new("metallicContrast", new ParameterValue.Number(metallicContrast)),
            new("sheenSpeed", new ParameterValue.Number(sheenSpeed)),
            new("fallbackSkyColor", new ParameterValue.Color(fallbackSky)),
            new("fallbackGroundColor", new ParameterValue.Color(fallbackGround)),
        ]);
        MaterialValue chromeMaterial = MaterialValue.PackageExport(materialHandle, materialParams);

        // 预建 9 档背景亮度自适应金属包边描边（暗底亮银灰、亮底深冷枪灰）
        var darkRim = new ColorRgba(0.08, 0.11, 0.16, 1.0);
        var brightRim = new ColorRgba(0.84, 0.90, 0.98, 1.0);
        var strokesBuilder = ImmutableArray.CreateBuilder<StrokeValue>(StrokeSteps + 1);
        for (int i = 0; i <= StrokeSteps; i++)
        {
            double brightBg = i / (double)StrokeSteps;
            double darkBgFactor = 1.0 - brightBg;
            ColorRgba rim = LerpColor(darkRim, brightRim, darkBgFactor, 1.0);
            strokesBuilder.Add(new StrokeValue(3.2, MaterialValue.Solid(rim), StrokeJoin.Round));
        }

        return new AdaptiveChromeFoilData(
            chromeMaterial,
            materialHandle,
            source.Media.GetId("env"),
            source.CanvasSize,
            envBlend,
            fallbackSky,
            fallbackGround,
            strokesBuilder.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, AdaptiveChromeFoilData data, TextOutput output)
    {
        var tokens = frame.Presentations.TokensInBinding;
        if (tokens.Length == 0)
            return;

        var geometries = frame.Geometry;
        bool hasMedia = frame.OutputMode == OutputMode.Opaque
            && frame.Media.Catalog.Input?.State == MediaInputState.Available;

        ChromeEnvSample fallbackEnv = new(
            data.FallbackSkyColor,
            data.FallbackGroundColor,
            ComputePerceptualLuma(data.FallbackSkyColor, data.FallbackGroundColor),
            0.28);

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var geometry = FindTokenGeometry(geometries, token.WithRuby);
            if (geometry is null || !geometry.BaseGeometry.CanTransformIndependently)
                continue;

            ChromeEnvSample env = hasMedia
                ? SampleTokenEnvironment(frame.Media, data, geometry, frame.TimeUs, fallbackEnv)
                : fallbackEnv;

            bool isCurrent = token.Performance.IsCurrent;
            bool isSung = token.Performance.IsSung;
            double activeSheen = isCurrent && token.Performance.Progress is { } prog
                ? 0.35 + 0.65 * Math.Clamp(prog, 0.0, 1.0)
                : (isSung ? 0.62 : 0.16);

            // 自适应高反差描边保障镀铬文字在任何底色上清晰可读
            int strokeIdx = Math.Clamp((int)Math.Round(env.BgLuma * StrokeSteps), 0, StrokeSteps);
            output.SetStrokes(token.WithRuby, TextState.Both, data.AdaptiveStrokes[strokeIdx]);

            UniformValue skyUniform = UniformValue.Color(env.SkyColor);

            if (isCurrent)
            {
                // 走字瞬间跨扫字分界同时显示 UnsungFill 与 SungFill，双槽皆注入液态镀铬材质
                output.SetFill(token.WithRuby, TextState.Both, data.ChromeMaterial);
                WriteTokenChromeUniforms(output, token.WithRuby, MaterialSlot.UnsungFill, data.ChromeMaterialHandle, skyUniform, env, 0.16);
                WriteTokenChromeUniforms(output, token.WithRuby, MaterialSlot.SungFill, data.ChromeMaterialHandle, skyUniform, env, activeSheen);
            }
            else if (isSung)
            {
                // 已唱字仅需写入 SungFill（每字仅 2 次 uniform 写入）
                output.SetFill(token.WithRuby, TextState.Sung, data.ChromeMaterial);
                WriteTokenChromeUniforms(output, token.WithRuby, MaterialSlot.SungFill, data.ChromeMaterialHandle, skyUniform, env, activeSheen);
            }
            else
            {
                // 未唱字仅需写入 UnsungFill（每字仅 2 次 uniform 写入）
                output.SetFill(token.WithRuby, TextState.Unsung, data.ChromeMaterial);
                WriteTokenChromeUniforms(output, token.WithRuby, MaterialSlot.UnsungFill, data.ChromeMaterialHandle, skyUniform, env, activeSheen);
            }
        }
    }

    private static void WriteTokenChromeUniforms(
        TextOutput output,
        TokenFamilyTarget target,
        MaterialSlot slot,
        MaterialHandle material,
        UniformValue skyUniform,
        ChromeEnvSample env,
        double sheenProgress)
    {
        double packedState = PackChromeState(env.BgLuma, env.VarianceNorm, sheenProgress);
        UniformValue groundAndState = UniformValue.Float4(
            CleanFloat(env.GroundColor.Red),
            CleanFloat(env.GroundColor.Green),
            CleanFloat(env.GroundColor.Blue),
            CleanFloat(packedState));

        output.SetMaterialUniform(target, slot, material, MaterialNodeId, "uSkyColor", skyUniform);
        output.SetMaterialUniform(target, slot, material, MaterialNodeId, "uGroundAndState", groundAndState);
    }

    private static ChromeEnvSample SampleTokenEnvironment(
        MediaFeatures media,
        AdaptiveChromeFoilData data,
        TokenGeometrySample geometry,
        long timeUs,
        ChromeEnvSample fallbackEnv)
    {
        Rect2 box = MapTokenUnionToCanvas(geometry);
        double padX = Math.Max(20.0, box.Width * 0.25);
        double probeH = Math.Max(48.0, box.Height * 1.15);

        Rect2 skyRect = ClampCanvasRect(
            box.Left - padX,
            box.Top - probeH,
            box.Right + padX,
            box.Top + box.Height * 0.15,
            data.CanvasSize);

        Rect2 groundRect = ClampCanvasRect(
            box.Left - padX,
            box.Bottom - box.Height * 0.15,
            box.Right + padX,
            box.Bottom + probeH,
            data.CanvasSize);

        var skySample = media.Sample(data.EnvQuery, skyRect, timeUs);
        var groundSample = media.Sample(data.EnvQuery, groundRect, timeUs);

        if (skySample.Input.State != MediaInputState.Available
            || groundSample.Input.State != MediaInputState.Available
            || skySample.CanvasValues is not { } skyVals
            || groundSample.CanvasValues is not { } groundVals)
        {
            return fallbackEnv;
        }

        ColorRgba sampledSky = PurifyEnvColor(skyVals.Mean, data.FallbackSkyColor);
        ColorRgba sampledGround = PurifyEnvColor(groundVals.Mean, data.FallbackGroundColor);

        ColorRgba sky = LerpColor(data.FallbackSkyColor, sampledSky, data.EnvBlend, 1.0);
        ColorRgba ground = LerpColor(data.FallbackGroundColor, sampledGround, data.EnvBlend, 1.0);

        double meanY = 0.5 * (Math.Max(0.0, skyVals.MeanPremulY) + Math.Max(0.0, groundVals.MeanPremulY));
        double bgLuma = Math.Clamp(Math.Sqrt(meanY), 0.0, 1.0);

        double meanVar = 0.5 * (Math.Max(0.0, skyVals.PremulYVariance) + Math.Max(0.0, groundVals.PremulYVariance));
        double varNorm = Math.Clamp(Math.Sqrt(meanVar) * 2.8, 0.0, 1.0);

        return new ChromeEnvSample(sky, ground, bgLuma, varNorm);
    }

    private static double PackChromeState(double bgLuma, double varianceNorm, double sheenProgress)
    {
        int lumaQ = Math.Clamp((int)Math.Round(bgLuma * 31.0), 0, 31);
        int varQ = Math.Clamp((int)Math.Round(varianceNorm * 31.0), 0, 31);
        double sheenQ = Math.Clamp(sheenProgress, 0.0, 1.0) * 0.98;
        return (lumaQ * 32 + varQ) + sheenQ;
    }

    private static ColorRgba PurifyEnvColor(LinearPremulMean mean, ColorRgba fallback)
    {
        double a = mean.Alpha;
        if (!double.IsFinite(a) || a <= 1e-4)
            return fallback;

        double lr = Math.Clamp(mean.R / a, 0.0, 1.0);
        double lg = Math.Clamp(mean.G / a, 0.0, 1.0);
        double lb = Math.Clamp(mean.B / a, 0.0, 1.0);

        double sr = LinearToSrgb(lr);
        double sg = LinearToSrgb(lg);
        double sb = LinearToSrgb(lb);

        // 适度提纯色彩饱和度并保留金属反射亮度余量
        double avg = (sr + sg + sb) / 3.0;
        double pr = Math.Clamp(avg + (sr - avg) * 1.40, 0.08, 0.98);
        double pg = Math.Clamp(avg + (sg - avg) * 1.40, 0.08, 0.98);
        double pb = Math.Clamp(avg + (sb - avg) * 1.40, 0.08, 0.98);
        return new ColorRgba(pr, pg, pb, 1.0);
    }

    private static double LinearToSrgb(double c)
    {
        double x = Math.Clamp(c, 0.0, 1.0);
        return x <= 0.0031308
            ? 12.92 * x
            : 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055;
    }

    private static double ComputePerceptualLuma(ColorRgba a, ColorRgba b)
    {
        double yA = 0.2126 * a.Red + 0.7152 * a.Green + 0.0722 * a.Blue;
        double yB = 0.2126 * b.Red + 0.7152 * b.Green + 0.0722 * b.Blue;
        return Math.Clamp(0.5 * (yA + yB), 0.0, 1.0);
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

    private static Rect2 MapTokenUnionToCanvas(TokenGeometrySample token)
    {
        Rect2 result = MapBoundsToCanvas(token.BaseGeometry);
        var rubies = token.Ruby;
        for (int i = 0; i < rubies.Length; i++)
            result = Union(result, MapBoundsToCanvas(rubies[i].Geometry));
        return result;
    }

    private static Rect2 MapBoundsToCanvas(GeometrySample geometry)
    {
        var b = geometry.InkBounds ?? geometry.AdvanceBounds;
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

    private static Rect2 Union(Rect2 a, Rect2 b) => new(
        Math.Min(a.Left, b.Left),
        Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Max(a.Bottom, b.Bottom));

    private static Rect2 ClampCanvasRect(double left, double top, double right, double bottom, Vector2 canvasSize)
    {
        double maxW = Math.Max(4.0, canvasSize.X);
        double maxH = Math.Max(4.0, canvasSize.Y);
        double l = Math.Clamp(left, 0.0, maxW - 2.0);
        double t = Math.Clamp(top, 0.0, maxH - 2.0);
        double r = Math.Clamp(right, l + 2.0, maxW);
        double b = Math.Clamp(bottom, t + 2.0, maxH);
        return new Rect2(l, t, r, b);
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

    private static double CleanFloat(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < 1e-5)
            return 0.0;
        return value;
    }
}
