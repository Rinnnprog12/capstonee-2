using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Api.Controllers;

[ApiController]
[Route("api/v1/colleges")]
[Authorize]
public sealed class CollegesController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public CollegesController(IApplicationDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var items = await _db.Colleges
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Code, c.Name })
            .ToListAsync(ct);
        return Ok(items);
    }
}
