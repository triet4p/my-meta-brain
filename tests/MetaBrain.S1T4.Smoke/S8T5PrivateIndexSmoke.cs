using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Connections;
using MetaBrain.Core.Index;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.S1T4.Smoke;

/// <summary>
/// S8-T5 regression boundary for the encrypted persistent private index.
/// <para>
/// The private index is a rebuildable derivative of the canonical encrypted
/// records, stored encrypted at rest with bounded resident state. This
/// scenario exercises the production engine in process — no service, named
/// pipe, WPF or IPC — and covers scope, revision, exact tokens, ciphertext
/// privacy, fail-closed reads, cache bounds and interrupted publication.
/// </para>
/// </summary>
internal static partial class OwnerServiceSmoke
{
    private const string S8T5IndexZone = "s8t5-index-zone";

    /// <summary>
    /// The canonical digest of the synthetic generation. It names the
    /// generation the engine publishes, exactly as the digest of the live
    /// canonical set names a real one.
    /// </summary>
    private const string S8T5IndexDigest = "s8t5-index-canonical-digest";

    internal static async Task RunS8T5PrivateIndexAsync()
    {
        Console.WriteLine("RUN S8-T5 encrypted persistent private index contract (in process, no service, pipe, WPF or IPC)");
        S8T5AssertCancellationBeforeCommitPublishesNothing();
        S8T5AssertQueryTokenMatcher();
        S8T5AssertMaximumSupportedQueryTerms();
        S8T5AssertNonceCounterCannotWrap();
        S8T5AssertGroupedSpillMultiPass();

        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keys = new S8T5IndexKeys();
            using var index = new EncryptedPrivateIndexStore(root, keys);

            var alpha = S8T5IndexDocument(
                "s8t5-index-alpha",
                1,
                "Quyet dinh: dang nhap he thong",
                "The owner decides that the login procedure keeps the recovery key outside the vault.");
            var beta = S8T5IndexDocument(
                "s8t5-index-beta",
                1,
                "Lesson: the login procedure",
                "The owner remembers that the recovery code is rotated after every login.");
            var outside = S8T5IndexDocument(
                "s8t5-index-outside",
                1,
                "Question: the login procedure",
                "The owner asks whether the recovery code belongs in the catalog.");

            // A generation is published by one committed write scope.
            using (var scope = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(alpha.Row);
                index.InsertSearchDocument(alpha.Document);
                index.Insert(beta.Row);
                index.InsertSearchDocument(beta.Document);
                index.Insert(outside.Row);
                index.InsertSearchDocument(outside.Document);
                scope.Commit();
            }

            Require(index.SchemaVersion == PrivateIndexFormat.CurrentVersion,
                "The private index engine did not open at the current format version (version=" + index.SchemaVersion + ").");
            Require(index.RowCount == 3,
                "The published generation did not carry every materialized row (rows=" + index.RowCount + ").");
            Require(string.Equals(index.CanonicalDigest, S8T5IndexDigest, StringComparison.Ordinal),
                "The published generation does not name the canonical set it was built from (digest=" + index.CanonicalDigest + ").");

            // A term selects exactly the documents that hold it, inside the
            // scope. The scope is applied inside the engine: a document
            // outside the scope never leaves it.
            var inScope = new[]
            {
                new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 1),
                new ScopeResourceRevision("s8t5-index-beta", S8T5IndexZone, 1),
            };
            var selected = S8T5Stream(index, inScope, new[] { "login" });
            Require(selected.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
                "The candidate stream did not select the two in-scope documents that carry the term (selected=" + string.Join(",", selected) + ").");

            var narrowed = S8T5Stream(index, new[] { new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 1) }, new[] { "login" });
            Require(narrowed.SequenceEqual(new[] { "s8t5-index-alpha" }),
                "The candidate stream did not keep the scope boundary (selected=" + string.Join(",", narrowed) + ").");

            // A term no document holds selects nothing.
            Require(S8T5Stream(index, inScope, new[] { "termthatnoholderhas" }).Length == 0,
                "The candidate stream selected a document for a term no document carries.");

            var emptyBrowse = S8T5Stream(index, inScope, Array.Empty<string>());
            Require(emptyBrowse.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
                "An empty candidate browse did not return the complete frozen scope.");
            var wideQueryText = S8T5WideQueryText("login");
            var wideQueryTerms = SearchText.ExpandQuery(wideQueryText)
                .Select(term => term.Token)
                .ToArray();
            Require(wideQueryTerms.Length > PrivateIndexFormat.MaximumQueryTermsPerPass &&
                    wideQueryTerms.Contains("login", StringComparer.Ordinal),
                "Query expansion truncated the full term set before the bounded passes.");
            var wideQueryResults = S8T5Stream(index, inScope, wideQueryTerms);
            Require(wideQueryResults.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
                "The query stream did not preserve terms after the first bounded pass (selected=" + string.Join(",", wideQueryResults) + ").");
            S8T5AssertCancellationResetsTouchedCandidates(index, inScope);
            S8T5AssertLockDuringQueryResetsTouchedCandidates(index, inScope, keys);

            S8T5AssertAbandonedEnumerationDoesNotAffectNextQuery(index, inScope);
            // The document carries exactly the token set of its own text, in
            // the engine's own order, and the same answer comes back for a
            // document read alone and for a batched read.
            var alphaTokens = S8T5ExpectedTokens(alpha.Document);
            var readAlone = index.SelectSearchDocument("s8t5-index-alpha", 1);
            Require(readAlone is not null && readAlone.Tokens.SequenceEqual(alphaTokens),
                "The document read did not return the exact token set in the engine's order (returned=" + string.Join(",", readAlone?.Tokens ?? Array.Empty<string>()) + ").");

            var batched = index.SelectSearchDocuments(inScope);
            Require(batched.Length == 2 &&
                    batched[0].ResourceId == "s8t5-index-alpha" &&
                    batched[0].Tokens.SequenceEqual(alphaTokens) &&
                    batched[1].ResourceId == "s8t5-index-beta",
                "The batched document read did not return the same documents and token sets as the single read.");

            // A second revision of the same record keeps its own postings:
            // the first revision's document still answers with the first
            // revision's tokens, and the second revision answers with its own.
            var alphaRevisionTwo = S8T5IndexDocument(
                "s8t5-index-alpha",
                2,
                "Quyet dinh: the recovery code rotation",
                "The owner decides that the recovery code is quarantined after every login.");
            using (var scope = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(alpha.Row);
                index.InsertSearchDocument(alpha.Document);
                index.Insert(alphaRevisionTwo.Row);
                index.InsertSearchDocument(alphaRevisionTwo.Document);
                index.Insert(beta.Row);
                index.InsertSearchDocument(beta.Document);
                index.Insert(outside.Row);
                index.InsertSearchDocument(outside.Document);
                scope.Commit();
            }

            var revisionOne = index.SelectSearchDocument("s8t5-index-alpha", 1);
            var revisionTwo = index.SelectSearchDocument("s8t5-index-alpha", 2);
            Require(revisionOne is not null && revisionOne.Tokens.SequenceEqual(alphaTokens),
                "The first revision lost its own token set after a second revision was indexed.");
            Require(revisionTwo is not null && revisionTwo.Tokens.SequenceEqual(S8T5ExpectedTokens(alphaRevisionTwo.Document)),
                "The second revision did not answer with its own token set.");
            Require(index.SelectSearchDocument("s8t5-index-alpha", 3) is null,
                "A revision the engine never materialized answered with a document.");

            // The approved revision decides the answer: a scope that names
            // the first revision is answered by the first revision, so the
            // term of the second revision is not one of its matched terms
            // and the ranking drops the candidate.
            var atRevisionOne = S8T5MatchedTerms(index, new[] { new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 1) }, new[] { "quarantined" });
            Require(atRevisionOne.Count == 0,
                "A query for a term present only in revision 2 returned the approved revision-1 document.");
            var atRevisionTwo = S8T5MatchedTerms(index, new[] { new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 2) }, new[] { "quarantined" });
            Require(atRevisionTwo.Count == 1 && atRevisionTwo[0].SequenceEqual(new[] { "quarantined" }),
                "The approved second revision was not found through its own postings (matched=" + string.Join(",", atRevisionTwo.Select(terms => string.Join("+", terms))) + ").");

            S8T5AssertIndexIsEncryptedAtRest(root, index);
            S8T5AssertIndexLeavesNoPlaintextArtifact(root);
            S8T5AssertTamperedIndexFailsClosed(root, keys);
            S8T5AssertTruncatedIndexFailsClosed(root, keys);
            S8T5AssertNewerFormatFailsClosed(root, keys);
            S8T5AssertMalformedLengthsFailBeforeAllocation(root, keys);
            S8T5AssertSameDigestRecordSpliceFails(keys);
            S8T5AssertLongAndLargeDocumentRecords();
            S8T5AssertCacheStaysInsideItsBudget(root, keys);
            S8T5AssertLockedVaultCannotRead(root, keys);
            S8T5AssertInterruptedRebuildPublishesNothing(root, keys, alpha, beta, outside);
            S8T5AssertCrashedRebuildIsReaped(root, keys, alpha, beta, outside);
            await S8T5AssertOwnerIndexStatusUsesMaterializedGenerationAsync().ConfigureAwait(false);
            await S8T5AssertOwnerIndexLifecycleReuseAsync().ConfigureAwait(false);
            await S8T5AssertEmptyAndMissingIndexAsync().ConfigureAwait(false);
            await S8T5AssertScopedAgentSearchDifferentialAsync().ConfigureAwait(false);

            Console.WriteLine("PASS the encrypted persistent private index selects, scopes, orders, revision-pins and fails closed exactly what the recall path reads");
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void S8T5AssertQueryTokenMatcher()
    {
        var queryTokens = new HashSet<string>(
            new[] { "false-hit", "Ωmega", "zulu", "😀", "\uE000", "\U00010000" },
            StringComparer.Ordinal);
        var payload = PrivateIndexRecords.WriteTokens(
            new[] { "collision", "Ωmega", "zulu", "😀", "zulu", "\uE000", "\U00010000" });
        var queryBeacons = PrivateIndexRecords.CreateQueryTokenBeacons(queryTokens, out var queryUtf8);
        // Model a row admitted by a posting-digest collision; only exact token bytes may match.
        try
        {

            var matched = PrivateIndexRecords.ReadMatchingTokens(payload, queryUtf8, queryBeacons);
            var baseline = PrivateIndexRecords.ReadTokens(payload)
                .Where(queryTokens.Contains)
                .ToArray();
            Require(matched.SequenceEqual(baseline) &&
                    matched.SequenceEqual(new[] { "zulu", "zulu", "Ωmega", "\uE000", "\U00010000", "😀" }),
                "The span matcher changed exact membership, duplicate occurrences, or Utf8Order for a collision candidate.");

            var emptyPayload = new byte[sizeof(int)];
            try
            {
                Require(PrivateIndexRecords.ReadMatchingTokens(
                            emptyPayload,
                            ReadOnlySpan<byte>.Empty,
                            Array.Empty<PrivateIndexRecords.QueryTokenBeacon>()).Length == 0,
                    "A valid empty token record did not produce an empty exact match.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(emptyPayload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(queryUtf8);
            Array.Clear(queryBeacons, 0, queryBeacons.Length);
        }

        void Expect<TException>(byte[] malformed, string failure)
            where TException : Exception
        {
            var observed = false;
            try
            {
                _ = PrivateIndexRecords.ReadMatchingTokens(
                    malformed,
                    ReadOnlySpan<byte>.Empty,
                    Array.Empty<PrivateIndexRecords.QueryTokenBeacon>());
            }
            catch (TException)
            {
                observed = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(malformed);
            }

            Require(observed, failure);
        }

        void ExpectFullTokenRead<TException>(byte[] malformed, string failure)
            where TException : Exception
        {
            var observed = false;
            try
            {
                _ = PrivateIndexRecords.ReadTokens(malformed);
            }
            catch (TException)
            {
                observed = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(malformed);
            }

            Require(observed, failure);
        }

        static byte[] OneToken(byte[] lengthPrefix, byte[] tokenBytes)
        {
            var payload = new byte[sizeof(int) + lengthPrefix.Length + tokenBytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(payload, 1);
            lengthPrefix.CopyTo(payload, sizeof(int));
            tokenBytes.CopyTo(payload, sizeof(int) + lengthPrefix.Length);
            return payload;
        }

        static byte[] LengthPrefix(int value)
        {
            var bytes = new byte[5];
            var remaining = unchecked((uint)value);
            var count = 0;
            do
            {
                var current = (byte)(remaining & 0x7F);
                remaining >>= 7;
                if (remaining != 0)
                {
                    current |= 0x80;
                }

                bytes[count++] = current;
            }
            while (remaining != 0);

            return bytes.AsSpan(0, count).ToArray();
        }

        Expect<EndOfStreamException>(
            new byte[sizeof(int) - 1],
            "The matched-token parser accepted a truncated token count.");

        var negativeCount = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(negativeCount, -1);
        Expect<InvalidDataException>(negativeCount, "The matched-token parser accepted a negative token count.");

        var excessiveCount = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(excessiveCount, PrivateIndexFormat.MaximumSearchTokens + 1);
        Expect<InvalidDataException>(excessiveCount, "The matched-token parser accepted a token count above its derived bound.");

        Expect<EndOfStreamException>(
            OneToken(new byte[] { 0x80 }, Array.Empty<byte>()),
            "The matched-token parser accepted a truncated 7-bit token length.");
        Expect<FormatException>(
            OneToken(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 }, Array.Empty<byte>()),
            "The matched-token parser accepted a 7-bit token length overflow.");
        Expect<InvalidDataException>(
            OneToken(LengthPrefix(-1), Array.Empty<byte>()),
            "The matched-token parser accepted a negative 7-bit token length.");
        Expect<InvalidDataException>(
            OneToken(LengthPrefix(PrivateIndexFormat.MaximumTokenBytes + 1), Array.Empty<byte>()),
            "The matched-token parser accepted a token longer than its derived byte bound.");
        Expect<InvalidDataException>(
            OneToken(new byte[] { 0 }, Array.Empty<byte>()),
            "The matched-token parser accepted an empty stored token.");
        Expect<InvalidDataException>(
            OneToken(new byte[] { 2 }, new byte[] { (byte)'a' }),
            "The matched-token parser accepted a truncated token payload.");
        Expect<InvalidDataException>(
            OneToken(new byte[] { 1 }, new byte[] { 0xFF }),
            "The matched-token parser replacement-decoded malformed UTF-8.");

        var invalidAfterMiss = new byte[sizeof(int) + 4];
        BinaryPrimitives.WriteInt32LittleEndian(invalidAfterMiss, 2);
        invalidAfterMiss[sizeof(int)] = 1;
        invalidAfterMiss[sizeof(int) + 1] = (byte)'x';
        invalidAfterMiss[sizeof(int) + 2] = 1;
        invalidAfterMiss[sizeof(int) + 3] = 0xFF;
        Expect<InvalidDataException>(
            invalidAfterMiss,
            "The matched-token parser skipped malformed UTF-8 after a non-matching entry.");

        var trailingData = new byte[sizeof(int) + 1];
        trailingData[sizeof(int)] = 1;
        Expect<InvalidDataException>(
            trailingData,
            "The matched-token parser accepted trailing token-record data.");

        Expect<InvalidDataException>(
            new byte[PrivateIndexFormat.MaximumTokensRecordBytes + 1],
            "The matched-token parser accepted a token record above its derived byte bound.");

        Expect<FormatException>(
            OneToken(new byte[] { 0x81, 0x00 }, new byte[] { (byte)'a' }),
            "The matched-token parser accepted a non-minimal 7-bit length.");
        ExpectFullTokenRead<FormatException>(
            OneToken(new byte[] { 0x81, 0x00 }, new byte[] { (byte)'a' }),
            "The full-token reader accepted a non-minimal 7-bit length.");
        ExpectFullTokenRead<InvalidDataException>(
            OneToken(new byte[] { 1 }, new byte[] { 0xFF }),
            "The full-token reader replacement-decoded malformed UTF-8.");
        ExpectFullTokenRead<InvalidDataException>(
            OneToken(new byte[] { 0 }, Array.Empty<byte>()),
            "The full-token reader accepted an empty token.");
        ExpectFullTokenRead<InvalidDataException>(
            new byte[sizeof(int) + 1],
            "The full-token reader accepted trailing token-record data.");
        var impossibleFullCount = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(impossibleFullCount, PrivateIndexFormat.MaximumSearchTokens);
        ExpectFullTokenRead<InvalidDataException>(
            impossibleFullCount,
            "The full-token reader allocated a token array for an impossible payload count.");

        var maximumToken = new string('\u1100', PrivateIndexFormat.MaximumTokenBytes / 3);
        var maximumTokenPayload = PrivateIndexRecords.WriteTokens(new[] { maximumToken });
        var maximumTokenBeacons = PrivateIndexRecords.CreateQueryTokenBeacons(
            new HashSet<string>(new[] { maximumToken }, StringComparer.Ordinal),
            out var maximumTokenUtf8);
        try
        {
            var maximumTokenMatches = PrivateIndexRecords.ReadMatchingTokens(
                maximumTokenPayload,
                maximumTokenUtf8,
                maximumTokenBeacons);
            Require(Encoding.UTF8.GetByteCount(maximumToken) == PrivateIndexFormat.MaximumTokenBytes &&
                    maximumTokenPayload.Length == sizeof(int) + 3 + PrivateIndexFormat.MaximumTokenBytes &&
                    maximumTokenMatches.Length == 1 &&
                    ReferenceEquals(maximumTokenMatches[0], maximumToken),
                "The matched-token parser rejected or copied the exact maximum bounded UTF-8 token.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(maximumTokenPayload);
            CryptographicOperations.ZeroMemory(maximumTokenUtf8);
            Array.Clear(maximumTokenBeacons, 0, maximumTokenBeacons.Length);
        }

        Console.WriteLine("PASS authenticated matched-token parser: exact collision-safe parity, Utf8Order, and malformed count/length/UTF-8/trailing checks");
    }

    private static void S8T5AssertMaximumSupportedQueryTerms()
    {
        var queryBuilder = new StringBuilder(SearchText.MaximumQueryLength);
        var digitCount = 0;
        for (var codeUnit = 0; codeUnit <= char.MaxValue && digitCount < 127; codeUnit++)
        {
            var digit = (char)codeUnit;
            if (!char.IsDigit(digit) || digit == '0')
            {
                continue;
            }

            if (digitCount > 0)
            {
                queryBuilder.Append(' ');
            }

            queryBuilder.Append(digit);
            digitCount++;
        }

        queryBuilder.Append(" 00");

        var queryText = queryBuilder.ToString();
        var queryTerms = SearchText.ExpandQuery(queryText)
            .Select(term => term.Token)
            .ToArray();
        Require(digitCount == 127 &&
                queryText.Length == SearchText.MaximumQueryLength &&
                queryTerms.Length == 128 &&
                queryTerms.Distinct(StringComparer.Ordinal).Count() == 128,
            "The maximum supported normalized query fixture did not retain 128 distinct terms.");

        var (row, document) = S8T5IndexDocument(
            "s8t5-index-query-bound",
            1,
            "Maximum query boundary",
            queryText);
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Query-Boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var keys = new S8T5IndexKeys();
            using var index = new EncryptedPrivateIndexStore(root, keys);
            using (var write = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(row);
                index.InsertSearchDocument(document);
                write.Commit();
            }

            var scope = new[] { new ScopeResourceRevision(row.ResourceId, row.ZoneId, row.Revision) };
            var matched = S8T5MatchedTerms(index, scope, queryTerms);
            var expected = queryTerms.OrderBy(term => term, PrivateIndexRecords.Utf8Order.Instance).ToArray();
            Require(matched.Count == 1 && matched[0].SequenceEqual(expected),
                "The full 256-character/128-term query lost terms across the bounded 64-term passes.");
            Console.WriteLine("PASS maximum supported normalized query: 256 characters, 128 exact terms across two 64-term passes");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// The candidate stream of one query, as the plain resource IDs it
    /// answered with.
    /// </summary>
    private static string[] S8T5Stream(
        IPrivateIndexStore index,
        IReadOnlyList<ScopeResourceRevision> scope,
        IReadOnlyList<string> tokens)
    {
        var selected = new List<string>();
        foreach (var batch in index.StreamSearchCandidates(scope, tokens, 64, includeTemporalRow: true))
        {
            foreach (var candidate in batch.Candidates)
            {
                selected.Add(candidate.Resource.ResourceId);
            }
        }

        return selected.ToArray();
    }
    private static void S8T5AssertAbandonedEnumerationDoesNotAffectNextQuery(
        IPrivateIndexStore index,
        IReadOnlyList<ScopeResourceRevision> scope)
    {
        using (var abandoned = index.StreamSearchCandidates(
                   scope,
                   new[] { "login", "recovery" },
                   batchSize: 1,
                   includeTemporalRow: true).GetEnumerator())
        {
            Require(abandoned.MoveNext() && abandoned.Current.Candidates.Count == 1,
                "The synthetic streaming query did not produce its first candidate batch.");
        }

        var afterDisposal = S8T5Stream(index, scope, new[] { "login" });
        Require(afterDisposal.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
            "Disposing a partially consumed query changed the next query's candidates (selected=" + string.Join(",", afterDisposal) + ").");

        var exceptionObserved = false;
        try
        {
            using var interrupted = index.StreamSearchCandidates(
                scope,
                new[] { "login", "recovery" },
                batchSize: 1,
                includeTemporalRow: true).GetEnumerator();
            Require(interrupted.MoveNext(),
                "The synthetic query did not begin before the consumer exception.");
            throw new InvalidOperationException("synthetic consumer exception");
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic consumer exception")
        {
            exceptionObserved = true;
        }

        Require(exceptionObserved, "The iterator cleanup regression did not exercise the consumer exception path.");
        var afterException = S8T5Stream(index, scope, new[] { "login" });
        Require(afterException.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
            "A consumer exception left touched query accumulators in the next query (selected=" + string.Join(",", afterException) + ").");
    }

    private static void S8T5AssertCancellationResetsTouchedCandidates(
        IPrivateIndexStore index,
        IReadOnlyList<ScopeResourceRevision> scope)
    {
        var tokens = Enumerable.Range(0, 64)
            .Select(position => position == 0 ? "login" : "cancelprobe" + position.ToString(CultureInfo.InvariantCulture))
            .Append("cancelprobe-after-pass")
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        var query = new S8T5CancelOnSecondPassTokens(tokens, cancellation);
        var observed = false;
        try
        {
            foreach (var batch in index.StreamSearchCandidates(
                         scope, query, batchSize: 64, includeTemporalRow: true, cancellationToken: cancellation.Token))
            {
                _ = batch;
            }
        }
        catch (OperationCanceledException)
        {
            observed = true;
        }

        Require(observed && cancellation.IsCancellationRequested,
            "The cancellation boundary did not interrupt after the first term pass had touched real candidates.");
        var afterCancellation = S8T5Stream(index, scope, new[] { "login" });
        Require(afterCancellation.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
            "Cancellation left touched query state in the next query (selected=" + string.Join(",", afterCancellation) + ").");
    }

    private static void S8T5AssertNonceCounterCannotWrap()
    {
        var sequence = new PrivateIndexNonceSequence(new byte[8], uint.MaxValue);
        var last = sequence.Next();
        Require(last.Length == 12 && last[8] == byte.MaxValue && last[9] == byte.MaxValue &&
                last[10] == byte.MaxValue && last[11] == byte.MaxValue,
            "The nonce sequence did not issue the last counter value in big-endian order.");

        var refused = false;
        try
        {
            sequence.Next();
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Require(refused, "The nonce sequence reused counter zero after exhausting its four-byte counter.");
    }

    /// <summary>
    /// The query terms each streamed candidate holds. The terms are what the
    /// ranking scores the candidate by, so a candidate that holds none of the
    /// query's terms is dropped by the ranking, exactly as before.
    /// </summary>
    private static List<string[]> S8T5MatchedTerms(
        IPrivateIndexStore index,
        IReadOnlyList<ScopeResourceRevision> scope,
        IReadOnlyList<string> tokens)
    {
        var matched = new List<string[]>();
        foreach (var batch in index.StreamSearchCandidates(scope, tokens, 64, includeTemporalRow: true))
        {
            foreach (var candidate in batch.Candidates)
            {
                matched.Add(candidate.Document.Tokens);
            }
        }

        return matched;
    }

    /// <summary>
    /// The index file on disk holds ciphertext: no resource ID, no token text
    /// and no title of the owner's own records is readable in it.
    /// </summary>
    private static void S8T5AssertIndexIsEncryptedAtRest(string root, EncryptedPrivateIndexStore index)
    {
        var path = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
        Require(File.Exists(path), "The published private index file does not exist.");
        var bytes = File.ReadAllBytes(path);
        Require(bytes.Length > 0, "The published private index file is empty.");
        foreach (var secret in new[] { "s8t5-index-alpha", "login", "recovery", "Quyet dinh" })
        {
            var plaintext = Encoding.UTF8.GetBytes(secret);
            Require(!S8T5Contains(bytes, plaintext),
                "The private index file carries the plaintext of a private value (" + secret + ").");
        }
    }

    /// <summary>
    /// The index directory holds the published generation and nothing else:
    /// no staging file, no journal, no write-ahead log, no spill file.
    /// </summary>
    private static void S8T5AssertIndexLeavesNoPlaintextArtifact(string root)
    {
        var directory = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName);
        var files = Directory.GetFiles(directory);
        Require(files.Length == 1 &&
                string.Equals(Path.GetFileName(files[0]), EncryptedPrivateIndexStore.PublishedFileName, StringComparison.Ordinal),
            "The private index directory holds more than the published generation (files=" + string.Join(",", Array.ConvertAll(files, Path.GetFileName)) + ").");
    }

    /// <summary>
    /// A tampered index file fails closed: the record's own tag no longer
    /// matches, so the generation cannot be read and nothing is served from
    /// it. The tamper is applied to a copy of the published generation, so
    /// the live one is never touched.
    /// </summary>
    private static void S8T5AssertTamperedIndexFailsClosed(string root, S8T5IndexKeys keys)
    {
        var copy = S8T5CopyGeneration(root, "tampered");
        try
        {
            var path = Path.Combine(copy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
            var tampered = File.ReadAllBytes(path);
            tampered[tampered.Length / 2] ^= 0x40;
            File.WriteAllBytes(path, tampered);

            using var index = new EncryptedPrivateIndexStore(copy, keys);
            var failed = false;
            try
            {
                index.Open();
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException)
            {
                failed = true;
            }

            Require(failed, "A tampered private index file was opened instead of failing closed.");
        }
        finally
        {
            TryDeleteDirectory(copy);
        }
    }

    /// <summary>
    /// A truncated index file fails closed: the generation is incomplete, so
    /// it is not served.
    /// </summary>
    private static void S8T5AssertTruncatedIndexFailsClosed(string root, S8T5IndexKeys keys)
    {
        var copy = S8T5CopyGeneration(root, "truncated");
        try
        {
            var path = Path.Combine(copy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
            var original = File.ReadAllBytes(path);
            File.WriteAllBytes(path, original.AsSpan(0, original.Length / 2).ToArray());

            using var index = new EncryptedPrivateIndexStore(copy, keys);
            var failed = false;
            try
            {
                index.Open();
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException)
            {
                failed = true;
            }

            Require(failed, "A truncated private index file was opened instead of failing closed.");
        }
        finally
        {
            TryDeleteDirectory(copy);
        }
    }

    /// <summary>
    /// A copy of the published generation, in its own vault root, so a
    /// negative test can damage the copy without touching the live one.
    /// </summary>
    private static string S8T5CopyGeneration(string root, string name)
    {
        var copy = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Index-" + name + "-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(copy, EncryptedPrivateIndexStore.IndexDirectoryName);
        Directory.CreateDirectory(directory);
        File.Copy(
            Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName),
            Path.Combine(directory, EncryptedPrivateIndexStore.PublishedFileName));
        return copy;
    }

    /// <summary>
    /// A generation written by a newer build fails closed: this build cannot
    /// read an index format it does not know, and it never partially reads
    /// one and never silently replaces it.
    /// </summary>
    private static void S8T5AssertNewerFormatFailsClosed(string root, S8T5IndexKeys keys)
    {
        var copy = S8T5CopyGeneration(root, "newer-format");
        try
        {
            var path = Path.Combine(copy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
            var bytes = File.ReadAllBytes(path);
            BitConverter.GetBytes(PrivateIndexFormat.CurrentVersion + 1).CopyTo(bytes, 8);
            File.WriteAllBytes(path, bytes);

            using var index = new EncryptedPrivateIndexStore(copy, keys);
            var failed = false;
            try
            {
                index.Open();
            }
            catch (Exception ex) when (ex is InvalidDataException or CryptographicException)
            {
                failed = true;
            }

            Require(failed, "A private index generation of a newer format was opened instead of failing closed.");
        }
        finally
        {
            TryDeleteDirectory(copy);
        }
    }

    /// <summary>
    /// A hostile header length and a hostile record length prefix are both
    /// rejected against the format bound before the engine allocates a buffer.
    /// Each mutation is applied only to a copied synthetic generation.
    /// </summary>
    private static void S8T5AssertMalformedLengthsFailBeforeAllocation(string root, S8T5IndexKeys keys)
    {
        void AssertHeaderMutation(string name, Action<FileStream> mutate, string message)
        {
            var copy = S8T5CopyGeneration(root, name);
            try
            {
                var path = Path.Combine(copy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    mutate(stream);
                }

                S8T5RequireMalformedGenerationRejected(copy, keys, message);
            }
            finally
            {
                TryDeleteDirectory(copy);
            }
        }

        AssertHeaderMutation("malformed-header-magic", stream =>
        {
            stream.Position = 0;
            stream.WriteByte(0);
        }, "A malformed index magic value was not rejected.");
        AssertHeaderMutation("malformed-header-negative-offset", stream =>
        {
            var offset = BitConverter.GetBytes(-1L);
            stream.Position = 12;
            stream.Write(offset);
            CryptographicOperations.ZeroMemory(offset);
        }, "A negative index directory offset was not rejected.");
        AssertHeaderMutation("malformed-header-record-offset", stream =>
        {
            var offset = BitConverter.GetBytes(40L);
            stream.Position = 12;
            stream.Write(offset);
            CryptographicOperations.ZeroMemory(offset);
        }, "A header that pointed at a non-directory record was not rejected.");
        AssertHeaderMutation("malformed-header-file-identity", stream =>
        {
            stream.Position = 24;
            var value = stream.ReadByte();
            stream.Position = 24;
            stream.WriteByte((byte)(value ^ 1));
        }, "A changed index file identity authenticated its directory.");
        AssertHeaderMutation("malformed-header-trailing-data", stream =>
        {
            stream.Position = stream.Length;
            stream.WriteByte(0);
        }, "Trailing data after the authenticated directory record was accepted.");

        var headerCopy = S8T5CopyGeneration(root, "malformed-header-length");
        try
        {
            var path = Path.Combine(headerCopy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Span<byte> invalidLength = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(invalidLength, int.MaxValue);
                stream.Position = 20;
                stream.Write(invalidLength);
            }

            S8T5RequireMalformedGenerationRejected(headerCopy, keys,
                "An index header that claimed a gigantic directory length was not refused before allocation.");
        }
        finally
        {
            TryDeleteDirectory(headerCopy);
        }

        var recordCopy = S8T5CopyGeneration(root, "malformed-record-length");
        try
        {
            var path = Path.Combine(recordCopy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Span<byte> offsetBytes = stackalloc byte[sizeof(long)];
                stream.Position = 12;
                stream.ReadExactly(offsetBytes);
                var directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(offsetBytes);
                Span<byte> invalidLength = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(invalidLength, int.MaxValue);
                stream.Position = directoryOffset;
                stream.Write(invalidLength);
            }

            S8T5RequireMalformedGenerationRejected(recordCopy, keys,
                "A record prefix that claimed a gigantic plaintext was not refused before allocation.");
        }
        finally
        {
            TryDeleteDirectory(recordCopy);
        }
    }

    private static void S8T5RequireMalformedGenerationRejected(string root, S8T5IndexKeys keys, string message)
    {
        using var index = new EncryptedPrivateIndexStore(root, keys);
        var failed = false;
        try
        {
            index.Open();
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException)
        {
            failed = true;
        }

        Require(failed, message);
    }

    /// <summary>
    /// Records from a prior generation with the same canonical digest cannot
    /// authenticate in the replacement generation: the per-generation file
    /// identity is part of every record's AEAD associated data.
    /// </summary>
    private static void S8T5AssertSameDigestRecordSpliceFails(S8T5IndexKeys keys)
    {
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Same-Digest-" + Guid.NewGuid().ToString("N"));
        var document = S8T5IndexDocument(
            "s8t5-same-digest-splice",
            1,
            "same digest generation",
            "same digest record splice protection");
        byte[] oldRecord = Array.Empty<byte>();
        long documentOffset = 0;
        try
        {
            using var index = new EncryptedPrivateIndexStore(root, keys);
            using (var scope = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(document.Row);
                index.InsertSearchDocument(document.Document);
                scope.Commit();
            }

            var path = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
                stream.Position = 40;
                stream.ReadExactly(lengthBytes);
                var resourceLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                documentOffset = 40 + sizeof(int) + 12 + resourceLength + 16;
                stream.Position = documentOffset;
                stream.ReadExactly(lengthBytes);
                var documentLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                oldRecord = new byte[sizeof(int) + 12 + documentLength + 16];
                stream.Position = documentOffset;
                stream.ReadExactly(oldRecord);
            }

            index.Reset();
            using (var scope = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(document.Row);
                index.InsertSearchDocument(document.Document);
                scope.Commit();
            }

            index.Reset();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = documentOffset;
                stream.Write(oldRecord);
            }

            index.Open();
            var failed = false;
            try
            {
                _ = index.SelectSearchDocument(document.Row.ResourceId, document.Row.Revision);
            }
            catch (CryptographicException)
            {
                failed = true;
            }

            Require(failed,
                "A document record from a prior generation with the same canonical digest authenticated after substitution.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldRecord);
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// A rebuild that crashed leaves its staged generation on disk. The staged
    /// file is ciphertext that was never published: the next rebuild removes
    /// it, and the published generation keeps answering in the meantime.
    /// </summary>
    private static void S8T5AssertCrashedRebuildIsReaped(
        string root,
        S8T5IndexKeys keys,
        (PrivateIndexRow Row, SearchIndexDocument Document) alpha,
        (PrivateIndexRow Row, SearchIndexDocument Document) beta,
        (PrivateIndexRow Row, SearchIndexDocument Document) outside)
    {
        var copy = S8T5CopyGeneration(root, "crashed-rebuild");
        var staged = Path.Combine(copy, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.StagingFileName);
        File.WriteAllBytes(staged, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        try
        {
        using (var index = new EncryptedPrivateIndexStore(copy, keys))
        {
            index.Open();
            var selected = S8T5Stream(
                index,
                new[] { new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 1) },
                new[] { "login" });
            Require(selected.SequenceEqual(new[] { "s8t5-index-alpha" }),
                "A crashed rebuild's staged generation disturbed the published generation (selected=" + string.Join(",", selected) + ").");

            using (var scope = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(alpha.Row);
                index.InsertSearchDocument(alpha.Document);
                index.Insert(beta.Row);
                index.InsertSearchDocument(beta.Document);
                index.Insert(outside.Row);
                index.InsertSearchDocument(outside.Document);
                scope.Commit();
            }
        }

        Require(!File.Exists(staged), "The next rebuild did not reap the staged generation of a crashed rebuild.");
        }
        finally
        {
            TryDeleteDirectory(copy);
        }
    }


    /// <summary>
    /// Exercises the actual owner status handler against a real encrypted
    /// synthetic vault. The published file is replaced after materialization,
    /// then the reply and the post-unlock zero-row state are checked at the
    /// consumer boundary without starting the service or an IPC channel.
    /// </summary>
    private static async Task S8T5AssertOwnerIndexStatusUsesMaterializedGenerationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Owner-Index-Status-" + Guid.NewGuid().ToString("N"));
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            using var vault = new VaultLifecycle(new EncryptedVaultStore(root));
            Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                "The synthetic index-status vault was not provisioned.");
            using var index = new EncryptedPrivateIndexStore(root, vault);
            var handler = new ServiceRequestHandler(
                new GrantAuthority(new InMemoryGrantStore()),
                vault,
                new OwnerScopeGrantAuthority(),
                privateIndex: index);
            var owner = AuthenticatedContext.ForOwner();
            using (var operation = vault.TryBeginOperation() ??
                   throw new InvalidOperationException("The synthetic index-status vault did not open an operation."))
            {
                var draft = Encoding.UTF8.GetBytes(S4T2ItemDraft(
                    "lesson",
                    "user",
                    "directWrite",
                    "unverified",
                    "S8-T5 synthetic status item",
                    "A safe in-process item for the owner index status consumer.",
                    DateTimeOffset.UtcNow));
                try
                {
                    _ = new MemoryItemService().Create(
                        owner,
                        "s8t5-status-memory",
                        S8T5IndexZone,
                        draft,
                        operation);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(draft);
                }

                var rebuilt = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(rebuilt.Status == "index_rebuilt" && rebuilt.PrivateIndex is { RowCount: 1, Stale: false },
                    "The real owner index-rebuild handler did not materialize the synthetic canonical row.");
                var expectedCanonicalDigest = S8T5LegacyCanonicalDigest(operation.ListResources());
                Require(string.Equals(rebuilt.PrivateIndex?.CanonicalDigest, expectedCanonicalDigest, StringComparison.Ordinal),
                    "The streaming canonical digest changed the prior resource-set digest encoding.");

                var current = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(current.Status == "index_status" &&
                        current.PrivateIndex is { RowCount: 1, Stale: false } &&
                        current.PrivateIndex.Rows.Length == 1,
                    "The real owner status consumer did not report its materialized row before substitution.");

                index.Reset();
                using (var replacement = index.BeginBulkWrite("s8t5-substituted-generation-digest"))
                {
                    index.Insert(new PrivateIndexRow(
                        "s8t5-substituted-row",
                        S8T5IndexZone,
                        1,
                        1,
                        PrivateIndexRecordKind.Unknown,
                        "unknown",
                        Array.Empty<string>()));
                    replacement.Commit();
                }
                var staleSearch = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.OwnerLibrarySearchOperation,
                        Query: "synthetic",
                        SearchOffset: 0,
                        SearchLimit: 10),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(staleSearch.Status is null && staleSearch.Error == "search_index_stale",
                    "The owner search did not return the stale-generation refusal (status=" +
                    (staleSearch.Status ?? "null") + ", error=" + (staleSearch.Error ?? "none") + ").");


                var stale = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                var currentStatus = current.PrivateIndex;
                var staleStatus = stale.PrivateIndex;
                Require(stale.Status == "index_stale" &&
                        currentStatus is not null &&
                        staleStatus is { RowCount: 1, Stale: true } &&
                        staleStatus.Rows.Length == 1 &&
                        string.Equals(staleStatus.CanonicalDigest, currentStatus.CanonicalDigest, StringComparison.Ordinal),
                    "The owner status reply did not bind staleness to the generation actually held by the engine.");
            }

            var locked = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(locked.Status == "locked" && index.DocumentCount == 0,
                "Locking the vault did not reset the materialized index in the real handler.");
            var lockedSearch = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "synthetic"),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(lockedSearch.Status is null && lockedSearch.Error == "vault_locked",
                "The locked owner search did not return the lock refusal (status=" +
                (lockedSearch.Status ?? "null") + ", error=" + (lockedSearch.Error ?? "none") + ").");


            var unlocked = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerVaultUnlockOperation,
                    Passphrase: passphrase),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(unlocked.Status == "unlocked",
                "The synthetic status vault did not unlock after the reset.");

            using var unlockedOperation = vault.TryBeginOperation() ??
                throw new InvalidOperationException("The unlocked status vault did not open an operation.");
            var empty = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                unlockedOperation,
                CancellationToken.None).ConfigureAwait(false);
            // The substituted generation is authenticated but describes another
            // canonical set: the post-unlock status loads it, refuses it as
            // stale, and publishes no rows from it.
            Require(empty.Status == "index_stale" &&
                    empty.PrivateIndex is { RowCount: 0, Stale: true } &&
                    empty.PrivateIndex.Rows.Length == 0 &&
                    index.DocumentCount == 0,
                "The post-unlock owner status reply served the substituted generation or exposed rows from the prior epoch.");
            Console.WriteLine("PASS actual owner index status handler: current generation, substituted-generation stale reply, and post-unlock stale zero rows");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// S8-T5 lifecycle readiness at the actual handler boundary: a published
    /// generation answers again after lock/unlock without a rebuild, the
    /// encrypted file is not rewritten, concurrent reads share the one load,
    /// scoped search applies the same policy, a canonical move is refused
    /// until the owner rebuilds, and a tampered generation fails closed while
    /// the supported owner rebuild recovers it.
    /// </summary>
    private static async Task S8T5AssertOwnerIndexLifecycleReuseAsync()
    {
        const string loginId = "s8t5-lifecycle-login";
        const string recoveryId = "s8t5-lifecycle-recovery";
        const string extraId = "s8t5-lifecycle-extra";
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Lifecycle-" + Guid.NewGuid().ToString("N"));
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            using var vault = new VaultLifecycle(new EncryptedVaultStore(root));
            Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                "The lifecycle synthetic vault was not provisioned.");
            using var index = new EncryptedPrivateIndexStore(root, vault);
            var handler = S8T5Handler(vault, index);
            var owner = AuthenticatedContext.ForOwner();
            var indexPath = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);

            string beforeLock;
            string fileBefore;
            long bytesBefore;
            using (var operation = vault.TryBeginOperation() ??
                   throw new InvalidOperationException("The lifecycle vault did not open an operation."))
            {
                S8T5CreateMemory(owner, operation, loginId, "Login procedure",
                    "The owner decides that the login procedure keeps the recovery key outside the vault.",
                    DateTimeOffset.UtcNow.AddMinutes(-10));
                S8T5CreateMemory(owner, operation, recoveryId, "Recovery rotation",
                    "The owner remembers that the recovery code is rotated after every login.",
                    DateTimeOffset.UtcNow.AddMinutes(-5));

                var rebuilt = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(rebuilt.Status == "index_rebuilt" && rebuilt.PrivateIndex is { RowCount: 2, Stale: false },
                    "The lifecycle owner index rebuild did not materialize both canonical rows (" + S8T5DescribeIndex(rebuilt) + ").");

                var first = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(first.Status == "library_page" && first.Library is { Total: 2 },
                    "The first lifecycle owner search did not answer the built generation (" + S8T5DescribeLibrary(first) + ").");
                beforeLock = S8T5PageSignature(first.Library!);
                fileBefore = S8T5FileSha256(indexPath);
                bytesBefore = new FileInfo(indexPath).Length;
            }

            // The lock drains active operations, so it is issued after this
            // operation lease is disposed instead of holding it across the wait.
            var locked = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(locked.Status == "locked" && index.DocumentCount == 0 && !index.HasOpenGeneration,
                "The lifecycle lock did not clear the resident generation (rows=" +
                index.DocumentCount.ToString(CultureInfo.InvariantCulture) + ").");

            var lockedSearch = await S8T5OwnerSearchAsync(handler, owner, operation: null, "login").ConfigureAwait(false);
            Require(lockedSearch.Status is null && lockedSearch.Error == "vault_locked",
                "A locked lifecycle owner search was not refused as vault_locked (" + S8T5DescribeLibrary(lockedSearch) + ").");

            var unlocked = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerVaultUnlockOperation,
                    Passphrase: passphrase),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(unlocked.Status == "unlocked", "The lifecycle vault did not unlock again (" + (unlocked.Error ?? "none") + ").");

            using (var operation = vault.TryBeginOperation() ??
                   throw new InvalidOperationException("The re-unlocked lifecycle vault did not open an operation."))
            {
                var reused = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(reused.Status == "library_page" && reused.Library is { Total: 2 } &&
                        string.Equals(S8T5PageSignature(reused.Library!), beforeLock, StringComparison.Ordinal),
                    "A reopened unchanged generation did not answer the same page without a rebuild (" +
                    S8T5DescribeLibrary(reused) + ").");
                Require(string.Equals(S8T5FileSha256(indexPath), fileBefore, StringComparison.Ordinal) &&
                        new FileInfo(indexPath).Length == bytesBefore,
                    "The read-only reopen rewrote the published generation.");

                var status = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(status.Status == "index_status" && status.PrivateIndex is { RowCount: 2, Stale: false },
                    "The reopened status did not report the loaded generation (" + S8T5DescribeIndex(status) + ").");

                var concurrent = await Task.WhenAll(Enumerable.Range(0, 4)
                    .Select(_ => S8T5OwnerSearchAsync(handler, owner, operation, "login"))).ConfigureAwait(false);
                Require(concurrent.All(reply => reply.Status == "library_page" && reply.Library is { Total: 2 }),
                    "Concurrent owner searches did not all read the one loaded generation.");
                Require(string.Equals(S8T5FileSha256(indexPath), fileBefore, StringComparison.Ordinal),
                    "Concurrent reads rewrote the published generation.");

                var preview = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.OwnerScopePreviewOperation,
                        ResourceIds: new[] { loginId, recoveryId },
                        Operations: new[]
                        {
                            ServiceRequestHandler.MemorySearchAccessOperation,
                            ServiceRequestHandler.MemoryReadOperation
                        },
                        ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10)),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(preview.Status == "scope_preview" && preview.ScopePreview is not null,
                    "The lifecycle owner scope preview did not freeze the reused generation's resources.");
                var issued = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.OwnerScopeIssueOperation,
                        PreviewId: preview.ScopePreview!.PreviewId),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(issued.Status == "token_issued" && issued.Token is not null,
                    "The lifecycle owner scope issue did not grant the reused generation's resources.");
                var agent = AuthenticatedContext.ForAgent("s8t5-lifecycle-agent", "s8t5-lifecycle-session");
                var redeemed = await handler.HandleAsync(
                    agent,
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.AgentRedeemOperation,
                        Token: issued.Token),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(redeemed.Status == "session_issued" && redeemed.SessionToken is not null,
                    "The lifecycle agent redeem did not freeze the approved scope.");
                var sessionToken = redeemed.SessionToken!;
                async Task<ServiceReply> AgentSearch(string? query) =>
                    await handler.HandleAsync(
                        agent,
                        new ServiceRequest(
                            ServiceRequestHandler.CurrentProtocolVersion,
                            ServiceRequestHandler.AgentMemorySearchOperation,
                            Query: query,
                            SearchOffset: 0,
                            SearchLimit: 10,
                            SessionToken: sessionToken,
                            AccessOperation: ServiceRequestHandler.MemorySearchAccessOperation),
                        operation,
                        CancellationToken.None).ConfigureAwait(false);

                var scoped = await AgentSearch("login").ConfigureAwait(false);
                Require(scoped.Status == "memory_search" && scoped.MemorySearch is { Total: 2 },
                    "The scoped search did not load the same reused generation (status=" +
                    (scoped.Status ?? "null") + ", error=" + (scoped.Error ?? "none") + ").");

                // A canonical move is refused, not silently served from the
                // generation the vault has moved past. The one supported
                // recovery remains the explicit owner rebuild.
                S8T5CreateMemory(owner, operation, extraId, "Login audit",
                    "The owner records that every login writes one audit line.", DateTimeOffset.UtcNow);
                var staleOwner = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(staleOwner.Status is null && staleOwner.Error == "search_index_stale",
                    "A canonical addition was not refused as search_index_stale (" + S8T5DescribeLibrary(staleOwner) + ").");
                var staleAgent = await AgentSearch("login").ConfigureAwait(false);
                Require(staleAgent.Status == "denied" && staleAgent.Error == "search_index_stale",
                    "The scoped search did not refuse a generation the canonical set moved past (status=" +
                    (staleAgent.Status ?? "null") + ", error=" + (staleAgent.Error ?? "none") + ").");
                var staleStatus = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(staleStatus.Status == "index_stale" && staleStatus.PrivateIndex is { Stale: true },
                    "The owner status did not report the moved generation as stale (" + S8T5DescribeIndex(staleStatus) + ").");

                var rebuiltAgain = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(rebuiltAgain.Status == "index_rebuilt" && rebuiltAgain.PrivateIndex is { RowCount: 3, Stale: false },
                    "The explicit owner rebuild did not publish the changed canonical set (" + S8T5DescribeIndex(rebuiltAgain) + ").");
                var afterRebuild = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(afterRebuild.Status == "library_page" && afterRebuild.Library is { Total: 3 },
                    "The rebuilt generation did not serve the added record (" + S8T5DescribeLibrary(afterRebuild) + ").");

                // A revised record moves the generation as well: the old
                // generation is refused, and the rebuild serves the new
                // revision instead of the superseded one.
                var revisionDraft = Encoding.UTF8.GetBytes(S4T2ItemDraft(
                    "lesson",
                    "user",
                    "directWrite",
                    "unverified",
                    "Login procedure revised",
                    "The owner decides that the login procedure also requires a second factor.",
                    DateTimeOffset.UtcNow));
                try
                {
                    _ = new MemoryItemService().Revise(owner, loginId, S8T5IndexZone, 1, revisionDraft, operation);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(revisionDraft);
                }

                var staleRevision = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(staleRevision.Status is null && staleRevision.Error == "search_index_stale",
                    "A revised record was not refused as search_index_stale (" + S8T5DescribeLibrary(staleRevision) + ").");
                var rebuiltRevision = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(rebuiltRevision.Status == "index_rebuilt" && rebuiltRevision.PrivateIndex is { RowCount: 3, Stale: false },
                    "The explicit owner rebuild did not publish the revised canonical set (" + S8T5DescribeIndex(rebuiltRevision) + ").");
                var revisedPage = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(revisedPage.Status == "library_page" &&
                        revisedPage.Library is { Total: 3 } &&
                        revisedPage.Library.Hits.SingleOrDefault(hit => hit.Id == loginId) is { Revision: 2 },
                    "The rebuilt generation did not serve the revised record at its new revision (" + S8T5DescribeLibrary(revisedPage) + ").");

                // A deleted canonical record moves the generation too. The
                // explicit purge deletes the record; the generation is then
                // refused until the owner rebuilds over the smaller set.
                var purgePreview = new PurgeService().Preview(
                    owner, extraId, 1, "Synthetic lifecycle delete.", operation);
                var purged = new PurgeService().Confirm(
                    owner, purgePreview, "PURGE", "Synthetic lifecycle delete.", operation, invalidateIndex: null);
                Require(purged.Deleted.Any(item => string.Equals(item.ResourceId, extraId, StringComparison.Ordinal)),
                    "The synthetic lifecycle purge did not delete the added canonical record.");
                var staleDelete = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(staleDelete.Status is null && staleDelete.Error == "search_index_stale",
                    "A deleted record was not refused as search_index_stale (" + S8T5DescribeLibrary(staleDelete) + ").");
                var rebuiltDelete = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(rebuiltDelete.Status == "index_rebuilt" && rebuiltDelete.PrivateIndex is { RowCount: 2, Stale: false },
                    "The explicit owner rebuild did not publish the smaller canonical set (" + S8T5DescribeIndex(rebuiltDelete) + ").");
                var afterDelete = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(afterDelete.Status == "library_page" &&
                        afterDelete.Library is { Total: 2 } &&
                        afterDelete.Library.Hits.All(hit => !string.Equals(hit.Id, extraId, StringComparison.Ordinal)),
                    "The rebuilt generation still served the deleted record (" + S8T5DescribeLibrary(afterDelete) + ").");

                // A canonical write concurrent with authenticated loads must
                // leave either a whole current answer or a clean stale
                // refusal; it must never crash or publish a partial state.
                const string raceId = "s8t5-lifecycle-race";
                var raceWrite = Task.Run(() =>
                {
                    using var writeOperation = vault.TryBeginOperation() ??
                        throw new InvalidOperationException("The race write vault did not open an operation.");
                    S8T5CreateMemory(owner, writeOperation, raceId, "Race write",
                        "The owner records a concurrent login note.", DateTimeOffset.UtcNow);
                });
                var raceReads = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
                {
                    using var readOperation = vault.TryBeginOperation() ??
                        throw new InvalidOperationException("The race read vault did not open an operation.");
                    return await S8T5OwnerSearchAsync(handler, owner, readOperation, "login").ConfigureAwait(false);
                })).ToArray();
                await Task.WhenAll(raceReads.Cast<Task>().Append(raceWrite)).ConfigureAwait(false);
                Require(raceReads.All(task =>
                        task.Result.Status == "library_page" || task.Result.Error == "search_index_stale"),
                    "A read concurrent with a canonical write neither answered nor refused cleanly.");
                var afterRaceStatus = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(afterRaceStatus.Status is "index_status" or "index_stale",
                    "The status after a concurrent write was neither current nor stale (" + S8T5DescribeIndex(afterRaceStatus) + ").");
                var rebuiltRace = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(rebuiltRace.Status == "index_rebuilt" && rebuiltRace.PrivateIndex is { RowCount: 3, Stale: false },
                    "The explicit owner rebuild did not publish the concurrent write (" + S8T5DescribeIndex(rebuiltRace) + ").");
                var afterRace = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(afterRace.Status == "library_page" && afterRace.Library is { Total: 3 } &&
                        afterRace.Library.Hits.Any(hit => string.Equals(hit.Id, raceId, StringComparison.Ordinal)),
                    "The rebuilt generation did not serve the concurrent write (" + S8T5DescribeLibrary(afterRace) + ").");
            }

            // The lock drains active operations: it runs after the lease above
            // is disposed, never while this test still holds it.
            var lockedAgain = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(lockedAgain.Status == "locked", "The lifecycle vault did not lock before the tamper scenario.");

            // An authenticated tamper fails closed through the same lifecycle
            // load; the derivative stays replaceable by the explicit rebuild.
            var corrupt = File.ReadAllBytes(indexPath);
            corrupt[corrupt.Length - 1] ^= 0x5A;
            File.WriteAllBytes(indexPath, corrupt);
            var unlockedAfterTamper = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerVaultUnlockOperation,
                    Passphrase: passphrase),
                operation: null,
                CancellationToken.None).ConfigureAwait(false);
            Require(unlockedAfterTamper.Status == "unlocked", "The lifecycle vault did not unlock for the tamper scenario.");
            using (var operation = vault.TryBeginOperation() ??
                   throw new InvalidOperationException("The tampered lifecycle vault did not open an operation."))
            {
                var tampered = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(tampered.Status is null && tampered.Error == "index_unavailable",
                    "A tampered published generation was not refused as index_unavailable (" + S8T5DescribeLibrary(tampered) + ").");
                var tamperedStatus = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(tamperedStatus.Error == "index_unavailable",
                    "The owner status did not fail closed on a tampered generation (" + S8T5DescribeIndex(tamperedStatus) + ").");

                var recovered = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
                Require(recovered.Status == "index_rebuilt" && recovered.PrivateIndex is { RowCount: 3, Stale: false },
                    "The supported owner rebuild did not recover from a tampered generation (" + S8T5DescribeIndex(recovered) + ").");
                var recoveredSearch = await S8T5OwnerSearchAsync(handler, owner, operation, "login").ConfigureAwait(false);
                Require(recoveredSearch.Status == "library_page" && recoveredSearch.Library is { Total: 3 },
                    "The recovered generation did not answer after the explicit rebuild (" + S8T5DescribeLibrary(recoveredSearch) + ").");
            }

            // A generation opened with another key is unreadable, never
            // trusted or reported as an empty index.
            var wrongKeyRoot = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-WrongKey-" + Guid.NewGuid().ToString("N"));
            try
            {
                var keysA = new S8T5IndexKeys();
                var keysB = new S8T5IndexKeys();
                var resources = new[] { new ScopeResourceRevision("s8t5-wrong-key-row", S8T5IndexZone, 1) };
                using (var writer = new EncryptedPrivateIndexStore(wrongKeyRoot, keysA))
                {
                    using var scope = writer.BeginBulkWrite(S8T5LegacyCanonicalDigest(resources));
                    writer.Insert(new PrivateIndexRow(
                        "s8t5-wrong-key-row",
                        S8T5IndexZone,
                        1,
                        1,
                        PrivateIndexRecordKind.MemoryItem,
                        "lesson",
                        Array.Empty<string>()));
                    scope.Commit();
                }

                using var reader = new EncryptedPrivateIndexStore(wrongKeyRoot, keysB);
                var wrongKey = new PrivateIndexAuthority().Load(reader, resources);
                Require(wrongKey.Status == PrivateIndexLoadStatus.Unreadable && wrongKey.Snapshot is null,
                    "A generation opened with a different key did not fail closed (status=" + wrongKey.Status + ").");
                Console.WriteLine("PASS a published generation opened with a different key fails closed");
            }
            finally
            {
                TryDeleteDirectory(wrongKeyRoot);
            }

            Console.WriteLine("PASS actual owner/scoped lifecycle: unchanged generation reused after lock/unlock without rewrite, scoped parity, stale refusal, tamper fail-closed, and explicit rebuild recovery");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// S8-T5 empty and missing generation behavior: a valid empty generation
    /// loads and answers an empty page across lock/unlock, while a missing
    /// generation over a non-empty canonical set is refused until the owner
    /// rebuilds (and an empty canonical set reports a truthful empty status).
    /// </summary>
    private static async Task S8T5AssertEmptyAndMissingIndexAsync()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Empty-" + Guid.NewGuid().ToString("N"));
        var missingRoot = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Missing-" + Guid.NewGuid().ToString("N"));
        const string passphrase = "s8t5-empty-missing-synthetic-passphrase";
        try
        {
            using (var vault = new VaultLifecycle(new EncryptedVaultStore(emptyRoot)))
            {
                Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                    "The empty lifecycle vault was not provisioned.");
                using var index = new EncryptedPrivateIndexStore(emptyRoot, vault);
                var handler = S8T5Handler(vault, index);
                var owner = AuthenticatedContext.ForOwner();
                var indexPath = Path.Combine(emptyRoot, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
                string fileHash;
                using (var operation = vault.TryBeginOperation() ??
                       throw new InvalidOperationException("The empty lifecycle vault did not open an operation."))
                {
                    var beforeBuild = await S8T5OwnerSearchAsync(handler, owner, operation, string.Empty).ConfigureAwait(false);
                    Require(beforeBuild.Status is null && beforeBuild.Error == "index_not_materialized",
                        "An empty vault without a generation did not ask for an explicit rebuild (" + S8T5DescribeLibrary(beforeBuild) + ").");
                    var emptyStatus = await handler.HandleAsync(
                        owner,
                        new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                        operation,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(emptyStatus.Status == "index_status" && emptyStatus.PrivateIndex is { RowCount: 0, Stale: false },
                        "An empty canonical set without a generation did not report a truthful empty status (" + S8T5DescribeIndex(emptyStatus) + ").");

                    var rebuilt = await handler.HandleAsync(
                        owner,
                        new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                        operation,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(rebuilt.Status == "index_rebuilt" && rebuilt.PrivateIndex is { RowCount: 0, Stale: false },
                        "The empty generation did not materialize as an empty current generation (" + S8T5DescribeIndex(rebuilt) + ").");
                    var browse = await S8T5OwnerSearchAsync(handler, owner, operation, string.Empty).ConfigureAwait(false);
                    Require(browse.Status == "library_page" && browse.Library is { Total: 0 },
                        "A valid empty generation did not answer an empty page (" + S8T5DescribeLibrary(browse) + ").");
                    fileHash = S8T5FileSha256(indexPath);
                }

                var locked = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
                    operation: null,
                    CancellationToken.None).ConfigureAwait(false);
                Require(locked.Status == "locked", "The empty lifecycle vault did not lock.");

                var unlocked = await handler.HandleAsync(
                    owner,
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.OwnerVaultUnlockOperation,
                        Passphrase: passphrase),
                    operation: null,
                    CancellationToken.None).ConfigureAwait(false);
                Require(unlocked.Status == "unlocked", "The empty lifecycle vault did not unlock again.");
                using (var operation = vault.TryBeginOperation() ??
                       throw new InvalidOperationException("The re-unlocked empty lifecycle vault did not open an operation."))
                {
                    var reusedEmpty = await S8T5OwnerSearchAsync(handler, owner, operation, string.Empty).ConfigureAwait(false);
                    Require(reusedEmpty.Status == "library_page" && reusedEmpty.Library is { Total: 0 },
                        "The reopened empty generation did not answer an empty page without a rebuild (" + S8T5DescribeLibrary(reusedEmpty) + ").");
                    var status = await handler.HandleAsync(
                        owner,
                        new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                        operation,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(status.Status == "index_status" && status.PrivateIndex is { RowCount: 0, Stale: false },
                        "The reopened empty status did not report an empty current generation (" + S8T5DescribeIndex(status) + ").");
                    Require(string.Equals(S8T5FileSha256(indexPath), fileHash, StringComparison.Ordinal),
                        "The empty generation was rewritten by the reopen.");
                }

                Console.WriteLine("PASS a valid empty generation loads across lock/unlock and answers an empty page without a rewrite");
            }

            using (var vault = new VaultLifecycle(new EncryptedVaultStore(missingRoot)))
            {
                Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                    "The missing-generation vault was not provisioned.");
                using var index = new EncryptedPrivateIndexStore(missingRoot, vault);
                var handler = S8T5Handler(vault, index);
                var owner = AuthenticatedContext.ForOwner();
                using (var operation = vault.TryBeginOperation() ??
                       throw new InvalidOperationException("The missing-generation vault did not open an operation."))
                {
                    S8T5CreateMemory(owner, operation, "s8t5-missing-row", "Missing generation",
                        "The owner records that this canonical item has no generation yet.", DateTimeOffset.UtcNow);
                    var missing = await S8T5OwnerSearchAsync(handler, owner, operation, "missing").ConfigureAwait(false);
                    Require(missing.Status is null && missing.Error == "index_not_materialized",
                        "A missing generation over a non-empty canonical set was not refused as index_not_materialized (" +
                        S8T5DescribeLibrary(missing) + ").");
                    var status = await handler.HandleAsync(
                        owner,
                        new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                        operation,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(status.Status == "index_stale" && status.PrivateIndex is { RowCount: 0, Stale: true },
                        "A missing generation over a non-empty canonical set did not report stale (" + S8T5DescribeIndex(status) + ").");
                    var rebuilt = await handler.HandleAsync(
                        owner,
                        new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                        operation,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(rebuilt.Status == "index_rebuilt" && rebuilt.PrivateIndex is { RowCount: 1, Stale: false },
                        "The explicit owner rebuild did not publish the missing generation (" + S8T5DescribeIndex(rebuilt) + ").");
                    var search = await S8T5OwnerSearchAsync(handler, owner, operation, "missing").ConfigureAwait(false);
                    Require(search.Status == "library_page" && search.Library is { Total: 1 },
                        "The rebuilt generation did not serve the canonical item (" + S8T5DescribeLibrary(search) + ").");
                }

                Console.WriteLine("PASS a missing generation is refused truthfully and the explicit owner rebuild recovers it");
            }
        }
        finally
        {
            TryDeleteDirectory(emptyRoot);
            TryDeleteDirectory(missingRoot);
        }
    }

    private static ServiceRequestHandler S8T5Handler(VaultLifecycle vault, IPrivateIndexStore index) =>
        new(
            new GrantAuthority(new InMemoryGrantStore()),
            vault,
            new OwnerScopeGrantAuthority(),
            privateIndex: index);

    private static Task<ServiceReply> S8T5OwnerSearchAsync(
        ServiceRequestHandler handler,
        AuthenticatedContext owner,
        IVaultOperation? operation,
        string query) =>
        handler.HandleAsync(
            owner,
            new ServiceRequest(
                ServiceRequestHandler.CurrentProtocolVersion,
                ServiceRequestHandler.OwnerLibrarySearchOperation,
                Query: query,
                SearchOffset: 0,
                SearchLimit: 10),
            operation,
            CancellationToken.None);

    private static void S8T5CreateMemory(
        AuthenticatedContext owner,
        IManagedResourceStore operation,
        string resourceId,
        string title,
        string body,
        DateTimeOffset occurred)
    {
        var draft = Encoding.UTF8.GetBytes(S4T2ItemDraft(
            "lesson",
            "user",
            "directWrite",
            "unverified",
            title,
            body,
            occurred));
        try
        {
            _ = new MemoryItemService().Create(owner, resourceId, S8T5IndexZone, draft, operation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(draft);
        }
    }

    private static string S8T5PageSignature(LibraryPageWire page) =>
        string.Join("|", page.Hits.Select(hit =>
            hit.Id + "@" + hit.Revision.ToString(CultureInfo.InvariantCulture) + ":" +
            hit.Score.ToString("R", CultureInfo.InvariantCulture) + ":" + hit.Publication));

    private static string S8T5DescribeLibrary(ServiceReply reply) =>
        "status=" + (reply.Status ?? "null") + " error=" + (reply.Error ?? "none") +
        " total=" + (reply.Library?.Total.ToString(CultureInfo.InvariantCulture) ?? "none");

    private static string S8T5DescribeIndex(ServiceReply reply) =>
        "status=" + (reply.Status ?? "null") + " error=" + (reply.Error ?? "none") +
        " rows=" + (reply.PrivateIndex?.RowCount.ToString(CultureInfo.InvariantCulture) ?? "none") +
        " stale=" + (reply.PrivateIndex?.Stale.ToString() ?? "none");

    private static string S8T5FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private const string S8T5LifecyclePassphrase = "s8t5-lifecycle-fresh-process-passphrase-01";

    /// <summary>
    /// S8-T5 fresh-process lifecycle phases. `build` creates a synthetic
    /// encrypted vault and one generation, then locks the vault and leaves the
    /// fixture. `reopen` runs in a new process against the same fixture: it
    /// unlocks, loads the authenticated generation without a rebuild, answers
    /// the same page, leaves the encrypted file byte-identical, and locks
    /// again.
    /// </summary>
    internal static async Task RunS8T5LifecyclePhaseAsync(string phase, string root)
    {
        if (string.Equals(phase, "build", StringComparison.Ordinal))
        {
            await S8T5BuildLifecycleFixtureAsync(root).ConfigureAwait(false);
            return;
        }

        if (string.Equals(phase, "reopen", StringComparison.Ordinal))
        {
            await S8T5ReopenLifecycleFixtureAsync(root).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("Unknown S8-T5 lifecycle phase: " + phase);
    }

    private static async Task S8T5BuildLifecycleFixtureAsync(string root)
    {
        const string loginId = "s8t5-restart-login";
        const string recoveryId = "s8t5-restart-recovery";
        using var vault = new VaultLifecycle(new EncryptedVaultStore(root));
        Require(vault.Provision(S8T5LifecyclePassphrase, out var recoveryCode) && recoveryCode is not null,
            "The fresh-process lifecycle vault was not provisioned.");
        using var index = new EncryptedPrivateIndexStore(root, vault);
        var handler = S8T5Handler(vault, index);
        var owner = AuthenticatedContext.ForOwner();
        var indexPath = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
        var statePath = Path.Combine(root, "s8t5-lifecycle-state.txt");
        string signature;
        using (var operation = vault.TryBeginOperation() ??
               throw new InvalidOperationException("The fresh-process lifecycle vault did not open an operation."))
        {
            S8T5CreateMemory(owner, operation, loginId, "Restart login",
                "The owner decides that the restart keeps the login procedure intact.", DateTimeOffset.UtcNow.AddMinutes(-10));
            S8T5CreateMemory(owner, operation, recoveryId, "Restart recovery",
                "The owner remembers that the recovery code survives a restart.", DateTimeOffset.UtcNow.AddMinutes(-5));
            var rebuilt = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(rebuilt.Status == "index_rebuilt" && rebuilt.PrivateIndex is { RowCount: 2, Stale: false },
                "The fresh-process fixture rebuild did not materialize both rows (" + S8T5DescribeIndex(rebuilt) + ").");
            var first = await S8T5OwnerSearchAsync(handler, owner, operation, "restart").ConfigureAwait(false);
            Require(first.Status == "library_page" && first.Library is { Total: 2 },
                "The fresh-process fixture search did not answer the built generation (" + S8T5DescribeLibrary(first) + ").");
            signature = S8T5PageSignature(first.Library!);
        }

        // The vault lock drains active operations, so it is issued after the
        // operation lease is disposed.
        var locked = await handler.HandleAsync(
            owner,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
            operation: null,
            CancellationToken.None).ConfigureAwait(false);
        Require(locked.Status == "locked", "The fresh-process fixture did not lock.");

        var indexHash = S8T5FileSha256(indexPath);
        File.WriteAllLines(statePath, new[]
        {
            indexHash,
            new FileInfo(indexPath).Length.ToString(CultureInfo.InvariantCulture),
            signature
        });
        Console.WriteLine("PASS fresh-process fixture built and locked: rows=2 index=" + indexHash);
    }

    private static async Task S8T5ReopenLifecycleFixtureAsync(string root)
    {
        var indexPath = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.PublishedFileName);
        var statePath = Path.Combine(root, "s8t5-lifecycle-state.txt");
        Require(File.Exists(statePath) && File.Exists(indexPath),
            "The fresh-process fixture state or published generation is missing.");
        var state = File.ReadAllLines(statePath);
        Require(state.Length == 3, "The fresh-process fixture state is malformed.");
        var indexHash = state[0];
        var indexBytes = long.Parse(state[1], CultureInfo.InvariantCulture);
        Require(string.Equals(S8T5FileSha256(indexPath), indexHash, StringComparison.Ordinal) &&
                new FileInfo(indexPath).Length == indexBytes,
            "The published generation changed before the fresh process opened it.");
        using var vault = new VaultLifecycle(new EncryptedVaultStore(root));
        Require(vault.Unlock(S8T5LifecyclePassphrase, useRecoveryCode: false),
            "The fresh process could not unlock the synthetic vault.");
        using var index = new EncryptedPrivateIndexStore(root, vault);
        var handler = S8T5Handler(vault, index);
        var owner = AuthenticatedContext.ForOwner();
        using (var operation = vault.TryBeginOperation() ??
               throw new InvalidOperationException("The fresh-process lifecycle vault did not open an operation."))
        {
            var page = await S8T5OwnerSearchAsync(handler, owner, operation, "restart").ConfigureAwait(false);
            Require(page.Status == "library_page" && page.Library is { Total: 2 } &&
                    string.Equals(S8T5PageSignature(page.Library!), state[2], StringComparison.Ordinal),
                "A fresh process did not reuse the authenticated generation without a rebuild (" + S8T5DescribeLibrary(page) + ").");
            var status = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexStatusOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(status.Status == "index_status" && status.PrivateIndex is { RowCount: 2, Stale: false },
                "The fresh-process status did not report the loaded generation (" + S8T5DescribeIndex(status) + ").");
            Require(string.Equals(S8T5FileSha256(indexPath), indexHash, StringComparison.Ordinal) &&
                    new FileInfo(indexPath).Length == indexBytes,
                "The fresh-process reads rewrote the published generation.");
        }

        // The vault lock drains active operations, so it is issued after the
        // reopen operation lease is disposed.
        var locked = await handler.HandleAsync(
            owner,
            new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerVaultLockOperation),
            operation: null,
            CancellationToken.None).ConfigureAwait(false);
        Require(locked.Status == "locked" && index.DocumentCount == 0,
            "The fresh process did not clear the generation on lock.");

        Console.WriteLine("PASS fresh-process restart unlocked and reused the authenticated generation without a rebuild or rewrite");
    }

    private static string S8T5LegacyCanonicalDigest(IReadOnlyList<ScopeResourceRevision> resources)
    {
        var builder = new StringBuilder("MetaBrain|private-index|v1");
        foreach (var resource in resources.OrderBy(entry => entry.ResourceId, StringComparer.Ordinal))
        {
            builder.Append('|')
                .Append(resource.ResourceId)
                .Append('@')
                .Append(resource.Revision.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLower(CultureInfo.InvariantCulture);
    }

    private static void S8T5WriteHandlerDump(
        string phase,
        ServiceReply ownerStatus,
        ServiceReply ownerLibrary,
        ServiceReply scopedSearch)
    {
        Console.WriteLine("S8T5_HANDLER_DUMP " + JsonSerializer.Serialize(new
        {
            phase,
            ownerStatus = new
            {
                ownerStatus.Status,
                ownerStatus.PrivateIndex
            },
            ownerLibrary = new
            {
                ownerLibrary.Status,
                ownerLibrary.Library
            },
            scopedSearch = new
            {
                scopedSearch.Status,
                scopedSearch.Error,
                scopedSearch.MemorySearch
            }
        }));
    }

    private static async Task S8T5AssertScopedAgentSearchDifferentialAsync()
    {
        const string zoneId = "s8t5-search-zone";
        const string alphaId = "s8t5-search-alpha";
        const string withdrawnId = "s8t5-search-withdrawn";
        const string rejectedId = "s8t5-search-rejected";
        const string archivedId = "s8t5-search-archived";
        const string tieAId = "s8t5-search-tie-a";
        const string tieBId = "s8t5-search-tie-b";
        const string supersededId = "s8t5-search-superseded";
        const string replacementId = "s8t5-search-replacement";
        const string purgeId = "s8t5-search-purge-target";
        const string outsideId = "s8t5-search-outside";
        const string outsideZoneId = "s8t5-search-outside-zone";
        const string unknownId = "s8t5-search-unknown";

        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Agent-Search-" + Guid.NewGuid().ToString("N"));
        var backupPath = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Agent-Search-" + Guid.NewGuid().ToString("N") + ".mbbackup");
        var restoredRoot = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Agent-Search-Restore-" + Guid.NewGuid().ToString("N"));
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            var vaultStore = new EncryptedVaultStore(root);
            using var vault = new VaultLifecycle(vaultStore);
            Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                "The synthetic scoped-search vault was not provisioned.");
            using var index = new EncryptedPrivateIndexStore(root, vault);
            var handler = new ServiceRequestHandler(
                new GrantAuthority(new InMemoryGrantStore()),
                vault,
                new OwnerScopeGrantAuthority(),
                privateIndex: index);
            var owner = AuthenticatedContext.ForOwner();
            using var operation = vault.TryBeginOperation() ??
                throw new InvalidOperationException("The synthetic scoped-search vault did not open an operation.");
            var occurred = DateTimeOffset.UtcNow.AddDays(-2);
            var recorded = DateTimeOffset.UtcNow.AddMinutes(-1);

            string Draft(
                string resourceId,
                string kind,
                string level,
                string title,
                string body,
                bool includeProvenance = false)
            {
                object[] sources = includeProvenance
                    ? new object[]
                    {
                        new
                        {
                            sourceId = "s8t5-unknown-provenance-source",
                            locator = "synthetic://search-proof",
                            availability = "unknown"
                        }
                    }
                    : Array.Empty<object>();
                object? applicability = string.Equals(resourceId, alphaId, StringComparison.Ordinal)
                    ? new { project = "search-project", environment = "lab", version = "v1" }
                    : null;
                return JsonSerializer.Serialize(new
                {
                    kind,
                    author = "user",
                    creationMethod = "directWrite",
                    evidence = "unverified",
                    level,
                    title,
                    body,
                    sources,
                    occurredAtUtc = occurred,
                    recordedAtUtc = recorded,
                    applicability
                });
            }

            MemoryItem CreateMemory(
                string resourceId,
                string kind,
                string level,
                string title,
                string body,
                bool includeProvenance = false,
                string? zoneOverride = null)
            {
                var draft = Encoding.UTF8.GetBytes(Draft(resourceId, kind, level, title, body, includeProvenance));
                try
                {
                    return new MemoryItemService().Create(owner, resourceId, zoneOverride ?? zoneId, draft, operation);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(draft);
                }
            }

            LifecycleService lifecycle = new();
            LifecycleRecord CreateLifecycle(string resourceId)
            {
                var draft = Encoding.UTF8.GetBytes(S2T5LifecycleDraft("Synthetic lifecycle for scoped index search."));
                try
                {
                    return lifecycle.Create(owner, resourceId, draft, operation);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(draft);
                }
            }

            LifecycleRecord TransitionLifecycle(
                string resourceId,
                LifecycleAct act,
                long expectedRevision,
                string rationale,
                string? replacementResourceId = null)
            {
                var draft = Encoding.UTF8.GetBytes(S2T5TransitionDraft(rationale, replacementResourceId));
                try
                {
                    return lifecycle.Transition(owner, resourceId, act, expectedRevision, draft, operation);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(draft);
                }
            }

            _ = CreateMemory(
                alphaId,
                "lesson",
                "l1",
                "Safe search provenance",
                "A safe in-process memory item preserves its source provenance and project applicability.",
                includeProvenance: true);
            _ = CreateMemory(withdrawnId, "lesson", "l2", "Withdrawn safe guidance", "safe withdrawn guidance.");
            _ = CreateMemory(rejectedId, "lesson", "l1", "Rejected safe guidance", "safe rejected guidance.");
            _ = CreateMemory(archivedId, "lesson", "l2", "Archived safe note", "safe archived note.");
            _ = CreateMemory(tieAId, "lesson", "l1", "Tie probe A", "tieprobe deterministic ranking tie.");
            _ = CreateMemory(tieBId, "procedure", "l2", "Tie probe B", "tieprobe deterministic ranking tie.");
            _ = CreateMemory(supersededId, "intention", "l1", "Superseded safe intention", "safe superseded intention.");
            _ = CreateMemory(replacementId, "intention", "l1", "Replacement intention", "replacement guidance.");
            _ = CreateMemory(purgeId, "lesson", "l1", "Purge probe", "safe purgeprobe target.");
            _ = CreateMemory(outsideId, "lesson", "l1", "Outside safe note", "safe outside the approved grant.", zoneOverride: outsideZoneId);
            var unknownBytes = Encoding.UTF8.GetBytes("unclassified synthetic canonical record");
            try
            {
                _ = operation.WriteContent(unknownId, zoneId, unknownBytes, 0);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(unknownBytes);
            }

            var withdrawnRecord = CreateLifecycle(withdrawnId);
            var published = TransitionLifecycle(withdrawnId, LifecycleAct.Publish, withdrawnRecord.Revision, "Publish synthetic guidance.");
            var withdrawn = TransitionLifecycle(withdrawnId, LifecycleAct.Withdraw, published.Revision, "Withdraw synthetic guidance for a history boundary.");
            var withdrawalAt = withdrawn.History.Last(change => change.Act == LifecycleAct.Withdraw).RecordedAtUtc;

            var rejectedRecord = CreateLifecycle(rejectedId);
            var rejected = TransitionLifecycle(rejectedId, LifecycleAct.RejectProposal, rejectedRecord.Revision, "Reject synthetic guidance for a history boundary.");
            _ = rejected.History.Last(change => change.Act == LifecycleAct.RejectProposal).RecordedAtUtc;

            var supersededRecord = CreateLifecycle(supersededId);
            _ = TransitionLifecycle(
                supersededId,
                LifecycleAct.SupersedeIntention,
                supersededRecord.Revision,
                "Supersede the synthetic intention.",
                replacementId);

            var archived = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerRecallApplyOperation,
                    ResourceId: archivedId,
                    Label: "archive",
                    Description: "Archive the synthetic item for scoped search coverage."),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(archived.Status == "recall_applied" && archived.Recall?.Disposition == "archived",
                "The actual owner recall handler did not archive the synthetic memory item.");

            var scopedIds = new[]
            {
                alphaId, withdrawnId, rejectedId, archivedId, tieAId, tieBId,
                supersededId, purgeId, unknownId
            };
            var backupService = new VaultBackupService(vaultStore, vault);
            var backup = backupService.CreateBackup(operation, backupPath);
            Require(backup.RecordCount == operation.ListResources().Count,
                "The encrypted backup did not capture the complete canonical scoped-search snapshot.");

            var restoredSummary = backupService.Restore(backupPath, restoredRoot, passphrase, useRecoveryCode: false);
            Require(restoredSummary.RecordCount == backup.RecordCount,
                "The restored canonical snapshot changed its record count before index rebuild.");
            using (var restoredVault = new VaultLifecycle(new EncryptedVaultStore(restoredRoot)))
            {
                Require(restoredVault.Unlock(passphrase, useRecoveryCode: false),
                    "The restored synthetic vault did not unlock with its original passphrase.");
                using var restoredOperation = restoredVault.TryBeginOperation() ??
                    throw new InvalidOperationException("The restored scoped-search vault did not open an operation.");
                using var restoredIndex = new EncryptedPrivateIndexStore(restoredRoot, restoredVault);
                var restoredOutcome = new PrivateIndexAuthority().Rebuild(restoredIndex, restoredOperation);
                Require(restoredOutcome.Snapshot is not null &&
                        restoredOutcome.Snapshot.RowCount == restoredOperation.ListResources().Count,
                    "The restored vault did not materialize its current encrypted canonical rows.");
                var restoredScope = restoredOperation.ListResources()
                    .Where(resource => scopedIds.Contains(resource.ResourceId, StringComparer.Ordinal))
                    .ToArray();
                var restoredCandidates = S8T5Stream(restoredIndex, restoredScope, new[] { "safe" });
                Require(restoredCandidates.Contains(alphaId, StringComparer.Ordinal) &&
                        restoredCandidates.Contains(withdrawnId, StringComparer.Ordinal) &&
                        restoredCandidates.Contains(purgeId, StringComparer.Ordinal) &&
                        !restoredCandidates.Contains(outsideId, StringComparer.Ordinal),
                    "Backup restore followed by index rebuild did not preserve the exact approved canonical search candidates.");
            }

            var materialized = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(materialized.Status == "index_rebuilt" && materialized.PrivateIndex is { Stale: false },
                "The real owner handler did not materialize the canonical scoped-search corpus.");
            var expectedScopedDigest = S8T5LegacyCanonicalDigest(operation.ListResources());
            Require(string.Equals(materialized.PrivateIndex?.CanonicalDigest, expectedScopedDigest, StringComparison.Ordinal),
                "The streaming canonical digest changed prior ordinal encoding for the multi-resource, multi-revision corpus.");

            var grantPreview = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScopePreviewOperation,
                    ResourceIds: scopedIds,
                    Operations: new[]
                    {
                        ServiceRequestHandler.MemorySearchAccessOperation,
                        ServiceRequestHandler.MemoryReadOperation
                    },
                    ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10)),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(grantPreview.Status == "scope_preview" && grantPreview.ScopePreview is not null,
                "The actual owner grant handler did not preview the frozen memory search scope (status=" + grantPreview.Status + ", error=" + grantPreview.Error + ").");

            var issued = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScopeIssueOperation,
                    PreviewId: grantPreview.ScopePreview!.PreviewId),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(issued.Status == "token_issued" && issued.Token is not null,
                "The actual owner grant handler did not issue the scoped search grant.");

            var agent = AuthenticatedContext.ForAgent("s8t5-search-agent", "s8t5-in-process-agent-session");
            var redeemed = await handler.HandleAsync(
                agent,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentRedeemOperation,
                    Token: issued.Token),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(redeemed.Status == "session_issued" && redeemed.SessionToken is not null &&
                    redeemed.AgentSession?.Resources.Length == scopedIds.Length,
                "The real agent redeem handler did not freeze the exact owner-approved resources.");
            var sessionToken = redeemed.SessionToken!;

            async Task<ServiceReply> AgentSearch(
                string? query,
                string? current = null,
                DateTimeOffset? asOfUtc = null,
                string? kind = null,
                int offset = 0,
                int limit = 10,
                bool includeArchived = false,
                string? level = null)
            {
                return await handler.HandleAsync(
                    agent,
                    new ServiceRequest(
                        ServiceRequestHandler.CurrentProtocolVersion,
                        ServiceRequestHandler.AgentMemorySearchOperation,
                        Query: query,
                        SearchOffset: offset,
                        SearchLimit: limit,
                        SearchKind: kind,
                        SearchCurrent: current,
                        SearchLevel: level,
                        SearchAsOfUtc: asOfUtc,
                        IncludeArchived: includeArchived,
                        SessionToken: sessionToken,
                        AccessOperation: ServiceRequestHandler.MemorySearchAccessOperation),
                    operation,
                    CancellationToken.None).ConfigureAwait(false);
            }

            var history = await AgentSearch("safe", current: "history").ConfigureAwait(false);
            Require(history.Status == "memory_search" && history.MemorySearch is { Total: 5 } &&
                    history.MemorySearch.Hits.Select(hit => hit.Id).SequenceEqual(new[]
                    {
                        alphaId, purgeId, supersededId, rejectedId, withdrawnId
                    }),
                "The actual scoped search changed its frozen history candidates, score penalties, or stable tie order.");
            var historyHits = history.MemorySearch!.Hits.ToDictionary(hit => hit.Id, StringComparer.Ordinal);
            Require(historyHits[alphaId].Score == 1.0 &&
                    historyHits[withdrawnId].Score == 0.4 &&
                    historyHits[rejectedId].Score == 0.4 &&
                    historyHits[alphaId].Revision == 1 &&
                    historyHits[alphaId].OccurredAtUtc == occurred &&
                    historyHits[alphaId].RecordedAtUtc == recorded &&
                    !historyHits.ContainsKey(outsideId) &&
                    !historyHits.ContainsKey(unknownId) &&
                    !historyHits.ContainsKey(archivedId),
                "The scoped search did not preserve exact revision, temporal provenance, state penalties, and grant filtering.");
            var levelL1 = await AgentSearch("safe", current: "history", level: "l1").ConfigureAwait(false);
            Require(levelL1.MemorySearch is { Total: 4 } &&
                    levelL1.MemorySearch.Hits.Select(hit => hit.Id).SequenceEqual(new[]
                    {
                        alphaId, purgeId, supersededId, rejectedId
                    }),
                "The actual scoped search did not filter every history candidate by its canonical abstraction level (status=" + levelL1.Status + ", error=" + levelL1.Error + ", total=" + levelL1.MemorySearch?.Total + ", hits=" + string.Join(",", levelL1.MemorySearch?.Hits.Select(hit => hit.Id) ?? Array.Empty<string>()) + ").");
            var levelL2 = await AgentSearch("safe", current: "history", level: "l2").ConfigureAwait(false);
            Require(levelL2.MemorySearch is { Total: 1 } &&
                    levelL2.MemorySearch.Hits.Single().Id == withdrawnId,
                "The actual scoped search did not retain the exact L2 history candidate.");


            var current = await AgentSearch("safe", current: "current").ConfigureAwait(false);
            Require(current.MemorySearch is { Total: 2 } &&
                    current.MemorySearch.Hits.Select(hit => hit.Id).SequenceEqual(new[] { alphaId, purgeId }),
                "Current search did not exclude withdrawn, rejected, archived, and superseded items.");

            var beforeWithdrawal = await AgentSearch(
                "safe",
                current: "current",
                asOfUtc: withdrawalAt.AddTicks(-1)).ConfigureAwait(false);
            Require(beforeWithdrawal.MemorySearch is { Total: 4 } &&
                    beforeWithdrawal.MemorySearch.Hits.Any(hit => hit.Id == withdrawnId) &&
                    beforeWithdrawal.MemorySearch.Hits.Any(hit => hit.Id == rejectedId),
                "The actual scoped search did not replay publication state at its requested historical instant.");

            var includeArchived = await AgentSearch("safe", current: "history", includeArchived: true).ConfigureAwait(false);
            Require(includeArchived.MemorySearch is { Total: 6 } &&
                    includeArchived.MemorySearch.Hits.Single(hit => hit.Id == archivedId).RecallState == "archived",
                "The scoped search did not keep archived owner recall state separate from its canonical search document.");

            var tie = await AgentSearch("tieprobe").ConfigureAwait(false);
            Require(tie.MemorySearch is { Total: 2 } &&
                    tie.MemorySearch.Hits.Select(hit => hit.Id).SequenceEqual(new[] { tieAId, tieBId }) &&
                    tie.MemorySearch.Hits[0].Score == tie.MemorySearch.Hits[1].Score,
                "Equal-score scoped hits did not retain their deterministic resource-ID tie order.");

            var metadata = await AgentSearch("lab").ConfigureAwait(false);
            Require(metadata.MemorySearch is { Total: 1 } && metadata.MemorySearch.Hits[0].Id == alphaId,
                "The actual scoped search did not index applicability metadata from the canonical memory item.");

            var emptyAgentSearch = await AgentSearch(string.Empty).ConfigureAwait(false);
            Require(emptyAgentSearch.Status == "memory_search" &&
                    emptyAgentSearch.MemorySearch is { Total: 0, Hits.Length: 0 },
                "An empty agent query stopped behaving as a no-match query.");

            var deepOffset = await AgentSearch("safe", current: "history", offset: 10_000).ConfigureAwait(false);
            Require(deepOffset.MemorySearch is { Total: 5, Offset: 10_000, Hits.Length: 0, HasMore: false },
                "A deep scoped search offset changed its exact total or returned a hidden candidate.");

            var wideQuery = S8T5WideQueryText("safe");
            Require(SearchText.ExpandQuery(wideQuery).Length > PrivateIndexFormat.MaximumQueryTermsPerPass,
                "The actual agent query did not cross the bounded term-pass boundary.");
            var wideSearch = await AgentSearch(wideQuery, current: "history").ConfigureAwait(false);
            Require(wideSearch.MemorySearch is { Total: 5 } &&
                    wideSearch.MemorySearch.Hits.Select(hit => hit.Id).SequenceEqual(history.MemorySearch.Hits.Select(hit => hit.Id)),
                "The actual scoped agent search truncated query terms after its first bounded pass.");

            var provenance = await handler.HandleAsync(
                agent,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentMemoryReadOperation,
                    ResourceId: alphaId,
                    ResourceRevision: 1,
                    SessionToken: sessionToken,
                    AccessOperation: ServiceRequestHandler.MemoryReadOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(provenance.Status == "memory" &&
                    provenance.MemoryItem is { Revision: 1 } &&
                    provenance.MemoryItem.Sources.Any(source => source.SourceId == "s8t5-unknown-provenance-source"),
                "The exact scoped search hit could not be opened at its frozen revision with its stored provenance intact.");

            var ownerLevel = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "tieprobe",
                    SearchOffset: 0,
                    SearchLimit: 10,
                    SearchLevel: "l2"),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerLevel.Status == "library_page" &&
                    ownerLevel.Library is { Total: 1 } &&
                    ownerLevel.Library.Hits.Single().Id == tieBId &&
                    ownerLevel.Library.Hits[0].Level == "l2",
                "The real owner library's abstraction-level filter changed during the index migration.");

            var ownerHistory = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "safe",
                    SearchCurrent: "history",
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerHistory.Status == "library_page" &&
                    ownerHistory.Library is { Total: 6, Hits.Length: 6 } &&
                    new HashSet<string>(ownerHistory.Library.Hits.Select(hit => hit.Id), StringComparer.Ordinal).SetEquals(
                        new[] { alphaId, purgeId, supersededId, rejectedId, withdrawnId, outsideId }),
                "The actual owner library changed its all-history scope or lifecycle selection.");
            var ownerCurrent = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "safe",
                    SearchCurrent: "current",
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerCurrent.Status == "library_page" &&
                    ownerCurrent.Library is { Total: 3, Hits.Length: 3 } &&
                    ownerCurrent.Library.Hits.Any(hit => hit.Id == alphaId) &&
                    ownerCurrent.Library.Hits.Any(hit => hit.Id == purgeId) &&
                    ownerCurrent.Library.Hits.Any(hit => hit.Id == outsideId),
                "The actual owner library changed its current lifecycle selection.");
            var ownerBeforeWithdrawal = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "safe",
                    SearchCurrent: "current",
                    SearchAsOfUtc: withdrawalAt.AddTicks(-1),
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerBeforeWithdrawal.Status == "library_page" &&
                    ownerBeforeWithdrawal.Library is { Total: 5 } &&
                    ownerBeforeWithdrawal.Library.Hits.Any(hit => hit.Id == withdrawnId) &&
                    ownerBeforeWithdrawal.Library.Hits.Any(hit => hit.Id == rejectedId) &&
                    ownerBeforeWithdrawal.Library.Hits.Any(hit => hit.Id == outsideId),
                "The actual owner library did not replay lifecycle state at its requested historical instant.");
            var ownerHistoryWithArchive = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "safe",
                    SearchCurrent: "history",
                    SearchOffset: 0,
                    SearchLimit: 50,
                    IncludeArchived: true),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerHistoryWithArchive.Status == "library_page" &&
                    ownerHistoryWithArchive.Library is { Total: 7 } &&
                    ownerHistoryWithArchive.Library.Hits.Single(hit => hit.Id == archivedId).RecallState == "archived",
                "The actual owner library changed its explicit archived-recall policy.");

            var ownerTie = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "tieprobe",
                    SearchOffset: 0,
                    SearchLimit: 10),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerTie.Status == "library_page" &&
                    ownerTie.Library is { Total: 2 } &&
                    ownerTie.Library.Hits.Select(hit => hit.Id).SequenceEqual(new[] { tieAId, tieBId }) &&
                    ownerTie.Library.Hits[0].Score == ownerTie.Library.Hits[1].Score,
                "The owner library changed equal-score tie order or score parity.");
            var ownerTieAtInstant = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "tieprobe",
                    SearchOffset: 0,
                    SearchLimit: 10,
                    SearchFromUtc: occurred,
                    SearchToUtc: occurred),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            var ownerTieOutsideWindow = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "tieprobe",
                    SearchOffset: 0,
                    SearchLimit: 10,
                    SearchFromUtc: occurred.AddTicks(1),
                    SearchToUtc: occurred.AddDays(1)),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerTieAtInstant.Status == "library_page" &&
                    ownerTieAtInstant.Library is { Total: 2 } &&
                    ownerTieAtInstant.Library.Hits.Select(hit => hit.Id).SequenceEqual(new[] { tieAId, tieBId }) &&
                    ownerTieOutsideWindow.Status == "library_page" &&
                    ownerTieOutsideWindow.Library is { Total: 0, Hits.Length: 0 },
                "The owner request-local snapshot changed inclusive time-window filtering.");


            var ownerBrowse = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: string.Empty,
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerBrowse.Status == "library_page" &&
                    ownerBrowse.Library is not null &&
                    ownerBrowse.Library.Hits.Any(hit => hit.Id == outsideId && hit.ZoneId == outsideZoneId) &&
                    ownerBrowse.Library.Hits.All(hit => hit.ZoneId == (hit.Id == outsideId ? outsideZoneId : zoneId)) &&
                    !ownerBrowse.Library.Hits.Any(hit => hit.Id == archivedId),
                "An empty owner library browse changed its full owner scope, zone projection, or recall-state rules.");

            var ownerZoneBrowse = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: string.Empty,
                    ZoneId: zoneId,
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerZoneBrowse.Status == "library_page" &&
                    ownerZoneBrowse.Library is not null &&
                    ownerZoneBrowse.Library.Hits.All(hit => hit.ZoneId == zoneId) &&
                    !ownerZoneBrowse.Library.Hits.Any(hit => hit.Id == outsideId),
                "The owner zone-filtered browse included another zone or lost its zone projection.");


            var ownerStatusBeforeRevision = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerIndexStatusOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerStatusBeforeRevision.Status == "index_status" &&
                    ownerStatusBeforeRevision.PrivateIndex is { Stale: false },
                "The real owner status handler did not report the current generation before the canonical revision.");
            S8T5WriteHandlerDump("before-revision", ownerStatusBeforeRevision, ownerBrowse, history);

            var revisedDraft = Encoding.UTF8.GetBytes(Draft(
                alphaId,
                "lesson",
                "l1",
                "Revised search provenance",
                "The revised item no longer contains the earlier query term.",
                includeProvenance: true));
            try
            {
                var revised = new MemoryItemService().Revise(owner, alphaId, zoneId, 1, revisedDraft, operation);
                Require(revised.Revision == 2,
                    "The canonical revision did not advance before the frozen-grant differential.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(revisedDraft);
            }
            var staleOwnerBeforeRebuild = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: "safe",
                    SearchOffset: 0,
                    SearchLimit: 10),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(staleOwnerBeforeRebuild.Status is null &&
                    staleOwnerBeforeRebuild.Error == "search_index_stale",
                "The owner search did not reject a changed canonical ID/revision digest before rebuild (status=" +
                (staleOwnerBeforeRebuild.Status ?? "null") + ", error=" + (staleOwnerBeforeRebuild.Error ?? "none") + ").");


            var staleBeforeRebuild = await AgentSearch("safe", current: "history").ConfigureAwait(false);
            Require(staleBeforeRebuild.Status == "denied" && staleBeforeRebuild.Error == "search_index_stale",
                "A real scoped search scanned past a moved canonical revision instead of failing closed.");

            var rebuiltAfterRevision = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(rebuiltAfterRevision.Status == "index_rebuilt" && rebuiltAfterRevision.PrivateIndex is { Stale: false },
                "The owner did not publish the actual canonical revision before the frozen-scope differential.");
            var afterRevision = await AgentSearch("safe", current: "history").ConfigureAwait(false);
            Require(afterRevision.MemorySearch is { Total: 4 } &&
                    afterRevision.MemorySearch.Hits.Select(hit => hit.Id).SequenceEqual(new[]
                    {
                        purgeId, supersededId, rejectedId, withdrawnId
                    }) &&
                    afterRevision.MemorySearch.Hits.All(hit => hit.Id != alphaId),
                "A rebuilt generation served a new body through an old real grant or changed unaffected scoped hits.");

            var ownerStatusAfterRevision = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerIndexStatusOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            var ownerBrowseAfterRevision = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: string.Empty,
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(ownerStatusAfterRevision.Status == "index_status" &&
                    ownerStatusAfterRevision.PrivateIndex is { Stale: false } &&
                    ownerBrowseAfterRevision.Status == "library_page" &&
                    ownerBrowseAfterRevision.Library?.Hits.SingleOrDefault(hit => hit.Id == alphaId) is { Revision: 2 } &&
                    ownerBrowseAfterRevision.Library.Hits.Any(hit => hit.Id == outsideId && hit.ZoneId == outsideZoneId) &&
                    ownerBrowseAfterRevision.Library.Hits.All(hit => hit.ZoneId == (hit.Id == outsideId ? outsideZoneId : zoneId)) &&
                    !ownerBrowseAfterRevision.Library.Hits.Any(hit => hit.Id == archivedId),
                "The actual owner status or browse consumer did not follow the rebuilt revision with the correct zones and lifecycle filters.");
            S8T5WriteHandlerDump("after-revision", ownerStatusAfterRevision, ownerBrowseAfterRevision, afterRevision);
            var tieAfterRebuild = await AgentSearch("tieprobe").ConfigureAwait(false);
            Require(tieAfterRebuild.MemorySearch is not null &&
                    tieAfterRebuild.MemorySearch.Hits.SequenceEqual(tie.MemorySearch!.Hits),
                "A fresh generation changed an unaffected scoped ranking tie or its served provenance.");

            var frozenRead = await handler.HandleAsync(
                agent,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentMemoryReadOperation,
                    ResourceId: alphaId,
                    ResourceRevision: 1,
                    SessionToken: sessionToken,
                    AccessOperation: ServiceRequestHandler.MemoryReadOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(frozenRead.Status == "denied" && frozenRead.MemoryItem is null,
                "A real grant continued to serve a superseded frozen revision after rebuild.");

            var purger = new PurgeService();
            var purgePreview = purger.Preview(owner, purgeId, 1, "Synthetic purge search boundary.", operation);
            var purgeResult = purger.Confirm(
                owner,
                purgePreview,
                "PURGE",
                "Synthetic purge search boundary.",
                operation,
                invalidateIndex: null);
            Require(purgeResult.Deleted.Any(resource => resource.ResourceId == purgeId),
                "The actual purge service did not delete the canonical query target.");

            var revokedSearch = await AgentSearch("purgeprobe").ConfigureAwait(false);
            Require(revokedSearch.Status == "denied" && revokedSearch.MemorySearch is null,
                "The real session continued searching after the purge policy-generation change.");

            var postPurgeIds = scopedIds.Where(id => id != purgeId).ToArray();
            var postPurgePreview = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScopePreviewOperation,
                    ResourceIds: postPurgeIds,
                    Operations: new[]
                    {
                        ServiceRequestHandler.MemorySearchAccessOperation,
                        ServiceRequestHandler.MemoryReadOperation
                    },
                    ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10)),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(postPurgePreview.ScopePreview is not null,
                "The owner could not issue a new scope after the purge removed its target.");
            var postPurgeIssued = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScopeIssueOperation,
                    PreviewId: postPurgePreview.ScopePreview!.PreviewId),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            var postPurgeRedeemed = await handler.HandleAsync(
                agent,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.AgentRedeemOperation,
                    Token: postPurgeIssued.Token),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(postPurgeRedeemed.Status == "session_issued" && postPurgeRedeemed.SessionToken is not null,
                "A new real session could not be issued over the surviving post-purge scope.");
            sessionToken = postPurgeRedeemed.SessionToken!;

            var staleAfterPurgeBeforeRebuild = await AgentSearch("safe", current: "history").ConfigureAwait(false);
            Require(staleAfterPurgeBeforeRebuild.Status == "denied" &&
                    staleAfterPurgeBeforeRebuild.Error == "search_index_stale",
                "The live agent-search consumer did not refuse the materialized generation after purge.");

            var rebuiltAfterPurge = await handler.HandleAsync(
                owner,
                new ServiceRequest(ServiceRequestHandler.CurrentProtocolVersion, ServiceRequestHandler.OwnerIndexRebuildOperation),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(rebuiltAfterPurge.Status == "index_rebuilt" && rebuiltAfterPurge.PrivateIndex is { Stale: false },
                "The owner did not rebuild the exact post-purge canonical set.");
            var afterPurge = await AgentSearch("purgeprobe", current: "history").ConfigureAwait(false);
            Require(afterPurge.MemorySearch is { Total: 0, Hits.Length: 0 } &&
                    afterPurge.MemorySearch.Hits.All(hit => hit.Id != purgeId),
                "The post-purge rebuild served a purged canonical item.");

            const string ownerCollectionId = "s8t5-owner-search-collection";
            var collectionSet = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerScopeCollectionSetOperation,
                    CollectionId: ownerCollectionId,
                    ResourceIds: new[] { tieAId, outsideId }),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            Require(collectionSet.Status == "collection_set" &&
                    collectionSet.CollectionId == ownerCollectionId &&
                    collectionSet.ScopeCollections is { Length: 1 } &&
                    collectionSet.ScopeCollections[0].ResourceIds.Length == 2,
                "The actual owner collection handler did not save the exact collection scope.");
            var ownerCollectionBrowse = await handler.HandleAsync(
                owner,
                new ServiceRequest(
                    ServiceRequestHandler.CurrentProtocolVersion,
                    ServiceRequestHandler.OwnerLibrarySearchOperation,
                    Query: string.Empty,
                    CollectionId: ownerCollectionId,
                    SearchOffset: 0,
                    SearchLimit: 50),
                operation,
                CancellationToken.None).ConfigureAwait(false);
            var ownerCollectionIds = ownerCollectionBrowse.Library?.Hits.Select(hit => hit.Id).ToArray() ?? Array.Empty<string>();
            Require(ownerCollectionBrowse.Status == "library_page" &&
                    ownerCollectionBrowse.Library is { Total: 2, Hits.Length: 2 } &&
                    ownerCollectionIds.Contains(tieAId, StringComparer.Ordinal) &&
                    ownerCollectionIds.Contains(outsideId, StringComparer.Ordinal) &&
                    ownerCollectionBrowse.Library.Hits.Single(hit => hit.Id == outsideId).ZoneId == outsideZoneId,
                "The owner collection scope included a nonmember, omitted a member, or changed its per-hit zone.");
            Console.WriteLine("PASS actual owner library search: collection/zone scope, canonical generation and revision stale checks, current/history/as-of, archived policy, time bounds, lock refusal, and deterministic ties");
            Console.WriteLine("PASS actual scoped agent search: real grants, frozen revisions, penalties, time/current/history/level, tie/provenance, >64 terms, pagination, browse, backup/restore rebuild, and purge stale boundary");
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(restoredRoot);
            try
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }


    /// <summary>
    /// Forces the encrypted spill through its bounded merge levels with
    /// 100,000 ordinals in each popular term, then checks every final posting.
    /// </summary>
    private static void S8T5AssertGroupedSpillMultiPass()
    {
        const int rowCount = 100_000;
        var tokens = Enumerable.Range(0, 9)
            .Select(index => "s8t5-spill-popular-" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        Require((long)tokens.Length * rowCount >
                (long)PrivateIndexFormat.MaximumPostingPairsPerRun * PrivateIndexFormat.MergeFanIn,
            "The synthetic spill corpus did not force an intermediate bounded merge.");

        var root = Path.Combine(AppContext.BaseDirectory, "s8t5-spill-multipass-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("The unique synthetic spill test directory already exists.");
        }

        Directory.CreateDirectory(root);
        var spillPath = Path.Combine(root, EncryptedPrivateIndexStore.SpillFileName);
        var key = RandomNumberGenerator.GetBytes(32);
        var sink = new S8T5PostingSink();
        try
        {
            using (var builder = new PrivateIndexPostingBuilder(spillPath, key))
            {
                for (var ordinal = 0; ordinal < rowCount; ordinal++)
                {
                    foreach (var token in tokens)
                    {
                        builder.Add(token, ordinal);
                    }
                    if (ordinal % 997 == 0)
                    {
                        // Repeat a digest/ordinal pair to cover zero deltas and final duplicate collapse.
                        builder.Add(tokens[0], ordinal);
                    }
                }

                builder.Merge(sink);
            }

            Require(!File.Exists(spillPath),
                "The grouped spill remained after its builder was disposed.");
            var entries = sink.ReadBucketEntries();
            Require(entries.Count == tokens.Length,
                "The grouped spill changed the final term count (actual=" + entries.Count.ToString(CultureInfo.InvariantCulture) + ").");

            foreach (var token in tokens)
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
                var bucketKey = Convert.ToHexString(digest);
                CryptographicOperations.ZeroMemory(digest);
                Require(entries.TryGetValue(bucketKey, out var postingRoot),
                    "The final posting bucket omitted a popular synthetic term.");

                var rootPayload = sink.ReadPayload(postingRoot.Offset, EncryptedPrivateIndexStore.RecordKind.Postings);
                var chunks = PrivateIndexRecords.ReadPostingRoot(rootPayload, rowCount);
                var actual = new List<int>(rowCount);
                foreach (var chunk in chunks)
                {
                    var chunkPayload = sink.ReadPayload(chunk.Offset, EncryptedPrivateIndexStore.RecordKind.PostingChunk);
                    actual.AddRange(PrivateIndexRecords.ReadPostingChunk(chunkPayload, chunk.Count, rowCount));
                }

                Require(actual.Count == rowCount && actual.SequenceEqual(Enumerable.Range(0, rowCount)),
                    "A high-frequency term lost, duplicated or reordered ordinals across encrypted spill merge passes.");
            }

            Console.WriteLine(
                "PASS grouped encrypted spill codec: 9 popular terms x 100,000 ordinals across bounded merge levels");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            sink.Dispose();
            TryDeleteDirectory(root);
        }
    }

    private sealed class S8T5PostingSink : IPrivateIndexGenerationSink, IDisposable
    {
        private readonly Dictionary<long, (EncryptedPrivateIndexStore.RecordKind Kind, byte[] Payload)> _records = new();
        private long[] _bucketOffsets = Array.Empty<long>();
        private long _nextOffset;

        public long Write(EncryptedPrivateIndexStore.RecordKind kind, byte[] payload)
        {
            var offset = _nextOffset;
            _records.Add(offset, (kind, payload.ToArray()));
            _nextOffset = checked(offset + payload.Length + 1L);
            return offset;
        }

        public void WriteBuckets(long[] bucketOffsets) =>
            _bucketOffsets = (long[])bucketOffsets.Clone();

        internal Dictionary<string, (long Offset, int Length)> ReadBucketEntries()
        {
            var entries = new Dictionary<string, (long Offset, int Length)>(StringComparer.Ordinal);
            for (var bucket = 0; bucket < _bucketOffsets.Length; bucket++)
            {
                var offset = _bucketOffsets[bucket];
                if (offset < 0)
                {
                    continue;
                }

                var root = ReadPayload(offset, EncryptedPrivateIndexStore.RecordKind.Bucket);
                foreach (var page in PrivateIndexRecords.ReadBucketRoot(root))
                {
                    var payload = ReadPayload(page.Offset, EncryptedPrivateIndexStore.RecordKind.BucketPage);
                    var pageEntries = PrivateIndexRecords.ReadBucketPage(payload);
                    if (pageEntries.Count != page.EntryCount)
                    {
                        throw new InvalidDataException("A synthetic spill bucket page count changed.");
                    }

                    foreach (var entry in pageEntries)
                    {
                        if (!entries.TryAdd(entry.Key, entry.Value))
                        {
                            throw new InvalidDataException("A synthetic spill bucket repeated a term key.");
                        }
                    }
                }
            }

            return entries;
        }

        internal byte[] ReadPayload(long offset, EncryptedPrivateIndexStore.RecordKind kind)
        {
            if (!_records.TryGetValue(offset, out var record) || record.Kind != kind)
            {
                throw new InvalidDataException("The synthetic posting sink could not resolve a final record.");
            }

            return record.Payload;
        }

        public void Dispose()
        {
            foreach (var record in _records.Values)
            {
                CryptographicOperations.ZeroMemory(record.Payload);
            }

            _records.Clear();
            Array.Clear(_bucketOffsets, 0, _bucketOffsets.Length);
            _bucketOffsets = Array.Empty<long>();
        }
    }

    /// <summary>
    /// A valid maximum-field envelope imported above the normal writer cap is
    /// rebuilt through the real encrypted vault and exact token record.
    /// </summary>
    private static void S8T5AssertLongAndLargeDocumentRecords()
    {
        const string resourceId = "s8t5-large-token-document";
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Large-Document-" + Guid.NewGuid().ToString("N"));
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            using var vault = new VaultLifecycle(new EncryptedVaultStore(root));
            Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                "The synthetic large-document vault was not provisioned.");
            using var operation = vault.TryBeginOperation() ??
                throw new InvalidOperationException("The synthetic large-document vault did not open an operation.");

            var words = Enumerable.Range(0, 3_000)
                .Select(index => ((char)(0xAC00 + index)).ToString())
                .ToArray();
            var wordPrefix = string.Join(" ", words);
            var title = new string('갂', MemoryEnvelopeCodec.MaximumTitleLength);
            var body = wordPrefix + " " + new string(
                '각',
                MemoryEnvelopeCodec.MaximumBodyChars - wordPrefix.Length - 1);
            var project = new string('갃', MemoryEnvelopeCodec.MaximumApplicabilityLength);
            var environment = new string('갅', MemoryEnvelopeCodec.MaximumApplicabilityLength);
            var version = new string('갆', MemoryEnvelopeCodec.MaximumApplicabilityLength);
            var now = DateTimeOffset.UtcNow;
            var canonical = new StoredMemoryEnvelope(
                MemoryEnvelopeCodec.CurrentSchemaVersion,
                resourceId,
                S8T5IndexZone,
                MemoryKind.Lesson,
                SpeakerRole.User,
                ExecutorKind.Owner,
                CreationMethod.DirectWrite,
                EvidenceLevel.Unverified,
                AbstractionLevel.L1,
                title,
                body,
                Array.Empty<MemorySourceReference>(),
                now.AddHours(-1),
                now,
                new MemoryApplicability(project, environment, version));

            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(canonical, MemoryEnvelopeCodec.Options);
            try
            {
                var normalWriterRejected = false;
                try
                {
                    var normalEncoding = MemoryEnvelopeCodec.EncodeStored(canonical);
                    CryptographicOperations.ZeroMemory(normalEncoding);
                }
                catch (ArgumentException)
                {
                    normalWriterRejected = true;
                }

                var decoded = MemoryEnvelopeCodec.DecodeStored(canonicalBytes);
                Require(canonicalBytes.Length > 1024 * 1024 &&
                        canonicalBytes.Length < SourceSnapshotCodec.MaximumStoredBytes &&
                        normalWriterRejected &&
                        decoded.Title?.Length == MemoryEnvelopeCodec.MaximumTitleLength &&
                        decoded.Body.Length == MemoryEnvelopeCodec.MaximumBodyChars &&
                        decoded.Applicability is { Project.Length: 128, Environment.Length: 128, Version.Length: 128 },
                    "The explicit import boundary did not admit a fully validated document larger than the normal writer cap.");
                var imported = new MemoryItemService().Import(
                    AuthenticatedContext.ForOwner(),
                    resourceId,
                    S8T5IndexZone,
                    expectedRevision: 0,
                    canonicalBytes,
                    operation);
                Require(imported.Revision == 1,
                    "The canonical import did not persist the maximum-field synthetic document.");
                var importedBytes = operation.ReadContent(resourceId, expectedRevision: 1);
                try
                {
                    Require(importedBytes.AsSpan().SequenceEqual(canonicalBytes),
                        "The import path changed the exact canonical envelope bytes.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(importedBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalBytes);
            }

            S8T5AssertUnicodeTokenBound();
            var codeUnits = title.Length + body.Length + project.Length + environment.Length + version.Length + 4;
            var tokens = SearchText.TokenizeDocument(title, body, project, environment, version)
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray();
            var storedLongToken = tokens.First(token => Encoding.UTF8.GetByteCount(token) > 512);
            var tokenBytes = tokens.Sum(token => Encoding.UTF8.GetByteCount(token));
            var tokenPayloadBytes = PrivateIndexRecords.MeasureTokensPayloadBytes(tokens);
            var measuredTokenPayload = PrivateIndexRecords.WriteTokens(tokens);
            try
            {
                Require(codeUnits == PrivateIndexFormat.MaximumDocumentCodeUnits &&
                        tokens.Length > 2_048 &&
                        tokens.Length <= PrivateIndexFormat.MaximumSearchTokens &&
                        Encoding.UTF8.GetByteCount(storedLongToken) > 512 &&
                        Encoding.UTF8.GetByteCount(storedLongToken) <= PrivateIndexFormat.MaximumTokenBytes &&
                        tokenBytes <= 9 * codeUnits &&
                        tokenPayloadBytes <= PrivateIndexFormat.MaximumTokensRecordBytes &&
                        measuredTokenPayload.Length == tokenPayloadBytes,
                    "The canonical tokenizer or exact serialized-byte bound rejected a maximum-field document.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(measuredTokenPayload);
            }

            using var index = new EncryptedPrivateIndexStore(root, vault);
            var outcome = new PrivateIndexAuthority().Rebuild(index, operation);
            Require(outcome.Snapshot is { RowCount: 1 } &&
                    string.Equals(index.CanonicalDigest, outcome.Snapshot.CanonicalDigest, StringComparison.Ordinal),
                "The real rebuild did not publish the exact imported maximum-field canonical set.");
            var selected = index.SelectSearchDocument(resourceId, 1);
            Require(selected is not null && selected.Tokens.SequenceEqual(tokens),
                "The encrypted token record did not round-trip every canonical term beyond the former count and byte caps.");
            var queryScope = new[] { new ScopeResourceRevision(resourceId, S8T5IndexZone, 1) };
            Require(S8T5Stream(index, queryScope, new[] { storedLongToken }).SequenceEqual(new[] { resourceId }),
                "A persisted long Unicode term did not find its exact in-scope canonical document.");

            var indexPath = Path.Combine(
                root,
                EncryptedPrivateIndexStore.IndexDirectoryName,
                EncryptedPrivateIndexStore.PublishedFileName);
            var indexBytes = File.ReadAllBytes(indexPath);
            var longTokenBytes = Encoding.UTF8.GetBytes(storedLongToken);
            try
            {
                Require(!S8T5Contains(indexBytes, longTokenBytes),
                    "The published generation contains a plaintext long tokenizer term.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(indexBytes);
                CryptographicOperations.ZeroMemory(longTokenBytes);
            }

            var indexFiles = Directory.GetFiles(Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName));
            Require(indexFiles.Length == 1 &&
                    string.Equals(Path.GetFileName(indexFiles[0]), EncryptedPrivateIndexStore.PublishedFileName, StringComparison.Ordinal),
                "The large-document rebuild left an encrypted staging or spill file after atomic publication.");
            Console.WriteLine("PASS maximum canonical import: envelope=" +
                canonicalBytes.Length.ToString(CultureInfo.InvariantCulture) + " B; tokenizer input=" +
                codeUnits.ToString(CultureInfo.InvariantCulture) + " UTF-16 units; token text=" +
                tokenBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 B; token record=" +
                tokenPayloadBytes.ToString(CultureInfo.InvariantCulture) + " B; terms=" +
                tokens.Length.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void S8T5AssertCacheStaysInsideItsBudget(string root, S8T5IndexKeys keys)
    {
        const int budget = 4096;
        using var index = new EncryptedPrivateIndexStore(root, keys, budget);
        index.Open();
        var scope = new[]
        {
            new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 1),
            new ScopeResourceRevision("s8t5-index-beta", S8T5IndexZone, 1),
        };
        for (var round = 0; round < 8; round++)
        {
            var selected = S8T5Stream(index, scope, new[] { "login", "recovery", "procedure" });
            Require(selected.Length == 2, "The engine stopped answering from the file once the cache budget was reached.");
            Require(index.CacheBytes <= budget,
                "The decrypted record cache grew past its byte budget (bytes=" + index.CacheBytes + ").");
        }
    }

    /// <summary>
    /// A locked vault has no key for the index: the engine refuses to open
    /// and refuses to read, so no private index byte is read outside an
    /// unlocked epoch.
    /// </summary>
    private static void S8T5AssertLockedVaultCannotRead(string root, S8T5IndexKeys keys)
    {
        keys.Locked = true;
        try
        {
            using var index = new EncryptedPrivateIndexStore(root, keys);
            var refused = false;
            try
            {
                index.Open();
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Require(refused, "A locked vault opened the private index instead of refusing it.");
        }
        finally
        {
            keys.Locked = false;
        }
    }

    /// <summary>
    /// An interrupted rebuild publishes nothing: the staged generation is
    /// dropped, the published generation keeps answering, and the staged file
    /// is reaped.
    /// </summary>
    private static void S8T5AssertInterruptedRebuildPublishesNothing(
        string root,
        S8T5IndexKeys keys,
        (PrivateIndexRow Row, SearchIndexDocument Document) alpha,
        (PrivateIndexRow Row, SearchIndexDocument Document) beta,
        (PrivateIndexRow Row, SearchIndexDocument Document) outside)
    {
        using (var index = new EncryptedPrivateIndexStore(root, keys))
        {
            index.Open();
            using (var scope = index.BeginBulkWrite(S8T5IndexDigest))
            {
                index.Insert(alpha.Row);
                index.InsertSearchDocument(alpha.Document);
                index.Insert(beta.Row);
                index.InsertSearchDocument(beta.Document);
                index.Insert(outside.Row);
                index.InsertSearchDocument(outside.Document);
                // The scope is abandoned: the staged generation is dropped.
            }

            var staged = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName, EncryptedPrivateIndexStore.StagingFileName);
            Require(!File.Exists(staged), "An abandoned rebuild left its staged generation on disk.");
        }

        using (var index = new EncryptedPrivateIndexStore(root, keys))
        {
            index.Open();
            var selected = S8T5Stream(
                index,
                new[] { new ScopeResourceRevision("s8t5-index-alpha", S8T5IndexZone, 1) },
                new[] { "login" });
            Require(selected.SequenceEqual(new[] { "s8t5-index-alpha" }),
                "An abandoned rebuild replaced the published generation (selected=" + string.Join(",", selected) + ").");
        }
    }

    private static void S8T5AssertCancellationBeforeCommitPublishesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "MetaBrain-S8-T5-Cancel-Before-Commit-" + Guid.NewGuid().ToString("N"));
        var passphraseBytes = RandomNumberGenerator.GetBytes(24);
        var passphrase = Convert.ToBase64String(passphraseBytes);
        CryptographicOperations.ZeroMemory(passphraseBytes);
        try
        {
            using var vault = new VaultLifecycle(new EncryptedVaultStore(root));
            Require(vault.Provision(passphrase, out var recoveryCode) && recoveryCode is not null,
                "The synthetic cancellation vault was not provisioned.");
            using var operation = vault.TryBeginOperation() ??
                throw new InvalidOperationException("The synthetic cancellation vault did not open an operation.");

            var now = DateTimeOffset.UtcNow;
            var resourceId = "s8t5-cancel-commit-memory";
            var canonical = new StoredMemoryEnvelope(
                MemoryEnvelopeCodec.CurrentSchemaVersion,
                resourceId,
                S8T5IndexZone,
                MemoryKind.Lesson,
                SpeakerRole.User,
                ExecutorKind.Owner,
                CreationMethod.DirectWrite,
                EvidenceLevel.Unverified,
                AbstractionLevel.L1,
                "Cancellation boundary",
                "A real canonical item used to cancel immediately before atomic publication.",
                Array.Empty<MemorySourceReference>(),
                now.AddMinutes(-1),
                now,
                null);
            var canonicalBytes = MemoryEnvelopeCodec.EncodeStored(canonical);
            try
            {
                operation.WriteContent(resourceId, S8T5IndexZone, canonicalBytes, expectedRevision: 0);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalBytes);
            }

            using var index = new EncryptedPrivateIndexStore(root, vault);
            using var cancellation = new CancellationTokenSource();
            var reader = new S8T5CancelBeforeCommitStore(operation, cancellation);
            var canceled = false;
            try
            {
                _ = new PrivateIndexAuthority().Rebuild(index, reader, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                canceled = true;
            }

            var indexDirectory = Path.Combine(root, EncryptedPrivateIndexStore.IndexDirectoryName);
            Require(canceled &&
                    index.RowCount == 0 &&
                    string.IsNullOrEmpty(index.CanonicalDigest) &&
                    (!Directory.Exists(indexDirectory) || Directory.GetFiles(indexDirectory).Length == 0),
                "Cancellation at the precommit boundary published or retained a partial private-index generation.");
            Console.WriteLine("PASS cancellation immediately before atomic index publication: staged ciphertext reaped, no generation published");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void S8T5AssertLockDuringQueryResetsTouchedCandidates(
        IPrivateIndexStore index,
        IReadOnlyList<ScopeResourceRevision> scope,
        S8T5IndexKeys keys)
    {
        var tokens = Enumerable.Range(0, PrivateIndexFormat.MaximumQueryTermsPerPass)
            .Select(position => position == 0 ? "login" : "lockprobe" + position.ToString(CultureInfo.InvariantCulture))
            .Append("lockprobe-after-pass")
            .ToArray();
        var query = new S8T5LockOnSecondPassTokens(tokens, keys);
        var locked = false;
        try
        {
            foreach (var batch in index.StreamSearchCandidates(scope, query, 64, includeTemporalRow: true))
            {
                _ = batch;
            }
        }
        catch (InvalidOperationException ex) when (ex.Message == "The private index is locked.")
        {
            locked = true;
        }

        Require(locked && keys.Locked,
            "The real streaming query did not stop when its key became unavailable after touching candidates.");
        keys.Locked = false;
        var afterUnlock = S8T5Stream(index, scope, new[] { "login" });
        Require(afterUnlock.SequenceEqual(new[] { "s8t5-index-alpha", "s8t5-index-beta" }),
            "A lock during a query left candidate state in the following unlocked query.");
        Console.WriteLine("PASS lock during a warmed query: the reader refused the locked key and the next query saw full candidates");
    }

    private static void S8T5AssertUnicodeTokenBound()
    {
        var maximumUtf8Bytes = 0;
        var maximumCodeUnit = '\0';
        for (var codePoint = (int)char.MinValue; codePoint <= char.MaxValue; codePoint++)
        {
            var character = (char)codePoint;
            if (char.IsSurrogate(character))
            {
                continue;
            }

            var normalized = SearchText.Normalize(character.ToString());
            var encodedBytes = Encoding.UTF8.GetByteCount(normalized);
            if (encodedBytes > 9)
            {
                throw new InvalidOperationException(
                    "One BMP code unit exceeded the derived nine-byte Form-D token bound (U+" +
                    codePoint.ToString("X4", CultureInfo.InvariantCulture) + ").");
            }

            if (encodedBytes > maximumUtf8Bytes)
            {
                maximumUtf8Bytes = encodedBytes;
                maximumCodeUnit = character;
            }
        }

        var stemTokens = SearchText.TokenizeDocument("studies running", null, null, null, null);
        var stemTokenBytes = stemTokens.Sum(token => Encoding.UTF8.GetByteCount(token));
        Require(stemTokens.SetEquals(new[] { "studies", "study", "running", "run" }) &&
                stemTokens.Count <= "studies running".Length &&
                stemTokenBytes <= 9 * "studies running".Length,
            "The ASCII suffix-stem expansion exceeded the derived per-document token or byte bound.");
        Require(maximumUtf8Bytes == 9,
            "The exhaustive BMP Form-D scan did not exercise the expected Hangul expansion witness.");
        Console.WriteLine("PASS exhaustive BMP Form-D bound: max=" +
            maximumUtf8Bytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 B at U+" +
            ((int)maximumCodeUnit).ToString("X4", CultureInfo.InvariantCulture) +
            "; ASCII stem expansion remains within the source-derived token bound");
    }

    private static string S8T5WideQueryText(string matchingToken)
    {
        var rawTerms = new List<string>(65);
        for (var first = 'a'; first <= 'z' && rawTerms.Count < 65; first++)
        {
            for (var second = 'a'; second <= 'z' && rawTerms.Count < 65; second++)
            {
                var candidate = new string(new[] { first, second });
                if (SearchText.ExpandQuery(candidate).Any(term =>
                        string.Equals(term.Token, candidate, StringComparison.Ordinal)))
                {
                    rawTerms.Add(candidate);
                }
            }
        }

        Require(rawTerms.Count == 65, "The bounded wide-query fixture could not find 65 supported short terms.");
        rawTerms.Add(matchingToken);
        var query = string.Join(" ", rawTerms);
        var expanded = SearchText.ExpandQuery(query);
        Require(query.Length <= SearchText.MaximumQueryLength &&
                expanded.Length > PrivateIndexFormat.MaximumQueryTermsPerPass &&
                expanded.Any(term => string.Equals(term.Token, matchingToken, StringComparison.Ordinal)),
            "The wide-query fixture did not retain every supported term under the existing query-length contract.");
        return query;
    }

    private static bool S8T5Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var found = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (haystack[start + index] != needle[index])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary fixture is left to the host's own cleanup.
        }
    }

    /// <summary>
    /// The index key of a test vault. The key exists only while the vault is
    /// unlocked, exactly as the real lifecycle's key does.
    /// </summary>
    private sealed class S8T5CancelOnSecondPassTokens : IReadOnlyList<string>
    {
        private readonly string[] _tokens;
        private readonly CancellationTokenSource _cancellation;

        internal S8T5CancelOnSecondPassTokens(string[] tokens, CancellationTokenSource cancellation)
        {
            _tokens = tokens;
            _cancellation = cancellation;
        }

        public int Count => _tokens.Length;

        public string this[int index]
        {
            get
            {
                if (index == PrivateIndexFormat.MaximumQueryTermsPerPass)
                {
                    _cancellation.Cancel();
                }

                return _tokens[index];
            }
        }

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_tokens).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class S8T5LockOnSecondPassTokens : IReadOnlyList<string>
    {
        private readonly string[] _tokens;
        private readonly S8T5IndexKeys _keys;

        internal S8T5LockOnSecondPassTokens(string[] tokens, S8T5IndexKeys keys)
        {
            _tokens = tokens;
            _keys = keys;
        }

        public int Count => _tokens.Length;

        public string this[int index]
        {
            get
            {
                if (index == PrivateIndexFormat.MaximumQueryTermsPerPass)
                {
                    _keys.Locked = true;
                }

                return _tokens[index];
            }
        }

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_tokens).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class S8T5CancelBeforeCommitStore : IManagedResourceStore
    {
        private readonly IManagedResourceStore _inner;
        private readonly CancellationTokenSource _cancellation;
        private int _listCalls;

        internal S8T5CancelBeforeCommitStore(
            IManagedResourceStore inner,
            CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        public byte[] ReadContent(string resourceId, long? expectedRevision = null) =>
            _inner.ReadContent(resourceId, expectedRevision);

        public void ValidateRegistration(string resourceId, long? expectedRevision = null) =>
            _inner.ValidateRegistration(resourceId, expectedRevision);

        public IReadOnlyList<ScopeResourceRevision> ListResources()
        {
            var resources = _inner.ListResources();
            if (Interlocked.Increment(ref _listCalls) == 2)
            {
                _cancellation.Cancel();
            }

            return resources;
        }

        public bool TryGetZone(string resourceId, out string? zoneId) =>
            _inner.TryGetZone(resourceId, out zoneId);

        public long WriteContent(string resourceId, string zoneId, byte[] content) =>
            _inner.WriteContent(resourceId, zoneId, content);

        public long WriteContent(string resourceId, string zoneId, byte[] content, long expectedRevision) =>
            _inner.WriteContent(resourceId, zoneId, content, expectedRevision);

        public byte[][] ReadBatch(
            IReadOnlyList<ScopeResourceRevision> batch,
            int maxDegreeOfParallelism,
            CancellationToken cancellationToken) =>
            _inner.ReadBatch(batch, maxDegreeOfParallelism, cancellationToken);
    }


    private sealed class S8T5IndexKeys : IPrivateIndexKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public bool Locked { get; set; }

        public byte[]? PrivateIndexKey => Locked ? null : _key;
    }

    /// <summary>
    /// One synthetic memory item and the search document the rebuild would
    /// build for it. The tokens are the product's own tokenizer output, so
    /// the expectation is the same token set the engine must serve.
    /// </summary>
    private static (PrivateIndexRow Row, SearchIndexDocument Document) S8T5IndexDocument(
        string resourceId,
        long revision,
        string title,
        string body)
    {
        var tokens = SearchText.TokenizeDocument(title, body, null, null, null)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();
        var recorded = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(revision);
        var row = new PrivateIndexRow(
            resourceId,
            S8T5IndexZone,
            revision,
            1,
            PrivateIndexRecordKind.MemoryItem,
            "decision",
            Array.Empty<string>());
        var document = new SearchIndexDocument(
            resourceId,
            revision,
            "decision",
            title,
            tokens,
            recorded.AddHours(-1),
            recorded,
            "proposal",
            string.Empty,
            string.Empty,
            string.Empty);
        return (row, document);
    }

    /// <summary>
    /// The token set a document must serve: the product's own tokenizer
    /// output in the engine's text order. The tokens of this fixture are
    /// ASCII, so the ordinal order and the engine's text order are the same
    /// order.
    /// </summary>
    private static string[] S8T5ExpectedTokens(SearchIndexDocument document) =>
        document.Tokens.OrderBy(token => token, StringComparer.Ordinal).ToArray();
}
