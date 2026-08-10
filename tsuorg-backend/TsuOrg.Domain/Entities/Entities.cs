using TsuOrg.Domain.Enums;

namespace TsuOrg.Domain.Entities;

public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class Role : BaseEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ICollection<UserAccount> Users { get; set; } = new List<UserAccount>();
}

public class UserAccount : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>Azure Blob path for profile photo (optional).</summary>
    public string? AvatarBlobPath { get; set; }
    /// <summary>Legacy/display college code (kept in sync with <see cref="CollegeId"/>).</summary>
    public string? College { get; set; }
    /// <summary>FK to College catalog — Dean scope uses this.</summary>
    public Guid? CollegeId { get; set; }
    public College? CollegeRef { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public string? RefreshTokenHash { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
}

public class College : BaseEntity
{
    public string Code { get; set; } = string.Empty; // e.g. CICS
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public ICollection<Organization> Organizations { get; set; } = new List<Organization>();
    public ICollection<UserAccount> Users { get; set; } = new List<UserAccount>();
}

public class AcademicYear : BaseEntity
{
    public string Label { get; set; } = string.Empty; // e.g. 2026-2027
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsCurrent { get; set; }
}

public class Organization : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Acronym { get; set; } = string.Empty;
    /// <summary>Legacy/display college code (kept in sync with <see cref="CollegeId"/>).</summary>
    public string? College { get; set; }
    public Guid? CollegeId { get; set; }
    public College? CollegeRef { get; set; }
    public string Status { get; set; } = "Active";
    public string? Semester { get; set; }
    /// <summary>Primary org officer (usually President) — submitter owner shortcut.</summary>
    public Guid? OfficerId { get; set; }
    public UserAccount? Officer { get; set; }
    /// <summary>Primary faculty adviser for this org (Adviser RBAC user).</summary>
    public Guid? PrimaryAdviserUserId { get; set; }
    public UserAccount? PrimaryAdviser { get; set; }
    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();
}

public class OrganizationMembership : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid UserAccountId { get; set; }
    public UserAccount? UserAccount { get; set; }
    /// <summary>Human label (President, Faculty Adviser, …).</summary>
    public string PositionTitle { get; set; } = string.Empty;
    /// <summary>Typed membership role used for Adviser assignment queries.</summary>
    public MembershipRole MembershipRole { get; set; } = MembershipRole.Member;
    public Guid AcademicYearId { get; set; }
    public AcademicYear? AcademicYear { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DocumentType : BaseEntity
{
    public string Code { get; set; } = string.Empty; // SF08, ACCOMPLISHMENT, ACCREDITATION
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ICollection<DocumentRequirement> Requirements { get; set; } = new List<DocumentRequirement>();
}

public class DocumentRequirement : BaseEntity
{
    public Guid DocumentTypeId { get; set; }
    public DocumentType? DocumentType { get; set; }
    public string RequirementKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsAttachment { get; set; }
    public bool IsMandatory { get; set; } = true;
    public bool IsConditional { get; set; }
    public string? ConditionExpression { get; set; }
}

public class Document : BaseEntity
{
    public string DocumentNumber { get; set; } = string.Empty;
    /// <summary>Event/activity title extracted from form or entered by officer.</summary>
    public string Title { get; set; } = string.Empty;
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid DocumentTypeId { get; set; }
    public DocumentType? DocumentType { get; set; }
    public Guid AcademicYearId { get; set; }
    public AcademicYear? AcademicYear { get; set; }
    public Guid SubmittedByUserId { get; set; }
    public UserAccount? SubmittedByUser { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public WorkflowStage CurrentStage { get; set; } = WorkflowStage.Officer;
    /// <summary>Azure Blob path to the primary uploaded file.</summary>
    public string? PrimaryFileBlobPath { get; set; }
    public string? PrimaryFileName { get; set; }
    public string? PrimaryContentType { get; set; }

    /// <summary>
    /// Figure 10 — once the officer confirms, metadata + files are locked.
    /// Upload / edit endpoints must reject when true.
    /// </summary>
    public bool IsMetadataLocked { get; set; }
    /// <summary>SHA-256 hex of the locked metadata snapshot (audit trail).</summary>
    public string? MetadataLockHash { get; set; }
    public DateTimeOffset? MetadataLockedAt { get; set; }

    public ApprovalWorkflow? Workflow { get; set; }
    public ICollection<DocumentAttachment> Attachments { get; set; } = new List<DocumentAttachment>();
    public ICollection<DocumentVersion> Versions { get; set; } = new List<DocumentVersion>();
}

public class DocumentAttachment : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public string AttachmentType { get; set; } = string.Empty;
    public string BlobPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}

public class DocumentVersion : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public int VersionNumber { get; set; }
    public string BlobPath { get; set; } = string.Empty;
    public string ChangeSummary { get; set; } = string.Empty;
}

public class ImagePreprocessingLog : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public string StepsJson { get; set; } = "[]";
    public decimal DeskewAngle { get; set; }
    public string? OutputBlobPath { get; set; }
}

public class OCRResult : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public string TokensJson { get; set; } = "[]";
    public string FullText { get; set; } = string.Empty;
    public decimal AvgConfidence { get; set; }
}

public class OCRErrorLog : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public string Token { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Flag { get; set; } = "LOW_CONFIDENCE";
}

public class AIValidationResult : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public Guid? DocumentVersionId { get; set; }
    public string DocumentClass { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public bool RequiresHumanReview { get; set; }
    public string FieldResultsJson { get; set; } = "[]";
    public string RawResponseJson { get; set; } = "{}";
    public string ModelVersion { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ApprovalWorkflow : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public WorkflowStage CurrentStage { get; set; } = WorkflowStage.Adviser;
    public bool IsComplete { get; set; }
    public ICollection<ApprovalHistory> History { get; set; } = new List<ApprovalHistory>();
}

public class ApprovalHistory : BaseEntity
{
    public Guid WorkflowId { get; set; }
    public ApprovalWorkflow? Workflow { get; set; }
    public Guid ActorUserId { get; set; }
    public UserAccount? ActorUser { get; set; }
    public AppRole ActorRole { get; set; }
    public ApprovalAction Action { get; set; }
    public string? Comments { get; set; }
    public string? SignatureBlobPath { get; set; }
    public string? SignatureHash { get; set; }
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TrackingHistory : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public DocumentStatus Status { get; set; }
    public WorkflowStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
}

public class AuditLog : BaseEntity
{
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string DetailsJson { get; set; } = "{}";
}

public class Notification : BaseEntity
{
    public Guid UserId { get; set; }
    public UserAccount? User { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public Guid? RelatedDocumentId { get; set; }
}
