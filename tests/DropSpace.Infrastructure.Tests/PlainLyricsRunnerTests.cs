using System.Diagnostics;
using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

/// <summary>Actual OS process fixtures verify launch policy and ownership. They do not load an AI model.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PlainLyricsRunnerTests
{
    private const string PlainModelSha256 = "5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4";

    [TestMethod]
    public async Task PlainArgumentsMatchFrozenProfileAndPromptIsClosedUtf8BeforeLaunch()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        using var cancel = new CancellationTokenSource();
        const string prompt = "Translate into Chinese without explanation:\n\n夜の空と星 / 달빛 / a quiet night\n";
        var release = Path.Combine(fixture.Root, "release");
        var environmentPath = Path.Combine(fixture.Root, "environment");
        var capturedPrompt = Path.Combine(fixture.Root, "captured-prompt");
        fixture.WriteRuntime(fixture.CaptureArguments +
            $"cat \"$4\" > {Quote(capturedPrompt)}\nenv > {Quote(environmentPath)}\n: > {Quote(fixture.Ready)}\n" +
            $"while [ ! -f {Quote(release)} ]; do /bin/sleep 0.02; done\n" +
            "printf 'A quiet night [end of text]\\n'\n", new { kind = "completion", argumentsPath = fixture.Arguments,
                capturedPromptPath = capturedPrompt, environmentPath, readyPath = fixture.Ready, releasePath = release,
                lateOutput = "A quiet night [end of text]\n" });
        var ambient = new Dictionary<string, string>
        {
            ["LLAMA_ARG_MODEL"] = "unexpected-model",
            ["llama_TEST_PROFILE"] = "unexpected-profile",
            ["GGML_TEST_DEVICE"] = "unexpected-device",
            ["ggml_TEST_BACKEND"] = "unexpected-backend",
            ["OMP_NUM_THREADS"] = "99",
            ["OMP_THREAD_LIMIT"] = "99",
        };
        var original = ambient.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        Task<string>? running = null;
        try
        {
            foreach (var (key, value) in ambient) Environment.SetEnvironmentVariable(key, value);
            running = runner.RunPlainAsync(fixture.Executable, fixture.Model, prompt, fixture.Staging,
                cancel.Token, PlainModelSha256.ToUpperInvariant());
            await WaitForFileAsync(fixture.Ready, running);
            var arguments = await File.ReadAllLinesAsync(fixture.Arguments);
            var promptPath = arguments[3];
            CollectionAssert.AreEqual(new[]
            {
                "-m", Path.GetFullPath(fixture.Model), "-f", promptPath, "--offline", "--perf", "--no-escape", "--jinja",
                "--single-turn", "--load-mode", "none", "--no-display-prompt", "--simple-io", "--no-context-shift", "--reasoning", "off",
                "-t", "4", "-tb", "4", "-ngl", "0", "-c", "4096", "-n", "2048", "--seed", "42", "--temp", "0.1", "--top-k", "20",
                "--top-p", "0.8", "--min-p", "0.05", "--repeat-penalty", "1.0", "--frequency-penalty", "0", "--presence-penalty", "0",
            }, arguments);
            CollectionAssert.AreEqual(LlamaCompletionRunner.BuildPlainArguments(fixture.Model, promptPath).ToArray(), arguments,
                "Evidence capture and native launch must use the same canonical argument builder.");
            Assert.AreEqual(fixture.Staging, Path.GetDirectoryName(promptPath));
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(promptPath));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(prompt), await File.ReadAllBytesAsync(capturedPrompt));
            // An exclusive managed read also catches a writer left open in this process.
            // The Windows child also opened the staged prompt with FileShare.None.
            await using (var stream = new FileStream(promptPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(prompt), bytes, "Prompt bytes must be exact UTF-8 without a BOM.");
            }
            var environment = (await File.ReadAllLinesAsync(environmentPath))
                .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            Assert.IsFalse(environment.Keys.Any(key => key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual("4", environment["OMP_NUM_THREADS"]);
            Assert.AreEqual("4", environment["OMP_THREAD_LIMIT"]);
            await File.WriteAllTextAsync(release, string.Empty);
            Assert.AreEqual("A quiet night", await running);
            await runner.DrainCleanupAsync(CancellationToken.None);
            Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
        }
        finally
        {
            foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value);
            cancel.Cancel();
            if (running is not null)
                try { await running; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task PlainProfileRejectsEveryUnverifiedOrDifferentModelBeforeStaging()
    {
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        foreach (var hash in new[] { null, "", "untrusted", PlainModelSha256[..^1], " " + PlainModelSha256,
            AiLyricsModelCatalog.Compact.Sha256, AiLyricsModelCatalog.Standard.Sha256,
            "061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a" })
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunPlainAsync(
                fixture.Executable, fixture.Model, "source", fixture.Staging, CancellationToken.None, hash!));
            Assert.IsFalse(Directory.Exists(fixture.Staging));
        }
        Assert.AreEqual(3L * 1024 * 1024 * 1024, LlamaCompletionRunner.MemoryBudgetFor(PlainModelSha256));
        Assert.AreEqual(3L * 1024 * 1024 * 1024, LlamaCompletionRunner.MemoryBudgetFor(PlainModelSha256.ToUpperInvariant()));
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments(PlainModelSha256));
    }

    [TestMethod]
    public async Task PlainCancellationWaitsForActualExitAndGateSupportsRepeatedPlainAndLegacyReuse()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        using var cancel = new CancellationTokenSource();
        var pidPath = Path.Combine(fixture.Root, "process-id");
        fixture.WriteRuntime(fixture.CaptureArguments + $"echo $$ > {Quote(pidPath)}\n" +
            $"printf 'partial output\\n'\n: > {Quote(fixture.Ready)}\nexec /bin/sleep 60\n", new { kind = "completion",
                argumentsPath = fixture.Arguments, pidPath, output = "partial output\n", readyPath = fixture.Ready, delayMilliseconds = 60_000 });
        var running = runner.RunPlainAsync(fixture.Executable, fixture.Model, "source", fixture.Staging, cancel.Token, PlainModelSha256);
        try
        {
            await WaitForFileAsync(fixture.Ready, running);
            using var observed = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture));
            var arguments = await File.ReadAllLinesAsync(fixture.Arguments);
            Assert.IsTrue(File.Exists(arguments[3]), "The active process must retain its staged prompt.");
            using var queuedCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var queuedStaging = Path.Combine(fixture.Root, "queued-prompts");
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(fixture.Executable, fixture.Model,
                "queued legacy", queuedStaging, queuedCancel.Token));
            Assert.IsFalse(Directory.Exists(queuedStaging), "Legacy and plain calls must share the gate before staging or launch.");
            Assert.IsFalse(observed.HasExited);
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => running);
            Assert.IsTrue(observed.HasExited, "Cancellation may not hand ownership back before actual process exit.");
            await runner.DrainCleanupAsync(CancellationToken.None);
            Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
            fixture.WriteRuntime("printf 'translated [end of text]\\n'\n", new { kind = "completion", output = "translated [end of text]\n" });
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.AreEqual("translated", await runner.RunPlainAsync(fixture.Executable, fixture.Model, "source",
                    fixture.Staging, CancellationToken.None, PlainModelSha256));
                await runner.DrainCleanupAsync(CancellationToken.None);
                Assert.AreEqual("translated", await runner.RunAsync(fixture.Executable, fixture.Model, "source",
                    fixture.Staging, CancellationToken.None));
                await runner.DrainCleanupAsync(CancellationToken.None);
                Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
            }
        }
        finally
        {
            cancel.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task NonzeroExitDrainsStderrRemovesPromptAndAllowsReuse()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        fixture.WriteRuntime("printf '%070000d' 0 >&2\nprintf 'unusable output'\nexit 27\n",
            new { kind = "completion", stderrBytes = 70_000, output = "unusable output", exitCode = 27 });
        var error = await Assert.ThrowsExactlyAsync<LocalInferenceExecutionException>(() => runner.RunPlainAsync(
            fixture.Executable, fixture.Model, "private source", fixture.Staging, CancellationToken.None, PlainModelSha256));
        Assert.AreEqual(27, error.ExitCode);
        Assert.AreEqual("Local inference exited with code 27.", error.Message);
        await runner.DrainCleanupAsync(CancellationToken.None);
        Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
        fixture.WriteRuntime("printf 'next response\\n'\n", new { kind = "completion", output = "next response\n" });
        Assert.AreEqual("next response", await runner.RunPlainAsync(fixture.Executable, fixture.Model, "source",
            fixture.Staging, CancellationToken.None, PlainModelSha256));
        await runner.DrainCleanupAsync(CancellationToken.None);
        Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
    }

    [TestMethod]
    public async Task OutputBudgetFailureKillsChildAndAllowsDrainAndReuse()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        fixture.WriteRuntime("printf '%070000d' 0\nexec /bin/sleep 60\n",
            new { kind = "completion", stdoutBytes = 70_000, delayMilliseconds = 60_000 });
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunPlainAsync(
            fixture.Executable, fixture.Model, "source", fixture.Staging, CancellationToken.None, PlainModelSha256));
        Assert.AreEqual("Local inference output exceeds budget.", error.Message);
        await runner.DrainCleanupAsync(CancellationToken.None);
        Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
        fixture.WriteRuntime("printf 'next response\\n'\n", new { kind = "completion", output = "next response\n" });
        Assert.AreEqual("next response", await runner.RunPlainAsync(fixture.Executable, fixture.Model, "source",
            fixture.Staging, CancellationToken.None, PlainModelSha256));
        await runner.DrainCleanupAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task PlainPromptBudgetAndPrecancelledCallRejectBeforeStaging()
    {
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunPlainAsync(fixture.Executable, fixture.Model,
            new string('夜', 30_000), fixture.Staging, CancellationToken.None, PlainModelSha256));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunPlainAsync(fixture.Executable, fixture.Model,
            "source", fixture.Staging, cancel.Token, PlainModelSha256));
        Assert.IsFalse(Directory.Exists(fixture.Staging));
        await runner.DrainCleanupAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LegacyJsonSchemaAndCompactCompatibilityArgumentsAreUnchanged()
    {
        WindowsProcessFixture.RequireAvailable();
        using var fixture = new Fixture();
        using var runner = new LlamaCompletionRunner();
        fixture.WriteRuntime(fixture.CaptureArguments + "printf '[] [end of text]\\n'\n",
            new { kind = "completion", argumentsPath = fixture.Arguments, output = "[] [end of text]\n" });
        Assert.AreEqual("[]", await runner.RunAsync(fixture.Executable, fixture.Model, "legacy prompt", fixture.Staging,
            CancellationToken.None, AiLyricsModelCatalog.Compact.Sha256, [0, 2]));
        var arguments = await File.ReadAllLinesAsync(fixture.Arguments);
        CollectionAssert.AreEqual(new[]
        {
            "-m", Path.GetFullPath(fixture.Model), "-f", arguments[3], "--offline", "--no-escape", "--jinja",
            "--single-turn", "--load-mode", "none", "--no-display-prompt", "--simple-io", "--no-context-shift", "--reasoning", "off",
            "-t", "4", "-tb", "4", "-ngl", "0", "-c", "4096", "-n", "2048", "--temp", "0.1", "-j",
            LyricsTranslationPrompt.OutputSchema([0, 2]), "--override-kv", "tokenizer.ggml.eos_token_id=int:120020",
        }, arguments);
        await runner.DrainCleanupAsync(CancellationToken.None);
        Assert.HasCount(0, Directory.GetFiles(fixture.Staging));
    }

    private static async Task WaitForFileAsync(string path, Task running)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            if (running.IsCompleted)
            {
                await running;
                Assert.Fail("The runtime exited before the fixture was ready.");
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "DropSpace plain-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string Executable => Path.Combine(Root, OperatingSystem.IsWindows() ? "fake runtime.exe" : "fake runtime");
        public string Model => Path.Combine(Root, "Hy model.gguf");
        public string Staging => Path.Combine(Root, "prompts");
        public string Arguments => Path.Combine(Root, "arguments");
        public string Ready => Path.Combine(Root, "ready");
        public string CaptureArguments => $"printf '%s\\n' \"$@\" > {Quote(Arguments)}\n";

        public void WriteRuntime(string body, object windowsScenario)
        {
            if (OperatingSystem.IsWindows()) { WindowsProcessFixture.Write(Executable, windowsScenario); return; }
            File.WriteAllText(Executable, "#!/bin/sh\nset -eu\n" + body, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
