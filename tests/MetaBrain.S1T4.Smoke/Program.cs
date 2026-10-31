using System.Globalization;

namespace MetaBrain.S1T4.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 5 && string.Equals(args[0], "s11-t2-recovery-crash-worker", StringComparison.Ordinal))
        {
            await OwnerServiceSmoke.RunS11T2RecoveryCrashWorkerAsync(
                args[1], args[2], args[3], args[4]).ConfigureAwait(false);
            return 0;
        }
        if (args.Length == 5 && string.Equals(args[0], "s11-t3-ingestion-crash-worker", StringComparison.Ordinal))
        {
            await OwnerServiceSmoke.RunS11T3IngestionCrashWorkerAsync(
                args[1], args[2], args[3], args[4]).ConfigureAwait(false);
            return 0;
        }
        var scenario = args.Length == 2 &&
            (string.Equals(args[0], "scope-issue", StringComparison.Ordinal) ||
             string.Equals(args[0], "agent-session", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t6", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t7", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t8", StringComparison.Ordinal) ||
             string.Equals(args[0], "s1-t9", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t1", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t2", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t3", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t4", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t6", StringComparison.Ordinal) ||
             string.Equals(args[0], "s2-t7", StringComparison.Ordinal) ||
             string.Equals(args[0], "s3-t1", StringComparison.Ordinal) ||
             string.Equals(args[0], "s3-t2", StringComparison.Ordinal) ||
             string.Equals(args[0], "s3-t3", StringComparison.Ordinal) ||
             string.Equals(args[0], "s3-t4", StringComparison.Ordinal) ||
             string.Equals(args[0], "s3-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s3-t6", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t1", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t2", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t3", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t4", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t6", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t7", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t7-client-fixture", StringComparison.Ordinal) ||
             string.Equals(args[0], "s4-t6-client-fixture", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t1", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t2", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t3", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t4", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t6", StringComparison.Ordinal) ||
             string.Equals(args[0], "s5-t7", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t1", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t1-live", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t2", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t2-live", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t4", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t5", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t6", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t6-live", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t7-agent-read", StringComparison.Ordinal) ||
             string.Equals(args[0], "s6-t7", StringComparison.Ordinal) ||
                string.Equals(args[0], "s7-t1", StringComparison.Ordinal) ||
                string.Equals(args[0], "s7-t2", StringComparison.Ordinal) ||
                string.Equals(args[0], "s7-t3", StringComparison.Ordinal) ||
                string.Equals(args[0], "s7-t4", StringComparison.Ordinal) ||
                string.Equals(args[0], "s7-t5", StringComparison.Ordinal) ||
                string.Equals(args[0], "s7-t6", StringComparison.Ordinal) ||
                string.Equals(args[0], "s8-t2", StringComparison.Ordinal) ||
                string.Equals(args[0], "s8-t2-ui", StringComparison.Ordinal) ||
                string.Equals(args[0], "s8-t4", StringComparison.Ordinal) ||
                string.Equals(args[0], "s8-t4-ui", StringComparison.Ordinal) ||
                string.Equals(args[0], "s8-t6-authorize-dispatch", StringComparison.Ordinal) ||
                string.Equals(args[0], "s11-t2", StringComparison.Ordinal) ||
                string.Equals(args[0], "s11-t3", StringComparison.Ordinal) ||
                string.Equals(args[0], "s11-t4", StringComparison.Ordinal))
            ? args[0]
                : args.Length == 5 &&
                  string.Equals(args[0], "s8-t5", StringComparison.Ordinal)
                ? args[0]
                : args.Length == 4 &&
                  string.Equals(args[0], "s8-t5-ui", StringComparison.Ordinal)
                ? args[0]
                : args.Length == 2 &&
                  string.Equals(args[0], "s8-t5-relevance", StringComparison.Ordinal)
                ? args[0]
                : args.Length == 1 &&
                  string.Equals(args[0], "s8-t5-index", StringComparison.Ordinal)
                ? args[0]
                : args.Length == 3 &&
                  string.Equals(args[0], "s8-t5-lifecycle", StringComparison.Ordinal)
                ? args[0]
                : args.Length == 3 &&
              (string.Equals(args[0], "s5-t1-console-fixture", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t2-console-fixture", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t3-console-fixture", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t3-console-corpus", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t4-console-fixture", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t4-console-inbox", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t4-console-proposals", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t6-console-corpus", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t7-console-fixture", StringComparison.Ordinal) ||
               string.Equals(args[0], "s5-t7-console-corpus", StringComparison.Ordinal) ||
               string.Equals(args[0], "s8-t1a", StringComparison.Ordinal) ||
               string.Equals(args[0], "s8-t3", StringComparison.Ordinal) ||
               string.Equals(args[0], "s8-t3-ui", StringComparison.Ordinal) ||
               string.Equals(args[0], "s8-t1", StringComparison.Ordinal))
                ? args[0]
                : null;
        var executable = scenario is not null ? (args.Length > 1 ? args[1] : null) : args.Length == 1 ? args[0] : null;
        // The installed lifecycle smoke takes the published payload directory
        // instead of a single executable.
        var isPayloadScenario = string.Equals(scenario, "s8-t1", StringComparison.Ordinal);
        // The private index contract scenario runs the engine in process and
        // needs no service binary at all. The lifecycle phases are the same
        // in-process engine and handler, run in two processes by the harness.
        var isInProcessScenario = string.Equals(scenario, "s8-t5-index", StringComparison.Ordinal) ||
            string.Equals(scenario, "s8-t5-lifecycle", StringComparison.Ordinal);
        var payloadExists = isInProcessScenario
            ? true
            : isPayloadScenario
            ? executable is not null && Directory.Exists(executable)
            : executable is not null && File.Exists(executable);
        if (!payloadExists)
        {
            Console.Error.WriteLine("Usage: MetaBrain.S1T4.Smoke <MetaBrain.Connections.exe> | scope-issue|agent-session|s1-t5|s1-t6|s1-t7|s1-t8|s1-t9|s2-t1|s2-t2|s2-t3|s2-t4|s2-t5|s2-t6|s2-t7|s3-t1|s3-t2|s3-t3|s3-t4|s3-t5|s3-t6|s4-t1|s4-t2|s4-t3|s4-t4|s4-t5|s4-t6|s4-t7|s4-t7-client-fixture|s4-t6-client-fixture|s5-t1 <MetaBrain.Connections.exe> | s5-t2 <MetaBrain.Connections.exe> | s5-t3 <MetaBrain.Connections.exe> | s5-t4 <MetaBrain.Connections.exe> | s5-t5 <MetaBrain.Connections.exe> | s5-t5-console-fixture|s5-t5-console-corpus| s5-t1-console-fixture|s5-t2-console-fixture|s5-t3-console-fixture|s5-t3-console-corpus|s5-t4-console-fixture|s5-t4-console-inbox|s5-t4-console-proposals <MetaBrain.Connections.exe> <fixture-root> | s6-t7|s6-t7-agent-read <MetaBrain.Connections.exe>");
            Console.Error.WriteLine("S7-T3 invocation: MetaBrain.S1T4.Smoke.exe s7-t3 <MetaBrain.Connections.exe>");
            Console.Error.WriteLine("S8-T1 invocation: MetaBrain.S1T4.Smoke.exe s8-t1 <published-payload-dir> <fixture-root>");
            Console.Error.WriteLine("S8-T3 invocation: MetaBrain.S1T4.Smoke.exe s8-t3 <MetaBrain.Connections.exe> <old-format-artifact-root>");
            Console.Error.WriteLine("S8-T3 console invocation: MetaBrain.S1T4.Smoke.exe s8-t3-ui <MetaBrain.Connections.exe> <old-format-artifact-root>");
            Console.Error.WriteLine("S8-T5 in-process index invocation: MetaBrain.S1T4.Smoke.exe s8-t5-index");
            Console.Error.WriteLine("S8-T5 lifecycle invocation: MetaBrain.S1T4.Smoke.exe s8-t5-lifecycle build|reopen <fixture-root>");
            Console.Error.WriteLine("S8-T6 authorization dispatch invocation: MetaBrain.S1T4.Smoke.exe s8-t6-authorize-dispatch <MetaBrain.Connections.exe>");
            Console.Error.WriteLine("S11-T2 invocation: MetaBrain.S1T4.Smoke.exe s11-t2 <MetaBrain.Connections.exe>");
            Console.Error.WriteLine("S11-T3 invocation: MetaBrain.S1T4.Smoke.exe s11-t3 <MetaBrain.Connections.exe>");
            Console.Error.WriteLine("S11-T4 invocation: MetaBrain.S1T4.Smoke.exe s11-t4 <MetaBrain.Connections.exe>");
            return 2;
        }

        try
        {
            var fullPath = executable is null ? string.Empty : Path.GetFullPath(executable);
            switch (scenario)
            {
                case "scope-issue":
                    await OwnerServiceSmoke.RunScopeIssueAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t5":
                    await OwnerServiceSmoke.RunT5SessionLifecycleAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t6":
                    await OwnerServiceSmoke.RunT6ScopedReadAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t7":
                    await OwnerServiceSmoke.RunT7CatalogAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t8":
                    await OwnerServiceSmoke.RunT8AccessRequestAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s1-t9":
                    await OwnerServiceSmoke.RunT9BridgeAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t1":
                    await OwnerServiceSmoke.RunS2T1DomainEnvelopeAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t2":
                    await OwnerServiceSmoke.RunS2T2CanonicalDurabilityAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t3":
                    await OwnerServiceSmoke.RunS2T3SourceRegistryAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t4":
                    await OwnerServiceSmoke.RunS2T4RelationStoreAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t5":
                    await OwnerServiceSmoke.RunS2T5LifecycleAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t6":
                    await OwnerServiceSmoke.RunS2T6CollectionsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s2-t7":
                    await OwnerServiceSmoke.RunS2T7PrivateIndexAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s3-t1":
                    await OwnerServiceSmoke.RunS3T1SourceIngestionAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s3-t2":
                    await OwnerServiceSmoke.RunS3T2CodexAdapterAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s3-t3":
                    await OwnerServiceSmoke.RunS3T3OmpAdapterAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s3-t4":
                    await OwnerServiceSmoke.RunS3T4ResearchCaptureAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s3-t5":
                    await OwnerServiceSmoke.RunS3T5PreservationAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s3-t6":
                    await OwnerServiceSmoke.RunS3T6OperationsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s11-t2":
                    await OwnerServiceSmoke.RunS11T2FolderEnrollmentAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s11-t3":
                    await OwnerServiceSmoke.RunS11T3RawPreservationAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s11-t4":
                    await OwnerServiceSmoke.RunS11T4FolderRefreshAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t2":
                    await OwnerServiceSmoke.RunS4T2SearchAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t3":
                    await OwnerServiceSmoke.RunS4T3DetailAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t4":
                    await OwnerServiceSmoke.RunS4T4BriefAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t5":
                    await OwnerServiceSmoke.RunS4T5ProposalAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t6":
                    await OwnerServiceSmoke.RunS4T6RelationsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t7":
                    await OwnerServiceSmoke.RunS4T7WorkflowAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t7-client-fixture":
                    await OwnerServiceSmoke.RunS4T7OfficialClientFixtureAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t6-client-fixture":
                    await OwnerServiceSmoke.RunS4T6OfficialClientFixtureAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s4-t1":
                    await OwnerServiceSmoke.RunS4T1BridgeAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t1":
                    await OwnerServiceSmoke.RunS5T1OwnerUiAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t2":
                    await OwnerServiceSmoke.RunS5T2VaultKeyAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t3":
                    await OwnerServiceSmoke.RunS5T3LibraryAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t4":
                    await OwnerServiceSmoke.RunS5T4CaptureReviewAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t5":
                    await OwnerServiceSmoke.RunS5T5RelationsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t6":
                    await OwnerServiceSmoke.RunS5T6SharingAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t7":
                    await OwnerServiceSmoke.RunS5T7SourcesAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s5-t1-console-fixture":
                    await OwnerServiceSmoke.RunS5T1OwnerUiFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t2-console-fixture":
                    await OwnerServiceSmoke.RunS5T2VaultKeyFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t3-console-fixture":
                    await OwnerServiceSmoke.RunS5T3LibraryFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t3-console-corpus":
                    await OwnerServiceSmoke.S5T3WriteConsoleCorpusAsync(fullPath, Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t4-console-fixture":
                    await OwnerServiceSmoke.RunS5T4InboxFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t4-console-inbox":
                    await OwnerServiceSmoke.S5T4WriteConsoleInboxAsync(fullPath, Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t4-console-proposals":
                    await OwnerServiceSmoke.S5T4WriteConsoleProposalsAsync(fullPath, Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t5-console-fixture":
                    await OwnerServiceSmoke.RunS5T5ConsoleFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t5-console-corpus":
                    await OwnerServiceSmoke.S5T5WriteConsoleCorpusAsync(fullPath, Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t6-console-fixture":
                    await OwnerServiceSmoke.RunS5T6ConsoleFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t6-console-corpus":
                    await OwnerServiceSmoke.S5T6WriteConsoleCorpusAsync(fullPath, Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t7-console-fixture":
                    await OwnerServiceSmoke.RunS5T7ConsoleFixtureAsync(Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s5-t7-console-corpus":
                    await OwnerServiceSmoke.S5T7WriteConsoleCorpusAsync(fullPath, Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "agent-session":
                    await OwnerServiceSmoke.RunAgentSessionAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t1":
                    await OwnerServiceSmoke.RunS6T1ModelJobsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t1-live":
                    await OwnerServiceSmoke.RunS6T1LiveProviderAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t2":
                    await OwnerServiceSmoke.RunS6T2CodingSessionAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t2-live":
                    await OwnerServiceSmoke.RunS6T2LiveCodingSessionAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t4":
                    await OwnerServiceSmoke.RunS6T4SynthesisAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t5":
                    await OwnerServiceSmoke.RunS6T5DeclassifyAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t6":
                    await OwnerServiceSmoke.RunS6T6WorkflowAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t6-live":
                    await OwnerServiceSmoke.RunS6T6LiveProviderAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t7":
                    await OwnerServiceSmoke.RunS6T7BoundaryAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s6-t7-agent-read":
                    await OwnerServiceSmoke.RunS6T7OrdinaryClientReadAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s7-t1":
                    await OwnerServiceSmoke.RunS7T1TemporalAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s7-t2":
                    await OwnerServiceSmoke.RunS7T2IntentionScheduleAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s7-t3":
                    await OwnerServiceSmoke.RunS7T3ContextualIntentionsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s7-t4":
                    await OwnerServiceSmoke.RunS7T4ForgettingAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s7-t5":
                    await OwnerServiceSmoke.RunS7T5PurgeAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s7-t6":
                    await OwnerServiceSmoke.RunS7T6ReviewAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s8-t1":
                    await S8T1InstallLifecycleSmoke.RunAsync(fullPath, Path.GetFullPath(args[2]!)).ConfigureAwait(false);
                    break;
                case "s8-t1a":
                    await OwnerServiceSmoke.RunS8T1AUsabilityAsync(fullPath, Path.GetFullPath(args[2]!)).ConfigureAwait(false);
                    break;
                case "s8-t2":
                    await OwnerServiceSmoke.RunS8T2BackupRestoreAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s8-t2-ui":
                    await OwnerServiceSmoke.RunS8T2BackupUiAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s8-t3":
                    await OwnerServiceSmoke.RunS8T3SchemaUpgradeAsync(fullPath, Path.GetFullPath(args[2]!)).ConfigureAwait(false);
                    break;
                case "s8-t3-ui":
                    await OwnerServiceSmoke.RunS8T3UiAsync(fullPath, Path.GetFullPath(args[2]!)).ConfigureAwait(false);
                    break;
                case "s8-t4":
                    await OwnerServiceSmoke.RunS8T4OperationsAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s8-t4-ui":
                    await OwnerServiceSmoke.RunS8T4OperationsUiAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s8-t5":
                    await OwnerServiceSmoke.RunS8T5EnvelopeAsync(
                        fullPath,
                        int.Parse(args[2], CultureInfo.InvariantCulture),
                        int.Parse(args[3], CultureInfo.InvariantCulture),
                        int.Parse(args[4], CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;
                case "s8-t5-ui":
                    await OwnerServiceSmoke.RunS8T5UiAsync(
                        fullPath,
                        int.Parse(args[2], CultureInfo.InvariantCulture),
                        int.Parse(args[3], CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;
                case "s8-t5-relevance":
                    await OwnerServiceSmoke.RunS8T5RelevanceAsync(fullPath).ConfigureAwait(false);
                    break;
                case "s8-t5-index":
                    await OwnerServiceSmoke.RunS8T5PrivateIndexAsync().ConfigureAwait(false);
                    break;
                case "s8-t5-lifecycle":
                    await OwnerServiceSmoke.RunS8T5LifecyclePhaseAsync(args[1], Path.GetFullPath(args[2])).ConfigureAwait(false);
                    break;
                case "s8-t6-authorize-dispatch":
                    await OwnerServiceSmoke.RunS8T6AuthorizeDispatchAsync(fullPath).ConfigureAwait(false);
                    break;
                default:
                    await OwnerServiceSmoke.RunAsync(fullPath).ConfigureAwait(false);
                    break;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL owner service smoke (" + ex.GetType().Name + ": " + ex.Message + ")");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
