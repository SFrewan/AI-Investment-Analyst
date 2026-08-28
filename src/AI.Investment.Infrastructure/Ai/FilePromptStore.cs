using System.Collections.Concurrent;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ai;

/// <summary>
/// Reads versioned prompts from disk, at <c>&lt;root&gt;/&lt;prompt-id&gt;/v&lt;n&gt;.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A file per version, never a file that is edited in place. Overwriting <c>v1.md</c> would leave
/// every stored analysis claiming to have run on a prompt whose text no longer exists, and the
/// audit trail would say so with complete confidence. Adding a new version file costs nothing and keeps
/// the record true.
/// </para>
/// <para>
/// Cached after first read, because a prompt cannot change while the process runs: if the file on
/// disk changed underneath a running system, the audit records written before and after would be
/// indistinguishable. Restarting is the way to pick up a new prompt, which is also what makes a
/// prompt change a deployment.
/// </para>
/// </remarks>
public sealed class FilePromptStore : IPromptStore
{
    public const string FileExtension = ".md";

    private readonly ConcurrentDictionary<string, PromptTemplate> _cache = new(StringComparer.Ordinal);
    private readonly string _rootPath;

    public FilePromptStore(IOptions<PromptStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = options.Value.RootPath;
    }

    public async Task<PromptTemplate> GetAsync(
        PromptRef prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var key = prompt.ToString();

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = PathFor(prompt);

        if (!File.Exists(path))
        {
            throw new PromptNotFoundException(
                prompt,
                $"Prompt '{prompt}' was not found at '{path}'. A missing prompt is a deployment " +
                "error, not a condition to recover from: an agent running on substitute " +
                "instructions produces output that cannot be reproduced or compared with " +
                "anything already stored.");
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var template = PromptTemplate.Create(prompt, text);

        return _cache.GetOrAdd(key, template);
    }

    /// <summary>The file a prompt reference resolves to.</summary>
    /// <remarks>
    /// Both segments come from <see cref="PromptRef"/>, which permits only lower-case letters,
    /// digits and hyphens, and the version parts are integers - so no segment can contain a
    /// separator or a parent-directory reference, and this cannot be walked out of the root.
    /// </remarks>
    public string PathFor(PromptRef prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return Path.Combine(_rootPath, prompt.Agent, $"{prompt.Name}.{prompt.VersionLabel}{FileExtension}");
    }
}
