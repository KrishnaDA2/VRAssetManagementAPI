
using System.Xml.Linq;

namespace VRAssetManagement.Domain.Entities;

public class ModelVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ModelId { get; set; }

    public int VersionNo { get; set; }
    public string? DriveFolderId { get; set; }
    public string? Notes { get; set; }

    public string Status { get; set; } = "Draft";
    public Guid UploadedBy { get; set; }

    public string StatsJson { get; set; } = "{}";
    public string ProcessingStatus { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Model Model { get; set; } = null!;

    public ICollection<AssetFile> Files { get; set; }
        = new List<AssetFile>();

    public ICollection<MaterialMapping> MaterialMappings { get; set; }
        = new List<MaterialMapping>();

    public ICollection<Comment> Comments { get; set; }
        = new List<Comment>();
}
