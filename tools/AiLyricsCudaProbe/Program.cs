using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

if (!OperatingSystem.IsWindows() || args.Length != 3)
    throw new ArgumentException("Usage: AiLyricsCudaProbe <installed App resource assembly> <installed data root> <new evidence directory>");
var assemblyPath = Path.GetFullPath(args[0]);
var installedRoot = Path.GetFullPath(args[1]);
var evidenceRoot = Path.GetFullPath(args[2]);
if (Directory.Exists(evidenceRoot)) throw new IOException("Use a fresh evidence directory to exclude cache hits.");
Directory.CreateDirectory(evidenceRoot);
var assembly = Assembly.LoadFile(assemblyPath); // resources only; no installed App code is executed
using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
using var models = new AiModelPackageService(Path.Combine(installedRoot, "AiLyrics", "Models"));
var runtime = new AiLyricsRuntimePackage(assembly, Path.Combine(installedRoot, "AiLyrics", "Runtime"));
var cuda = new CudaLyricsRuntimePackage(assembly, Path.Combine(installedRoot, "AiLyrics", "Runtime"));
var cache = new LyricsCache(Path.Combine(evidenceRoot, "cache"));
var options = new AiLyricsRuntimeOptions { GpuEnabled = true, Backend = LyricsGpuBackend.Cuda };
using var runner = PersistentPlainLyricsRunner.CreateAutomatic(runtime, cuda, options, loggerFactory.CreateLogger<PersistentPlainLyricsRunner>());
using var backend = new PlainHyLyricsBackend(new PlainHyLyricsCoordinator(new AiLyricsCache(cache)), runner,
    runtime, Path.Combine(evidenceRoot, "staging"), cuda);
using var service = new AiLyricsService(new AppStoragePaths(evidenceRoot), cache, loggerFactory.CreateLogger<AiLyricsService>(),
    models, new PlainHyLyricsPackageResolver(models, runtime, cuda), backend, options, cuda, runner);
var settings = new LyricsSettings { Enabled = true, AiTranslationEnabled = true, AiModelId = AiLyricsModelCatalog.ExperimentalPlain.Id,
    AiLyricsGpuAccelerationEnabled = true, AiLyricsGpuBackend = LyricsGpuBackend.Cuda };
var source = new LyricsDocument([
    new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "The morning sunlight shines through the window, and we will meet beside the river after breakfast.", null, []),
], LyricsProviderKind.LocalLrc);
var query = new LyricsQuery("Beta13 CUDA validation", "DropSpace", "", TimeSpan.FromSeconds(10), Guid.NewGuid().ToString("N"));
var processProperty = typeof(PersistentPlainLyricsRunner).GetProperty("ActiveProcessId", BindingFlags.NonPublic | BindingFlags.Instance)!;
var deviceProperty = typeof(PersistentPlainLyricsRunner).GetProperty("ActiveDevice", BindingFlags.NonPublic | BindingFlags.Instance)!;
var samples = new List<object>();
var processes = new Dictionary<int, object>();
var cudaProcessIds = new HashSet<int>();
using var stopSampling = new CancellationTokenSource();
using var counters = new GpuCounters();
using var jobMemory = new JobMemoryProbe();
Console.WriteLine(JsonSerializer.Serialize(new { phase = "start", CudaCompatible = CudaDriverAvailability.IsCompatible(),
    InstalledCuda = cuda.HasInstalledFiles, ResourceAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
    RuntimeIdentity = runtime.GetManifestCacheIdentity(), CudaIdentity = cuda.GetManifestCacheIdentity() }));
var watch = Stopwatch.StartNew();
var sampleTask = Task.Run(async () =>
{
    while (!stopSampling.IsCancellationRequested)
    {
        var pid = (int?)processProperty.GetValue(runner);
        var observations = counters.Sample(pid);
        if (pid is { } id)
        {
            samples.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, pid, observations, job = jobMemory.Sample(runner, id) });
            try
            {
                using var process = Process.GetProcessById(id);
                if (Path.GetFileName(process.MainModule?.FileName) == CudaLyricsRuntimePackage.ExecutableName) cudaProcessIds.Add(id);
                processes[id] = new { pid = id, image = process.MainModule?.FileName, device = deviceProperty.GetValue(runner),
                    peakWorkingSet = process.PeakWorkingSet64, privateBytes = process.PrivateMemorySize64,
                    modules = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName)
                        .Where(name => name is "cublas64_13.dll" or "cublasLt64_13.dll" or "nvcuda.dll").ToArray() };
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try { await Task.Delay(100, stopSampling.Token); }
        catch (OperationCanceledException) { break; }
    }
});
LyricsDocument? result = null;
string? errorCategory = null;
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
    var publication = await service.TranslateForPublicationAsync(query, source, settings, "zh-Hans", timeout.Token);
    if (!publication.IsCurrent) throw new InvalidOperationException("Translation publication was retired.");
    result = publication.Document;
}
catch (Exception error) { errorCategory = error.GetType().Name; }
finally
{
    await stopSampling.CancelAsync();
    await sampleTask;
}
var status = service.GetExecutionStatus(settings);
var useful = result is not null && LyricsTranslationOutput.HasUsefulLocalTranslation(result, "zh-Hans") &&
    result.Lines.Any(line => line.Secondary?.Any(character => character is >= '\u4e00' and <= '\u9fff') == true);
var cudaTranslation = errorCategory is null && useful && !status.FromCache && status.Last is { Backend: "cuda", UsedCpuFallback: false } &&
    status.Failure is null;
var gpuExecutionObserved = cudaProcessIds.Any(pid => counters.ByProcess.TryGetValue(pid, out var evidence) &&
    evidence.PeakEnginePercent > 0 && evidence.PeakDedicatedBytes >= AiLyricsModelCatalog.ExperimentalPlain.Bytes);
var cudaResult = cudaTranslation && gpuExecutionObserved;
jobMemory.Drain();
var report = new { cudaResult, cudaTranslation, gpuExecutionObserved, errorCategory, elapsedMs = watch.Elapsed.TotalMilliseconds, status, state = service.TranslationState.ToString(),
    translations = result?.Lines.Select(line => line.Secondary), processes = processes.Values, samples, jobEvents = jobMemory.Events,
    gpuCounterError = counters.Error, gpuEvidence = counters.ByProcess, model = AiLyricsModelCatalog.ExperimentalPlain.Id,
    modelSha256 = AiLyricsModelCatalog.ExperimentalPlain.Sha256, callChain = "AiLyricsService -> PlainHyLyricsPackageResolver -> PlainHyLyricsBackend -> PlainHyLyricsCoordinator -> PersistentPlainLyricsRunner.CreateAutomatic" };
await runner.DrainCleanupAsync(CancellationToken.None);
var reportText = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "result.json"), reportText);
Console.WriteLine(reportText);
Environment.ExitCode = cudaResult ? 0 : 2;
