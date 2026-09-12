namespace AI.Investment.Infrastructure.Ingestion;

/// <summary>
/// Turns the configured <c>RawArchive:RootPath</c> into an absolute directory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The working directory is never consulted.</strong> This type exists because it used to
/// be: the root was resolved with <c>Path.GetFullPath</c> against
/// <c>Environment.CurrentDirectory</c>, so the same configured value meant a different directory
/// depending on how the process happened to be started. Under <c>dotnet test</c> the working
/// directory is the test assembly's output folder, which is how every payload this system has
/// acquired came to be written beneath <c>bin/</c> - a directory that <c>git clean</c>, a
/// workspace wipe or a rebuild may remove, and that git ignores. The bytes were real evidence
/// living in a place nothing treats as evidence.
/// </para>
/// <para>
/// <strong>A fully qualified value is honoured exactly as written.</strong> Every tool and test
/// harness in this repository already passes an absolute root - a temporary directory of its own,
/// or a path built from a repository root it was given - and those callers must keep the root they
/// asked for. Anchoring a path that is already anchored would silently relocate them.
/// </para>
/// <para>
/// <strong>A relative value is anchored to the repository.</strong> The anchor is the nearest
/// ancestor of <see cref="AppContext.BaseDirectory"/> holding a solution file. That is the
/// convention this repository already uses wherever a test or tool has to find a file it does not
/// own, and it is chosen over the assembly directory for the reason those callers document: it
/// does not depend on how deep the output directory happens to be, so the API host, the test host
/// and a tool all resolve the same configured value to the same directory.
/// </para>
/// <para>
/// <strong>Outside a working tree the assembly directory is the anchor.</strong> A published
/// application has no solution file above it, and there the archive belongs beside the application
/// rather than wherever the service was launched from. That fallback is still absolute and still
/// independent of the working directory, which is the property this type exists to guarantee.
/// </para>
/// </remarks>
public static class RawArchiveRoot
{
    /// <summary>Resolves a configured root to an absolute directory.</summary>
    /// <param name="configured">The value of <c>RawArchive:RootPath</c>.</param>
    public static string Resolve(string configured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);

        if (Path.IsPathFullyQualified(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(Path.Combine(Anchor(), configured));
    }

    /// <summary>
    /// The directory a relative root is measured from.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can prove the anchor is what it claims to be rather than asserting a
    /// path the test itself computed the same way twice.
    /// </remarks>
    public static string Anchor() => FindWorkingTreeRoot() ?? AppContext.BaseDirectory;

    /// <summary>
    /// Walks up from the assembly directory looking for a solution file.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> rather than throwing when there is none. A published
    /// application is not a broken working tree, and a store that refused to start outside one
    /// would be worse than a store that keeps its bytes beside the application.
    /// </remarks>
    private static string? FindWorkingTreeRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.EnumerateFiles(directory.FullName, "*.sln").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
