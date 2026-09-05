using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Common;

/// <summary>
/// Email-domain rules that reinforce role assignment (RBAC).
/// Org officers are student accounts and must use the TSU student mail domain.
/// </summary>
public static class RoleEmailPolicy
{
    public const string StudentDomain = "student.tsu.edu.ph";
    public const string StaffDomain = "tsu.edu.ph";

    public static bool IsStudentEmail(string? email) =>
        EndsWithDomain(email, StudentDomain);

    public static bool IsStaffEmail(string? email) =>
        EndsWithDomain(email, StaffDomain) && !IsStudentEmail(email);

    /// <summary>
    /// Throws when the account email does not match the role’s expected domain.
    /// Currently enforced for <see cref="AppRole.OrgOfficer"/> only.
    /// </summary>
    public static void EnsureEmailMatchesRole(string email, string roleCode)
    {
        if (!string.Equals(roleCode, nameof(AppRole.OrgOfficer), StringComparison.Ordinal))
            return;

        if (!IsStudentEmail(email))
        {
            throw new BusinessRuleException(
                $"Organization officers must sign in with a @{StudentDomain} email.");
        }
    }

    public static void EnsureEmailMatchesRole(string email, AppRole role) =>
        EnsureEmailMatchesRole(email, role.ToString());

    private static bool EndsWithDomain(string? email, string domain)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return false;

        return string.Equals(email[(at + 1)..], domain, StringComparison.OrdinalIgnoreCase);
    }
}
