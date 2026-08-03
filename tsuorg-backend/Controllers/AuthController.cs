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

    public AuthController(
        LoginHandler login,
        RefreshTokenHandler refresh,
        LogoutHandler logout,
        ChangePasswordHandler changePassword,
        GetProfileHandler profile)
    {
        _login = login;
        _refresh = refresh;
        _logout = logout;
        _changePassword = changePassword;
        _profile = profile;
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

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
}

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
