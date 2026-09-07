# Diff scope — attempt-identity fix

## tests/AI.Investment.Api.Tests/AcquisitionBatchTests.cs (modified)
```diff
@@ -201,6 +201,10 @@
 
         authorization.RecordPriorConsumption(prior.Total);
 
+        // Which approved attempt at this batch this is. It goes into every correlation so that a
+        // re-approved attempt is a new act to the seam rather than a duplicate of the failed one.
+        var attemptNumber = prior.Attempts + 1;
+
         var cap = batch.Count;
 
         Assert.True(
@@ -240,7 +244,10 @@
 
         foreach (var member in members)
         {
-            var probe = BuildRequest(PriceSource, member.Symbol, subjectKind, region, clock, "probe");
+            // Built only to compute a fingerprint and then discarded; it is never dispatched, so
+            // its correlation names no attempt.
+            var probe = BuildRequest(
+                PriceSource, member.Symbol, subjectKind, region, clock, batch.Index, attemptNumber, "probe");
 
             if (!await runStore.HasCompletedAsync(probe.Fingerprint()))
             {
@@ -307,7 +314,7 @@
             var member = byMember[symbol];
 
             var request = BuildRequest(
-                PriceSource, symbol, subjectKind, region, clock, member.Ticker);
+                PriceSource, symbol, subjectKind, region, clock, batch.Index, attemptNumber, member.Ticker);
 
             var fingerprint = request.Fingerprint();
 
@@ -421,7 +428,7 @@
             .ToList();
 
         var attemptPath = Universe.RepositoryPath(
-            "artifacts", "universe", Universe.Inv($"acquisition-batch-{batch.Index:00}-attempt-{prior.Attempts + 1:00}.json"));
+            "artifacts", "universe", Universe.Inv($"acquisition-batch-{batch.Index:00}-attempt-{attemptNumber:00}.json"));
 
         await Universe.WriteAsync(
             attemptPath,
@@ -513,45 +520,34 @@
         Assert.Null(stopped);
     }
 
-    /// <summary>Builds the request exactly as the interrupted run built it.</summary>
+    /// <summary>
+    /// Builds one request, with a correlation that names the attempt as well as the symbol.
+    /// </summary>
+    /// <remarks>
+    /// The attempt is in the correlation and deliberately not in the fingerprint: the fingerprint
+    /// is what the ledger suppresses on and must stay identical across attempts, while the
+    /// correlation is what the Action/Policy seam keys idempotency on and must differ, or a
+    /// transport failure claims the key forever and the symbol can never be re-attempted. See
+    /// <see cref="AcquisitionCorrelation"/> for the failure this fixes.
+    /// </remarks>
     private static IngestionRequest BuildRequest(
         string source,
         string symbol,
         string subjectKind,
         Region region,
         IClock clock,
-        string correlationSuffix) =>
+        int batchIndex,
+        int attempt,
+        string ticker) =>
         IngestionRequest.Create(
             SourceId.Create(source),
             DataCategory.MarketPrices,
             region,
             IngestionSubject.Create(subjectKind, symbol),
-            CorrelationId.Create(Universe.Inv($"batch-{Correlation(correlationSuffix)}-MarketPrices")),
+            AcquisitionCorrelation.For(batchIndex, attempt, ticker, DataCategory.MarketPrices),
             clock.UtcNow,
             DateRange.Create(WindowStart, WindowEnd));
 
-    /// <summary>
-    /// Reduces a ticker to the characters a correlation identifier admits.
-    /// </summary>
-    /// <remarks>
-    /// Letters, digits, hyphen and underscore, and nothing else - a dot in a ticker would throw a
-    /// domain validation error while the request was being built, which is outside the try that
-    /// records failures, and would end the batch before it wrote down what it had already spent.
-    /// The correlation identifier is not part of the fingerprint, so shaping it changes nothing
-    /// about which requests are suppressed.
-    /// </remarks>
-    private static string Correlation(string ticker)
-    {
-        var safe = new StringBuilder(ticker.Length);
-
-        foreach (var c in ticker)
-        {
-            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
-        }
-
-        return safe.Length == 0 ? "unnamed" : safe.ToString();
-    }
-
     /// <summary>Waits until the declared quota has room, rather than being refused by it.</summary>
     private static async Task PaceAsync(DateTime lastDispatchUtc)
     {
```

## tests/AI.Investment.Application.UnitTests/Ingestion/IngestionGatewayTests.cs (modified)
```diff
@@ -530,7 +530,146 @@
             key => Assert.Equal($"{fingerprint}:{secondCycle}", key));
     }
 
-    /// <summary>The request the two tests above share, differing only in correlation.</summary>
+    /// <summary>
+    /// C. A failed attempt does not make the work permanently unfetchable.
+    /// </summary>
+    /// <remarks>
+    /// <para>
+    /// The regression this guards is the one that stranded <c>NXST.US</c> and <c>SIRI.US</c>. The
+    /// seam claims the idempotency key <em>before</em> it invokes the effect and does not release
+    /// the claim when the effect throws - correctly, because a claim that evaporated on failure
+    /// would protect nothing. So a caller that derives its correlation from the request rather than
+    /// from the attempt gets one chance at that request for the lifetime of the store: the first
+    /// transport failure burns the key and every later attempt is refused as a duplicate.
+    /// </para>
+    /// <para>
+    /// Both attempts here fail, deliberately. The claim under test is not that the second one
+    /// succeeds - the provider is still broken - but that it was <em>allowed to try</em>: the effect
+    /// ran twice, so nothing about the first failure made the second attempt unreachable.
+    /// </para>
+    /// </remarks>
+    [Fact]
+    public async Task A_failed_attempt_does_not_stop_a_later_attempt_under_a_new_correlation()
+    {
+        var firstAttempt = CorrelationId.Create("batch-2-a1-NXST-MarketPrices");
+        var secondAttempt = CorrelationId.Create("batch-4-a2-NXST-MarketPrices");
+
+        // Stands for a transport failure. The gateway treats any throwing effect identically, and
+        // naming a networking type here would put one in a layer that must not know about them.
+        var provider = new FakeDataProvider(
+            TestSource,
+            Capabilities(),
+            throwOnFetch: new IOException("the name did not resolve"));
+
+        var actions = new ClaimingActionGateway();
+        var runs = new RecordingRunStore();
+        var gateway = Claiming(provider, runs, actions);
+
+        var fingerprint = RequestFor(firstAttempt).Fingerprint();
+
+        // Identical work, so the test cannot pass by accidentally asking for something else.
+        Assert.Equal(fingerprint, RequestFor(secondAttempt).Fingerprint());
+
+        var first = await gateway.IngestAsync(RequestFor(firstAttempt));
+        var second = await gateway.IngestAsync(RequestFor(secondAttempt));
+
+        Assert.Equal(IngestionOutcome.Failed, first.Outcome);
+        Assert.Equal(IngestionOutcome.Failed, second.Outcome);
+
+        // The point of the test: the second attempt reached the provider. Refused would mean the
+        // seam had suppressed it as a duplicate of the first.
+        Assert.NotEqual(IngestionOutcome.Refused, second.Outcome);
+        Assert.Equal(2, provider.FetchCount);
+        Assert.Equal(2, actions.EffectInvocations);
+
+        Assert.Collection(
+            actions.Keys,
+            key => Assert.Equal($"{fingerprint}:{firstAttempt}", key),
+            key => Assert.Equal($"{fingerprint}:{secondAttempt}", key));
+    }
+
+    /// <summary>
+    /// D. Repeating an attempt is still suppressed, even after that attempt failed.
+    /// </summary>
+    /// <remarks>
+    /// The other half of C, and the one that proves the fix did not weaken anything. Scoping the
+    /// correlation to the attempt must not turn the seam into a retry loop: within one attempt the
+    /// duplicate rule is exactly as strict as it was, and a redelivered or re-entered attempt still
+    /// reaches the provider once and once only - failure included.
+    /// </remarks>
+    [Fact]
+    public async Task A_repeated_correlation_is_suppressed_even_when_the_first_attempt_failed()
+    {
+        var attempt = CorrelationId.Create("batch-2-a1-NXST-MarketPrices");
+
+        var provider = new FakeDataProvider(
+            TestSource,
+            Capabilities(),
+            throwOnFetch: new IOException("the name did not resolve"));
+
+        var actions = new ClaimingActionGateway();
+        var runs = new RecordingRunStore();
+        var gateway = Claiming(provider, runs, actions);
+
+        var first = await gateway.IngestAsync(RequestFor(attempt));
+        var second = await gateway.IngestAsync(RequestFor(attempt));
+
+        Assert.Equal(IngestionOutcome.Failed, first.Outcome);
+        Assert.Equal(IngestionOutcome.Refused, second.Outcome);
+        Assert.Equal(IngestionGateway.PolicyPermittedRule, second.RefusalRuleId);
+
+        // Once. The failure did not buy the caller a second go at the same act.
+        Assert.Equal(1, provider.FetchCount);
+        Assert.Equal(1, actions.EffectInvocations);
+    }
+
+    /// <summary>
+    /// E. A new correlation does not let completed work be acquired twice.
+    /// </summary>
+    /// <remarks>
+    /// <para>
+    /// The two boundaries do different jobs and this is where the difference matters. Action
+    /// idempotency protects <em>one execution</em>: it stops the same authorised act happening
+    /// twice. The acquisition ledger protects <em>the work</em>: it stops the platform paying for
+    /// history it already holds. Scoping the correlation to the attempt loosens the first
+    /// deliberately, and this asserts it left the second exactly where it was.
+    /// </para>
+    /// <para>
+    /// If this ever fails, a resumed batch would re-fetch everything it had already acquired, at
+    /// full vendor cost, and the ledger's restart guarantee would be gone.
+    /// </para>
+    /// </remarks>
+    [Fact]
+    public async Task A_completed_fingerprint_stays_suppressible_under_any_later_correlation()
+    {
+        var firstAttempt = CorrelationId.Create("batch-2-a1-NXST-MarketPrices");
+        var laterAttempt = CorrelationId.Create("batch-4-a2-NXST-MarketPrices");
+
+        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{\"page\":1}")]);
+        var actions = new ClaimingActionGateway();
+        var runs = new RecordingRunStore();
+        var gateway = Claiming(provider, runs, actions);
+
+        var completed = await gateway.IngestAsync(RequestFor(firstAttempt));
+
+        Assert.Equal(IngestionOutcome.Succeeded, completed.Outcome);
+
+        var fingerprint = RequestFor(firstAttempt).Fingerprint();
+        var later = RequestFor(laterAttempt);
+
+        // The correlation changed; the identity of the work did not.
+        Assert.Equal(fingerprint, later.Fingerprint());
+        Assert.NotEqual(firstAttempt, laterAttempt);
+
+        // The ledger suppresses on the fingerprint, so the new correlation buys nothing here. This
+        // is the check every batch runner makes before it spends an authorisation.
+        Assert.True(await runs.HasCompletedAsync(fingerprint));
+        Assert.True(await runs.HasCompletedAsync(later.Fingerprint()));
+
+        Assert.Equal(1, provider.FetchCount);
+    }
+
+    /// <summary>The request the tests above share, differing only in correlation.</summary>
     private static IngestionRequest RequestFor(CorrelationId correlation) =>
         IngestionRequest.Create(
             TestSource,
```

## New files

- tests/AI.Investment.Api.Tests/AcquisitionCorrelation.cs
- tests/AI.Investment.Api.Tests/AcquisitionCorrelationTests.cs
- scripts/run-fix.ps1, scripts/run-fix.cmd

## Production source changed: none. src/ is untouched.
