[CmdletBinding()]
param(
    [string]$CandidateDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($CandidateDirectory)) { $CandidateDirectory = Join-Path $repoRoot 'runtime\release-candidates\s10-t5-impl-a1\1.0.0-win-x64-cfb060c29e3d-tree7af6bd77709c-80576c073f06' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot 'dist\site' }
$sourceDirectory = Join-Path $repoRoot 'website'
$candidateRoot = [IO.Path]::GetFullPath($CandidateDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$expectedCandidate = '80576c073f06'
$expectedHead = 'cfb060c29e3d2b5e1aecd791cae3d3c88c92a02a'
$expectedTree = '7af6bd77709c2a42dcd54224082b7d3f7bd16d90d923d3fec0b0d76c273e6738'
$manifestPath = Join-Path $candidateRoot 'candidate-manifest.json'
$templatePath = Join-Path $sourceDirectory 'index.template.html'
$stylePath = Join-Path $sourceDirectory 'styles.css'
$guidePaths = @('scenarios.html', 'codex.html', 'omp.html')
foreach ($guidePath in $guidePaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $guidePath) -PathType Leaf)) { throw "Website guide is missing: $guidePath" }
}
$operationsPath = Join-Path $repoRoot 'docs\OPERATIONS.md'
if (-not (Test-Path -LiteralPath $operationsPath -PathType Leaf)) { throw 'Operations runbook is missing.' }

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Candidate manifest not found: $manifestPath" }
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) { throw 'Website template is missing.' }
if (-not (Test-Path -LiteralPath $stylePath -PathType Leaf)) { throw 'Website stylesheet is missing.' }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.productVersion -ne '1.0.0' -or $manifest.runtimeIdentifier -ne 'win-x64' -or -not $manifest.selfContained) { throw 'Candidate product version, platform, or self-contained identity does not match this site.' }
if ($manifest.candidateId -ne $expectedCandidate -or $manifest.source.head -ne $expectedHead -or $manifest.source.sourceTreeSha256 -ne $expectedTree -or $manifest.source.identity -ne ($expectedHead.Substring(0, 12) + '-tree' + $expectedTree.Substring(0, 12)) -or -not $manifest.source.workingTreeIncluded) { throw 'Candidate provenance does not match the accepted 1.0.0 candidate expected by this site.' }
if ($manifest.signing -notlike 'unsigned*') { throw 'Candidate signing state is not the disclosed unsigned state.' }

$expectedProducts = @('CLI', 'OwnerUI', 'MCP')
if ($manifest.assets.Count -ne $expectedProducts.Count) { throw 'Candidate must contain exactly the three website products.' }
$assetRecords = @{}
foreach ($asset in $manifest.assets) {
    if ($asset.product -notin $expectedProducts -or $assetRecords.ContainsKey($asset.product)) { throw "Unexpected or duplicate product asset: $($asset.product)" }
    $extension = if ($asset.product -eq 'MCP') { 'zip' } else { 'exe' }
    $expectedName = "MetaBrain-$($asset.product)-1.0.0-win-x64-cfb060c29e3d-tree7af6bd77709c-80576c073f06.$extension"
    $hashMatches = $asset.sha256 -match '^[a-fA-F0-9]{64}$'
    if ($asset.name -cne $expectedName -or -not $hashMatches -or [long]$asset.bytes -le 0) { throw "Invalid candidate metadata for $($asset.product): name=$($asset.name) hashValid=$hashMatches bytes=$($asset.bytes)." }
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
    $assetRecords[$asset.product] = [pscustomobject]@{ Name = $asset.name; Bytes = [long]$asset.bytes; Hash = $actualHash; Path = $assetPath }
}
foreach ($product in $expectedProducts) { if (-not $assetRecords.ContainsKey($product)) { throw "Candidate is missing $product." } }

if ($outputRoot -ne [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist\site'))) { throw 'Build output is fixed to the ignored repository-local dist/site directory.' }
if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
$downloadDirectory = Join-Path $outputRoot 'downloads'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
Copy-Item -LiteralPath $stylePath -Destination (Join-Path $outputRoot 'styles.css')
foreach ($asset in $assetRecords.Values) { Copy-Item -LiteralPath $asset.Path -Destination (Join-Path $downloadDirectory $asset.Name) }

$replacements = @{
    '{{CANDIDATE}}' = $manifest.candidateId
    '{{SOURCE_HEAD}}' = $manifest.source.head
    '{{SOURCE_TREE}}' = $manifest.source.sourceTreeSha256
    '{{CLI_NAME}}' = $assetRecords['CLI'].Name
    '{{CLI_BYTES}}' = $assetRecords['CLI'].Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{CLI_HASH}}' = $assetRecords['CLI'].Hash
    '{{UI_NAME}}' = $assetRecords['OwnerUI'].Name
    '{{UI_BYTES}}' = $assetRecords['OwnerUI'].Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{UI_HASH}}' = $assetRecords['OwnerUI'].Hash
    '{{MCP_NAME}}' = $assetRecords['MCP'].Name
    '{{MCP_BYTES}}' = $assetRecords['MCP'].Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    '{{MCP_HASH}}' = $assetRecords['MCP'].Hash
    '{{CLI_ACTION}}' = ('<a class="download-action" href="downloads/' + $assetRecords['CLI'].Name + '" download>T&#7843;i trong xem tr&#432;&#7899;c c&#7909;c b&#7897; <span aria-hidden="true">&darr;</span></a>')
    '{{UI_ACTION}}' = ('<a class="download-action" href="downloads/' + $assetRecords['OwnerUI'].Name + '" download>T&#7843;i trong xem tr&#432;&#7899;c c&#7909;c b&#7897; <span aria-hidden="true">&darr;</span></a>')
    '{{MCP_ACTION}}' = ('<a class="download-action" href="downloads/' + $assetRecords['MCP'].Name + '" download>T&#7843;i trong xem tr&#432;&#7899;c c&#7909;c b&#7897; <span aria-hidden="true">&darr;</span></a>')
    '{{OPS_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/docs/OPERATIONS.md'
    '{{SECURITY_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/README.md'
    '{{CHANGELOG_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/CHANGELOG.md'
    '{{README_URL}}' = 'https://github.com/triet4p/my-meta-brain/blob/main/README.md'
}
$html = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8
foreach ($token in $replacements.Keys) { $html = $html.Replace($token, [string]$replacements[$token]) }
if ($html -match '\{\{[A-Z_]+\}\}') { throw 'Website template contains an unresolved release or documentation placeholder.' }
Set-Content -LiteralPath (Join-Path $outputRoot 'index.html') -Value $html -Encoding utf8
$operationsHtml = '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Operations runbook - Meta Brain</title><link rel="stylesheet" href="styles.css"></head><body><main class="wrap"><p><a href="index.html">Home</a> | <a href="scenarios.html">Scenario guides</a></p><h1>Operations runbook</h1><pre id="321-mcp-client-registration-standalone-package-100">' + [Net.WebUtility]::HtmlEncode((Get-Content -LiteralPath $operationsPath -Raw -Encoding UTF8)) + '</pre></main></body></html>'
Set-Content -LiteralPath (Join-Path $outputRoot 'operations.html') -Value $operationsHtml -Encoding utf8
foreach ($guidePath in $guidePaths) {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $guidePath) -Destination (Join-Path $outputRoot $guidePath)
}
Write-Host "Built local candidate preview at $outputRoot"
Write-Host "Candidate $($manifest.candidateId); version $($manifest.productVersion); verified all three assets (size + SHA-256)."
Write-Host 'This output is a local candidate preview, not a public release or deployment.'
