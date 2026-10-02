param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$VulkanSdk = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requiredNotices = @('LICENSE', 'vendor/cpp-httplib/LICENSE', 'licenses/LICENSE-jsonhpp')
foreach ($notice in $requiredNotices) {
    if (-not (Test-Path (Join-Path $source $notice) -PathType Leaf)) { throw "Missing required runtime notice: $notice" }
}
$notices = Get-ChildItem $source -Recurse -File |
    Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING)([.-].*)?$' -and $_.FullName -notmatch '[\\/]\.git[\\/]' } |
    Sort-Object FullName
$licenseText = @('Notices from the pinned llama.cpp source tree. Optional unlinked components may also be listed.')
foreach ($notice in $notices) {
    $relative = [IO.Path]::GetRelativePath($source, $notice.FullName)
    $licenseText += "===== $relative =====`n" + (Get-Content $notice.FullName -Raw)
}
if ($VulkanSdk) {
    if (-not (Test-Path $VulkanSdk -PathType Container)) { throw 'The Vulkan SDK license source is missing.' }
    $sdkNotices = @(Get-ChildItem $VulkanSdk -Recurse -File | Where-Object {
        $_.Name -match '(?i)(LICENSE|LICENCE|NOTICE|COPYING)' -and $_.Length -le 2097152 -and
        $_.Extension -in @('.txt', '.md', '.rst', '')
    } | Sort-Object FullName)
    # License locations differ between official SDK installers. Fail closed rather than
    # distribute a new backend with only llama.cpp's own notices.
    foreach ($component in @('Vulkan-Headers', 'Vulkan-Hpp', 'SPIRV-Headers')) {
        $matches = @($sdkNotices | Where-Object {
            $_.FullName -match [regex]::Escape($component) -or
            (Get-Content $_.FullName -Raw) -match [regex]::Escape($component)
        })
        if ($matches.Count -eq 0) { throw "Missing $component license attribution in the provisioned Vulkan SDK. Supply its official component notices before packaging." }
    }
    foreach ($notice in $sdkNotices) {
        $relative = [IO.Path]::GetRelativePath($VulkanSdk, $notice.FullName)
        $licenseText += "===== Vulkan SDK / $relative =====`n" + (Get-Content $notice.FullName -Raw)
    }
}
$licenseText -join "`n`n" | Set-Content $OutputPath -Encoding utf8
