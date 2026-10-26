<#
.SYNOPSIS
    Assemble immutable Sprint 10 candidate payload archives from this checkout.
.DESCRIPTION
    Reuses the existing self-contained win-x64 bundle. CLI/UI archives are
    payloads, not completed setup programs. Identity binds HEAD and a digest of
    tracked source plus tracked working-tree content. Output is never overwritten.
#>
[CmdletBinding()]
param([string] $OutputRoot = '', [switch] $VerifyOnly, [string] $CandidateDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props'))
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'Product version is missing from Directory.Build.props.' }
function Hash-File([string] $Path) { $stream=[IO.File]::OpenRead($Path); try { $sha=[Security.Cryptography.SHA256]::Create(); try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() } } finally { $stream.Dispose() } }
function Verify-Candidate([string] $Root) {
    $manifestPath = Join-Path $Root 'candidate-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Candidate manifest is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.productVersion -ne $version -or $manifest.runtimeIdentifier -ne 'win-x64') { throw 'Candidate version or runtime identity mismatch.' }
    foreach ($asset in $manifest.assets) {
        $path = Join-Path $Root $asset.name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Candidate asset missing: ' + $asset.name) }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne $asset.bytes -or (Hash-File $path) -ne $asset.sha256) { throw ('Candidate asset integrity mismatch: ' + $asset.name) }
        $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
        try {
            $actual = @($zip.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object -Unique)
            $expected = @($asset.files | ForEach-Object { $_.path } | Sort-Object -Unique)
            if (($actual -join "`n") -cne ($expected -join "`n")) { throw ('Candidate file set mismatch: ' + $asset.name) }
            foreach ($file in $asset.files) {
                $entry = $zip.Entries | Where-Object { $_.FullName.Replace('\','/') -ceq $file.path } | Select-Object -First 1
                if ($null -eq $entry -or $entry.Length -ne $file.bytes) { throw ('Candidate member mismatch: ' + $file.path) }
                $stream = $entry.Open()
                try { $sha = [Security.Cryptography.SHA256]::Create(); try { $digest = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() } } finally { $stream.Dispose() }
                if ($digest -ne $file.sha256) { throw ('Candidate member hash mismatch: ' + $file.path) }
            }
        } finally { $zip.Dispose() }
    }
    if (@($manifest.assets).Count -ne 3) { throw 'Candidate must contain exactly three product payloads.' }
    Write-Host ('VERIFIED ' + $Root)
}
if ($VerifyOnly) { if (-not $CandidateDirectory) { throw '-CandidateDirectory is required with -VerifyOnly.' }; Verify-Candidate ([IO.Path]::GetFullPath($CandidateDirectory)); exit 0 }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'runtime\release-candidates' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not (Test-Path -LiteralPath $OutputRoot)) { New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null }
$head = (& git -C $repo rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify repository HEAD.' }
$tracked = @(& git -C $repo ls-files --cached --others --exclude-standard | Where-Object { $_ -notmatch '^(artifacts/|runtime/|dist/|build/|out/|\.git/|docs/PLAN\.md$|docs/sprint-plans/sprint-10\.md$|scripts/sprint-6/__pycache__/)' } | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'Cannot enumerate source tree.' }
$treeLines = foreach ($relative in $tracked) { $file = Join-Path $repo $relative; if (Test-Path -LiteralPath $file -PathType Leaf) { (Hash-File $file) + '  ' + $relative.Replace('\\','/') } }
$treeBytes = [Text.Encoding]::UTF8.GetBytes(($treeLines -join "`n"))
$sha = [Security.Cryptography.SHA256]::Create(); try { $treeHash = -join ($sha.ComputeHash($treeBytes) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
$source = $head.Substring(0,12) + '-tree' + $treeHash.Substring(0,12)
$id = [Guid]::NewGuid().ToString('N').Substring(0,12)
$destination = Join-Path $OutputRoot ($version + '-win-x64-' + $source + '-' + $id)
New-Item -ItemType Directory -Path $destination | Out-Null
$temp = Join-Path $destination 'bundle'
& powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'publish-release-bundle.ps1') -OutputRoot $temp
if ($LASTEXITCODE -ne 0) { throw 'Existing self-contained bundle assembly failed.' }
$bundle = Join-Path $temp ('MetaBrain-' + $version + '-win-x64')
$definitions = @(
    @{ product='CLI'; deps='cli/MetaBrain.Cli.deps.json'; name=('MetaBrain-CLI-' + $version + '-win-x64-' + $source + '-' + $id + '-payload.zip'); paths=@('cli','service','installer') },
    @{ product='OwnerUI'; deps='owner-ui/MetaBrain.OwnerUi.deps.json'; name=('MetaBrain-OwnerUI-' + $version + '-win-x64-' + $source + '-' + $id + '-payload.zip'); paths=@('owner-ui','service','installer') },
    @{ product='MCP'; deps='mcp/MetaBrain.Mcp.deps.json'; name=('MetaBrain-MCP-' + $version + '-win-x64-' + $source + '-' + $id + '.zip'); paths=@('mcp','service') }
)
function Add-PackageLicenses([string] $Stage, [string] $DepsPath, [string] $Product) {
    $depsFile = Join-Path $bundle $DepsPath
    if (-not (Test-Path -LiteralPath $depsFile -PathType Leaf)) { throw ('Missing dependency graph for ' + $Product + ': ' + $DepsPath) }
    $deps = Get-Content -LiteralPath $depsFile -Raw | ConvertFrom-Json
    $packageRoot = Join-Path $repo 'runtime\dotnet-home\.nuget\packages'
    $licenseDirectory = Join-Path $Stage 'licenses'
    New-Item -ItemType Directory -Path $licenseDirectory | Out-Null
    $inventory = @()
    foreach ($library in $deps.libraries.PSObject.Properties) {
        $parts = $library.Name.Split('/')
        if ($parts.Count -ne 2) { throw ('Invalid dependency identity: ' + $library.Name) }
        if ($library.Value.type -eq 'project') {
            $inventory += [ordered]@{id=$parts[0];version=$parts[1];type='first-party project'}
            continue
        }
        $packageId = $parts[0] -replace '^runtimepack\.', ''
        $packageVersion = $parts[1]
        $packageDirectory = Join-Path (Join-Path $packageRoot $packageId.ToLowerInvariant()) $packageVersion
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' | Select-Object -First 1
        $nupkg = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nupkg' | Select-Object -First 1
        if (-not $nuspec -or -not $nupkg) { throw ('Dependency package metadata unavailable: ' + $packageId + '/' + $packageVersion) }
        $xml = [xml](Get-Content -LiteralPath $nuspec.FullName)
        $licenseNode = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrlNode = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $license = if ($licenseNode) { [string]$licenseNode.InnerText } elseif ($licenseUrlNode) { [string]$licenseUrlNode.InnerText } else { '' }
        if (-not $license) { throw ('Dependency has no declared license: ' + $packageId + '/' + $packageVersion) }
        $archive = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
        try {
            $licenseEntry = $archive.Entries | Where-Object { $_.FullName -match '^LICENSE(\.TXT)?$' } | Select-Object -First 1
            if (-not $licenseEntry) { throw ('Dependency license text missing from package: ' + $packageId + '/' + $packageVersion) }
            $licenseTarget = Join-Path $licenseDirectory ($packageId + '-' + $packageVersion + '-LICENSE.txt')
            $input = $licenseEntry.Open(); try { $output=[IO.File]::Create($licenseTarget); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
            $noticeEntries = @($archive.Entries | Where-Object { $_.FullName -match '^(THIRD-PARTY-NOTICES|ThirdPartyNotices)(\.TXT|\.txt)?$' })
            foreach ($noticeEntry in $noticeEntries) {
                $noticeTarget = Join-Path $licenseDirectory ($packageId + '-' + $packageVersion + '-THIRD-PARTY-NOTICES.txt')
                $input = $noticeEntry.Open(); try { $output=[IO.File]::Create($noticeTarget); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
            }
        } finally { $archive.Dispose() }
        $inventory += [ordered]@{id=$packageId;version=$packageVersion;type='runtime package';license=$license;licenseText=('licenses/' + $packageId + '-' + $packageVersion + '-LICENSE.txt');thirdPartyNotices=($noticeEntries.Count -gt 0)}
    }
    return ,$inventory
}
$dependencyInventories = @{}
$assets = foreach ($d in $definitions) {
    $stage = Join-Path $destination ($d.product + '-stage'); New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($part in $d.paths) { Copy-Item -LiteralPath (Join-Path $bundle $part) -Destination $stage -Recurse }
    $notice = @"
Meta Brain $version — $($d.product) payload

Unsigned; SHA-256 detects byte changes but does not authenticate the publisher.
Self-contained Windows x64 payload; no installed .NET SDK/runtime, source repository or PowerShell is required to run the entrypoint.
Supported OS limits: see docs/release-contract.md and the .NET 10 Windows x64 support matrix. Compatibility is not asserted for every listed SKU.
CLI and Owner UI archives are application payloads, not completed installers.
"@
    [IO.File]::WriteAllText((Join-Path $stage 'RELEASE-NOTES.txt'), $notice, [Text.UTF8Encoding]::new($false))
    $dependencyInventories[$d.product] = Add-PackageLicenses $stage $d.deps $d.product
    $thirdParty = "The publish dependency graphs contain only Meta Brain project references and these self-contained .NET runtime packages:`n"
    foreach ($dependency in $dependencyInventories[$d.product]) { if ($dependency.Contains('license')) { $thirdParty += ('- ' + $dependency.id + ' ' + $dependency.version + ' — license ' + $dependency.license + '; included text: ' + $dependency.licenseText + '; third-party notices: ' + $dependency.thirdPartyNotices + "`n") } else { $thirdParty += ('- ' + $dependency.id + ' ' + $dependency.version + ' — first-party Meta Brain project reference.' + "`n") } }
    $thirdParty += "Package-provided LICENSE and THIRD-PARTY-NOTICES files are in licenses/. Refer to those texts for notices and terms.`n"
    [IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $thirdParty, [Text.UTF8Encoding]::new($false))
    $layout = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $rel=$_.FullName.Substring($stage.Length+1).Replace('\','/'); [ordered]@{path=$rel;bytes=$_.Length;sha256=(Hash-File $_.FullName)} })
    $assetPath = Join-Path $destination $d.name
    [IO.Compression.ZipFile]::CreateFromDirectory($stage,$assetPath,[IO.Compression.CompressionLevel]::Optimal,$false)
    [ordered]@{product=$d.product;name=$d.name;bytes=(Get-Item $assetPath).Length;sha256=(Hash-File $assetPath);files=$layout}
}
Remove-Item -LiteralPath $temp -Recurse -Force
foreach ($p in @('CLI-stage','OwnerUI-stage','MCP-stage')) { Remove-Item -LiteralPath (Join-Path $destination $p) -Recurse -Force }
$manifest = [ordered]@{
    schemaVersion=1; product='Meta Brain'; productVersion=$version; runtimeIdentifier='win-x64'; selfContained=$true
    source=[ordered]@{head=$head;sourceTreeSha256=$treeHash;identity=$source;workingTreeIncluded=$true}
    candidateId=$id; assembledAtUtc=[DateTime]::UtcNow.ToString('O')
    signing='unsigned; SHA-256 does not authenticate publisher'
    supportedPlatforms='64-bit Windows client/server releases supported by .NET 10 at publication time and satisfying required desktop/WPF capabilities; exact product-tested SKU coverage is not asserted.'
    knownLimits=@('No Linux, macOS, ARM, unsupported Windows editions/builds, Server Core or Nano UI support.','CLI and Owner UI archives are payloads, not completed installers.','Public release remains owner-controlled; no publication is authorized by assembly.')
    compatibility=[ordered]@{cli='cli/MetaBrain.Cli.exe --help|--version';ownerUi='owner-ui/MetaBrain.OwnerUi.exe';mcp='mcp/MetaBrain.Mcp.exe; stdio transport; configure client command/args to this path.'}
    notes='SHA-256 is an integrity check, not publisher authentication. Package graph and per-package license/notice inventory are included; review the bundled license texts before distribution.'
    dependencyInventories=$dependencyInventories
    assets=$assets
}
[IO.File]::WriteAllText((Join-Path $destination 'candidate-manifest.json'),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Verify-Candidate $destination
Write-Host ('CANDIDATE ' + $destination)
