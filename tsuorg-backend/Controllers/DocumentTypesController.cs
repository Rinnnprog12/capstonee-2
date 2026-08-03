using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document type catalog for structured submission (DSM-03 / DSM-04).
/// </summary>
[ApiController]
[Route("api/v1/document-types")]
[Authorize]
public sealed class DocumentTypesController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public DocumentTypesController(IApplicationDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var types = await _db.DocumentTypes
            .AsNoTracking()
            .Include(t => t.Requirements)
            .OrderBy(t => t.Code)
            .Select(t => new
            {
                t.Id,
                t.Code,
                t.Name,
                t.Description,
                Requirements = t.Requirements
                    .OrderBy(r => r.IsAttachment)
                    .ThenBy(r => r.DisplayName)
                    .Select(r => new
                    {
                        r.Id,
                        r.RequirementKey,
                        r.DisplayName,
                        r.IsAttachment,
                        r.IsMandatory,
                        r.IsConditional,
                        r.ConditionExpression,
                    })
            })
            .ToListAsync(ct);

        return Ok(types);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var type = await _db.DocumentTypes
            .AsNoTracking()
            .Include(t => t.Requirements)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        return type is null ? NotFound() : Ok(type);
    }
}
