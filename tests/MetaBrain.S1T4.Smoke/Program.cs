namespace MetaBrain.S1T4.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
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
             string.Equals(args[0], "s6-t4", StringComparison.Ordinal))
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
               string.Equals(args[0], "s5-t7-console-corpus", StringComparison.Ordinal))
                ? args[0]
                : null;
        var executable = scenario is not null ? args[1] : args.Length == 1 ? args[0] : null;
        if (executable is null || !File.Exists(executable))
        {
            Console.Error.WriteLine("Usage: MetaBrain.S1T4.Smoke <MetaBrain.Connections.exe> | scope-issue|agent-session|s1-t5|s1-t6|s1-t7|s1-t8|s1-t9|s2-t1|s2-t2|s2-t3|s2-t4|s2-t5|s2-t6|s2-t7|s3-t1|s3-t2|s3-t3|s3-t4|s3-t5|s3-t6|s4-t1|s4-t2|s4-t3|s4-t4|s4-t5|s4-t6|s4-t7|s4-t7-client-fixture|s4-t6-client-fixture|s5-t1 <MetaBrain.Connections.exe> | s5-t2 <MetaBrain.Connections.exe> | s5-t3 <MetaBrain.Connections.exe> | s5-t4 <MetaBrain.Connections.exe> | s5-t5 <MetaBrain.Connections.exe> | s5-t5-console-fixture|s5-t5-console-corpus| s5-t1-console-fixture|s5-t2-console-fixture|s5-t3-console-fixture|s5-t3-console-corpus|s5-t4-console-fixture|s5-t4-console-inbox|s5-t4-console-proposals <MetaBrain.Connections.exe> <fixture-root>");
            return 2;
        }

        try
        {
            var fullPath = Path.GetFullPath(executable);
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
