using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Queries;

public sealed record ListDocumentsQuery(
    Guid RequestingUserId,
    string RequestingUserRole,
    Guid? OrganizationId = null,
    DocumentStatus? Status = null,
    string? DocumentTypeCode = null,
    int Page = 1,
    int PageSize = 20);

public sealed record DocumentSummaryDto(
    Guid Id,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
    DocumentStatus Status,
    WorkflowStage CurrentStage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed class ListDocumentsHandler
{
    private readonly IApplicationDbContext _db;

    public ListDocumentsHandler(IApplicationDbContext db) => _db = db;

    public async Task<PagedResult<DocumentSummaryDto>> HandleAsync(
        ListDocumentsQuery q, CancellationToken ct = default)
    {
        var query = _db.Documents
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .AsNoTracking()
            .AsQueryable();

        query = DocumentVisibility.ApplyRoleScope(query, q.RequestingUserId, q.RequestingUserRole);
        query = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
            _db, query, q.RequestingUserId, q.RequestingUserRole, ct);

        if (q.OrganizationId.HasValue)
            query = query.Where(d => d.OrganizationId == q.OrganizationId.Value);

        if (q.Status.HasValue)
            query = query.Where(d => d.Status == q.Status.Value);

        if (!string.IsNullOrEmpty(q.DocumentTypeCode))
            query = query.Where(d => d.DocumentType!.Code == q.DocumentTypeCode);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(d => new DocumentSummaryDto(
                d.Id,
                d.DocumentNumber,
                d.Title,
                d.Organization!.Name,
                d.DocumentType!.Code,
                d.Status,
                d.CurrentStage,
                d.CreatedAt,
                d.UpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<DocumentSummaryDto>(items, total, q.Page, q.PageSize);
    }
}
