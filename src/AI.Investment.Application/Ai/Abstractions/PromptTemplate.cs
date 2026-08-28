using System.Security.Cryptography;
using System.Text;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Abstractions;

/// <summary>A versioned prompt, with a fingerprint of the exact text that was used.</summary>
/// <remarks>
/// The version says which prompt; the hash proves which text. They are not the same guarantee: a
/// version can be bumped without the file changing, and - the case that matters - a file can change
/// without the version being bumped. Recording the hash is what turns "we always version our
/// prompts" from a policy into something the audit trail can check.
/// </remarks>
public sealed record PromptTemplate
{
    private PromptTemplate(PromptRef reference, string text, string hash)
    {
        Reference = reference;
        Text = text;
        Hash = hash;
    }

    public PromptRef Reference { get; }

    public string Text { get; }

    /// <summary>Lower-case hexadecimal SHA-256 of the prompt text.</summary>
    public string Hash { get; }

    public static PromptTemplate Create(PromptRef reference, string text)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DomainValidationException(
                nameof(text),
                $"Prompt {reference} is empty. An agent with no instructions is an agent doing " +
                "whatever the model would have done anyway.");
        }

        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));

        return new PromptTemplate(reference, normalised, Convert.ToHexString(digest).ToLowerInvariant());
    }

    public override string ToString() => $"{Reference} [{Hash[..12]}]";
}
