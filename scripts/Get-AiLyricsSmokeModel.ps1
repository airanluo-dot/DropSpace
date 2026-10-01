param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory = Join-Path $root 'artifacts/ai-smoke-model'
New-Item $directory -ItemType Directory -Force | Out-Null
# Development-only data; these weights are not included in the application bundle.
$models = @(
    @{ File = 'Hy-MT2-1.8B-Q4_K_M.gguf'; Bytes = 1133080448; Hash = 'dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699'; Environment = 'DROPSPACE_AI_SMOKE_MODEL'; Uri = 'https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf' },
    @{ File = 'Hy-MT2-1.8B.i1-IQ3_S.gguf'; Bytes = 876311552; Hash = '46e67068820c68ac43ea9f304556c641e8c9dbf54efb14cef2b4dd540a30f12a'; Environment = 'DROPSPACE_AI_SMOKE_COMPACT_MODEL'; Uri = 'https://huggingface.co/mradermacher/Hy-MT2-1.8B-i1-GGUF/resolve/9f5c7d98d8b625800775e6197e55c7ed38f2f33a/Hy-MT2-1.8B.i1-IQ3_S.gguf' }
)
foreach ($model in $models) {
    $path = Join-Path $directory $model.File
    $valid = (Test-Path $path -PathType Leaf) -and (Get-Item $path).Length -eq $model.Bytes -and (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $model.Hash
    if (-not $valid) {
        $temporary = "$path.partial"
        Invoke-WebRequest -Uri $model.Uri -OutFile $temporary -TimeoutSec 600
        if ((Get-Item $temporary).Length -ne $model.Bytes -or (Get-FileHash $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.Hash) {
            throw 'AI smoke model failed its pinned size/hash checks.'
        }
        Move-Item $temporary $path -Force
    }
    Write-Host "Verified native inference smoke model: $($model.File)"
    if ($env:GITHUB_ENV) { "$($model.Environment)=$path" | Out-File $env:GITHUB_ENV -Encoding utf8 -Append }
}
if ($env:GITHUB_ENV) {
    "DROPSPACE_AI_SMOKE_RUNTIME=$(Join-Path $root 'artifacts/ai-runtime/win-x64')" | Out-File $env:GITHUB_ENV -Encoding utf8 -Append
}
