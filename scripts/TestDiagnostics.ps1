Set-StrictMode -Version Latest

function ConvertTo-DropSpaceDiagnosticMetadata
{
    param($Value)

    if ($Value -is [Collections.IDictionary])
    {
        $safe = [ordered]@{}
        foreach ($key in $Value.Keys)
        {
            if ([string]$key -match '(?i)(fingerprint|secret|token|authorization|password)') { continue }
            if ($Value[$key] -is [bool] -or $Value[$key] -is [ValueType])
            {
                $safe[$key] = $Value[$key]
                continue
            }
            if ([string]$key -match '(?i)(^(text|clipboardText|rawText|message|content|payload|errorDetail|error|root|dataRoot|directory)$|Path$|Directory$|Message$)' -and
                $Value[$key] -is [string]) { continue }
            $safe[$key] = ConvertTo-DropSpaceDiagnosticMetadata $Value[$key]
        }
        return $safe
    }
    if ($Value -is [Array])
    {
        return ,@($Value | ForEach-Object { ConvertTo-DropSpaceDiagnosticMetadata $_ })
    }
    return $Value
}

function New-DropSpaceTestDiagnosticDirectory
{
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][ValidateSet('portable', 'installed', 'installer-lifecycle', 'probe')][string]$Category
    )

    $directory = Join-Path $RepositoryRoot "artifacts/diagnostics/$Category-$([Guid]::NewGuid().ToString('N'))"
    try
    {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        return $directory
    }
    catch { Write-Warning "Test diagnostic directory creation failed ($($_.Exception.GetType().Name))." }
}

function Save-DropSpaceTestDiagnostics
{
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9-]{1,100}$')][string]$Phase,
        [ValidateSet('en-US', 'zh-CN', 'not-applicable')][string]$Language = "not-applicable",
        [ValidatePattern('^[^/\\]{1,260}$')][string]$ExecutableName = "DropSpace.exe",
        [int]$ProcessId = 0,
        [string]$MarkerPath = "",
        [string]$DataRoot = "",
        [string[]]$LifecycleLogs = @()
    )

    # Retention is best-effort; it must never replace a smoke assertion or prevent cleanup.
    try
    {
        $destination = Join-Path $Directory "$Phase-$Language-$ProcessId"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $files = [Collections.Generic.List[object]]::new()
        if (-not [string]::IsNullOrWhiteSpace($MarkerPath) -and (Test-Path -LiteralPath $MarkerPath -PathType Leaf))
        {
            $markerFile = Get-Item -LiteralPath $MarkerPath
            if ($markerFile.Length -le 1048576 -and ($markerFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)
            {
                try
                {
                    $marker = Get-Content -LiteralPath $MarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
                    if ($marker.ContainsKey('errorDetail'))
                    {
                        $marker['stackFrames'] = @([regex]::Matches([string]$marker.errorDetail, '(?m)^\s*at (?<function>[A-Za-z0-9_.$+`<>]+)\([^\r\n]*') | Select-Object -First 32 | ForEach-Object {
                            $frame = [ordered]@{ function = $_.Groups['function'].Value }
                            $position = [regex]::Match($_.Value, ':line (?<line>\d+)\s*$')
                            if ($position.Success) { $frame['line'] = [int]$position.Groups['line'].Value }
                            $frame
                        })
                    }
                    # Free-form errors can include clipboard data. The console retains the original
                    # redacted error; artifacts retain only its type/HRESULT and stage/metrics.
                    $marker.Remove('error') | Out-Null
                    $marker.Remove('errorDetail') | Out-Null
                    ConvertTo-DropSpaceDiagnosticMetadata $marker | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $destination 'smoke-marker.json') -Encoding utf8
                    $files.Add([ordered]@{ name = 'smoke-marker.json'; kind = 'marker' })
                }
                catch { $files.Add([ordered]@{ name = 'smoke-marker.json'; status = 'read-failed'; exceptionType = $_.Exception.GetType().Name }) }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($DataRoot))
        {
            $logDirectory = Join-Path $DataRoot 'logs'
            foreach ($name in @('clipboard-smoke-diagnostics.json', 'dropspace.log', 'dropspace.log.1', 'crash.marker', 'dropspace-log-diagnostics.txt'))
            {
                $source = Join-Path $logDirectory $name
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
                try
                {
                    $file = Get-Item -LiteralPath $source
                    if ($file.Length -gt 4194304 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                    if ($name -eq 'clipboard-smoke-diagnostics.json')
                    {
                        # This dedicated sidecar's schema contains counters and enum decisions only.
                        $diagnostics = Get-Content -LiteralPath $source -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
                        $parsedProcessId = 0
                        $sourceProcessId = $null
                        if ($diagnostics.ContainsKey('processId') -and
                            [int]::TryParse([string]$diagnostics.processId, [ref]$parsedProcessId) -and $parsedProcessId -gt 0)
                        {
                            $sourceProcessId = $parsedProcessId
                        }
                        $retainedName = $name
                        $status = 'aggregate'
                        if ($ProcessId -gt 0)
                        {
                            if ($null -eq $sourceProcessId)
                            {
                                $status = 'orphan'
                                $retainedName = 'clipboard-smoke-diagnostics-orphan.json'
                            }
                            elseif ($sourceProcessId -ne $ProcessId)
                            {
                                $status = 'stale'
                                $retainedName = "clipboard-smoke-diagnostics-stale-$sourceProcessId.json"
                            }
                            else { $status = 'fresh' }
                        }
                        $safeDiagnostics = ConvertTo-DropSpaceDiagnosticMetadata $diagnostics
                        $safeDiagnostics['processId'] = $sourceProcessId
                        $safeDiagnostics | ConvertTo-Json -Depth 16 | Set-Content (Join-Path $destination $retainedName) -Encoding utf8
                        $files.Add([ordered]@{
                            name = $retainedName; kind = 'application-diagnostic'; status = $status
                            sourceProcessId = $sourceProcessId; expectedProcessId = $ProcessId
                        })
                        continue
                    }
                    elseif ($name -eq 'crash.marker')
                    {
                        $firstLine = [string](Get-Content -LiteralPath $source -TotalCount 1 -Encoding UTF8)
                        $timestamp = [DateTimeOffset]::MinValue
                        if ($firstLine -match '^(?<timestamp>\S+) stage=(?<stage>[A-Za-z0-9_-]{1,80}) exception=(?<type>[A-Za-z0-9_.]{1,160})\((?<hresult>0x[0-9A-Fa-f]{8})\):' -and
                            [DateTimeOffset]::TryParse($Matches.timestamp, [ref]$timestamp))
                        {
                            "$($timestamp.ToString('O')) stage=$($Matches.stage) exception=$($Matches.type)($($Matches.hresult))" |
                                Set-Content (Join-Path $destination $name) -Encoding utf8
                        }
                        else
                        {
                            $files.Add([ordered]@{ name = $name; kind = 'application-diagnostic'; status = 'filtered-empty' })
                            continue
                        }
                    }
                    else
                    {
                        # General application logs may contain arbitrary messages. Keep only bounded
                        # timestamp/level/event/category metadata, never message contents.
                        $metadata = foreach ($line in Get-Content -LiteralPath $source -Tail 2000 -Encoding UTF8)
                        {
                            if ($line -match '^(?<header>\S+ level=(?:Trace|Debug|Information|Warning|Error|Critical) event=-?\d+ category=[A-Za-z0-9_.]+) message=')
                            {
                                $Matches.header
                            }
                            elseif ($line -match '^\S+ event=logger-shutdown droppedMessages=\d+ writeFailures=\d+$') { $line }
                        }
                        if (@($metadata).Count -eq 0)
                        {
                            $files.Add([ordered]@{ name = $name; kind = 'application-diagnostic'; status = 'filtered-empty' })
                            continue
                        }
                        @($metadata) | Set-Content (Join-Path $destination $name) -Encoding utf8
                    }
                    $files.Add([ordered]@{ name = $name; kind = 'application-diagnostic' })
                }
                catch { $files.Add([ordered]@{ name = $name; status = 'read-failed'; exceptionType = $_.Exception.GetType().Name }) }
            }
        }

        foreach ($source in $LifecycleLogs)
        {
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
            try
            {
                $file = Get-Item -LiteralPath $source
                if ($file.Length -gt 4194304 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                $lines = foreach ($line in Get-Content -LiteralPath $source -Tail 2000 -Encoding UTF8)
                {
                    $safe = [regex]::Replace($line, '(?i)(https?://[^\s?#]+)(?:\?[^\s#]*)?', '$1?[redacted]')
                    $safe = [regex]::Replace($safe, '(?i)Bearer\s+[A-Za-z0-9._~+/-]+=*', 'Bearer [secret]')
                    $safe = [regex]::Replace($safe, '(?i)\b(api[_-]?key|token|secret|password|authorization)\s*[:=]\s*[^\s,;]+', '$1=[secret]')
                    [regex]::Replace($safe, '(?<![A-Za-z0-9])(?:[A-Za-z]:\\|\\\\)[^\r\n<>|"'']+', '[path]')
                }
                $name = [IO.Path]::GetFileName($source)
                @($lines) | Set-Content (Join-Path $destination $name) -Encoding utf8
                $files.Add([ordered]@{ name = $name; kind = 'installer-log' })
            }
            catch { $files.Add([ordered]@{ name = [IO.Path]::GetFileName($source); status = 'read-failed'; exceptionType = $_.Exception.GetType().Name }) }
        }

        [ordered]@{
            schemaVersion = 1
            capturedAtUtc = [DateTime]::UtcNow.ToString('O')
            phase = $Phase
            actualLanguage = $Language
            executableName = $ExecutableName
            processId = $ProcessId
            files = @($files.ToArray())
        } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $destination 'retention.json') -Encoding utf8
    }
    catch
    {
        Write-Warning "Test diagnostic retention failed ($($_.Exception.GetType().Name))."
    }
}
