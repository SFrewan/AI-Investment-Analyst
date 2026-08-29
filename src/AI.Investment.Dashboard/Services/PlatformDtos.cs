namespace AI.Investment.Dashboard.Services;

/*
    The shapes the platform's read endpoints actually answer with, transcribed from the API's own
    response records rather than guessed.

    Deliberately nullable wherever the platform can decline to know something, and nullable all the
    way to the screen. A client that defaulted a missing market value to zero would turn "we have no
    price for this" into "this is worthless", which is the single most damaging thing a financial
    dashboard can do.

    These are transport shapes, not domain types. Nothing in this project computes a position, a
    score, a limit or a verdict; it renders what the platform decided.
*/

/// <summary>GET api/portfolio</summary>
public sealed record PortfolioDto(
    string Currency,
    DateTime AsAtUtc,
    decimal Cash,
    decimal CostBasis,
    decimal RealisedPnL,
    decimal? UnrealisedPnL,
    decimal? MarketValue,
    decimal? TotalValue,
    bool IsFullyValued,
    int OpenPositions,
    int ValuedPositions,
    int UnvaluedPositions,
    IReadOnlyList<PositionDto> Positions);

/// <summary>One holding, as the portfolio and positions endpoints answer it.</summary>
public sealed record PositionDto(
    string Instrument,
    decimal Quantity,
    decimal? AverageCost,
    decimal CostBasis,
    decimal Exposure,
    decimal RealisedPnL,
    string PriceAvailability,
    decimal? CurrentPrice,
    DateTime? PriceAsOfUtc,
    DateTime? PricePublishedAtUtc,
    decimal? MarketValue,
    decimal? UnrealisedPnL,
    bool IsOpen);

/// <summary>GET api/opportunities</summary>
public sealed record OpportunityDto(
    Guid OpportunityId,
    string Type,
    string SubjectKind,
    string? SubjectIdentifier,
    string DiscovererId,
    DateTime DiscoveredAtUtc,
    string Title,
    string Description,
    string Status,
    DateTime StatusChangedAtUtc,
    DateTime CreatedAtUtc,
    OpportunityEconomicsDto? Economics,
    OpportunityRiskDto? Risk,
    decimal? Confidence,
    OpportunityScoreDto? Score,
    int EvidenceCount,
    IReadOnlyList<Guid> ProposalIds,
    Guid? ApprovalTokenId,
    Guid? ExecutionId,
    string? Resolution);

public sealed record OpportunityEconomicsDto(
    decimal EstimatedCost,
    decimal EstimatedRevenue,
    decimal EstimatedProfit,
    decimal MarginRatio,
    decimal RequiredCapital,
    decimal SuccessProbability,
    decimal RiskAdjustedReturn,
    string Currency,
    DateTime HorizonStartUtc,
    DateTime HorizonEndUtc);

public sealed record OpportunityRiskDto(
    string Summary,
    string Reversibility,
    IReadOnlyList<string> Factors,
    int EvidenceCount);

public sealed record OpportunityScoreDto(string Metric, decimal Value, string Version, DateTime AsOfUtc);

/// <summary>GET api/sources</summary>
public sealed record SourceDto(
    string Id,
    string Name,
    string Type,
    string Authority,
    string Region,
    IReadOnlyList<string> Categories,
    string Cadence,
    int? ExpectedIntervalSeconds,
    bool IsActive,
    SourceLicensingDto Licensing,
    string VerificationPolicy,
    bool CanConfirmAlone,
    int RequiredIndependentSources,
    string ReliabilityGrade,
    DateTime RegisteredAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SourceLicensingDto(
    bool AllowsStorage,
    bool AllowsAutomatedProcessing,
    bool AllowsRedistribution,
    bool RequiresAttribution,
    int? RetentionLimitDays,
    string? Notes);

/// <summary>GET api/data-plane/freshness</summary>
public sealed record FreshnessDto(
    string SourceId,
    string Name,
    string Cadence,
    bool IsActive,
    string State,
    string RuleId,
    DateTime? LastRefreshedAtUtc,
    double? ElapsedSeconds,
    bool NeedsRefresh);

/// <summary>GET api/data-plane/runs</summary>
public sealed record IngestionRunDto(
    Guid Id,
    string SourceId,
    string Category,
    string SubjectKind,
    string? SubjectIdentifier,
    string Outcome,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    int ArtifactCount,
    string? RefusalRuleId,
    string? Reason);

/// <summary>GET api/operations/cycles</summary>
public sealed record CycleDto(
    Guid CycleId,
    string CorrelationId,
    string Capability,
    string Template,
    string TriggerKey,
    Guid? WatchId,
    string Status,
    string Stage,
    string Budget,
    string Consumption,
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? StoppedAtUtc,
    string? StoppedReason,
    int EscalationCount);

/// <summary>GET api/operations/escalations</summary>
public sealed record EscalationDto(
    Guid EscalationId,
    Guid? CycleId,
    Guid? ProposalId,
    string Capability,
    string Reason,
    string Explanation,
    DateTime RaisedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? AcknowledgedAtUtc,
    DateTime? ResolvedAtUtc,
    string? Resolution,
    bool IsUnhandled);

/// <summary>GET api/operations/shadow</summary>
public sealed record ShadowDecisionDto(
    Guid ShadowDecisionId,
    Guid? CycleId,
    Guid ProposalId,
    string Capability,
    string ActionType,
    string RiskTier,
    decimal Exposure,
    string Currency,
    string ActualMode,
    string ActualOutcome,
    string ShadowMode,
    string ShadowOutcome,
    bool WouldHaveExecuted,
    bool Agreed,
    DateTime RecordedAtUtc);

/// <summary>GET api/operations/grants</summary>
public sealed record AutonomyGrantDto(
    Guid AutonomyGrantId,
    string Capability,
    string? ActionType,
    string Environment,
    string GrantedMode,
    string EffectiveMode,
    string MaxRiskTier,
    decimal MaxExposure,
    string Currency,
    string LimitSet,
    string GrantedBy,
    DateTime GrantedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    int DemotionCount,
    bool IsActive);

/// <summary>GET api/autonomy/promotion</summary>
public sealed record PromotionAssessmentDto(
    string Capability,
    string ProposedMode,
    bool IsJustified,
    string Verdict,
    Guid? ValidationRunId,
    string? BenchmarkFingerprint,
    DateTime AssessedAtUtc,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Reasons);

/// <summary>GET api/capital/ledger</summary>
public sealed record LedgerReportDto(
    string Currency,
    bool IsBalanced,
    int EntryCount,
    IReadOnlyList<LedgerBalanceDto> Balances);

public sealed record LedgerBalanceDto(string Account, string Kind, decimal Amount, string Currency);

public sealed record LedgerEntryDto(
    Guid LedgerEntryId,
    string DebitAccount,
    string CreditAccount,
    decimal Amount,
    string Currency,
    DateTime OccurredAtUtc,
    string Description,
    Guid? OpportunityId,
    Guid? ExecutionId);

/// <summary>
/// One figure the platform may or may not have been able to measure.
/// </summary>
/// <remarks>
/// The validation report is built out of these, and the reason it is: an unmeasured hit rate and a
/// hit rate of zero are entirely different claims, and only one of them is ever true here today.
/// <c>Availability</c> carries which, and <c>Explanation</c> carries the platform's own words for
/// why - so a screen never has to guess.
/// </remarks>
public sealed record MeasurementDto(string Availability, decimal? Value, int SampleSize, string Explanation);

/// <summary>GET api/validation/report</summary>
public sealed record ValidationReportDto(
    Guid RunId,
    DateTime GeneratedAtUtc,
    string Verdict,
    string Conclusion,
    DateTime WindowFromUtc,
    DateTime WindowToUtc,
    TimeSpan Horizon,
    decimal EventThresholdRatio,
    string Methodology,
    string BenchmarkName,
    string BenchmarkFingerprint,
    DateTime BenchmarkDeclaredAtUtc,
    IReadOnlyList<string> DataSources,
    int PredictionsConsidered,
    int PredictionsAdmitted,
    int PredictionsRefused,
    int TruePositives,
    int FalsePositives,
    int TrueNegatives,
    int FalseNegatives,
    int Unresolved,
    int Unavailable,
    int Abstained,
    MeasurementDto HitRate,
    MeasurementDto FalsePositiveRate,
    MeasurementDto FalseNegativeRate,
    MeasurementDto Recall,
    MeasurementDto Accuracy,
    MeasurementDto BrierScore,
    IReadOnlyList<CalibrationBinDto> Calibration,
    MeasurementDto SystemReturn,
    MeasurementDto BenchmarkReturn,
    MeasurementDto ExcessReturn,
    int ShadowMeasurements,
    MeasurementDto ShadowAgreementRate,
    MeasurementDto ShadowDivergenceHitRate,
    IReadOnlyList<string> DataGaps,
    IReadOnlyList<string> Limitations);

public sealed record CalibrationBinDto(
    decimal LowerRatio,
    decimal UpperRatio,
    int Count,
    MeasurementDto MeanStated,
    MeasurementDto ObservedFrequency,
    MeasurementDto Gap);
