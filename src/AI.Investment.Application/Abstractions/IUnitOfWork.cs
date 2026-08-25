namespace AI.Investment.Application.Abstractions;

/// <summary>Commits the changes accumulated during one operation.</summary>
/// <remarks>
/// The implementation refuses to commit unless an authorised action execution is in progress -
/// see <see cref="IWriteAuthorization"/>. Calling this outside the Action/Policy seam throws
/// rather than silently writing.
/// </remarks>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
