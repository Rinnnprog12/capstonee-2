using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Application.Features.Auth.Commands;

// ─── Login ───────────────────────────────────────────────────────────────────

public sealed record LoginCommand(string Email, string Password);

public sealed record LoginResult(
    string AccessToken,
    string RefreshToken,
    string Email,
    string FullName,
    string Role,
    Guid UserId,
    string? College);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;

    public LoginHandler(IApplicationDbContext db, IPasswordHasher hasher, IJwtTokenService jwt)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
    }

    public async Task<LoginResult> HandleAsync(LoginCommand cmd, CancellationToken ct = default)
    {
        new LoginValidator().ValidateAndThrow(cmd);

        var user = await _db.UserAccounts
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Email == cmd.Email.ToLower().Trim(), ct)
            ?? throw new BusinessRuleException("Invalid email or password.");

        if (!user.IsActive)
            throw new BusinessRuleException("Account is deactivated. Contact your system administrator.");

        if (!_hasher.Verify(cmd.Password, user.PasswordHash))
            throw new BusinessRuleException("Invalid email or password.");

        var roleName = user.Role?.Code ?? "OrgOfficer";
        RoleEmailPolicy.EnsureEmailMatchesRole(user.Email, roleName);

        var access   = _jwt.GenerateAccessToken(user.Id, user.Email, roleName, user.FullName);
        var refresh  = _jwt.GenerateRefreshToken();

        user.RefreshTokenHash      = HashToken(refresh);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(14);
        user.UpdatedAt             = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new LoginResult(access, refresh, user.Email, user.FullName, roleName, user.Id, user.College);
    }

    internal static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}

// ─── Refresh ─────────────────────────────────────────────────────────────────

public sealed record RefreshTokenCommand(string RefreshToken);

public sealed class RefreshTokenHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IJwtTokenService _jwt;

    public RefreshTokenHandler(IApplicationDbContext db, IJwtTokenService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    public async Task<LoginResult> HandleAsync(RefreshTokenCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.RefreshToken))
            throw new BusinessRuleException("Refresh token is required.");

        var hash = LoginHandler.HashToken(cmd.RefreshToken);

        var user = await _db.UserAccounts
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.RefreshTokenHash == hash, ct)
            ?? throw new BusinessRuleException("Invalid refresh token.");

        if (!user.IsActive)
            throw new BusinessRuleException("Account is deactivated.");

        if (user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
            throw new BusinessRuleException("Refresh token expired. Please log in again.");

        var roleName = user.Role?.Code ?? "OrgOfficer";
        RoleEmailPolicy.EnsureEmailMatchesRole(user.Email, roleName);

        var access   = _jwt.GenerateAccessToken(user.Id, user.Email, roleName, user.FullName);
        var refresh  = _jwt.GenerateRefreshToken();

        user.RefreshTokenHash      = LoginHandler.HashToken(refresh);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(14);
        user.UpdatedAt             = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new LoginResult(access, refresh, user.Email, user.FullName, roleName, user.Id, user.College);
    }
}

// ─── Logout ──────────────────────────────────────────────────────────────────

public sealed record LogoutCommand(Guid UserId);

public sealed class LogoutHandler
{
    private readonly IApplicationDbContext _db;

    public LogoutHandler(IApplicationDbContext db) => _db = db;

    public async Task HandleAsync(LogoutCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct);
        if (user is null) return;

        user.RefreshTokenHash      = null;
        user.RefreshTokenExpiresAt = null;
        user.UpdatedAt             = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

// ─── Change password ─────────────────────────────────────────────────────────

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword);

public sealed class ChangePasswordHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;

    public ChangePasswordHandler(IApplicationDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task HandleAsync(ChangePasswordCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.NewPassword) || cmd.NewPassword.Length < 8)
            throw new BusinessRuleException("New password must be at least 8 characters.");

        var user = await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct)
            ?? throw new NotFoundException("UserAccount", cmd.UserId);

        if (!_hasher.Verify(cmd.CurrentPassword, user.PasswordHash))
            throw new BusinessRuleException("Current password is incorrect.");

        user.PasswordHash          = _hasher.Hash(cmd.NewPassword);
        user.RefreshTokenHash      = null; // force re-login
        user.RefreshTokenExpiresAt = null;
        user.UpdatedAt             = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

// ─── Profile ─────────────────────────────────────────────────────────────────

public sealed record GetProfileQuery(Guid UserId);
public sealed record UpdateProfileCommand(Guid UserId, string FullName);
public sealed record UploadAvatarCommand(Guid UserId, Stream Content, string FileName, string ContentType);

public sealed record UserProfileDto(
    Guid Id,
    string Email,
    string FullName,
    string? College,
    string Role,
    string? AvatarUrl);

public sealed class GetProfileHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;

    public GetProfileHandler(IApplicationDbContext db, IBlobService blob)
    {
        _db = db;
        _blob = blob;
    }

    public async Task<UserProfileDto> HandleAsync(GetProfileQuery q, CancellationToken ct = default)
    {
        var user = await _db.UserAccounts
            .Include(u => u.Role)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == q.UserId, ct)
            ?? throw new NotFoundException("UserAccount", q.UserId);

        string? avatarUrl = null;
        if (!string.IsNullOrWhiteSpace(user.AvatarBlobPath))
        {
            try
            {
                avatarUrl = await _blob.GetReadUrlAsync(user.AvatarBlobPath, TimeSpan.FromHours(6), ct);
            }
            catch
            {
                avatarUrl = null;
            }
        }

        return new UserProfileDto(
            user.Id,
            user.Email,
            user.FullName,
            user.College,
            user.Role?.Code ?? "",
            avatarUrl);
    }
}

public sealed class UpdateProfileHandler
{
    private readonly IApplicationDbContext _db;
    private readonly GetProfileHandler _profile;

    public UpdateProfileHandler(IApplicationDbContext db, GetProfileHandler profile)
    {
        _db = db;
        _profile = profile;
    }

    public async Task<UserProfileDto> HandleAsync(UpdateProfileCommand cmd, CancellationToken ct = default)
    {
        var name = cmd.FullName?.Trim() ?? string.Empty;
        if (name.Length < 2)
            throw new BusinessRuleException("Full name must be at least 2 characters.");
        if (name.Length > 256)
            throw new BusinessRuleException("Full name is too long.");

        var user = await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct)
            ?? throw new NotFoundException("UserAccount", cmd.UserId);

        user.FullName = name;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return await _profile.HandleAsync(new GetProfileQuery(cmd.UserId), ct);
    }
}

public sealed class UploadAvatarHandler
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif",
    };

    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;
    private readonly GetProfileHandler _profile;

    public UploadAvatarHandler(IApplicationDbContext db, IBlobService blob, GetProfileHandler profile)
    {
        _db = db;
        _blob = blob;
        _profile = profile;
    }

    public async Task<UserProfileDto> HandleAsync(UploadAvatarCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Content.CanSeek && cmd.Content.Length > 2 * 1024 * 1024)
            throw new BusinessRuleException("Profile photo must be 2 MB or smaller.");

        var contentType = string.IsNullOrWhiteSpace(cmd.ContentType) ? "application/octet-stream" : cmd.ContentType;
        if (!AllowedTypes.Contains(contentType))
            throw new BusinessRuleException("Profile photo must be JPEG, PNG, WebP, or GIF.");

        var user = await _db.UserAccounts.FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct)
            ?? throw new NotFoundException("UserAccount", cmd.UserId);

        var oldPath = user.AvatarBlobPath;
        var blobPath = await _blob.UploadAsync(cmd.Content, cmd.FileName, contentType, ct);
        user.AvatarBlobPath = blobPath;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(oldPath))
        {
            try { await _blob.DeleteAsync(oldPath, ct); }
            catch { /* best-effort cleanup */ }
        }

        return await _profile.HandleAsync(new GetProfileQuery(cmd.UserId), ct);
    }
}
