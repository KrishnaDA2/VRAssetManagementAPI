namespace VRAssetManagement.Domain.Entities;

public class AppUser
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? GoogleId { get; set; }

    public string? PasswordHash { get; set; }

    public string GlobalRole { get; set; } = "Developer";

    public bool Active { get; set; } = true;

    // Individual permissions stored as JSON in Users table.
    public string PermissionsJson { get; set; } = "[]";

    public ICollection<ProjectMember> Memberships { get; set; }
        = new List<ProjectMember>();
}