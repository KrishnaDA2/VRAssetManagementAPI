
namespace VRAssetManagement.Domain.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }

    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? DriveFolderId { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "Active";

    public Company Company { get; set; } = null!;

    public ICollection<ProjectMember> Members { get; set; }
        = new List<ProjectMember>();

    public ICollection<Model> Models { get; set; }
        = new List<Model>();
}
