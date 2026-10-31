[CmdletBinding()]
param(
    [string]$CandidateDirectory,
    [string]$OutputDirectory,
    [string]$ReleaseDownloadBaseUrl
)
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($CandidateDirectory)) { $CandidateDirectory = Join-Path $repoRoot 'runtime\release-candidates\1.1.0-win-x64-b4a3bbbce0f0-tree3183d48f66d9-573e00d784cd' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot 'dist\site' }
$sourceDirectory = Join-Path $repoRoot 'website'
$candidateRoot = [IO.Path]::GetFullPath($CandidateDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$expectedCandidate = '573e00d784cd'
$expectedHead = 'b4a3bbbce0f0c90d32dabddef058eb2b7848f508'
$expectedTree = '3183d48f66d934c4d164b237b76a1d18e8c994c80040c195911251129ee87021'
$manifestPath = Join-Path $candidateRoot 'candidate-manifest.json'
$templatePath = Join-Path $sourceDirectory 'index.template.html'
$stylePath = Join-Path $sourceDirectory 'styles.css'
$guidePaths = @('scenarios.html', 'codex.html', 'omp.html')
$published = -not [string]::IsNullOrWhiteSpace($ReleaseDownloadBaseUrl)
if ($published) {
    $ReleaseDownloadBaseUrl = $ReleaseDownloadBaseUrl.TrimEnd('/')
    if ($ReleaseDownloadBaseUrl -cne 'https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0') {
        throw 'ReleaseDownloadBaseUrl must be the exact owner-controlled 1.1.0 GitHub Releases download URL.'
    }
}
foreach ($guidePath in $guidePaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $guidePath) -PathType Leaf)) { throw "Website guide is missing: $guidePath" }
}
$operationsPath = Join-Path $repoRoot 'docs\OPERATIONS.md'
if (-not (Test-Path -LiteralPath $operationsPath -PathType Leaf)) { throw 'Operations runbook is missing.' }

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Candidate manifest not found: $manifestPath" }
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) { throw 'Website template is missing.' }
if (-not (Test-Path -LiteralPath $stylePath -PathType Leaf)) { throw 'Website stylesheet is missing.' }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.productVersion -ne '1.1.0' -or $manifest.runtimeIdentifier -ne 'win-x64' -or -not $manifest.selfContained) { throw 'Candidate product version, platform, or self-contained identity does not match this site.' }
if ($manifest.candidateId -ne $expectedCandidate -or $manifest.source.head -ne $expectedHead -or $manifest.source.sourceTreeSha256 -ne $expectedTree -or $manifest.source.identity -ne ($expectedHead.Substring(0, 12) + '-tree' + $expectedTree.Substring(0, 12)) -or -not $manifest.source.workingTreeIncluded) { throw 'Candidate provenance does not match the accepted 1.1.0 candidate expected by this site.' }
if ($manifest.signing -notlike 'unsigned*') { throw 'Candidate signing state is not the disclosed unsigned state.' }

$expectedProducts = @('CLI', 'OwnerUI', 'MCP')
if ($manifest.assets.Count -ne $expectedProducts.Count) { throw 'Candidate must contain exactly the three website products.' }
$assetRecords = @{}
foreach ($asset in $manifest.assets) {
    if ($asset.product -notin $expectedProducts -or $assetRecords.ContainsKey($asset.product)) { throw "Unexpected or duplicate product asset: $($asset.product)" }
    $publicName = switch ($asset.product) {
        'CLI' { 'MetaBrain-CLI-' + $manifest.productVersion + '.exe' }
        'OwnerUI' { 'MetaBrain-UI-' + $manifest.productVersion + '.exe' }
        'MCP' { 'MetaBrain-MCP-' + $manifest.productVersion + '.zip' }
    }
    if ([string]::IsNullOrWhiteSpace($publicName)) { throw "No public filename is defined for $($asset.product)." }
    $extension = if ($asset.product -eq 'MCP') { 'zip' } else { 'exe' }
    $expectedName = "MetaBrain-$($asset.product)-1.1.0-win-x64-b4a3bbbce0f0-tree3183d48f66d9-573e00d784cd.$extension"
    $hashMatches = $asset.sha256 -match '^[a-fA-F0-9]{64}$'
    $licenseEntries = @()
    if ($asset.product -eq 'MCP') { $licenseEntries = @($asset.files | Where-Object { $_.path -ceq 'LICENSE' }) } else { $licenseEntries = @($asset.payload.files | Where-Object { $_.path -ceq 'LICENSE' }) }
    if ($asset.name -cne $expectedName -or -not $hashMatches -or [long]$asset.bytes -le 0 -or $licenseEntries.Count -ne 1) { throw "Invalid candidate metadata or missing project LICENSE for $($asset.product)." }
    $assetPath = Join-Path $candidateRoot $asset.name
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Candidate asset missing: $($asset.name)" }
    $file = [IO.FileInfo]::new($assetPath)
    if ([long]$file.Length -ne [long]$asset.bytes) { throw "Candidate size mismatch for $($asset.name): expected $($asset.bytes), found $($file.Length)." }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($assetPath)
        try { $actualHash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
    }
    finally { $sha256.Dispose() }
    if ($actualHash -ne $asset.sha256.ToLowerInvariant()) { throw "Candidate SHA-256 mismatch for $($asset.name)." }
    $assetRecords[$asset.product] = [pscustomobject]@{ Name = $asset.name; PublicName = $publicName; Bytes = [long]$asset.bytes; Hash = $actualHash; Path = $assetPath }
}
foreach ($product in $expectedProducts) { if (-not $assetRecords.ContainsKey($product)) { throw "Candidate is missing $product." } }

if ($outputRoot -ne [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist\site'))) { throw 'Build output is fixed to the ignored repository-local dist/site directory.' }
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Copy-Item -LiteralPath $stylePath -Destination (Join-Path $outputRoot 'styles.css')
if (-not $published) {
    $downloadDirectory = Join-Path $outputRoot 'downloads'
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    foreach ($asset in $assetRecords.Values) { Copy-Item -LiteralPath $asset.Path -Destination (Join-Path $downloadDirectory $asset.PublicName) }
}

$downloadHrefs = @{}
foreach ($product in $expectedProducts) {
    $downloadHrefs[$product] = if ($published) { $ReleaseDownloadBaseUrl + '/' + $assetRecords[$product].PublicName } else { 'downloads/' + $assetRecords[$product].PublicName }
}
$actionLabel = if ($published) { 'T&#7843;i b&#7843;n ph&#225;t h&#224;nh 1.1.0' } else { 'T&#7843;i b&#7843;n xem tr&#432;&#7899;c c&#7909;c b&#7897;' }
$replacements = @{
    '{{CANDIDATE}}' = $manifest.candidateId
    '{{SOURCE_HEAD}}' = $manifest.source.head
    '{{SOURCE_TREE}}' = $manifest.source.sourceTreeSha256
    '{{VERSION}}' = $manifest.productVersion
    '{{RELEASE_STATUS_TITLE}}' = if ($published) { '&#272;&#227; c&#244;ng b&#7889; 1.1.0' } else { 'Ch&#432;a ph&#225;t h&#224;nh c&#244;ng khai' }
    '{{RELEASE_STATUS_TEXT}}' = if ($published) { 'Meta Brain 1.1.0 &#273;&#227; &#273;&#432;&#7907;c ph&#225;t h&#224;nh. C&#225;c li&#234;n k&#7871;t b&#234;n d&#432;&#7899;i tr&#7887; t&#7899;i GitHub Release v1.1.0.' } else { 'C&#225;c t&#7879;p d&#432;&#7899;i &#273;&#226;y ch&#7881; c&#243; trong b&#7843;n xem tr&#432;&#7899;c c&#7909;c b&#7897; &#273;&#227; d&#7921;ng.' }
    '{{RELEASE_BANNER}}' = if ($published) { 'B&#7842;N PH&#193;T H&#192;NH 1.1.0 &#183; &#272;&#195; C&#212;NG B&#7888;' } else { 'B&#7842;N XEM TR&#431;&#7898;C C&#7908;C B&#7896; &#183; KH&#212;NG PH&#7842;I GITHUB RELEASE' }
    '{{RELEASE_FOOTER}}' = if ($published) { 'Published release' } else { 'B&#7843;n xem tr&#432;&#7899;c c&#7909;c b&#7897;' }
    '{{RELEASE_NOTES_STATUS}}' = if ($published) { 'SHA-256 &#273;&#432;&#7907;c ghi nh&#7853;n cho t&#7915;ng t&#7879;p. T&#7843;i b&#7843;n ph&#225;t h&#224;nh t&#7915; GitHub Release v1.1.0.' } else { 'SHA-256 &#273;&#432;&#7907;c cung c&#7845;p cho t&#7915;ng t&#7879;p trong b&#7843;n xem tr&#432;&#7899;c c&#7909;c b&#7897;. G&#243;i t&#7843;i c&#244;ng khai v&#224; ph&#225;t h&#224;nh GitHub ch&#432;a &#273;&#432;&#7907;c x&#225;c nh&#7853;n.' }
    '{{RELEASE_HEADING}}' = if ($published) { 'B&#7843;n ph&#225;t h&#224;nh' } else { 'B&#7843;n xem tr&#432;&#7899;c c&#7909;c b&#7897;' }
    '{{CLI_NAME}}' = $assetRecords['CLI'].PublicName
    '{{CLI_BYTES}}' = $assetRecords['CLI'].Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{CLI_HASH}}' = $assetRecords['CLI'].Hash
    '{{UI_NAME}}' = $assetRecords['OwnerUI'].PublicName
    '{{UI_BYTES}}' = $assetRecords['OwnerUI'].Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{UI_HASH}}' = $assetRecords['OwnerUI'].Hash
    '{{MCP_NAME}}' = $assetRecords['MCP'].PublicName
    '{{MCP_BYTES}}' = $assetRecords['MCP'].Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{MCP_HASH}}' = $assetRecords['MCP'].Hash
    '{{CLI_ACTION}}' = ('<a class="download-action" href="' + $downloadHrefs['CLI'] + '" download>' + $actionLabel + ' <span aria-hidden="true">&darr;</span></a>')
    '{{UI_ACTION}}' = ('<a class="download-action" href="' + $downloadHrefs['OwnerUI'] + '" download>' + $actionLabel + ' <span aria-hidden="true">&darr;</span></a>')
    '{{MCP_ACTION}}' = ('<a class="download-action" href="' + $downloadHrefs['MCP'] + '" download>' + $actionLabel + ' <span aria-hidden="true">&darr;</span></a>')
    '{{OPS_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/docs/OPERATIONS.md'
    '{{SECURITY_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/README.md'
    '{{CHANGELOG_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/CHANGELOG.md'
    '{{RELEASE_NOTES_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/docs/release-1.1.0.md'
    '{{README_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/README.md'
}
$html = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8
foreach ($token in $replacements.Keys) { $html = $html.Replace($token, [string]$replacements[$token]) }
if ($html -match '\{\{[A-Z_]+\}\}') { throw 'Website template contains an unresolved release or documentation placeholder.' }
Set-Content -LiteralPath (Join-Path $outputRoot 'index.html') -Value $html -Encoding utf8
$operationsText = Get-Content -LiteralPath $operationsPath -Raw -Encoding UTF8
$operationsText = [regex]::Replace($operationsText, '\[([^\]]+)\]\((?:\.\./)?(?:artifacts|\.agents/tmp)/[^)]*\)', '$1')
$operationsText = [regex]::Replace($operationsText, '(?:\.\./)?artifacts/[A-Za-z0-9._/-]+', 'internal evidence record')
$operationsText = [regex]::Replace($operationsText, '\.agents/tmp/[A-Za-z0-9._/-]+', 'internal evidence record')
$operationsHtml = '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Operations runbook - Meta Brain</title><link rel="stylesheet" href="styles.css"></head><body><main class="wrap"><p><a href="index.html">Home</a> | <a href="scenarios.html">Scenario guides</a></p><h1>Operations runbook</h1><pre id="321-mcp-client-registration-standalone-package-100">' + [Net.WebUtility]::HtmlEncode($operationsText) + '</pre></main></body></html>'
Set-Content -LiteralPath (Join-Path $outputRoot 'operations.html') -Value $operationsHtml -Encoding utf8
foreach ($guidePath in $guidePaths) {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $guidePath) -Destination (Join-Path $outputRoot $guidePath)
}
if ($published) {
    Write-Host "Built publication candidate at $outputRoot"
    Write-Host "Candidate $($manifest.candidateId); version $($manifest.productVersion); verified all three assets (size + SHA-256); download actions target $ReleaseDownloadBaseUrl."
    Write-Host 'This output links the exact owner-controlled GitHub Releases assets; it was not deployed.'
} else {
    Write-Host "Built local candidate preview at $outputRoot"
    Write-Host "Candidate $($manifest.candidateId); version $($manifest.productVersion); verified all three assets (size + SHA-256)."
    Write-Host 'This output is a local candidate preview, not a public release or deployment.'
}
