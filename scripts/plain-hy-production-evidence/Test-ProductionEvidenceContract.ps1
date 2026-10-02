# Pure contract fixtures only: never starts native inference and never emits a release envelope.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$wrapper = Join-Path $PSScriptRoot 'Run-WindowsProductionEvidence.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($wrapper, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
# Load only the checked-in pure input-validation helpers, not the capture entry point.
foreach ($name in @('Assert-PlainPath', 'Get-Identity', 'Assert-Identity', 'Get-RuntimeInventory')) {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }.GetNewClosure(), $false)
    if ($null -eq $function) { throw "Missing validation helper $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
function Assert-Fails([scriptblock]$Action, [string]$Expected) {
    $failed = $false
    try { & $Action | Out-Null } catch { if ($_.ToString().Contains($Expected)) { $failed = $true } else { throw } }
    if (-not $failed) { throw "Expected rejection: $Expected" }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('DropSpace-production-packet-contract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    foreach ($name in @('llama-completion.exe','llama-completion-avx2.exe','llama-tokenize.exe','plain-lyrics-worker.exe','plain-lyrics-worker-avx2.exe','plain-lyrics-worker-vulkan.exe')) {
        Set-Content -LiteralPath (Join-Path $temporary $name) -Value ('INERT CONTRACT FIXTURE ' + $name) -Encoding utf8NoBOM -NoNewline
    }
    $base = Get-Identity (Join-Path $temporary 'llama-completion.exe')
    $avx2 = Get-Identity (Join-Path $temporary 'llama-completion-avx2.exe')
    $tokenizer = Get-Identity (Join-Path $temporary 'llama-tokenize.exe')
    $manifest = [ordered]@{ schemaVersion=1;runtimeId='llama-cpp-v0.5.0-cpu-win-x64';sourceCommit='7fe450e19305b828c199d602c23a8337aaa1f03b';executable='llama-completion.exe';sha256=$base.sha256;bytes=$base.bytes;avx2=[ordered]@{executable='llama-completion-avx2.exe';sha256=$avx2.sha256;bytes=$avx2.bytes};tokenizer=[ordered]@{executable='llama-tokenize.exe';sha256=$tokenizer.sha256;bytes=$tokenizer.bytes} }
    $resident = [ordered]@{protocol=1;profile='hy-q8-plain-resident-v1';sourceSha256=('a' * 64)}
    foreach ($variant in @(@{key='cpu';name='plain-lyrics-worker.exe'}, @{key='avx2';name='plain-lyrics-worker-avx2.exe'}, @{key='vulkan';name='plain-lyrics-worker-vulkan.exe'})) {
        $identity = Get-Identity (Join-Path $temporary $variant.name)
        $resident[$variant.key] = [ordered]@{executable=$variant.name;sha256=$identity.sha256;bytes=$identity.bytes}
    }
    $manifest.resident = $resident
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $temporary 'runtime-manifest.json') -Encoding utf8NoBOM
    Assert-Fails { Get-RuntimeInventory $temporary } 'exactly the eight shipping files'
    $licensePath = Join-Path $temporary 'LICENSE-llama.cpp'
    Set-Content -LiteralPath $licensePath -Value "===== LICENSE =====`n===== vendor\cpp-httplib\LICENSE =====`n===== licenses\LICENSE-jsonhpp =====" -Encoding utf8NoBOM
    $inventory = @(Get-RuntimeInventory $temporary)
    if ($inventory.Count -ne 8) { throw 'Complete runtime fixture was not inventoried.' }
    Add-Content -LiteralPath (Join-Path $temporary 'plain-lyrics-worker-avx2.exe') -Value 'tampered'
    Assert-Fails { Get-RuntimeInventory $temporary } 'File integrity mismatch'
    Set-Content -LiteralPath (Join-Path $temporary 'plain-lyrics-worker-avx2.exe') -Value 'INERT CONTRACT FIXTURE plain-lyrics-worker-avx2.exe' -Encoding utf8NoBOM -NoNewline
    Set-Content -LiteralPath $licensePath -Value '===== LICENSE =====' -Encoding utf8NoBOM
    Assert-Fails { Get-RuntimeInventory $temporary } 'Required runtime license notice missing'
    $source = Get-Content -LiteralPath $wrapper -Raw
    if (-not $source.Contains("if (Test-Path -LiteralPath `$OutputDirectory) { throw 'Output directory already exists.")) { throw 'Create-only output directory guard missing.' }
    if (-not $source.Contains("if (`$harnessExit -ne 0) { throw")) { throw 'Failed harness must prevent evidence envelope.' }
    Write-Host 'Production evidence contracts passed: parser, eight-file inventory, required license sections, all-component hashes, create-only and failed-run guards. No native execution.'
}
finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
