using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>Where the versioned prompt files live.</summary>
/// <remarks>
/// A directory of files in the repository, not a database table and not a configuration string. A
/// prompt is source: it is reviewed, diffed and versioned with the code that depends on it, and a
/// prompt that could be edited at run time would let the system change what it asks itself, which
/// is the one kind of self-modification this platform refuses outright.
/// </remarks>
public sealed class PromptStoreOptions
{
    public const string SectionName = "Prompts";

    /// <summary>Root directory holding one folder per prompt.</summary>
    [Required]
    [MinLength(1)]
    public string RootPath { get; init; } = "prompts";
}
