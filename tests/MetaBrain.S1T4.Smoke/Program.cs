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
             string.Equals(args[0], "s4-t6-client-fixture", StringComparison.Ordinal))
            ? args[0]
            : null;
        var executable = scenario is not null ? args[1] : args.Length == 1 ? args[0] : null;
        if (executable is null || !File.Exists(executable))
        {
            Console.Error.WriteLine("Usage: MetaBrain.S1T4.Smoke <MetaBrain.Connections.exe> | scope-issue|agent-session|s1-t5|s1-t6|s1-t7|s1-t8|s1-t9|s2-t1|s2-t2|s2-t3|s2-t4|s2-t5|s2-t6|s2-t7|s3-t1|s3-t2|s3-t3|s3-t4|s3-t5|s3-t6|s4-t1|s4-t2|s4-t3|s4-t4|s4-t5|s4-t6|s4-t7|s4-t7-client-fixture|s4-t6-client-fixture <MetaBrain.Connections.exe>");
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
                case "agent-session":
                    await OwnerServiceSmoke.RunAgentSessionAsync(fullPath).ConfigureAwait(false);
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
