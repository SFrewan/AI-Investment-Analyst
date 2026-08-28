namespace AI.Investment.Application.Ai;

/// <summary>Thrown while reading a model's answer when it does not have the shape it must have.</summary>
/// <remarks>
/// Caught by the agent runner, which retries a bounded number of times and then reports
/// <c>AgentStatus.SchemaFailed</c>. It never falls back to reading the answer as free text: an
/// answer that did not parse is an answer nobody has validated, and treating it as prose is how an
/// unchecked figure gets downstream.
/// </remarks>
public sealed class AgentSchemaException : Exception
{
    public AgentSchemaException(string message)
        : base(message)
    {
    }

    public AgentSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AgentSchemaException()
        : base("The model's answer did not satisfy the required schema.")
    {
    }
}
