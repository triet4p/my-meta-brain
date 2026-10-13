using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Domain;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private sealed record S3T5ServiceExit(int ProcessId, DateTime StartedAtUtc);

    internal static async Task RunS3T5PreservationAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S3-T5 preservation smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S3-T5 exact source availability and selected encrypted preservation");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        var serviceExits = new List<S3T5ServiceExit>();
        byte[]? passphraseBytes = null;
        byte[]? originalBytes = null;
        byte[]? changedBytes = null;
        try
        {
            passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            passphraseBytes = null;

            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The actual owner service did not provision the synthetic preservation vault.");

            const string sourceId = "s3t5-source-fixture";
            const string itemId = "s3t5-item-fixture";
            const string zoneId = "s3t5-private-zone";
            const string sourceFormat = "s3t5.fixture";
            var sourcePath = Path.Combine(fixture.OutputDirectory, "s3t5-enrolled-fixture.jsonl");
            var movedChangedPath = Path.Combine(fixture.OutputDirectory, "s3t5-moved-changed-fixture.jsonl");
            var relocatedPath = Path.Combine(fixture.OutputDirectory, "s3t5-owner-verified-fixture.jsonl");
            var marker = "S3T5-SYNTHETIC-SOURCE-" + Guid.NewGuid().ToString("N");
            originalBytes = Encoding.UTF8.GetBytes("Synthetic fixture content; " + marker + "; no private session data.");
            changedBytes = (byte[])originalBytes.Clone();
            changedBytes[0] ^= 1;
            await File.WriteAllBytesAsync(sourcePath, originalBytes).ConfigureAwait(false);

            var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var sourceDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = "Synthetic S3-T5 source reference",
                locator = sourcePath,
                version = "fixture-v1",
                availability = "available",
                creationMethod = "directWrite",
                capturedAtUtc = capturedAt,
                recordedAtUtc = capturedAt.AddMinutes(1)
            });
            try
            {
                var sourceCreated = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
                {
                    protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                    operation = ServiceRequestHandler.SourceCreateOperation,
                    resourceId = sourceId,
                    zoneId,
                    contentBase64 = Convert.ToBase64String(sourceDraft)
                }).ConfigureAwait(false);
                S3T5RequireCode(sourceCreated, "source_created");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sourceDraft);
            }

            var enrolled = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceEnrollOperation,
                resourceId = sourceId,
                zoneId,
                sourceLocator = sourcePath,
                sourceFormat,
                sourceFormatVersion = 1
            }).ConfigureAwait(false);
            S3T5RequireCode(enrolled, "source_enrolled");

            var baselineStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            var baselineEnrollment = S3T5Object(baselineStatus, "sourceEnrollment");
            Require(S3T5Text(baselineEnrollment, "availability") == "available" &&
                    S3T5Long(baselineEnrollment, "sourceReferenceRevision") == 1,
                "The exact enrolled fixture did not report an available source reference at its initial revision.");
            var sourceRead = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireCode(sourceRead, "source");
            var initialSource = S3T5Object(sourceRead, "source");
            Require(S3T5Text(initialSource, "availability") == "available" &&
                    S3T5Long(initialSource, "revision") == 1 &&
                    S3T5Text(initialSource, "locator") == sourcePath &&
                    !initialSource.TryGetProperty("text", out _) &&
                    !sourceRead.GetRawText().Contains(marker, StringComparison.Ordinal),
                "The source-reference API did not return metadata-only reference state.");

            var itemDraft = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "question",
                author = "user",
                creationMethod = "directWrite",
                evidence = "unverified",
                level = "l1",
                title = "Synthetic source citation remains historical",
                body = "Synthetic citation fixture; it is never verified by a cached copy.",
                sources = new[]
                {
                    new
                    {
                        sourceId,
                        availability = "available",
                        sourceRevision = 1L,
                        span = new { start = 0L, end = (long)originalBytes.Length }
                    }
                },
                occurredAtUtc = capturedAt,
                recordedAtUtc = capturedAt.AddMinutes(1),
                applicability = (object?)null
            });
            try
            {
                var itemCreated = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
                {
                    protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                    operation = ServiceRequestHandler.MemoryCreateOperation,
                    resourceId = itemId,
                    zoneId,
                    contentBase64 = Convert.ToBase64String(itemDraft)
                }).ConfigureAwait(false);
                S3T5RequireCode(itemCreated, "memory_created");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(itemDraft);
            }

            var beforePreserve = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotListOperation,
                resourceId = sourceId
            }).ConfigureAwait(false);
            S3T5RequireCode(beforePreserve, "source_snapshots");
            Require(S3T5Array(beforePreserve, "sourceSnapshots").GetArrayLength() == 0,
                "Enrollment implicitly preserved source content before explicit owner selection.");

            var retentionOne = DateTimeOffset.UtcNow.AddHours(1);
            var retentionTwo = retentionOne.AddHours(2);
            var snapshotId = SourceSnapshotIds.For(sourceId, 1);
            var preserved = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotPreserveOperation,
                resourceId = sourceId,
                resourceRevision = 1L,
                snapshotRetainUntilUtc = retentionOne
            }).ConfigureAwait(false);
            S3T5RequireCode(preserved, "source_snapshot_preserved");
            var preservedMetadata = S3T5Object(preserved, "sourceSnapshot");
            Require(S3T5Text(preservedMetadata, "id") == snapshotId &&
                    S3T5Long(preservedMetadata, "revision") == 1 &&
                    S3T5Long(preservedMetadata, "sourceRevision") == 1 &&
                    S3T5Long(preservedMetadata, "sourceGeneration") == 1 &&
                    S3T5Long(preservedMetadata, "sourceLength") == originalBytes.Length &&
                    S3T5Text(preservedMetadata, "sourceDigest") == S3T5Digest(originalBytes) &&
                    (!preservedMetadata.TryGetProperty("contentBase64", out var preservedContent) ||
                     preservedContent.ValueKind == JsonValueKind.Null) &&
                    !preserved.GetRawText().Contains(sourcePath, StringComparison.Ordinal),
                "The owner-selected snapshot did not keep exact source lineage or leaked content/location in its metadata reply.");
            Require(await S3T5FileMatchesAsync(sourcePath, originalBytes).ConfigureAwait(false),
                "Snapshot creation modified the original source fixture.");
            var listed = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotListOperation,
                resourceId = sourceId
            }).ConfigureAwait(false);
            S3T5RequireCode(listed, "source_snapshots");
            Require(S3T5Array(listed, "sourceSnapshots").GetArrayLength() == 1 &&
                    S3T5Text(S3T5Array(listed, "sourceSnapshots")[0], "id") == snapshotId,
                "The explicit snapshot was not listed under its enrolled source.");
            await S3T5VerifyOwnerSnapshotAsync(fixture.ControlPipe, snapshotId, 1, originalBytes,
                sourceId, sourcePath, zoneId, retentionOne).ConfigureAwait(false);

            var retained = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotRetainOperation,
                resourceId = snapshotId,
                resourceRevision = 1L,
                snapshotRetainUntilUtc = retentionTwo
            }).ConfigureAwait(false);
            S3T5RequireCode(retained, "source_snapshot_retained");
            var retainedMetadata = S3T5Object(retained, "sourceSnapshot");
            Require(S3T5Long(retainedMetadata, "revision") == 2 &&
                    S3T5Date(retainedMetadata, "retainUntilUtc") == retentionTwo &&
                    S3T5Long(retainedMetadata, "sourceRevision") == 1 &&
                    S3T5Long(retainedMetadata, "sourceGeneration") == 1,
                "Owner-approved retention did not revise only snapshot retention while preserving source lineage.");
            await S3T5VerifyOwnerSnapshotAsync(fixture.ControlPipe, snapshotId, 2, originalBytes,
                sourceId, sourcePath, zoneId, retentionTwo).ConfigureAwait(false);

            var invalidMixedGrant = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerScopePreviewOperation,
                resourceIds = new[] { sourceId, snapshotId },
                operations = new[] { ServiceRequestHandler.SourceReadOperation, ServiceRequestHandler.SourceSnapshotReadOperation },
                expiresAtUtc = DateTimeOffset.UtcNow.AddHours(2)
            }).ConfigureAwait(false);
            S3T5RequireError(invalidMixedGrant, "invalid_grant_request");
            var invalidSourcePermission = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerScopePreviewOperation,
                resourceIds = new[] { snapshotId },
                operations = new[] { ServiceRequestHandler.SourceReadOperation },
                expiresAtUtc = DateTimeOffset.UtcNow.AddHours(2)
            }).ConfigureAwait(false);
            S3T5RequireError(invalidSourcePermission, "invalid_grant_request");
            var invalidSnapshotPermission = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerScopePreviewOperation,
                resourceIds = new[] { sourceId },
                operations = new[] { ServiceRequestHandler.SourceSnapshotReadOperation },
                expiresAtUtc = DateTimeOffset.UtcNow.AddHours(2)
            }).ConfigureAwait(false);
            S3T5RequireError(invalidSnapshotPermission, "invalid_grant_request");

            var sourceSessionFile = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture,
                sourceId, ServiceRequestHandler.SourceReadOperation).ConfigureAwait(false);
            var itemSessionFile = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture,
                itemId, ServiceRequestHandler.MemoryReadOperation).ConfigureAwait(false);
            var snapshotSessionFile = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture,
                snapshotId, ServiceRequestHandler.SourceSnapshotReadOperation).ConfigureAwait(false);
            var sourceBearer = ReadSessionBearer(sourceSessionFile);
            var itemBearer = ReadSessionBearer(itemSessionFile);
            var snapshotBearer = ReadSessionBearer(snapshotSessionFile);
            var sourceSessionInfo = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = "agent.session.inspect",
                sessionToken = sourceBearer
            }).ConfigureAwait(false);
            S3T5RequireCode(sourceSessionInfo, "session_valid");
            var sourceScope = S3T5Object(sourceSessionInfo, "agentSession");
            var sourceScopeResource = S3T5Array(sourceScope, "resources");
            var sourceScopeOperations = S3T5Array(sourceScope, "operations");
            Require(sourceScopeResource.GetArrayLength() == 1 &&
                    S3T5Text(sourceScopeResource[0], "resourceId") == sourceId &&
                    S3T5Text(sourceScopeResource[0], "zoneId") == zoneId &&
                    S3T5Long(sourceScopeResource[0], "revision") == 1 &&
                    sourceScopeOperations.GetArrayLength() == 1 &&
                    sourceScopeOperations[0].GetString() == ServiceRequestHandler.SourceReadOperation,
                "The source-reference agent session did not freeze exactly its source ID, zone, revision and operation.");
            var snapshotSessionInfo = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = "agent.session.inspect",
                sessionToken = snapshotBearer
            }).ConfigureAwait(false);
            S3T5RequireCode(snapshotSessionInfo, "session_valid");
            var snapshotScope = S3T5Object(snapshotSessionInfo, "agentSession");
            var snapshotScopeResource = S3T5Array(snapshotScope, "resources");
            var snapshotScopeOperations = S3T5Array(snapshotScope, "operations");
            Require(snapshotScopeResource.GetArrayLength() == 1 &&
                    S3T5Text(snapshotScopeResource[0], "resourceId") == snapshotId &&
                    S3T5Text(snapshotScopeResource[0], "zoneId") == zoneId &&
                    S3T5Long(snapshotScopeResource[0], "revision") == 2 &&
                    snapshotScopeOperations.GetArrayLength() == 1 &&
                    snapshotScopeOperations[0].GetString() == ServiceRequestHandler.SourceSnapshotReadOperation,
                "The snapshot agent session did not freeze exactly its snapshot ID, zone, revision and operation.");

            var sourceGranted = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceReadOperation,
                sessionToken = sourceBearer,
                accessOperation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireCode(sourceGranted, "source");
            var sourceGrantWire = S3T5Object(sourceGranted, "source");
            Require(S3T5Long(sourceGrantWire, "revision") == 1 &&
                    S3T5Text(sourceGrantWire, "availability") == "available" &&
                    !sourceGrantWire.TryGetProperty("text", out _) &&
                    !sourceGranted.GetRawText().Contains(marker, StringComparison.Ordinal),
                "The reference-only agent permission disclosed source content or failed to remain revision-bound.");

            await S3T5VerifyAgentSnapshotAsync(fixture.AgentPipe, snapshotBearer, snapshotId, 2,
                originalBytes, sourceId, zoneId, retentionTwo).ConfigureAwait(false);

            var snapshotViaSourceGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceSnapshotReadOperation,
                sessionToken = sourceBearer,
                accessOperation = ServiceRequestHandler.SourceSnapshotReadOperation,
                resourceId = snapshotId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireDenied(snapshotViaSourceGrant);
            var sourceViaSnapshotGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceReadOperation,
                sessionToken = snapshotBearer,
                accessOperation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireDenied(sourceViaSnapshotGrant);
            var itemViaSourceGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentMemoryReadOperation,
                sessionToken = sourceBearer,
                accessOperation = ServiceRequestHandler.MemoryReadOperation,
                resourceId = itemId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireDenied(itemViaSourceGrant);
            var snapshotViaItemGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceSnapshotReadOperation,
                sessionToken = itemBearer,
                accessOperation = ServiceRequestHandler.SourceSnapshotReadOperation,
                resourceId = snapshotId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireDenied(snapshotViaItemGrant);
            var genericSnapshotRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentResourceReadOperation,
                sessionToken = snapshotBearer,
                accessOperation = ServiceRequestHandler.ResourceReadOperation,
                resourceId = snapshotId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireDenied(genericSnapshotRead);
            var wrongSnapshotRevision = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceSnapshotReadOperation,
                sessionToken = snapshotBearer,
                accessOperation = ServiceRequestHandler.SourceSnapshotReadOperation,
                resourceId = snapshotId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireDenied(wrongSnapshotRevision);
            var itemProjection = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentMemoryReadOperation,
                sessionToken = itemBearer,
                accessOperation = ServiceRequestHandler.MemoryReadOperation,
                resourceId = itemId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireCode(itemProjection, "memory");
            var itemWire = S3T5Object(itemProjection, "memoryItem");
            var citation = S3T5Array(itemWire, "sources")[0];
            var citationSpan = S3T5Object(citation, "span");
            Require(S3T5Text(itemWire, "evidence") == "unverified" &&
                    S3T5Long(citation, "sourceRevision") == 1 &&
                    S3T5Long(citationSpan, "start") == 0 &&
                    S3T5Long(citationSpan, "end") == originalBytes.Length &&
                    !itemProjection.GetRawText().Contains(sourcePath, StringComparison.Ordinal) &&
                    !itemProjection.GetRawText().Contains(marker, StringComparison.Ordinal),
                "An item-only grant changed historical citation span/lineage or disclosed source contents/location.");

            Console.WriteLine("PASS default reference-only source read and separate item/source/snapshot grants enforce exact IDs, revisions, operations and zone");

            var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
            await File.WriteAllBytesAsync(sourcePath, changedBytes).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(sourcePath, originalWriteTime);
            var afterChangeInfo = new FileInfo(sourcePath);
            Require(afterChangeInfo.Length == originalBytes.Length &&
                    File.GetLastWriteTimeUtc(sourcePath) == originalWriteTime,
                "The same-size source-change fixture did not preserve its original last-write time.");
            var changedStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            var changedEnrollment = S3T5Object(changedStatus, "sourceEnrollment");
            Require(S3T5Text(changedEnrollment, "availability") == "changed" &&
                    S3T5Long(changedEnrollment, "sourceReferenceRevision") == 2,
                "A same-size, same-mtime content change was not detected or did not revise source-reference availability.");
            var unavailableSource = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireCode(unavailableSource, "source");
            Require(S3T5Text(S3T5Object(unavailableSource, "source"), "availability") == "unavailable",
                "Changed source bytes remained marked available in the registered source API.");
            var changedSourceSessionFile = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture,
                sourceId, ServiceRequestHandler.SourceReadOperation).ConfigureAwait(false);
            var changedSourceBearer = ReadSessionBearer(changedSourceSessionFile);
            var changedSourceRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceReadOperation,
                sessionToken = changedSourceBearer,
                accessOperation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireCode(changedSourceRead, "source");
            Require(S3T5Text(S3T5Object(changedSourceRead, "source"), "availability") == "unavailable",
                "A source.read grant at the current revision did not expose the truthful unavailable state.");
            var wrongSourceRevision = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceReadOperation,
                sessionToken = changedSourceBearer,
                accessOperation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireDenied(wrongSourceRevision);
            var staleSourceGrant = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceReadOperation,
                sessionToken = sourceBearer,
                accessOperation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireDenied(staleSourceGrant);
            var changedPreserve = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotPreserveOperation,
                resourceId = sourceId,
                resourceRevision = 2L,
                snapshotRetainUntilUtc = retentionTwo.AddHours(1)
            }).ConfigureAwait(false);
            S3T5RequireError(changedPreserve, "source_changed");
            await S3T5VerifyOwnerSnapshotAsync(fixture.ControlPipe, snapshotId, 2, originalBytes,
                sourceId, sourcePath, zoneId, retentionTwo).ConfigureAwait(false);

            var historicalItem = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentMemoryReadOperation,
                sessionToken = itemBearer,
                accessOperation = ServiceRequestHandler.MemoryReadOperation,
                resourceId = itemId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireCode(historicalItem, "memory");
            var historicalMemory = S3T5Object(historicalItem, "memoryItem");
            var historicalCitation = S3T5Array(historicalMemory, "sources")[0];
            var historicalSpan = S3T5Object(historicalCitation, "span");
            Require(S3T5Text(historicalMemory, "evidence") == "unverified" &&
                    S3T5Long(historicalCitation, "sourceRevision") == 1 &&
                    S3T5Long(historicalSpan, "start") == 0 &&
                    S3T5Long(historicalSpan, "end") == originalBytes.Length,
                "An old evidence citation's span or revision changed after its original changed.");

            File.Move(sourcePath, movedChangedPath);
            var missingStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            Require(S3T5Text(S3T5Object(missingStatus, "sourceEnrollment"), "availability") == "missing" &&
                    S3T5Long(S3T5Object(missingStatus, "sourceEnrollment"), "sourceReferenceRevision") == 2,
                "The source status followed a moved file instead of reporting its exact enrolled locator missing.");
            var rejectedRelocation = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceRelocateOperation,
                resourceId = sourceId,
                sourceLocator = movedChangedPath,
                sourceGeneration = 1L
            }).ConfigureAwait(false);
            S3T5RequireError(rejectedRelocation, "source_relocation_changed");
            var stillMissing = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            Require(S3T5Text(S3T5Object(stillMissing, "sourceEnrollment"), "availability") == "missing",
                "A digest-mismatched relocation candidate changed the enrolled mapping.");
            Require(await S3T5FileMatchesAsync(movedChangedPath, changedBytes).ConfigureAwait(false),
                "The service modified the changed relocation candidate while refusing it.");

            await File.WriteAllBytesAsync(relocatedPath, originalBytes).ConfigureAwait(false);
            var relocation = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceRelocateOperation,
                resourceId = sourceId,
                sourceLocator = relocatedPath,
                sourceGeneration = 1L
            }).ConfigureAwait(false);
            S3T5RequireCode(relocation, "source_relocated");
            var relocatedStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            var relocatedEnrollment = S3T5Object(relocatedStatus, "sourceEnrollment");
            Require(S3T5Text(relocatedEnrollment, "availability") == "available" &&
                    S3T5Long(relocatedEnrollment, "generation") == 1 &&
                    S3T5Long(relocatedEnrollment, "sourceReferenceRevision") == 3 &&
                    relocatedEnrollment.GetProperty("relocatedAtUtc").ValueKind == JsonValueKind.String,
                "Owner-verified exact-digest relocation did not keep its generation and update source-reference availability.");
            var relocatedSource = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 3L
            }).ConfigureAwait(false);
            S3T5RequireCode(relocatedSource, "source");
            var relocatedSourceWire = S3T5Object(relocatedSource, "source");
            Require(S3T5Text(relocatedSourceWire, "locator") == relocatedPath &&
                    S3T5Text(relocatedSourceWire, "availability") == "available" &&
                    S3T5Long(relocatedSourceWire, "revision") == 3 &&
                    !relocatedSource.GetRawText().Contains(marker, StringComparison.Ordinal),
                "The owner-verified relocation did not update only the encrypted reference metadata.");
            Require(await S3T5FileMatchesAsync(relocatedPath, originalBytes).ConfigureAwait(false) &&
                    await S3T5FileMatchesAsync(movedChangedPath, changedBytes).ConfigureAwait(false),
                "Relocation changed an original source candidate.");

            var currentPathSnapshot = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentSourceReadOperation,
                sessionToken = sourceBearer,
                accessOperation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireDenied(currentPathSnapshot);
            await S3T5VerifyOwnerSnapshotAsync(fixture.ControlPipe, snapshotId, 2, originalBytes,
                sourceId, relocatedPath, zoneId, retentionTwo).ConfigureAwait(false);

            File.Delete(relocatedPath);
            var removedStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            var removedEnrollment = S3T5Object(removedStatus, "sourceEnrollment");
            Require(S3T5Text(removedEnrollment, "availability") == "missing" &&
                    S3T5Long(removedEnrollment, "sourceReferenceRevision") == 4,
                "Removing the verified current source did not return a truthful missing/unavailable state.");
            var removedReference = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceReadOperation,
                resourceId = sourceId,
                resourceRevision = 4L
            }).ConfigureAwait(false);
            S3T5RequireCode(removedReference, "source");
            Require(S3T5Text(S3T5Object(removedReference, "source"), "availability") == "unavailable",
                "A removed source remained marked available in the source-reference API.");
            await S3T5VerifyOwnerSnapshotAsync(fixture.ControlPipe, snapshotId, 2, originalBytes,
                sourceId, relocatedPath, zoneId, retentionTwo).ConfigureAwait(false);
            var missingItem = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.AgentMemoryReadOperation,
                sessionToken = itemBearer,
                accessOperation = ServiceRequestHandler.MemoryReadOperation,
                resourceId = itemId,
                resourceRevision = 1L
            }).ConfigureAwait(false);
            S3T5RequireCode(missingItem, "memory");
            var missingMemory = S3T5Object(missingItem, "memoryItem");
            var missingCitation = S3T5Array(missingMemory, "sources")[0];
            var missingSpan = S3T5Object(missingCitation, "span");
            Require(S3T5Text(missingMemory, "evidence") == "unverified" &&
                    S3T5Long(missingCitation, "sourceRevision") == 1 &&
                    S3T5Long(missingSpan, "start") == 0 &&
                    S3T5Long(missingSpan, "end") == originalBytes.Length,
                "A removed original changed historical evidence state, citation span or revision.");

            var inspectSnapshotSession = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = "agent.session.inspect",
                sessionToken = snapshotBearer
            }).ConfigureAwait(false);
            S3T5RequireCode(inspectSnapshotSession, "session_valid");
            var sessionId = S3T5Text(S3T5Object(inspectSnapshotSession, "agentSession"), "sessionId");
            var revoked = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.OwnerSessionRevokeOperation,
                sessionId
            }).ConfigureAwait(false);
            S3T5RequireCode(revoked, "session_revoked");
            var revokedSnapshotRead = await S3T5AgentSnapshotRequestAsync(fixture.AgentPipe, snapshotBearer, snapshotId, 2).ConfigureAwait(false);
            S3T5RequireDenied(revokedSnapshotRead);

            var lockSessionFile = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture,
                snapshotId, ServiceRequestHandler.SourceSnapshotReadOperation).ConfigureAwait(false);
            var lockBearer = ReadSessionBearer(lockSessionFile);
            var ciphertextBeforeLock = HashResourceCiphertexts(fixture.VaultResourcesPath);
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0 && locked.StandardOutput.Contains("in-flight private operations drained", StringComparison.Ordinal),
                "The owner lock did not drain the service's private snapshot operations.");
            var lockedRead = await S3T5OwnerSnapshotRequestAsync(fixture.ControlPipe, snapshotId, 2).ConfigureAwait(false);
            S3T5RequireError(lockedRead, "vault_locked");
            var lockedStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            S3T5RequireError(lockedStatus, "vault_locked");
            var lockedRetain = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotRetainOperation,
                resourceId = snapshotId,
                resourceRevision = 2L,
                snapshotRetainUntilUtc = retentionTwo.AddHours(1)
            }).ConfigureAwait(false);
            S3T5RequireError(lockedRetain, "vault_locked");
            var lockedPreserve = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotPreserveOperation,
                resourceId = sourceId,
                resourceRevision = 4L,
                snapshotRetainUntilUtc = retentionTwo.AddHours(2)
            }).ConfigureAwait(false);
            S3T5RequireError(lockedPreserve, "vault_locked");
            var lockedPurge = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotPurgeOperation,
                resourceId = snapshotId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireError(lockedPurge, "vault_locked");
            var lockedAgentRead = await S3T5AgentSnapshotRequestAsync(fixture.AgentPipe, lockBearer, snapshotId, 2).ConfigureAwait(false);
            S3T5RequireDenied(lockedAgentRead);
            Require(HashResourceCiphertexts(fixture.VaultResourcesPath).SequenceEqual(ciphertextBeforeLock) &&
                    !HasTemporaryFiles(fixture.VaultDirectoryPath) && !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "Locked read/refresh/write denials changed encrypted resources or left a temporary file.");

            var stopping = service;
            serviceExits.Add(await StopS3T5ServiceAsync(stopping).ConfigureAwait(false));
            service = null;
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            Require((await ReadStatusAsync(connectionsExecutable, fixture.ControlPipe).ConfigureAwait(false))
                    .Contains("vault=locked", StringComparison.Ordinal),
                "The restarted service did not return to the locked state.");
            var restartLockedRead = await S3T5OwnerSnapshotRequestAsync(fixture.ControlPipe, snapshotId, 2).ConfigureAwait(false);
            S3T5RequireError(restartLockedRead, "vault_locked");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The real owner service did not unlock the same encrypted vault after restart.");
            var restartOldSession = await S3T5AgentSnapshotRequestAsync(fixture.AgentPipe, lockBearer, snapshotId, 2).ConfigureAwait(false);
            S3T5RequireDenied(restartOldSession);

            var restartedList = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotListOperation,
                resourceId = sourceId
            }).ConfigureAwait(false);
            S3T5RequireCode(restartedList, "source_snapshots");
            var restartedDescriptors = S3T5Array(restartedList, "sourceSnapshots");
            Require(restartedDescriptors.GetArrayLength() == 1 &&
                    S3T5Text(restartedDescriptors[0], "id") == snapshotId &&
                    S3T5Long(restartedDescriptors[0], "revision") == 2 &&
                    S3T5Long(restartedDescriptors[0], "generation") == 1,
                "The preserved snapshot identity/revision/generation did not persist across lock, restart and unlock.");
            await S3T5VerifyOwnerSnapshotAsync(fixture.ControlPipe, snapshotId, 2, originalBytes,
                sourceId, relocatedPath, zoneId, retentionTwo).ConfigureAwait(false);
            var postRestartStatus = await S3T5SourceStatusAsync(fixture.ControlPipe, sourceId).ConfigureAwait(false);
            Require(S3T5Text(S3T5Object(postRestartStatus, "sourceEnrollment"), "availability") == "missing" &&
                    S3T5Long(S3T5Object(postRestartStatus, "sourceEnrollment"), "sourceReferenceRevision") == 4,
                "Restart converted a missing original into a verified or implicitly followed source.");

            var postRestartSnapshotSession = await IssueS2T3AgentSessionAsync(connectionsExecutable, fixture,
                snapshotId, ServiceRequestHandler.SourceSnapshotReadOperation).ConfigureAwait(false);
            var postRestartBearer = ReadSessionBearer(postRestartSnapshotSession);
            await S3T5VerifyAgentSnapshotAsync(fixture.AgentPipe, postRestartBearer, snapshotId, 2,
                originalBytes, sourceId, zoneId, retentionTwo).ConfigureAwait(false);

            var purge = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotPurgeOperation,
                resourceId = snapshotId,
                resourceRevision = 2L
            }).ConfigureAwait(false);
            S3T5RequireCode(purge, "source_snapshot_purged");
            var purgedMetadata = S3T5Object(purge, "sourceSnapshot");
            Require(S3T5Long(purgedMetadata, "revision") == 3 &&
                    purgedMetadata.GetProperty("purged").ValueKind == JsonValueKind.True &&
                    (!purgedMetadata.TryGetProperty("contentBase64", out var purgedContent) ||
                     purgedContent.ValueKind == JsonValueKind.Null),
                "Snapshot purge did not replace content with a revisioned content-free tombstone.");
            var purgedRead = await S3T5OwnerSnapshotRequestAsync(fixture.ControlPipe, snapshotId, 3).ConfigureAwait(false);
            S3T5RequireError(purgedRead, "source_snapshot_purged");
            var supersededAgentRead = await S3T5AgentSnapshotRequestAsync(fixture.AgentPipe, postRestartBearer, snapshotId, 2).ConfigureAwait(false);
            S3T5RequireDenied(supersededAgentRead);
            var purgedList = await InvokeOwnerRequestAsync(fixture.ControlPipe, new
            {
                protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
                operation = ServiceRequestHandler.SourceSnapshotListOperation,
                resourceId = sourceId
            }).ConfigureAwait(false);
            S3T5RequireCode(purgedList, "source_snapshots");
            Require(S3T5Array(purgedList, "sourceSnapshots").GetArrayLength() == 1 &&
                    S3T5Long(S3T5Array(purgedList, "sourceSnapshots")[0], "revision") == 3,
                "The purged snapshot tombstone did not remain discoverable at its new canonical revision.");
            var index = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(index.ExitCode == 0 &&
                    index.StandardOutput.Contains("kind=preservedSourceSnapshot", StringComparison.Ordinal) &&
                    index.StandardOutput.Contains("state=purged", StringComparison.Ordinal) &&
                    !index.StandardOutput.Contains(marker, StringComparison.Ordinal) &&
                    !index.StandardOutput.Contains(sourcePath, StringComparison.Ordinal),
                "The rebuilt private index did not classify the content-free snapshot tombstone without content or locator.");

            var encryptedResources = Directory.GetFiles(fixture.VaultResourcesPath, "*.mbv", SearchOption.TopDirectoryOnly);
            Require(encryptedResources.Length == 3 &&
                    encryptedResources.All(path =>
                    {
                        var envelope = File.ReadAllText(path, Encoding.UTF8);
                        return envelope.Contains("\"ciphertext\"", StringComparison.Ordinal) &&
                            envelope.Contains("\"nonce\"", StringComparison.Ordinal);
                    }),
                "The source reference, item and snapshot were not stored as encrypted canonical resource envelopes.");
            Require(!S3T5HasDiskIndexFiles(fixture.VaultDirectoryPath) &&
                    !S3T5HasDiskIndexFiles(fixture.ServiceDirectoryPath),
                "The private index created a plaintext database, journal or WAL file.");
            Require(!S3T5TreeContains(fixture.VaultDirectoryPath, marker) &&
                    !S3T5TreeContains(fixture.VaultDirectoryPath, sourcePath) &&
                    !S3T5TreeContains(fixture.ServiceDirectoryPath, marker) &&
                    !S3T5TreeContains(fixture.ServiceDirectoryPath, sourcePath) &&
                    !HasTemporaryFiles(fixture.VaultDirectoryPath) &&
                    !HasTemporaryFiles(fixture.ServiceDirectoryPath),
                "Source bytes, the private locator, or an atomic temporary file leaked into the vault/service trees.");

        }
        finally
        {
            if (service is not null)
            {
                var stopping = service;
                serviceExits.Add(await StopS3T5ServiceAsync(stopping).ConfigureAwait(false));
                service = null;
            }

            if (passphraseBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);
            }

            if (originalBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(originalBytes);
            }

            if (changedBytes is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(changedBytes);
            }

            foreach (var exit in serviceExits)
            {
                Console.WriteLine($"CLEANUP service pid={exit.ProcessId} startedUtc={exit.StartedAtUtc:O} exited=yes exactHandle=true treeKill=false");
            }
        }

        Console.WriteLine("PASS same-size/mtime mutation, exact-missing behavior, digest-verified owner relocation and stale evidence lineage");
        Console.WriteLine("PASS retained snapshot content survives restart/unlock; session revoke, lock, wrong scope/revision, purge and private-index boundaries deny or classify correctly");
        Console.WriteLine("PASS source fixtures remain application-read-only; vault/service persistence contains ciphertext and no source-body/locator spill");
    }

    private static async Task<S3T5ServiceExit> StopS3T5ServiceAsync(RunningService service)
    {
        var process = service.Process;
        var exit = new S3T5ServiceExit(process.Id, process.StartTime.ToUniversalTime());
        if (!process.HasExited)
        {
            process.Kill();
        }

        await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
        await Task.WhenAll(service.StandardOutputDrain, service.StandardErrorDrain)
            .WaitAsync(CleanupTimeout).ConfigureAwait(false);
        Require(process.HasExited, "The exact S3-T5-owned service process did not exit after cleanup.");
        process.Dispose();
        return exit;
    }

    private static async Task<JsonElement> S3T5SourceStatusAsync(string controlPipe, string sourceId) =>
        await InvokeOwnerRequestAsync(controlPipe, new
        {
            protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
            operation = ServiceRequestHandler.SourceStatusOperation,
            resourceId = sourceId
        }).ConfigureAwait(false);

    private static async Task<JsonElement> S3T5OwnerSnapshotRequestAsync(string controlPipe, string snapshotId, long revision) =>
        await InvokeOwnerRequestAsync(controlPipe, new
        {
            protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
            operation = ServiceRequestHandler.SourceSnapshotReadOperation,
            resourceId = snapshotId,
            resourceRevision = revision
        }).ConfigureAwait(false);

    private static async Task<JsonElement> S3T5AgentSnapshotRequestAsync(string agentPipe, string bearer, string snapshotId, long revision) =>
        await InvokeAgentRequestAsync(agentPipe, new
        {
            protocolVersion = ServiceRequestHandler.CurrentProtocolVersion,
            operation = ServiceRequestHandler.AgentSourceSnapshotReadOperation,
            sessionToken = bearer,
            accessOperation = ServiceRequestHandler.SourceSnapshotReadOperation,
            resourceId = snapshotId,
            resourceRevision = revision
        }).ConfigureAwait(false);

    private static async Task S3T5VerifyOwnerSnapshotAsync(
        string controlPipe,
        string snapshotId,
        long revision,
        byte[] expectedContent,
        string sourceId,
        string expectedLocator,
        string zoneId,
        DateTimeOffset retainUntilUtc)
    {
        var reply = await S3T5OwnerSnapshotRequestAsync(controlPipe, snapshotId, revision).ConfigureAwait(false);
        S3T5RequireCode(reply, "source_snapshot");
        var snapshot = S3T5Object(reply, "sourceSnapshot");
        Require(S3T5Text(snapshot, "id") == snapshotId &&
                S3T5Text(snapshot, "zoneId") == zoneId &&
                S3T5Text(snapshot, "sourceId") == sourceId &&
                S3T5Long(snapshot, "revision") == revision &&
                S3T5Long(snapshot, "sourceRevision") == 1 &&
                S3T5Long(snapshot, "sourceGeneration") == 1 &&
                S3T5Long(snapshot, "sourceLength") == expectedContent.Length &&
                S3T5Text(snapshot, "sourceDigest") == S3T5Digest(expectedContent) &&
                S3T5Date(snapshot, "retainUntilUtc") == retainUntilUtc &&
                snapshot.GetProperty("purged").ValueKind == JsonValueKind.False &&
                !snapshot.TryGetProperty("locator", out _) &&
                !reply.GetRawText().Contains(JsonSerializer.Serialize(expectedLocator), StringComparison.Ordinal),
            "The owner snapshot response lost lineage/retention metadata or disclosed the source locator.");
        var content = Convert.FromBase64String(S3T5Text(snapshot, "contentBase64"));
        try
        {
            Require(content.AsSpan().SequenceEqual(expectedContent),
                "The selected encrypted snapshot did not return the exact enrolled source bytes.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }

    }

    private static async Task S3T5VerifyAgentSnapshotAsync(
        string agentPipe,
        string bearer,
        string snapshotId,
        long revision,
        byte[] expectedContent,
        string sourceId,
        string zoneId,
        DateTimeOffset retainUntilUtc)
    {
        var reply = await S3T5AgentSnapshotRequestAsync(agentPipe, bearer, snapshotId, revision).ConfigureAwait(false);
        S3T5RequireCode(reply, "source_snapshot");
        var snapshot = S3T5Object(reply, "sourceSnapshot");
        Require(S3T5Text(snapshot, "id") == snapshotId &&
                S3T5Text(snapshot, "zoneId") == zoneId &&
                S3T5Text(snapshot, "sourceId") == sourceId &&
                S3T5Long(snapshot, "revision") == revision &&
                S3T5Long(snapshot, "sourceRevision") == 1 &&
                S3T5Long(snapshot, "sourceGeneration") == 1 &&
                S3T5Date(snapshot, "retainUntilUtc") == retainUntilUtc &&
                !snapshot.TryGetProperty("locator", out _),
            "The scoped agent snapshot response did not preserve source lineage/revision/zone or leaked the locator.");
        var content = Convert.FromBase64String(S3T5Text(snapshot, "contentBase64"));
        try
        {
            Require(content.AsSpan().SequenceEqual(expectedContent),
                "The scoped source.snapshot.read permission did not return the selected snapshot bytes.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<bool> S3T5FileMatchesAsync(string path, byte[] expected)
    {
        var content = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        try
        {
            return content.AsSpan().SequenceEqual(expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static bool S3T5TreeContains(string root, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        try
        {
            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var content = File.ReadAllBytes(path);
                try
                {
                    if (content.AsSpan().IndexOf(needle) >= 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }

            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(needle);
        }
    }

    private static bool S3T5HasDiskIndexFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Any(path =>
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
        });


    private static string S3T5Digest(byte[] content)
    {
        var hash = SHA256.HashData(content);
        try
        {
            return Convert.ToBase64String(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static JsonElement S3T5Object(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException("The S3-T5 response omitted an expected object.");

    private static JsonElement S3T5Array(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidOperationException("The S3-T5 response omitted an expected array.");

    private static string S3T5Text(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException("The S3-T5 response omitted an expected string.");

    private static long S3T5Long(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : throw new InvalidOperationException("The S3-T5 response omitted an expected integer.");

    private static DateTimeOffset S3T5Date(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var result)
            ? result
            : throw new InvalidOperationException("The S3-T5 response omitted an expected timestamp.");

    private static string S3T5Code(JsonElement reply) =>
        reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()!
            : reply.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()!
                : string.Empty;

    private static void S3T5RequireCode(JsonElement reply, string expected) =>
        Require(S3T5Code(reply) == expected, "The S3-T5 service response did not match the expected operation result.");

    private static void S3T5RequireError(JsonElement reply, string expected) =>
        Require(reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() == expected,
            "The S3-T5 service did not fail closed with the expected error.");

    private static void S3T5RequireDenied(JsonElement reply) =>
        Require(reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String,
            "The S3-T5 scoped service unexpectedly served an unauthorized/stale read.");
}
