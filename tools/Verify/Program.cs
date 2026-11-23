using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using SkiaSharp;

// 用法：
//   dotnet build -c Release --artifacts-path .artifacts tools/Verify/Verify.csproj
//   dotnet .artifacts/bin/Verify/release/Verify.dll [--root 仓库根] [--output 输出目录] [--only 包目录名,...] [--backend vulkan|cpu] [--no-images]
var options = Options.Parse(args);
string packagesRoot = Path.Combine(options.Root, "packages");
if (!Directory.Exists(packagesRoot))
{
    Console.Error.WriteLine($"找不到效果包目录：{packagesRoot}");
    return 2;
}

string[] diskPackages = [.. Directory.EnumerateDirectories(packagesRoot)
    .Where(dir => File.Exists(Path.Combine(dir, "manifest.json")))
    .Select(dir => Path.GetFileName(dir)!)
    .Order(StringComparer.Ordinal)];

var coveredPackages = Scenarios.All.Select(s => s.Package).ToHashSet(StringComparer.Ordinal);
var missingCoverage = diskPackages.Where(name => !coveredPackages.Contains(name)).ToArray();
if (missingCoverage.Length > 0)
{
    Console.Error.WriteLine($"存在未配置验证场景的包：{string.Join(", ", missingCoverage)}");
    return 2;
}

string background = Path.Combine(AppContext.BaseDirectory, "assets", "color-checkerboard-640x480.png");
string refsDir = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
string cacheDir = Path.Combine(options.Output, ".script-cache");
Directory.CreateDirectory(cacheDir);

var compiler = new ScriptCompiler(refsDir, cacheDir);
using var videos = new FfmpegVideoFrameProvider();
var factory = new ProjectRenderSessionFactory(
    compiler,
    videos,
    renderBackend: options.Backend);

int failures = 0;
var selectedScenarios = Scenarios.All
    .Where(s => options.Only.Count == 0 || options.Only.Contains(s.Package))
    .ToArray();

if (selectedScenarios.Length == 0)
{
    Console.Error.WriteLine("没有匹配任何验证场景。");
    return 2;
}

var loadedPackages = new Dictionary<string, (EffectPackageContents Contents, EffectPackageDefinition Definition)>(StringComparer.Ordinal);

foreach (string packageName in selectedScenarios.Select(s => s.Package).Distinct(StringComparer.Ordinal))
{
    string dir = Path.Combine(packagesRoot, packageName);
    Console.WriteLine($"[校验包] {packageName}");
    try
    {
        var contents = await EffectPackageStorage.ReadSupportedDirectoryAsync(dir);
        var definition = EffectPackageDefinition.Read(contents);

        // 验证 .mkfx 打包与重新读取的往返一致性
        string tempArchive = Path.Combine(cacheDir, packageName + ".verify.mkfx");
        try
        {
            await EffectPackageTransfer.WriteArchiveAsync(tempArchive, contents);
            var reloaded = await EffectPackageTransfer.ReadSupportedArchiveAsync(tempArchive);
            if (reloaded.PackageId != contents.PackageId || reloaded.Revision != contents.Revision)
                throw new InvalidOperationException("归档往返后 PackageId 或 Revision 不一致。");
        }
        finally
        {
            if (File.Exists(tempArchive)) File.Delete(tempArchive);
        }

        // 执行全套正式准入编译与校验器（图片/Shader/图/材质/SVG/C#）
        var compiled = await CompiledEffectPackage.CompileAsync(
            definition,
            compiler,
            new PackageImageValidator(),
            new PackageShaderValidator(),
            new PackageImageGraphValidator(),
            new PackageMaterialValidator(),
            new PackageSvgValidator());

        if (compiled.ControllerArtifact is { } artifact)
        {
            foreach (var diag in artifact.Diagnostics)
            {
                string sev = diag.IsError ? "Error" : "Warning";
                Console.WriteLine($"   编译诊断 [{sev}] {diag.Code} {diag.Path}:{diag.Line}:{diag.Column} {diag.Message}");
            }
        }
        if (!compiled.Succeeded)
            throw new InvalidOperationException("效果包准入或 C# 编译失败。");

        string exportSummary = string.Join(", ", definition.Exports.Select(e => e switch
        {
            EffectControllerDefinition => $"{e.Id}(controller)",
            EffectMaterialDefinition => $"{e.Id}(material)",
            EffectGraphDefinition => $"{e.Id}(graph)",
            _ => e.Id,
        }));
        Console.WriteLine($"   OK  {contents.PackageId}  {contents.Revision[..19]}…  导出: {exportSummary}");
        loadedPackages[packageName] = (contents, definition);
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"!! 包校验失败 {packageName}: {error.GetType().Name}: {error.Message}");
    }
}

foreach (var scenario in selectedScenarios)
{
    if (!loadedPackages.TryGetValue(scenario.Package, out var pkg))
        continue;

    string label = $"{scenario.Package}/{scenario.Variant}";
    try
    {
        var baseline = Fixture.CreateProject(background, scenario.WithAudioAnalysis);
        var project = scenario.Apply(baseline, pkg.Contents);
        Console.WriteLine($"== 渲染 {label} (backend={options.Backend})");
        foreach (bool transparent in new[] { false, true })
        {
            string mode = transparent ? "transparent" : "normal";
            var effect = await Fixture.RenderAsync(factory, compiler, videos, options.Backend, project, scenario.TimesUs, transparent);
            var reference = await Fixture.RenderAsync(factory, compiler, videos, options.Backend, baseline, scenario.TimesUs, transparent);
            long changed = 0;
            for (int index = 0; index < effect.Count; index++)
            {
                var frame = effect[index];
                long diff = Fixture.CountChangedPixels(frame.Pixels, reference[index].Pixels);
                changed += diff;
                if (!options.NoImages)
                {
                    string name = $"{scenario.Variant}-{mode}-{frame.TimeUs / 1000:D5}ms.png";
                    Fixture.WritePng(Path.Combine(options.Output, scenario.Package, name), frame.Pixels);
                }
                string codes = frame.Codes.Count == 0 ? "无" : string.Join(" ", frame.Codes);
                Console.WriteLine($"   {mode,-11} {frame.TimeUs / 1e6,6:F2}s  变化像素 {diff,7}  诊断 {codes}");
                if (frame.HasError)
                    throw new InvalidOperationException($"{mode} {frame.TimeUs}us 出现 Error 诊断：{codes}");
            }
            if (changed == 0)
                throw new InvalidOperationException($"{mode} 输出与无效果基准逐像素相同，效果未生效。");
        }
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"!! 渲染失败 {label}: {error.GetType().Name}: {error.Message}");
    }
}

Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;

internal sealed record Options(string Root, string Output, HashSet<string> Only, string Backend, bool NoImages)
{
    internal static Options Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        bool noImages = false;
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg == "--no-images")
            {
                noImages = true;
                continue;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"无效参数：{arg}。支持 --root、--output、--only、--backend、--no-images。");
            string key = arg[2..];
            if (key is not ("root" or "output" or "only" or "backend") || !values.TryAdd(key, args[++index]))
                throw new ArgumentException($"不支持或重复的参数：--{key}。");
        }

        string root = Path.GetFullPath(values.GetValueOrDefault("root", Directory.GetCurrentDirectory()));
        string output = Path.GetFullPath(values.GetValueOrDefault("output", Path.Combine(root, "out")));
        var only = values.TryGetValue("only", out var text)
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : [];
        string backend = values.GetValueOrDefault("backend", "vulkan");
        return new(root, output, only, backend, noImages);
    }
}

internal sealed record RenderedFrame(long TimeUs, byte[] Pixels, bool HasError, List<string> Codes);

internal static class Fixture
{
    internal const int Width = 1280, Height = 720;
    private static readonly string[] Texts = ["朝の光浴びて", "青い空見上げ", "風の声を聞く", "君と歩く道に", "夢の花が咲く", "明日へ続いて"];

    internal static Project CreateProject(string backgroundPath, bool withAudioAnalysis)
    {
        var lines = Texts.Select((text, row) =>
        {
            long start = 2_000_000L + row * 3_000_000L;
            long end = row == Texts.Length - 1 ? 20_000_000L : start + 3_000_000L;
            var tokens = text.Select((character, index) => LyricToken.Create(
                $"r{row}t{index}",
                character.ToString(),
                new(start + index * 500_000L, index == text.Length - 1 ? end : start + (index + 1) * 500_000L),
                TimingStatus.Edited)).ToArray();
            return LyricLine.Create($"line-{row}", text, new(start, end), TimingStatus.Edited, tokens);
        }).ToArray();

        var project = Project.Create(
            "MKFX sample verification",
            Composition.Create(Width, Height, FrameRate.Fps30, outputDurationUs: 20_000_000),
            LyricsDocument.Create(lines),
            lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = "Noto Sans CJK JP",
                FontSizePx = 104,
                UnsungFill = new() { Color = new(240, 240, 240, 255) },
                SungFill = new() { Color = new(255, 120, 170, 255) },
                Stroke = new() { Color = new(30, 30, 40, 255), WidthPx = 4 },
            },
            background: BackgroundSource.ForImage(backgroundPath, BackgroundFit.Cover));

        if (withAudioAnalysis)
        {
            project = project with
            {
                AudioPath = "synthetic-audio.wav",
                AudioAnalysis = Scenarios.CreateSyntheticAudioAnalysis(20_000_000),
            };
        }
        return project;
    }

    internal static async Task<List<RenderedFrame>> RenderAsync(
        ProjectRenderSessionFactory defaultFactory,
        IScriptCompilationService compiler,
        IVideoFrameProvider videos,
        string backend,
        Project project,
        long[] times,
        bool transparent)
    {
        var snapshot = ProjectSnapshot.FromProject(project);
        var mediaPlan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications);
        await using var media = mediaPlan.Dependencies.IsEmpty
            ? null
            : await MediaVerifyContext.CreateAsync(snapshot, mediaPlan, transparent);
        if (media is not null)
            snapshot = snapshot with { MediaFeatureInput = media.Input };

        var sessionFactory = media is null
            ? defaultFactory
            : new ProjectRenderSessionFactory(
                compiler,
                videos,
                renderBackend: backend,
                mediaBinding: media.Binding);

        var session = await sessionFactory.CreateAsync(snapshot);
        List<RenderedFrame> frames = [];
        try
        {
            foreach (long time in times)
            {
                var request = RenderRequest.At(snapshot, time) with
                {
                    Target = new() { PixelWidth = Width, PixelHeight = Height },
                    Quality = RenderQuality.Final,
                    TransparentBackground = transparent,
                };
                using RenderFrame frame = await session.RenderAsync(request);
                if (frame.Format != PixelFormat.Bgra8888Premultiplied || frame.Stride != Width * 4)
                    throw new InvalidOperationException("帧格式或步长不符。");
                List<string> codes = [];
                codes.AddRange(frame.DiagnosticSnapshot.ReadAll().Select(item =>
                    $"{item.Diagnostic.Diagnostic.Severity}:{item.Diagnostic.Diagnostic.Code}({item.Diagnostic.Diagnostic.Message})"));
                codes.AddRange(frame.ImageEffectDiagnostics.Select(item => "image:" + item.Code));
                codes.AddRange(frame.MaterialDiagnostics.Select(item => "material:" + item.Code));
                frames.Add(new(time, frame.PixelMemory.ToArray(), frame.DiagnosticSnapshot.HasError, [.. codes.Distinct(StringComparer.Ordinal)]));
            }
        }
        finally
        {
            await session.DisposeAsync();
        }
        return frames;
    }

    internal static long CountChangedPixels(byte[] left, byte[] right)
    {
        long count = 0;
        for (int offset = 0; offset < left.Length; offset += 4)
            if (BitConverter.ToUInt32(left, offset) != BitConverter.ToUInt32(right, offset)) count++;
        return count;
    }

    internal static void WritePng(string path, byte[] pixels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());
    }
}

internal sealed class MediaVerifyContext : IAsyncDisposable
{
    private readonly MediaFeatureSampler _sampler;
    private readonly IMediaFeatureTrackReader? _reader;

    private MediaVerifyContext(MediaFeatureInput input, MediaFeatureSampler sampler, IMediaFeatureTrackReader? reader)
    {
        Input = input;
        _sampler = sampler;
        _reader = reader;
        Binding = new(sampler);
    }

    internal MediaFeatureInput Input { get; }
    internal ProjectMediaFeatureBinding Binding { get; }

    internal static async Task<MediaVerifyContext> CreateAsync(ProjectSnapshot snapshot, ProjectMediaDependencyPlan plan, bool transparent)
    {
        var writer = new MediaAbIdentityWriter();
        var comp = snapshot.Composition;
        var mapping = MediaMapping.Create(
            comp.CanvasWidth,
            comp.CanvasHeight,
            snapshot.Background.Fit,
            snapshot.Background.Playback,
            LinearColor(comp.BackgroundColor),
            transparent ? MediaOutputMode.Transparent : MediaOutputMode.Normal);
        var budget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes);

        if (transparent)
        {
            var profile = CreateProfileBundle(writer, Digest("verify.media.decoder.v1"), Digest("verify.media.color.v1"));
            var context = MediaInputContext.Create(profile, mapping, plan.QueryCatalogDigest, plan.FixedDependencyDigest);
            var noMedia = MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.TransparentOutput, Digest("verify.transparent"));
            return new(noMedia, new MediaFeatureSampler(noMedia, plan, null, budget), null);
        }

        if (snapshot.Background.Kind == BackgroundKind.Image && File.Exists(snapshot.Background.Path))
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(snapshot.Background.Path);
            var content = new MediaFileContent(
                new MediaDigest(Convert.ToHexStringLower(SHA256.HashData(fileBytes))),
                fileBytes.LongLength);

            using var decoded = SKBitmap.Decode(snapshot.Background.Path)
                ?? throw new InvalidOperationException("无法解码验证背景图像：" + snapshot.Background.Path);
            using var rgbaBitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (!decoded.CopyTo(rgbaBitmap, SKColorType.Rgba8888))
                throw new InvalidOperationException("无法转换验证背景像素格式。");

            float[] linearPixels = ToLinearPremulRgba(rgbaBitmap);
            var rawColor = new MediaColorInterpretation(
                MediaTransfer.Srgb,
                MediaMatrix.Rgb,
                MediaRange.Full,
                MediaAlpha.Straight,
                MediaChromaLocation.None,
                MediaNativePixelFormat.Rgba32,
                fromTrustedMissingTagOverride: false,
                Digest("verify.color.authority"),
                default);
            var color = new MediaColorInterpretation(
                rawColor.Transfer,
                rawColor.Matrix,
                rawColor.Range,
                rawColor.Alpha,
                rawColor.ChromaLocation,
                rawColor.NativePixelFormat,
                rawColor.FromTrustedMissingTagOverride,
                rawColor.AuthorityDigest,
                writer.ComputeColorDigest(rawColor));

            var rawTool = new MediaToolIdentity(Digest("verify.exe"), Digest("verify.dep"), Digest("verify.pipe"), default);
            var tool = new MediaToolIdentity(rawTool.Executables, rawTool.Dependencies, rawTool.NormalizedPipeline, writer.ComputeToolDigest(rawTool));

            var profile = CreateProfileBundle(writer, tool.Digest, color.Digest);
            var context = MediaInputContext.Create(profile, mapping, plan.QueryCatalogDigest, plan.FixedDependencyDigest);

            var rawDesc = new MediaFilePresentationDescriptor(
                0,
                rgbaBitmap.Width,
                rgbaBitmap.Height,
                rgbaBitmap.Width,
                rgbaBitmap.Height,
                null,
                1,
                1,
                MediaQuarterTurn.None,
                false,
                color,
                default);
            var desc = new MediaFilePresentationDescriptor(
                rawDesc.StreamIndex,
                rawDesc.EncodedWidth,
                rawDesc.EncodedHeight,
                rawDesc.CanonicalWidth,
                rawDesc.CanonicalHeight,
                rawDesc.StreamTimeBase,
                rawDesc.SarNumerator,
                rawDesc.SarDenominator,
                rawDesc.Rotation,
                rawDesc.MirrorHorizontallyBeforeRotation,
                rawDesc.Color,
                writer.ComputeDescriptorDigest(rawDesc));

            var rawSource = new MediaFeatureFileSource(default, MediaSourceKind.Still, content, desc, tool, profile);
            var source = new MediaFeatureFileSource(writer.ComputeSourceKey(rawSource), rawSource.Kind, rawSource.Content, rawSource.Presentation, rawSource.Tool, rawSource.Profile);

            byte[] intervalBytes = new byte[MediaTrackLayoutV1.IntervalBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(intervalBytes.AsSpan(48), (uint)PresentationEndAuthority.Timeless);
            var indexDigest = new MediaDigest(Convert.ToHexStringLower(SHA256.HashData(intervalBytes)));
            var summary = new PresentationIndexSummary(source.Key, profile.Digest, desc.Digest, indexDigest, 1, null);

            var codec = new MediaFeatureTrackCodec(new MediaMomentKernel(), new MediaAbValueValidator());
            var buildBudget = new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumBuilderWorkingBytes);
            await using var scratch = new InMemoryTrackScratch(8_000_000);
            await using var builder = codec.CreateBuilder(source, scratch, buildBudget);
            await builder.OnFrameAsync(new PresentationFrameBorrow(
                source.Key,
                profile.Digest,
                new PresentationFrameObservation(new PresentationFrameId(0), null, rgbaBitmap.Width, rgbaBitmap.Height, color.Digest),
                linearPixels), CancellationToken.None);
            await builder.OnIntervalAsync(new PresentationInterval.Timeless(new PresentationFrameId(0)), CancellationToken.None);
            await using var writeSource = await builder.CompleteAsync(summary, CancellationToken.None);
            using var trackBuffer = new MemoryStream();
            var artifact = await codec.WriteCanonicalAsync(writeSource, trackBuffer, buildBudget, CancellationToken.None);
            byte[] trackBytes = trackBuffer.ToArray();
            var reader = await codec.OpenValidatedAsync(new MemoryStream(trackBytes, writable: false), source, artifact, budget, CancellationToken.None);

            var available = MediaFeatureInput.CreateAvailableFile(context, source, artifact);
            return new(available, new MediaFeatureSampler(available, plan, reader, budget), reader);
        }

        var solidProfile = CreateProfileBundle(writer, Digest("verify.media.decoder.v1"), Digest("verify.media.color.v1"));
        var solidInput = MediaFeatureInput.CreateAvailableSolid(
            MediaInputContext.Create(solidProfile, MediaMapping.CreateSolid(comp.CanvasWidth, comp.CanvasHeight), plan.QueryCatalogDigest, plan.FixedDependencyDigest),
            MediaFeatureSolidSource.Create(solidProfile, LinearColor(comp.BackgroundColor)));
        return new(solidInput, new MediaFeatureSampler(solidInput, plan, null, budget), null);
    }

    private static MediaProfileBundle CreateProfileBundle(MediaAbIdentityWriter writer, MediaDigest decoderIdentity, MediaDigest colorInterpretation)
    {
        var raw = new MediaProfileBundle(
            MediaProfilesV1.Presentation,
            MediaProfilesV1.DecoderPipeline,
            MediaProfilesV1.Color,
            MediaProfilesV1.Statistics,
            MediaProfilesV1.Quantization,
            MediaProfilesV1.Codec,
            MediaProfilesV1.Query,
            MediaProfilesV1.Gpu,
            decoderIdentity,
            colorInterpretation,
            default);
        return new MediaProfileBundle(
            raw.Presentation,
            raw.DecoderPipeline,
            raw.Color,
            raw.Statistics,
            raw.Quantization,
            raw.Codec,
            raw.Query,
            raw.Gpu,
            raw.DecoderIdentity,
            raw.ColorInterpretation,
            writer.ComputeProfileDigest(raw));
    }

    public async ValueTask DisposeAsync()
    {
        try { _sampler.Dispose(); }
        finally
        {
            if (_reader is not null)
                await _reader.DisposeAsync();
        }
    }

    private static float[] ToLinearPremulRgba(SKBitmap bitmap)
    {
        int count = bitmap.Width * bitmap.Height;
        float[] result = new float[count * 4];
        ReadOnlySpan<byte> bytes = bitmap.GetPixelSpan();
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            double a = bytes[o + 3] / 255.0;
            float r = (float)Math.Min(a, SrgbToLinear(bytes[o] / 255.0) * a);
            float g = (float)Math.Min(a, SrgbToLinear(bytes[o + 1] / 255.0) * a);
            float b = (float)Math.Min(a, SrgbToLinear(bytes[o + 2] / 255.0) * a);
            result[o] = r <= 0f ? 0f : r;
            result[o + 1] = g <= 0f ? 0f : g;
            result[o + 2] = b <= 0f ? 0f : b;
            result[o + 3] = a <= 0.0 ? 0f : (float)a;
        }
        return result;
    }

    private static LinearPremulRgba LinearColor(RgbaColor color)
    {
        double a = color.Alpha / 255.0;
        double r = Math.Min(a, SrgbToLinear(color.Red / 255.0) * a);
        double g = Math.Min(a, SrgbToLinear(color.Green / 255.0) * a);
        double b = Math.Min(a, SrgbToLinear(color.Blue / 255.0) * a);
        return new(r <= 0 ? 0 : r, g <= 0 ? 0 : g, b <= 0 ? 0 : b, a <= 0 ? 0 : a);
    }

    private static double SrgbToLinear(double encoded)
        => encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    private static MediaDigest Digest(string text)
        => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));

    private sealed class InMemoryTrackScratch(long budget) : IMediaTrackScratchReservation
    {
        private readonly List<MemoryStream> _streams = [];
        public long ReservedByteBudget { get; } = budget;

        public ValueTask<Stream> OpenSectionAsync(MediaTrackSection section, long maximumLength, CancellationToken cancellationToken)
        {
            var ms = new MemoryStream();
            _streams.Add(ms);
            return new ValueTask<Stream>(ms);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var s in _streams) s.Dispose();
            _streams.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
