using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Features.Settings;
using TsuOrg.Application.Features.Tracking.Queries;

namespace TsuOrg.Api.Controllers;

/// <summary>Figure 27 — SOU System Reports &amp; Analytics.</summary>
[ApiController]
[Route("api/v1/reports")]
[Authorize(Roles = "SouStaff,SystemAdmin")]
public sealed class ReportsController : ControllerBase
{
    private readonly GetSystemReportsHandler _summary;
    private readonly ExportReportHandler _export;

    public ReportsController(GetSystemReportsHandler summary, ExportReportHandler export)
    {
        _summary = summary;
        _export = export;
    }

    /// <summary>Monthly submission trend + pipeline turnaround averages.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct = default) =>
        Ok(await _summary.HandleAsync(ct));

    /// <summary>
    /// CSV export. Types: compliance-summary | audit-trail | organization-status | adviser-performance.
    /// </summary>
    [HttpGet("export/{type}")]
    public async Task<IActionResult> Export(string type, CancellationToken ct = default)
    {
        var result = await _export.HandleAsync(type, ct);
        if (result is null)
            return NotFound(new { message = $"Unknown report type '{type}'." });

        var (fileName, csv) = result.Value;
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
    }
}

/// <summary>Figure 29 — SOU System Settings &amp; Configuration.</summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Roles = "SouStaff,SystemAdmin")]
public sealed class SettingsController : ControllerBase
{
    private readonly GetSystemSettingsHandler _get;
    private readonly UpdateSystemSettingsHandler _update;

    public SettingsController(GetSystemSettingsHandler get, UpdateSystemSettingsHandler update)
    {
        _get = get;
        _update = update;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct = default) =>
        Ok(await _get.HandleAsync(ct));

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] SystemSettingsDto body, CancellationToken ct = default) =>
        Ok(await _update.HandleAsync(body, ct));
}
