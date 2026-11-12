
namespace VRAssetManagement.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}
