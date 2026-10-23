#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Pair', 'Catalog', 'Execute', 'Status', 'McpConfiguration')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [Guid]$BindingId,
    [string]$EndpointFile = (Join-Path $env:LOCALAPPDATA 'DropSpace\Agent\endpoint.json'),
    [string]$CredentialDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$script:failureCode = 'client_error'
$script:failureMessage = 'The local DropSpace client could not complete this request.'

function Fail-Client([string]$Code, [string]$Message) {
    $script:failureCode = $Code
    $script:failureMessage = $Message
    throw $Code
}

function Read-InputObject {
    if (-not [Console]::IsInputRedirected) {
        Fail-Client 'stdin_required' 'Supply one UTF-8 JSON object through stdin; do not put pairing keys in command-line arguments.'
    }
    $builder = New-Object System.Text.StringBuilder
    $buffer = New-Object char[] 4096
    while (($count = [Console]::In.Read($buffer, 0, $buffer.Length)) -gt 0) {
        [void]$builder.Append($buffer, 0, $count)
        if ($builder.Length -gt 1048576) { Fail-Client 'input_too_large' 'Input exceeds the 1 MiB limit.' }
    }
    $json = $builder.ToString()
    if ($utf8.GetByteCount($json) -gt 1048576) { Fail-Client 'input_too_large' 'Input exceeds the 1 MiB limit.' }
    try { $value = ConvertFrom-Json -InputObject $json } catch { Fail-Client 'invalid_json' 'Stdin must contain valid JSON.' }
    if ($value -isnot [PSCustomObject]) { Fail-Client 'invalid_json' 'Stdin must contain one JSON object.' }
    return $value
}

function Get-Property($Value, [string]$Name) {
    if ($null -eq $Value) { return $null }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Resolve-Endpoint {
    if (-not [IO.File]::Exists($EndpointFile)) { Fail-Client 'app_unavailable' 'Start DropSpace and enable the Agent connection in Settings.' }
    if ((Get-Item -LiteralPath $EndpointFile).Length -gt 16384) { Fail-Client 'invalid_endpoint' 'The endpoint descriptor is invalid.' }
    try { $descriptor = ConvertFrom-Json ([IO.File]::ReadAllText($EndpointFile, $utf8)) } catch { Fail-Client 'invalid_endpoint' 'The endpoint descriptor is invalid.' }
    $endpoint = Get-Property $descriptor 'endpoint'
    $uri = $null
    if ((Get-Property $descriptor 'protocolVersion') -ne 1 -or
        $endpoint -isnot [string] -or
        -not [Uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'http' -or $uri.Host -cne '127.0.0.1' -or
        $uri.Port -lt 1 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.AbsolutePath -ne '/' -or $endpoint -notmatch '^http://127\.0\.0\.1:[0-9]+/?$') {
        Fail-Client 'invalid_endpoint' 'Only the discovered http://127.0.0.1:port endpoint is accepted.'
    }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Ensure-CredentialDirectory {
    [void][IO.Directory]::CreateDirectory($CredentialDirectory)
    if (((Get-Item -LiteralPath $CredentialDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Fail-Client 'invalid_credentials_path' 'The credential directory must not be a link.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inheritance, $propagation, $allow)))
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $inheritance, $propagation, $allow)))
    [IO.Directory]::SetAccessControl($CredentialDirectory, $security)
}

function Save-Credential([string]$Token) {
    Add-Type -AssemblyName System.Security
    $bytes = $utf8.GetBytes($Token)
    try {
        $encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes, $BindingId.ToByteArray(), [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $protected = [Convert]::ToBase64String($encrypted)
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    $record = @{ protocolVersion = 1; bindingId = $BindingId.ToString('D'); protectedToken = $protected }
    $temporary = Join-Path $CredentialDirectory ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Compress), $utf8)
        if ([IO.File]::Exists($credentialFile)) { [IO.File]::Replace($temporary, $credentialFile, $null) }
        else { [IO.File]::Move($temporary, $credentialFile) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Read-Credential {
    if (-not [IO.File]::Exists($credentialFile)) { Fail-Client 'not_paired' 'Pair this binding once using the invitation copied from DropSpace Settings.' }
    if ((Get-Item -LiteralPath $credentialFile).Length -gt 32768) { Fail-Client 'invalid_credentials' 'The saved credential is invalid; create a new binding in DropSpace Settings.' }
    try {
        $record = ConvertFrom-Json ([IO.File]::ReadAllText($credentialFile, $utf8))
        if ((Get-Property $record 'bindingId') -ne $BindingId.ToString('D') -or (Get-Property $record 'protocolVersion') -ne 1) { throw 'mismatch' }
        Add-Type -AssemblyName System.Security
        $encrypted = [Convert]::FromBase64String((Get-Property $record 'protectedToken'))
        $bytes = $null
        try {
            $bytes = [Security.Cryptography.ProtectedData]::Unprotect($encrypted, $BindingId.ToByteArray(), [Security.Cryptography.DataProtectionScope]::CurrentUser)
            return $utf8.GetString($bytes)
        } finally {
            if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        }
    } catch { Fail-Client 'invalid_credentials' 'The saved credential cannot be opened by this Windows user; create a new binding in DropSpace Settings.' }
}

function Invoke-LocalApi([string]$Method, [string]$Path, $Body, [string]$Token) {
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(35)
    $client.MaxResponseContentBufferSize = 4194304
    $request = New-Object Net.Http.HttpRequestMessage((New-Object Net.Http.HttpMethod($Method)), ($baseUri + $Path))
    try {
        if ($Token) { $request.Headers.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Token) }
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 50 -Compress
            if ($utf8.GetByteCount($json) -gt 1048576) { Fail-Client 'input_too_large' 'Request exceeds the 1 MiB limit.' }
            $request.Content = New-Object Net.Http.StringContent($json, $utf8, 'application/json')
        }
        try { $response = $client.SendAsync($request).GetAwaiter().GetResult() }
        catch { Fail-Client 'connection_failed' 'DropSpace did not respond. Start the app; after a mutation timeout, inspect its state before retrying.' }
        try {
            $raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            try { $bodyValue = ConvertFrom-Json $raw } catch { Fail-Client 'invalid_response' 'DropSpace returned an invalid JSON response.' }
            if (-not $response.IsSuccessStatusCode) {
                $code = Get-Property (Get-Property $bodyValue 'error') 'code'
                if ($code -isnot [string] -or $code -notmatch '^[a-z_]{1,64}$') { $code = 'request_failed' }
                $message = 'DropSpace rejected the request. Inspect the command catalog and current app state before retrying.'
                if ($code -in @('unauthorized', 'revoked')) { $message = 'This binding is no longer authorized. Create a new binding in DropSpace Settings; the client will not bypass revocation.' }
                Fail-Client $code $message
            }
            if ($bodyValue -isnot [PSCustomObject]) { Fail-Client 'invalid_response' 'DropSpace returned an invalid response object.' }
            return $bodyValue
        } finally { $response.Dispose() }
    } finally { $request.Dispose(); $client.Dispose(); $handler.Dispose() }
}

try {
    if ($BindingId -eq [Guid]::Empty) { Fail-Client 'invalid_binding' 'A non-empty binding ID is required.' }
    $EndpointFile = [IO.Path]::GetFullPath($EndpointFile)
    if (-not $CredentialDirectory) { $CredentialDirectory = Join-Path ([IO.Path]::GetDirectoryName($EndpointFile)) 'ClientCredentials' }
    $CredentialDirectory = [IO.Path]::GetFullPath($CredentialDirectory)
    $credentialFile = Join-Path $CredentialDirectory ($BindingId.ToString('D') + '.json')
    if ($Action -eq 'McpConfiguration') {
        $node = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $node) { Fail-Client 'node_required' 'MCP mode requires an existing Node.js 18 or newer installation. Use the PowerShell REST client without Node; no runtime will be downloaded.' }
        $nodeVersion = & $node.Source --version
        if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 18) {
            Fail-Client 'node_required' 'MCP mode requires Node.js 18 or newer. Use the PowerShell REST client; no runtime will be downloaded.'
        }
        $result = @{ command = $node.Source; args = @((Join-Path $PSScriptRoot 'dropspace-mcp.mjs'), '--binding-id', $BindingId.ToString('D'), '--endpoint-file', $EndpointFile, '--credential-directory', $CredentialDirectory) }
    } else {
        $baseUri = Resolve-Endpoint
        if ($Action -eq 'Pair') {
            $inputValue = Read-InputObject
            $pairingKey = Get-Property $inputValue 'pairingKey'
            if ($pairingKey -isnot [string] -or $pairingKey -cnotmatch '^[A-Za-z0-9_-]{43}$') { Fail-Client 'invalid_pairing_key' 'Supply the one-time pairing key from DropSpace through stdin JSON.' }
            Ensure-CredentialDirectory
            $paired = Invoke-LocalApi 'POST' '/v1/pair' @{ bindingId = $BindingId.ToString('D'); pairingKey = $pairingKey } ''
            $token = Get-Property $paired 'bearerToken'
            if ((Get-Property $paired 'bindingId') -ne $BindingId.ToString('D') -or (Get-Property $paired 'protocolVersion') -ne 1 -or
                $token -isnot [string] -or $token.Length -lt 32 -or $token.Length -gt 4096 -or $token -match '[\r\n]') {
                Fail-Client 'invalid_response' 'DropSpace returned an invalid pairing response.'
            }
            try { Save-Credential $token }
            catch { Fail-Client 'credential_save_failed' 'The app accepted pairing, but this client could not save the credential. Remove this binding in DropSpace Settings and create a fresh invitation.' }
            $result = @{ paired = $true; bindingId = $BindingId.ToString('D'); protocolVersion = 1 }
            $token = $null; $pairingKey = $null; $paired = $null; $inputValue = $null
        } else {
            $token = Read-Credential
            try {
                if ($Action -eq 'Execute') {
                    $inputValue = Read-InputObject
                    $command = Get-Property $inputValue 'command'
                    $arguments = Get-Property $inputValue 'arguments'
                    if ($command -isnot [string] -or [string]::IsNullOrWhiteSpace($command) -or $arguments -isnot [PSCustomObject]) {
                        Fail-Client 'invalid_request' 'Execute expects stdin JSON with a command string and an arguments object.'
                    }
                    $result = Invoke-LocalApi 'POST' '/v1/execute' @{ command = $command; arguments = $arguments } $token
                } else {
                    $result = Invoke-LocalApi 'GET' '/v1/catalog' $null $token
                    if ($Action -eq 'Status') {
                        $result = @{ connected = $true; bindingId = $BindingId.ToString('D'); protocolVersion = 1; commandCount = @(Get-Property $result 'commands').Count }
                    }
                }
            } finally { $token = $null }
        }
    }
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 50 -Compress))
    exit 0
} catch {
    [Console]::Out.WriteLine((@{ error = @{ code = $script:failureCode; message = $script:failureMessage } } | ConvertTo-Json -Compress))
    exit 1
}
