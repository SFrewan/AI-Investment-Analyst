namespace AI.Investment.Dashboard.Services;

/// <summary>Why a call to the platform did not produce data.</summary>
/// <remarks>
/// Kept distinct because the operator's response to each differs, and because the two that matter
/// most are the ones a careless client would merge: <see cref="Unauthorized"/> means the session is
/// gone, <see cref="Forbidden"/> means the session is fine and the privilege is not. Merging them
/// sends somebody to sign in again over a permissions problem.
/// </remarks>
public enum ApiFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>401. Not authenticated, or the key is no longer recognised.</summary>
    Unauthorized = 1,

    /// <summary>403. Authenticated, but without the privilege this endpoint requires.</summary>
    Forbidden = 2,

    /// <summary>404. The endpoint or resource does not exist on this platform.</summary>
    NotFound = 3,

    /// <summary>429. Rate limited.</summary>
    RateLimited = 4,

    /// <summary>400 or 409. The platform refused the request.</summary>
    Refused = 5,

    /// <summary>5xx. The platform failed.</summary>
    ServerError = 6,

    /// <summary>The platform could not be reached at all.</summary>
    Network = 7,
}

/// <summary>What a failure means to a person, in one place.</summary>
public static class ApiFailureExtensions
{
    /// <summary>The localization key describing this failure.</summary>
    public static string MessageKey(this ApiFailure failure) => failure switch
    {
        ApiFailure.Unauthorized => "error.unauthorized",
        ApiFailure.Forbidden => "error.forbidden",
        ApiFailure.NotFound => "error.notFound",
        ApiFailure.RateLimited => "error.rateLimited",
        ApiFailure.Refused => "error.validation",
        ApiFailure.ServerError => "error.server",
        ApiFailure.Network => "error.network",
        _ => "error.title",
    };
}

/// <summary>Builds results. Non-generic, so the factories are not static members of a generic type.</summary>
public static class ApiResult
{
    public static ApiResult<T> Ok<T>(T value) => new(value, ApiFailure.None);

    public static ApiResult<T> Failed<T>(ApiFailure failure) => new(default, failure);
}

/// <summary>The outcome of one call: a value, or a reason there is none.</summary>
/// <remarks>
/// A result type rather than exceptions, so every call site has to decide what to render when the
/// answer is missing - which is the whole difficulty of a dashboard over a platform that is honest
/// about not knowing things.
/// </remarks>
public sealed class ApiResult<T>
{
    internal ApiResult(T? value, ApiFailure failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public ApiFailure Failure { get; }

    public bool Succeeded => Failure == ApiFailure.None;

    /// <summary>The localization key describing this failure to a person.</summary>
    public string MessageKey => Failure.MessageKey();
}
