
using VRAssetManagement.Application.Services;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Infrastructure.Services;

public class AuditLogService : IAuditLogService
{
    private readonly ApplicationDbContext _db;

    public AuditLogService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(
        Guid? userId,
        string action,
        string targetType,
        Guid targetId)
    {
        if (userId is null || userId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid UserId is required.",
                nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException(
                "Action is required.",
                nameof(action));
        }

        if (string.IsNullOrWhiteSpace(targetType))
        {
            throw new ArgumentException(
                "TargetType is required.",
                nameof(targetType));
        }

        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            At = DateTime.UtcNow
        };

        _db.AuditLogs.Add(log);

        await _db.SaveChangesAsync();
    }
}
