using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Projects;
using CoreFoundry.Domain.Users;

namespace CoreFoundry.Application.Projects;

public sealed record MemberDto(long UserId, string Email, ProjectRole Role, DateTime JoinedAt);

/// <summary>
/// Project membership. The API's role policies run first; this adds the rules a policy can't express
/// (e.g. a Developer may remove only themselves). Invariants such as "exactly one Owner" live in <see cref="Project"/>.
/// </summary>
public sealed class MemberService(IProjectRepository projects, IUserRepository users, IUnitOfWork unitOfWork)
{
    public async Task<IReadOnlyList<MemberDto>> ListAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        var emails = (await users.ListByIdsAsync([.. project.Members.Select(member => member.UserId)], cancellationToken))
            .ToDictionary(user => user.Id, user => user.Email);

        return
        [
            .. project.Members
                .OrderByDescending(member => member.Role == ProjectRole.Owner)
                .ThenByDescending(member => member.Role == ProjectRole.Admin)
                .ThenBy(member => emails.GetValueOrDefault(member.UserId), StringComparer.Ordinal)
                .Select(member => ToDto(member, emails.GetValueOrDefault(member.UserId) ?? string.Empty)),
        ];
    }

    /// <summary>Adds an existing account to the project as Admin or Developer.</summary>
    public async Task<MemberDto> AddAsync(long projectId, string email, ProjectRole role, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        var user = await users.FindByEmailAsync(NormalizedEmail(email), cancellationToken)
            ?? throw new NotFoundException("There is no account with this email. Ask them to register first.");

        if (project.FindMember(user.Id) is not null)
        {
            throw new ConflictException("This user is already a member of the project.");
        }

        var member = project.AddMember(user.Id, role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(member, user.Email);
    }

    public async Task<MemberDto> ChangeRoleAsync(long projectId, long userId, ProjectRole role, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        RequireMember(project, userId);

        project.ChangeMemberRole(userId, role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DtoForAsync(project, userId, cancellationToken);
    }

    /// <summary>Admins and the Owner may remove other members; anyone may remove themselves (leave).</summary>
    public async Task RemoveAsync(long projectId, long callerId, long userId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        var caller = RequireMember(project, callerId);
        RequireMember(project, userId);

        if (userId != callerId && !caller.Role.AtLeast(ProjectRole.Admin))
        {
            throw new ForbiddenException("Only admins can remove other members.");
        }

        project.RemoveMember(userId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Makes an existing member the Owner; the previous Owner becomes an Admin. One save, so it's atomic.</summary>
    public async Task<IReadOnlyList<MemberDto>> TransferOwnershipAsync(long projectId, long newOwnerId, CancellationToken cancellationToken)
    {
        var project = await FindVisibleAsync(projectId, cancellationToken);
        RequireMember(project, newOwnerId);

        project.TransferOwnership(newOwnerId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ListAsync(projectId, cancellationToken);
    }

    private async Task<Project> FindVisibleAsync(long projectId, CancellationToken cancellationToken)
    {
        var project = await projects.FindAsync(projectId, cancellationToken);
        return project is null || project.Status == ProjectStatus.Deleting
            ? throw new NotFoundException("Project not found.")
            : project;
    }

    private static ProjectMember RequireMember(Project project, long userId) =>
        project.FindMember(userId) ?? throw new NotFoundException("Member not found.");

    private async Task<MemberDto> DtoForAsync(Project project, long userId, CancellationToken cancellationToken)
    {
        var user = (await users.ListByIdsAsync([userId], cancellationToken)).SingleOrDefault();
        return ToDto(RequireMember(project, userId), user?.Email ?? string.Empty);
    }

    private static string NormalizedEmail(string email)
    {
        try
        {
            return User.NormalizeEmail(email);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException("email", ex.Message);
        }
    }

    private static MemberDto ToDto(ProjectMember member, string email) =>
        new(member.UserId, email, member.Role, member.CreatedAt);
}
