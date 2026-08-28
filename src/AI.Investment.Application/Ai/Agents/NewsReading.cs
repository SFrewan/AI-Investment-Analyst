using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>The news agent's reading of what was published about a subject.</summary>
public sealed class NewsReading : IGroundedOutput
{
    private readonly List<AssertedFigure> _figures;
    private readonly List<string> _themes;

    public NewsReading(
        string summary,
        NewsSentiment sentiment,
        IEnumerable<string> themes,
        IEnumerable<AssertedFigure> figures)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(figures);

        Summary = summary;
        Sentiment = sentiment;
        _themes = themes.ToList();
        _figures = figures.ToList();
    }

    public string Summary { get; }

    public NewsSentiment Sentiment { get; }

    public IReadOnlyList<string> Themes => _themes;

    public IReadOnlyList<AssertedFigure> Figures => _figures;

    public IReadOnlyList<AssertedFigure> AssertedFigures() => _figures;

    public IReadOnlyList<string> NarrativeFragments()
    {
        var fragments = new List<string>(1 + _themes.Count) { Summary };

        fragments.AddRange(_themes);

        return fragments;
    }
}
