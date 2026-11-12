
namespace VRAssetManagement.Domain.Entities;

public class DownloadLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid FileId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}
