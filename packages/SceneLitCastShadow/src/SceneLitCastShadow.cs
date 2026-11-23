using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public readonly record struct SceneLightEstimate(
    double LightDirX,
    double LightDirY,
    double ContrastFactor,
    double BlurFactor,
    ColorRgba ShadowColor);

public readonly record struct CastShadowTransformSolution(
    Affine2 Matrix,
    Rect2 OutputBounds,
    double EffectiveScaleY);

public sealed record SceneLitCastShadowData(
    GraphHandle ShadowGraph,
    ImmutableArray<ParameterValues> BlurParamBuckets,
    MediaQueryId LightProbeQuery,
    Vector2 CanvasSize,
    double MaxSkew,
    double ShadowScaleY,
    double ShadowBlurPx,
    double ShadowOpacity,
    bool AutoColorTemp,
    double DefaultLightAngleDeg,
    ColorRgba DefaultShadowColor) : PreparedEffect;

/// <summary>实景光源感知立体投影：采样画布四象限亮度与主光源色温，为每行基线动态注入斜切压扁 Affine2 投影矩阵与互补阴影色。</summary>
public sealed class SceneLitCastShadow : IPureKaraokeEffect<SceneLitCastShadowData>
{
    private const int BlurBuckets = 16;
    private const double CastExpandBudgetPx = 140.0;
    private const double MaxSafeSampleOffsetPx = 220.0;

    public SceneLitCastShadowData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        double maxSkew = p.GetNumber("maxSkew");
        double shadowScaleY = p.GetNumber("shadowScaleY");
        double shadowBlurPx = Math.Clamp(p.GetNumber("shadowBlurPx"), 0.0, 12.0);
        double shadowOpacity = p.GetNumber("shadowOpacity");
        bool autoColorTemp = p.GetBoolean("autoColorTemp");
        double defaultLightAngleDeg = p.GetNumber("defaultLightAngleDeg");
        ColorRgba defaultShadowColor = p.GetColor("defaultShadowColor");

        GraphHandle shadowGraph = source.Resources.GetGraph("cast-shadow-line");

        var paramBuckets = ImmutableArray.CreateBuilder<ParameterValues>(BlurBuckets + 1);
        for (int i = 0; i <= BlurBuckets; i++)
        {
            double factor = 0.55 + 0.65 * (i / (double)BlurBuckets);
            double sigma = Math.Clamp(shadowBlurPx * factor, 0.0, 12.0);
            paramBuckets.Add(new ParameterValues([
                new("shadowBlurPx", new ParameterValue.Number(sigma)),
                new("shadowOpacity", new ParameterValue.Number(shadowOpacity)),
                new("defaultShadowColor", new ParameterValue.Color(defaultShadowColor)),
            ]));
        }

        return new SceneLitCastShadowData(
            shadowGraph,
            paramBuckets.ToImmutable(),
            source.Media.GetId("lightProbe"),
            source.CanvasSize,
            maxSkew,
            shadowScaleY,
            shadowBlurPx,
            shadowOpacity,
            autoColorTemp,
            defaultLightAngleDeg,
            defaultShadowColor);
    }

    public void Evaluate(in TextFrame frame, SceneLitCastShadowData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        SceneLightEstimate light = EstimateSceneLight(frame, data);
        int blurIdx = Math.Clamp((int)Math.Round(light.BlurFactor * BlurBuckets), 0, BlurBuckets);
        ParameterValues graphParams = data.GraphParametersBucket(blurIdx);

        var lineGeometries = frame.LineGeometry;
        var tokenGeometries = frame.Geometry;
        var tokens = frame.Presentations.TokensInBinding;

        for (int i = 0; i < loads.Length; i++)
        {
            var load = loads[i];
            var lineGeom = FindLineGeometry(lineGeometries, load.Target);
            if (lineGeom is null)
                continue;

            Rect2 lineBox = ComputeLineCanvasBounds(lineGeom, tokens, tokenGeometries, load.Line.BindingId);
            Point2 baselineCanvas = lineGeom.BaseGeometry.MapPoint(lineGeom.BaseGeometry.Baseline);

            CastShadowTransformSolution castSolution = SolveCastShadowTransform(
                lineBox,
                baselineCanvas.Y,
                light,
                data);

            var transforms = new GraphTransformValues([
                new("cast", new GraphTransformValue(castSolution.Matrix, castSolution.OutputBounds))
            ]);

            double shadowHeightSpan = Math.Max(32.0, lineBox.Height * castSolution.EffectiveScaleY);
            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>("shadowTint", "uShadowColor"), UniformValue.Color(light.ShadowColor)),
                new(new ValueTuple<string, string>("shadowTint", "uOpacity"), UniformValue.Float(CleanFloat(data.ShadowOpacity))),
                new(new ValueTuple<string, string>("shadowTint", "uLineGeo"), UniformValue.Float4(
                    CleanFloat(baselineCanvas.Y),
                    CleanFloat(shadowHeightSpan),
                    CleanFloat(light.LightDirY),
                    CleanFloat(light.ContrastFactor))),
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.ShadowGraph,
                graphParams,
                uniforms,
                transforms));
        }
    }

    private static SceneLightEstimate EstimateSceneLight(in TextFrame frame, SceneLitCastShadowData data)
    {
        double defRad = data.DefaultLightAngleDeg * Math.PI / 180.0;
        double defLx = Math.Cos(defRad);
        double defLy = Math.Sin(defRad);

        if (frame.OutputMode != OutputMode.Opaque || frame.Media.Catalog.Input?.State != MediaInputState.Available)
            return new SceneLightEstimate(defLx, defLy, 0.94, 0.55, data.DefaultShadowColor);

        double w = Math.Max(4.0, data.CanvasSize.X);
        double h = Math.Max(4.0, data.CanvasSize.Y);
        double hw = w * 0.5;
        double hh = h * 0.5;

        var sTL = frame.Media.Sample(data.LightProbeQuery, new Rect2(0.0, 0.0, hw, hh), frame.TimeUs);
        var sTR = frame.Media.Sample(data.LightProbeQuery, new Rect2(hw, 0.0, w, hh), frame.TimeUs);
        var sBL = frame.Media.Sample(data.LightProbeQuery, new Rect2(0.0, hh, hw, h), frame.TimeUs);
        var sBR = frame.Media.Sample(data.LightProbeQuery, new Rect2(hw, hh, w, h), frame.TimeUs);

        if (sTL.Input.State != MediaInputState.Available
            || sTL.CanvasValues is not { } vTL
            || sTR.CanvasValues is not { } vTR
            || sBL.CanvasValues is not { } vBL
            || sBR.CanvasValues is not { } vBR)
        {
            return new SceneLightEstimate(defLx, defLy, 0.94, 0.55, data.DefaultShadowColor);
        }

        double yTL = Math.Sqrt(Math.Max(0.0, vTL.MeanPremulY));
        double yTR = Math.Sqrt(Math.Max(0.0, vTR.MeanPremulY));
        double yBL = Math.Sqrt(Math.Max(0.0, vBL.MeanPremulY));
        double yBR = Math.Sqrt(Math.Max(0.0, vBR.MeanPremulY));

        double yLeft = 0.5 * (yTL + yBL);
        double yRight = 0.5 * (yTR + yBR);
        double yTop = 0.5 * (yTL + yTR);
        double yBottom = 0.5 * (yBL + yBR);

        double dlx = yRight - yLeft;
        double dly = yBottom - yTop;
        double rawMag = Math.Sqrt(dlx * dlx + dly * dly);

        // 在明显方向差异下跟随实景主光方向；均匀漫反射背景时平滑回退到默认光源角
        double probeWeight = Math.Clamp(rawMag / 0.035, 0.0, 1.0);
        double probeLx = rawMag > 1e-5 ? dlx / rawMag : defLx;
        double probeLy = rawMag > 1e-5 ? dly / rawMag : defLy;

        double mixLx = defLx + (probeLx - defLx) * probeWeight;
        double mixLy = defLy + (probeLy - defLy) * probeWeight;
        double mixLen = Math.Sqrt(mixLx * mixLx + mixLy * mixLy);
        double lx = mixLen > 1e-5 ? mixLx / mixLen : defLx;
        double ly = mixLen > 1e-5 ? mixLy / mixLen : defLy;

        double contrastNorm = Math.Clamp(Math.Tanh(rawMag * 14.0), 0.0, 1.0);
        double contrastFactor = 0.68 + 0.34 * contrastNorm;
        double blurFactor = 1.0 - 0.65 * contrastNorm;

        ColorRgba shadowColor = data.DefaultShadowColor;
        if (data.AutoColorTemp)
        {
            LinearPremulMean brightestMean = SelectBrightestMean(yTL, vTL.Mean, yTR, vTR.Mean, yBL, vBL.Mean, yBR, vBR.Mean);
            ColorRgba lightSrgb = UnpremulToSrgb(brightestMean);
            ColorRgba compTint = new(
                Math.Clamp((1.0 - lightSrgb.Red) * 0.34, 0.02, 0.38),
                Math.Clamp((1.0 - lightSrgb.Green) * 0.32, 0.02, 0.36),
                Math.Clamp((1.0 - lightSrgb.Blue) * 0.42, 0.04, 0.46),
                1.0);
            shadowColor = LerpColor(data.DefaultShadowColor, compTint, 0.72, 1.0);
        }

        return new SceneLightEstimate(lx, ly, contrastFactor, blurFactor, shadowColor);
    }

    private static CastShadowTransformSolution SolveCastShadowTransform(
        Rect2 lineBox,
        double baselineY,
        SceneLightEstimate light,
        SceneLitCastShadowData data)
    {
        double skewX = Math.Clamp(light.LightDirX * data.MaxSkew, -1.45, 1.45);
        double scaleY = Math.Clamp(data.ShadowScaleY + (light.LightDirY > 0.0 ? 0.14 * light.LightDirY : 0.0), 0.30, 0.90);
        double dx = -light.LightDirX * 18.0;
        double dy = (light.LightDirY <= 0.0 ? -light.LightDirY * 14.0 : -light.LightDirY * 10.0) + 4.0;

        double maxW = Math.Max(4.0, data.CanvasSize.X);
        double maxH = Math.Max(4.0, data.CanvasSize.Y);
        double limL = -CastExpandBudgetPx;
        double limT = -CastExpandBudgetPx;
        double limR = maxW + CastExpandBudgetPx;
        double limB = maxH + CastExpandBudgetPx;

        // 迭代确保外扩框和逆采样偏移严格满足 GraphTransformSamplingExceeded 与 GraphTransformBoundsExceeded 约束
        double damp = 1.0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            double curSkew = skewX * damp;
            double curScaleY = 1.0 + (scaleY - 1.0) * damp;
            curScaleY = Math.Clamp(curScaleY, 0.28, 0.95);
            double curDx = dx * damp;
            double curDy = dy * damp;

            double tx = curDx - curSkew * baselineY;
            double ty = baselineY * (1.0 - curScaleY) + curDy;

            Rect2 projected = MapBox(lineBox, 1.0, 0.0, curSkew, curScaleY, tx, ty);
            Rect2 padded = new(
                projected.Left - 8.0,
                projected.Top - 8.0,
                projected.Right + 8.0,
                projected.Bottom + 8.0);
            Rect2 clamped = ClampInsideLimit(padded, limL, limT, limR, limB);

            double maxInvOffset = MaxInverseCornerOffset(clamped, curSkew, curScaleY, tx, ty);
            if (maxInvOffset <= MaxSafeSampleOffsetPx)
            {
                return new CastShadowTransformSolution(
                    new Affine2(CleanFloat(1.0), 0.0, CleanFloat(curSkew), CleanFloat(curScaleY), CleanFloat(tx), CleanFloat(ty)),
                    clamped,
                    curScaleY);
            }

            damp *= 0.72;
        }

        Rect2 safeFallback = ClampInsideLimit(lineBox, limL, limT, limR, limB);
        return new CastShadowTransformSolution(Affine2.Identity, safeFallback, 1.0);
    }

    private static double MaxInverseCornerOffset(Rect2 bounds, double skewX, double scaleY, double tx, double ty)
    {
        double invScaleY = 1.0 / scaleY;
        double maxOff = 0.0;

        maxOff = Math.Max(maxOff, InverseOffsetAt(bounds.Left, bounds.Top, skewX, invScaleY, tx, ty));
        maxOff = Math.Max(maxOff, InverseOffsetAt(bounds.Right, bounds.Top, skewX, invScaleY, tx, ty));
        maxOff = Math.Max(maxOff, InverseOffsetAt(bounds.Right, bounds.Bottom, skewX, invScaleY, tx, ty));
        maxOff = Math.Max(maxOff, InverseOffsetAt(bounds.Left, bounds.Bottom, skewX, invScaleY, tx, ty));
        return maxOff;
    }

    private static double InverseOffsetAt(double x, double y, double skewX, double invScaleY, double tx, double ty)
    {
        double srcY = (y - ty) * invScaleY;
        double srcX = x - tx - skewX * srcY;
        return Math.Max(Math.Abs(srcX - x), Math.Abs(srcY - y));
    }

    private static Rect2 MapBox(Rect2 box, double a, double b, double c, double d, double tx, double ty)
    {
        double x0 = a * box.Left + c * box.Top + tx;
        double y0 = b * box.Left + d * box.Top + ty;
        double x1 = a * box.Right + c * box.Top + tx;
        double y1 = b * box.Right + d * box.Top + ty;
        double x2 = a * box.Right + c * box.Bottom + tx;
        double y2 = b * box.Right + d * box.Bottom + ty;
        double x3 = a * box.Left + c * box.Bottom + tx;
        double y3 = b * box.Left + d * box.Bottom + ty;

        double left = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        double top = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        double right = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        double bottom = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return new Rect2(left, top, Math.Max(left + 4.0, right), Math.Max(top + 4.0, bottom));
    }

    private static Rect2 ClampInsideLimit(Rect2 rect, double limL, double limT, double limR, double limB)
    {
        double l = Math.Clamp(rect.Left, limL, limR - 4.0);
        double t = Math.Clamp(rect.Top, limT, limB - 4.0);
        double r = Math.Clamp(rect.Right, l + 4.0, limR);
        double b = Math.Clamp(rect.Bottom, t + 4.0, limB);
        return new Rect2(l, t, r, b);
    }

    private static LinearPremulMean SelectBrightestMean(
        double y0, LinearPremulMean m0,
        double y1, LinearPremulMean m1,
        double y2, LinearPremulMean m2,
        double y3, LinearPremulMean m3)
    {
        double bestY = y0;
        LinearPremulMean best = m0;
        if (y1 > bestY) { bestY = y1; best = m1; }
        if (y2 > bestY) { bestY = y2; best = m2; }
        if (y3 > bestY) { best = m3; }
        return best;
    }

    private static ColorRgba UnpremulToSrgb(LinearPremulMean mean)
    {
        double a = mean.Alpha;
        if (!double.IsFinite(a) || a <= 1e-4)
            return new ColorRgba(1.0, 1.0, 1.0, 1.0);

        return new ColorRgba(
            LinearToSrgb(mean.R / a),
            LinearToSrgb(mean.G / a),
            LinearToSrgb(mean.B / a),
            1.0);
    }

    private static double LinearToSrgb(double c)
    {
        double x = Math.Clamp(c, 0.0, 1.0);
        return x <= 0.0031308
            ? 12.92 * x
            : 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055;
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
            if (tokens[i].BindingId != bindingId)
                continue;
            var tokenGeom = FindTokenGeometry(tokenGeometries, tokens[i].WithRuby);
            if (tokenGeom is not null)
                result = Union(result, MapTokenUnionToCanvas(tokenGeom));
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

internal static class SceneLitCastShadowDataExtensions
{
    internal static ParameterValues GraphParametersBucket(this SceneLitCastShadowData data, int index)
        => data.BlurParamBuckets[Math.Clamp(index, 0, data.BlurParamBuckets.Length - 1)];
}
