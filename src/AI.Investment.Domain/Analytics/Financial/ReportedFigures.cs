using System.Diagnostics.CodeAnalysis;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// What one subject reported for one period.
/// </summary>
/// <remarks>
/// <para>
/// A set of named line items rather than a fixed record with a property per figure, because which
/// items a filer reports genuinely varies - and a fixed shape would force every absent figure to be
/// represented as zero or null, both of which read downstream like a reported value.
/// </para>
/// <para>
/// <see cref="With"/> returns a new set rather than mutating, so a computed figure can feed a
/// further calculation - free cash flow into its own margin - without the derived figure ever being
/// mistaken for something the filer reported.
/// </para>
/// </remarks>
public sealed class ReportedFigures
{
    private readonly Dictionary<string, ReportedFigure> _figures;

    private ReportedFigures(
        IngestionSubject subject,
        DateTime periodEndUtc,
        Currency currency,
        Dictionary<string, ReportedFigure> figures)
    {
        Subject = subject;
        PeriodEndUtc = periodEndUtc;
        Currency = currency;
        _figures = figures;
    }

    public IngestionSubject Subject { get; }

    /// <summary>The period these figures describe.</summary>
    public DateTime PeriodEndUtc { get; }

    /// <summary>The currency every money figure here is denominated in.</summary>
    public Currency Currency { get; }

    public IReadOnlyCollection<ReportedFigure> Figures => _figures.Values;

    public static ReportedFigures Create(
        IngestionSubject subject,
        DateTime periodEndUtc,
        Currency currency,
        IEnumerable<ReportedFigure> figures)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(figures);

        DateRange.EnsureUtc(periodEndUtc, nameof(periodEndUtc));

        var map = new Dictionary<string, ReportedFigure>(StringComparer.Ordinal);

        foreach (var figure in figures)
        {
            if (figure is null)
            {
                throw new DomainValidationException(nameof(figures), "A reported figure may not be null.");
            }

            if (!map.TryAdd(figure.Attribute, figure))
            {
                throw new DomainRuleViolationException(
                    "ReportedFigures.ConflictingFigure",
                    $"'{figure.Attribute}' was reported twice for {subject} at {periodEndUtc:O}. " +
                    "Which of two values for one line item is correct is a question about the data, " +
                    "and analytics must not answer it by picking whichever arrived last.");
            }
        }

        return new ReportedFigures(subject, periodEndUtc, currency, map);
    }

    public bool TryFind(string attribute, [NotNullWhen(true)] out ReportedFigure? figure)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            figure = null;
            return false;
        }

        return _figures.TryGetValue(attribute.Trim().ToLowerInvariant(), out figure);
    }

    /// <summary>The same period with one more figure, typically one this platform computed.</summary>
    public ReportedFigures With(ReportedFigure figure)
    {
        ArgumentNullException.ThrowIfNull(figure);

        var map = new Dictionary<string, ReportedFigure>(_figures, StringComparer.Ordinal)
        {
            [figure.Attribute] = figure,
        };

        return new ReportedFigures(Subject, PeriodEndUtc, Currency, map);
    }

    public override string ToString() =>
        $"{Subject} at {PeriodEndUtc:yyyy-MM-dd} ({_figures.Count} figures)";
}
