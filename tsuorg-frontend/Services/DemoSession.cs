namespace TsuOrg.Frontend.Services;

/// <summary>
/// Static-demo session used by storyboard UI before real JWT wiring.
/// </summary>
public sealed class DemoSession
{
    public string Role { get; private set; } = "OrgOfficer";
    public string FullName { get; private set; } = "Harvey Barrozo";
    public string Subtitle { get; private set; } = "Artist Circle · Multimedia Officer";
    public string Email { get; private set; } = "org.officer@tsu.edu.ph";
    public string PortalLabel { get; private set; } = "Organization Portal";
    public bool IsSignedIn { get; private set; }

    public event Action? Changed;

    public void SignInAs(string role, string? email = null)
    {
        Role = role;
        Email = email ?? DefaultEmail(role);
        (FullName, Subtitle, PortalLabel) = role switch
        {
            "Adviser" => ("Dr. Harvey Barrozo", "College of Computer Studies", "Organization Portal"),
            "Dean" => ("Dean Harvey Barrozo", "College of Computer Studies", "Organization Portal"),
            "SouStaff" or "SOU Admin" => ("SOU Administrator", "Student Organizations Unit", "SOU Portal"),
            _ => ("Harvey Barrozo", "Artist Circle · Multimedia Officer", "Organization Portal"),
        };
        if (role is "SouStaff" or "SOU Admin") Role = "SouStaff";
        IsSignedIn = true;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        IsSignedIn = false;
        Changed?.Invoke();
    }

    public string Initials => string.Concat(
        FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(p => char.ToUpperInvariant(p[0])));

    private static string DefaultEmail(string role) => role switch
    {
        "Adviser" => "adviser@tsu.edu.ph",
        "Dean" => "dean@tsu.edu.ph",
        "SouStaff" or "SOU Admin" => "sou@tsu.edu.ph",
        _ => "officer@tsu.edu.ph",
    };
}
