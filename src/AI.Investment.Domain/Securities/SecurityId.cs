namespace AI.Investment.Domain.Securities;

/// <summary>The durable identity of a security. A surrogate, deliberately.</summary>
/// <remarks>
/// <para>
/// <strong>It is a surrogate because every natural candidate is wrong.</strong> A ticker is recycled
/// between issuers and rewritten by corporate events; an exchange code names a venue rather than an
/// instrument; a vendor symbol belongs to the vendor. The Security Master's own invariant is
/// <em>"a ticker is never a key"</em>, and the only way to honour that is for identity to carry no
/// meaning at all, so that nothing about the instrument can change what it is.
/// </para>
/// <para>
/// Shaped after <c>CompanyId</c> so that the two read alike and neither can be passed where the
/// other is expected.
/// </para>
/// </remarks>
public readonly record struct SecurityId(Guid Value)
{
    public static SecurityId New() => new(Guid.NewGuid());

    public static SecurityId Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A security id may not be the empty GUID.", nameof(value));
        }

        return new SecurityId(value);
    }

    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
