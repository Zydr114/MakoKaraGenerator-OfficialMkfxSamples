using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public enum EchoTransformMode
{
    RadialZoom = 0,
    HorizontalShear = 1,
    OrbitalSplit = 2,
}

public sealed record BeatKineticEchoData(
    GraphHandle EchoGraph,
    ParameterValues GraphParameters,
    Vector2 CanvasSize,
    EchoTransformMode Mode,
    double MaxSpreadPx,
    double MaxScaleBoost,
    double EchoOpacity,
    ColorRgba TintA,
    ColorRgba TintMid,
    ColorRgba TintB,
    double FallbackBpm) : PreparedEffect;

/// <summary>节拍动感残影与放射叠印：音频节拍或唱字瞬态驱动三路并行 Transform + Blur + Plus 叠印残影。</summary>
public sealed class BeatKineticEcho : IPureKaraokeEffect<BeatKineticEchoData>
{
    private const double MaxSampleOffsetBudget = 244.0;
    private const double TwoPi = Math.PI * 2.0;

    private readonly record struct AudioDrive(double Energy, double Phase);
    private readonly record struct EchoPlacement(bool IsValid, Affine2 Matrix, Rect2 OutputBounds);

    public BeatKineticEchoData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        double blurSigma = Math.Clamp(p.GetNumber("blurSigmaPx"), 0.0, 16.0);
        bool sungOnly = p.GetBoolean("sungOnly");
        var graph = source.Resources.GetGraph("kinetic-echo");
        var graphParams = new ParameterValues([
            new("blurSigmaPx", new ParameterValue.Number(blurSigma)),
            new("sungOnly", new ParameterValue.Toggle(sungOnly)),
        ]);

        string modeText = p.GetEnum("echoMode");
        EchoTransformMode mode = modeText switch
        {
            "horizontalShear" => EchoTransformMode.HorizontalShear,
            "orbitalSplit" => EchoTransformMode.OrbitalSplit,
            _ => EchoTransformMode.RadialZoom,
        };

        ColorRgba tintA = p.GetColor("tintA");
        ColorRgba tintB = p.GetColor("tintB");
        ColorRgba tintMid = LerpColor(tintA, tintB, 0.5, 1.0);

        return new BeatKineticEchoData(
            graph,
            graphParams,
            source.CanvasSize,
            mode,
            Math.Clamp(p.GetNumber("maxSpreadPx"), 0.0, 120.0),
            Math.Clamp(p.GetNumber("maxScaleBoost"), 0.0, 0.45),
            Math.Clamp(p.GetNumber("echoOpacity"), 0.0, 1.0),
            tintA,
            tintMid,
            tintB,
            Math.Clamp(p.GetNumber("fallbackBpm"), 0.0, 240.0));
    }

    public void Evaluate(in TextFrame frame, BeatKineticEchoData data, TextOutput output)
    {
        var loads = frame.LineLoads;
        if (loads.Length == 0)
            return;

        var lineGeometries = frame.LineGeometry;
        var tokenGeometries = frame.Geometry;
        var tokens = frame.Presentations.TokensInBinding;

        AudioDrive baseAudio = SampleAudio(frame.Audio, frame.TimeUs, data.FallbackBpm);

        for (int i = 0; i < loads.Length; i++)
        {
            var load = loads[i];
            var lineGeom = FindLineGeometry(lineGeometries, load.Target);
            if (lineGeom is null)
                continue;

            Rect2 lineBounds = ComputeLineCanvasBounds(lineGeom, tokens, tokenGeometries, load.Line.BindingId);
            if (lineBounds.Width < 2.0 || lineBounds.Height < 2.0)
                continue;

            AudioDrive drive = baseAudio.Energy > 0.001
                ? baseAudio
                : FallbackOnsetAudio(frame.TimeUs, tokens, load.Line.BindingId);

            Point2 pivot = lineBounds.Center;
            EchoPlacement p1 = BuildEchoPlacement(0, data.Mode, pivot, lineBounds, data.CanvasSize, data.MaxSpreadPx, data.MaxScaleBoost, drive);
            EchoPlacement p2 = BuildEchoPlacement(1, data.Mode, pivot, lineBounds, data.CanvasSize, data.MaxSpreadPx, data.MaxScaleBoost, drive);
            EchoPlacement p3 = BuildEchoPlacement(2, data.Mode, pivot, lineBounds, data.CanvasSize, data.MaxSpreadPx, data.MaxScaleBoost, drive);

            double baseOp = data.EchoOpacity * (0.35 + 0.65 * drive.Energy);
            double op1 = p1.IsValid ? Math.Clamp(baseOp * 0.95, 0.0, 1.0) : 0.0;
            double op2 = p2.IsValid ? Math.Clamp(baseOp * 0.72, 0.0, 1.0) : 0.0;
            double op3 = p3.IsValid ? Math.Clamp(baseOp * 0.52, 0.0, 1.0) : 0.0;

            var transforms = new GraphTransformValues([
                new("echo1", new GraphTransformValue(p1.Matrix, p1.OutputBounds)),
                new("echo2", new GraphTransformValue(p2.Matrix, p2.OutputBounds)),
                new("echo3", new GraphTransformValue(p3.Matrix, p3.OutputBounds)),
            ]);

            var uniforms = new GraphUniformValues([
                new(new ValueTuple<string, string>("tint1", "tintColor"), UniformValue.Color(data.TintA)),
                new(new ValueTuple<string, string>("tint1", "opacity"), UniformValue.Float(CleanFloat(op1))),
                new(new ValueTuple<string, string>("tint2", "tintColor"), UniformValue.Color(data.TintMid)),
                new(new ValueTuple<string, string>("tint2", "opacity"), UniformValue.Float(CleanFloat(op2))),
                new(new ValueTuple<string, string>("tint3", "tintColor"), UniformValue.Color(data.TintB)),
                new(new ValueTuple<string, string>("tint3", "opacity"), UniformValue.Float(CleanFloat(op3))),
            ]);

            output.BindGraph(load.Target, new GraphBindingValue(
                data.EchoGraph,
                data.GraphParameters,
                uniforms,
                transforms));
        }
    }

    private static EchoPlacement BuildEchoPlacement(
        int layerIndex,
        EchoTransformMode mode,
        Point2 pivot,
        Rect2 lineBounds,
        Vector2 canvasSize,
        double maxSpreadPx,
        double maxScaleBoost,
        AudioDrive drive)
    {
        Affine2 matrix;
        if (mode == EchoTransformMode.HorizontalShear)
        {
            double dir = layerIndex == 0 ? -0.90 : (layerIndex == 1 ? 0.65 : 1.18);
            double amount = 0.28 + 0.72 * drive.Energy;
            double dx = dir * maxSpreadPx * amount;
            double dy = (layerIndex - 1) * 3.5 * amount;
            double shear = -dir * 0.26 * amount;
            double sx = 1.0 + 0.025 * (layerIndex + 1) * drive.Energy;
            matrix = BuildAffine(pivot, sx, 1.0, shear, dx, dy);
        }
        else if (mode == EchoTransformMode.OrbitalSplit)
        {
            double theta = drive.Phase * TwoPi + layerIndex * (TwoPi / 3.0);
            double radius = maxSpreadPx * (0.42 + 0.26 * layerIndex) * (0.35 + 0.65 * drive.Energy);
            double dx = Math.Cos(theta) * radius;
            double dy = Math.Sin(theta) * radius * 0.72;
            double scale = 1.0 + 0.05 * (layerIndex + 1) * drive.Energy;
            matrix = BuildAffine(pivot, scale, scale, 0.0, dx, dy);
        }
        else
        {
            double frac = layerIndex == 0 ? 0.35 : (layerIndex == 1 ? 0.68 : 1.00);
            double scale = 1.0 + maxScaleBoost * frac * (0.25 + 0.75 * drive.Energy);
            matrix = BuildAffine(pivot, scale, scale, 0.0, 0.0, 0.0);
        }

        double srcLeft = lineBounds.Left - 14.0;
        double srcTop = lineBounds.Top - 14.0;
        double srcRight = lineBounds.Right + 14.0;
        double srcBottom = lineBounds.Bottom + 14.0;

        Point2 p0 = MapAffine(matrix, srcLeft, srcTop);
        Point2 p1 = MapAffine(matrix, srcRight, srcTop);
        Point2 p2 = MapAffine(matrix, srcRight, srcBottom);
        Point2 p3 = MapAffine(matrix, srcLeft, srcBottom);

        double dstLeft = Math.Clamp(Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)), 0.0, canvasSize.X);
        double dstTop = Math.Clamp(Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)), 0.0, canvasSize.Y);
        double dstRight = Math.Clamp(Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)), 0.0, canvasSize.X);
        double dstBottom = Math.Clamp(Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)), 0.0, canvasSize.Y);

        // 对动态 Transform 输出框做严格逆映射偏移钳位，避免超宽字幕行缩放后触发 GraphTransformSamplingExceeded
        double det = matrix.A * matrix.D - matrix.B * matrix.C;
        if (Math.Abs(det) < 1e-4 || dstRight - dstLeft < 2.0 || dstBottom - dstTop < 2.0)
        {
            return SafeFallbackPlacement(pivot, canvasSize);
        }

        double invA = matrix.D / det;
        double invC = -matrix.C / det;
        double invTx = (matrix.C * matrix.Ty - matrix.D * matrix.Tx) / det;
        double invB = -matrix.B / det;
        double invD = matrix.A / det;
        double invTy = (matrix.B * matrix.Tx - matrix.A * matrix.Ty) / det;

        for (int iter = 0; iter < 4; iter++)
        {
            double maxErrX = 0.0;
            double maxErrY = 0.0;
            for (int c = 0; c < 4; c++)
            {
                double cx = (c & 1) == 0 ? dstLeft : dstRight;
                double cy = (c & 2) == 0 ? dstTop : dstBottom;
                double mx = invA * cx + invC * cy + invTx;
                double my = invB * cx + invD * cy + invTy;
                maxErrX = Math.Max(maxErrX, Math.Abs(mx - cx));
                maxErrY = Math.Max(maxErrY, Math.Abs(my - cy));
            }
            if (maxErrX <= MaxSampleOffsetBudget && maxErrY <= MaxSampleOffsetBudget)
                break;

            double shrinkX = maxErrX > MaxSampleOffsetBudget ? MaxSampleOffsetBudget / maxErrX : 1.0;
            double shrinkY = maxErrY > MaxSampleOffsetBudget ? MaxSampleOffsetBudget / maxErrY : 1.0;
            double factor = Math.Min(shrinkX, shrinkY) * 0.96;
            double cxMid = 0.5 * (dstLeft + dstRight);
            double cyMid = 0.5 * (dstTop + dstBottom);
            double halfW = 0.5 * (dstRight - dstLeft) * factor;
            double halfH = 0.5 * (dstBottom - dstTop) * factor;
            dstLeft = Math.Clamp(cxMid - halfW, 0.0, canvasSize.X);
            dstRight = Math.Clamp(cxMid + halfW, 0.0, canvasSize.X);
            dstTop = Math.Clamp(cyMid - halfH, 0.0, canvasSize.Y);
            dstBottom = Math.Clamp(cyMid + halfH, 0.0, canvasSize.Y);
        }

        if (dstRight - dstLeft < 2.0 || dstBottom - dstTop < 2.0)
            return SafeFallbackPlacement(pivot, canvasSize);

        return new EchoPlacement(true, matrix, new Rect2(dstLeft, dstTop, dstRight, dstBottom));
    }

    private static EchoPlacement SafeFallbackPlacement(Point2 pivot, Vector2 canvasSize)
    {
        double l = Math.Clamp(pivot.X - 1.0, 0.0, Math.Max(0.0, canvasSize.X - 2.0));
        double t = Math.Clamp(pivot.Y - 1.0, 0.0, Math.Max(0.0, canvasSize.Y - 2.0));
        return new EchoPlacement(false, Affine2.Identity, new Rect2(l, t, l + 2.0, t + 2.0));
    }

    private static AudioDrive SampleAudio(AudioFeatures audio, long timeUs, double fallbackBpm)
    {
        long t0 = Math.Max(0L, timeUs);
        var s0 = audio.GetAt(t0);
        if (s0.Availability == AudioAvailability.Available && s0.Values is { } v0)
        {
            double p1 = SamplePulseAt(audio, Math.Max(0L, t0 - 40_000L));
            double p2 = SamplePulseAt(audio, Math.Max(0L, t0 - 80_000L));
            double decayedPulse = Math.Clamp(Math.Max(v0.Pulse, Math.Max(p1 * 0.72, p2 * 0.46)), 0.0, 1.0);
            double energy = Math.Clamp(0.58 * decayedPulse + 0.42 * v0.BassLevel, 0.0, 1.0);
            double phase = v0.BeatPhase ?? ((t0 / 1_000_000.0) % 1.0);
            return new AudioDrive(energy, phase);
        }

        if (fallbackBpm > 0.0)
        {
            double beats = (t0 / 1_000_000.0) * (fallbackBpm / 60.0);
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.2);
            double energy = Math.Clamp(0.24 + 0.72 * impulse, 0.0, 1.0);
            return new AudioDrive(energy, phase);
        }

        return new AudioDrive(0.0, 0.0);
    }

    private static double SamplePulseAt(AudioFeatures audio, long timeUs)
    {
        var s = audio.GetAt(timeUs);
        return s.Availability == AudioAvailability.Available && s.Values is { } v ? v.Pulse : 0.0;
    }

    private static AudioDrive FallbackOnsetAudio(long timeUs, ImmutableArray<FrameToken> tokens, BindingId bindingId)
    {
        double bestEnergy = 0.18;
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.BindingId != bindingId)
                continue;

            if (token.Performance.TimeRange is not { IsValid: true } range)
                continue;

            if (timeUs >= range.StartUs && timeUs <= range.EndUs + 200_000L)
            {
                double elapsedSec = Math.Max(0.0, (timeUs - range.StartUs) / 1_000_000.0);
                double onset = Math.Exp(-elapsedSec * 6.5);
                bestEnergy = Math.Max(bestEnergy, 0.25 + 0.70 * onset);
            }
        }
        double phase = ((Math.Max(0L, timeUs) / 1_000_000.0) * 1.6) % 1.0;
        return new AudioDrive(Math.Clamp(bestEnergy, 0.0, 1.0), phase);
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

    private static Affine2 BuildAffine(Point2 pivot, double sx, double sy, double shearX, double dx, double dy)
    {
        double a = sx;
        double b = 0.0;
        double c = shearX;
        double d = sy;
        double tx = pivot.X + dx - (a * pivot.X + c * pivot.Y);
        double ty = pivot.Y + dy - (b * pivot.X + d * pivot.Y);
        return new Affine2(a, b, c, d, tx, ty);
    }

    private static Point2 MapAffine(Affine2 m, double x, double y)
        => new(m.A * x + m.C * y + m.Tx, m.B * x + m.D * y + m.Ty);

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
