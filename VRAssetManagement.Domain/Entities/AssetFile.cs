
namespace VRAssetManagement.Domain.Entities;

public class AssetFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VersionId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? DriveFileId { get; set; }

    public required string Filename { get; set; }

    public long SizeBytes { get; set; }

    public string? Checksum { get; set; }

    public ModelVersion Version { get; set; } = null!;
}
