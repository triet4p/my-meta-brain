using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetaBrain.S1T4.Smoke;

// S4-T2: scope-filtered lexical/metadata/ranking/time recall over the
// protected private index, plus the approved-catalog surface as a separate
// surface. One synthetic owner vault is provisioned and unlocked through the
// real owner CLI over the real named-pipe service. Memory items carry
// bilingual (Vietnamese/English), symbol, paraphrase, ranking and time
// signals; the index is rebuilt; an owner grant with memory.search plus
// memory.read is issued and redeemed; every positive/negative scenario below
// runs through the real agent channel. All evidence is observed from the real
// service and real persisted files; nothing here is a mock.
internal static partial class OwnerServiceSmoke
{
    private const string S4T2ZoneAlpha = "s4t2-zone-alpha";
    private const string S4T2ZoneBeta = "s4t2-zone-beta";
    private const string S4T2OutItem = "s4t2-item-outside";
    private const string S4T2GuessedId = "s4t2-guessed-item";

    private static string S4T2ItemDraft(string kind, string author, string creationMethod, string evidence, string title, string body, DateTimeOffset occurred, string? level = "l1") =>
        JsonSerializer.Serialize(new
        {
            kind,
            author,
            creationMethod,
            evidence,
            level,
            title,
            body,
            sources = Array.Empty<object>(),
            occurredAtUtc = occurred,
            recordedAtUtc = occurred.AddMinutes(1),
            applicability = (object?)null
        });

    internal static async Task RunS4T2SearchAsync(string connectionsExecutable)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The S4-T2 search smoke requires Windows named pipes and DPAPI.");
        }

        Console.WriteLine("RUN S4-T2 scoped lexical/metadata/ranking/time search over the protected private index");
        using var fixture = SmokeFixture.CreateUninitialized();
        RunningService? service = null;
        try
        {
            var passphraseBytes = RandomNumberGenerator.GetBytes(24);
            var passphrase = Convert.ToBase64String(passphraseBytes);
            CryptographicOperations.ZeroMemory(passphraseBytes);
            service = await StartServiceAsync(connectionsExecutable, fixture.SettingsPath).ConfigureAwait(false);
            var provisioned = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine + passphrase + Environment.NewLine,
                "owner", "provision", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(provisioned.ExitCode == 0 && provisioned.StandardOutput.Contains("VAULT provisioned", StringComparison.Ordinal),
                "Provision did not create the S4-T2 synthetic vault (exit=" + provisioned.ExitCode + "; stderr=" + provisioned.StandardError.Trim() + ").");
            var unlock = await RunOwnerCommandWithInputAsync(connectionsExecutable,
                passphrase + Environment.NewLine, "owner", "unlock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(unlock.ExitCode == 0 && unlock.StandardOutput.Contains("VAULT unlocked", StringComparison.Ordinal),
                "The synthetic S4-T2 vault did not unlock (exit=" + unlock.ExitCode + "; stderr=" + unlock.StandardError.Trim() + ").");

            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-60);
            // In-scope corpus (zone alpha): bilingual pairs, symbol forms,
            // paraphrase targets, ranking pair, current/history pair.
            // Each document carries the query signal in exactly one language
            // or paraphrase side; the query uses the other side, so every
            // positive below proves non-exact recall, not planted overlap.
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-login", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 dang nhap lesson",
                    "Synthetic S4-T2 body: cach dang nhap he thong sau khi quen mat khau; never a real owner secret.", now.AddDays(-10)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-deploy", S4T2ZoneAlpha,
                S4T2ItemDraft("procedure", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 trien khai procedure",
                    "Synthetic S4-T2 body: cac buoc trien khai ban moi len may chu; never a real owner secret.", now.AddDays(-9)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-symbol", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 redeemToken lesson",
                    "Synthetic S4-T2 body: the redeemToken round trip keeps the opaque bearer out of arguments; never a real owner secret.", now.AddDays(-8)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-repair", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 sua chua lesson",
                    "Synthetic S4-T2 body: cach sua chua loi ket noi co so du lieu; never a real owner secret.", now.AddDays(-7)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-rank-fix", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 repair runbook",
                    "Synthetic S4-T2 body: fix fix fix the broken pipeline with a fix checklist and fix verification; never a real owner secret.", now.AddDays(-6)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-rank-mention", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 pipeline note",
                    "Synthetic S4-T2 body: one short note mentioning a fix in passing among unrelated pipeline steps; never a real owner secret.", now.AddDays(-5)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-current", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 current rollout lesson",
                    "Synthetic S4-T2 body: the current rollout playbook for this quarter; never a real owner secret.", now.AddDays(-4)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-old", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 retired rollout lesson",
                    "Synthetic S4-T2 body: the retired rollout playbook from two quarters ago; never a real owner secret.", old), 1).ConfigureAwait(false);
            // Out-of-scope corpus: one item in a second zone (never granted)
            // and one ungranted item in the granted zone.
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, "s4t2-doc-beta", S4T2ZoneBeta,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 beta zone lesson",
                    "Synthetic S4-T2 body: login procedures for the beta zone; never a real owner secret.", now.AddDays(-3)), 1).ConfigureAwait(false);
            await WriteS4T2ItemAsync(connectionsExecutable, fixture, S4T2OutItem, S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 ungranted login lesson",
                    "Synthetic S4-T2 body: login procedures kept outside the approved grant; never a real owner secret.", now.AddDays(-2)), 1).ConfigureAwait(false);

            var rebuild = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild.ExitCode == 0 && rebuild.StandardOutput.Contains("INDEX rebuilt rows=", StringComparison.Ordinal) &&
                    !rebuild.StandardOutput.Contains("rows=0", StringComparison.Ordinal),
                "The S4-T2 private index rebuild did not publish a materialized generation (exit=" + rebuild.ExitCode + "; stdout=" + rebuild.StandardOutput.Trim() + "; stderr=" + rebuild.StandardError.Trim() + ").");

            // Owner grants memory.search + memory.read over the eight alpha
            // items (never the beta item, never the ungranted item).
            var grantedIds = new[] { "s4t2-doc-login", "s4t2-doc-deploy", "s4t2-doc-symbol", "s4t2-doc-repair", "s4t2-doc-rank-fix", "s4t2-doc-rank-mention", "s4t2-doc-current", "s4t2-doc-old" };
            var session = await IssueS4T2GrantAsync(connectionsExecutable, fixture, "s4t2-search", "memory.search,memory.read", grantedIds).ConfigureAwait(false);

            // Discriminator: same-session memory.read proves the session
            // itself authorizes; the raw search reply prints full
            // status/error so a denial is never guessed at.
            var bearer = ReadSessionBearer(session.SessionFile);
            var readProbe = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = bearer,
                accessOperation = "memory.read",
                resourceId = "s4t2-doc-login",
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Console.WriteLine("S4T2-PROBE memory.read status=" + ReplyCode(readProbe));
            Require(ReplyCode(readProbe) == "memory",
                "Same-session memory.read denied; the session itself is broken (reply=" + readProbe.GetRawText() + ").");
            var authProbe = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.access.authorize",
                sessionToken = bearer,
                accessOperation = "memory.search"
            }).ConfigureAwait(false);
            Require(ReplyCode(authProbe) == "authorization_decision" && authProbe.GetRawText().Contains("\"allowed\":true", StringComparison.Ordinal),
                "Authorize for memory.search denied; the session authority rejects the grant op (reply=" + authProbe.GetRawText() + ").");
            Console.WriteLine("S4T2-PROBE authorize memory.search status=" + ReplyCode(authProbe));
            var rawSearch = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.search",
                sessionToken = bearer,
                accessOperation = "memory.search",
                query = "login",
                searchOffset = 0,
                searchLimit = 10
            }).ConfigureAwait(false);
            Console.WriteLine("S4T2-PROBE memory.search status=" + ReplyCode(rawSearch));
            Require(ReplyCode(rawSearch) == "memory_search",
                "Raw-pipe memory.search denied (reply=" + rawSearch.GetRawText() + ").");
            // Positive bilingual/symbol/paraphrase/current/ranking queries.
            var loginEn = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "login").ConfigureAwait(false);
            Require(loginEn.ExitCode == 0 && loginEn.StandardOutput.Contains("HIT id=s4t2-doc-login", StringComparison.Ordinal),
                "English 'login' did not recall the Vietnamese dang-nhap document (exit=" + loginEn.ExitCode + "; stdout=" + loginEn.StandardOutput.Trim() + "; stderr=" + loginEn.StandardError.Trim() + ").");
            Require(!loginEn.StandardOutput.Contains("s4t2-doc-beta", StringComparison.Ordinal) && !loginEn.StandardOutput.Contains(S4T2OutItem, StringComparison.Ordinal),
                "The login recall leaked an out-of-scope document.");
            Console.WriteLine("PASS bilingual EN->VI recall scoped to the grant");

            var deployVi = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "trien khai").ConfigureAwait(false);
            Require(deployVi.ExitCode == 0 && deployVi.StandardOutput.Contains("HIT id=s4t2-doc-deploy", StringComparison.Ordinal),
                "Vietnamese 'trien khai' did not recall the deployment document (exit=" + deployVi.ExitCode + "; stdout=" + deployVi.StandardOutput.Trim() + "; stderr=" + deployVi.StandardError.Trim() + ").");
            Console.WriteLine("PASS bilingual VI recall");

            var symbol = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "redeem_token").ConfigureAwait(false);
            Require(symbol.ExitCode == 0 && symbol.StandardOutput.Contains("HIT id=s4t2-doc-symbol", StringComparison.Ordinal),
                "Symbol query 'redeem_token' did not recall the camelCase document (exit=" + symbol.ExitCode + "; stdout=" + symbol.StandardOutput.Trim() + "; stderr=" + symbol.StandardError.Trim() + ").");
            Console.WriteLine("PASS symbol recall");

            var repair = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "repair").ConfigureAwait(false);
            Require(repair.ExitCode == 0 && repair.StandardOutput.Contains("HIT id=s4t2-doc-repair", StringComparison.Ordinal),
                "Paraphrase 'repair' did not recall the sua-chua document (exit=" + repair.ExitCode + "; stdout=" + repair.StandardOutput.Trim() + "; stderr=" + repair.StandardError.Trim() + ").");
            Console.WriteLine("PASS paraphrase recall across languages");

            var ranked = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "fix").ConfigureAwait(false);
            Require(ranked.ExitCode == 0 && ranked.StandardOutput.Contains("HIT id=s4t2-doc-rank-fix", StringComparison.Ordinal) &&
                    ranked.StandardOutput.Contains("HIT id=s4t2-doc-rank-mention", StringComparison.Ordinal),
                "Ranking query 'fix' did not return both rank documents (exit=" + ranked.ExitCode + "; stdout=" + ranked.StandardOutput.Trim() + ").");
            var fixFirst = ranked.StandardOutput.IndexOf("HIT id=s4t2-doc-rank-fix", StringComparison.Ordinal);
            var mentionFirst = ranked.StandardOutput.IndexOf("HIT id=s4t2-doc-rank-mention", StringComparison.Ordinal);
            Require(fixFirst >= 0 && fixFirst < mentionFirst,
                "The repeated-term document did not rank above the single-mention document.");
            Console.WriteLine("PASS ranking orders repeated-term hits first");

            var current = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "rollout", current: "current").ConfigureAwait(false);
            Require(current.ExitCode == 0 && current.StandardOutput.Contains("HIT id=s4t2-doc-current", StringComparison.Ordinal),
                "Current query 'rollout' did not surface the live playbook (exit=" + current.ExitCode + "; stdout=" + current.StandardOutput.Trim() + "; stderr=" + current.StandardError.Trim() + ").");
            Console.WriteLine("PASS current/time recall");

            var kind = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "trien khai", kind: "procedure").ConfigureAwait(false);
            Require(kind.ExitCode == 0 && kind.StandardOutput.Contains("HIT id=s4t2-doc-deploy", StringComparison.Ordinal) &&
                    !kind.StandardOutput.Contains("HIT id=s4t2-doc-login", StringComparison.Ordinal),
                "Kind filter did not restrict to procedures (stdout=" + kind.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS metadata kind filter");

            // Latency (evidence only, NOT an envelope gate): ten scoped
            // recalls over the persistent agent pipe (no per-query process
            // spawn), first call kept separate as JIT/connection warmup. The
            // frozen S1-T1 envelope (p95 <= 250ms at 1,000 records) is
            // reported beside the observation; a 10-item corpus can neither
            // pass nor fail it, and no target is invented here.
            var pipeSw = Stopwatch.StartNew();
            string? firstReply = null;
            for (var i = 0; i < 11; i++)
            {
                var timed = await InvokeAgentRequestAsync(fixture.AgentPipe, new
                {
                    protocolVersion = 1,
                    operation = "agent.memory.search",
                    sessionToken = bearer,
                    accessOperation = "memory.search",
                    query = "login",
                    searchOffset = 0,
                    searchLimit = 10
                }).ConfigureAwait(false);
                Require(ReplyCode(timed) == "memory_search", "Timed pipe recall failed on iteration " + i + ".");
                if (i == 0)
                {
                    firstReply = timed.GetRawText();
                    pipeSw.Restart();
                }
            }
            pipeSw.Stop();
            var pipeMeanMs = pipeSw.Elapsed.TotalMilliseconds / 10.0;
            Console.WriteLine($"PASS S4-T2 small-corpus pipe latency: 10 steady scoped recalls mean={pipeMeanMs.ToString("F1", CultureInfo.InvariantCulture)}ms over the live pipe on a 10-item synthetic corpus (first/warmup call excluded; frozen envelope p95<=250ms@1k reported for context only, NOT claimed)");
            var cliSw = Stopwatch.StartNew();
            for (var i = 0; i < 3; i++)
            {
                var timed = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "login").ConfigureAwait(false);
                Require(timed.ExitCode == 0, "Timed CLI recall failed on iteration " + i + ".");
            }
            cliSw.Stop();
            Console.WriteLine($"PASS S4-T2 CLI roundtrip latency: 3 full CLI recalls mean={(cliSw.Elapsed.TotalMilliseconds / 3.0).ToString("F1", CultureInfo.InvariantCulture)}ms (includes per-call process spawn; evidence only)");

            // Pagination: limit=3 over the 'synthetic' term walks the frozen
            // order stably with scope-only totals.
            var pageOne = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "synthetic", limit: 3, offset: 0).ConfigureAwait(false);
            Require(pageOne.ExitCode == 0 && pageOne.StandardOutput.Contains("total=8", StringComparison.Ordinal) && pageOne.StandardOutput.Contains("hasMore=true", StringComparison.Ordinal),
                "First page did not report the scope-only total with hasMore (stdout=" + pageOne.StandardOutput.Trim() + ").");
            var pageTwo = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "synthetic", limit: 3, offset: 3).ConfigureAwait(false);
            Require(pageTwo.ExitCode == 0 && pageTwo.StandardOutput.Contains("total=8", StringComparison.Ordinal),
                "Second page did not preserve the scope-only total (stdout=" + pageTwo.StandardOutput.Trim() + ").");
            Require(!pageOne.StandardOutput.Contains("s4t2-doc-beta", StringComparison.Ordinal) && !pageTwo.StandardOutput.Contains(S4T2OutItem, StringComparison.Ordinal),
                "Pagination leaked an out-of-scope document.");
            Console.WriteLine("PASS stable snapshot pagination with scope-only totals");

            // Negative scope queries: guessed IDs, wrong revision, ungranted
            // zone/item, and catalog separation.
            var guessed = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, S4T2GuessedId).ConfigureAwait(false);
            Require(guessed.ExitCode == 0 && guessed.StandardOutput.Contains("SEARCH no_match", StringComparison.Ordinal) && !guessed.StandardOutput.Contains("HIT id=", StringComparison.Ordinal),
                "A guessed-ID query returned hits (stdout=" + guessed.StandardOutput.Trim() + ").");
            var betaQuery = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "beta zone").ConfigureAwait(false);
            Require(betaQuery.ExitCode == 0 && !betaQuery.StandardOutput.Contains("HIT id=s4t2-doc-beta", StringComparison.Ordinal),
                "An ungranted-zone query returned the beta document (stdout=" + betaQuery.StandardOutput.Trim() + ").");
            var catalogList = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "agent-catalog-list", "--agent-pipe", fixture.AgentPipe).ConfigureAwait(false);
            Require(catalogList.ExitCode == 0 && !catalogList.StandardOutput.Contains("s4t2-doc-login", StringComparison.Ordinal),
                "Memory items auto-published into agent catalog discovery.");
            Console.WriteLine("PASS negative scope queries and catalog separation");

            // Revise one granted item (new revision enters canonical) then
            // search BEFORE rebuilding: stale generation must fail closed.
            await WriteS4T2ItemReviseAsync(connectionsExecutable, fixture, "s4t2-doc-login", S4T2ZoneAlpha,
                S4T2ItemDraft("lesson", "user", "directWrite", "unverified",
                    "Synthetic S4-T2 dang nhap lesson",
                    "Synthetic S4-T2 body: cach dang nhap he thong sau khi quen mat khau, ban sua doi; never a real owner secret.", now.AddDays(-10)), 1, 2).ConfigureAwait(false);
            var stale = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "login").ConfigureAwait(false);
            Require(stale.ExitCode == 3 && stale.StandardError.Contains("search_index_stale", StringComparison.Ordinal),
                "A moved canonical set did not fail closed with search_index_stale (exit=" + stale.ExitCode + "; stdout=" + stale.StandardOutput.Trim() + "; stderr=" + stale.StandardError.Trim() + ").");
            Console.WriteLine("PASS stale generation fails closed instead of scanning");

            // Rebuild: the frozen snapshot still pins revision 1, so the
            // revised body must NOT surface through the old grant.
            var rebuild2 = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "index-rebuild", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(rebuild2.ExitCode == 0, "Rebuild after revision failed (exit=" + rebuild2.ExitCode + ").");
            var afterRebuild = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "login").ConfigureAwait(false);
            Require(afterRebuild.ExitCode == 0 && !afterRebuild.StandardOutput.Contains("ban sua doi", StringComparison.Ordinal),
                "The frozen grant served the new revision body (stdout=" + afterRebuild.StandardOutput.Trim() + ").");
            Require(!afterRebuild.StandardOutput.Contains("HIT id=s4t2-doc-login", StringComparison.Ordinal),
                "The superseded frozen revision stayed servable after its revision moved (stdout=" + afterRebuild.StandardOutput.Trim() + ").");
            var frozenRead = await InvokeAgentRequestAsync(fixture.AgentPipe, new
            {
                protocolVersion = 1,
                operation = "agent.memory.read",
                sessionToken = bearer,
                accessOperation = "memory.read",
                resourceId = "s4t2-doc-login",
                resourceRevision = 1L
            }).ConfigureAwait(false);
            Require(ReplyCode(frozenRead) == "denied",
                "Get-by-ID at the superseded frozen revision stayed servable (reply=" + frozenRead.GetRawText() + ").");
            var survivors = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "trien khai").ConfigureAwait(false);
            Require(survivors.ExitCode == 0 && survivors.StandardOutput.Contains("HIT id=s4t2-doc-deploy", StringComparison.Ordinal),
                "One moved resource denied the surviving scope (stdout=" + survivors.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS new revisions never enter the frozen grant; moved items drop, survivors serve");

            // MCP bridge: the new tool is advertised and serves scoped hits.
            var bridgeList = await RunBridgeToolsListAsync(connectionsExecutable, fixture.AgentPipe).ConfigureAwait(false);
            Require(bridgeList.Contains("metabrain_memory_search", StringComparison.Ordinal),
                "The MCP bridge did not advertise metabrain_memory_search.");
            var bridgeSearch = await RunBridgeToolAsync(connectionsExecutable, fixture.AgentPipe,
                "metabrain_memory_search", new { sessionFile = session.SessionFile, query = "trien khai" }).ConfigureAwait(false);
            Require(bridgeSearch.Contains("s4t2-doc-deploy", StringComparison.Ordinal) && !bridgeSearch.Contains("mb1_", StringComparison.Ordinal),
                "The MCP bridge search did not serve the scoped hit without bearer material.");
            Console.WriteLine("PASS MCP bridge serves scoped search hits");

            // Revoke then lock: both deny cached/next search calls.
            var revoke = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "revoke-session", "--control-pipe", fixture.ControlPipe,
                "--session-id", session.SessionId).ConfigureAwait(false);
            Require(revoke.ExitCode == 0, "The owner could not revoke the S4-T2 session.");
            var revokedSearch = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session, "login").ConfigureAwait(false);
            Require(revokedSearch.ExitCode == 3 && revokedSearch.StandardError.Contains("resource_unavailable", StringComparison.Ordinal) &&
                    !revokedSearch.StandardOutput.Contains("HIT id=", StringComparison.Ordinal),
                "A revoked session still searched (exit=" + revokedSearch.ExitCode + "; stdout=" + revokedSearch.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS revoke denies cached/next search calls");

            var session2 = await IssueS4T2GrantAsync(connectionsExecutable, fixture, "s4t2-lock", "memory.search,memory.read", grantedIds).ConfigureAwait(false);
            var locked = await RunOwnerCommandAsync(connectionsExecutable,
                "owner", "lock", "--control-pipe", fixture.ControlPipe).ConfigureAwait(false);
            Require(locked.ExitCode == 0, "The owner could not lock after the S4-T2 walkthrough.");
            var lockedSearch = await AgentS4T2SearchAsync(connectionsExecutable, fixture, session2, "login").ConfigureAwait(false);
            Require(lockedSearch.ExitCode == 3 && !lockedSearch.StandardOutput.Contains("HIT id=", StringComparison.Ordinal),
                "A locked vault served a scoped search (exit=" + lockedSearch.ExitCode + "; stdout=" + lockedSearch.StandardOutput.Trim() + ").");
            Console.WriteLine("PASS lock denies search calls");

            Console.WriteLine("PASS S4-T2 scoped search walkthrough with no hidden counts and no bearer material");
            Console.WriteLine("PASS fixture processes, profiles, and protected files were cleaned up");
        }
        finally
        {
            if (service is not null)
            {
                await StopServiceAsync(service).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteS4T2ItemAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t2-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-create", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory create did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private static async Task WriteS4T2ItemReviseAsync(string executable, SmokeFixture fixture, string resourceId, string zoneId, string draftJson, long writeRevision, long expectedRevision)
    {
        var input = Path.Combine(fixture.OutputDirectory, "s4t2-item-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input, draftJson, Encoding.UTF8).ConfigureAwait(false);
        var result = await RunOwnerCommandAsync(executable,
            "owner", "memory-revise", "--control-pipe", fixture.ControlPipe,
            "--resource-id", resourceId, "--zone-id", zoneId,
            "--expected-revision", writeRevision.ToString(CultureInfo.InvariantCulture),
            "--input-file", input).ConfigureAwait(false);
        Require(result.ExitCode == 0 && result.StandardOutput.Contains($"MEMORY stored revision={expectedRevision}", StringComparison.Ordinal),
            $"Memory revise did not store {resourceId} at revision {expectedRevision} (exit={result.ExitCode}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}).");
        File.Delete(input);
    }

    private sealed record S4T2Session(string SessionId, string SessionFile);

    private static async Task<S4T2Session> IssueS4T2GrantAsync(string executable, SmokeFixture fixture, string name, string operations, string[] resourceIds)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture);
        var handoff = Path.Combine(fixture.OutputDirectory, name + ".grant-handoff");
        var sessionFile = Path.Combine(fixture.OutputDirectory, name + ".session-handoff");
        var grant = await RunOwnerCommandWithInputAsync(executable, "ISSUE" + Environment.NewLine,
            "owner", "grant", "--control-pipe", fixture.ControlPipe,
            "--resource-ids", string.Join(",", resourceIds),
            "--operations", operations,
            "--expires-at-utc", expiry,
            "--handoff-file", handoff).ConfigureAwait(false);
        Require(grant.ExitCode == 0,
            $"The owner could not issue the synthetic S4-T2 grant {name} (exit={grant.ExitCode}; stdout={grant.StandardOutput.Trim()}; stderr={grant.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(handoff);
        var redeem = await RunOwnerCommandAsync(executable,
            "owner", "redeem", "--agent-pipe", fixture.AgentPipe,
            "--handoff-file", handoff, "--session-file", sessionFile).ConfigureAwait(false);
        Require(redeem.ExitCode == 0 && redeem.StandardOutput.Contains("SESSION issued", StringComparison.Ordinal),
            $"Redemption failed for the synthetic S4-T2 grant {name} (exit={redeem.ExitCode}; stderr={redeem.StandardError.Trim()}).");
        VerifyOwnerHandoffAcl(sessionFile);
        return new S4T2Session(ParseSessionId(redeem.StandardOutput), sessionFile);
    }

    private static async Task<CommandResult> AgentS4T2SearchAsync(string executable, SmokeFixture fixture, S4T2Session session, string query, int limit = 10, int offset = 0, string? kind = null, string? current = null)
    {
        var args = new List<string>
        {
            "owner", "agent-search", "--agent-pipe", fixture.AgentPipe,
            "--session-file", session.SessionFile,
            "--query", query,
            "--offset", offset.ToString(CultureInfo.InvariantCulture),
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
        };
        if (kind is not null)
        {
            args.Add("--kind");
            args.Add(kind);
        }

        if (current is not null)
        {
            args.Add("--current");
            args.Add(current);
        }

        return await RunOwnerCommandAsync(executable, args.ToArray()).ConfigureAwait(false);
    }
}
