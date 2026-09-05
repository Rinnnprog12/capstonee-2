using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Infrastructure.Persistence;

public class TsuOrgDbContext : DbContext, IApplicationDbContext
{
    public TsuOrgDbContext(DbContextOptions<TsuOrgDbContext> options) : base(options) { }

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<College> Colleges => Set<College>();
    public DbSet<AcademicYear> AcademicYears => Set<AcademicYear>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<DocumentRequirement> DocumentRequirements => Set<DocumentRequirement>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentAttachment> DocumentAttachments => Set<DocumentAttachment>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<ImagePreprocessingLog> ImagePreprocessingLogs => Set<ImagePreprocessingLog>();
    public DbSet<OCRResult> OCRResults => Set<OCRResult>();
    public DbSet<OCRErrorLog> OCRErrorLogs => Set<OCRErrorLog>();
    public DbSet<AttachmentOCRResult> AttachmentOCRResults => Set<AttachmentOCRResult>();
    public DbSet<AIValidationResult> AIValidationResults => Set<AIValidationResult>();
    public DbSet<ApprovalWorkflow> ApprovalWorkflows => Set<ApprovalWorkflow>();
    public DbSet<ApprovalHistory> ApprovalHistories => Set<ApprovalHistory>();
    public DbSet<TrackingHistory> TrackingHistories => Set<TrackingHistory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Role>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<College>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<UserAccount>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(256).IsRequired();
            e.Property(x => x.AvatarBlobPath).HasMaxLength(512);
            e.Property(x => x.College).HasMaxLength(128);
            e.Property(x => x.RefreshTokenHash).HasMaxLength(128);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.CollegeId);
            e.HasOne(x => x.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CollegeRef)
                .WithMany(c => c.Users)
                .HasForeignKey(x => x.CollegeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Acronym).HasMaxLength(32).IsRequired();
            e.Property(x => x.College).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Semester).HasMaxLength(32);
            e.HasIndex(x => x.CollegeId);
            e.HasIndex(x => x.PrimaryAdviserUserId);
            e.HasOne(x => x.CollegeRef)
                .WithMany(c => c.Organizations)
                .HasForeignKey(x => x.CollegeId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Officer)
                .WithMany()
                .HasForeignKey(x => x.OfficerId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.PrimaryAdviser)
                .WithMany()
                .HasForeignKey(x => x.PrimaryAdviserUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OrganizationMembership>(e =>
        {
            e.Property(x => x.PositionTitle).HasMaxLength(128).IsRequired();
            e.Property(x => x.MembershipRole).HasConversion<int>();
            e.HasOne(x => x.Organization)
                .WithMany(o => o.Memberships)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UserAccount)
                .WithMany()
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.UserAccountId, x.AcademicYearId }).IsUnique();
            e.HasIndex(x => new { x.UserAccountId, x.MembershipRole, x.IsActive });
            e.HasIndex(x => new { x.OrganizationId, x.MembershipRole, x.IsActive });
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.Property(x => x.DocumentNumber).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.PrimaryFileBlobPath).HasMaxLength(512);
            e.Property(x => x.PrimaryFileName).HasMaxLength(256);
            e.Property(x => x.PrimaryContentType).HasMaxLength(128);
            e.Property(x => x.MetadataLockHash).HasMaxLength(64);
            e.HasIndex(x => x.DocumentNumber).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasIndex(x => new { x.CurrentStage, x.Status });
            e.HasIndex(x => new { x.DocumentTypeId, x.AcademicYearId });
            e.HasIndex(x => x.IsMetadataLocked);
        });

        modelBuilder.Entity<DocumentType>()
            .HasIndex(t => t.Code)
            .IsUnique();

        modelBuilder.Entity<DocumentAttachment>(e =>
        {
            e.Property(x => x.AttachmentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.BlobPath).HasMaxLength(512).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(256).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
            e.Property(x => x.ProcessingError).HasMaxLength(512);
            e.HasIndex(x => new { x.DocumentId, x.ProcessingStatus });
        });

        modelBuilder.Entity<AcademicYear>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.IsCurrent);
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.UserId, x.IsRead });
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.EntityName).HasMaxLength(128).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<SystemSetting>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.Value).HasMaxLength(1024).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<AIValidationResult>()
            .Property(x => x.Confidence)
            .HasPrecision(5, 4);

        modelBuilder.Entity<ImagePreprocessingLog>()
            .Property(x => x.DeskewAngle)
            .HasPrecision(8, 4);

        modelBuilder.Entity<OCRResult>()
            .Property(x => x.AvgConfidence)
            .HasPrecision(5, 4);

        modelBuilder.Entity<AttachmentOCRResult>(e =>
        {
            e.Property(x => x.AvgConfidence).HasPrecision(5, 4);
            e.Property(x => x.Engine).HasMaxLength(64);
            e.HasIndex(x => x.AttachmentId).IsUnique();
            e.HasOne(x => x.Attachment)
                .WithMany()
                .HasForeignKey(x => x.AttachmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(modelBuilder);
    }
}
