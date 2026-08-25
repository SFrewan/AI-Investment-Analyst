using AI.Investment.Domain.Companies;

namespace AI.Investment.Application.Companies.CreateCompany;

/// <summary>
/// Shape validation for <see cref="CreateCompanyCommand"/>, run before the domain is touched.
/// </summary>
/// <remarks>
/// <para>
/// This checks presence and length so a caller receives every problem at once, rather than the
/// first domain exception that happens to fire. It deliberately does NOT re-implement domain
/// rules: whether a ticker is well formed remains the authority of <c>Ticker</c>, and
/// duplicating that logic here would create two definitions that drift apart.
/// </para>
/// <para>
/// Hand-written rather than using a validation library. At one command, a library would add a
/// dependency, an assembly scan at start-up and a layer of indirection to save a dozen lines.
/// It earns its place when there are twenty commands with shared rules; it does not yet.
/// </para>
/// </remarks>
public static class CreateCompanyValidator
{
    public static IReadOnlyList<string> Validate(CreateCompanyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            errors.Add("Name is required.");
        }
        else if (command.Name.Trim().Length > Company.MaxNameLength)
        {
            errors.Add($"Name may not exceed {Company.MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(command.Ticker))
        {
            errors.Add("Ticker is required.");
        }

        if (command.Description is not null &&
            command.Description.Trim().Length > Company.MaxDescriptionLength)
        {
            errors.Add($"Description may not exceed {Company.MaxDescriptionLength} characters.");
        }

        return errors;
    }
}
