using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Shared CPU-only whole-track fastText instance. No network or translation resource access.</summary>
public sealed class FastTextLanguageIdentifier : ILyricsLanguageIdentifier, IDisposable
{
    private const string ManifestResource = "DropSpace.Infrastructure.Lyrics.Manifests.fasttext-lid176-bin-v1.json";
    private const string AdapterVersion = "dropspace-native-abi-v1";
    private const int CacheCapacity = 128;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly Lazy<nint> NativeLibraryHandle = new(LoadNativeLibrary);
    private static string? _loadedNativePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedPrediction> _predictions = new(StringComparer.Ordinal);
    private readonly Manifest _manifest = ReadManifest();
    private readonly string? _suppliedModelPath;
    private FastTextHandle? _model;
    private bool _initializationAttempted;
    private string? _initializationFailure;
    private bool _disposed;
    private sealed record CachedPrediction(LyricsLanguagePrediction Prediction, long Timestamp);
    private sealed record Manifest(string ModelFile, string ResourceName, long Bytes, string Sha256,
        string EngineCommit, string NativePackageVersion, string NativeWrapperCommit, long NativeBytes, string NativeSha256);

    public FastTextLanguageIdentifier() { }
    // Explicit local path is for build compatibility verification, never a runtime download route.
    public FastTextLanguageIdentifier(string modelPath) => _suppliedModelPath = Path.GetFullPath(modelPath);
    public string ModelIdentity => string.Join(':', _manifest.Sha256, _manifest.EngineCommit,
        _manifest.NativePackageVersion, _manifest.NativeWrapperCommit, AdapterVersion, LyricsLanguagePolicy.PreprocessingVersion);
    public int PredictionCount { get; private set; }
    public int ModelLoadCount { get; private set; }
    public string NativeIdentity => "Panlingo.LanguageIdentification.FastText.Native:" +
        _manifest.NativePackageVersion + ":" + _manifest.NativeSha256;
    public string? LoadedModelPath { get; private set; }
    public string? LoadedNativePath => _loadedNativePath;

    static FastTextLanguageIdentifier() => NativeLibrary.SetDllImportResolver(typeof(FastTextLanguageIdentifier).Assembly,
        (name, _, _) => name == "dropspace_fasttext" ? NativeLibraryHandle.Value : 0);

    public async Task<LyricsDocument> PrepareAsync(LyricsDocument document, string targetLanguage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LyricsLanguagePolicy.IsAdmissionCurrent(document, targetLanguage) &&
            document.TranslationAdmission!.OriginalPrediction.ModelIdentity == ModelIdentity &&
            (document.TranslationAdmission.TranslationPrediction is null ||
                document.TranslationAdmission.TranslationPrediction.ModelIdentity == ModelIdentity)) return document;
        var originalSample = LyricsLanguagePolicy.BuildSample(document, LyricsLanguageRole.Original);
        if (originalSample.Length == 0 || LyricsBodyQualityPolicy.Classify(document) != LyricsBodyQuality.Usable)
            return LyricsLanguagePolicy.Prepare(document, targetLanguage, LyricsLanguagePrediction.Unknown("no-lyric-body"));
        var original = await PredictAsync(originalSample, LyricsLanguageRole.Original, cancellationToken).ConfigureAwait(false);
        // Step two ends immediately for target originals. Provider recognition belongs
        // to a distinct role and is performed only after the original is Unknown.
        LyricsLanguagePrediction? translation = null;
        if (!LyricsLanguagePolicy.PredictionMatches(original, originalSample, targetLanguage) &&
            !LyricsLanguagePolicy.HasTrustedTargetProviderTranslation(document, targetLanguage))
        {
            var providerSample = LyricsLanguagePolicy.BuildSample(document, LyricsLanguageRole.ProviderTranslation);
            if (providerSample.Length > 0)
                translation = await PredictAsync(providerSample, LyricsLanguageRole.ProviderTranslation, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = LyricsLanguagePolicy.Prepare(document, targetLanguage, original, translation,
            document.TranslationAdmission?.Generation ?? 0);
        LyricsRequestTrace.Record("whole-track-language", new { result.TranslationAdmission, model = _manifest.Sha256,
            preprocessing = LyricsLanguagePolicy.PreprocessingVersion });
        return result;
    }

    private async Task<LyricsLanguagePrediction> PredictAsync(string sample, LyricsLanguageRole role, CancellationToken token)
    {
        var key = string.Join(':', role, ModelIdentity, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sample))));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = Stopwatch.GetTimestamp();
            foreach (var expired in _predictions.Where(item => Stopwatch.GetElapsedTime(item.Value.Timestamp, now) >= CacheLifetime)
                .Select(item => item.Key).ToArray()) _predictions.Remove(expired);
            if (_predictions.TryGetValue(key, out var cached))
            {
                _predictions[key] = cached with { Timestamp = now };
                return cached.Prediction with { CacheHit = true };
            }
            // Both hash verification/model creation and prediction stay off the UI.
            var prediction = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                InitializeModel(token);
                if (_model is null) return new LyricsLanguagePrediction(null, 0, ModelIdentity: ModelIdentity,
                    Diagnostic: _initializationFailure ?? "model-unavailable");
                var input = Encoding.UTF8.GetBytes(sample);
                nint error = 0;
                var pointer = Native.Predict(_model, input, (nuint)input.Length, 2, 0, ref error);
                PredictionCount++;
                CheckError(error);
                try
                {
                    if (pointer == 0) return LyricsLanguagePrediction.Unknown("empty-prediction") with { ModelIdentity = ModelIdentity };
                    var list = Marshal.PtrToStructure<NativePredictionList>(pointer);
                    if (list.Length is 0 or > 2 || list.Predictions == 0)
                        return LyricsLanguagePrediction.Unknown("invalid-prediction") with { ModelIdentity = ModelIdentity };
                    var top = Marshal.PtrToStructure<NativePrediction>(list.Predictions);
                    var runner = list.Length == 2 ? Marshal.PtrToStructure<NativePrediction>(
                        list.Predictions + Marshal.SizeOf<NativePrediction>()).Score : 0;
                    return new LyricsLanguagePrediction(Marshal.PtrToStringUTF8(top.Label), top.Score, runner, ModelIdentity);
                }
                finally { if (pointer != 0) Native.DestroyPredictions(pointer); }
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (prediction.Diagnostic is null)
            {
                if (_predictions.Count >= CacheCapacity)
                    _predictions.Remove(_predictions.MinBy(item => item.Value.Timestamp).Key);
                _predictions[key] = new(prediction, Stopwatch.GetTimestamp());
            }
            return prediction;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or
            BadImageFormatException or EntryPointNotFoundException or InvalidDataException or NotSupportedException)
        {
            LyricsRequestTrace.Record("whole-track-language-unavailable", new { role = role.ToString(), reason = error.GetType().Name });
            return new(null, 0, ModelIdentity: ModelIdentity, Diagnostic: "language-model-" + error.GetType().Name);
        }
        finally { _gate.Release(); }
    }

    private void InitializeModel(CancellationToken token)
    {
        if (_initializationAttempted) return;
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new NotSupportedException("The bundled language engine is pinned to Windows x64.");
            var modelPath = _suppliedModelPath ?? ExtractBundledModel(token);
            using var file = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != _manifest.Bytes || Convert.ToHexStringLower(SHA256.HashData(file)) != _manifest.Sha256)
                throw new InvalidDataException("Bundled lid.176.bin size or SHA-256 mismatch.");
            token.ThrowIfCancellationRequested();
            file.Position = 0;
            using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: true);
            using var view = mapping.CreateViewAccessor(0, file.Length, MemoryMappedFileAccess.Read);
            var handle = new FastTextHandle(Native.Create());
            if (handle.IsInvalid) { handle.Dispose(); throw new InvalidDataException("fastText model creation failed."); }
            try
            {
                nint error = 0;
                // Explicit LoadModel of verified bin data. Mapping avoids the full
                // managed copies in Panlingo's optional stream/default-ftz wrapper.
                Native.LoadModel(handle, view.SafeMemoryMappedViewHandle.DangerousGetHandle() + checked((int)view.PointerOffset),
                    (nuint)file.Length, ref error);
                CheckError(error);
                _model = handle;
                LoadedModelPath = modelPath;
                ModelLoadCount++;
                _initializationAttempted = true;
            }
            catch { handle.Dispose(); throw; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _initializationAttempted = true;
            _initializationFailure = "language-model-" + error.GetType().Name;
            LyricsRequestTrace.Record("whole-track-language-model-unavailable", new { reason = _initializationFailure });
        }
    }

    private string ExtractBundledModel(CancellationToken token)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DropSpace", "BundledLyricsLanguage", _manifest.Sha256);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Bundled model directory is a reparse point.");
        var modelPath = Path.Combine(directory, _manifest.ModelFile);
        if (File.Exists(modelPath))
        {
            if ((File.GetAttributes(modelPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Bundled model is a reparse point.");
            return modelPath;
        }
        var assembly = Assembly.GetEntryAssembly();
        using var resource = assembly?.GetManifestResourceStream(_manifest.ResourceName)
            ?? throw new InvalidDataException("App is missing its bundled lid.176.bin resource.");
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                long copied = 0;
                int count;
                while ((count = resource.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    copied += count;
                    if (copied > _manifest.Bytes) throw new InvalidDataException("Bundled bin exceeds manifest size.");
                    output.Write(buffer, 0, count);
                }
                if (copied != _manifest.Bytes) throw new InvalidDataException("Bundled bin is incomplete.");
            }
            using (var verified = File.OpenRead(temporary))
                if (Convert.ToHexStringLower(SHA256.HashData(verified)) != _manifest.Sha256)
                    throw new InvalidDataException("Bundled bin resource hash mismatch.");
            File.Move(temporary, modelPath, overwrite: false);
            return modelPath;
        }
        catch (IOException) when (File.Exists(modelPath)) { return modelPath; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Manifest ReadManifest()
    {
        using var resource = typeof(FastTextLanguageIdentifier).Assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidDataException("Missing language identification manifest.");
        return JsonSerializer.Deserialize<Manifest>(resource, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid language identification manifest.");
    }
    private static nint LoadNativeLibrary()
    {
        var handle = NativeLibrary.Load("fasttext", typeof(FastTextLanguageIdentifier).Assembly, null);
        try
        {
            var path = new StringBuilder(32768);
            if (Native.GetModuleFileName(handle, path, path.Capacity) == 0) throw new IOException("Cannot resolve the bundled native engine.");
            var manifest = ReadManifest();
            using var file = new FileStream(path.ToString(), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != manifest.NativeBytes || Convert.ToHexStringLower(SHA256.HashData(file)) != manifest.NativeSha256)
                throw new InvalidDataException("Bundled Windows x64 fastText native asset hash mismatch.");
            _loadedNativePath = path.ToString();
            return handle;
        }
        catch { NativeLibrary.Free(handle); throw; }
    }
    private static void CheckError(nint error)
    {
        if (error == 0) return;
        try { throw new InvalidDataException("fastText native operation failed: " + Marshal.PtrToStringUTF8(error)); }
        finally { Native.DestroyString(error); }
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; _predictions.Clear(); _model?.Dispose(); _model = null; }
        finally { _gate.Release(); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePrediction { public readonly float Score; public readonly nint Label; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePredictionList { public readonly nint Predictions; public readonly ulong Length; }
    private sealed class FastTextHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FastTextHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { Native.Destroy(handle); return true; }
    }
    private static class Native
    {
        [DllImport("dropspace_fasttext", EntryPoint = "create_fasttext", CallingConvention = CallingConvention.Cdecl)] public static extern nint Create();
        [DllImport("dropspace_fasttext", EntryPoint = "destroy_fasttext", CallingConvention = CallingConvention.Cdecl)] public static extern void Destroy(nint handle);
        [DllImport("dropspace_fasttext", EntryPoint = "fasttext_load_model_data", CallingConvention = CallingConvention.Cdecl)] public static extern void LoadModel(FastTextHandle handle, nint data, nuint length, ref nint error);
        [DllImport("dropspace_fasttext", EntryPoint = "fasttext_predict", CallingConvention = CallingConvention.Cdecl)] public static extern nint Predict(FastTextHandle handle, byte[] text, nuint length, int count, float threshold, ref nint error);
        [DllImport("dropspace_fasttext", EntryPoint = "destroy_predictions", CallingConvention = CallingConvention.Cdecl)] public static extern void DestroyPredictions(nint predictions);
        [DllImport("dropspace_fasttext", EntryPoint = "destroy_string", CallingConvention = CallingConvention.Cdecl)] public static extern void DestroyString(nint text);
        [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetModuleFileName(nint module, StringBuilder path, int size);
    }
}
