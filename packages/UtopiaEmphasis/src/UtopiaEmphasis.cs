using System;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record UtopiaData(
    double PeakScale,
    long AttackUs,
    bool TokenMode,
    long DecayUs,
    CurveValue DecayCurve,
    string AnchorMode,
    bool FlashGlow,
    ColorRgba GlowColor,
    double GlowBlurPx,
    double GlowOpacity) : PreparedEffect;

/// <summary>仿 NicoKaraMaker Utopia 效果：唱到的字瞬间突变为最大缩放，随后按曲线随时间回落到原尺寸，并伴随脉冲闪光。</summary>
public sealed class UtopiaEmphasis : IPureKaraokeEffect<UtopiaData>
{
    public UtopiaData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        return new UtopiaData(
            p.GetNumber("peakScale"),
            p.GetDuration("attackUs"),
            string.Equals(p.GetEnum("decayMode"), "token", StringComparison.Ordinal),
            p.GetDuration("decayUs"),
            p.GetCurve("decayCurve"),
            p.GetEnum("anchor"),
            p.GetBoolean("flashGlow"),
            p.GetColor("glowColor"),
            p.GetNumber("glowBlurPx"),
            p.GetNumber("glowOpacity"));
    }

    public void Evaluate(in TextFrame frame, UtopiaData data, TextOutput output)
    {
        foreach (var token in frame.Presentations.TokensInBinding)
        {
            var perf = token.Performance;
            if (perf.ProgressSource != ProgressSource.Token || perf.TimeRange is not { } range)
                continue;

            long elapsed = frame.TimeUs - range.StartUs;
            if (elapsed < 0)
                continue;

            double weight;
            if (data.TokenMode)
            {
                if (!perf.IsCurrent)
                    continue;
                long effectiveAttack = Math.Min(data.AttackUs, range.DurationUs);
                if (effectiveAttack > 0 && elapsed < effectiveAttack)
                {
                    weight = Curves.EaseOut(elapsed / (double)effectiveAttack);
                }
                else
                {
                    long rem = Math.Max(1L, range.DurationUs - effectiveAttack);
                    double u = Math.Clamp((elapsed - effectiveAttack) / (double)rem, 0.0, 1.0);
                    weight = Math.Max(0.0, 1.0 - data.DecayCurve.Evaluate(u));
                }
            }
            else
            {
                if (data.AttackUs > 0 && elapsed < data.AttackUs)
                {
                    weight = Curves.EaseOut(elapsed / (double)data.AttackUs);
                }
                else
                {
                    long decayElapsed = elapsed - data.AttackUs;
                    if (decayElapsed >= data.DecayUs)
                        continue;
                    double u = Math.Clamp(decayElapsed / (double)data.DecayUs, 0.0, 1.0);
                    weight = Math.Max(0.0, 1.0 - data.DecayCurve.Evaluate(u));
                }
            }

            if (weight <= 0.0005)
                continue;

            double factor = Math.Max(0.1, 1.0 + (data.PeakScale - 1.0) * weight);

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

                output.AppendTransform(token.WithRuby, Affine2.ScaleAt(factor, pivot));

                if (data.FlashGlow && data.GlowOpacity > 0 && data.GlowBlurPx > 0)
                {
                    double alpha = Math.Clamp(data.GlowOpacity * weight, 0.0, 1.0);
                    if (alpha > 0.001)
                    {
                        output.SetGlow(token.WithRuby, TextState.Sung,
                            new GlowValue(data.GlowColor, alpha, data.GlowBlurPx, spreadPx: 2.0 * weight));
                    }
                }
            }
        }
    }
}
