using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Infrastructure.Persistence;

/// <summary>
/// Seeds roles, colleges, demo users, orgs, typed memberships, document types, AY.
/// Safe to call repeatedly — inserts are guarded.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(TsuOrgDbContext db, IPasswordHasher hasher)
    {
        await SeedRolesAsync(db);
        await db.SaveChangesAsync();

        await SeedCollegesAsync(db);
        await db.SaveChangesAsync();

        await MigrateLegacyOfficerEmailsAsync(db);
        await db.SaveChangesAsync();

        await SeedDemoUsersAsync(db, hasher);
        await SeedAcademicYearAsync(db);
        await db.SaveChangesAsync();

        await SeedOrganizationsAsync(db);
        await SeedDocumentTypesAsync(db);
        await EnsureDocumentTypeCatalogAsync(db);
        await db.SaveChangesAsync();

        await SeedMembershipsAsync(db);
        await BackfillCollegeLinksAsync(db);
        await db.SaveChangesAsync();

        await SeedSampleArchiveDocumentsAsync(db);
        await SeedSampleOfficerDashboardDocumentsAsync(db);
        await db.SaveChangesAsync();
    }

    private static async Task SeedRolesAsync(TsuOrgDbContext db)
    {
        var roles = new[] { "OrgOfficer", "Adviser", "Dean", "SouStaff", "SystemAdmin" };
        foreach (var code in roles)
        {
            if (!await db.Roles.AnyAsync(r => r.Code == code))
                db.Roles.Add(new Role { Code = code, Name = code });
        }
    }

    private static async Task SeedCollegesAsync(TsuOrgDbContext db)
    {
        var colleges = new[]
        {
            ("CICS", "College of Information and Computing Sciences"),
            ("CEA",  "College of Engineering and Architecture"),
            ("CASS", "College of Arts and Social Sciences"),
        };

        foreach (var (code, name) in colleges)
        {
            if (!await db.Colleges.AnyAsync(c => c.Code == code))
                db.Colleges.Add(new College { Code = code, Name = name, IsActive = true });
        }
    }

    /// <summary>
    /// Org officers use @student.tsu.edu.ph — rename legacy staff-domain demo accounts once.
    /// </summary>
    private static async Task MigrateLegacyOfficerEmailsAsync(TsuOrgDbContext db)
    {
        var renames = new (string From, string To)[]
        {
            ("officer@tsu.edu.ph",  "officer@student.tsu.edu.ph"),
            ("officer2@tsu.edu.ph", "officer2@student.tsu.edu.ph"),
        };

        foreach (var (from, to) in renames)
        {
            var legacy = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == from);
            if (legacy is null) continue;

            var target = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == to);
            if (target is not null)
            {
                // Student-domain account already exists — retire the legacy duplicate so seed can proceed.
                legacy.Email = $"{from}.migrated.{legacy.Id:N}";
                legacy.IsActive = false;
                legacy.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }

            legacy.Email = to;
            legacy.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task SeedDemoUsersAsync(TsuOrgDbContext db, IPasswordHasher hasher)
    {
        var cics = await db.Colleges.FirstAsync(c => c.Code == "CICS");
        var cea  = await db.Colleges.FirstAsync(c => c.Code == "CEA");

        var demoUsers = new (string Email, string FullName, string RoleCode, Guid? CollegeId, string? CollegeCode)[]
        {
            ("officer@student.tsu.edu.ph",  "Officer Demo",   "OrgOfficer",  cics.Id, "CICS"),
            ("adviser@tsu.edu.ph",          "Adviser Demo",   "Adviser",     cics.Id, "CICS"),
            ("dean@tsu.edu.ph",             "Dean Demo",      "Dean",        cics.Id, "CICS"),
            ("officer2@student.tsu.edu.ph", "CEA Officer",    "OrgOfficer",  cea.Id,  "CEA"),
            ("adviser2@tsu.edu.ph",         "CEA Adviser",    "Adviser",     cea.Id,  "CEA"),
            ("sou@tsu.edu.ph",              "SOU Staff Demo", "SouStaff",    null,    null),
            ("admin@tsu.edu.ph",            "System Admin",   "SystemAdmin", null,    null),
        };

        foreach (var (email, fullName, roleCode, collegeId, collegeCode) in demoUsers)
        {
            var existing = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == email);
            if (existing is null)
            {
                // Also skip if a tracked rename already points at this email (not yet saved).
                existing = db.UserAccounts.Local.FirstOrDefault(u =>
                    string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
            }

            if (existing is not null)
            {
                // Backfill college FK on re-seed
                if (existing.CollegeId is null && collegeId is not null)
                {
                    existing.CollegeId = collegeId;
                    existing.College = collegeCode;
                }
                continue;
            }

            var role = await db.Roles.FirstAsync(r => r.Code == roleCode);
            db.UserAccounts.Add(new UserAccount
            {
                Email        = email,
                FullName     = fullName,
                PasswordHash = hasher.Hash("TsuOrg@2026"),
                College      = collegeCode,
                CollegeId    = collegeId,
                IsActive     = true,
                RoleId       = role.Id,
            });
        }
    }

    private static async Task SeedOrganizationsAsync(TsuOrgDbContext db)
    {
        var cics = await db.Colleges.FirstAsync(c => c.Code == "CICS");
        var cea  = await db.Colleges.FirstAsync(c => c.Code == "CEA");
        var officer = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer@student.tsu.edu.ph");
        var adviser = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "adviser@tsu.edu.ph");
        var officer2 = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer2@student.tsu.edu.ph");
        var adviser2 = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "adviser2@tsu.edu.ph");

        await UpsertOrgAsync(db, "Computer Science Society (CSC)", "CSC", cics, "1st", officer?.Id, adviser?.Id);
        await UpsertOrgAsync(db, "Association of Computing Engineers", "ACE", cea, "1st", officer2?.Id, adviser2?.Id);
    }

    private static async Task UpsertOrgAsync(
        TsuOrgDbContext db,
        string name,
        string acronym,
        College college,
        string semester,
        Guid? officerId,
        Guid? adviserId)
    {
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Acronym == acronym);
        if (org is null)
        {
            db.Organizations.Add(new Organization
            {
                Name = name,
                Acronym = acronym,
                College = college.Code,
                CollegeId = college.Id,
                Semester = semester,
                Status = "Active",
                OfficerId = officerId,
                PrimaryAdviserUserId = adviserId,
            });
            return;
        }

        org.CollegeId ??= college.Id;
        org.College ??= college.Code;
        if (org.OfficerId is null && officerId is not null) org.OfficerId = officerId;
        if (org.PrimaryAdviserUserId is null && adviserId is not null) org.PrimaryAdviserUserId = adviserId;
    }

    private static async Task SeedMembershipsAsync(TsuOrgDbContext db)
    {
        var ay = await db.AcademicYears.FirstOrDefaultAsync(a => a.IsCurrent)
                 ?? await db.AcademicYears.FirstOrDefaultAsync();
        if (ay is null) return;

        // Dean is college-scoped — no org membership required.
        var rows = new (string OrgAcronym, string Email, string Position, MembershipRole Role)[]
        {
            ("CSC", "officer@student.tsu.edu.ph",  "President",       MembershipRole.Officer),
            ("CSC", "adviser@tsu.edu.ph",           "Faculty Adviser", MembershipRole.Adviser),
            ("ACE", "officer2@student.tsu.edu.ph", "President",       MembershipRole.Officer),
            ("ACE", "adviser2@tsu.edu.ph",          "Faculty Adviser", MembershipRole.Adviser),
        };

        foreach (var (acronym, email, position, memRole) in rows)
        {
            var org = await db.Organizations.FirstOrDefaultAsync(o => o.Acronym == acronym);
            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == email);
            if (org is null || user is null) continue;

            var existing = await db.OrganizationMemberships.FirstOrDefaultAsync(m =>
                m.OrganizationId == org.Id && m.UserAccountId == user.Id && m.AcademicYearId == ay.Id);

            if (existing is null)
            {
                db.OrganizationMemberships.Add(new OrganizationMembership
                {
                    OrganizationId = org.Id,
                    UserAccountId  = user.Id,
                    PositionTitle  = position,
                    MembershipRole = memRole,
                    AcademicYearId = ay.Id,
                    IsActive       = true,
                });
            }
            else
            {
                // Upgrade legacy rows that only had PositionTitle
                if (existing.MembershipRole == MembershipRole.Member && memRole != MembershipRole.Member)
                    existing.MembershipRole = memRole;
                existing.IsActive = true;
                if (string.IsNullOrWhiteSpace(existing.PositionTitle))
                    existing.PositionTitle = position;
            }
        }

        // Remove mistaken Dean org membership from older seeds (Dean is college-scoped)
        var dean = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "dean@tsu.edu.ph");
        if (dean is not null)
        {
            var deanMemberships = await db.OrganizationMemberships
                .Where(m => m.UserAccountId == dean.Id)
                .ToListAsync();
            if (deanMemberships.Count > 0)
                db.OrganizationMemberships.RemoveRange(deanMemberships);
        }
    }

    private static async Task BackfillCollegeLinksAsync(TsuOrgDbContext db)
    {
        var colleges = await db.Colleges.ToDictionaryAsync(c => c.Code, c => c.Id);

        foreach (var user in await db.UserAccounts.Where(u => u.CollegeId == null && u.College != null).ToListAsync())
        {
            if (colleges.TryGetValue(user.College!, out var id))
                user.CollegeId = id;
        }

        foreach (var org in await db.Organizations.Where(o => o.CollegeId == null && o.College != null).ToListAsync())
        {
            if (colleges.TryGetValue(org.College!, out var id))
                org.CollegeId = id;
        }
    }

    private static async Task SeedAcademicYearAsync(TsuOrgDbContext db)
    {
        if (!await db.AcademicYears.AnyAsync())
        {
            db.AcademicYears.Add(new AcademicYear
            {
                Label     = "2026-2027",
                StartDate = new DateOnly(2026, 8, 1),
                EndDate   = new DateOnly(2027, 6, 30),
                IsCurrent = true,
            });
        }
    }

    private static async Task SeedDocumentTypesAsync(TsuOrgDbContext db)
    {
        if (!await db.DocumentTypes.AnyAsync(t => t.Code == "SF08"))
        {
            db.DocumentTypes.Add(new DocumentType
            {
                Code = "SF08",
                Name = "Activity Proposal (SF08)",
                Description = "Request to Conduct an Activity — primary SOU activity proposal form with supporting attachments.",
                Requirements = new List<DocumentRequirement>
                {
                    Req("ActivityTitle", "Activity Title", false),
                    Req("ActivityDate", "Activity Date", false),
                    Req("ActivityVenue", "Activity Venue", false),
                    Req("ActivityObjectives", "Activity Objectives", false),
                    Req("ExpectedParticipants", "Expected No. of Participants", false),
                    Req("OfficerSignature", "Officer-in-Charge Signature", false),
                    Req("AdviserSignature", "Adviser Signature", false),
                    Req("ActivityProposal", "Activity Proposal Form (PDF)", true),
                    Req("ProgramMatrix", "Program of Activities / Matrix", true),
                    Req("VenueApproval", "Venue Approval / Permit", true),
                    Req("EndorsementLetter", "Endorsement / Supporting Letter", true),
                    Req("ParentConsent", "Parent Consent Form", true, isConditional: true, condition: "if_minors"),
                }
            });
        }

        if (!await db.DocumentTypes.AnyAsync(t => t.Code == "ACCOMPLISHMENT"))
        {
            db.DocumentTypes.Add(new DocumentType
            {
                Code = "ACCOMPLISHMENT",
                Name = "Accomplishment Report",
                Description = "Post-activity accomplishment report to be filed after the event.",
                Requirements = new List<DocumentRequirement>
                {
                    Req("EventTitle", "Event / Activity Title", false),
                    Req("EventDate", "Date of Activity", false),
                    Req("EventVenue", "Venue", false),
                    Req("Objectives", "Objectives", false),
                    Req("Accomplishments", "Accomplishments / Summary", false),
                    Req("AttendeeCount", "Actual No. of Participants", false),
                    Req("OfficerSignature", "Officer-in-Charge Signature", false),
                    Req("AdviserSignature", "Adviser Signature", false),
                    Req("ActivityPhotos", "Activity Documentation / Photos", true),
                    Req("ApprovedSF08", "Approved SF08 Form", true),
                    Req("AttendanceSheet", "Attendance Sheet(s)", true),
                    Req("FinancialLiquidation", "Financial Liquidation Report", true),
                    Req("Certificates", "Certificates (if any)", true, isMandatory: false),
                }
            });
        }

        if (!await db.DocumentTypes.AnyAsync(t => t.Code == "ACCREDITATION"))
        {
            db.DocumentTypes.Add(new DocumentType
            {
                Code = "ACCREDITATION",
                Name = "Accreditation / Application Form",
                Description = "Annual accreditation package for student organizations.",
                Requirements = new List<DocumentRequirement>
                {
                    Req("OrganizationName", "Organization Name", false),
                    Req("College", "College / Department", false),
                    Req("OfficerList", "List of Officers", false),
                    Req("MemberCount", "Number of Members", false),
                    Req("AcademicYear", "Academic Year", false),
                    Req("AdviserSignature", "Adviser Signature", false),
                    Req("PresidentSignature", "President Signature", false),
                    Req("Constitution", "Constitution and By-Laws", true),
                    Req("OrgProfile", "Organization Profile", true),
                    Req("OfficerListDoc", "Formal List of Officers", true),
                    Req("MembershipList", "Membership List", true),
                    Req("AnnualPlan", "General Annual Plan of Activities", true),
                    Req("AdviserEndorsement", "Adviser Endorsement Letter", true),
                }
            });
        }
    }

    private static DocumentRequirement Req(
        string key,
        string display,
        bool isAttachment,
        bool isMandatory = true,
        bool isConditional = false,
        string? condition = null)
        => new()
        {
            RequirementKey = key,
            DisplayName = display,
            IsAttachment = isAttachment,
            IsMandatory = isMandatory,
            IsConditional = isConditional,
            ConditionExpression = condition,
        };

    /// <summary>
    /// Keeps display names + Figure 6 checklist labels current on existing databases.
    /// </summary>
    private static async Task EnsureDocumentTypeCatalogAsync(TsuOrgDbContext db)
    {
        var types = await db.DocumentTypes.Include(t => t.Requirements).ToListAsync();

        foreach (var type in types)
        {
            switch (type.Code)
            {
                case "SF08":
                    type.Name = "Activity Proposal (SF08)";
                    type.Description = "Request to Conduct an Activity — primary SOU activity proposal form with supporting attachments.";
                    EnsureReq(type, "ActivityProposal", "Activity Proposal Form (PDF)", true);
                    EnsureReq(type, "ProgramMatrix", "Program of Activities / Matrix", true);
                    EnsureReq(type, "VenueApproval", "Venue Approval / Permit", true);
                    EnsureReq(type, "EndorsementLetter", "Endorsement / Supporting Letter", true);
                    break;

                case "ACCOMPLISHMENT":
                    type.Name = "Accomplishment Report";
                    EnsureReq(type, "ActivityPhotos", "Activity Documentation / Photos", true);
                    EnsureReq(type, "ApprovedSF08", "Approved SF08 Form", true);
                    EnsureReq(type, "AttendanceSheet", "Attendance Sheet(s)", true);
                    EnsureReq(type, "FinancialLiquidation", "Financial Liquidation Report", true);
                    break;

                case "ACCREDITATION":
                    type.Name = "Accreditation / Application Form";
                    EnsureReq(type, "Constitution", "Constitution and By-Laws", true);
                    EnsureReq(type, "OrgProfile", "Organization Profile", true);
                    EnsureReq(type, "OfficerListDoc", "Formal List of Officers", true);
                    EnsureReq(type, "MembershipList", "Membership List", true);
                    EnsureReq(type, "AnnualPlan", "General Annual Plan of Activities", true);
                    EnsureReq(type, "AdviserEndorsement", "Adviser Endorsement Letter", true);
                    break;
            }
        }
    }

    private static void EnsureReq(DocumentType type, string key, string display, bool isAttachment, bool isMandatory = true)
    {
        var existing = type.Requirements.FirstOrDefault(r => r.RequirementKey == key);
        if (existing is not null)
        {
            existing.DisplayName = display;
            existing.IsAttachment = isAttachment;
            if (isMandatory) existing.IsMandatory = true;
            return;
        }

        type.Requirements.Add(Req(key, display, isAttachment, isMandatory));
    }

    /// <summary>
    /// Demo DMA repository entries so Org Documents is not empty before a full SOU approval cycle.
    /// </summary>
    private static async Task SeedSampleArchiveDocumentsAsync(TsuOrgDbContext db)
    {
        if (await db.Documents.AnyAsync(d =>
                d.Status == DocumentStatus.Archived || d.Status == DocumentStatus.Approved))
            return;

        var ay = await db.AcademicYears.FirstOrDefaultAsync(a => a.IsCurrent)
                 ?? await db.AcademicYears.OrderByDescending(a => a.StartDate).FirstOrDefaultAsync();
        var csc = await db.Organizations.FirstOrDefaultAsync(o => o.Acronym == "CSC");
        var ace = await db.Organizations.FirstOrDefaultAsync(o => o.Acronym == "ACE");
        var officer = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer@student.tsu.edu.ph");
        var officer2 = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer2@student.tsu.edu.ph");
        var sf08 = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Code == "SF08");
        var ar = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Code == "ACCOMPLISHMENT");

        if (ay is null || sf08 is null || officer is null || csc is null)
            return;

        var archiveTypeId = ar?.Id ?? sf08.Id;
        var now = DateTimeOffset.UtcNow;

        db.Documents.Add(new Document
        {
            DocumentNumber = "ORG-2026-ARCH-0001",
            Title = "CSC Leadership Summit 2026",
            OrganizationId = csc.Id,
            DocumentTypeId = sf08.Id,
            AcademicYearId = ay.Id,
            SubmittedByUserId = officer.Id,
            Status = DocumentStatus.Archived,
            CurrentStage = WorkflowStage.Sou,
            PrimaryFileName = "csc-leadership-summit-sf08.pdf",
            CreatedAt = now.AddDays(-40),
            UpdatedAt = now.AddDays(-30),
        });

        db.Documents.Add(new Document
        {
            DocumentNumber = "ORG-2026-ARCH-0002",
            Title = "CSC Year-End Accomplishment Report",
            OrganizationId = csc.Id,
            DocumentTypeId = archiveTypeId,
            AcademicYearId = ay.Id,
            SubmittedByUserId = officer.Id,
            Status = DocumentStatus.Approved,
            CurrentStage = WorkflowStage.Sou,
            PrimaryFileName = "csc-accomplishment-2026.pdf",
            CreatedAt = now.AddDays(-20),
            UpdatedAt = now.AddDays(-12),
        });

        if (ace is not null && officer2 is not null)
        {
            db.Documents.Add(new Document
            {
                DocumentNumber = "ORG-2026-ARCH-0003",
                Title = "ACE Engineering Week Kickoff",
                OrganizationId = ace.Id,
                DocumentTypeId = sf08.Id,
                AcademicYearId = ay.Id,
                SubmittedByUserId = officer2.Id,
                Status = DocumentStatus.Archived,
                CurrentStage = WorkflowStage.Sou,
                PrimaryFileName = "ace-engineering-week-sf08.pdf",
                CreatedAt = now.AddDays(-25),
                UpdatedAt = now.AddDays(-18),
            });
        }
    }

    /// <summary>
    /// Figure 5 demo rows for the Officer Dashboard: under review, approved, returned, submitted, flagged.
    /// </summary>
    private static async Task SeedSampleOfficerDashboardDocumentsAsync(TsuOrgDbContext db)
    {
        if (await db.Documents.AnyAsync(d => d.DocumentNumber.StartsWith("ORG-2026-DASH-")))
            return;

        var ay = await db.AcademicYears.FirstOrDefaultAsync(a => a.IsCurrent)
                 ?? await db.AcademicYears.OrderByDescending(a => a.StartDate).FirstOrDefaultAsync();
        var csc = await db.Organizations.FirstOrDefaultAsync(o => o.Acronym == "CSC");
        var officer = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer@student.tsu.edu.ph");
        var sf08 = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Code == "SF08");
        var ar = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Code == "ACCOMPLISHMENT");
        var accreditation = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Code == "ACCREDITATION");

        if (ay is null || csc is null || officer is null || sf08 is null)
            return;

        var arTypeId = ar?.Id ?? sf08.Id;
        var memberTypeId = accreditation?.Id ?? sf08.Id;
        var now = DateTimeOffset.UtcNow;

        var samples = new (string Number, string Title, Guid TypeId, DocumentStatus Status, WorkflowStage Stage, int DaysAgo)[]
        {
            ("ORG-2026-DASH-0001", "Activity Proposal – SocNight Rehearsal", sf08.Id, DocumentStatus.UnderReview, WorkflowStage.Adviser, 2),
            ("ORG-2026-DASH-0002", "Accomplishment Report – February", arTypeId, DocumentStatus.Approved, WorkflowStage.Done, 18),
            ("ORG-2026-DASH-0003", "Accomplishment Report – March", arTypeId, DocumentStatus.Returned, WorkflowStage.Officer, 8),
            ("ORG-2026-DASH-0004", "Application Form – New Member Reg.", memberTypeId, DocumentStatus.Submitted, WorkflowStage.Calsv, 1),
            ("ORG-2026-DASH-0005", "Activity Proposal – Midyear Booth", sf08.Id, DocumentStatus.Flagged, WorkflowStage.Calsv, 4),
            ("ORG-2026-DASH-0006", "Accomplishment Report – January", arTypeId, DocumentStatus.UnderReview, WorkflowStage.Dean, 6),
        };

        foreach (var s in samples)
        {
            var doc = new Document
            {
                DocumentNumber = s.Number,
                Title = s.Title,
                OrganizationId = csc.Id,
                DocumentTypeId = s.TypeId,
                AcademicYearId = ay.Id,
                SubmittedByUserId = officer.Id,
                Status = s.Status,
                CurrentStage = s.Stage,
                PrimaryFileName = $"{s.Number.ToLowerInvariant()}.pdf",
                CreatedAt = now.AddDays(-s.DaysAgo),
                UpdatedAt = now.AddDays(-Math.Max(0, s.DaysAgo - 1)),
            };
            db.Documents.Add(doc);

            if (s.Status is DocumentStatus.UnderReview or DocumentStatus.Submitted or DocumentStatus.Flagged or DocumentStatus.Returned)
            {
                db.ApprovalWorkflows.Add(new ApprovalWorkflow
                {
                    Document = doc,
                    CurrentStage = s.Stage,
                    IsComplete = false,
                    CreatedAt = doc.CreatedAt,
                    UpdatedAt = doc.UpdatedAt,
                });
            }
            else if (s.Status == DocumentStatus.Approved)
            {
                db.ApprovalWorkflows.Add(new ApprovalWorkflow
                {
                    Document = doc,
                    CurrentStage = WorkflowStage.Done,
                    IsComplete = true,
                    CreatedAt = doc.CreatedAt,
                    UpdatedAt = doc.UpdatedAt,
                });
            }
        }
    }
}
