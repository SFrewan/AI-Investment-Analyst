using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the durable half of the kill switch.</summary>
/// <remarks>
/// One row per scope, with a unique index on the capability so a scope cannot end up with two rows
/// disagreeing about whether it is engaged. Which row won would depend on ordering nobody controls,
/// and the answer would be "sometimes on".
/// </remarks>
public sealed class KillSwitchFlagConfiguration : IEntityTypeConfiguration<KillSwitchFlag>
{
    public void Configure(EntityTypeBuilder<KillSwitchFlag> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("kill_switch");

        builder.HasKey(f => f.KillSwitchFlagId);

        builder.Property(f => f.KillSwitchFlagId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(f => f.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60);

        builder.Property(f => f.Engaged).HasColumnName("engaged").IsRequired();

        builder.Property(f => f.Reason)
            .HasColumnName("reason")
            .HasMaxLength(KillSwitchFlag.MaxReasonLength)
            .IsRequired();

        builder.Property(f => f.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        builder.HasIndex(f => f.Capability)
            .IsUnique()
            .HasDatabaseName("ux_kill_switch_capability");
    }
}
