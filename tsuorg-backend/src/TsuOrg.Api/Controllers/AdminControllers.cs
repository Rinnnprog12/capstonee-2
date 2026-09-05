using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Organizations.Queries;
using TsuOrg.Domain.Entities;

namespace TsuOrg.Api.Controllers;

[ApiController]
[Route("api/v1/organizations")]
[Authorize]
public sealed class OrganizationsController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly GetOrganizationsAdminHandler _adminList;

    public OrganizationsController(IApplicationDbContext db, GetOrganizationsAdminHandler adminList)
    {
        _db = db;
        _adminList = adminList;
    }

    /// <summary>Figure 26: SOU organizations registry with adviser, president, and compliance stats.</summary>
    [HttpGet("admin-list")]
    [Authorize(Roles = "SouStaff,SystemAdmin")]
    public async Task<IActionResult> AdminList(
        [FromQuery] OrganizationsAdminTab tab = OrganizationsAdminTab.Active,
        CancellationToken ct = default)
    {
        var result = await _adminList.HandleAsync(new GetOrganizationsAdminQuery(tab), ct);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var items = await _db.Organizations
            .AsNoTracking()
            .OrderBy(o => o.Name)
            .Select(o => new
            {
                o.Id,
                o.Name,
                o.Acronym,
                o.College,
                o.CollegeId,
                o.Status,
                o.Semester,
                o.OfficerId,
                o.PrimaryAdviserUserId,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>Organizations the current user belongs to (membership or OfficerId).</summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMine(CancellationToken ct = default)
    {
        var userId = GetUserId();

        var membershipOrgIds = await _db.OrganizationMemberships
            .Where(m => m.UserAccountId == userId)
            .Select(m => m.OrganizationId)
            .Distinct()
            .ToListAsync(ct);

        var directOrgIds = await _db.Organizations
            .Where(o => o.OfficerId == userId)
            .Select(o => o.Id)
            .ToListAsync(ct);

        var allIds = membershipOrgIds.Union(directOrgIds).Distinct().ToList();

        var orgs = await _db.Organizations
            .AsNoTracking()
            .Where(o => allIds.Contains(o.Id))
            .OrderBy(o => o.Name)
            .Select(o => new { o.Id, o.Name, o.Acronym, o.College, o.Status, o.Semester })
            .ToListAsync(ct);

        return Ok(orgs);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var org = await _db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        return org is null ? NotFound() : Ok(org);
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    [HttpPost]
    [Authorize(Policy = "CanManageOrganizations")]
    public async Task<IActionResult> Create([FromBody] UpsertOrgRequest body, CancellationToken ct = default)
    {
        var collegeId = await ResolveCollegeIdAsync(body.CollegeId, body.College, ct);
        var org = new Organization
        {
            Name = body.Name.Trim(),
            Acronym = body.Acronym?.Trim() ?? "",
            College = body.College?.Trim(),
            CollegeId = collegeId,
            Semester = body.Semester,
            Status = string.IsNullOrWhiteSpace(body.Status) ? "Active" : body.Status.Trim(),
            OfficerId = body.OfficerId,
            PrimaryAdviserUserId = body.PrimaryAdviserUserId,
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = org.Id }, org);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "CanManageOrganizations")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertOrgRequest body, CancellationToken ct = default)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null) return NotFound();

        org.Name = body.Name.Trim();
        org.Acronym = body.Acronym?.Trim() ?? org.Acronym;
        org.College = body.College?.Trim() ?? org.College;
        org.CollegeId = await ResolveCollegeIdAsync(body.CollegeId, org.College, ct) ?? org.CollegeId;
        org.Semester = body.Semester;
        org.OfficerId = body.OfficerId;
        org.PrimaryAdviserUserId = body.PrimaryAdviserUserId ?? org.PrimaryAdviserUserId;
        org.Status = body.Status ?? org.Status;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(org);
    }

    private async Task<Guid?> ResolveCollegeIdAsync(Guid? collegeId, string? collegeCode, CancellationToken ct)
    {
        if (collegeId is Guid id && id != Guid.Empty)
            return id;

        if (string.IsNullOrWhiteSpace(collegeCode))
            return null;

        var code = collegeCode.Trim();
        return await _db.Colleges
            .AsNoTracking()
            .Where(c => c.Code == code)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }
}

public sealed record UpsertOrgRequest(
    string Name,
    string? Acronym,
    string? College,
    string? Semester,
    Guid? OfficerId,
    Guid? CollegeId = null,
    Guid? PrimaryAdviserUserId = null,
    string? Status = null);

[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = "CanManageSystem")]
public sealed class UsersController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;

    public UsersController(IApplicationDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var users = await _db.UserAccounts
            .Include(u => u.Role)
            .AsNoTracking()
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FullName,
                u.College,
                u.IsActive,
                Role = u.Role!.Code,
            })
            .ToListAsync(ct);
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest body, CancellationToken ct = default)
    {
        if (await _db.UserAccounts.AnyAsync(u => u.Email == body.Email.ToLower().Trim(), ct))
            return UnprocessableEntity(Problem("Email already registered."));

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Code == body.RoleCode, ct);
        if (role is null) return UnprocessableEntity(Problem($"Unknown role '{body.RoleCode}'."));

        var email = body.Email.ToLower().Trim();
        try
        {
            RoleEmailPolicy.EnsureEmailMatchesRole(email, role.Code);
        }
        catch (BusinessRuleException ex)
        {
            return UnprocessableEntity(Problem(ex.Message));
        }

        var user = new UserAccount
        {
            Email        = email,
            FullName     = body.FullName.Trim(),
            College      = body.College,
            PasswordHash = _hasher.Hash(body.Password),
            RoleId       = role.Id,
            IsActive     = true,
        };
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { id = user.Id }, new
        {
            user.Id, user.Email, user.FullName, user.College, Role = role.Code
        });
    }

    [HttpPatch("{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] SetActiveRequest body, CancellationToken ct = default)
    {
        var user = await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        user.IsActive  = body.IsActive;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record CreateUserRequest(string Email, string FullName, string Password, string RoleCode, string? College);
public sealed record SetActiveRequest(bool IsActive);

[ApiController]
[Route("api/v1/audit-logs")]
[Authorize(Policy = "CanManageSystem")]
public sealed class AuditLogsController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public AuditLogsController(IApplicationDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? entityName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var q = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityName))
            q = q.Where(a => a.EntityName == entityName);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { items, totalCount = total, page, pageSize });
    }
}
