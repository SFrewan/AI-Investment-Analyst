using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;
using AI.Investment.Infrastructure.Ai;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ai;

/// <summary>
/// The prompts on disk and the agents in the code, checked against each other.
/// </summary>
/// <remarks>
/// This is the test that would have caught the most likely real failure in this phase. Prompts are
/// files and agent prompt references are code; they are edited at different times, by different
/// changes, and nothing but this connects them. An agent pointing at a prompt that was renamed
/// throws at run time, in production, on the first analysis after deployment.
/// </remarks>
public sealed class PromptFilesTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static FilePromptStore Store() =>
        new(Options.Create(new PromptStoreOptions
        {
            RootPath = Path.Combine(RepositoryRoot, "prompts"),
        }));

    private static IReadOnlyList<IAnalysisAgent> Agents()
    {
        var model = new UnconfiguredChatModel();
        var prompts = Store();

        return
        [
            new FinancialAnalysisAgent(model, prompts),
            new NewsAnalysisAgent(model, prompts),
            new RiskAnalysisAgent(model, prompts),
            new SynthesisAgent(model, prompts),
        ];
    }

    [Fact]
    public void The_repository_root_was_located() =>
        Assert.True(
            Directory.Exists(Path.Combine(RepositoryRoot, "prompts")),
            $"No prompts directory was found from {AppContext.BaseDirectory}.");

    [Fact]
    public async Task Every_agent_resolves_the_prompt_it_declares()
    {
        var store = Store();

        foreach (var agent in Agents())
        {
            var template = await store.GetAsync(agent.Prompt);

            Assert.Equal(agent.Prompt, template.Reference);
            Assert.False(string.IsNullOrWhiteSpace(template.Text));
            Assert.Equal(64, template.Hash.Length);
        }
    }

    /// <summary>
    /// The version says which prompt; the hash proves which text. A file edited without its version
    /// moving is exactly what the hash exists to make visible.
    /// </summary>
    [Fact]
    public async Task The_same_prompt_hashes_the_same_way_twice()
    {
        var prompt = PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0);

        var first = await Store().GetAsync(prompt);
        var second = await Store().GetAsync(prompt);

        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public async Task Two_different_prompts_do_not_share_a_hash()
    {
        var store = Store();

        var financial = await store.GetAsync(
            PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0));
        var risk = await store.GetAsync(
            PromptRef.Create("risk-analyst", "risk-identification", 1, 0));

        Assert.NotEqual(financial.Hash, risk.Hash);
    }

    /// <summary>
    /// A missing prompt is a deployment error, not a condition to recover from: an agent running on
    /// substitute instructions produces output nobody can reproduce.
    /// </summary>
    [Fact]
    public async Task A_prompt_that_does_not_exist_fails_loudly()
    {
        var missing = PromptRef.Create("financial-analyst", "statement-interpretation", 9, 9);

        var exception = await Assert.ThrowsAsync<PromptNotFoundException>(
            () => Store().GetAsync(missing));

        Assert.Equal(missing, exception.Prompt);
    }

    /// <summary>
    /// Every prompt states, in the text the model actually reads, that the evidence is data. The
    /// framing is the cheapest of the four barriers and the easiest to lose in an edit.
    /// </summary>
    [Fact]
    public async Task Every_prompt_tells_the_agent_that_evidence_is_not_instructions()
    {
        var store = Store();

        foreach (var agent in Agents())
        {
            var text = (await store.GetAsync(agent.Prompt)).Text;

            Assert.Contains("never instructions", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("JSON only", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("refus", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Prompts carry front matter naming the prompt, its version and the output type, because a
    /// prompt file that does not say what it is for becomes unattributable the moment it is moved.
    /// </summary>
    [Fact]
    public async Task Every_prompt_declares_its_identity_in_front_matter()
    {
        var store = Store();

        foreach (var agent in Agents())
        {
            var text = (await store.GetAsync(agent.Prompt)).Text;

            Assert.StartsWith("---", text, StringComparison.Ordinal);
            Assert.Contains($"promptId:   {agent.Prompt.Value}", text, StringComparison.Ordinal);
            Assert.Contains("outputType:", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A prompt is source. Nothing in this phase writes one, and the port offers no way to.
    /// </summary>
    [Fact]
    public void The_prompt_store_contract_offers_no_way_to_write_a_prompt()
    {
        var methods = typeof(IPromptStore).GetMethods().Select(method => method.Name).ToList();

        Assert.Equal(["GetAsync"], methods);
    }

    /// <summary>
    /// Walks up from the test binary until the repository root is recognisable, so the test does not
    /// depend on how deep the output directory happens to be.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "prompts", "financial-analyst")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No repository root containing prompts/financial-analyst was found above {AppContext.BaseDirectory}.");
    }
}
