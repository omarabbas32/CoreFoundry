using CoreFoundry.Domain.Assistant;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreFoundry.Infrastructure.Persistence.Configurations;

internal sealed class AssistantSessionConfiguration : IEntityTypeConfiguration<AssistantSession>
{
    public void Configure(EntityTypeBuilder<AssistantSession> builder)
    {
        builder.ToTable("AssistantSessions");
        builder.HasKey(session => session.Id);
        builder.HasIndex(session => new { session.ProjectId, session.Status });

        builder.Property(session => session.Goal).HasMaxLength(AssistantSession.GoalMaxLength).IsRequired();
        builder.Property(session => session.ProposalJson).HasColumnType("json");

        // Bumped by every turn; a stale Version makes the save fail (409).
        builder.Property(session => session.Version).IsConcurrencyToken();

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(session => session.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The conversation stays with the project when the user who started it is deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(session => session.Messages)
            .WithOne()
            .HasForeignKey(message => message.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(session => session.Messages).HasField("_messages");

        builder.Ignore(session => session.IsOpen);
        builder.Ignore(session => session.MustPropose);
    }
}

internal sealed class AssistantMessageConfiguration : IEntityTypeConfiguration<AssistantMessage>
{
    public void Configure(EntityTypeBuilder<AssistantMessage> builder)
    {
        builder.ToTable("AssistantMessages");
        builder.HasKey(message => message.Id);
        builder.HasIndex(message => new { message.SessionId, message.Sequence }).IsUnique();

        builder.Property(message => message.Text).HasColumnType("text").IsRequired();
        builder.Property(message => message.OptionsJson).HasColumnType("json");

        builder.Ignore(message => message.FromAssistant);
    }
}

internal sealed class AssistantUsageConfiguration : IEntityTypeConfiguration<AssistantUsage>
{
    public void Configure(EntityTypeBuilder<AssistantUsage> builder)
    {
        builder.ToTable("AssistantUsage");
        builder.HasKey(usage => new { usage.UserId, usage.Day });
        builder.Property(usage => usage.Day).HasColumnType("date");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(usage => usage.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
