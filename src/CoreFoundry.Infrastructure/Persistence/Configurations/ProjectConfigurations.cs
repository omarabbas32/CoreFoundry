using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreFoundry.Infrastructure.Persistence.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(project => project.Id);

        builder.Property(project => project.Name).HasMaxLength(Project.NameMaxLength).IsRequired();
        builder.Property(project => project.Slug).HasMaxLength(Project.SlugMaxLength).IsRequired();
        builder.HasIndex(project => project.Slug).IsUnique();
        builder.Property(project => project.TemplateKey).HasMaxLength(Project.TemplateKeyMaxLength);

        // Ownership must be transferred before the owner can be deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(project => project.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(project => project.Members)
            .WithOne()
            .HasForeignKey(member => member.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(project => project.Members).HasField("_members");

        // Derived from Id (cf_p_{Id}); not a column.
        builder.Ignore(project => project.DatabaseName);
    }
}

internal sealed class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> builder)
    {
        builder.ToTable("ProjectMembers");
        builder.HasKey(member => new { member.ProjectId, member.UserId });

        // Index for "which projects am I in?".
        builder.HasIndex(member => member.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
