using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the append-only ledger of attempted effects.</summary>
public sealed class ActionExecutionConfiguration : IEntityTypeConfiguration<ActionExecution>
{
    public void Configure(EntityTypeBuilder<ActionExecution> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("action_executions");

        builder.HasKey(e => e.ExecutionId);

        builder.Property(e => e.ExecutionId).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.ProposalId).HasColumnName("proposal_id").IsRequired();

        // Not nullable, and that is the schema recording the invariant: an execution row cannot
        // exist without the decision that authorised it.
        builder.Property(e => e.DecisionId).HasColumnName("decision_id").IsRequired();

        builder.Property(e => e.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(CorrelationId.MaxLength)
            .HasConversion(c => c.Value, value => CorrelationId.Create(value))
            .IsRequired();

        builder.Property(e => e.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(ActionType.MaxLength)
            .HasConversion(a => a.Value, value => ActionType.Create(value))
            .IsRequired();

        builder.Property(e => e.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(e => e.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(ActionProposal.MaxIdempotencyKeyLength)
            .IsRequired();

        builder.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(e => e.CompletedAtUtc).HasColumnName("completed_at_utc");

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property(e => e.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(ActionExecution.MaxFailureReasonLength);

        builder.Ignore(e => e.IsComplete);

        builder.HasIndex(e => e.CorrelationId).HasDatabaseName("ix_action_executions_correlation_id");
        builder.HasIndex(e => e.ProposalId).HasDatabaseName("ix_action_executions_proposal_id");
        builder.HasIndex(e => e.StartedAtUtc).HasDatabaseName("ix_action_executions_started_at_utc");
    }
}
