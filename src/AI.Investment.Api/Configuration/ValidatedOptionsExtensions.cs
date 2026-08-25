using Microsoft.Extensions.Options;

namespace AI.Investment.Api.Configuration;

/// <summary>
/// Registration helpers for configuration that must be valid before the application accepts traffic.
/// </summary>
public static class ValidatedOptionsExtensions
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> to a configuration section, validates it with
    /// data annotations, and fails application start-up if validation fails.
    /// </summary>
    /// <remarks>
    /// <c>ValidateOnStart</c> is the important part: without it, a missing or malformed
    /// setting surfaces as an exception on the first request that happens to read it, which
    /// in a background-processing system may be hours later and on a different machine.
    /// </remarks>
    public static IServiceCollection AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        services
            .AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
