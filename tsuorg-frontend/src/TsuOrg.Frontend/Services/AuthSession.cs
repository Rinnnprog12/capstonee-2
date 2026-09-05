using System.Security.Claims;
using TsuOrg.Frontend.Models;

namespace TsuOrg.Frontend.Services;

/// <summary>
/// UI session state hydrated from JWT /auth/login. Role always comes from the backend.
/// </summary>
public sealed class AuthSession
{
    public Guid? UserId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public string Subtitle { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    /// <summary>API marker that an avatar exists (not a browsable &lt;img&gt; URL).</summary>
    public string? AvatarUrl { get; private set; }
    /// <summary>Authenticated data URL for display in Account / sidebar.</summary>
    public string? AvatarDataUrl { get; private set; }
    public string? College { get; private set; }
    public string PortalLabel { get; private set; } = "Organization Portal";
    public bool IsSignedIn { get; private set; }

    public event Action? Changed;

    public string Initials => string.IsNullOrWhiteSpace(FullName)
        ? "?"
        : string.Concat(
            FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>Friendly role label for UI badges (e.g. SOU Admin).</summary>
    public string RoleDisplayName => Role switch
    {
        "OrgOfficer" => "Org Officer",
        "Adviser" => "Adviser",
        "Dean" => "Dean",
        "SouStaff" => "SOU Admin",
        "SystemAdmin" => "System Admin",
        _ => string.IsNullOrWhiteSpace(Role) ? "User" : Role,
    };

    /// <summary>Primary org/unit line under the name.</summary>
    public string ProfileLine1 => Role switch
    {
        "SouStaff" => "Student Organizations Unit",
        "SystemAdmin" => "System Administration",
        "Adviser" => string.IsNullOrWhiteSpace(College) ? "Faculty Adviser" : College,
        "Dean" => string.IsNullOrWhiteSpace(College) ? "College Dean" : College,
        _ => string.IsNullOrWhiteSpace(College) ? "Student Organization" : College,
    };

    /// <summary>Secondary office/context line under ProfileLine1.</summary>
    public string ProfileLine2 => Role switch
    {
        "SouStaff" => "Office of Student Affairs",
        "SystemAdmin" => "TSU-ORGDOCX Administration",
        "Adviser" => "Faculty Adviser",
        "Dean" => "College Dean",
        "OrgOfficer" => "Organization Officer",
        _ => PortalLabel,
    };

    public bool IsInRole(params string[] roles) =>
        roles.Length == 0 || roles.Contains(Role, StringComparer.Ordinal);

    /// <summary>OrgOfficer accounts must use the TSU student mail domain.</summary>
    public bool HasValidOfficerEmail =>
        Role != "OrgOfficer" || IsStudentEmail(Email);

    public static bool IsStudentEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return false;
        return string.Equals(email[(at + 1)..], "student.tsu.edu.ph", StringComparison.OrdinalIgnoreCase);
    }

    public void Apply(LoginResponse login)
    {
        UserId = login.UserId;
        Role = NormalizeRole(login.Role);
        FullName = login.FullName;
        Email = login.Email;
        AvatarUrl = null;
        AvatarDataUrl = null;
        College = login.College;
        (Subtitle, PortalLabel) = LabelsFor(Role, College);
        IsSignedIn = true;
        Changed?.Invoke();
    }

    public void ApplyFromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            Clear();
            return;
        }

        var role = principal.FindFirst(ClaimTypes.Role)?.Value
                   ?? principal.FindFirst("role")?.Value
                   ?? string.Empty;

        UserId = Guid.TryParse(
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value,
            out var id)
            ? id
            : null;
        Role = NormalizeRole(role);
        FullName = principal.FindFirst(ClaimTypes.Name)?.Value
                   ?? principal.FindFirst("name")?.Value
                   ?? Email;
        Email = principal.FindFirst(ClaimTypes.Email)?.Value
                ?? principal.FindFirst("email")?.Value
                ?? string.Empty;
        // Avatar comes from /auth/me — keep existing if token refresh only.
        (Subtitle, PortalLabel) = LabelsFor(Role, College);
        IsSignedIn = true;
        Changed?.Invoke();
    }

    public void ApplyProfile(UserProfile profile)
    {
        UserId = profile.Id;
        Role = NormalizeRole(profile.Role);
        FullName = profile.FullName;
        Email = profile.Email;
        AvatarUrl = profile.AvatarUrl;
        if (string.IsNullOrWhiteSpace(profile.AvatarUrl))
            AvatarDataUrl = null;
        College = profile.College;
        (Subtitle, PortalLabel) = LabelsFor(Role, College);
        IsSignedIn = true;
        Changed?.Invoke();
    }

    public void SetAvatarDataUrl(string? dataUrl)
    {
        AvatarDataUrl = dataUrl;
        Changed?.Invoke();
    }

    public void Clear()
    {
        UserId = null;
        Role = string.Empty;
        FullName = string.Empty;
        Subtitle = string.Empty;
        Email = string.Empty;
        AvatarUrl = null;
        AvatarDataUrl = null;
        College = null;
        PortalLabel = "Organization Portal";
        IsSignedIn = false;
        Changed?.Invoke();
    }

    public static string HomePathForRole(string role) => NormalizeRole(role) switch
    {
        "Adviser" or "Dean" => "/review",
        "SouStaff" or "SystemAdmin" => "/sou",
        _ => "/officer",
    };

    public string HomePath => HomePathForRole(Role);

    private static string NormalizeRole(string role) => role switch
    {
        "SOU Admin" => "SouStaff",
        _ => role,
    };

    private static (string Subtitle, string PortalLabel) LabelsFor(string role, string? college) =>
        role switch
        {
            "Adviser" => (college ?? "Faculty Adviser", "Organization Portal"),
            "Dean" => (college ?? "College Dean", "Organization Portal"),
            "SouStaff" => ("Student Organizations Unit", "SOU Portal"),
            "SystemAdmin" => ("System Administrator", "Admin Portal"),
            _ => (college is { Length: > 0 } ? college : "Organization Officer", "Organization Portal"),
        };
}
