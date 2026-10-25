<#
.SYNOPSIS
    Developer-only: publish the Meta Brain release bundle from this checkout.

.DESCRIPTION
    Builds the release bundle the owner runs. It is developer tooling and never a
    runtime prerequisite: the owner only unpacks the finished bundle and runs the
    entrypoints, with no .NET SDK, repository or PowerShell on the machine.

    The bundle is self-contained for Windows x64, so it carries the .NET runtime
    and needs no installed runtime or SDK. It has one directory per product
    entrypoint and one for the shared runtime host:

        cli/MetaBrain.Cli.exe              owner CLI (help, --version, lifecycle, owner commands)
        owner-ui/MetaBrain.OwnerUi.exe     owner console
        mcp/MetaBrain.Mcp.exe              agent MCP server over stdio
        service/MetaBrain.Connections.exe  shared runtime host (started by the shared lifecycle)
        installer/MetaBrain.Installer.exe  per-user install/upgrade/uninstall

    The payload layout is exactly what `MetaBrain.Installer.exe install --payload
    <bundle>` expects. The script also writes release-manifest.json (product
    version, runtime identifier, every file with its size and SHA-256) and a
    .zip beside the stage directory. It writes only under the output root
    (repository-ignored runtime/release by default) and changes no host state:
    no installation, no service registration, no autostart entry, no registry
    value, no environment or profile change and no permission change.

    Every SDK invocation runs with process-only first-run protection and a
    repository-local CLI home and package cache.

.EXAMPLE
    powershell.exe -NoProfile -File scripts\release\publish-release-bundle.ps1

.EXAMPLE
    powershell.exe -NoProfile -File scripts\release\publish-release-bundle.ps1 -OutputRoot F:\releases -NoZip
#>
param(
    [string] $OutputRoot = '',
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo 'runtime\release'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

# Repository-local toolchain state, and process-only prevention of the SDK's
# first-run side effects. Nothing here is written outside the repository.
$toolchain = Join-Path $repo 'runtime\dotnet-home'
$env:DOTNET_CLI_HOME = $toolchain
$env:NUGET_PACKAGES = Join-Path $toolchain '.nuget\packages'
$env:HOME = $toolchain
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props'))
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Directory.Build.props does not declare the product version.'
}

$bundleName = 'MetaBrain-' + $version + '-win-x64'
$stage = Join-Path $OutputRoot $bundleName
$zip = Join-Path $OutputRoot ($bundleName + '.zip')

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$publishes = @(
    @{ Project = 'src\MetaBrain.Connections\MetaBrain.Connections.csproj'; Directory = 'service';   Executable = 'MetaBrain.Connections.exe' },
    @{ Project = 'src\MetaBrain.Cli\MetaBrain.Cli.csproj';                 Directory = 'cli';       Executable = 'MetaBrain.Cli.exe' },
    @{ Project = 'src\MetaBrain.OwnerUi\MetaBrain.OwnerUi.csproj';         Directory = 'owner-ui';  Executable = 'MetaBrain.OwnerUi.exe' },
    @{ Project = 'src\MetaBrain.Mcp\MetaBrain.Mcp.csproj';                 Directory = 'mcp';       Executable = 'MetaBrain.Mcp.exe' },
    @{ Project = 'src\MetaBrain.Installer\MetaBrain.Installer.csproj';     Directory = 'installer'; Executable = 'MetaBrain.Installer.exe' }
)

function Get-FileSha256([string] $Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha.ComputeHash($stream)
            return -join ($hash | ForEach-Object { $_.ToString('x2') })
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($publish in $publishes) {
    $project = Join-Path $repo $publish.Project
    $target = Join-Path $stage $publish.Directory
    if (-not (Test-Path -LiteralPath $project)) {
        throw ('The project is missing: ' + $project)
    }

    Write-Host ('publish ' + $publish.Directory + ' <- ' + $publish.Project)
    & dotnet publish $project -c Release -r win-x64 --self-contained true --nologo `
        -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $target
    if ($LASTEXITCODE -ne 0) {
        throw ('dotnet publish failed for ' + $publish.Project + ' (exit ' + $LASTEXITCODE + ').')
    }

    $executable = Join-Path $target $publish.Executable
    if (-not (Test-Path -LiteralPath $executable)) {
        throw ('The published entrypoint is missing: ' + $executable)
    }

    # A self-contained publish carries the runtime. These files are the proof,
    # so a framework-dependent payload can never be shipped as "self-contained".
    foreach ($runtimeFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $runtimeFile))) {
            throw ('The publish is not self-contained; missing ' + $runtimeFile + ' in ' + $target)
        }
    }
}

$files = @(
    Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            bytes = $_.Length
            sha256 = Get-FileSha256 $_.FullName
        }
    }
)

$manifest = [ordered]@{
    product = 'Meta Brain'
    productVersion = $version
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    builtAtUtc = [DateTime]::UtcNow.ToString('O')
    entrypoints = [ordered]@{
        cli = 'cli/MetaBrain.Cli.exe'
        ownerUi = 'owner-ui/MetaBrain.OwnerUi.exe'
        mcp = 'mcp/MetaBrain.Mcp.exe'
        installer = 'installer/MetaBrain.Installer.exe'
    }
    sharedRuntimeHost = 'service/MetaBrain.Connections.exe'
    installedLayout = '<install root>/{cli,owner-ui,mcp,service,installer} plus install-state.json; the data root is separate and is always kept by uninstall'
    fileCount = $files.Count
    files = $files
}
$manifestPath = Join-Path $stage 'release-manifest.json'
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ('stage   ' + $stage)
Write-Host ('files   ' + $files.Count)

if (-not $NoZip) {
    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipHash = Get-FileSha256 $zip
    Set-Content -LiteralPath ($zip + '.sha256') -Value ($zipHash + ' *' + (Split-Path -Leaf $zip)) -Encoding ASCII
    Write-Host ('zip     ' + $zip)
    Write-Host ('sha256  ' + $zipHash)
}

Write-Host ('RELEASE BUNDLE ' + $version + ' ' + $stage)
