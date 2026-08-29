namespace AI.Investment.Dashboard.Localization.Resources;

/// <summary>Every user-facing string, in English.</summary>
/// <remarks>
/// <para>
/// One flat dictionary rather than a .resx, for two reasons: it is plain C# that the analyzers and
/// the compiler can see, and a test can compare its keys against the Arabic set to prove neither
/// language has drifted. A missing translation is a defect that should fail a build, not a string
/// that quietly renders in the wrong language.
/// </para>
/// <para>
/// <strong>Backend enum names are not shown to anybody.</strong> The entries prefixed with a domain
/// name below are the display labels for them; rendering <c>NoObservedPrice</c> or
/// <c>DeniedByPolicy</c> raw would be showing an operator an identifier and calling it a UI.
/// </para>
/// </remarks>
public static class EnglishResources
{
    public static IReadOnlyDictionary<string, string> Values { get; } = new Dictionary<string, string>(
        StringComparer.Ordinal)
    {
        // ---- product and shell -----------------------------------------------------------
        ["app.name"] = "AI Investment Analyst",
        ["app.subtitle"] = "Investment analysis platform",
        ["nav.overview"] = "Overview",
        ["nav.market"] = "Market data",
        ["nav.opportunities"] = "Opportunities",
        ["nav.portfolio"] = "Portfolio",
        ["nav.capital"] = "Capital",
        ["nav.risk"] = "Risk",
        ["nav.validation"] = "Validation",
        ["nav.operations"] = "Operations",
        ["nav.safety"] = "Safety and autonomy",
        ["nav.menu"] = "Main navigation",
        ["nav.open"] = "Open navigation",
        ["nav.close"] = "Close navigation",
        ["shell.language"] = "Language",
        ["shell.signedInAs"] = "Signed in as",
        ["shell.signOut"] = "Sign out",
        ["shell.refresh"] = "Refresh",
        ["shell.refreshing"] = "Refreshing…",
        ["shell.lastRefreshed"] = "Last refreshed {0}",
        ["shell.neverRefreshed"] = "Not yet loaded",
        ["shell.systemStatus"] = "System status",

        // ---- sign in ---------------------------------------------------------------------
        ["signIn.title"] = "Sign in",
        ["signIn.intro"] =
            "This platform is read-mostly and every action is recorded against the operator who " +
            "asked. Enter the operator key issued to you.",
        ["signIn.keyLabel"] = "Operator key",
        ["signIn.keyHint"] = "The key is held for this browser session only and is never displayed again.",
        ["signIn.submit"] = "Sign in",
        ["signIn.checking"] = "Checking…",
        ["signIn.rejected"] = "That key was not recognised.",
        ["signIn.empty"] = "Enter an operator key.",
        ["signIn.unreachable"] = "The platform could not be reached.",
        ["signIn.noPrivileges"] =
            "You are signed in, but no privileges are granted to this key. Ask an administrator " +
            "to grant the privileges you need.",

        // ---- generic states --------------------------------------------------------------
        ["state.loading"] = "Loading…",
        ["state.empty"] = "Nothing to show yet.",
        ["state.unavailable"] = "Unavailable",
        ["state.unknown"] = "Unknown",
        ["state.notMeasured"] = "Not measured",
        ["state.retry"] = "Try again",
        ["state.noData"] = "No data",
        ["state.notApplicable"] = "—",

        // ---- errors ----------------------------------------------------------------------
        ["error.title"] = "Something went wrong",
        ["error.unauthorized"] = "Your session is no longer valid. Sign in again.",
        ["error.forbidden"] = "You are signed in, but this key does not hold the privilege this page needs.",
        ["error.notFound"] = "That is not available on this platform.",
        ["error.rateLimited"] = "Too many requests. Wait a moment and try again.",
        ["error.server"] = "The platform reported a failure.",
        ["error.network"] = "The platform could not be reached. Check that it is running.",
        ["error.validation"] = "The request was refused as invalid.",

        // ---- overview --------------------------------------------------------------------
        ["overview.title"] = "Overview",
        ["overview.health"] = "Health",
        ["overview.portfolioValue"] = "Portfolio value",
        ["overview.cash"] = "Cash",
        ["overview.openPositions"] = "Open positions",
        ["overview.unrealised"] = "Unrealised P&L",
        ["overview.realised"] = "Realised P&L",
        ["overview.opportunities"] = "Opportunities",
        ["overview.escalations"] = "Open escalations",
        ["overview.autonomy"] = "Autonomy level",
        ["overview.liveExecution"] = "Live execution",
        ["overview.observationWindow"] = "Observation window",
        ["overview.windowInactive"] = "Not started — no market observations recorded",
        ["overview.windowActive"] = "Accumulating",

        // ---- portfolio -------------------------------------------------------------------
        ["portfolio.title"] = "Portfolio",
        ["portfolio.instrument"] = "Instrument",
        ["portfolio.quantity"] = "Quantity",
        ["portfolio.averageCost"] = "Average cost",
        ["portfolio.costBasis"] = "Cost basis",
        ["portfolio.currentPrice"] = "Current price",
        ["portfolio.marketValue"] = "Market value",
        ["portfolio.exposure"] = "Exposure",
        ["portfolio.valuation"] = "Valuation",
        ["portfolio.totalValue"] = "Total value",
        ["portfolio.totalUnavailable"] = "Total cannot be determined",
        ["portfolio.totalUnavailableWhy"] =
            "{0} of {1} open positions have no published price, so a total would be smaller than " +
            "the truth while still looking like an answer.",
        ["portfolio.empty"] = "No positions have been recorded.",
        ["portfolio.positionDetail"] = "Position detail",
        ["portfolio.back"] = "Back to portfolio",

        // ---- valuation states (backend enum → label) --------------------------------------
        ["valuation.Available"] = "Valued",
        ["valuation.NoObservedPrice"] = "No observed price",
        ["valuation.NotHeld"] = "Closed",
        ["valuation.Unknown"] = "Unknown",

        // ---- market ----------------------------------------------------------------------
        ["market.title"] = "Market data",
        ["market.sources"] = "Sources",
        ["market.source"] = "Source",
        ["market.active"] = "Active",
        ["market.inactive"] = "Inactive",
        ["market.freshness"] = "Freshness",
        ["market.marketDate"] = "Market date",
        ["market.publishedAt"] = "Published by source",
        ["market.retrievedAt"] = "Ingested by platform",
        ["market.timestampNote"] =
            "Market date and publication time are the source's. Ingestion time is ours, and is " +
            "never used as a publication time.",
        ["market.runs"] = "Ingestion runs",
        ["market.noObservations"] = "No market observations have been recorded on this platform.",

        // ---- opportunities ---------------------------------------------------------------
        ["opportunities.title"] = "Opportunities",
        ["opportunities.status"] = "Status",
        ["opportunities.score"] = "Score",
        ["opportunities.risk"] = "Risk",
        ["opportunities.created"] = "Created",
        ["opportunities.evidence"] = "Evidence",
        ["opportunities.evidenceCount"] = "{0} cited observations",
        ["opportunities.detail"] = "Opportunity detail",
        ["opportunities.empty"] = "No opportunities have been discovered.",
        ["opportunities.emptyWhy"] =
            "Discovery runs against observed prices, and none have been recorded yet.",

        // ---- capital ---------------------------------------------------------------------
        ["capital.title"] = "Capital",
        ["capital.ledger"] = "Ledger",
        ["capital.account"] = "Account",
        ["capital.balance"] = "Balance",
        ["capital.entries"] = "Postings",
        ["capital.debit"] = "Debit",
        ["capital.credit"] = "Credit",
        ["capital.amount"] = "Amount",
        ["capital.occurredAt"] = "Occurred",
        ["capital.description"] = "Description",
        ["capital.ledgerNote"] =
            "Every figure here is derived from double-entry postings, not from a stored balance, " +
            "and none of it is a market valuation.",
        ["capital.empty"] = "The ledger holds no postings.",

        // ---- risk ------------------------------------------------------------------------
        ["risk.title"] = "Risk",
        ["risk.limits"] = "Limits",
        ["risk.limit"] = "Limit",
        ["risk.ceiling"] = "Ceiling",
        ["risk.exposureByInstrument"] = "Exposure by instrument",
        ["risk.killSwitch"] = "Kill switch",
        ["risk.killSwitchEngaged"] = "Engaged — every action is refused",
        ["risk.killSwitchDisengaged"] = "Disengaged",
        ["risk.killSwitchUnknown"] = "Unknown — treated as engaged",
        ["risk.exposureNote"] = "Exposure is measured at cost, on the same basis as the ledger.",
        ["risk.empty"] = "No exposure has been recorded.",

        // ---- validation ------------------------------------------------------------------
        ["validation.title"] = "Validation",
        ["validation.notEstablished"] = "No result established",
        ["validation.notEstablishedWhy"] =
            "No admissible prediction has survived the point-in-time guard, because no market " +
            "observations have been recorded. This is not a failed measurement; it is an " +
            "unmeasured one.",
        ["validation.hitRate"] = "Hit rate",
        ["validation.calibration"] = "Calibration",
        ["validation.benchmark"] = "Benchmark",
        ["validation.sampleSize"] = "Admissible predictions",

        // ---- operations ------------------------------------------------------------------
        ["operations.title"] = "Operations",
        ["operations.cycles"] = "Operating cycles",
        ["operations.escalations"] = "Escalations",
        ["operations.shadow"] = "Shadow decisions",
        ["operations.shadowNote"] =
            "Shadow decisions are measurements of what would have happened. Nothing here was executed.",
        ["operations.raisedAt"] = "Raised",
        ["operations.reason"] = "Reason",
        ["operations.capability"] = "Capability",
        ["operations.acknowledged"] = "Acknowledged",
        ["operations.resolved"] = "Resolved",
        ["operations.open"] = "Open",

        // ---- safety ----------------------------------------------------------------------
        ["safety.title"] = "Safety and autonomy",
        ["safety.currentLevel"] = "Current level",
        ["safety.liveExecution"] = "Live execution",
        ["safety.liveExecutionUnavailable"] = "Unavailable",
        ["safety.liveExecutionNote"] =
            "No execution venue in this platform can reach a real market. This is structural, not " +
            "a setting.",
        ["safety.promotion"] = "Promotion",
        ["safety.promotionState"] = "Promotion state",
        ["safety.grants"] = "Capability grants",
        ["safety.unmetCriteria"] = "Unmet criteria",
        ["safety.noWarrant"] = "No promotion warrant exists.",
        ["safety.l3"] = "L3 — prepare for approval",
        ["safety.l3Note"] =
            "The platform prepares proposals for a person to approve. It does not act on its own.",
    };
}
