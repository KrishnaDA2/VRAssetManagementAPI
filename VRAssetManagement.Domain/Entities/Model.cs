namespace VRAssetManagement.Domain.Entities;

public class Model
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? DriveFolderId { get; set; }

    public string Category { get; set; } = "Prop";
    public string TagsJson { get; set; } = "[]";

    public Guid? LatestVersionId { get; set; }
    public Guid? LatestApprovedVersionId { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<ModelVersion> Versions { get; set; }
        = new List<ModelVersion>();
}
