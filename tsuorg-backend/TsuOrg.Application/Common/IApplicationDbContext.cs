using Microsoft.EntityFrameworkCore;
using TsuOrg.Domain.Entities;

namespace TsuOrg.Application.Common;

public interface IApplicationDbContext
{
    DbSet<Role> Roles { get; }
    DbSet<UserAccount> UserAccounts { get; }
    DbSet<College> Colleges { get; }
    DbSet<AcademicYear> AcademicYears { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationMembership> OrganizationMemberships { get; }
    DbSet<DocumentType> DocumentTypes { get; }
    DbSet<DocumentRequirement> DocumentRequirements { get; }
    DbSet<Document> Documents { get; }
    DbSet<DocumentAttachment> DocumentAttachments { get; }
    DbSet<DocumentVersion> DocumentVersions { get; }
    DbSet<ImagePreprocessingLog> ImagePreprocessingLogs { get; }
    DbSet<OCRResult> OCRResults { get; }
    DbSet<OCRErrorLog> OCRErrorLogs { get; }
    DbSet<AIValidationResult> AIValidationResults { get; }
    DbSet<ApprovalWorkflow> ApprovalWorkflows { get; }
    DbSet<ApprovalHistory> ApprovalHistories { get; }
    DbSet<TrackingHistory> TrackingHistories { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Notification> Notifications { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
