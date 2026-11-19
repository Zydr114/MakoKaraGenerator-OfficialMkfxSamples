using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record BeatPulseData(
    double MaxBeatScale,
    double GlowBaseBlurPx,
    double GlowPulseBlurPx,
    double GlowOpacity,
    double FallbackBpm,
    bool ShowEqualizer,
    int BarCount,
    double BarWidthPx,
    double BarMaxHeightPx,
    double BarOffsetPx,
    ColorRgba PrimaryNeon,
    ColorRgba AccentNeon) : PreparedEffect;

/// <summary>音律脉冲霓虹：由真实音频低频／脉冲特征（无音频分析时按备用BPM律动）驱动当前唱字的脉动缩放、双色霓虹辉光与字后均衡器律动条。</summary>
public sealed class BeatPulseNeon : IPureKaraokeEffect<BeatPulseData>, IPureVisualEffect<BeatPulseData>
{
    private readonly record struct AudioState(double Bass, double Pulse, double Phase);

    public BeatPulseData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        return new BeatPulseData(
            p.GetNumber("maxBeatScale"),
            p.GetNumber("glowBaseBlurPx"),
            p.GetNumber("glowPulseBlurPx"),
            p.GetNumber("glowOpacity"),
            p.GetNumber("fallbackBpm"),
            p.GetBoolean("showEqualizer"),
            (int)p.GetInteger("barCount"),
            p.GetNumber("barWidthPx"),
            p.GetNumber("barMaxHeightPx"),
            p.GetNumber("barOffsetPx"),
            p.GetColor("primaryNeon"),
            p.GetColor("accentNeon"))
        {
            VisualOutputs =
            [
                new VisualOutputDeclaration(new OutputId("spectrum"), VisualPrimitiveKind.Segments, OverlayLayer.BehindText),
            ],
        };
    }

    public void Evaluate(in TextFrame frame, BeatPulseData data, TextOutput output)
    {
        AudioState audioState = SampleAudio(frame.Audio, frame.TimeUs, data.FallbackBpm);
        double energy = Math.Clamp(0.55 * audioState.Pulse + 0.45 * audioState.Bass, 0.0, 1.0);
        ColorRgba neon = LerpColor(data.PrimaryNeon, data.AccentNeon, 0.5 + 0.5 * Math.Sin(audioState.Phase * 6.2831853), 1.0);

        foreach (var token in frame.Presentations.TokensInBinding)
        {
            if (!token.Performance.IsCurrent)
                continue;

            foreach (var geometry in frame.Geometry)
            {
                if (geometry.WithRuby != token.WithRuby || !geometry.BaseGeometry.CanTransformIndependently)
                    continue;

                double scale = Math.Max(0.1, 1.0 + (data.MaxBeatScale - 1.0) * energy);
                if (Math.Abs(scale - 1.0) > 0.0005)
                {
                    output.AppendTransform(token.WithRuby,
                        Affine2.ScaleAt(scale, geometry.BaseGeometry.AdvanceBounds.Center));
                }

                double blur = Math.Max(0.0, data.GlowBaseBlurPx + data.GlowPulseBlurPx * energy);
                double alpha = Math.Clamp(data.GlowOpacity * (0.45 + 0.55 * energy), 0.0, 1.0);
                if (blur > 0.1 && alpha > 0.005)
                {
                    output.SetGlow(token.WithRuby, TextState.Sung,
                        new GlowValue(neon, alpha, blur, spreadPx: 1.5 * energy));
                }
            }
        }
    }

    public void EvaluateVisuals(in VisualFrame frame, BeatPulseData d, VisualOutput output)
    {
        var segments = ImmutableArray.CreateBuilder<Segment>();
        if (d.ShowEqualizer && d.BarCount > 0 && d.BarWidthPx > 0 && d.BarMaxHeightPx > 1)
        {
            var lines = frame.FinalGeometry.Lines;
            foreach (var line in frame.FinalGeometry.Presentations.LinesInBinding)
            {
                double lineWeight = line.DisplayPhase == DisplayPhase.Performing ? 1.0 : 0.28;
                double lineAlpha = Math.Clamp(line.EnterProgress * lineWeight, 0.0, 1.0);
                if (lineAlpha <= 0.01)
                    continue;

                LineGeometrySample? geom = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Target == line.Target)
                    {
                        geom = lines[i];
                        break;
                    }
                }
                if (geom is null)
                    continue;

                Rect2 bounds = geom.BaseGeometry.AdvanceBounds;
                if (bounds.Width <= 4)
                    continue;

                double baseY = bounds.Bottom + d.BarOffsetPx;
                for (int i = 0; i < d.BarCount; i++)
                {
                    double u = (i + 0.5) / d.BarCount;
                    long offsetUs = (long)((u - 0.5) * 440_000.0);
                    long sampleTime = Math.Max(0L, frame.TimeUs + offsetUs);

                    AudioState sample = SampleAudio(frame.Audio, sampleTime, d.FallbackBpm);
                    double barEnergy = Math.Clamp(0.6 * sample.Pulse + 0.4 * sample.Bass, 0.0, 1.0);
                    double arch = 0.5 + 0.5 * Math.Sin(u * Math.PI);
                    double height = Math.Max(2.0, d.BarMaxHeightPx * (0.12 + 0.88 * barEnergy * arch));

                    double localX = bounds.Left + u * bounds.Width;
                    Point2 worldBottom = geom.BaseGeometry.MapPoint(new Point2(localX, baseY));
                    Point2 worldTop = geom.BaseGeometry.MapPoint(new Point2(localX, baseY - height));

                    ColorRgba color = LerpColor(d.PrimaryNeon, d.AccentNeon, u,
                        Math.Clamp(lineAlpha * (0.45 + 0.55 * barEnergy) * d.PrimaryNeon.Alpha, 0.0, 1.0));
                    segments.Add(new Segment(worldBottom, worldTop, d.BarWidthPx, color));
                }
            }
        }

        output.DrawSegments(new OutputId("spectrum"), segments.ToImmutable(), OverlayLayer.BehindText);
    }

    private static AudioState SampleAudio(AudioFeatures audio, long timeUs, double fallbackBpm)
    {
        long clamped = Math.Max(0L, timeUs);
        var sample = audio.GetAt(clamped);
        if (sample.Availability == AudioAvailability.Available && sample.Values is { } values)
        {
            return new AudioState(values.BassLevel, values.Pulse, values.BeatPhase ?? ((clamped / 1_000_000d) % 1.0));
        }

        if (fallbackBpm > 0)
        {
            double beats = (clamped / 1_000_000d) * (fallbackBpm / 60.0);
            double phase = beats - Math.Floor(beats);
            double impulse = Math.Pow(1.0 - phase, 2.4);
            return new AudioState(0.25 + 0.65 * impulse, 0.20 + 0.75 * impulse, phase);
        }

        return new AudioState(0, 0, 0);
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
}
