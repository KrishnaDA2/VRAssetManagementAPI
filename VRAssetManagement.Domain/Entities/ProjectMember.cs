
namespace VRAssetManagement.Domain.Entities;

public class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }

    public string Role { get; set; } = "Developer";

    public Project Project { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}
