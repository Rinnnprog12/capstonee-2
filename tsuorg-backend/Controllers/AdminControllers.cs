using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;

namespace TsuOrg.Api.Controllers;

[ApiController]
[Route("api/v1/organizations")]
[Authorize]
public sealed class OrganizationsController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public OrganizationsController(IApplicationDbContext db) => _db = db;

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
                o.Status,
                o.Semester,
                o.OfficerId,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var org = await _db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        return org is null ? NotFound() : Ok(org);
    }

    [HttpPost]
    [Authorize(Policy = "CanManageOrganizations")]
    public async Task<IActionResult> Create([FromBody] UpsertOrgRequest body, CancellationToken ct = default)
    {
        var org = new Organization
        {
            Name      = body.Name.Trim(),
            Acronym   = body.Acronym?.Trim() ?? "",
            College   = body.College,
            Semester  = body.Semester,
            Status    = "Active",
            OfficerId = body.OfficerId,
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

        org.Name      = body.Name.Trim();
        org.Acronym   = body.Acronym?.Trim() ?? org.Acronym;
        org.College   = body.College;
        org.Semester  = body.Semester;
        org.OfficerId = body.OfficerId;
        org.Status    = body.Status ?? org.Status;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(org);
    }
}

public sealed record UpsertOrgRequest(
    string Name,
    string? Acronym,
    string? College,
    string? Semester,
    Guid? OfficerId,
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

        var user = new UserAccount
        {
            Email        = body.Email.ToLower().Trim(),
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
