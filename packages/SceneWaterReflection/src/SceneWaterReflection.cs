using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record SceneWaterReflectionData(
    GraphHandle ReflectionGraph,
    ParameterValues GraphParameters,
    MediaQueryId WaterQuery,
    Vector2 CanvasSize,
    double ReflectionScaleY,
    double GapPx,
    double SkewX,
    double RippleAmpPx,
    double RippleFreq,
    double ReflectionOpacity,
    double WaterColorMix,
    ColorRgba DefaultWaterTint) : PreparedEffect;

/// <summary>实景水面倒影与波纹镜像：沿行基线构造动态倒影仿射矩阵，并采样基线下方水面亮度与方差驱动波纹与染色。</summary>
public sealed class SceneWaterReflection : IPureKaraokeEffect<SceneWaterReflectionData>
{
    private const double MaxSampleOffsetBudget = 244.0;
    private const string MirrorNodeId = "mirror";
    private const string RippleNodeId = "waterRipple";

    private readonly record struct WaterStats(ColorRgba Tint, double Luma, double Roughness);
    private readonly record struct MirrorPlacement(
        bool IsValid,
        Affine2 Matrix,
        Rect2 OutputBounds,
        double WaterlineY,
        double SpanPx,
        Rect2 SampleRect);

    public SceneWaterReflectionData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        double blurSigma = Math.Clamp(p.GetNumber("blurSigmaPx"), 0.0, 12.0);
        var graph = source.Resources.GetGraph("water-reflection");
        var graphParams = new ParameterValues([
            new("blurSigmaPx", new ParameterValue.Number(blurSigma))
        ]);

        return new SceneWaterReflectionData(
            graph,
            graphParams,
            source.Media.GetId("waterSurface"),
            source.CanvasSize,
            Math.Clamp(p.GetNumber("reflectionScaleY"), 0.25, 1.0),
            Math.Clamp(p.GetNumber("gapPx"), 0.0, 32.0),
            Math.Clamp(p.GetNumber("skewX"), -0.5, 0.5),
            Math.Clamp(p.GetNumber("rippleAmpPx"), 0.0, 20.0),
            Math.Clamp(p.GetNumber("rippleFreq"), 0.2, 4.0),
            Math.Clamp(p.GetNumber("reflectionOpacity"), 0.0, 1.0),
            Math.Clamp(p.GetNumber("waterColorMix"), 0.0, 1.0),
            p.GetColor("defaultWaterTint"));
    }

    public void Evaluate(in TextFrame frame, SceneWaterReflectionData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        var lineGeometries = frame.LineGeometry;
        var tokenGeometries = frame.Geometry;
        var tokens = frame.Presentations.TokensInBinding;
        bool mediaAvailable = frame.OutputMode == OutputMode.Opaque
            && frame.Media.Catalog.Input?.State == MediaInputState.Available;

        for (int i = 0; i < loads.Length; i++)
        {
            var load = loads[i];
            var lineGeom = FindLineGeometry(lineGeometries, load.Target);
            if (lineGeom is null)
                continue;

            Rect2 lineBounds = ComputeLineCanvasBounds(lineGeom, tokens, tokenGeometries, load.Line.BindingId);
            double baselineY = lineGeom.BaseGeometry.MapPoint(lineGeom.BaseGeometry.Baseline).Y;

            MirrorPlacement placement = BuildMirrorPlacement(
                lineBounds,
                baselineY,
                data.CanvasSize,
                data.ReflectionScaleY,
                data.GapPx,
                data.SkewX);

            if (!placement.IsValid)
                continue;

            WaterStats water = mediaAvailable
                ? SampleWaterSurface(frame.Media, data.WaterQuery, placement.SampleRect, frame.TimeUs, data.DefaultWaterTint)
                : new WaterStats(data.DefaultWaterTint, 0.35, 0.25);

            ColorRgba tintedWater = LerpColor(data.DefaultWaterTint, water.Tint, data.WaterColorMix, 1.0);
            double lumaClarity = 1.18 - 0.58 * water.Luma;
            double effectiveOpacity = Math.Clamp(data.ReflectionOpacity * lumaClarity, 0.0, 1.0);
            double effectiveRippleAmp = Math.Clamp(data.RippleAmpPx * (0.65 + 0.95 * water.Roughness), 0.0, 23.0);

            var transforms = new GraphTransformValues([
                new(MirrorNodeId, new GraphTransformValue(placement.Matrix, placement.OutputBounds))
            ]);

            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>(RippleNodeId, "waterlineY"), UniformValue.Float(CleanFloat(placement.WaterlineY))),
                new(new ValueTuple<string, string>(RippleNodeId, "reflSpanPx"), UniformValue.Float(CleanFloat(placement.SpanPx))),
                new(new ValueTuple<string, string>(RippleNodeId, "rippleAmpPx"), UniformValue.Float(CleanFloat(effectiveRippleAmp))),
                new(new ValueTuple<string, string>(RippleNodeId, "rippleFreq"), UniformValue.Float(CleanFloat(data.RippleFreq))),
                new(new ValueTuple<string, string>(RippleNodeId, "reflectionOpacity"), UniformValue.Float(CleanFloat(effectiveOpacity))),
                new(new ValueTuple<string, string>(RippleNodeId, "waterColorMix"), UniformValue.Float(CleanFloat(data.WaterColorMix))),
                new(new ValueTuple<string, string>(RippleNodeId, "waterTint"), UniformValue.Color(tintedWater))
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.ReflectionGraph,
                data.GraphParameters,
                uniforms,
                transforms));
        }
    }

    private static MirrorPlacement BuildMirrorPlacement(
        Rect2 lineBounds,
        double baselineY,
        Vector2 canvasSize,
        double scaleY,
        double gapPx,
        double skewX)
    {
        double tx = -skewX * baselineY;
        double ty = baselineY * (1.0 + scaleY) + gapPx;
        var matrix = new Affine2(1.0, 0.0, skewX, -scaleY, tx, ty);

        double srcLeft = lineBounds.Left - 12.0;
        double srcTop = lineBounds.Top - 12.0;
        double srcRight = lineBounds.Right + 12.0;
        double srcBottom = lineBounds.Bottom + 8.0;

        Point2 p0 = MapAffine(matrix, srcLeft, srcTop);
        Point2 p1 = MapAffine(matrix, srcRight, srcTop);
        Point2 p2 = MapAffine(matrix, srcRight, srcBottom);
        Point2 p3 = MapAffine(matrix, srcLeft, srcBottom);

        double dstLeft = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        double dstTop = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        double dstRight = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        double dstBottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));

        // 限制动态 outputBounds 在画布范围内且严格满足逆映射采样偏移上限
        dstLeft = Math.Clamp(dstLeft, 0.0, canvasSize.X);
        dstRight = Math.Clamp(dstRight, 0.0, canvasSize.X);
        dstTop = Math.Clamp(dstTop, 0.0, canvasSize.Y);
        dstBottom = Math.Clamp(dstBottom, 0.0, canvasSize.Y);

        double yMaxAllowed = baselineY + (MaxSampleOffsetBudget * scaleY + gapPx) / (1.0 + scaleY);
        double yMinAllowed = baselineY + (-MaxSampleOffsetBudget * scaleY + gapPx) / (1.0 + scaleY);
        dstTop = Math.Max(dstTop, yMinAllowed);
        dstBottom = Math.Min(dstBottom, yMaxAllowed);

        if (Math.Abs(skewX) > 1e-4)
        {
            double halfSkewReach = MaxSampleOffsetBudget * scaleY / Math.Abs(skewX);
            double waterline = baselineY + gapPx;
            dstTop = Math.Max(dstTop, waterline - halfSkewReach);
            dstBottom = Math.Min(dstBottom, waterline + halfSkewReach);
        }

        if (dstRight - dstLeft < 2.0 || dstBottom - dstTop < 2.0)
        {
            return new MirrorPlacement(false, Affine2.Identity, new Rect2(0, 0, 1, 1), baselineY + gapPx, 100.0, new Rect2(0, 0, 1, 1));
        }

        var outputBounds = new Rect2(dstLeft, dstTop, dstRight, dstBottom);
        double waterlineY = baselineY + gapPx;
        double spanPx = Math.Max(24.0, dstBottom - waterlineY);

        double sampleLeft = Math.Clamp(dstLeft, 0.0, Math.Max(0.0, canvasSize.X - 2.0));
        double sampleRight = Math.Clamp(dstRight, sampleLeft + 1.0, canvasSize.X);
        double sampleTop = Math.Clamp(Math.Max(dstTop, waterlineY), 0.0, Math.Max(0.0, canvasSize.Y - 2.0));
        double sampleBottom = Math.Clamp(dstBottom, sampleTop + 1.0, canvasSize.Y);
        var sampleRect = new Rect2(sampleLeft, sampleTop, sampleRight, sampleBottom);

        return new MirrorPlacement(true, matrix, outputBounds, waterlineY, spanPx, sampleRect);
    }

    private static WaterStats SampleWaterSurface(
        MediaFeatures media,
        MediaQueryId query,
        Rect2 sampleRect,
        long timeUs,
        ColorRgba defaultTint)
    {
        var sample = media.Sample(query, sampleRect, timeUs);
        if (sample.Input.State != MediaInputState.Available || sample.CanvasValues is not { } vals)
        {
            return new WaterStats(defaultTint, 0.35, 0.25);
        }

        ColorRgba meanColor = defaultTint;
        if (vals.Mean.Alpha > 1e-4)
        {
            double invA = 1.0 / vals.Mean.Alpha;
            meanColor = new ColorRgba(
                LinearToSrgb(vals.Mean.R * invA),
                LinearToSrgb(vals.Mean.G * invA),
                LinearToSrgb(vals.Mean.B * invA),
                1.0);
        }

        double luma = Math.Clamp(vals.MeanPremulY, 0.0, 1.0);
        double roughness = Math.Clamp(Math.Sqrt(Math.Max(0.0, vals.PremulYVariance)) * 3.5, 0.0, 1.0);
        return new WaterStats(meanColor, luma, roughness);
    }

    private static Rect2 ComputeLineCanvasBounds(
        LineGeometrySample lineGeom,
        ImmutableArray<FrameToken> tokens,
        ImmutableArray<TokenGeometrySample> tokenGeometries,
        BindingId bindingId)
    {
        Rect2 result = MapBoundsToCanvas(lineGeom.BaseGeometry);
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            var tokenGeom = FindTokenGeometry(tokenGeometries, token.WithRuby);
            if (tokenGeom is null)
                continue;

            result = Union(result, MapBoundsToCanvas(tokenGeom.BaseGeometry));
            var rubies = tokenGeom.Ruby;
            for (int r = 0; r < rubies.Length; r++)
                result = Union(result, MapBoundsToCanvas(rubies[r].Geometry));
        }
        return result;
    }

    private static LineGeometrySample? FindLineGeometry(ImmutableArray<LineGeometrySample> lines, LineTarget target)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Target == target)
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

    private static Point2 MapAffine(Affine2 m, double x, double y)
        => new(m.A * x + m.C * y + m.Tx, m.B * x + m.D * y + m.Ty);

    private static double LinearToSrgb(double linear)
    {
        double c = Math.Clamp(linear, 0.0, 1.0);
        return c <= 0.0031308
            ? Math.Clamp(12.92 * c, 0.0, 1.0)
            : Math.Clamp(1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055, 0.0, 1.0);
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
        if (!double.IsFinite(value) || Math.Abs(value) < 1e-6)
            return 0.0;
        return value;
    }
}
