using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Api.Controllers;

[ApiController]
[Route("api/v1/academic-years")]
[Authorize]
public sealed class AcademicYearsController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public AcademicYearsController(IApplicationDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var items = await _db.AcademicYears
            .AsNoTracking()
            .OrderByDescending(a => a.IsCurrent)
            .ThenByDescending(a => a.StartDate)
            .Select(a => new
            {
                a.Id,
                a.Label,
                a.StartDate,
                a.EndDate,
                a.IsCurrent,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct = default)
    {
        var ay = await _db.AcademicYears
            .AsNoTracking()
            .Where(a => a.IsCurrent)
            .Select(a => new
            {
                a.Id,
                a.Label,
                a.StartDate,
                a.EndDate,
                a.IsCurrent,
            })
            .FirstOrDefaultAsync(ct);

        if (ay is null)
        {
            ay = await _db.AcademicYears
                .AsNoTracking()
                .OrderByDescending(a => a.StartDate)
                .Select(a => new
                {
                    a.Id,
                    a.Label,
                    a.StartDate,
                    a.EndDate,
                    a.IsCurrent,
                })
                .FirstOrDefaultAsync(ct);
        }

        return ay is null ? NotFound(Problem("No academic year configured.")) : Ok(ay);
    }
}
