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
        await SaveSeedAsync(db);

        await SeedCollegesAsync(db);
        await SaveSeedAsync(db);

        await MigrateLegacyOfficerEmailsAsync(db);
        await SaveSeedAsync(db);

        await SeedDemoUsersAsync(db, hasher);
        await SeedAcademicYearAsync(db);
        await SaveSeedAsync(db);

        await SeedOrganizationsAsync(db);
        await SaveSeedAsync(db);

        await SeedDocumentTypesAsync(db);
        await EnsureDocumentTypeCatalogAsync(db);
        await SaveSeedAsync(db);

        await SeedMembershipsAsync(db);
        await BackfillCollegeLinksAsync(db);
        await SaveSeedAsync(db);

        // No demo documents — officer Submit→CALSV→Confirm feeds Adviser/Dean live.
        await PurgeLegacySampleDocumentsAsync(db);
        await SaveSeedAsync(db);
    }

    /// <summary>
    /// MySQL ROW_COUNT() is 0 when an UPDATE sets values that already match after
    /// datetime coercion. EF then throws DbUpdateConcurrencyException. Drop no-op
    /// timestamp dirty flags before saving seed changes.
    /// </summary>
    private static async Task SaveSeedAsync(TsuOrgDbContext db)
    {
        db.ChangeTracker.DetectChanges();

        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.State != EntityState.Modified)
                continue;

            if (entry.Metadata.FindProperty(nameof(BaseEntity.CreatedAt)) is not null)
                entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;

            var meaningful = entry.Properties.Any(p =>
                p.IsModified
                && p.Metadata.Name is not nameof(BaseEntity.CreatedAt)
                && p.Metadata.Name is not nameof(BaseEntity.UpdatedAt));

            if (!meaningful)
            {
                if (entry.Metadata.FindProperty(nameof(BaseEntity.UpdatedAt)) is not null)
                    entry.Property(nameof(BaseEntity.UpdatedAt)).IsModified = false;
                entry.State = EntityState.Unchanged;
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Removes older ORG-2026-DASH-* / ORG-2026-ARCH-* demo rows so dashboards are live-only.
    /// </summary>
    private static async Task PurgeLegacySampleDocumentsAsync(TsuOrgDbContext db)
    {
        var docs = await db.Documents
            .Where(d =>
                d.DocumentNumber.StartsWith("ORG-2026-DASH-")
                || d.DocumentNumber.StartsWith("ORG-2026-ARCH-"))
            .ToListAsync();
        if (docs.Count == 0)
            return;

        var ids = docs.Select(d => d.Id).ToList();

        var workflowIds = await db.ApprovalWorkflows
            .Where(w => ids.Contains(w.DocumentId))
            .Select(w => w.Id)
            .ToListAsync();

        if (workflowIds.Count > 0)
        {
            var histories = await db.ApprovalHistories
                .Where(h => workflowIds.Contains(h.WorkflowId))
                .ToListAsync();
            db.ApprovalHistories.RemoveRange(histories);

            var workflows = await db.ApprovalWorkflows
                .Where(w => workflowIds.Contains(w.Id))
                .ToListAsync();
            db.ApprovalWorkflows.RemoveRange(workflows);
        }

        db.AIValidationResults.RemoveRange(
            await db.AIValidationResults.Where(v => ids.Contains(v.DocumentId)).ToListAsync());
        db.OCRResults.RemoveRange(
            await db.OCRResults.Where(o => ids.Contains(o.DocumentId)).ToListAsync());
        db.ImagePreprocessingLogs.RemoveRange(
            await db.ImagePreprocessingLogs.Where(p => ids.Contains(p.DocumentId)).ToListAsync());
        db.DocumentAttachments.RemoveRange(
            await db.DocumentAttachments.Where(a => ids.Contains(a.DocumentId)).ToListAsync());
        db.DocumentVersions.RemoveRange(
            await db.DocumentVersions.Where(v => ids.Contains(v.DocumentId)).ToListAsync());
        db.TrackingHistories.RemoveRange(
            await db.TrackingHistories.Where(t => ids.Contains(t.DocumentId)).ToListAsync());
        db.OCRErrorLogs.RemoveRange(
            await db.OCRErrorLogs.Where(e => ids.Contains(e.DocumentId)).ToListAsync());

        db.Documents.RemoveRange(docs);
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

    private static readonly (string Code, string Name)[] CollegeCatalog =
    [
        ("SSC",  "Supreme Student Council"),
        ("CCS",  "College of Computer Studies"),
        ("COE",  "College of Engineering"),
        ("CASS", "College of Arts and Social Sciences"),
        ("CCJE", "College of Criminal Justice Education"),
        ("CBA",  "College of Business and Accountancy"),
        ("CAFA", "College of Architecture and Fine Arts"),
        ("COED", "College of Education"),
        ("CIT",  "College of Industrial Technology"),
        ("COS",  "College of Science"),
    ];

    private static async Task SeedCollegesAsync(TsuOrgDbContext db)
    {
        // TSU catalog uses CCS / COE — retire older CICS / CEA codes.
        await RetireCollegeCodeAsync(db, "CICS", "CCS");
        await RetireCollegeCodeAsync(db, "CEA", "COE");
        await db.SaveChangesAsync();

        foreach (var (code, name) in CollegeCatalog)
        {
            var existing = await db.Colleges.FirstOrDefaultAsync(c => c.Code == code);
            if (existing is null)
                db.Colleges.Add(new College { Code = code, Name = name, IsActive = true });
        }
    }

    /// <summary>
    /// Renames <paramref name="fromCode"/> to <paramref name="toCode"/>, or merges
    /// into an already-existing target college and retires the old row.
    /// </summary>
    private static async Task RetireCollegeCodeAsync(TsuOrgDbContext db, string fromCode, string toCode)
    {
        var from = await db.Colleges.FirstOrDefaultAsync(c => c.Code == fromCode);
        if (from is null) return;

        var to = await db.Colleges.FirstOrDefaultAsync(c => c.Code == toCode);
        if (to is null)
        {
            from.Code = toCode;
            from.IsActive = true;
            await RelabelCollegeStringsAsync(db, fromCode, toCode);
            return;
        }

        foreach (var user in await db.UserAccounts.Where(u => u.CollegeId == from.Id).ToListAsync())
        {
            user.CollegeId = to.Id;
            user.College = toCode;
        }

        foreach (var org in await db.Organizations.Where(o => o.CollegeId == from.Id).ToListAsync())
        {
            org.CollegeId = to.Id;
            org.College = toCode;
        }

        await RelabelCollegeStringsAsync(db, fromCode, toCode);

        from.IsActive = false;
        from.Code = $"{fromCode}-RETIRED";
        from.Name = $"{from.Name} (retired)";
        from.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task RelabelCollegeStringsAsync(TsuOrgDbContext db, string fromCode, string toCode)
    {
        foreach (var user in await db.UserAccounts.Where(u => u.College == fromCode).ToListAsync())
            user.College = toCode;

        foreach (var org in await db.Organizations.Where(o => o.College == fromCode).ToListAsync())
            org.College = toCode;
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
        var ssc = await db.Colleges.FirstAsync(c => c.Code == "SSC");
        var coe = await db.Colleges.FirstAsync(c => c.Code == "COE");

        var demoUsers = new (string Email, string FullName, string RoleCode, Guid? CollegeId, string? CollegeCode)[]
        {
            ("officer@student.tsu.edu.ph",  "Officer Demo",   "OrgOfficer",  ssc.Id, "SSC"),
            ("adviser@tsu.edu.ph",          "Adviser Demo",   "Adviser",     ssc.Id, "SSC"),
            ("dean@tsu.edu.ph",             "Dean Demo",      "Dean",        ssc.Id, "SSC"),
            ("officer2@student.tsu.edu.ph", "COE Officer",    "OrgOfficer",  coe.Id, "COE"),
            ("adviser2@tsu.edu.ph",         "COE Adviser",    "Adviser",     coe.Id, "COE"),
            ("sou@tsu.edu.ph",              "SOU Staff Demo", "SouStaff",    null,   null),
            ("admin@tsu.edu.ph",            "System Admin",   "SystemAdmin", null,   null),
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
                // Keep phpMyAdmin / admin edits. Only fill a missing college on first backfill.
                if (existing.CollegeId is null && collegeId is not null)
                {
                    existing.CollegeId = collegeId;
                    existing.College = collegeCode;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
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
        var colleges = await db.Colleges.Where(c => c.IsActive).ToDictionaryAsync(c => c.Code, c => c);
        College Col(string code) => colleges[code];

        var officer = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer@student.tsu.edu.ph");
        var adviser = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "adviser@tsu.edu.ph");
        var officer2 = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "officer2@student.tsu.edu.ph");
        var adviser2 = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == "adviser2@tsu.edu.ph");

        await UpsertOrgAsync(db, "Supreme Student Council", "SSC", Col("SSC"), "1st", officer?.Id, adviser?.Id);
        await UpsertOrgAsync(db, "Programmers Den", "PD", Col("CCS"), "1st", null, null);
        await UpsertOrgAsync(db, "Artist Circle", "ACIRC", Col("CCS"), "1st", null, null);
        await UpsertOrgAsync(db, "CBA - SC", "CBA-SC", Col("CBA"), "1st", null, null);
        await UpsertOrgAsync(db, "CCS - SC", "CCS-SC", Col("CCS"), "1st", null, null);
        await UpsertOrgAsync(db, "CCJE - SC", "CCJE-SC", Col("CCJE"), "1st", null, null);
        await UpsertOrgAsync(db, "CASS - SC", "CASS-SC", Col("CASS"), "1st", null, null);
        await UpsertOrgAsync(db, "COED - SC", "COED-SC", Col("COED"), "1st", null, null);
        await UpsertOrgAsync(db, "CAFA - SC", "CAFA-SC", Col("CAFA"), "1st", null, null);
        await UpsertOrgAsync(db, "CIT - SC", "CIT-SC", Col("CIT"), "1st", null, null);
        await UpsertOrgAsync(db, "COS - SC", "COS-SC", Col("COS"), "1st", null, null);
        await UpsertOrgAsync(db, "COE - SC", "COE-SC", Col("COE"), "1st", null, null);
        await UpsertOrgAsync(db, "Computer Science Society (CSC)", "CSC", Col("CCS"), "1st", null, null);
        await UpsertOrgAsync(db, "Association of Computing Engineers", "ACE", Col("COE"), "1st", officer2?.Id, adviser2?.Id);
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
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Acronym == acronym)
                  ?? db.Organizations.Local.FirstOrDefault(o =>
                      string.Equals(o.Acronym, acronym, StringComparison.OrdinalIgnoreCase));
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

        // Existing row is source of truth (phpMyAdmin / SOU edits). Fill blanks only.
        if (org.CollegeId is null)
        {
            org.CollegeId = college.Id;
            org.College = college.Code;
        }
        if (org.OfficerId is null && officerId is not null)
            org.OfficerId = officerId;
        if (org.PrimaryAdviserUserId is null && adviserId is not null)
            org.PrimaryAdviserUserId = adviserId;
    }

    private static async Task SeedMembershipsAsync(TsuOrgDbContext db)
    {
        var ay = await db.AcademicYears.FirstOrDefaultAsync(a => a.IsCurrent)
                 ?? await db.AcademicYears.FirstOrDefaultAsync();
        if (ay is null) return;

        // Dean is college-scoped — no org membership required.
        var rows = new (string OrgAcronym, string Email, string Position, MembershipRole Role)[]
        {
            ("SSC", "officer@student.tsu.edu.ph",  "President",       MembershipRole.Officer),
            ("SSC", "adviser@tsu.edu.ph",           "Faculty Adviser", MembershipRole.Adviser),
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

        foreach (var org in await db.Organizations.Include(o => o.CollegeRef).ToListAsync())
        {
            if (org.CollegeId is null && !string.IsNullOrWhiteSpace(org.College)
                && colleges.TryGetValue(org.College, out var id))
            {
                org.CollegeId = id;
            }

            if (org.CollegeRef is not null && string.IsNullOrWhiteSpace(org.College))
                org.College = org.CollegeRef.Code;
        }
    }

    private static readonly (string Label, DateOnly Start, DateOnly End)[] AcademicYearCatalog =
    [
        ("2022-2023", new DateOnly(2022, 8, 1), new DateOnly(2023, 6, 30)),
        ("2023-2024", new DateOnly(2023, 8, 1), new DateOnly(2024, 6, 30)),
        ("2024-2025", new DateOnly(2024, 8, 1), new DateOnly(2025, 6, 30)),
        ("2025-2026", new DateOnly(2025, 8, 1), new DateOnly(2026, 6, 30)),
        ("2026-2027", new DateOnly(2026, 8, 1), new DateOnly(2027, 6, 30)),
    ];

    private static async Task SeedAcademicYearAsync(TsuOrgDbContext db)
    {
        var existing = await db.AcademicYears.ToListAsync();
        var byLabel = existing.ToDictionary(a => a.Label, StringComparer.OrdinalIgnoreCase);

        foreach (var (label, start, end) in AcademicYearCatalog)
        {
            if (byLabel.ContainsKey(label))
                continue;

            var ay = new AcademicYear
            {
                Label     = label,
                StartDate = start,
                EndDate   = end,
                IsCurrent = false,
            };
            db.AcademicYears.Add(ay);
            byLabel[label] = ay;
        }

        if (byLabel.Count == 0)
            return;

        // Respect an already-chosen current year (phpMyAdmin / SOU).
        if (byLabel.Values.Any(a => a.IsCurrent))
            return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var current = byLabel.Values.FirstOrDefault(a => a.StartDate <= today && today <= a.EndDate)
                      ?? byLabel.Values.OrderByDescending(a => a.StartDate).First();
        current.IsCurrent = true;
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
                    Req("ProgramMatrix", "Program of Activities / Matrix", true, isMandatory: false),
                    Req("VenueApproval", "Venue Approval / Permit", true, isMandatory: false),
                    Req("EndorsementLetter", "Endorsement / Supporting Letter", true, isMandatory: false),
                    Req("ParentConsent", "Parent Consent Form", true, isMandatory: false, isConditional: true, condition: "if_minors"),
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
    /// Uses ExecuteUpdate for existing rows so MySQL no-op UPDATEs cannot trip EF concurrency.
    /// </summary>
    private static async Task EnsureDocumentTypeCatalogAsync(TsuOrgDbContext db)
    {
        var types = await db.DocumentTypes.AsNoTracking().ToListAsync();

        foreach (var type in types)
        {
            switch (type.Code)
            {
                case "SF08":
                    await PatchDocumentTypeAsync(db, type.Id, "Activity Proposal (SF08)",
                        "Request to Conduct an Activity — primary SOU activity proposal form with supporting attachments.");
                    await EnsureReqAsync(db, type.Id, "ActivityProposal", "Activity Proposal Form (PDF)", true);
                    await EnsureReqAsync(db, type.Id, "ProgramMatrix", "Program of Activities / Matrix", true, isMandatory: false);
                    await EnsureReqAsync(db, type.Id, "VenueApproval", "Venue Approval / Permit", true, isMandatory: false);
                    await EnsureReqAsync(db, type.Id, "EndorsementLetter", "Endorsement / Supporting Letter", true, isMandatory: false);
                    await EnsureReqAsync(db, type.Id, "ParentConsent", "Parent Consent Form", true, isMandatory: false, isConditional: true);
                    break;

                case "ACCOMPLISHMENT":
                    await PatchDocumentTypeAsync(db, type.Id, "Accomplishment Report", null);
                    await EnsureReqAsync(db, type.Id, "ActivityPhotos", "Activity Documentation / Photos", true);
                    await EnsureReqAsync(db, type.Id, "ApprovedSF08", "Approved SF08 Form", true);
                    await EnsureReqAsync(db, type.Id, "AttendanceSheet", "Attendance Sheet(s)", true);
                    await EnsureReqAsync(db, type.Id, "FinancialLiquidation", "Financial Liquidation Report", true);
                    break;

                case "ACCREDITATION":
                    await PatchDocumentTypeAsync(db, type.Id, "Accreditation / Application Form", null);
                    await EnsureReqAsync(db, type.Id, "Constitution", "Constitution and By-Laws", true);
                    await EnsureReqAsync(db, type.Id, "OrgProfile", "Organization Profile", true);
                    await EnsureReqAsync(db, type.Id, "OfficerListDoc", "Formal List of Officers", true);
                    await EnsureReqAsync(db, type.Id, "MembershipList", "Membership List", true);
                    await EnsureReqAsync(db, type.Id, "AnnualPlan", "General Annual Plan of Activities", true);
                    await EnsureReqAsync(db, type.Id, "AdviserEndorsement", "Adviser Endorsement Letter", true);
                    break;
            }
        }
    }

    private static async Task PatchDocumentTypeAsync(TsuOrgDbContext db, Guid typeId, string name, string? description)
    {
        if (description is null)
        {
            await db.DocumentTypes
                .Where(t => t.Id == typeId && t.Name != name)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, name));
            return;
        }

        await db.DocumentTypes
            .Where(t => t.Id == typeId && (t.Name != name || t.Description != description))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Name, name)
                .SetProperty(t => t.Description, description));
    }

    private static async Task EnsureReqAsync(
        TsuOrgDbContext db,
        Guid typeId,
        string key,
        string display,
        bool isAttachment,
        bool isMandatory = true,
        bool isConditional = false)
    {
        var existing = await db.DocumentRequirements.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DocumentTypeId == typeId && r.RequirementKey == key);

        if (existing is null)
        {
            var row = Req(key, display, isAttachment, isMandatory, isConditional);
            row.DocumentTypeId = typeId;
            db.DocumentRequirements.Add(row);
            return;
        }

        if (existing.DisplayName == display
            && existing.IsAttachment == isAttachment
            && existing.IsMandatory == isMandatory
            && existing.IsConditional == isConditional)
            return;

        await db.DocumentRequirements
            .Where(r => r.Id == existing.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DisplayName, display)
                .SetProperty(r => r.IsAttachment, isAttachment)
                .SetProperty(r => r.IsMandatory, isMandatory)
                .SetProperty(r => r.IsConditional, isConditional));
    }
}
