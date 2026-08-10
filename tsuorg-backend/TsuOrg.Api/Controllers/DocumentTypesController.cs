using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document type catalog for structured submission (DSM-03 / DSM-04 / Figure 6).
/// Dropdown + Required Checklist data for the New Submission Select Type step.
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
        var rows = await _db.DocumentTypes
            .AsNoTracking()
            .Include(t => t.Requirements)
            .ToListAsync(ct);

        // Figure 6 dropdown order: Activity Proposal → Accomplishment → Accreditation
        var types = rows
            .OrderBy(t => t.Code switch
            {
                "SF08" => 0,
                "ACCOMPLISHMENT" => 1,
                "ACCREDITATION" => 2,
                _ => 9,
            })
            .ThenBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Code,
                t.Name,
                t.Description,
                Requirements = t.Requirements
                    .OrderByDescending(r => r.IsAttachment)
                    .ThenByDescending(r => r.IsMandatory)
                    .ThenBy(r => r.IsConditional)
                    .ThenBy(r => ChecklistRank(t.Code, r.RequirementKey))
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
                    .ToList(),
            })
            .ToList();

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

    /// <summary>Figure 6 checklist order for known SOU attachment keys.</summary>
    private static int ChecklistRank(string typeCode, string key) => typeCode switch
    {
        "ACCOMPLISHMENT" => key switch
        {
            "ActivityPhotos" => 0,
            "ApprovedSF08" => 1,
            "AttendanceSheet" => 2,
            "FinancialLiquidation" => 3,
            _ => 50,
        },
        "SF08" => key switch
        {
            "ActivityProposal" => 0,
            "ProgramMatrix" => 1,
            "VenueApproval" => 2,
            "EndorsementLetter" => 3,
            "ParentConsent" => 4,
            _ => 50,
        },
        "ACCREDITATION" => key switch
        {
            "Constitution" => 0,
            "OrgProfile" => 1,
            "OfficerListDoc" => 2,
            "MembershipList" => 3,
            "AnnualPlan" => 4,
            "AdviserEndorsement" => 5,
            _ => 50,
        },
        _ => 50,
    };
}
