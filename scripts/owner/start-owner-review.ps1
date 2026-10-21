# S8-T2 owner-console review launcher (repository-local).
#
# Publishes the owner service and the owner console from this repository into
# runtime\owner-review\payload, starts the service on a private synthetic
# data root, waits until the owner channel answers, and opens the owner
# console against that pipe. Closing the console stops the synthetic service.
#
# It changes nothing outside this repository: no installation, no service
# registration, no autostart entry, no registry value, no environment or
# profile change, and no permission change on any path. The review vault is a
# synthetic vault under runtime\owner-review\data; it is separate from the
# installed owner vault.
#
# It runs under the machine's existing PowerShell execution policy. It needs
# no policy override, and it never installs a certificate, changes a trust
# store, runs an update or writes a profile setting. If your policy blocks
# running the script, PowerShell reports that restriction; the script does not
# weaken the policy for you.
#
#   powershell.exe -NoProfile -File scripts\owner\start-owner-review.ps1
#   powershell.exe -NoProfile -File scripts\owner\start-owner-review.ps1 -Stop
param(
    [switch]$Stop,
    [string]$ControlPipe = 'MetaBrainOwnerReview',
    [switch]$SkipPublish,
    [switch]$ServiceOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$work = Join-Path $repo 'runtime\owner-review'
$payload = Join-Path $work 'payload'
$data = Join-Path $work 'data'
$serviceExe = Join-Path $payload 'service\MetaBrain.Connections.exe'
$uiExe = Join-Path $payload 'owner-ui\MetaBrain.OwnerUi.exe'
$settings = Join-Path $data 'service-settings.json'

function Stop-ReviewService {
    $runtime = Join-Path $data 'runtime'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Set-Content -LiteralPath (Join-Path $runtime 'service.stop') -Value 'stop' -Encoding ascii
    $pidPath = Join-Path $runtime 'service.pid'
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if (-not (Test-Path -LiteralPath $pidPath)) {
            Write-Host "Synthetic review service stopped. Data remains in $data."
            return
        }

        Start-Sleep -Milliseconds 250
    }

    Write-Warning "The stop request was written; see $runtime\service.log."
}

if ($Stop) {
    Stop-ReviewService
    return
}

if (-not $SkipPublish) {
    # The repository-local toolchain: the CLI home, the package cache and the
    # temporary directories all stay inside this repository.
    $env:DOTNET_CLI_HOME = Join-Path $repo 'runtime\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $repo 'runtime\dotnet-home\.nuget\packages'
    $env:TEMP = Join-Path $repo 'runtime\tmp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
    New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null

    Write-Host 'Publishing the owner service and the owner console into the repository...'
    & dotnet publish (Join-Path $repo 'src\MetaBrain.Connections\MetaBrain.Connections.csproj') -c Release -o (Join-Path $payload 'service') --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Publishing the owner service failed.'
    }

    & dotnet publish (Join-Path $repo 'src\MetaBrain.OwnerUi\MetaBrain.OwnerUi.csproj') -c Release -o (Join-Path $payload 'owner-ui') --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Publishing the owner console failed.'
    }
}

if (-not (Test-Path -LiteralPath $serviceExe) -or -not (Test-Path -LiteralPath $uiExe)) {
    throw "The published payload is missing under $payload. Run without -SkipPublish."
}

New-Item -ItemType Directory -Force -Path $data | Out-Null
if (-not (Test-Path -LiteralPath $settings)) {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    [ordered]@{
        schemaVersion = 3
        ownerSid = $sid
        controlPipe = $ControlPipe
        agentPipe = "$ControlPipe.agent"
    } | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
}

function Test-ReviewServiceReady {
    $probe = Start-Process -FilePath $serviceExe `
        -ArgumentList @('owner', 'status', '--control-pipe', $ControlPipe) `
        -Wait -PassThru -WindowStyle Hidden
    return $probe.ExitCode -eq 0
}

if (-not (Test-ReviewServiceReady)) {
    Start-Process -FilePath $serviceExe `
        -ArgumentList @('serve-console', '--config', $settings, '--background') `
        -WorkingDirectory $data -WindowStyle Hidden
}

$ready = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    if (Test-ReviewServiceReady) {
        $ready = $true
        break
    }

    Start-Sleep -Milliseconds 250
}

if (-not $ready) {
    throw "The synthetic service did not become ready. See $data\runtime\service.log"
}

Write-Host "Synthetic review data root: $data"
Write-Host "Owner control pipe:        $ControlPipe"
Write-Host 'Stop: close the console, or run this script again with -Stop.'
Write-Host 'The vault in this directory is separate from the installed owner vault. The first start leaves it uninitialized:'
Write-Host 'use Vault & key in the console to create a synthetic passphrase, and keep that passphrase for this review only.'
Write-Host 'The Backup & export view of that console takes the encrypted backup, the clean restore and the explicit readable export.'

if ($ServiceOnly) {
    Write-Host "Service only: open the console yourself with"
    Write-Host "  $uiExe --control-pipe $ControlPipe"
    return
}

Start-Process -FilePath $uiExe -ArgumentList @('--control-pipe', $ControlPipe) -Wait
Stop-ReviewService
