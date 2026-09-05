using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking;

/// <summary>
/// Shared RBAC visibility for DTM / DSM reads.
/// <list type="bullet">
/// <item><b>OrgOfficer</b> — own submissions</item>
/// <item><b>Adviser</b> — orgs where MembershipRole=Adviser OR PrimaryAdviserUserId</item>
/// <item><b>Dean</b> — all orgs in the Dean's CollegeId (college-wide)</item>
/// <item><b>SouStaff / SystemAdmin</b> — global</item>
/// </list>
/// </summary>
public static class DocumentVisibility
{
    public static IQueryable<Document> ApplyRoleScope(
        IQueryable<Document> query,
        Guid requestingUserId,
        string requestingRole)
    {
        return requestingRole switch
        {
            "OrgOfficer" => query.Where(d => d.SubmittedByUserId == requestingUserId),
            "Adviser" or "Dean" => query, // refined in ApplyAdviserDeanScopeAsync
            "SouStaff" or "SystemAdmin" => query,
            _ => query.Where(d => d.SubmittedByUserId == requestingUserId),
        };
    }

    public static async Task<IQueryable<Document>> ApplyAdviserDeanScopeAsync(
        IApplicationDbContext db,
        IQueryable<Document> query,
        Guid requestingUserId,
        string requestingRole,
        CancellationToken ct)
    {
        if (requestingRole == "Adviser")
        {
            var assignedOrgIds = await db.OrganizationMemberships
                .AsNoTracking()
                .Where(m =>
                    m.UserAccountId == requestingUserId
                    && m.IsActive
                    && m.MembershipRole == MembershipRole.Adviser)
                .Select(m => m.OrganizationId)
                .Distinct()
                .ToListAsync(ct);

            var primaryOrgIds = await db.Organizations
                .AsNoTracking()
                .Where(o => o.PrimaryAdviserUserId == requestingUserId)
                .Select(o => o.Id)
                .ToListAsync(ct);

            var orgIds = assignedOrgIds.Union(primaryOrgIds).Distinct().ToList();
            if (orgIds.Count == 0)
                return query.Where(_ => false);

            return query.Where(d => orgIds.Contains(d.OrganizationId));
        }

        if (requestingRole == "Dean")
        {
            var user = await db.UserAccounts.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == requestingUserId, ct);

            if (user?.CollegeId is Guid collegeId)
            {
                return query.Where(d => d.Organization!.CollegeId == collegeId);
            }

            // Fallback to legacy string college code
            if (!string.IsNullOrEmpty(user?.College))
            {
                var code = user.College;
                return query.Where(d => d.Organization!.College == code
                    || d.Organization!.CollegeRef!.Code == code);
            }

            return query.Where(_ => false);
        }

        return query;
    }

    public static void EnsureCanViewDocument(
        Document doc,
        Guid requestingUserId,
        string requestingRole)
    {
        if (requestingRole is "SouStaff" or "SystemAdmin")
            return;

        if (requestingRole == "OrgOfficer" && doc.SubmittedByUserId != requestingUserId)
            throw new ForbiddenException("Access denied to this document timeline.");
    }

    /// <summary>
    /// DMA repository scope — officers see <b>all</b> Approved/Archived docs in their org(s),
    /// not only their own submissions.
    /// </summary>
    public static async Task<IQueryable<Document>> ApplyArchiveScopeAsync(
        IApplicationDbContext db,
        IQueryable<Document> query,
        Guid requestingUserId,
        string requestingRole,
        CancellationToken ct)
    {
        if (requestingRole is "SouStaff" or "SystemAdmin")
            return query;

        if (requestingRole == "OrgOfficer")
        {
            var membershipOrgIds = await db.OrganizationMemberships
                .AsNoTracking()
                .Where(m => m.UserAccountId == requestingUserId && m.IsActive)
                .Select(m => m.OrganizationId)
                .Distinct()
                .ToListAsync(ct);

            var officerOrgIds = await db.Organizations
                .AsNoTracking()
                .Where(o => o.OfficerId == requestingUserId)
                .Select(o => o.Id)
                .ToListAsync(ct);

            var orgIds = membershipOrgIds.Union(officerOrgIds).Distinct().ToList();
            if (orgIds.Count == 0)
                return query.Where(_ => false);

            return query.Where(d => orgIds.Contains(d.OrganizationId));
        }

        return await ApplyAdviserDeanScopeAsync(db, query, requestingUserId, requestingRole, ct);
    }

    public static async Task EnsureCanViewArchiveDocumentAsync(
        IApplicationDbContext db,
        Document doc,
        Guid requestingUserId,
        string requestingRole,
        CancellationToken ct)
    {
        if (requestingRole is "SouStaff" or "SystemAdmin")
            return;

        if (doc.Status is not (DocumentStatus.Archived or DocumentStatus.Approved))
            throw new ForbiddenException("Document is not in the organization archive.");

        var scoped = await ApplyArchiveScopeAsync(
            db,
            db.Documents.AsNoTracking().Where(d => d.Id == doc.Id),
            requestingUserId,
            requestingRole,
            ct);

        if (!await scoped.AnyAsync(ct))
            throw new ForbiddenException("Access denied to this archived document.");
    }

    /// <summary>
    /// Figure 16 / View file — adviser/dean may open live UnderReview packages in their scope;
    /// officers may open their own submissions; SOU/admin may open any file.
    /// </summary>
    public static async Task EnsureCanDownloadDocumentAsync(
        IApplicationDbContext db,
        Document doc,
        Guid requestingUserId,
        string requestingRole,
        CancellationToken ct)
    {
        if (requestingRole is "SouStaff" or "SystemAdmin")
            return;

        if (requestingRole == "OrgOfficer")
        {
            if (doc.SubmittedByUserId != requestingUserId)
                throw new ForbiddenException("Access denied to this document file.");
            return;
        }

        if (requestingRole is "Adviser" or "Dean")
        {
            var scoped = await ApplyAdviserDeanScopeAsync(
                db,
                db.Documents.AsNoTracking().Where(d => d.Id == doc.Id),
                requestingUserId,
                requestingRole,
                ct);

            if (!await scoped.AnyAsync(ct))
                throw new ForbiddenException("Access denied to this document file.");
            return;
        }

        throw new ForbiddenException("Access denied to this document file.");
    }
}
