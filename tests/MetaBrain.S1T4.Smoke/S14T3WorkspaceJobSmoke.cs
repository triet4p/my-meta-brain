using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S14T3Zone = "s14t3-zone";
    private const string S14T3Inbox = "s14t3-inbox";
    private const string S14T3Evidence = "The fixture cache expires after an idle minute.";
    private const string S14T3SecondEvidence = "Restarting the worker cleared the fixture cache.";

    internal static async Task RunS14T3WorkspaceJobsAsync(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S14-T3 CLI/service smoke is Windows-only.");
        }

        var paraphrasedManagedOutput = JsonSerializer.Serialize(new
        {
            summary = "Synthetic managed-resource output.",
            items = new[]
            {
                new
                {
                    kind = "claim",
                    title = "Cache expires after one minute of inactivity",
                    body = "The fixture cache expires after sixty seconds without activity.",
                    excerpt = S14T3Evidence
                }
            }
        });
        Require(!JobOutputValidator.TryValidate(
                paraphrasedManagedOutput,
                S14T3Evidence,
                out _,
                out var managedOutputError,
                out _,
                requireFacets: false) &&
            managedOutputError == "unverified_inference",
            "Managed-resource single-input outputs no longer retain their verbatim title/body boundary.");

        var managedSynthesisInput = new JobInputText("s14-t3-managed", 1, S14T3Evidence, false);
        var paraphrasedManagedSynthesis = JsonSerializer.Serialize(new
        {
            summary = "Synthetic managed-resource synthesis output.",
            items = new[]
            {
                new
                {
                    kind = "synthesis",
                    title = "Cache expires after one minute of inactivity",
                    body = "The fixture cache expires after sixty seconds without activity.",
                    crossSource = false,
                    occurredAtSourceText = (string?)null,
                    grounding = new[]
                    {
                        new { inputId = managedSynthesisInput.ResourceId, inputRevision = 1L, excerpt = S14T3Evidence }
                    }
                }
            }
        });
        Require(!JobOutputValidator.TryValidateSynthesis(
                paraphrasedManagedSynthesis,
                new[] { managedSynthesisInput },
                out _,
                out var managedSynthesisError,
                out _) &&
            managedSynthesisError == "unverified_inference",
            "Managed-resource synthesis outputs no longer retain their verbatim title/body boundary.");

        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        HttpListener? listener = null;
        try
        {
            service = await StartServiceAsync(executable, fixture.SettingsPath).ConfigureAwait(false);
            var passphrase = CreateSyntheticPassphrase();
            var provision = await RunOwnerCommandWithInputAsync(
                executable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provision.ExitCode == 0, "The T3 synthetic vault did not provision.");
            await S14T2UnlockAsync(executable, fixture.ControlPipe, passphrase).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var inboxPath = Path.Combine(fixture.OutputDirectory, "s14-t3-inbox.json");
            await File.WriteAllTextAsync(inboxPath, JsonSerializer.Serialize(new
            {
                kind = "observation",
                author = "user",
                creationMethod = "directWrite",
                evidence = "userConfirmed",
                level = "l1",
                title = "S14-T3 synthetic inbox",
                body = "Pending workspace-backed structuring proposals only.",
                sources = Array.Empty<object>(),
                occurredAtUtc = now,
                recordedAtUtc = now
            })).ConfigureAwait(false);
            var createInbox = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "memory-create", "--resource-id", S14T3Inbox, "--zone-id", S14T3Zone,
                "--input-file", inboxPath).ConfigureAwait(false);
            Require(createInbox.ExitCode == 0, "The T3 destination inbox could not be created.");

            var sourceText = "Synthetic workspace fixture. " + S14T3Evidence + " " + S14T3SecondEvidence;
            var secondSourceText = "Synthetic second workspace fixture. " + S14T3SecondEvidence;
            var sourcePath = Path.Combine(fixture.OutputDirectory, "s14-t3-source.txt");
            var secondSourcePath = Path.Combine(fixture.OutputDirectory, "s14-t3-source-two.txt");
            var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
            var secondSourceBytes = Encoding.UTF8.GetBytes(secondSourceText);
            await File.WriteAllBytesAsync(sourcePath, sourceBytes).ConfigureAwait(false);
            await File.WriteAllBytesAsync(secondSourcePath, secondSourceBytes).ConfigureAwait(false);

            var workspaceStatus = await S14T2OwnerAsync(executable, fixture.ControlPipe, "workspace-status").ConfigureAwait(false);
            Require(workspaceStatus.ExitCode == 0, "The T3 workspace status could not be read.");
            var treeRevision = WorkspaceNumber(workspaceStatus.StandardOutput, "revision");
            var imported = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-import", "--parent-id", "root", "--name", "s14-t3-source.txt",
                "--input-file", sourcePath, "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(imported.ExitCode == 0, "The T3 workspace source was not captured through the owner CLI.");
            var sourceId = WorkspaceToken(imported.StandardOutput, "id");
            treeRevision = WorkspaceNumber(imported.StandardOutput, "revision");
            var importedSecond = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-import", "--parent-id", "root", "--name", "s14-t3-source-two.txt",
                "--input-file", secondSourcePath, "--expected-tree-revision", treeRevision.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(importedSecond.ExitCode == 0, "The second T3 workspace source was not captured through the owner CLI.");
            var secondSourceId = WorkspaceToken(importedSecond.StandardOutput, "id");

            var ownerExcerptOffset = Encoding.UTF8.GetByteCount(sourceText.AsSpan(0, sourceText.IndexOf(S14T3Evidence, StringComparison.Ordinal)));
            var ownerAnchor = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-anchor-create", "--file-id", sourceId,
                "--expected-content-revision", "1",
                "--offset", ownerExcerptOffset.ToString(CultureInfo.InvariantCulture),
                "--length", Encoding.UTF8.GetByteCount(S14T3Evidence).ToString(CultureInfo.InvariantCulture),
                "--expected-tree-revision", WorkspaceNumber(importedSecond.StandardOutput, "revision").ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            Require(ownerAnchor.ExitCode == 0, "The pre-existing owner passage anchor could not be created.");
            var ownerAnchorId = WorkspaceToken(ownerAnchor.StandardOutput, "id");
            var ownerAnnotationDraft = Path.Combine(fixture.OutputDirectory, "s14-t3-owner-annotation.json");
            await File.WriteAllTextAsync(ownerAnnotationDraft, JsonSerializer.Serialize(new
            {
                anchorId = ownerAnchorId,
                facets = new[] { "experience" }
            })).ConfigureAwait(false);
            var ownerAnnotationResult = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-create", "--file-id", sourceId,
                "--input-file", ownerAnnotationDraft).ConfigureAwait(false);
            Require(ownerAnnotationResult.ExitCode == 0 &&
                    ownerAnnotationResult.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    ownerAnnotationResult.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal),
                "The pre-existing owner annotation was not established.");
            var ownerAnnotationId = WorkspaceToken(ownerAnnotationResult.StandardOutput, "id");

            var port = GetAvailablePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var activeListener = new HttpListener();
            listener = activeListener;
            activeListener.Prefixes.Add(prefix);
            activeListener.Start();
            var providerConfig = await RunOwnerCommandWithInputAsync(executable,
                "local-synthetic-key" + Environment.NewLine,
                "owner", "provider-config", "--control-pipe", fixture.ControlPipe,
                "--provider", "deepseek", "--model", "deepseek-flash",
                "--base-url", prefix, "--api-key-stdin", "true").ConfigureAwait(false);
            Require(providerConfig.ExitCode == 0, "The offline local HTTP fixture endpoint could not be configured.");

            async Task<(string JobId, CommandResult Start)> RunSuccessAsync(
                string profile,
                string responseContent,
                (string Id, long Revision)[] inputs,
                string? objective = null,
                string? targetLevel = null)
            {
                var selectionPath = Path.Combine(fixture.OutputDirectory, "s14-t3-selection-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(selectionPath, JsonSerializer.Serialize(new
                {
                    inputs = inputs.Select(input => new { resourceId = input.Id, revision = input.Revision, kind = "workspaceFile" }).ToArray(),
                    objective,
                    targetLevel
                })).ConfigureAwait(false);
                var created = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "job-create", "--zone-id", S14T3Zone, "--destination-id", S14T3Inbox,
                    "--destination-revision", "1", "--provider", "deepseek", "--model", "deepseek-flash",
                    "--profile", profile, "--input-file", selectionPath).ConfigureAwait(false);
                Require(created.ExitCode == 0 && created.StandardOutput.Contains("state=Pending", StringComparison.Ordinal),
                    $"The {profile} workspace job could not be created: {created.StandardError} {created.StandardOutput}");
                var jobId = ExtractJobId(created.StandardOutput);
                var serverTask = QueueResponseAsync(activeListener, ProviderEnvelope(responseContent));
                var started = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "job-start", "--job-id", jobId).ConfigureAwait(false);
                await serverTask.ConfigureAwait(false);
                Require(started.ExitCode == 0 && started.StandardOutput.Contains("state=Succeeded", StringComparison.Ordinal),
                    $"The {profile} workspace job did not complete: {started.StandardError} {started.StandardOutput}");
                Require(started.StandardOutput.Contains("proposals=[prop-", StringComparison.Ordinal) &&
                        started.StandardOutput.Contains("annotations=[prop-", StringComparison.Ordinal),
                    $"The {profile} workspace job did not expose its pending proposal/annotation links: {started.StandardOutput}");
                return (jobId, started);
            }

            string DirectOutput(string kind, string excerpt, string title, string? body = null) => JsonSerializer.Serialize(new
            {
                summary = "Synthetic exact-source workspace structuring output.",
                items = new[]
                {
                    new
                    {
                        kind,
                        title,
                        body = body ?? excerpt,
                        excerpt,
                        facets = new[] { "world" }
                    }
                }
            });

            string UntrustedOwnerClaimOutput(string excerpt, string title, string body) => JsonSerializer.Serialize(new
            {
                summary = "Synthetic paraphrase remains a pending AI suggestion.",
                claimedUserConfirmed = true,
                items = new[]
                {
                    new
                    {
                        kind = "claim",
                        title,
                        body,
                        excerpt,
                        facets = new[] { "world" },
                        author = "user",
                        executor = "owner",
                        evidence = "userConfirmed",
                        origin = "Owner",
                        reviewStatus = "Accepted"
                    }
                }
            });
            string SynthesisOutput(string firstExcerpt, string secondExcerpt, string title, string body) => JsonSerializer.Serialize(new
            {
                summary = "Synthetic workspace synthesis output remains pending owner review.",
                items = new[]
                {
                    new
                    {
                        kind = "synthesis",
                        title,
                        body,
                        facets = new[] { "world" },
                        crossSource = true,
                        grounding = new[]
                        {
                            new { inputId = sourceId, inputRevision = 1L, excerpt = firstExcerpt },
                            new { inputId = secondSourceId, inputRevision = 1L, excerpt = secondExcerpt }
                        }
                    }
                }
            });

            var research = await RunSuccessAsync("research-note",
                UntrustedOwnerClaimOutput(
                    S14T3Evidence,
                    "Cache expires after one minute of inactivity",
                    "The fixture cache expires after sixty seconds without activity."),
                new[] { (sourceId, 1L) }).ConfigureAwait(false);
            var researchProposalId = WorkspaceToken(research.Start.StandardOutput, "proposals").Trim('[', ']');
            var researchProposal = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "proposal-read", "--resource-id", researchProposalId).ConfigureAwait(false);
            Require(researchProposal.ExitCode == 0 &&
                    researchProposal.StandardOutput.Contains("executor=StructuringJob", StringComparison.OrdinalIgnoreCase) &&
                    researchProposal.StandardOutput.Contains("executorDeclaredMethod=paraphrase", StringComparison.OrdinalIgnoreCase) &&
                    researchProposal.StandardOutput.Contains("userConfirmedClaim=false", StringComparison.OrdinalIgnoreCase) &&
                    researchProposal.StandardOutput.Contains("title=Cache expires after one minute of inactivity", StringComparison.Ordinal) &&
                    researchProposal.StandardOutput.Contains("body=The fixture cache expires after sixty seconds without activity.", StringComparison.Ordinal),
                "Paraphrased workspace fields or model-supplied owner-confirmation metadata escaped as owner-confirmed proposal data.");
            var verbatimResearch = await RunSuccessAsync("research-note",
                DirectOutput("observation", S14T3Evidence, S14T3Evidence),
                new[] { (sourceId, 1L) }).ConfigureAwait(false);
            var verbatimResearchProposalId = WorkspaceToken(verbatimResearch.Start.StandardOutput, "proposals").Trim('[', ']');
            var verbatimResearchProposal = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "proposal-read", "--resource-id", verbatimResearchProposalId).ConfigureAwait(false);
            Require(verbatimResearchProposal.ExitCode == 0 &&
                    verbatimResearchProposal.StandardOutput.Contains("executorDeclaredMethod=verbatimExtract", StringComparison.OrdinalIgnoreCase),
                "A verbatim workspace output did not retain its verbatim extraction method metadata.");
            var researchAnnotationLinks = WorkspaceToken(research.Start.StandardOutput, "annotations");
            var researchAnnotationStart = researchAnnotationLinks.IndexOf("ann-", StringComparison.Ordinal);
            Require(researchAnnotationStart >= 0, "The paraphrased research proposal did not link its pending workspace annotation.");
            var researchAnnotationEnd = researchAnnotationLinks.IndexOf(']', researchAnnotationStart);
            Require(researchAnnotationEnd > researchAnnotationStart, "The paraphrased research annotation link was malformed.");
            var researchAnnotationId = researchAnnotationLinks[researchAnnotationStart..researchAnnotationEnd];
            var researchAnnotation = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", researchAnnotationId).ConfigureAwait(false);
            var expectedAnchorDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(S14T3Evidence))).ToLowerInvariant();
            var expectedAnchor = $"file={sourceId} contentRevision=1 offset={ownerExcerptOffset.ToString(CultureInfo.InvariantCulture)} length={Encoding.UTF8.GetByteCount(S14T3Evidence).ToString(CultureInfo.InvariantCulture)} sha256={expectedAnchorDigest}";
            Require(researchAnnotation.ExitCode == 0 &&
                    researchAnnotation.StandardOutput.Contains("origin=AiSuggestion", StringComparison.OrdinalIgnoreCase) &&
                    researchAnnotation.StandardOutput.Contains("review=Pending", StringComparison.OrdinalIgnoreCase) &&
                    researchAnnotation.StandardOutput.Contains("executor=StructuringJob", StringComparison.OrdinalIgnoreCase) &&
                    researchAnnotation.StandardOutput.Contains(expectedAnchor, StringComparison.Ordinal) &&
                    researchAnnotation.StandardOutput.Contains("state=live", StringComparison.OrdinalIgnoreCase),
                "A paraphrased workspace item did not remain an unverified pending AI annotation on its exact live source anchor.");

            var coding = await RunSuccessAsync("coding-session",
                DirectOutput(
                    "result",
                    S14T3Evidence,
                    "The fixture cache has a one-minute inactivity expiry.",
                    "The cache is removed after a minute without activity."),
                new[] { (sourceId, 1L) }).ConfigureAwait(false);
            var trend = await RunSuccessAsync("trend",
                DirectOutput(
                    "observation",
                    S14T3Evidence,
                    "The cache's idle timeout is one minute.",
                    "After a minute without activity, the fixture cache expires."),
                new[] { (sourceId, 1L) }).ConfigureAwait(false);

            var synthesisContent = SynthesisOutput(
                S14T3Evidence,
                S14T3SecondEvidence,
                "The worker restart cleared the fixture cache",
                "Restarting the worker removes its cached state.");

            var synthesis = await RunSuccessAsync("synthesis", synthesisContent,
                new[] { (sourceId, 1L), (secondSourceId, 1L) },
                objective: "Compare the two synthetic workspace notes.", targetLevel: "l2").ConfigureAwait(false);
            var synthesisProposalId = WorkspaceToken(synthesis.Start.StandardOutput, "proposals").Trim('[', ']');
            var synthesisProposal = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "proposal-read", "--resource-id", synthesisProposalId).ConfigureAwait(false);
            Require(synthesisProposal.ExitCode == 0 &&
                    synthesisProposal.StandardOutput.Contains("executor=StructuringJob", StringComparison.OrdinalIgnoreCase) &&
                    synthesisProposal.StandardOutput.Contains("userConfirmedClaim=false", StringComparison.OrdinalIgnoreCase) &&
                    synthesisProposal.StandardOutput.Contains("title=The worker restart cleared the fixture cache", StringComparison.Ordinal) &&
                    synthesisProposal.StandardOutput.Contains("body=Restarting the worker removes its cached state.", StringComparison.Ordinal),
                "Paraphrased synthesis prose escaped its pending, non-owner-confirmed AI proposal.");
            var synthesisAnnotationIds = WorkspaceToken(synthesis.Start.StandardOutput, "annotations")
                .Split(new[] { '[', ']', '|', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(annotationId => annotationId.StartsWith("ann-", StringComparison.Ordinal))
                .ToArray();
            Require(synthesisAnnotationIds.Length == 2,
                "The paraphrased synthesis item did not link one pending workspace annotation per selected source.");
            var synthesisAnnotationReads = new List<string>();
            foreach (var annotationId in synthesisAnnotationIds)
            {
                var synthesisAnnotation = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "cognitive-annotation-read", "--resource-id", annotationId).ConfigureAwait(false);
                Require(synthesisAnnotation.ExitCode == 0 &&
                        synthesisAnnotation.StandardOutput.Contains("origin=AiSuggestion", StringComparison.OrdinalIgnoreCase) &&
                        synthesisAnnotation.StandardOutput.Contains("review=Pending", StringComparison.OrdinalIgnoreCase) &&
                        synthesisAnnotation.StandardOutput.Contains("executor=StructuringJob", StringComparison.OrdinalIgnoreCase) &&
                        synthesisAnnotation.StandardOutput.Contains("state=live", StringComparison.OrdinalIgnoreCase),
                    "A paraphrased synthesis item did not remain pending on a live AI annotation.");
                synthesisAnnotationReads.Add(synthesisAnnotation.StandardOutput);
            }

            var firstSynthesisDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(S14T3Evidence))).ToLowerInvariant();
            var firstSynthesisAnchor = $"file={sourceId} contentRevision=1 offset={ownerExcerptOffset.ToString(CultureInfo.InvariantCulture)} length={Encoding.UTF8.GetByteCount(S14T3Evidence).ToString(CultureInfo.InvariantCulture)} sha256={firstSynthesisDigest}";
            var secondSynthesisOffset = Encoding.UTF8.GetByteCount("Synthetic second workspace fixture. ");
            var secondSynthesisDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(S14T3SecondEvidence))).ToLowerInvariant();
            var secondSynthesisAnchor = $"file={secondSourceId} contentRevision=1 offset={secondSynthesisOffset.ToString(CultureInfo.InvariantCulture)} length={Encoding.UTF8.GetByteCount(S14T3SecondEvidence).ToString(CultureInfo.InvariantCulture)} sha256={secondSynthesisDigest}";
            Require(synthesisAnnotationReads.Any(output => output.Contains(firstSynthesisAnchor, StringComparison.Ordinal)) &&
                    synthesisAnnotationReads.Any(output => output.Contains(secondSynthesisAnchor, StringComparison.Ordinal)),
                "Paraphrased synthesis annotations did not preserve both exact source revision/range/digest anchors.");

            var status = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "job-status", "--job-id", synthesis.JobId).ConfigureAwait(false);
            Require(status.ExitCode == 0 && status.StandardOutput.Contains(sourceId + "@1", StringComparison.Ordinal) &&
                    status.StandardOutput.Contains(secondSourceId + "@1", StringComparison.Ordinal) &&
                    status.StandardOutput.Contains("#WorkspaceFile", StringComparison.OrdinalIgnoreCase),
                "Job status did not preserve the selected workspace file IDs, content revisions, and input kind.");
            var proposalList = await S14T2OwnerAsync(executable, fixture.ControlPipe, "proposal-list").ConfigureAwait(false);
            Require(proposalList.ExitCode == 0 && proposalList.StandardOutput.Contains("PROPOSAL id=", StringComparison.Ordinal),
                "The workspace jobs did not create pending proposals in the owner inbox.");
            var annotationList = await S14T2OwnerAsync(executable, fixture.ControlPipe, "cognitive-annotation-list").ConfigureAwait(false);
            Require(annotationList.ExitCode == 0 && annotationList.StandardOutput.Contains("ANNOTATIONS count=7", StringComparison.Ordinal),
                "Workspace jobs did not create six anchored pending suggestions across four profiles plus the verbatim metadata case while preserving the owner annotation.");
            var ownerAnnotationRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", ownerAnnotationId).ConfigureAwait(false);
            Require(ownerAnnotationRead.ExitCode == 0 &&
                    ownerAnnotationRead.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    ownerAnnotationRead.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    ownerAnnotationRead.StandardOutput.Contains("FACETS Experience", StringComparison.Ordinal),
                "A successful structuring job changed the pre-existing owner annotation.");
            var workspaceReadPath = Path.Combine(fixture.OutputDirectory, "s14-t3-raw-after-success.txt");
            var rawRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", sourceId, "--revision", "1", "--output-file", workspaceReadPath).ConfigureAwait(false);
            Require(rawRead.ExitCode == 0 && (await File.ReadAllBytesAsync(workspaceReadPath).ConfigureAwait(false)).SequenceEqual(sourceBytes),
                "A successful job altered the raw workspace source bytes.");

            async Task<string> CreateSingleWorkspaceJobAsync(string profile)
            {
                var selectionPath = Path.Combine(fixture.OutputDirectory, "s14-t3-negative-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(selectionPath, JsonSerializer.Serialize(new
                {
                    inputs = new[] { new { resourceId = sourceId, revision = 1L, kind = "workspaceFile" } }
                })).ConfigureAwait(false);
                var created = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "job-create", "--zone-id", S14T3Zone, "--destination-id", S14T3Inbox,
                    "--destination-revision", "1", "--provider", "deepseek", "--model", "deepseek-flash",
                    "--max-cost-usd", "0.0054",
                    "--profile", profile, "--input-file", selectionPath).ConfigureAwait(false);
                Require(created.ExitCode == 0, $"The negative {profile} workspace job could not be created.");
                return ExtractJobId(created.StandardOutput);
            }

            async Task<CommandResult> RunNegativeResponseAsync(string profile, string providerContent, string expectedFailure, int statusCode = 200)
            {
                var jobId = await CreateSingleWorkspaceJobAsync(profile).ConfigureAwait(false);
                var serverTask = QueueResponseAsync(activeListener, statusCode == 200
                    ? ProviderEnvelope(providerContent)
                    : "synthetic local provider failure", statusCode);
                var started = await S14T2OwnerAsync(executable, fixture.ControlPipe, "job-start", "--job-id", jobId).ConfigureAwait(false);
                await serverTask.ConfigureAwait(false);
                Require(started.ExitCode == 5 && started.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                        started.StandardOutput.Contains("fail=" + expectedFailure, StringComparison.Ordinal),
                    $"The negative workspace job did not refuse with {expectedFailure}: {started.StandardOutput} {started.StandardError}");
                return started;
            }
            async Task<CommandResult> RunNegativeSynthesisResponseAsync(string responseContent, string expectedFailure)
            {
                var selectionPath = Path.Combine(fixture.OutputDirectory, "s14-t3-negative-synthesis-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(selectionPath, JsonSerializer.Serialize(new
                {
                    inputs = new[]
                    {
                        new { resourceId = sourceId, revision = 1L, kind = "workspaceFile" },
                        new { resourceId = secondSourceId, revision = 1L, kind = "workspaceFile" }
                    },
                    objective = "Compare the two synthetic workspace notes.",
                    targetLevel = "l2"
                })).ConfigureAwait(false);
                var created = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "job-create", "--zone-id", S14T3Zone, "--destination-id", S14T3Inbox,
                    "--destination-revision", "1", "--provider", "deepseek", "--model", "deepseek-flash",
                    "--max-cost-usd", "0.0054",
                    "--profile", "synthesis", "--input-file", selectionPath).ConfigureAwait(false);
                Require(created.ExitCode == 0, "The negative synthesis workspace job could not be created.");
                var jobId = ExtractJobId(created.StandardOutput);
                var serverTask = QueueResponseAsync(activeListener, ProviderEnvelope(responseContent));
                var started = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                    "job-start", "--job-id", jobId).ConfigureAwait(false);
                await serverTask.ConfigureAwait(false);
                Require(started.ExitCode == 5 && started.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                        started.StandardOutput.Contains("fail=" + expectedFailure, StringComparison.Ordinal),
                    $"The negative synthesis workspace job did not refuse with {expectedFailure}: {started.StandardOutput} {started.StandardError}");
                return started;
            }


            await RunNegativeResponseAsync("research-note", "not JSON", "malformed_output").ConfigureAwait(false);
            await RunNegativeResponseAsync("research-note",
                DirectOutput("claim", "This excerpt is not present in the selected source.", "Unsupported title"),
                "grounding_failure").ConfigureAwait(false);
            await RunNegativeResponseAsync("research-note",
                DirectOutput("result", S14T3Evidence, "Deployed to production", "The software was deployed to production."),
                "unverified_inference").ConfigureAwait(false);
            await RunNegativeSynthesisResponseAsync(
                SynthesisOutput("Excerpt absent from the selected source.", S14T3SecondEvidence, "Synthesis title", "Synthesis body"),
                "grounding_failure").ConfigureAwait(false);
            await RunNegativeResponseAsync("research-note", string.Empty, "provider_error", statusCode: 500).ConfigureAwait(false);
            annotationList = await S14T2OwnerAsync(executable, fixture.ControlPipe, "cognitive-annotation-list").ConfigureAwait(false);
            Require(annotationList.ExitCode == 0 && annotationList.StandardOutput.Contains("ANNOTATIONS count=7", StringComparison.Ordinal),
                "Malformed, wrong-excerpt, deployment-inference, or provider-failed output created a partial AI annotation.");
            ownerAnnotationRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", ownerAnnotationId).ConfigureAwait(false);
            Require(ownerAnnotationRead.ExitCode == 0 && ownerAnnotationRead.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    ownerAnnotationRead.StandardOutput.Contains("FACETS Experience", StringComparison.Ordinal),
                "Malformed, wrong-excerpt, deployment-inference, or provider-failed output altered the owner annotation.");

            var cancelJobId = await CreateSingleWorkspaceJobAsync("research-note").ConfigureAwait(false);
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelResponseTask = Task.Run(async () =>
            {
                var context = await activeListener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                requestStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(ProviderEnvelope(DirectOutput("observation", S14T3Evidence, "The fixture cache expires after an idle minute")));
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                }
            });
            var cancelStartTask = RunOwnerCommandAsync(executable,
                "owner", "job-start", "--control-pipe", fixture.ControlPipe, "--job-id", cancelJobId);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var cancel = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "job-cancel", "--job-id", cancelJobId).ConfigureAwait(false);
            var cancelledStart = await cancelStartTask.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await cancelResponseTask.ConfigureAwait(false);
            Require(cancel.ExitCode == 0 && cancel.StandardOutput.Contains("state=Cancelled", StringComparison.Ordinal) &&
                    cancelledStart.ExitCode == 5 && cancelledStart.StandardOutput.Contains("state=Cancelled", StringComparison.Ordinal),
                $"Cancelling an in-flight workspace job did not preserve the cancelled state. Cancel exit={cancel.ExitCode}, stdout={cancel.StandardOutput}, stderr={cancel.StandardError}; start exit={cancelledStart.ExitCode}, stdout={cancelledStart.StandardOutput}, stderr={cancelledStart.StandardError}.");
            annotationList = await S14T2OwnerAsync(executable, fixture.ControlPipe, "cognitive-annotation-list").ConfigureAwait(false);
            Require(annotationList.ExitCode == 0 && annotationList.StandardOutput.Contains("ANNOTATIONS count=7", StringComparison.Ordinal),
                "Cancelling a provider request created a partial AI annotation.");
            ownerAnnotationRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", ownerAnnotationId).ConfigureAwait(false);
            Require(ownerAnnotationRead.ExitCode == 0 && ownerAnnotationRead.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    ownerAnnotationRead.StandardOutput.Contains("FACETS Experience", StringComparison.Ordinal),
                "Cancelling an in-flight job altered the owner annotation.");
            var cancelledReadPath = Path.Combine(fixture.OutputDirectory, "s14-t3-raw-after-cancellation.txt");
            rawRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", sourceId, "--revision", "1", "--output-file", cancelledReadPath).ConfigureAwait(false);
            Require(rawRead.ExitCode == 0 && (await File.ReadAllBytesAsync(cancelledReadPath).ConfigureAwait(false)).SequenceEqual(sourceBytes),
                $"Provider failure or cancellation altered the raw workspace source bytes: exit={rawRead.ExitCode}, stdout={rawRead.StandardOutput}, stderr={rawRead.StandardError}.");

            var staleJobId = await CreateSingleWorkspaceJobAsync("research-note").ConfigureAwait(false);
            var revisedBytes = Encoding.UTF8.GetBytes(sourceText + " Owner revision remains raw.");
            var revisedPath = Path.Combine(fixture.OutputDirectory, "s14-t3-source-revised.txt");
            await File.WriteAllBytesAsync(revisedPath, revisedBytes).ConfigureAwait(false);
            var revised = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-revise", "--file-id", sourceId, "--expected-content-revision", "1", "--input-file", revisedPath).ConfigureAwait(false);
            Require(revised.ExitCode == 0 && revised.StandardOutput.Contains("contentRevision=2", StringComparison.Ordinal),
                "The owner source revision did not advance for the stale-job scenario.");
            var staleStart = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "job-start", "--job-id", staleJobId).ConfigureAwait(false);
            Require(staleStart.ExitCode == 5 && staleStart.StandardOutput.Contains("state=Failed", StringComparison.Ordinal) &&
                    staleStart.StandardOutput.Contains("fail=source_revision_changed", StringComparison.Ordinal),
                "A job pinned to an old workspace content revision did not fail before provider send.");
            var ownerAfterRevision = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "cognitive-annotation-read", "--resource-id", ownerAnnotationId).ConfigureAwait(false);
            Require(ownerAfterRevision.ExitCode == 0 && ownerAfterRevision.StandardOutput.Contains("origin=Owner", StringComparison.Ordinal) &&
                    ownerAfterRevision.StandardOutput.Contains("review=Accepted", StringComparison.Ordinal) &&
                    ownerAfterRevision.StandardOutput.Contains("state=stale", StringComparison.Ordinal),
                "A source revision change overwrote the owner annotation instead of preserving its original stale anchor.");
            var revisedReadPath = Path.Combine(fixture.OutputDirectory, "s14-t3-raw-after-owner-revision.txt");
            rawRead = await S14T2OwnerAsync(executable, fixture.ControlPipe,
                "workspace-read", "--file-id", sourceId, "--revision", "2", "--output-file", revisedReadPath).ConfigureAwait(false);
            Require(rawRead.ExitCode == 0 && (await File.ReadAllBytesAsync(revisedReadPath).ConfigureAwait(false)).SequenceEqual(revisedBytes),
                "The owner-authored revised raw bytes were not preserved after the blocked job.");

            Console.WriteLine("PASS S14-T3 workspace job CLI/service: coding, research, trend and synthesis use exact revisions; all four profiles accept paraphrased workspace prose only as pending AI suggestions; wrong excerpts, deployment inference, malformed/provider-failed/cancelled/stale jobs preserve raw bytes and owner annotations.");
        }
        finally
        {
            listener?.Close();
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static string ProviderEnvelope(string content) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-s14t3-synthetic",
        @object = "chat.completion",
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        model = "deepseek-flash",
        choices = new[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content },
                finish_reason = "stop"
            }
        },
        usage = new { prompt_tokens = 120, completion_tokens = 40, total_tokens = 160 }
    });

    private static Task QueueResponseAsync(HttpListener listener, string body, int statusCode = 200) => Task.Run(async () =>
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    });
}
