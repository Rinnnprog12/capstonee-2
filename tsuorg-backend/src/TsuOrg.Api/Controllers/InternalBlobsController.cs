using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Infrastructure.Services;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Time-limited blob reads for CALSV/ML. No JWT — HMAC query signature only.
/// </summary>
[ApiController]
[Route("api/v1/internal/blobs")]
[AllowAnonymous]
public sealed class InternalBlobsController : ControllerBase
{
    private readonly IBlobService _blob;
    private readonly LocalBlobAccess _access;

    public InternalBlobsController(IBlobService blob, LocalBlobAccess access)
    {
        _blob = blob;
        _access = access;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string p,
        [FromQuery] long exp,
        [FromQuery] string sig,
        CancellationToken ct)
    {
        if (!_access.TryValidate(p, exp, sig, out var blobPath))
            return Unauthorized();

        try
        {
            var (stream, contentType) = await _blob.DownloadWithContentTypeAsync(blobPath, ct);
            return File(stream, contentType, enableRangeProcessing: false);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest();
        }
    }
}
