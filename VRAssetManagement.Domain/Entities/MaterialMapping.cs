
namespace VRAssetManagement.Domain.Entities;

public class MaterialMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VersionId { get; set; }
    public Guid? FileId { get; set; }

    public required string MaterialName { get; set; }
    public required string Slot { get; set; }

    public string ColorSpace { get; set; } = "Linear";
    public bool FlipY { get; set; } = false;

    public float TilingX { get; set; } = 1f;
    public float TilingY { get; set; } = 1f;
    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = 0f;

    public ModelVersion Version { get; set; } = null!;
    public AssetFile? File { get; set; }
}
