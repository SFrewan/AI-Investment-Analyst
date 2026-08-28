using AI.Investment.Application.Validation;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Validation;

/// <inheritdoc cref="IValidationRequestFactory"/>
public sealed class ConfiguredValidationRequestFactory : IValidationRequestFactory
{
    /// <summary>The version of the validation method itself, reported with every result.</summary>
    public static CalculationVersion MethodologyVersion { get; } = CalculationVersion.Create(1, 0);

    private readonly IOptionsMonitor<ValidationOptions> _options;

    public ConfiguredValidationRequestFactory(IOptionsMonitor<ValidationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ValidationRequest Create()
    {
        var options = _options.CurrentValue;

        var benchmark = BenchmarkDefinition.Create(
            options.BenchmarkName,
            IngestionSubject.Create(options.BenchmarkSubjectKind, options.BenchmarkSubjectIdentifier),
            options.PriceAttribute,
            BenchmarkRule.BuyAndHold,
            Money.Create(options.BenchmarkInitialCapital, options.Currency),
            Percentage.FromRatio(options.CostPerTradeRatio),
            DateTime.SpecifyKind(options.BenchmarkDeclaredAtUtc, DateTimeKind.Utc));

        return new ValidationRequest(
            EvaluationWindow.Create(
                DateTime.SpecifyKind(options.FromUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(options.ToUtc, DateTimeKind.Utc),
                options.Horizon,
                options.Step),
            Percentage.FromRatio(options.EventThresholdRatio),
            MethodologyVersion,
            benchmark,
            options.PriceAttribute);
    }
}
