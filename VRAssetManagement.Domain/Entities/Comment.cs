
namespace VRAssetManagement.Domain.Entities;

public class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VersionId { get; set; }
    public Guid UserId { get; set; }

    public required string Body { get; set; }

    public float? PinX { get; set; }
    public float? PinY { get; set; }
    public float? PinZ { get; set; }

    public bool Resolved { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ModelVersion Version { get; set; } = null!;
}
