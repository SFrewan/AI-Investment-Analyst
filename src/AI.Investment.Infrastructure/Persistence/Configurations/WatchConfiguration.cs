using AI.Investment.Domain.Watching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps watches.</summary>
/// <remarks>
/// The index is on trigger type and enabled state, because that is the query every observation runs
/// before anything else happens: a feed delivering a hundred price ticks a second asks it a hundred
/// times a second, and a scan there would make the platform's cost a function of how many watches
/// somebody has configured rather than of how much work there is.
/// </remarks>
public sealed class WatchConfiguration : IEntityTypeConfiguration<Watch>
{
    public void Configure(EntityTypeBuilder<Watch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("watches");

        builder.HasKey(w => w.WatchId);

        builder.Property(w => w.WatchId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(w => w.Name)
            .HasColumnName("name")
            .HasMaxLength(Watch.MaxNameLength)
            .IsRequired();

        builder.Property(w => w.TriggerType)
            .HasColumnName("trigger_type")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(w => w.Cooldown).HasColumnName("cooldown").IsRequired();
        builder.Property(w => w.MaxSignalAge).HasColumnName("max_signal_age").IsRequired();
        builder.Property(w => w.Priority).HasColumnName("priority").IsRequired();

        builder.Property(w => w.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(w => w.CycleTemplate)
            .HasColumnName("cycle_template")
            .HasMaxLength(Watch.MaxTemplateLength)
            .IsRequired();

        builder.Property(w => w.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(w => w.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(w => w.LastFiredAtUtc).HasColumnName("last_fired_at_utc");
        builder.Property(w => w.FireCount).HasColumnName("fire_count").IsRequired();

        builder.Property(w => w.DisabledReason)
            .HasColumnName("disabled_reason")
            .HasMaxLength(Watch.MaxNameLength);

        builder.OwnsOne(w => w.Target, target =>
        {
            target.Property(t => t.Kind)
                .HasColumnName("target_kind")
                .HasMaxLength(WatchTarget.MaxKindLength)
                .IsRequired();

            target.Property(t => t.Identifier)
                .HasColumnName("target_identifier")
                .HasMaxLength(WatchTarget.MaxIdentifierLength);
        });

        builder.Navigation(w => w.Target).IsRequired();

        builder.OwnsOne(w => w.Condition, condition =>
        {
            condition.Property(c => c.Comparison)
                .HasColumnName("condition_comparison")
                .HasConversion<string>()
                .HasMaxLength(40)
                .IsRequired();

            condition.Property(c => c.Threshold)
                .HasColumnName("condition_threshold")
                .HasPrecision(18, 6);

            condition.Property(c => c.Interval).HasColumnName("condition_interval");
        });

        builder.Navigation(w => w.Condition).IsRequired();

        builder.HasIndex(w => new { w.TriggerType, w.Enabled })
            .HasDatabaseName("ix_watches_trigger");
    }
}
