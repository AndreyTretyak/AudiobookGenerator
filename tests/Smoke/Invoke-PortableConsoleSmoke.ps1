param(
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$artifactRoot = Join-Path (Join-Path $repoRoot 'artifacts') 'smoke'
$fixturePath = Join-Path $artifactRoot 'portable-smoke.epub'
$settingsDirectory = Join-Path $artifactRoot 'settings'
$settingsPath = Join-Path $settingsDirectory 'tts-settings.json'
$consoleProject = Join-Path (Join-Path (Join-Path $repoRoot 'src') 'AudiobookGenerator.Console') 'YewCone.AudiobookGenerator.Console.csproj'

if (Test-Path $artifactRoot) {
    Remove-Item -Recurse -Force $artifactRoot
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null

& (Join-Path $PSScriptRoot 'New-PortableSmokeFixture.ps1') -OutputPath $fixturePath | Out-Null

function Join-Arguments {
    param([string[]]$Items)

    return ($Items | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join ' '
}

function Invoke-AudiobookCommand {
    param(
        [string[]]$Arguments,
        [hashtable]$Environment = @{},
        [string]$InputText
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $startInfo.FileName = 'dotnet'
        $startInfo.Arguments = Join-Arguments (@('run', '--project', $consoleProject, '-c', 'Release', '-p:PortableAot=true', '--') + $Arguments)
    }
    else {
        $startInfo.FileName = $ExecutablePath
        $startInfo.Arguments = Join-Arguments $Arguments
    }

    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = -not [string]::IsNullOrEmpty($InputText)
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $startInfo.Environment['AUDIOBOOKGENERATOR_SETTINGS_DIRECTORY'] = $settingsDirectory

    foreach ($name in @($startInfo.Environment.Keys)) {
        if ($name -like 'AUDIOBOOKGENERATOR_TTS_*' -or $name -like 'AUDIOBOOKGENERATOR_VISION_*') {
            $startInfo.Environment.Remove($name)
        }
    }

    foreach ($entry in $Environment.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            $startInfo.Environment.Remove($entry.Key)
        }
        else {
            $startInfo.Environment[$entry.Key] = [string]$entry.Value
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    if (-not [string]::IsNullOrEmpty($InputText)) {
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
    }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
        Output = ($stdout + "`n" + $stderr).Trim()
    }
}

function Assert-Success {
    param(
        [pscustomobject]$Result,
        [string]$Context,
        [string[]]$RequiredText = @()
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Context failed with exit code $($Result.ExitCode):`n$($Result.Output)"
    }

    foreach ($text in $RequiredText) {
        if ($Result.Output.IndexOf($text, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "$Context did not contain expected text '$text'. Output:`n$($Result.Output)"
        }
    }
}

function Test-MediaToolsAvailable {
    foreach ($tool in 'ffmpeg', 'ffprobe') {
        $command = Get-Command $tool -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            return $false
        }
    }

    return $true
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

try {
    $help = Invoke-AudiobookCommand -Arguments @('help')
    Assert-Success -Result $help -Context 'help smoke' -RequiredText @('Audiobook', 'info')

    $info = Invoke-AudiobookCommand -Arguments @('info', $fixturePath)
    Assert-Success -Result $info -Context 'info smoke' -RequiredText @('Portable Smoke Book', 'Smoke Author', 'Chapter One', 'cover.png')

    $environmentListing = Invoke-AudiobookCommand -Arguments @('voices', '--provider', 'smoke') -Environment @{
        'AUDIOBOOKGENERATOR_TTS_DEFAULT_PROVIDER' = 'smoke'
        'AUDIOBOOKGENERATOR_TTS_PROFILE_IDS' = 'smoke'
        'AUDIOBOOKGENERATOR_TTS_SMOKE_DISPLAY_NAME' = 'Smoke Local'
        'AUDIOBOOKGENERATOR_TTS_SMOKE_BASE_URL' = 'http://127.0.0.1:18880/v1/'
        'AUDIOBOOKGENERATOR_TTS_SMOKE_MODEL' = 'smoke-model'
        'AUDIOBOOKGENERATOR_TTS_SMOKE_VOICES' = 'smoke=Smoke Voice'
    }
    Assert-Success -Result $environmentListing -Context 'environment voice listing' -RequiredText @('Smoke Local', 'Smoke Voice')

    @'
{
  "defaultProviderId": "file",
  "openAiCompatibleProfiles": [
    {
      "id": "file",
      "displayName": "File Profile",
      "baseUrl": "http://127.0.0.1:18880/v1/",
      "model": "file-model",
      "voices": [
        {
          "id": "file_voice",
          "displayName": "File Voice",
          "culture": "en-US",
          "gender": "Female"
        }
      ]
    }
  ]
}
'@ | Set-Content -Path $settingsPath -Encoding UTF8

    $settingsListing = Invoke-AudiobookCommand -Arguments @('voices', '--provider', 'file')
    Assert-Success -Result $settingsListing -Context 'settings-file voice listing' -RequiredText @('File Profile', 'File Voice')

    if (Test-MediaToolsAvailable) {
        $port = Get-FreeTcpPort
        $serverStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $serverStartInfo.FileName = (Get-Command pwsh -ErrorAction Stop).Source
        $serverStartInfo.ArgumentList.Add('-NoProfile')
        $serverStartInfo.ArgumentList.Add('-File')
        $serverStartInfo.ArgumentList.Add((Join-Path $PSScriptRoot 'Start-FakeOpenAiTtsServer.ps1'))
        $serverStartInfo.ArgumentList.Add('-Port')
        $serverStartInfo.ArgumentList.Add([string]$port)
        $serverStartInfo.UseShellExecute = $false
        $serverStartInfo.CreateNoWindow = $true
        $server = [System.Diagnostics.Process]::Start($serverStartInfo)
        try {
            $ready = $false
            for ($attempt = 0; $attempt -lt 50 -and -not $ready; $attempt++) {
                try {
                    $client = [System.Net.Sockets.TcpClient]::new()
                    $client.Connect('127.0.0.1', $port)
                    $client.Dispose()
                    $ready = $true
                }
                catch {
                    Start-Sleep -Milliseconds 100
                }
            }
            if (-not $ready) {
                throw 'The fake OpenAI TTS server did not become ready.'
            }

            $conversion = Invoke-AudiobookCommand `
                -Arguments @(
                    'convert',
                    $fixturePath,
                    '--provider',
                    'smoke',
                    '--voice',
                    'smoke',
                    '--output',
                    $artifactRoot,
                    '--yes') `
                -Environment @{
                    'AUDIOBOOKGENERATOR_TTS_DEFAULT_PROVIDER' = 'smoke'
                    'AUDIOBOOKGENERATOR_TTS_PROFILE_IDS' = 'smoke'
                    'AUDIOBOOKGENERATOR_TTS_SMOKE_DISPLAY_NAME' = 'Smoke Local'
                    'AUDIOBOOKGENERATOR_TTS_SMOKE_BASE_URL' = "http://127.0.0.1:$port/v1/"
                    'AUDIOBOOKGENERATOR_TTS_SMOKE_MODEL' = 'smoke-model'
                    'AUDIOBOOKGENERATOR_TTS_SMOKE_VOICES' = 'smoke=Smoke Voice'
                }
            Assert-Success -Result $conversion -Context 'portable conversion smoke' -RequiredText @('Audiobook saved')

            $m4bPath = Join-Path $artifactRoot 'portable-smoke.m4b'
            $sidecarPath = Join-Path $artifactRoot 'portable-smoke.audiobook.json'
            if (-not (Test-Path $m4bPath)) {
                throw "Portable conversion did not create $m4bPath"
            }
            if (-not (Test-Path $sidecarPath)) {
                throw "Portable conversion did not create $sidecarPath"
            }

            $successfulHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $m4bPath).Hash
            $unreachablePort = Get-FreeTcpPort
            $failedConversion = Invoke-AudiobookCommand `
                -Arguments @(
                    'convert',
                    $fixturePath,
                    '--provider',
                    'offline',
                    '--voice',
                    'offline',
                    '--output',
                    $artifactRoot,
                    '--yes') `
                -Environment @{
                    'AUDIOBOOKGENERATOR_TTS_DEFAULT_PROVIDER' = 'offline'
                    'AUDIOBOOKGENERATOR_TTS_PROFILE_IDS' = 'offline'
                    'AUDIOBOOKGENERATOR_TTS_OFFLINE_DISPLAY_NAME' = 'Offline'
                    'AUDIOBOOKGENERATOR_TTS_OFFLINE_BASE_URL' = "http://127.0.0.1:$unreachablePort/v1/"
                    'AUDIOBOOKGENERATOR_TTS_OFFLINE_MODEL' = 'offline-model'
                    'AUDIOBOOKGENERATOR_TTS_OFFLINE_VOICES' = 'offline=Offline Voice'
                }
            if ($failedConversion.ExitCode -eq 0) {
                throw "A failed non-interactive conversion returned exit code 0:`n$($failedConversion.Output)"
            }
            if ($failedConversion.Output.IndexOf('Conversion failed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Failed conversion did not report the failure:`n$($failedConversion.Output)"
            }
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $m4bPath).Hash -ne $successfulHash) {
                throw 'A failed conversion changed the previously generated audiobook.'
            }
        }
        finally {
            if ($null -ne $server -and -not $server.HasExited) {
                $server.Kill($true)
                $server.WaitForExit()
            }
            $server.Dispose()
        }
    }
    else {
        Write-Host 'Skipped portable end-to-end conversion smoke because ffmpeg/ffprobe are unavailable.'
    }

    Write-Host 'Portable console smoke completed successfully.'
}
finally {
}
