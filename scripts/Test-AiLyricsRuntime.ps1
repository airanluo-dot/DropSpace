param([string]$RuntimeDirectory = '', [string]$ModelPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native AI runtime verification requires Windows.' }
if (-not $RuntimeDirectory) { $RuntimeDirectory = Join-Path $PSScriptRoot '../artifacts/ai-runtime/win-x64' }
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$manifest = Get-Content (Join-Path $RuntimeDirectory 'runtime-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.runtimeId -cne 'llama-cpp-v0.5.0-cpu-win-x64' -or
    $manifest.sourceRepository -cne 'https://github.com/ggml-org/llama.cpp' -or
    $manifest.sourceCommit -cne '7fe450e19305b828c199d602c23a8337aaa1f03b' -or
    $manifest.executable -cne 'llama-completion.exe') { throw 'Unexpected runtime provenance.' }
if (-not $manifest.build.cpuOnly -or $manifest.build.sharedLibraries -or $manifest.build.dynamicBackends -or
    $manifest.build.server -or $manifest.build.subprocess -or $manifest.build.openssl -or $manifest.build.openmp) {
    throw 'Unexpected native runtime build features.'
}
if (@(Get-ChildItem $RuntimeDirectory -Directory).Count -ne 0) { throw 'Unexpected runtime payload subdirectory.' }
$names = @(Get-ChildItem $RuntimeDirectory -File | ForEach-Object Name | Sort-Object)
$expected = @('LICENSE-llama.cpp', 'llama-completion.exe', 'llama-completion-avx2.exe', 'llama-tokenize.exe', 'runtime-manifest.json') | Sort-Object
if (Compare-Object $names $expected) { throw 'The runtime payload contains missing or unexpected files.' }
if ($manifest.avx2.executable -cne 'llama-completion-avx2.exe') { throw 'Unexpected optimized runtime executable.' }
if ($manifest.tokenizer.executable -cne 'llama-tokenize.exe') { throw 'Unexpected tokenizer executable.' }
foreach ($variant in @($manifest, $manifest.avx2, $manifest.tokenizer)) {
    $exe = Join-Path $RuntimeDirectory $variant.executable
    if ((Get-Item $exe).Length -ne $variant.bytes -or (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $variant.sha256) {
        throw 'Native runtime size/SHA256 verification failed.'
    }
    $stream = [IO.File]::OpenRead($exe)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Not a Windows executable.' }
        $stream.Position = 0x3c
        $stream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Runtime is not a native Windows x64 PE.' }
    } finally { $reader.Dispose() }
}
$exe = Join-Path $RuntimeDirectory 'llama-completion.exe'
$version = & $exe --version 2>&1
if ($LASTEXITCODE -ne 0 -or ($version -join "`n") -notmatch '0\.5\.0') { throw 'Native runtime version smoke failed.' }

if ($ModelPath) {
    if (-not (Test-Path $ModelPath -PathType Leaf)) { throw 'Native smoke model is missing.' }
    $prompt = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-ai-prompt-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        # Written and closed before native std::ifstream reads it. Never echo generated song text.
        [IO.File]::WriteAllText($prompt, 'Return exactly [{"id":0,"text":"hello"}].', [Text.UTF8Encoding]::new($false))
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($arg in @('-m', [IO.Path]::GetFullPath($ModelPath), '-f', $prompt, '--offline', '--single-turn', '--jinja', '--load-mode', 'none', '--no-display-prompt', '--simple-io', '--reasoning', 'off', '-t', '4', '-tb', '4', '-ngl', '0', '-c', '4096', '-n', '64', '--temp', '0', '-j', '{"type":"array","items":{"type":"object","properties":{"id":{"type":"integer"},"text":{"type":"string"}},"required":["id","text"],"additionalProperties":false}}')) {
            $start.ArgumentList.Add($arg)
        }
        foreach ($key in @($start.Environment.Keys)) { if ($key -match '^(LLAMA_|GGML_)') { $start.Environment.Remove($key) | Out-Null } }
        $child = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $child.StandardOutput.ReadToEndAsync()
            $stderr = $child.StandardError.ReadToEndAsync()
            if (-not $child.WaitForExit(120000)) { $child.Kill($true); throw 'Native model/prompt smoke timed out.' }
            if ($child.ExitCode -ne 0) { throw 'Native model/prompt smoke failed.' }
            $text = $stdout.GetAwaiter().GetResult().Trim() -replace '\s*\[end of text\]\s*$', ''
            $null = $stderr.GetAwaiter().GetResult()
            $result = $text | ConvertFrom-Json
            if (@($result).Count -ne 1 -or $result[0].id -ne 0 -or -not $result[0].text) { throw 'Native model/prompt smoke returned an invalid result.' }
        } finally { $child.Dispose() }
    } finally { Remove-Item $prompt -Force -ErrorAction SilentlyContinue }
    Write-Host 'Native model loading and closed-prompt file read passed.'
}
Write-Host 'Pinned Windows runtime provenance, SHA256, x64 image, payload, and startup verified.'
