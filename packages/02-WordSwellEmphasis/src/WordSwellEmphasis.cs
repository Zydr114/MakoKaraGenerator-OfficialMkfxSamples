using System;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record WordSwellData(
    double PeakScale,
    double LiftPx,
    long ReturnUs,
    CurveValue GrowCurve,
    CurveValue ReturnCurve,
    string AnchorMode) : PreparedEffect;

/// <summary>逐字渐大强调：唱到的字随演唱进度从原尺寸逐渐膨胀至峰值，唱完后平滑回落至原有尺寸。</summary>
public sealed class WordSwellEmphasis : IPureKaraokeEffect<WordSwellData>
{
    public WordSwellData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        return new WordSwellData(
            p.GetNumber("peakScale"),
            p.GetNumber("liftPx"),
            p.GetDuration("returnUs"),
            p.GetCurve("growCurve"),
            p.GetCurve("returnCurve"),
            p.GetEnum("anchor"));
    }

    public void Evaluate(in TextFrame frame, WordSwellData data, TextOutput output)
    {
        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var perf = token.Performance;
            if (perf.ProgressSource != ProgressSource.Token || perf.TimeRange is not { } range)
                continue;

            double weight = 0;
            if (perf.IsCurrent && perf.Progress is { } progress)
            {
                weight = Math.Max(0.0, data.GrowCurve.Evaluate(Math.Clamp(progress, 0.0, 1.0)));
            }
            else if (perf.IsSung && data.ReturnUs > 0)
            {
                long elapsedAfterEnd = frame.TimeUs - range.EndUs;
                if (elapsedAfterEnd >= 0 && elapsedAfterEnd < data.ReturnUs)
                {
                    double u = Math.Clamp(elapsedAfterEnd / (double)data.ReturnUs, 0.0, 1.0);
                    weight = Math.Max(0.0, 1.0 - data.ReturnCurve.Evaluate(u));
                }
            }

            if (weight <= 0.0005)
                continue;

            double scale = Math.Max(0.1, 1.0 + (data.PeakScale - 1.0) * weight);
            double dy = data.LiftPx * weight;

            foreach (var geometry in frame.Geometry)
            {
                if (geometry.WithRuby != token.WithRuby || !geometry.BaseGeometry.CanTransformIndependently)
                    continue;

                var bounds = geometry.BaseGeometry.AdvanceBounds;
                Point2 pivot = data.AnchorMode switch
                {
                    "baseline" => new Point2(bounds.Center.X, geometry.BaseGeometry.Baseline.Y),
                    "bottom" => new Point2(bounds.Center.X, bounds.Bottom),
                    _ => bounds.Center,
                };

                Affine2 matrix = Math.Abs(dy) > 0.001
                    ? Affine2.Translation(0, dy).Compose(Affine2.ScaleAt(scale, pivot))
                    : Affine2.ScaleAt(scale, pivot);

                output.AppendTransform(token.WithRuby, matrix);
            }
        }
    }
}
