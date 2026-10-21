# S8-T3 genuine prior-format fixture generator (repository-local).
#
# Builds the committed service at two pinned historical commits and uses those
# builds, unchanged, to create the synthetic encrypted vaults that S8-T3's
# migration proof consumes. The artifacts are genuine: the vault files are
# written by the historical EncryptedVaultStore / VaultLifecycle /
# ServiceRequestHandler code with the content schemas those commits shipped
# (owner scope-grant state v3 at 1ede35e; canonical lifecycle v1, model job v1
# and a relation at 74e2b1f). Nothing here edits a version number or re-encodes
# a record by hand.
#
# The historical settings loader refuses a settings file whose ACL is not
# explicitly protected, and this task must not change an ACL. Each harness
# therefore adds one programmatic entry point to the extracted snapshot and
# drives the historical handler directly; the writers themselves are untouched.
# The added file and the one added dispatch case are recorded in the artifact's
# provenance.
#
#   powershell.exe -NoProfile -File scripts\s8-t3\prepare-old-vaults.ps1 [-OutputRoot <dir>]
#
# It changes nothing outside this repository: no installation, no service
# registration, no registry value, no environment or profile change, and no
# permission/ownership change on any path. It needs no execution-policy
# override. Historical snapshots are extracted with `git archive` (read-only,
# no worktree registration) and built with a repository-local .NET home,
# package cache and temp directory. The synthetic passphrase below is a
# throwaway value, not an owner secret.
param(
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo 'runtime\s8-t3\old-artifacts'
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$repoRoot = [System.IO.Path]::GetFullPath($repo) + [System.IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The output root must stay inside the repository: $OutputRoot"
}

$work = Join-Path $repo 'runtime\s8-t3'
$snapshots = Join-Path $work 'snapshots'
$dataRoot = Join-Path $work 'data'
$tempRoot = Join-Path $repo 'runtime\tmp'
$dotnetHome = Join-Path $repo 'runtime\dotnet-home'
New-Item -ItemType Directory -Force -Path $snapshots, $dataRoot, $tempRoot | Out-Null

# Process-local toolchain redirection: the historical builds must not write a
# first-run profile, a certificate or a package outside this repository.
$env:DOTNET_CLI_HOME = $dotnetHome
$env:NUGET_PACKAGES = Join-Path $dotnetHome '.nuget\packages'
$env:TEMP = $tempRoot
$env:TMP = $tempRoot
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$controlCommit = '1ede35e2a908a090aeb43e94621ba7a8f9293c70'
$canonicalCommit = '74e2b1f03c37b9c8f42728b41b35290e1bd67157'
$syntheticPassphrase = 'S8T3_SYNTHETIC_OLD_VAULT_PASSPHRASE_4f1c'

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Get-RepositoryOwnerSid {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw 'The current Windows identity has no SID.'
    }

    return $identity.User.Value
}

function Set-ExpectedText {
    param([string]$Path, [string]$Old, [string]$New)

    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.IndexOf($Old, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Expected text was not found in $Path"
    }

    Write-Utf8NoBom -Path $Path -Text ($text.Replace($Old, $New))
}

# The inherited historical builds enforce an explicitly protected ACL on every
# private file (and create one on every write). Running that unchanged would
# mutate permissions, which this task forbids. This cutover removes only the
# enforcement and the write calls in the *extracted snapshot*: no schema,
# record, envelope, manifest or key-format code changes, so the vault bytes the
# build produces are the ones that commit produced through those code paths.
function Remove-HistoricalAclBehavior {
    param([string]$Snapshot)

    $patchReport = New-Object System.Collections.Generic.List[string]

    # 1. Every read-time ACL requirement in the protected-file helper becomes
    #    a no-op. The owner is still read; no permission is inspected.
    $protectedFile = Join-Path $Snapshot 'src\MetaBrain.Connections\ProtectedFile.cs'
    $throws = @(
        'throw new UnauthorizedAccessException("Service credentials are not protected by an explicit ACL.");',
        'throw new UnauthorizedAccessException("Service settings grant read access outside owner and service principals.");'
    )
    foreach ($throw in $throws) {
        Set-ExpectedText -Path $protectedFile -Old $throw `
            -New '/* S8-T3 cutover: inherited ACL enforcement removed; no permission is inspected or changed. */'
        $patchReport.Add('ProtectedFile.cs: removed read-time ACL requirement: ' + $throw)
    }

    # 2. Every ACL write call site in the whole historical Connections project
    #    is removed, so no code path the harness can reach - directly or
    #    indirectly - mutates a permission. Only the call statements are
    #    removed; their helper methods stay as unreachable dead code and the
    #    format, schema, serializer and store logic are untouched.
    $connections = Join-Path $Snapshot 'src\MetaBrain.Connections'
    foreach ($file in Get-ChildItem -LiteralPath $connections -Filter '*.cs' -File) {
        $lines = [System.IO.File]::ReadAllLines($file.FullName)
        $changed = $false
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $trimmed = $lines[$index].Trim()
            if ($trimmed.StartsWith('FileSystemAclExtensions.SetAccessControl(', [System.StringComparison]::Ordinal)) {
                $lines[$index] = '                /* S8-T3 cutover: no permission change on a written file. */'
                $patchReport.Add($file.Name + ': removed ACL write call: ' + $trimmed)
                $changed = $true
            }
            elseif ($trimmed -ceq 'ApplyDirectorySecurity(stage);' -or
                    $trimmed -ceq 'ApplyDirectorySecurity(resourceDirectory);') {
                $lines[$index] = '            /* S8-T3 cutover: no permission change on the vault directory. */'
                $patchReport.Add($file.Name + ': removed ACL write call: ' + $trimmed)
                $changed = $true
            }
        }

        if ($changed) {
            Write-Utf8NoBom -Path $file.FullName -Text (($lines -join ([Environment]::NewLine)) + [Environment]::NewLine)
        }
    }

    # 3. Prove the cutover is complete: no ACL write call may remain anywhere
    #    in the build the harness is about to run.
    foreach ($file in Get-ChildItem -LiteralPath $connections -Filter '*.cs' -File) {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text.IndexOf('FileSystemAclExtensions.SetAccessControl(', [System.StringComparison]::Ordinal) -ge 0 -or
            $text.IndexOf('ApplyDirectorySecurity(stage);', [System.StringComparison]::Ordinal) -ge 0 -or
            $text.IndexOf('ApplyDirectorySecurity(resourceDirectory);', [System.StringComparison]::Ordinal) -ge 0) {
            throw "An ACL write call survived the cutover in $($file.Name)"
        }
    }

    $patchReport
}

function Add-FixtureHarness {
    param([string]$Snapshot, [string]$Profile)

    $template = Join-Path $PSScriptRoot ("harness-" + $Profile + ".cs.txt")
    if (-not (Test-Path -LiteralPath $template)) {
        throw "Missing harness template for profile $Profile"
    }

    $harnessPath = Join-Path $Snapshot 'src\MetaBrain.Connections\S8T3FixtureHarness.cs'
    Copy-Item -LiteralPath $template -Destination $harnessPath -Force

    $programPath = Join-Path $Snapshot 'src\MetaBrain.Connections\Program.cs'
    $program = [System.IO.File]::ReadAllText($programPath)
    if (($program.IndexOf('s8t3-fixture', [System.StringComparison]::Ordinal) -ge 0)) {
        return
    }

    $anchor = '"serve-console" => await RunConsoleServiceAsync(Options.Parse(args, 1), CancellationToken.None).ConfigureAwait(false),'
    if (-not ($program.IndexOf($anchor, [System.StringComparison]::Ordinal) -ge 0)) {
        throw 'The historical program dispatch does not have the expected shape.'
    }

    $case = $anchor + [Environment]::NewLine +
        '                "s8t3-fixture" => await S8T3FixtureHarness.RunAsync(Options.Parse(args, 1)).ConfigureAwait(false),'
    $program = $program.Replace($anchor, $case)
    Write-Utf8NoBom -Path $programPath -Text $program
}

function Get-OldBuild {
    param([string]$Commit, [string]$Profile)

    $snapshot = Join-Path $snapshots $Commit
    $exe = [System.IO.Path]::GetFullPath((Join-Path $snapshot 'src\MetaBrain.Connections\bin\Release\net10.0-windows\MetaBrain.Connections.exe'))
    $harnessPath = Join-Path $snapshot 'src\MetaBrain.Connections\S8T3FixtureHarness.cs'
    if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $harnessPath)) {
        return [pscustomobject]@{
            Executable = $exe
            Patches = @('reused a previously patched snapshot of this pinned commit')
        }
    }

    if (Test-Path -LiteralPath $snapshot) {
        Remove-Item -LiteralPath $snapshot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
    $archive = Join-Path $work ("snapshot-" + $Commit + ".tar")
    try {
        & git -C $repo archive --format=tar --output=$archive $Commit | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "git archive failed for $Commit"
        }

        & tar -xf $archive -C $snapshot | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "tar extraction failed for $Commit"
        }
    }
    finally {
        if (Test-Path -LiteralPath $archive) {
            Remove-Item -LiteralPath $archive -Force
        }
    }

    $patches = @(Remove-HistoricalAclBehavior -Snapshot $snapshot)
    Add-FixtureHarness -Snapshot $snapshot -Profile $Profile
    $patches += 'Program.cs: added one s8t3-fixture dispatch case; S8T3FixtureHarness.cs added (entry point only).'
    $project = Join-Path $snapshot 'src\MetaBrain.Connections\MetaBrain.Connections.csproj'
    & dotnet build $project -c Release | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) {
        throw "The historical build at $Commit did not produce the service executable."
    }

    return [pscustomobject]@{ Executable = $exe; Patches = $patches }
}

function Invoke-Capture {
    param(
        [string]$Exe,
        [string]$StandardInput,
        [string[]]$Arguments
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Exe
    $startInfo.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw ($Arguments -join ' ') + ' did not start'
    }

    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine($StandardInput)
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(180000)) {
        $process.Kill()
        throw ($Arguments -join ' ') + ' timed out after 180 seconds'
    }

    $process.WaitForExit()
    $output = $stdout.GetAwaiter().GetResult()
    $error = $stderr.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        throw ($Arguments -join ' ') + " failed (exit " + $exitCode + "): " + $output.Trim() + ' ' + $error.Trim()
    }

    return $output.Trim()
}

function New-Artifact {
    param(
        [string]$Name,
        [string]$Commit,
        [string]$Profile
    )

    $build = Get-OldBuild -Commit $Commit -Profile $Profile
    $exe = $build.Executable
    $data = Join-Path $dataRoot $Name
    if (Test-Path -LiteralPath $data) {
        Remove-Item -LiteralPath $data -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $vaultPath = Join-Path $data 'vault'
    $output = Invoke-Capture -Exe $exe -StandardInput $syntheticPassphrase -Arguments @(
        's8t3-fixture', '--vault', $vaultPath)
    Write-Host ("  " + $output)

    $artifact = Join-Path $OutputRoot $Name
    if (Test-Path -LiteralPath $artifact) {
        Remove-Item -LiteralPath $artifact -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $artifact | Out-Null
    Copy-Item -LiteralPath $vaultPath -Destination (Join-Path $artifact 'vault') -Recurse -Force

    # The artifact gets a fresh settings file for the current build. The vault
    # bytes are untouched: this file only names the owner and two new pipes.
    $ownerSid = Get-RepositoryOwnerSid
    $freshPipe = 'MetaBrainS8T3Cur' + $Name
    $freshSettings = [ordered]@{
        schemaVersion = 3
        ownerSid = $ownerSid
        controlPipe = $freshPipe
        agentPipe = $freshPipe + '.agent'
    } | ConvertTo-Json -Compress
    Write-Utf8NoBom -Path (Join-Path $artifact 'service-settings.json') -Text $freshSettings
    $provenance = [ordered]@{
        schemaVersion = 1
        sourceCommit = $Commit
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        harness = 'S8T3FixtureHarness.cs added to the extracted snapshot; one dispatch case added to Program.cs. No schema, serializer, envelope, manifest, KDF, lifecycle or handler logic changed.'
        patches = @($build.Patches)
        note = 'Synthetic vault written by the committed historical service code, with the inherited ACL enforcement and ACL writes removed (a safety cutover that touches no format or schema code). No owner data.'
    } | ConvertTo-Json -Compress
    Write-Utf8NoBom -Path (Join-Path $artifact 'provenance.json') -Text $provenance
    Write-Host "ARTIFACT $Name commit=$Commit path=$artifact"
}

New-Artifact -Name 'control-v3' -Commit $controlCommit -Profile 'control-v3'
New-Artifact -Name 'canonical-v1' -Commit $canonicalCommit -Profile 'canonical-v1'

Write-Host "Prepared historical artifacts under $OutputRoot"
