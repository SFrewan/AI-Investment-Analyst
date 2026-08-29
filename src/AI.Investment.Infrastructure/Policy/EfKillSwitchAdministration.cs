using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Policy;

/// <summary>Writes the database half of the kill switch. One way: it only ever engages.</summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="DatabaseAndEnvironmentKillSwitch"/>, which reads the same table.
/// There is no method here that clears a flag, and that is the design rather than an omission - see
/// <see cref="IKillSwitchAdministration"/> for why disengaging stays out of band.
/// </para>
/// <para>
/// <strong>Writes through the guarded save path.</strong> A kill-switch flag is not seam bookkeeping
/// and is not exempt: engaging must be proposed, policy-evaluated and audited like any other side
/// effect, and the authorisation window the gateway opens is what lets this commit. A store that
/// wrote outside the seam would be a way to change what the platform is permitted to do without a
/// record of who changed it.
/// </para>
/// <para>
/// Engaging an already-engaged switch adds nothing. The read side asks whether <em>any</em> engaged
/// flag matches, so a second row would change no answer and would leave two reasons where one act
/// happened.
/// </para>
/// </remarks>
public sealed class EfKillSwitchAdministration : IKillSwitchAdministration
{
    private readonly AppDbContext _dbContext;

    public EfKillSwitchAdministration(AppDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task EngageAsync(
        Capability? capability,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var already = await _dbContext.KillSwitchFlags
            .AnyAsync(
                flag => flag.Engaged && flag.Capability == capability,
                cancellationToken)
            .ConfigureAwait(false);

        if (already)
        {
            return;
        }

        await _dbContext.KillSwitchFlags
            .AddAsync(KillSwitchFlag.Create(capability, engaged: true, reason, nowUtc), cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
