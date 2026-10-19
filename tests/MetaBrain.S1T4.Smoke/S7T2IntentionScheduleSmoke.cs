using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;
using MetaBrain.OwnerUi;

namespace MetaBrain.S1T4.Smoke;

internal static partial class OwnerServiceSmoke
{
    private const string S7T2Zone = "s7t2-private-zone";
    private const string S7T2ControlledId = "s7t2-controlled-intention";
    private const string S7T2ControlledTitle = "S7T2_SYNTHETIC_CONTROLLED_TITLE_36c2";
    private const string S7T2ControlledBody = "S7T2_SYNTHETIC_CONTROLLED_BODY_1a8f";
    private const string S7T2UiAcknowledgeId = "s7t2-ui-acknowledge";
    private const string S7T2UiAcknowledgeBody = "S7T2_SYNTHETIC_UI_ACK_BODY_44ac";
    private const string S7T2UiAcknowledgeTitle = "S7T2_SYNTHETIC_UI_ACK_TITLE_f918";
    private const string S7T2UiCompleteId = "s7t2-ui-complete";
    private const string S7T2UiCompleteBody = "S7T2_SYNTHETIC_UI_COMPLETE_BODY_a9f4";
    private const string S7T2UiCompleteTitle = "S7T2_SYNTHETIC_UI_COMPLETE_TITLE_37b2";
    private const string S7T2UiLockedId = "s7t2-ui-locked";
    private const string S7T2UiLockedBody = "S7T2_SYNTHETIC_UI_LOCKED_BODY_0eb3";
    private const string S7T2UiLockedTitle = "S7T2_SYNTHETIC_UI_LOCKED_TITLE_c61d";

    internal static async Task RunS7T2IntentionScheduleAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S7-T2 intention scheduler smoke requires Windows named pipes, DPAPI and WPF.");
        }

        Console.WriteLine("RUN S7-T2 explicit intention time triggers, controlled catch-up and real WPF due-list actions");
        await RunS7T2ControlledSchedulerAsync().ConfigureAwait(false);
        Console.WriteLine("PASS controlled-time encrypted scheduler: locked/restart catch-up, one-shot notification, acknowledgement, reschedule and cancellation");
        await RunS7T2OwnerUiAsync(connectionsExecutable).ConfigureAwait(false);
        Console.WriteLine("PASS actual named-pipe service and shown WPF intention list: create/edit/cancel/acknowledge/complete/lock/restart/unlock");
        Console.WriteLine("LIMIT synthetic Windows-only data and a throwaway vault; no LLM/provider/network request, external notification channel, installed service, machine sleep/clock correction or secure-erasure claim was exercised.");
    }

    private static async Task RunS7T2ControlledSchedulerAsync()
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        var ownerSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The controlled S7-T2 fixture has no Windows owner SID.");
        var store = new EncryptedVaultStore(fixture.VaultDirectoryPath, ownerSid.Value);
        var passphrase = "S7T2-synthetic-controlled-passphrase-" + Guid.NewGuid().ToString("N");
        var vault = new VaultLifecycle(store);
        Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
            "The controlled S7-T2 vault was not provisioned.");

        var clock = new S7T2ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = S7T2CreateHandler(vault, clock);
        var context = AuthenticatedContext.ForOwner();
        var initialDue = clock.GetUtcNow().AddMinutes(1).ToOffset(TimeSpan.FromMinutes(330));
        var initialDueText = initialDue.ToString("O", CultureInfo.InvariantCulture);
        var rescheduledDue = clock.GetUtcNow().AddMinutes(20).ToOffset(TimeSpan.FromMinutes(-240));
        var rescheduledDueText = rescheduledDue.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            var itemDraft = JsonSerializer.SerializeToUtf8Bytes(new MemoryItemDraft(
                MemoryKind.Intention,
                SpeakerRole.User,
                CreationMethod.DirectWrite,
                EvidenceLevel.Unverified,
                AbstractionLevel.L1,
                S7T2ControlledTitle,
                S7T2ControlledBody,
                Array.Empty<MemorySourceReference>(),
                null,
                clock.GetUtcNow(),
                null));
            try
            {
                using var operation = vault.TryBeginOperation()
                    ?? throw new InvalidOperationException("The controlled S7-T2 vault did not open an owner operation.");
                _ = new MemoryItemService().Create(context, S7T2ControlledId, S7T2Zone, itemDraft, operation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(itemDraft);
            }

            Require(IntentionTimeTrigger.TryParse("2026-10-19T17:00:00+05:30", out var offsetStamp) &&
                    offsetStamp is { TimeZoneOffsetMinutes: 330, InstantUtc: { } parsedInstant } &&
                    parsedInstant == new DateTimeOffset(2026, 10, 19, 11, 30, 0, TimeSpan.Zero),
                "The exact offset-qualified due timestamp did not preserve its known offset and corresponding instant.");
            var repeatedHourEarlier = IntentionTimeTrigger.TryParse("2026-11-01T01:30:00-04:00", out var earlierInstant);
            var repeatedHourLater = IntentionTimeTrigger.TryParse("2026-11-01T01:30:00-05:00", out var laterInstant);
            Require(repeatedHourEarlier && repeatedHourLater &&
                    earlierInstant?.InstantUtc is { } earlierUtc && laterInstant?.InstantUtc is { } laterUtc &&
                    laterUtc - earlierUtc == TimeSpan.FromHours(1),
                "An explicit offset did not unambiguously select one of the repeated DST wall-clock instants.");
            foreach (var invalid in new[]
                     {
                         "2026-10-19",
                         "2026-10-19T12:00:00",
                         "2026-11-01T01:30:00",
                         "2026-10-19 12:00:00Z",
                         "2026-10-19T12:00:00+14:01",
                         "2026-10-19T12:00:00-08:60",
                         "2026-11-01T01:30:00[America/New_York]"
                     })
            {
                Require(!IntentionTimeTrigger.TryParse(invalid, out _),
                    "A date-only, timezone-free, ambiguous-wall-clock, named-zone, or invalid-offset timestamp was accepted: " + invalid);
            }

            var legacy = new StoredLifecycleRecord(
                1,
                "lc-s7t2-v1-legacy",
                S7T2Zone,
                "s7t2-v1-legacy",
                MemoryKind.Intention,
                PublicationState.Proposal,
                null,
                IntentionState.Pending,
                false,
                null,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                ExecutorKind.Owner,
                CreationMethod.DirectWrite,
                "Synthetic pre-schedule lifecycle.",
                DateTimeOffset.UtcNow,
                Array.Empty<LifecycleStateChange>(),
                Array.Empty<LifecycleRetainedRevision>());
            var legacyOptions = new JsonSerializerOptions(LifecycleCodec.Options)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy, legacyOptions);
            try
            {
                Require(!Encoding.UTF8.GetString(legacyBytes).Contains("intentionDueAt", StringComparison.Ordinal),
                    "The old lifecycle fixture unexpectedly included the new trigger field.");
                var migrated = LifecycleCodec.DecodeStored(legacyBytes);
                Require(migrated.SchemaVersion == LifecycleCodec.CurrentSchemaVersion && migrated.IntentionDueAt is null,
                    "A trigger-free v1 lifecycle record did not migrate in memory to v2 without changing validity.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(legacyBytes);
            }

            var noOffsetRequest = await S7T2SetTriggerAsync(handler, vault, S7T2ControlledId,
                "2026-10-19T12:01:00", cancel: false, expectedRevision: null).ConfigureAwait(false);
            Require(noOffsetRequest.Error == "invalid_request" && noOffsetRequest.Lifecycle is null,
                "The owner schedule operation accepted a timezone-free timestamp or created a lifecycle record for it.");

            var set = await S7T2SetTriggerAsync(handler, vault, S7T2ControlledId, initialDueText,
                cancel: false, expectedRevision: null).ConfigureAwait(false);
            Require(set.Status == "intention_trigger_set" && set.Lifecycle?.IntentionDueAt?.TimeZoneOffsetMinutes == 330,
                "The owner schedule operation did not persist the exact initial trigger and offset (" + set.Error + ").");
            Require(set.Lifecycle?.History.LastOrDefault() is { Aspect: "schedule", Act: "setIntentionDue" },
                "The initial time trigger was not recorded as a separate schedule act.");

            var beforeDue = await S7T2ListAsync(handler, vault).ConfigureAwait(false);
            Require(beforeDue.Status == "intentions_listed" && beforeDue.IntentionSchedule?.Items is [{ IsDue: false, Notified: false, Acknowledged: false, State: "pending" }],
                "The future trigger was not pending and quiet before its exact instant.");
            var beforePass = await S7T2RunPassAsync(handler, vault).ConfigureAwait(false);
            Require(beforePass.IntentionSchedulePass?.NotificationsRecorded == 0,
                "The controlled scheduler notified before the exact due instant.");

            await vault.LockAsync().ConfigureAwait(false);
            clock.Advance(TimeSpan.FromMinutes(2));
            vault.Dispose();
            vault = new VaultLifecycle(store);
            Require(vault.State == "locked", "A controlled service restart restored the encrypted vault unlocked.");
            handler = S7T2CreateHandler(vault, clock);
            var lockedList = await handler.HandleAsync(
                context,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIntentionScheduleListOperation),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            var lockedPass = await handler.HandleAsync(
                context,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIntentionScheduleRunOperation),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(lockedList.IntentionSchedule is null && lockedPass.IntentionSchedulePass is null &&
                    !JsonSerializer.Serialize(lockedList).Contains(S7T2ControlledTitle, StringComparison.Ordinal) &&
                    !JsonSerializer.Serialize(lockedPass).Contains(S7T2ControlledBody, StringComparison.Ordinal),
                "A locked/restarted service queried or disclosed a private intention or due notification.");
            Require(vault.State == "locked" && vault.TryBeginOperation() is null,
                "A due deadline or schedule request automatically unlocked private state.");

            Require(vault.Unlock(passphrase, useRecoveryCode: false) && vault.State == "unlocked",
                "Explicit owner unlock did not reopen the persisted schedule after restart.");
            var catchUp = await S7T2RunPassAsync(handler, vault).ConfigureAwait(false);
            Require(catchUp.IntentionSchedulePass?.NotificationsRecorded == 1,
                "An overdue trigger did not catch up exactly once after explicit unlock.");
            var catchUpList = await S7T2ListAsync(handler, vault).ConfigureAwait(false);
            Require(catchUpList.IntentionSchedule?.Items is [{ IsDue: true, Notified: true, Acknowledged: false, State: "pending", DueAt.Offset: var offset }] &&
                    offset == TimeSpan.FromMinutes(330),
                "The unlocked due view lost its exact offset, pending state or notification marker.");
            var secondPass = await S7T2RunPassAsync(handler, vault).ConfigureAwait(false);
            Require(secondPass.IntentionSchedulePass?.NotificationsRecorded == 0,
                "Repeated due passes emitted a duplicate notification marker.");

            LifecycleRecord acknowledged;
            using (var operation = vault.TryBeginOperation()
                   ?? throw new InvalidOperationException("The reopened controlled vault did not grant an owner operation."))
            {
                var lifecycle = new LifecycleService(clock);
                var current = lifecycle.Read(context, S7T2ControlledId, expectedRevision: null, operation)
                    ?? throw new InvalidOperationException("The due intention lifecycle could not be read after catch-up.");
                acknowledged = lifecycle.Transition(context, S7T2ControlledId, LifecycleAct.Acknowledge,
                    current.Revision, transitionJson: null, operation);
            }
            Require(acknowledged.Intention == IntentionState.Pending && acknowledged.Acknowledged && acknowledged.Notified,
                "Notification or acknowledgement changed a due intention to done.");

            var reschedule = await S7T2SetTriggerAsync(handler, vault, S7T2ControlledId, rescheduledDueText,
                cancel: false, expectedRevision: acknowledged.Revision).ConfigureAwait(false);
            Require(reschedule.Status == "intention_trigger_set" && reschedule.Lifecycle?.ValidFrom is null,
                "Editing a reminder changed the separate temporal-validity fields.");
            var rescheduled = await S7T2ListAsync(handler, vault).ConfigureAwait(false);
            Require(rescheduled.IntentionSchedule?.Items is [{ IsDue: false, Notified: false, Acknowledged: false, State: "pending", DueAt.Offset: var rescheduledOffset }] &&
                    rescheduledOffset == TimeSpan.FromHours(-4),
                "Rescheduling did not preserve its exact offset or reset only reminder markers.");

            var cancelTrigger = await S7T2SetTriggerAsync(handler, vault, S7T2ControlledId, dueAt: null,
                cancel: true, expectedRevision: reschedule.Lifecycle!.Revision).ConfigureAwait(false);
            Require(cancelTrigger.Status == "intention_trigger_cleared" && cancelTrigger.Lifecycle?.IntentionDueAt is null,
                "The explicit trigger cancellation did not clear only the scheduled instant.");
            var afterCancel = await S7T2ListAsync(handler, vault).ConfigureAwait(false);
            Require(afterCancel.IntentionSchedule?.Items is { Length: 0 } &&
                    cancelTrigger.Lifecycle?.Intention == "pending",
                "Cancelling a time trigger removed or completed its intention.");

            var dueAgain = clock.GetUtcNow().AddSeconds(-1).ToString("O", CultureInfo.InvariantCulture);
            var freshTrigger = await S7T2SetTriggerAsync(handler, vault, S7T2ControlledId, dueAgain,
                cancel: false, expectedRevision: cancelTrigger.Lifecycle!.Revision).ConfigureAwait(false);
            Require(freshTrigger.Status == "intention_trigger_set", "The explicitly cancelled trigger could not be re-enabled.");
            var finalPass = await S7T2RunPassAsync(handler, vault).ConfigureAwait(false);
            Require(finalPass.IntentionSchedulePass?.NotificationsRecorded == 1,
                "The newly enabled past trigger did not receive one fresh notification marker.");
            var completionPayload = LifecycleCodec.EncodeTransitionDraft(new LifecycleTransitionDraft(
                Rationale: "Synthetic explicit completion after acknowledgement review.",
                ReplacementResourceId: null,
                Occurred: null,
                Recorded: null,
                ValidFrom: null,
                ValidUntil: null,
                ReviewAfter: null));
            try
            {
                using var operation = vault.TryBeginOperation()
                    ?? throw new InvalidOperationException("The controlled vault locked before explicit completion.");
                var lifecycle = new LifecycleService(clock);
                var current = lifecycle.Read(context, S7T2ControlledId, expectedRevision: null, operation)
                    ?? throw new InvalidOperationException("The final due intention lifecycle could not be read.");
                var completed = lifecycle.Transition(context, S7T2ControlledId, LifecycleAct.CompleteIntention,
                    current.Revision, completionPayload, operation);
                Require(completed.Intention == IntentionState.Done,
                    "The separate explicit completion action did not set the done state.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(completionPayload);
            }

            VerifyS7T2EncryptedPrivateState(fixture, passphrase, recoveryCode!, initialDueText, rescheduledDueText, dueAgain);
            Console.WriteLine("PASS strict timestamp policy rejects date-only/no-offset/ambiguous wall-clock/invalid-offset inputs and retains explicit offset minutes");
            Console.WriteLine("PASS lock plus offline/restart crosses a controlled deadline without private queries or auto-unlock; unlock catches up exactly once, acknowledgement stays pending, reschedule clears markers, trigger cancel keeps intention pending, explicit complete marks done");
            Console.WriteLine("PASS encrypted resource scan contains no synthetic title/body, due timestamp, passphrase or recovery code in plaintext");
        }
        finally
        {
            vault.Dispose();
        }
    }

    private static async Task RunS7T2OwnerUiAsync(string connectionsExecutable)
    {
        using var fixture = SmokeFixture.CreateUninitialized();
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        RunningService? service = null;
        try
        {
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "The actual S7-T2 named-pipe service did not provision the synthetic vault.");
            var unlocked = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlocked.ExitCode == 0 && unlocked.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The actual S7-T2 named-pipe service did not unlock the synthetic vault.");

            var now = DateTimeOffset.UtcNow;
            await WriteS7T2IntentionAsync(connectionsExecutable, fixture, S7T2UiAcknowledgeId,
                S7T2UiAcknowledgeTitle, S7T2UiAcknowledgeBody, now).ConfigureAwait(false);
            await WriteS7T2IntentionAsync(connectionsExecutable, fixture, S7T2UiCompleteId,
                S7T2UiCompleteTitle, S7T2UiCompleteBody, now).ConfigureAwait(false);
            await WriteS7T2IntentionAsync(connectionsExecutable, fixture, S7T2UiLockedId,
                S7T2UiLockedTitle, S7T2UiLockedBody, now).ConfigureAwait(false);

            DriveS7T2Wpf(fixture, connectionsExecutable);
            var evidenceDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "sprint-7", "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            var screenshot = Path.Combine(evidenceDirectory, "s7t2-wpf-due-list.png");
            var rendered = Path.Combine(fixture.OutputDirectory, "s7t2-wpf-due-list.png");
            Require(File.Exists(rendered), "The actual shown WPF due-list screenshot was not rendered.");
            File.Copy(rendered, screenshot, overwrite: true);
            Console.WriteLine("S7T2 WPF visual evidence: " + screenshot);

            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
            var stopped = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(stopped.Availability == OwnerChannelAvailability.ServiceUnavailable,
                "The locked S7-T2 service remained available after stop.");
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var restarted = await OwnerChannelClient.ReadStatusAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(restarted.Availability == OwnerChannelAvailability.Reachable && restarted.VaultState == OwnerVaultState.Locked,
                "The actual intention service restart did not return locked.");
            var lockedList = await OwnerChannelClient.ListScheduledIntentionsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(lockedList.Outcome != OwnerCommandOutcome.Completed && lockedList.Intentions is null,
                "The locked restarted service disclosed a private schedule.");

            var explicitUnlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(explicitUnlock.ExitCode == 0 && explicitUnlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "Only the explicit owner unlock should reopen the restarted vault.");
            var persisted = await OwnerChannelClient.ListScheduledIntentionsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            Require(persisted.Outcome == OwnerCommandOutcome.Completed &&
                    persisted.Intentions?.SingleOrDefault(item => item.ResourceId == S7T2UiLockedId) is
                        { IsDue: true, Notified: true, State: "pending" },
                "The due notification did not survive lock/restart and explicit unlock as a pending item.");
            var cancelled = await OwnerChannelClient.SetIntentionTimeTriggerAsync(
                fixture.ControlPipe,
                S7T2UiLockedId,
                dueAt: null,
                cancel: true,
                expectedLifecycleRevision: persisted.Intentions!.Single(item => item.ResourceId == S7T2UiLockedId).LifecycleRevision,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            Require(cancelled.Outcome == OwnerCommandOutcome.Completed &&
                    cancelled.Lifecycle?.SubjectResourceId == S7T2UiLockedId,
                "The persisted post-restart trigger could not be cancelled through the real owner client.");
            var afterRestartCancel = await OwnerChannelClient.ListScheduledIntentionsAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(false);
            var lifecycleAfterRestartCancel = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T2UiLockedId).ConfigureAwait(false);
            Require(afterRestartCancel.Outcome == OwnerCommandOutcome.Completed && afterRestartCancel.Intentions is { Length: 0 } &&
                    lifecycleAfterRestartCancel.ExitCode == 0 &&
                    lifecycleAfterRestartCancel.StandardOutput.Contains("intention=pending", StringComparison.Ordinal),
                "Cancelling the persisted post-restart trigger completed the intention or left it scheduled.");
            await StopServiceAsync(service).ConfigureAwait(false);
            service = null;
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteS7T2IntentionAsync(
        string executable,
        SmokeFixture fixture,
        string resourceId,
        string title,
        string body,
        DateTimeOffset recordedAt)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s7t2-intention-" + resourceId + ".json");
        await File.WriteAllTextAsync(input, S7T2IntentionDraft(title, body, recordedAt), Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", S7T2Zone,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains("MEMORY stored revision=1", StringComparison.Ordinal),
            "The synthetic S7-T2 intention was not stored (" + result.StandardError.Trim() + ").");
    }

    private static string S7T2IntentionDraft(string title, string body, DateTimeOffset recordedAt) => JsonSerializer.Serialize(new
    {
        kind = "intention",
        author = "user",
        creationMethod = "directWrite",
        evidence = "unverified",
        level = "l1",
        title,
        body,
        sources = Array.Empty<object>(),
        occurredAtUtc = (DateTimeOffset?)null,
        recordedAtUtc = recordedAt,
        applicability = (object?)null
    });

    private static void DriveS7T2Wpf(SmokeFixture fixture, string connectionsExecutable)
    {
        var session = new OwnerConsoleSession(fixture.ControlPipe);
        session.AttachAsync().GetAwaiter().GetResult();
        Require(session.ReportedVaultState == OwnerVaultState.Unlocked,
            "The shown S7-T2 owner session did not attach to the actual unlocked service.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OwnerConsoleWindow? window = null;
            try
            {
                var activeWindow = new OwnerConsoleWindow(session)
                {
                    Width = 1360,
                    Height = 1100
                };
                window = activeWindow;
                activeWindow.Show();
                activeWindow.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await S7T2WpfInteractionsAsync(activeWindow, session, fixture, connectionsExecutable).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        await WaitForAsync(() => !session.IntentionActInFlight, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                        await Task.Delay(200).ConfigureAwait(true);
                        activeWindow.DetachView();
                        activeWindow.Close();
                        activeWindow.Dispatcher.InvokeShutdown();
                    }
                });
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure ??= ex;
                try
                {
                    window?.DetachView();
                    window?.Close();
                }
                catch
                {
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The shown S7-T2 WPF intention surface failed: " + failure.Message, failure);
        }
    }

    private static async Task S7T2WpfInteractionsAsync(
        OwnerConsoleWindow window,
        OwnerConsoleSession session,
        SmokeFixture fixture,
        string connectionsExecutable)
    {
        SelectS7T2Navigation(window, "Intentions");
        Require(window.Navigation.SelectedItem is ListBoxItem selected &&
                string.Equals(selected.Content as string, "Intentions", StringComparison.Ordinal),
            "The actual WPF console did not navigate to Intentions.");
        Require(await WaitForAsync(() => !session.IntentionActInFlight, TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The actual WPF intention list did not finish its initial owner refresh.");
        await session.RefreshIntentionsAsync().ConfigureAwait(true);

        var now = DateTimeOffset.UtcNow;
        var ackDue = now.AddMinutes(-2).ToOffset(TimeSpan.FromHours(-4)).ToString("O", CultureInfo.InvariantCulture);
        await SetS7T2WpfTriggerAsync(window, session, S7T2UiAcknowledgeId, ackDue).ConfigureAwait(true);
        var dueAck = session.ScheduledIntentions.Single(item => item.ResourceId == S7T2UiAcknowledgeId);
        Require(dueAck.IsDue && dueAck.DueAt.Offset == TimeSpan.FromHours(-4) && dueAck.State == "pending",
            "The WPF due list did not show the exact past trigger with its explicit offset and pending state.");
        var ackPass = await OwnerChannelClient.RunIntentionSchedulePassAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(true);
        Require(ackPass.Outcome == OwnerCommandOutcome.Completed, "The real owner schedule pass did not run from the shown UI scenario.");
        await session.RefreshIntentionsAsync().ConfigureAwait(true);
        Require(session.ScheduledIntentions.Single(item => item.ResourceId == S7T2UiAcknowledgeId).Notified,
            "The real service schedule pass did not persist the one-shot notification marker.");
        var repeatPass = await OwnerChannelClient.RunIntentionSchedulePassAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(true);
        Require(repeatPass.Outcome == OwnerCommandOutcome.Completed && repeatPass.NotificationsRecorded == 0,
            "A repeated service pass wrote a duplicate due notification.");

        SelectS7T2WpfIntention(window, session, S7T2UiAcknowledgeId);
        Require(window.IntentionAcknowledgeButton.IsEnabled,
            "The WPF view did not enable acknowledgement for the selected due item.");
        window.IntentionAcknowledgeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.SingleOrDefault(item => item.ResourceId == S7T2UiAcknowledgeId)?.Acknowledged == true,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The WPF acknowledgement act did not reach the real service.");
        var acknowledged = session.ScheduledIntentions.Single(item => item.ResourceId == S7T2UiAcknowledgeId);
        Require(acknowledged.State == "pending" && acknowledged.Acknowledged,
            "WPF notification acknowledgement incorrectly completed the intention.");

        var future = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromMinutes(330)).ToString("O", CultureInfo.InvariantCulture);
        window.IntentionDueAtInput.Text = future;
        Require(window.IntentionTriggerSetButton.IsEnabled, "The WPF edit control did not enable for a valid explicit-offset timestamp.");
        window.IntentionTriggerSetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.SingleOrDefault(item => item.ResourceId == S7T2UiAcknowledgeId) is
                { IsDue: false, Notified: false, Acknowledged: false } item && item.DueAt.Offset == TimeSpan.FromMinutes(330),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The WPF reschedule did not persist the new offset or reset only acknowledgement/notification markers.");
        SelectS7T2WpfIntention(window, session, S7T2UiAcknowledgeId);
        window.IntentionTriggerCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.All(item => item.ResourceId != S7T2UiAcknowledgeId),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The WPF cancel-trigger control did not clear the selected schedule.");
        var pendingAfterTriggerCancel = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T2UiAcknowledgeId).ConfigureAwait(true);
        Require(pendingAfterTriggerCancel.ExitCode == 0 && pendingAfterTriggerCancel.StandardOutput.Contains("intention=pending", StringComparison.Ordinal),
            "Cancelling the trigger from WPF cancelled or completed the intention itself.");

        var completeDue = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        await SetS7T2WpfTriggerAsync(window, session, S7T2UiCompleteId, completeDue).ConfigureAwait(true);
        var completePass = await OwnerChannelClient.RunIntentionSchedulePassAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(true);
        Require(completePass.Outcome == OwnerCommandOutcome.Completed, "The real service could not evaluate the completion scenario's due trigger.");
        await session.RefreshIntentionsAsync().ConfigureAwait(true);
        SelectS7T2WpfIntention(window, session, S7T2UiCompleteId);
        window.IntentionRationaleInput.Text = "Synthetic explicit completion after seeing the due reminder.";
        Require(window.IntentionCompleteButton.IsEnabled, "Typing a completion rationale did not enable the explicit completion control.");
        window.IntentionAcknowledgeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.SingleOrDefault(item => item.ResourceId == S7T2UiCompleteId)?.Acknowledged == true,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true), "The completion scenario's acknowledgement did not remain a separate owner act.");
        Require(session.ScheduledIntentions.Single(item => item.ResourceId == S7T2UiCompleteId).State == "pending",
            "Acknowledging the second due intention changed its state to done.");
        window.IntentionRationaleInput.Text = "Synthetic explicit completion after seeing the due reminder.";
        window.IntentionCompleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.All(item => item.ResourceId != S7T2UiCompleteId),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The explicit WPF completion did not remove the completed item from the pending due list.");
        var completedRead = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "lifecycle-read", "--control-pipe", fixture.ControlPipe, "--resource-id", S7T2UiCompleteId).ConfigureAwait(true);
        Require(completedRead.ExitCode == 0 && completedRead.StandardOutput.Contains("intention=done", StringComparison.Ordinal),
            "The separate WPF completion action did not persist done state.");

        var lockedDue = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        await SetS7T2WpfTriggerAsync(window, session, S7T2UiLockedId, lockedDue).ConfigureAwait(true);
        var lockedPass = await OwnerChannelClient.RunIntentionSchedulePassAsync(fixture.ControlPipe, CancellationToken.None).ConfigureAwait(true);
        Require(lockedPass.Outcome == OwnerCommandOutcome.Completed, "The real schedule service did not mark the locked-privacy scenario due.");
        await session.RefreshIntentionsAsync().ConfigureAwait(true);
        SelectS7T2WpfIntention(window, session, S7T2UiLockedId);
        Require(session.ScheduledIntentions.Single(item => item.ResourceId == S7T2UiLockedId).IsDue,
            "The due item was not displayed in the actual WPF list before locking.");
        SaveS7T2Wpf(window, fixture, "s7t2-wpf-due-list.png", window.IntentionsList);

        var lockResult = await RunOwnerCommandAsync(connectionsExecutable,
            "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(true);
        Require(lockResult.ExitCode == 0 && lockResult.StandardOutput.Contains("VAULT locked", StringComparison.Ordinal),
            "The actual owner vault lock did not complete during the WPF privacy check.");
        await session.CheckAsync().ConfigureAwait(true);
        Require(session.ReportedVaultState == OwnerVaultState.Locked && session.ScheduledIntentions.Length == 0 &&
                window.IntentionsList.Items.Count == 0 && window.IntentionResourceIdInput.Text.Length == 0 &&
                window.IntentionDueAtInput.Text.Length == 0 && window.IntentionRationaleInput.Text.Length == 0 &&
                !window.IntentionsMessageText.Text.Contains(S7T2UiLockedTitle, StringComparison.Ordinal) &&
                !window.IntentionsMessageText.Text.Contains(S7T2UiLockedBody, StringComparison.Ordinal),
            "Lock did not clear the actual WPF due list and private intention input/state.");
        Console.WriteLine("PASS WPF due-list interactions persisted explicit create/edit/cancel, one-shot notification, acknowledge-without-complete and explicit complete; lock cleared the actual private view and inputs");
    }

    private static async Task SetS7T2WpfTriggerAsync(OwnerConsoleWindow window, OwnerConsoleSession session, string resourceId, string dueAt)
    {
        window.IntentionResourceIdInput.Text = resourceId;
        window.IntentionDueAtInput.Text = dueAt;
        Require(window.IntentionTriggerSetButton.IsEnabled,
            "The WPF trigger control did not accept the explicit timestamp for " + resourceId + ".");
        window.IntentionTriggerSetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await WaitForAsync(() => session.ScheduledIntentions.Any(item => item.ResourceId == resourceId), TimeSpan.FromSeconds(30)).ConfigureAwait(true),
            "The WPF trigger-create/edit operation did not return a schedule row for " + resourceId + ".");
    }

    private static void SelectS7T2WpfIntention(OwnerConsoleWindow window, OwnerConsoleSession session, string resourceId)
    {
        var index = Array.FindIndex(session.ScheduledIntentions, item => string.Equals(item.ResourceId, resourceId, StringComparison.Ordinal));
        Require(index >= 0 && index < window.IntentionsList.Items.Count,
            "The actual WPF list did not render the selected intention " + resourceId + ".");
        window.IntentionsList.SelectedIndex = index;
        window.IntentionsList.ScrollIntoView(window.IntentionsList.SelectedItem);
    }

    private static void SelectS7T2Navigation(OwnerConsoleWindow window, string entry)
    {
        foreach (var item in window.Navigation.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Content as string, entry, StringComparison.Ordinal))
            {
                window.Navigation.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("The owner console has no '" + entry + "' navigation entry.");
    }

    private static void SaveS7T2Wpf(OwnerConsoleWindow window, SmokeFixture fixture, string fileName, FrameworkElement? scrollTo)
    {
        window.UpdateLayout();
        scrollTo?.BringIntoView();
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Open(Path.Combine(fixture.OutputDirectory, fileName), FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static ServiceRequestHandler S7T2CreateHandler(VaultLifecycle vault, TimeProvider clock) =>
        new(new GrantAuthority(new InMemoryGrantStore()), vault, new OwnerScopeGrantAuthority(), timeProvider: clock);

    private static async Task<ServiceReply> S7T2SetTriggerAsync(
        ServiceRequestHandler handler,
        VaultLifecycle vault,
        string resourceId,
        string? dueAt,
        bool cancel,
        long? expectedRevision)
    {
        var draft = JsonSerializer.SerializeToUtf8Bytes(new IntentionTimeTriggerDraft(dueAt, cancel, expectedRevision));
        try
        {
            return await S7T2SendAsync(handler, vault, new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerIntentionTriggerSetOperation,
                ResourceId: resourceId,
                ContentBase64: Convert.ToBase64String(draft))).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    private static Task<ServiceReply> S7T2ListAsync(ServiceRequestHandler handler, VaultLifecycle vault) =>
        S7T2SendAsync(handler, vault, new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerIntentionScheduleListOperation));

    private static Task<ServiceReply> S7T2RunPassAsync(ServiceRequestHandler handler, VaultLifecycle vault) =>
        S7T2SendAsync(handler, vault, new ServiceRequest(
            ServiceRequestHandler.CurrentProtocolVersion,
            ServiceRequestHandler.OwnerIntentionScheduleRunOperation));

    private static async Task<ServiceReply> S7T2SendAsync(ServiceRequestHandler handler, VaultLifecycle vault, ServiceRequest request)
    {
        using var operation = vault.TryBeginOperation();
        return await handler.HandleAsync(
            AuthenticatedContext.ForOwner(), request, operation, CancellationToken.None).ConfigureAwait(false);
    }

    private static void VerifyS7T2EncryptedPrivateState(
        SmokeFixture fixture,
        string passphrase,
        string recoveryCode,
        string initialDueText,
        string rescheduledDueText,
        string dueAgainText)
    {
        var normalizedUtcStamps = new[] { initialDueText, rescheduledDueText, dueAgainText }
            .Select(value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .ToArray();
        var prohibitedPlaintext = new[]
        {
            S7T2ControlledTitle,
            S7T2ControlledBody,
            initialDueText,
            rescheduledDueText,
            dueAgainText,
            passphrase,
            recoveryCode
        }.Concat(normalizedUtcStamps).ToArray();
        var paths = Directory.EnumerateFiles(fixture.VaultDirectoryPath, "*", SearchOption.AllDirectories).ToArray();
        Require(paths.Length > 0 && paths.All(path => !path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)),
            "The encrypted fixture did not persist a vault or created a plaintext key file.");
        foreach (var path in paths)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                var text = Encoding.UTF8.GetString(bytes);
                foreach (var marker in prohibitedPlaintext)
                {
                    Require(!text.Contains(marker, StringComparison.Ordinal),
                        "The encrypted vault file persisted private intention metadata/key material in plaintext: " + Path.GetFileName(path));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private sealed class S7T2ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
