using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Auth.Commands;

namespace TsuOrg.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly LoginHandler _login;
    private readonly RefreshTokenHandler _refresh;
    private readonly LogoutHandler _logout;
    private readonly ChangePasswordHandler _changePassword;
    private readonly GetProfileHandler _profile;
    private readonly UpdateProfileHandler _updateProfile;
    private readonly UploadAvatarHandler _uploadAvatar;

    public AuthController(
        LoginHandler login,
        RefreshTokenHandler refresh,
        LogoutHandler logout,
        ChangePasswordHandler changePassword,
        GetProfileHandler profile,
        UpdateProfileHandler updateProfile,
        UploadAvatarHandler uploadAvatar)
    {
        _login = login;
        _refresh = refresh;
        _logout = logout;
        _changePassword = changePassword;
        _profile = profile;
        _updateProfile = updateProfile;
        _uploadAvatar = uploadAvatar;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest body, CancellationToken ct = default)
    {
        try
        {
            var result = await _login.HandleAsync(new LoginCommand(body.Email, body.Password), ct);
            return Ok(result);
        }
        catch (BusinessRuleException ex) { return Unauthorized(Problem(ex.Message)); }
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest body, CancellationToken ct = default)
    {
        try
        {
            var result = await _refresh.HandleAsync(new RefreshTokenCommand(body.RefreshToken), ct);
            return Ok(result);
        }
        catch (BusinessRuleException ex) { return Unauthorized(Problem(ex.Message)); }
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct = default)
    {
        await _logout.HandleAsync(new LogoutCommand(GetUserId()), ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest body, CancellationToken ct = default)
    {
        try
        {
            await _changePassword.HandleAsync(
                new ChangePasswordCommand(GetUserId(), body.CurrentPassword, body.NewPassword), ct);
            return NoContent();
        }
        catch (BusinessRuleException ex) { return UnprocessableEntity(Problem(ex.Message)); }
        catch (NotFoundException ex)     { return NotFound(Problem(ex.Message)); }
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct = default)
    {
        try
        {
            var result = await _profile.HandleAsync(new GetProfileQuery(GetUserId()), ct);
            return Ok(result);
        }
        catch (NotFoundException ex) { return NotFound(Problem(ex.Message)); }
    }

    [HttpPut("me")]
    [Authorize]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileRequest body, CancellationToken ct = default)
    {
        try
        {
            var result = await _updateProfile.HandleAsync(
                new UpdateProfileCommand(GetUserId(), body.FullName), ct);
            return Ok(result);
        }
        catch (BusinessRuleException ex) { return UnprocessableEntity(Problem(ex.Message)); }
        catch (NotFoundException ex)     { return NotFound(Problem(ex.Message)); }
    }

    [HttpPost("me/avatar")]
    [Authorize]
    [RequestSizeLimit(2_500_000)]
    public async Task<IActionResult> UploadAvatar(IFormFile file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return UnprocessableEntity(Problem("Choose a photo to upload."));

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _uploadAvatar.HandleAsync(
                new UploadAvatarCommand(GetUserId(), stream, file.FileName, file.ContentType), ct);
            return Ok(result);
        }
        catch (BusinessRuleException ex) { return UnprocessableEntity(Problem(ex.Message)); }
        catch (NotFoundException ex)     { return NotFound(Problem(ex.Message)); }
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record UpdateProfileRequest(string FullName);
