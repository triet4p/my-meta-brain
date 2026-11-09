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
        mcp/MetaBrain.Mcp.exe              agent MCP server over stdio
        service/MetaBrain.Connections.exe  shared runtime host (started by the shared lifecycle)
        installer/MetaBrain.Installer.exe  per-user install/upgrade/uninstall

    The payload layout is exactly what `MetaBrain.Installer.exe install --payload
    <bundle>` expects. Releases from 1.2.0 ship the CLI and the MCP server only
    (CR03/D089); the frozen 1.1.0 WPF Owner UI is not published by this script. The script also writes release-manifest.json (product
    version, runtime identifier, every file with its size and SHA-256) and a
    The bundle and archive go under the selected output root. SDK restore/build
    output, temporary files and NuGet cache stay under ignored repository-local
    runtime paths. The script changes no host state: no installation, service
    registration, autostart, registry, persistent environment/profile or permissions.

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
 $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
 $env:TEMP = Join-Path $repo 'runtime\release-build-temp'
 $env:TMP = $env:TEMP
 New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
 $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $toolchain 'bundle-extract'
 New-Item -ItemType Directory -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Force | Out-Null

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

 $nativeTokenizerSource = Join-Path $repo 'runtime\s15-t6-native-build\target\x86_64-pc-windows-msvc\release\hf_tokenizers.dll'
 $nativeTokenizerSha256 = '4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d'
 $nativeTokenizerBytes = 3198464
 $nativeTokenizerSourceArchive = Join-Path $repo 'runtime\s15-t6-native-build\tokenizers-dotnet-b8eaa77d-source.zip'
 $nativeTokenizerSourceArchiveSha256 = '2ee9c2d81aba55396ca67544765bd5cf2438ff35b5a959fe820915e449f77ded'
 $nativeTokenizerSourceArchiveBytes = 1236124
 $nativeTokenizerLock = Join-Path $repo 'src\MetaBrain.Connections\native\provenance\Cargo.lock'
 $nativeTokenizerLockSha256 = '7597e8861f7368d02ea5daded9b71cbc226286ffbf1bbe7ad57f3d719b53d48f'
 $nativeTokenizerNotices = Join-Path $repo 'src\MetaBrain.Connections\native\THIRD-PARTY-NOTICES.txt'
 $nativeTokenizerProvenance = Join-Path $repo 'src\MetaBrain.Connections\native\provenance\BUILD-PROVENANCE.md'
 if (-not (Test-Path -LiteralPath $nativeTokenizerSource -PathType Leaf) -or
     (Get-Item -LiteralPath $nativeTokenizerSource).Length -ne $nativeTokenizerBytes -or
     (Get-FileSha256 $nativeTokenizerSource) -ne $nativeTokenizerSha256) {
     throw 'The pinned source-built tokenizer payload is missing or has an unexpected identity.'
 }
 if (-not (Test-Path -LiteralPath $nativeTokenizerSourceArchive -PathType Leaf) -or
     (Get-Item -LiteralPath $nativeTokenizerSourceArchive).Length -ne $nativeTokenizerSourceArchiveBytes -or
     (Get-FileSha256 $nativeTokenizerSourceArchive) -ne $nativeTokenizerSourceArchiveSha256) {
     throw 'The pinned Tokenizers.DotNet source archive is missing or has an unexpected identity.'
 }
 if (-not (Test-Path -LiteralPath $nativeTokenizerLock -PathType Leaf) -or
     (Get-FileSha256 $nativeTokenizerLock) -ne $nativeTokenizerLockSha256) {
     throw 'The pinned tokenizer Cargo.lock is missing or has an unexpected identity.'
 }
 if (-not (Test-Path -LiteralPath $nativeTokenizerNotices -PathType Leaf) -or
     -not (Test-Path -LiteralPath $nativeTokenizerProvenance -PathType Leaf)) {
     throw 'The native tokenizer notices or build provenance are missing.'
 }
 $nativeTokenizerNoticesSha256 = Get-FileSha256 $nativeTokenizerNotices
 $nativeTokenizerNoticesBytes = (Get-Item -LiteralPath $nativeTokenizerNotices).Length
 $nativeTokenizerNoticePath = 'service/licenses/native-tokenizer/THIRD-PARTY-NOTICES.txt'
 $nugetSources = @(
     (Join-Path $repo 'runtime\s15-t1-model-staging\614241f622f53c4eeff9890bdc4f31cfecc418b3\nuget'),
     (Join-Path $repo 'runtime\s15-t2-tokenizer-staging\nuget'),
     (Join-Path $repo 'runtime\dotnet-home\.nuget\packages')
 )
 foreach ($source in $nugetSources) {
     if (-not (Test-Path -LiteralPath $source -PathType Container)) {
         throw ('An approved repository-local NuGet source is missing: ' + $source)
     }
 }


foreach ($publish in $publishes) {
    $project = Join-Path $repo $publish.Project
    $target = Join-Path $stage $publish.Directory
    if (-not (Test-Path -LiteralPath $project)) {
        throw ('The project is missing: ' + $project)
    }

     $restoreArguments = @('restore', $project, '-r', 'win-x64', '--nologo', '-p:NuGetAudit=false')
     foreach ($source in $nugetSources) {
         $restoreArguments += @('--source', $source)
     }
     Write-Host ('offline restore ' + $publish.Directory + ' <- repository-local approved feeds')
     $restoreOutput = & dotnet @restoreArguments
     $restoreExitCode = $LASTEXITCODE
     $restoreOutput | ForEach-Object { Write-Host $_ }
     if ($restoreExitCode -ne 0) {
         throw ('Offline restore failed for ' + $publish.Project + ' (exit ' + $restoreExitCode + ').')
     }

     Write-Host ('publish ' + $publish.Directory + ' <- ' + $publish.Project)
     & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore --nologo `
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

$nativeTokenizerOutput = Join-Path (Join-Path $stage 'service') 'hf_tokenizers.dll'
if (-not (Test-Path -LiteralPath $nativeTokenizerOutput -PathType Leaf) -or
    (Get-Item -LiteralPath $nativeTokenizerOutput).Length -ne $nativeTokenizerBytes -or
    (Get-FileSha256 $nativeTokenizerOutput) -ne $nativeTokenizerSha256) {
    throw 'The published shared service does not carry the pinned dynamic tokenizer payload.'
}
 $nativeTokenizerNoticeOutput = Join-Path $stage ($nativeTokenizerNoticePath.Replace('/', '\'))
 New-Item -ItemType Directory -Path (Split-Path -Parent $nativeTokenizerNoticeOutput) -Force | Out-Null
 Copy-Item -LiteralPath $nativeTokenizerNotices -Destination $nativeTokenizerNoticeOutput
 if ((Get-Item -LiteralPath $nativeTokenizerNoticeOutput).Length -ne $nativeTokenizerNoticesBytes -or
     (Get-FileSha256 $nativeTokenizerNoticeOutput) -ne $nativeTokenizerNoticesSha256) {
     throw 'The published shared service does not carry the exact pinned native tokenizer notices.'
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
        mcp = 'mcp/MetaBrain.Mcp.exe'
        installer = 'installer/MetaBrain.Installer.exe'
    }
    sharedRuntimeHost = 'service/MetaBrain.Connections.exe'
     nativeTokenizer = [ordered]@{
         path = 'service/hf_tokenizers.dll'
         bytes = $nativeTokenizerBytes
         sha256 = $nativeTokenizerSha256
         target = 'x86_64-pc-windows-msvc'
         sourceRepository = 'https://github.com/sappho192/Tokenizers.DotNet'
         sourceCommit = 'b8eaa77d72bf9d9c768d75599bfa6a602e467b93'
         sourceArchive = 'runtime/s15-t6-native-build/tokenizers-dotnet-b8eaa77d-source.zip'
         sourceArchiveBytes = $nativeTokenizerSourceArchiveBytes
         sourceArchiveSha256 = $nativeTokenizerSourceArchiveSha256
         cargoLock = 'src/MetaBrain.Connections/native/provenance/Cargo.lock'
         cargoLockSha256 = $nativeTokenizerLockSha256
         cargoPackageCount = 86
         registryPackageCount = 85
         thirdPartyNotices = $nativeTokenizerNoticePath
         thirdPartyNoticesBytes = $nativeTokenizerNoticesBytes
         thirdPartyNoticesSha256 = $nativeTokenizerNoticesSha256
     }
     runtimePrerequisite = [ordered]@{
         name = 'Microsoft Visual C++ v14 x64 Redistributable'
         minimumVersion = '14.43.34808'
         architecture = 'x64'
         downloadPage = 'https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170'
         installedSeparatelyByOwner = $true
         bundled = $false
         applicationDownloadsOrInstalls = $false
         ucrtNote = 'Windows 10+ includes the OS Universal CRT; it does not supply the VCRUNTIME140.dll and VCRUNTIME140_1.dll imports.'
     }
    installedLayout = '<install root>/{cli,mcp,service,installer} plus install-state.json; the data root is separate and is always kept by uninstall. The frozen 1.1.0 WPF Owner UI is not part of this release.'
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
