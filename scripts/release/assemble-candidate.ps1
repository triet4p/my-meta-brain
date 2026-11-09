<#
.SYNOPSIS
    Assemble immutable Sprint 10 setup and MCP candidate assets from this checkout.
.DESCRIPTION
    Reuses the existing self-contained win-x64 bundle. The CLI becomes a
    standalone setup embedding the exact component/runtime payload; MCP remains
    independently consumable. Releases from 1.2.0 ship the CLI and the MCP server
    only (CR03/D089); no Owner UI asset is assembled. Identity binds HEAD and a
    digest of tracked source plus tracked working-tree content. Output is never
    overwritten.
#>
[CmdletBinding()]
param([string] $OutputRoot = '', [switch] $VerifyOnly, [string] $CandidateDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$toolchain = Join-Path $repo 'runtime\dotnet-home'
$env:DOTNET_CLI_HOME = $toolchain
$env:NUGET_PACKAGES = Join-Path $toolchain '.nuget\packages'
$env:HOME = $toolchain
$runtimeGuard = Join-Path $repo 'runtime\release-setup-runtime'
$env:TEMP = Join-Path $runtimeGuard 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $runtimeGuard 'bundle-extract'
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
New-Item -ItemType Directory -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Force | Out-Null
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $toolchain 'bundle-extract'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
 $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props'))
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'Product version is missing from Directory.Build.props.' }
function Hash-File([string] $Path) { $stream=[IO.File]::OpenRead($Path); try { $sha=[Security.Cryptography.SHA256]::Create(); try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() } } finally { $stream.Dispose() } }
 $nativeTokenizerSha256 = '4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d'
 $nativeTokenizerBytes = 3198464
 $nativeSourceCommit = 'b8eaa77d72bf9d9c768d75599bfa6a602e467b93'
 $nativeSourceArchive = Join-Path $repo 'runtime\s15-t6-native-build\tokenizers-dotnet-b8eaa77d-source.zip'
 $nativeSourceArchiveBytes = 1236124
 $nativeSourceArchiveSha256 = '2ee9c2d81aba55396ca67544765bd5cf2438ff35b5a959fe820915e449f77ded'
 $nativeCargoLock = Join-Path $repo 'src\MetaBrain.Connections\native\provenance\Cargo.lock'
 $nativeCargoLockSha256 = '7597e8861f7368d02ea5daded9b71cbc226286ffbf1bbe7ad57f3d719b53d48f'
 $nativeNotices = Join-Path $repo 'src\MetaBrain.Connections\native\THIRD-PARTY-NOTICES.txt'
 $nativeNoticesSha256 = if (Test-Path -LiteralPath $nativeNotices -PathType Leaf) { Hash-File $nativeNotices } else { '' }
 $nativeNoticesBytes = if (Test-Path -LiteralPath $nativeNotices -PathType Leaf) { (Get-Item -LiteralPath $nativeNotices).Length } else { 0 }
 $nativeNoticePath = 'service/licenses/native-tokenizer/THIRD-PARTY-NOTICES.txt'
 $tensorPackageLicenseRoot = Join-Path $repo 'runtime\s15-t6-package-license\system-numerics-tensors-10.0.14'
 $tensorPackageLicenseSource = Join-Path $tensorPackageLicenseRoot 'dotnet-vmr-LICENSE.TXT'
 $tensorPackageCopyrightSource = Join-Path $tensorPackageLicenseRoot 'System.Numerics.Tensors-10.0.14-COPYRIGHT.txt'
 $tensorPackageCopyright = ([string][char]0x00A9) + ' Microsoft Corporation. All rights reserved.'
 $tensorPackageLicenseMetadata = [ordered]@{
     id='System.Numerics.Tensors'
     version='10.0.14'
     license='MIT'
     licenseText='licenses/System.Numerics.Tensors-10.0.14-LICENSE.txt'
     licenseTextBytes=1115
     licenseTextSha256='ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b'
     copyright=$tensorPackageCopyright
     copyrightText='licenses/System.Numerics.Tensors-10.0.14-COPYRIGHT.txt'
     copyrightTextBytes=46
     copyrightTextSha256='30bedab2f6c25a864a0c7ceac40f5c4ce1816684db188e87644e72acf6a2b8c3'
     provenanceText='licenses/System.Numerics.Tensors-10.0.14-PROVENANCE.txt'
     sourceRepository='https://github.com/dotnet/dotnet'
     sourceCommit='0427b9abbead3fa64c6655eb4290c537397131fa'
     sourceLicensePath='LICENSE.TXT'
     sourceLicenseUrl='https://raw.githubusercontent.com/dotnet/dotnet/0427b9abbead3fa64c6655eb4290c537397131fa/LICENSE.TXT'
     repositoryNoticeUrl='https://raw.githubusercontent.com/dotnet/dotnet/0427b9abbead3fa64c6655eb4290c537397131fa/THIRD-PARTY-NOTICES.txt'
     repositoryNoticeSha256='5a90be9000b5c2f928dead74be4e86952420d1160fa38f6f2d34526eeee49ff6'
     repositoryNoticeScope='Monorepo-wide source notice; staged for provenance and not presented as package-specific.'
     licenseDocumentationUrl='https://licenses.nuget.org/MIT'
     licenseDocumentationSha256='0ff20ddac3d7ee3c94cd6b60bef6537b4c4f6815063ab28228ab8ca563b1fe8e'
     spdxDocumentationUrl='https://spdx.org/licenses/MIT.html'
     spdxDocumentationSha256='b7eaaa2c58e7b36f76e1562cb4070a1e15ad52ee0a7386c724039bb929d3b078'
     packageNoticePath='licenses/System.Numerics.Tensors-10.0.14-THIRD-PARTY-NOTICES.txt'
     packageNoticeBytes=78041
     packageNoticeSha256='6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21'
     packageNoticeMember='THIRD-PARTY-NOTICES.TXT'
 }
 $tensorLicenseEvidenceFiles = @(
     [ordered]@{path=(Join-Path $tensorPackageLicenseRoot 'dotnet-vmr-LICENSE.TXT');bytes=1115;sha256='ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b'}
     [ordered]@{path=(Join-Path $tensorPackageLicenseRoot 'dotnet-vmr-THIRD-PARTY-NOTICES.txt');bytes=265586;sha256='5a90be9000b5c2f928dead74be4e86952420d1160fa38f6f2d34526eeee49ff6'}
     [ordered]@{path=(Join-Path $tensorPackageLicenseRoot 'System.Numerics.Tensors-README.md');bytes=122;sha256='2ab3b3eb4a6e7e398c631d7187a3f9b99560405387dd0cee9013dc1f202b935e'}
     [ordered]@{path=(Join-Path $tensorPackageLicenseRoot 'licenses.nuget.org-MIT.html');bytes=2442;sha256='0ff20ddac3d7ee3c94cd6b60bef6537b4c4f6815063ab28228ab8ca563b1fe8e'}
     [ordered]@{path=(Join-Path $tensorPackageLicenseRoot 'spdx-MIT.html');bytes=13452;sha256='b7eaaa2c58e7b36f76e1562cb4070a1e15ad52ee0a7386c724039bb929d3b078'}
     [ordered]@{path=$tensorPackageCopyrightSource;bytes=46;sha256='30bedab2f6c25a864a0c7ceac40f5c4ce1816684db188e87644e72acf6a2b8c3'}
 )
 foreach ($evidenceFile in $tensorLicenseEvidenceFiles) {
     if (-not (Test-Path -LiteralPath $evidenceFile.path -PathType Leaf) -or
         (Get-Item -LiteralPath $evidenceFile.path).Length -ne $evidenceFile.bytes -or
         (Hash-File $evidenceFile.path) -cne $evidenceFile.sha256) {
         throw ('Pinned Tensor license evidence is missing or has an unexpected identity: ' + $evidenceFile.path)
     }
 }
 $nativeTokenizerMetadata = [ordered]@{
     path='service/hf_tokenizers.dll'
     bytes=$nativeTokenizerBytes
     sha256=$nativeTokenizerSha256
     target='x86_64-pc-windows-msvc'
     sourceRepository='https://github.com/sappho192/Tokenizers.DotNet'
     sourceCommit=$nativeSourceCommit
     sourceArchive='runtime/s15-t6-native-build/tokenizers-dotnet-b8eaa77d-source.zip'
     sourceArchiveBytes=$nativeSourceArchiveBytes
     sourceArchiveSha256=$nativeSourceArchiveSha256
     cargoLock='src/MetaBrain.Connections/native/provenance/Cargo.lock'
     cargoLockSha256=$nativeCargoLockSha256
     cargoPackageCount=86
     registryPackageCount=85
     thirdPartyNotices=$nativeNoticePath
     thirdPartyNoticesBytes=$nativeNoticesBytes
     thirdPartyNoticesSha256=$nativeNoticesSha256
 }
 $runtimePrerequisiteMetadata = [ordered]@{
     name='Microsoft Visual C++ v14 x64 Redistributable'
     minimumVersion='14.43.34808'
     architecture='x64'
     downloadPage='https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170'
     installedSeparatelyByOwner=$true
     bundled=$false
     applicationDownloadsOrInstalls=$false
     ucrtNote='Windows 10+ includes the OS Universal CRT; it does not supply the VCRUNTIME140.dll and VCRUNTIME140_1.dll imports.'
 }
 if (-not (Test-Path -LiteralPath $nativeSourceArchive -PathType Leaf) -or
     (Get-Item -LiteralPath $nativeSourceArchive).Length -ne $nativeSourceArchiveBytes -or
     (Hash-File $nativeSourceArchive) -ne $nativeSourceArchiveSha256) {
     throw 'The pinned native tokenizer source archive is missing or has an unexpected identity.'
 }
 if (-not (Test-Path -LiteralPath $nativeCargoLock -PathType Leaf) -or
     (Hash-File $nativeCargoLock) -ne $nativeCargoLockSha256) {
     throw 'The pinned native tokenizer Cargo.lock is missing or has an unexpected identity.'
 }
 if (-not $nativeNoticesSha256) {
     throw 'The native tokenizer third-party notices are missing.'
 }
 function Assert-NativeMetadata($nativeTokenizer, $runtimePrerequisite) {
     if ($null -eq $nativeTokenizer -or
         $nativeTokenizer.path -cne 'service/hf_tokenizers.dll' -or
         $nativeTokenizer.bytes -ne $nativeTokenizerBytes -or
         $nativeTokenizer.sha256 -cne $nativeTokenizerSha256 -or
         $nativeTokenizer.target -cne 'x86_64-pc-windows-msvc' -or
         $nativeTokenizer.sourceRepository -cne 'https://github.com/sappho192/Tokenizers.DotNet' -or
         $nativeTokenizer.sourceCommit -cne $nativeSourceCommit -or
         $nativeTokenizer.sourceArchive -cne 'runtime/s15-t6-native-build/tokenizers-dotnet-b8eaa77d-source.zip' -or
         $nativeTokenizer.sourceArchiveBytes -ne $nativeSourceArchiveBytes -or
         $nativeTokenizer.sourceArchiveSha256 -cne $nativeSourceArchiveSha256 -or
         $nativeTokenizer.cargoLock -cne 'src/MetaBrain.Connections/native/provenance/Cargo.lock' -or
         $nativeTokenizer.cargoLockSha256 -cne $nativeCargoLockSha256 -or
         $nativeTokenizer.cargoPackageCount -ne 86 -or
         $nativeTokenizer.registryPackageCount -ne 85 -or
         $nativeTokenizer.thirdPartyNotices -cne $nativeNoticePath -or
         $nativeTokenizer.thirdPartyNoticesBytes -ne $nativeNoticesBytes -or
         $nativeTokenizer.thirdPartyNoticesSha256 -cne $nativeNoticesSha256) {
         throw 'Native tokenizer package provenance or identity metadata is incomplete or unexpected.'
     }
     if ($null -eq $runtimePrerequisite -or
         $runtimePrerequisite.name -cne 'Microsoft Visual C++ v14 x64 Redistributable' -or
         $runtimePrerequisite.minimumVersion -cne '14.43.34808' -or
         $runtimePrerequisite.architecture -cne 'x64' -or
         $runtimePrerequisite.downloadPage -cne 'https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170' -or
         $runtimePrerequisite.installedSeparatelyByOwner -ne $true -or
         $runtimePrerequisite.bundled -ne $false -or
         $runtimePrerequisite.applicationDownloadsOrInstalls -ne $false -or
         $runtimePrerequisite.ucrtNote -cne 'Windows 10+ includes the OS Universal CRT; it does not supply the VCRUNTIME140.dll and VCRUNTIME140_1.dll imports.') {
         throw 'The external Visual C++ runtime prerequisite metadata is incomplete or unexpected.'
     }
 }
 function Assert-NativePackageFiles($files) {
     $nativeDll = @($files | Where-Object { $_.path -ceq 'service/hf_tokenizers.dll' })
     if ($nativeDll.Count -ne 1 -or
         $nativeDll[0].bytes -ne $nativeTokenizerBytes -or
         $nativeDll[0].sha256 -cne $nativeTokenizerSha256) {
         throw 'The product package does not contain the exact pinned native tokenizer payload.'
     }
     $nativeNotices = @($files | Where-Object { $_.path -ceq $nativeNoticePath })
     if ($nativeNotices.Count -ne 1 -or
         $nativeNotices[0].bytes -ne $nativeNoticesBytes -or
         $nativeNotices[0].sha256 -cne $nativeNoticesSha256) {
         throw 'The product package does not contain the exact pinned native tokenizer license notices.'
     }
 }
 function Assert-PackageManifestFiles($packageFiles, $actualFiles) {
     $listedFiles = @($packageFiles | Sort-Object -Property path)
     $actualPackageFiles = @($actualFiles | Where-Object { $_.path -cne 'package-manifest.json' } | Sort-Object -Property path)
     if ($listedFiles.Count -ne $actualPackageFiles.Count) {
         throw 'Product package manifest file inventory count mismatch.'
     }
     for ($index = 0; $index -lt $listedFiles.Count; $index++) {
         if ($listedFiles[$index].path -cne $actualPackageFiles[$index].path -or
             $listedFiles[$index].bytes -ne $actualPackageFiles[$index].bytes -or
             $listedFiles[$index].sha256 -cne $actualPackageFiles[$index].sha256) {
             throw ('Product package manifest file inventory mismatch: ' + $actualPackageFiles[$index].path)
         }
     }
 }
function Assert-TensorLicenseMetadata($dependencies, $actualFiles) {
    $tensor = @($dependencies | Where-Object { $_.id -ceq $tensorPackageLicenseMetadata.id -and $_.version -ceq $tensorPackageLicenseMetadata.version })
    if ($tensor.Count -ne 1) { throw 'The product package manifest must contain exactly one pinned System.Numerics.Tensors license record.' }
    $entry = $tensor[0]
    if ($entry.license -cne $tensorPackageLicenseMetadata.license -or
        $entry.licenseText -cne $tensorPackageLicenseMetadata.licenseText -or
        $entry.licenseTextBytes -ne $tensorPackageLicenseMetadata.licenseTextBytes -or
        $entry.licenseTextSha256 -cne $tensorPackageLicenseMetadata.licenseTextSha256 -or
        $entry.packageCopyright -cne $tensorPackageLicenseMetadata.copyright -or
        $entry.packageCopyrightFile -cne $tensorPackageLicenseMetadata.copyrightText -or
        $entry.packageCopyrightBytes -ne $tensorPackageLicenseMetadata.copyrightTextBytes -or
        $entry.packageCopyrightSha256 -cne $tensorPackageLicenseMetadata.copyrightTextSha256 -or
        $entry.licenseSourceRepository -cne $tensorPackageLicenseMetadata.sourceRepository -or
        $entry.licenseSourceCommit -cne $tensorPackageLicenseMetadata.sourceCommit -or
        $entry.licenseSourcePath -cne $tensorPackageLicenseMetadata.sourceLicensePath -or
        $entry.licenseSourceUrl -cne $tensorPackageLicenseMetadata.sourceLicenseUrl -or
        $entry.licenseDocumentationUrl -cne $tensorPackageLicenseMetadata.licenseDocumentationUrl -or
        $entry.licenseDocumentationSha256 -cne $tensorPackageLicenseMetadata.licenseDocumentationSha256 -or
        $entry.spdxDocumentationUrl -cne $tensorPackageLicenseMetadata.spdxDocumentationUrl -or
        $entry.spdxDocumentationSha256 -cne $tensorPackageLicenseMetadata.spdxDocumentationSha256 -or
        $entry.upstreamRepositoryNoticeUrl -cne $tensorPackageLicenseMetadata.repositoryNoticeUrl -or
        $entry.upstreamRepositoryNoticeSha256 -cne $tensorPackageLicenseMetadata.repositoryNoticeSha256 -or
        $entry.upstreamRepositoryNoticeScope -cne $tensorPackageLicenseMetadata.repositoryNoticeScope -or
        $entry.provenanceText -cne $tensorPackageLicenseMetadata.provenanceText -or
        $entry.thirdPartyNotices -ne $true -or
        $entry.thirdPartyNoticeText -cne $tensorPackageLicenseMetadata.packageNoticePath -or
        $entry.thirdPartyNoticeBytes -ne $tensorPackageLicenseMetadata.packageNoticeBytes -or
        $entry.thirdPartyNoticeSha256 -cne $tensorPackageLicenseMetadata.packageNoticeSha256) {
        throw 'System.Numerics.Tensors package license or attribution provenance is incomplete or unexpected.'
    }
    foreach ($expected in @(
        [ordered]@{path=$tensorPackageLicenseMetadata.licenseText;bytes=$tensorPackageLicenseMetadata.licenseTextBytes;sha256=$tensorPackageLicenseMetadata.licenseTextSha256}
        [ordered]@{path=$tensorPackageLicenseMetadata.copyrightText;bytes=$tensorPackageLicenseMetadata.copyrightTextBytes;sha256=$tensorPackageLicenseMetadata.copyrightTextSha256}
        [ordered]@{path=$tensorPackageLicenseMetadata.provenanceText;bytes=0;sha256=''}
        [ordered]@{path=$tensorPackageLicenseMetadata.packageNoticePath;bytes=$tensorPackageLicenseMetadata.packageNoticeBytes;sha256=$tensorPackageLicenseMetadata.packageNoticeSha256}
    )) {
        $actual = @($actualFiles | Where-Object { $_.path -ceq $expected.path })
        if ($actual.Count -ne 1 -or
            ($expected.bytes -gt 0 -and $actual[0].bytes -ne $expected.bytes) -or
            ($expected.bytes -eq 0 -and ($actual[0].bytes -le 0 -or $actual[0].sha256 -notmatch '^[0-9a-f]{64}$')) -or
            ($expected.sha256 -and $actual[0].sha256 -cne $expected.sha256)) {
            throw ('The product package is missing pinned Tensor license evidence: ' + $expected.path)
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$entry.provenanceText)) {
        throw 'System.Numerics.Tensors license provenance is missing.'
    }
}
function Verify-Candidate([string] $Root) {
    $manifestPath = Join-Path $Root 'candidate-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Candidate manifest is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.productVersion -ne $version -or $manifest.runtimeIdentifier -ne 'win-x64') { throw 'Candidate version or runtime identity mismatch.' }
     Assert-NativeMetadata $manifest.nativeTokenizer $manifest.runtimePrerequisite
    foreach ($asset in $manifest.assets) {
        $path = Join-Path $Root $asset.name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Candidate asset missing: ' + $asset.name) }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne $asset.bytes -or (Hash-File $path) -ne $asset.sha256) { throw ('Candidate asset integrity mismatch: ' + $asset.name) }
        if ($asset.product -eq 'CLI') {
            $files = @($asset.files)
            if ($files.Count -ne 1 -or $files[0].path -cne $asset.name -or
                $files[0].bytes -ne $asset.bytes -or $files[0].sha256 -cne $asset.sha256) {
                throw ('Setup file manifest mismatch: ' + $asset.name)
            }
            if ($asset.payload.bytes -le 0 -or $asset.payload.sha256 -notmatch '^[0-9a-f]{64}$') {
                throw ('Setup payload provenance is incomplete: ' + $asset.name)
            }
            $bundleCacheRoot = Join-Path $repo 'runtime\.net'
            $bundleCacheRootExisted = Test-Path -LiteralPath $bundleCacheRoot
            New-Item -ItemType Directory -Path $bundleCacheRoot -Force | Out-Null
            $verificationBundleRoot = Join-Path $bundleCacheRoot ($manifest.candidateId + '-' + $asset.product)
            if (Test-Path -LiteralPath $verificationBundleRoot) { throw 'A previous setup verification extraction remains; inspect it before retrying.' }
            New-Item -ItemType Directory -Path $verificationBundleRoot | Out-Null
            $previousBundleRoot = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
            try {
                $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $verificationBundleRoot
                $inspectionText = (& $path --verify-payload) -join "`n"
                $inspectionExitCode = $LASTEXITCODE
                $inspection = $inspectionText | ConvertFrom-Json
            } finally {
                if ($null -eq $previousBundleRoot) {
                    Remove-Item Env:\DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
                } else {
                    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousBundleRoot
                }
                if (Test-Path -LiteralPath $verificationBundleRoot) {
                    Remove-Item -LiteralPath $verificationBundleRoot -Recurse -Force
                }
                if (-not $bundleCacheRootExisted -and
                    (Get-ChildItem -LiteralPath $bundleCacheRoot -Force | Measure-Object).Count -eq 0) {
                    Remove-Item -LiteralPath $bundleCacheRoot -Force
                }
            }
            if ($inspectionExitCode -ne 0) { throw ('Setup payload inspection failed: ' + $asset.name) }
            if ($inspection.bytes -ne $asset.payload.bytes -or $inspection.sha256 -cne $asset.payload.sha256) {
                throw ('Setup embedded payload hash mismatch: ' + $asset.name)
            }
            $actualFiles = @($inspection.files | Sort-Object -Property path)
            $expectedFiles = @($asset.payload.files | Sort-Object -Property path)
            if ($actualFiles.Count -ne $expectedFiles.Count) { throw ('Setup embedded payload file count mismatch: ' + $asset.name) }
            for ($index = 0; $index -lt $actualFiles.Count; $index++) {
                if ($actualFiles[$index].path -cne $expectedFiles[$index].path -or
                    $actualFiles[$index].bytes -ne $expectedFiles[$index].bytes -or
                    $actualFiles[$index].sha256 -cne $expectedFiles[$index].sha256) {
                    throw ('Setup embedded payload member mismatch: ' + $actualFiles[$index].path)
                }
            }
             Assert-NativePackageFiles $actualFiles
             Assert-NativeMetadata $inspection.packageManifest.nativeTokenizer $inspection.packageManifest.runtimePrerequisite
             Assert-PackageManifestFiles $inspection.packageManifest.files $actualFiles
             Assert-TensorLicenseMetadata $inspection.packageManifest.dependencies $actualFiles
             if ($inspection.packageManifest.product -cne 'Meta Brain CLI' -or
                 $inspection.packageManifest.productVersion -ne $version -or
                 $inspection.packageManifest.runtimeIdentifier -ne 'win-x64' -or
                 $inspection.packageManifest.selfContained -ne $true -or
                 $inspection.packageManifest.source.head -cne $manifest.source.head -or
                 $inspection.packageManifest.source.sourceTreeSha256 -cne $manifest.source.sourceTreeSha256) {
                 throw 'CLI setup embedded package manifest identity mismatch.'
             }
        }
        else {
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
                if ($asset.product -eq 'MCP') {
                    $manifestEntry = $zip.GetEntry('package-manifest.json')
                    if ($null -eq $manifestEntry) { throw 'MCP package manifest is missing.' }
                    $manifestStream = $manifestEntry.Open()
                    try {
                        $reader = [IO.StreamReader]::new($manifestStream, [Text.Encoding]::UTF8)
                        try { $packageManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                    } finally { $manifestStream.Dispose() }
                    if ($packageManifest.product -ne 'Meta Brain MCP' -or
                        $packageManifest.productVersion -ne $version -or
                        $packageManifest.runtimeIdentifier -ne 'win-x64' -or
                        $packageManifest.selfContained -ne $true -or
                        $packageManifest.entrypoint -ne 'mcp/MetaBrain.Mcp.exe' -or
                        $packageManifest.sharedRuntime -ne 'service/MetaBrain.Connections.exe' -or
                        $packageManifest.command -ne 'mcp/MetaBrain.Mcp.exe' -or
                        (@($packageManifest.args.dataRoot) -join "`n") -cne "--data-root`n<data-root>" -or
                        (@($packageManifest.args.config) -join "`n") -cne "--config`n<service-settings.json>" -or
                        $packageManifest.transport -ne 'stdio') {
                        throw 'MCP package manifest identity or invocation contract mismatch.'
                    }
                     Assert-NativePackageFiles $asset.files
                     Assert-NativeMetadata $packageManifest.nativeTokenizer $packageManifest.runtimePrerequisite
                     Assert-PackageManifestFiles $packageManifest.files $asset.files
                     Assert-TensorLicenseMetadata $packageManifest.dependencies $asset.files
                     if ($packageManifest.source.head -cne $manifest.source.head -or
                         $packageManifest.source.sourceTreeSha256 -cne $manifest.source.sourceTreeSha256) {
                         throw 'MCP package manifest source identity mismatch.'
                     }
                    }
            } finally { $zip.Dispose() }
        }
    }
    if (@($manifest.assets).Count -ne 2) { throw 'Candidate must contain exactly two product assets: the CLI setup and the MCP package.' }
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
    @{ product='CLI'; deps='cli/MetaBrain.Cli.deps.json'; name=('MetaBrain-CLI-' + $version + '-win-x64-' + $source + '-' + $id + '.exe'); paths=@('cli','service','installer') },
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
        $xml = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw)
        $licenseNode = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrlNode = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $copyrightNode = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='copyright']")
        $repositoryNode = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']")
        $license = if ($licenseNode) { [string]$licenseNode.InnerText } elseif ($licenseUrlNode) { [string]$licenseUrlNode.InnerText } else { '' }
        if (-not $license) { throw ('Dependency has no declared license: ' + $packageId + '/' + $packageVersion) }
        $isTensorPackage = $packageId -ceq $tensorPackageLicenseMetadata.id -and $packageVersion -ceq $tensorPackageLicenseMetadata.version
        $archive = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
        try {
            $licenseEntry = $archive.Entries | Where-Object { $_.FullName -match '^LICENSE(\.TXT)?$' } | Select-Object -First 1
            $licenseTextPath = 'licenses/' + $packageId + '-' + $packageVersion + '-LICENSE.txt'
            if ($isTensorPackage) {
                if ($license -cne $tensorPackageLicenseMetadata.license) {
                    throw ('System.Numerics.Tensors declares an unexpected license expression: ' + $license)
                }
                if (-not $licenseUrlNode -or [string]$licenseUrlNode.InnerText -cne $tensorPackageLicenseMetadata.licenseDocumentationUrl) {
                    throw ('System.Numerics.Tensors declares an unexpected license URL: ' + [string]$licenseUrlNode.InnerText)
                }
                if (-not $copyrightNode -or [string]$copyrightNode.InnerText -cne $tensorPackageLicenseMetadata.copyright) {
                    throw ('System.Numerics.Tensors has unexpected package copyright metadata: ' + [string]$copyrightNode.InnerText)
                }
                if (-not $repositoryNode) { throw 'System.Numerics.Tensors package repository metadata is missing.' }
                if ($repositoryNode.GetAttribute('url') -cne $tensorPackageLicenseMetadata.sourceRepository) {
                    throw ('System.Numerics.Tensors declares an unexpected source repository: ' + $repositoryNode.GetAttribute('url'))
                }
                if ($repositoryNode.GetAttribute('commit') -cne $tensorPackageLicenseMetadata.sourceCommit) {
                    throw ('System.Numerics.Tensors declares an unexpected source commit: ' + $repositoryNode.GetAttribute('commit'))
                }
                $licenseTextPath = $tensorPackageLicenseMetadata.licenseText
                Copy-Item -LiteralPath $tensorPackageLicenseSource -Destination (Join-Path $Stage $licenseTextPath)
                Copy-Item -LiteralPath $tensorPackageCopyrightSource -Destination (Join-Path $Stage $tensorPackageLicenseMetadata.copyrightText)
                $provenanceText = @"
System.Numerics.Tensors 10.0.14 license-source provenance

Package metadata declares the MIT license expression and the package copyright in its nuspec.
Package copyright (kept separate from upstream license text): $($tensorPackageLicenseMetadata.copyright)
Official repository: $($tensorPackageLicenseMetadata.sourceRepository)
Exact repository commit: $($tensorPackageLicenseMetadata.sourceCommit)
License text path: $($tensorPackageLicenseMetadata.sourceLicensePath)
License text URL: $($tensorPackageLicenseMetadata.sourceLicenseUrl)
License text SHA-256: $($tensorPackageLicenseMetadata.licenseTextSha256)
Nuspec license documentation: $($tensorPackageLicenseMetadata.licenseDocumentationUrl)
Nuspec license documentation SHA-256: $($tensorPackageLicenseMetadata.licenseDocumentationSha256)
Direct SPDX documentation: $($tensorPackageLicenseMetadata.spdxDocumentationUrl)
Direct SPDX documentation SHA-256: $($tensorPackageLicenseMetadata.spdxDocumentationSha256)
Pinned repository-root notice: $($tensorPackageLicenseMetadata.repositoryNoticeUrl)
Repository-root notice SHA-256: $($tensorPackageLicenseMetadata.repositoryNoticeSha256)
Repository-root notice scope: $($tensorPackageLicenseMetadata.repositoryNoticeScope)
Package third-party notices are copied from the package archive member $($tensorPackageLicenseMetadata.packageNoticeMember), not inferred from the monorepo-wide notice.
"@
                [IO.File]::WriteAllText((Join-Path $Stage $tensorPackageLicenseMetadata.provenanceText), $provenanceText, [Text.UTF8Encoding]::new($false))
            } elseif ($licenseEntry) {
                $licenseTarget = Join-Path $Stage $licenseTextPath
                $input = $licenseEntry.Open(); try { $output=[IO.File]::Create($licenseTarget); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
            } elseif ($packageId -eq 'Tokenizers.DotNet' -and (Test-Path -LiteralPath (Join-Path $Stage $nativeNoticePath) -PathType Leaf)) {
                $licenseTextPath = $nativeNoticePath
            } else {
                throw ('Dependency license text missing from package: ' + $packageId + '/' + $packageVersion)
            }
            $noticeEntries = @($archive.Entries | Where-Object { $_.FullName -match '^(THIRD-PARTY-NOTICES|ThirdPartyNotices)(\.TXT|\.txt)?$' })
            if ($isTensorPackage -and
                ($noticeEntries.Count -ne 1 -or $noticeEntries[0].FullName -cne $tensorPackageLicenseMetadata.packageNoticeMember)) {
                throw 'System.Numerics.Tensors package third-party notice member is missing or unexpected.'
            }
            foreach ($noticeEntry in $noticeEntries) {
                $noticeTarget = Join-Path $licenseDirectory ($packageId + '-' + $packageVersion + '-THIRD-PARTY-NOTICES.txt')
                $input = $noticeEntry.Open(); try { $output=[IO.File]::Create($noticeTarget); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
            }
            if ($isTensorPackage) {
                $tensorNoticeTarget = Join-Path $Stage $tensorPackageLicenseMetadata.packageNoticePath
                if ((Get-Item -LiteralPath $tensorNoticeTarget).Length -ne $tensorPackageLicenseMetadata.packageNoticeBytes -or
                    (Hash-File $tensorNoticeTarget) -cne $tensorPackageLicenseMetadata.packageNoticeSha256) {
                    throw 'System.Numerics.Tensors package third-party notice has an unexpected identity.'
                }
            }
        } finally { $archive.Dispose() }
        $packageInventory = [ordered]@{
            id=$packageId
            version=$packageVersion
            type='runtime package'
            license=$license
            licenseText=$licenseTextPath
            thirdPartyNotices=($noticeEntries.Count -gt 0 -or $packageId -eq 'Tokenizers.DotNet')
        }
        if ($isTensorPackage) {
            $packageInventory['licenseTextBytes'] = $tensorPackageLicenseMetadata.licenseTextBytes
            $packageInventory['licenseTextSha256'] = $tensorPackageLicenseMetadata.licenseTextSha256
            $packageInventory['packageCopyright'] = $tensorPackageLicenseMetadata.copyright
            $packageInventory['packageCopyrightFile'] = $tensorPackageLicenseMetadata.copyrightText
            $packageInventory['packageCopyrightBytes'] = $tensorPackageLicenseMetadata.copyrightTextBytes
            $packageInventory['packageCopyrightSha256'] = $tensorPackageLicenseMetadata.copyrightTextSha256
            $packageInventory['provenanceText'] = $tensorPackageLicenseMetadata.provenanceText
            $packageInventory['licenseSourceRepository'] = $tensorPackageLicenseMetadata.sourceRepository
            $packageInventory['licenseSourceCommit'] = $tensorPackageLicenseMetadata.sourceCommit
            $packageInventory['licenseSourcePath'] = $tensorPackageLicenseMetadata.sourceLicensePath
            $packageInventory['licenseSourceUrl'] = $tensorPackageLicenseMetadata.sourceLicenseUrl
            $packageInventory['licenseDocumentationUrl'] = $tensorPackageLicenseMetadata.licenseDocumentationUrl
            $packageInventory['licenseDocumentationSha256'] = $tensorPackageLicenseMetadata.licenseDocumentationSha256
            $packageInventory['spdxDocumentationUrl'] = $tensorPackageLicenseMetadata.spdxDocumentationUrl
            $packageInventory['spdxDocumentationSha256'] = $tensorPackageLicenseMetadata.spdxDocumentationSha256
            $packageInventory['upstreamRepositoryNoticeUrl'] = $tensorPackageLicenseMetadata.repositoryNoticeUrl
            $packageInventory['upstreamRepositoryNoticeSha256'] = $tensorPackageLicenseMetadata.repositoryNoticeSha256
            $packageInventory['upstreamRepositoryNoticeScope'] = $tensorPackageLicenseMetadata.repositoryNoticeScope
            $packageInventory['thirdPartyNoticeText'] = $tensorPackageLicenseMetadata.packageNoticePath
            $packageInventory['thirdPartyNoticeBytes'] = $tensorPackageLicenseMetadata.packageNoticeBytes
            $packageInventory['thirdPartyNoticeSha256'] = $tensorPackageLicenseMetadata.packageNoticeSha256
        }
        $inventory += $packageInventory
    }
    return ,$inventory
}
$dependencyInventories = @{}
$assets = foreach ($d in $definitions) {
$stage = Join-Path $destination ($d.product + '-stage'); New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($part in $d.paths) { Copy-Item -LiteralPath (Join-Path $bundle $part) -Destination $stage -Recurse }
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
     if ($d.product -eq 'CLI') {
         $runtimeNote = 'The CLI setup requires the owner-installed Microsoft Visual C++ v14 x64 Redistributable version 14.43.34808 or later. Setup checks the x64 version and refuses before extracting or changing application/data files if it is missing or too old. Install it separately from https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170, then rerun setup. Meta Brain does not download, bundle, or install the runtime. Windows 10+ includes the OS Universal CRT; it does not supply the VCRUNTIME140.dll and VCRUNTIME140_1.dll imports. Setup temporarily extracts its payload under the current user''s %TEMP% and may use the .NET bundle cache; it changes no global/profile environment.'
     } else {
         $runtimeNote = 'The MCP server requires the owner-installed Microsoft Visual C++ v14 x64 Redistributable version 14.43.34808 or later. It checks the x64 version and refuses before creating a data root if it is missing or too old. Install it separately from https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170, then rerun the MCP entrypoint. Meta Brain does not download, bundle, or install the runtime. Windows 10+ includes the OS Universal CRT; it does not supply the VCRUNTIME140.dll and VCRUNTIME140_1.dll imports. Extract the complete ZIP and register mcp/MetaBrain.Mcp.exe as the absolute stdio command with --data-root <owner data-root> or --config <service-settings.json>.'
     }
     $notice = @"
Meta Brain $version — $($d.product) distribution

Project license: MIT; see LICENSE. Copyright (c) 2026 triet4p.
Third-party .NET dependency licenses are in licenses/. The complete native tokenizer notices are in $nativeNoticePath.
Unsigned; SHA-256 detects byte changes but does not authenticate the publisher.
Self-contained Windows x64 distribution; no installed .NET SDK/runtime, source repository or PowerShell is required. The separate Microsoft Visual C++ x64 runtime prerequisite is not included.
Supported OS limits: see docs/release-contract.md and the .NET 10 Windows x64 support matrix. Compatibility is not asserted for every listed SKU.
$runtimeNote
"@
     [IO.File]::WriteAllText((Join-Path $stage 'RELEASE-NOTES.txt'), $notice, [Text.UTF8Encoding]::new($false))
     $dependencyInventories[$d.product] = Add-PackageLicenses $stage $d.deps $d.product
     $thirdParty = "The .NET publish dependency graph contains these first-party projects and self-contained runtime packages:`n"
     foreach ($dependency in $dependencyInventories[$d.product]) { if ($dependency.Contains('license')) { $thirdParty += ('- ' + $dependency.id + ' ' + $dependency.version + ' — license ' + $dependency.license + '; included text: ' + $dependency.licenseText + '; third-party notices: ' + $dependency.thirdPartyNotices + "`n") } else { $thirdParty += ('- ' + $dependency.id + ' ' + $dependency.version + ' — first-party Meta Brain project reference.' + "`n") } }
     $thirdParty += "Project license: MIT (LICENSE at package root). Package-provided dependency license files are in licenses/. The native tokenizer's complete source and crate license documents are in $nativeNoticePath; its source commit, archive, lock identity, and external runtime prerequisite are in package-manifest.json.`n"
     [IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $thirdParty, [Text.UTF8Encoding]::new($false))
     $packageFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
         $relative = $_.FullName.Substring($stage.Length + 1).Replace('\','/')
         [ordered]@{path=$relative;bytes=$_.Length;sha256=(Hash-File $_.FullName)}
     })
     $packageManifest = [ordered]@{
         schemaVersion=1
         product=$(if ($d.product -eq 'CLI') { 'Meta Brain CLI' } else { 'Meta Brain MCP' })
         productVersion=$version
         runtimeIdentifier='win-x64'
         selfContained=$true
         license=[ordered]@{name='MIT';file='LICENSE';copyright='Copyright (c) 2026 triet4p'}
         candidateId=$id
         source=[ordered]@{head=$head;sourceTreeSha256=$treeHash;identity=$source}
         entrypoint=$(if ($d.product -eq 'CLI') { 'cli/MetaBrain.Cli.exe' } else { 'mcp/MetaBrain.Mcp.exe' })
         sharedRuntime='service/MetaBrain.Connections.exe'
         dependencies=$dependencyInventories[$d.product]
         nativeTokenizer=$nativeTokenizerMetadata
         runtimePrerequisite=$runtimePrerequisiteMetadata
         files=$packageFiles
     }
     if ($d.product -eq 'MCP') {
         $packageManifest['transport']='stdio'
         $packageManifest['command']='mcp/MetaBrain.Mcp.exe'
         $packageManifest['protocolVersions']=@('2025-11-25','2025-06-18','2025-03-26','2024-11-05')
         $packageManifest['args']=[ordered]@{dataRoot=@('--data-root','<data-root>');config=@('--config','<service-settings.json>')}
         $packageManifest['help']='mcp/MetaBrain.Mcp.exe --help'
         $packageManifest['version']='mcp/MetaBrain.Mcp.exe --version'
         $packageManifest['compatibility']=[ordered]@{
             codexCli=[ordered]@{configuration='process-scoped -c overrides';observedVersion='0.164.0';evidence='Sprint 9 T7 accepted package compatibility observation'}
             ohMyPi=[ordered]@{configuration='project-local .omp/mcp.json';observedVersion='18.12.6';evidence='Sprint 9 T7 accepted package compatibility observation'}
         }
         $packageManifest['knownLimits']=@(
             'Windows x64 only, on a Windows release supported by .NET 10; the exact supported Windows SKU matrix was not separately established for this MCP asset.',
             'Linux, macOS and ARM are unsupported.',
             'The MCP server exposes agent-scoped tools only; owner authority and key/bearer credentials are not passed through client configuration.',
             'Client configuration is owner-authored and scoped to the process (Codex) or project (OMP); the package does not edit profiles or configuration.',
             'Unsigned; SHA-256 verifies integrity but does not authenticate publisher identity.'
         )
     } else {
         $packageManifest['installMode']='dedicated CLI setup; installs and registers the cli component only'
         $packageManifest['installRoot']='owner-selected app directory'
         $packageManifest['dataRoot']='owner-selected data directory, kept separately from the app install root'
     }
     [IO.File]::WriteAllText((Join-Path $stage 'package-manifest.json'),($packageManifest | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $layout = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $rel=$_.FullName.Substring($stage.Length+1).Replace('\','/'); [ordered]@{path=$rel;bytes=$_.Length;sha256=(Hash-File $_.FullName)} })
    $assetPath = Join-Path $destination $d.name
    if ($d.product -eq 'CLI') {
        $payloadName = 'CLI-setup-payload.zip'
        $payloadPath = Join-Path $destination $payloadName
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $payloadPath, [IO.Compression.CompressionLevel]::Optimal, $false)
        $payloadSha256 = Hash-File $payloadPath
        $setupOutput = Join-Path $destination ($d.product + '-setup-build')
        $candidateIdentity = $source + '-' + $id
        $setupProject = Join-Path $repo 'src\MetaBrain.Installer\MetaBrain.Installer.csproj'
        $setupAssembly = 'MetaBrain.Installer.exe'
        $identityArgument = '-p:CliSetupCandidateIdentity=' + $candidateIdentity
        $payloadArgument = '-p:CliSetupPayloadPath=' + $payloadPath
        $payloadHashArgument = '-p:CliSetupPayloadSha256=' + $payloadSha256
         $dotnetRestore = & dotnet restore $setupProject -r win-x64 --source (Join-Path $repo 'runtime\dotnet-home\.nuget\packages') --nologo -p:NuGetAudit=false
        $restoreExitCode = $LASTEXITCODE
        $dotnetRestore | ForEach-Object { Write-Host $_ }
        if ($restoreExitCode -ne 0) { throw ('Offline restore failed for dedicated ' + $d.product + ' setup; no package source outside the repository cache is used.') }
        $publishOutput = & dotnet publish $setupProject -c Release -r win-x64 --self-contained true --no-restore --nologo `
            -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
            $payloadArgument $identityArgument $payloadHashArgument `
            -p:DebugType=None -p:DebugSymbols=false -o $setupOutput
        $publishExitCode = $LASTEXITCODE
        $publishOutput | ForEach-Object { Write-Host $_ }
        if ($publishExitCode -ne 0) { throw ('Dedicated ' + $d.product + ' setup publish failed.') }
        $setupExecutable = Join-Path $setupOutput $setupAssembly
        if (-not (Test-Path -LiteralPath $setupExecutable -PathType Leaf)) { throw ('Dedicated ' + $d.product + ' setup executable is missing.') }
        Copy-Item -LiteralPath $setupExecutable -Destination $assetPath
        $payloadInfo = [ordered]@{bytes=(Get-Item $payloadPath).Length;sha256=(Hash-File $payloadPath);files=$layout}
        $layout = @([ordered]@{path=$d.name;bytes=(Get-Item $assetPath).Length;sha256=(Hash-File $assetPath)})
        Remove-Item -LiteralPath $setupOutput -Recurse -Force
        Remove-Item -LiteralPath $payloadPath -Force
        [ordered]@{product=$d.product;name=$d.name;bytes=(Get-Item $assetPath).Length;sha256=(Hash-File $assetPath);files=$layout;payload=$payloadInfo}
    }
    else {
        [IO.Compression.ZipFile]::CreateFromDirectory($stage,$assetPath,[IO.Compression.CompressionLevel]::Optimal,$false)
        [ordered]@{product=$d.product;name=$d.name;bytes=(Get-Item $assetPath).Length;sha256=(Hash-File $assetPath);files=$layout}
    }
}
Remove-Item -LiteralPath $temp -Recurse -Force
foreach ($p in @('CLI-stage','MCP-stage')) { Remove-Item -LiteralPath (Join-Path $destination $p) -Recurse -Force }
$manifest = [ordered]@{
    schemaVersion=1; product='Meta Brain'; productVersion=$version; runtimeIdentifier='win-x64'; selfContained=$true
    source=[ordered]@{head=$head;sourceTreeSha256=$treeHash;identity=$source;workingTreeIncluded=$true}
     nativeTokenizer=$nativeTokenizerMetadata
     runtimePrerequisite=$runtimePrerequisiteMetadata
    candidateId=$id; assembledAtUtc=[DateTime]::UtcNow.ToString('O')
    signing='unsigned; SHA-256 does not authenticate publisher'
    supportedPlatforms='64-bit Windows client/server releases supported by .NET 10 at publication time; the CLI and MCP server need no desktop/WPF capability. Exact product-tested SKU coverage is not asserted.'
    compatibility=[ordered]@{cli='Dedicated setup EXE installs cli/MetaBrain.Cli.exe; use that installed path directly with --help or --version. It installs and registers the CLI component only.';mcp=[ordered]@{package='standalone self-contained zip';version=$version;entrypoint='mcp/MetaBrain.Mcp.exe';sharedRuntime='service/MetaBrain.Connections.exe';transport='stdio';command='absolute path to extracted mcp/MetaBrain.Mcp.exe';args=[ordered]@{dataRoot=@('--data-root','<owner data-root>');config=@('--config','<service-settings.json>')};protocolVersions=@('2025-11-25','2025-06-18','2025-03-26','2024-11-05');testedCodexCliVersion='0.164.0';testedOhMyPiVersion='18.12.6';configuration='process-scoped Codex overrides or project-local OMP .omp/mcp.json';limits='No SDK, repository, installed .NET runtime, PowerShell, key or bearer is required; no profile is edited.'};supportTransition='1.2.0 installs and registers the CLI and MCP components only. The 1.1.0 WPF Owner UI is the frozen legacy product: this release does not package a UI installer and refuses to change an installed 1.1.0 or unknown/mixed installation in place, recommending a separate 1.2 install root, data root and endpoints.'}
    notes='SHA-256 is an integrity check, not publisher authentication. Package graph and per-package license/notice inventory are included; review bundled license texts before distribution. Setups temporarily stage payload in the current user temporary directory and single-file hosts may use the .NET bundle-extraction cache; neither changes global/profile settings.'
    dependencyInventories=$dependencyInventories
    assets=$assets
}
[IO.File]::WriteAllText((Join-Path $destination 'candidate-manifest.json'),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Verify-Candidate $destination
Write-Host ('CANDIDATE ' + $destination)
