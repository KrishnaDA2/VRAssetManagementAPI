
namespace VRAssetManagement.Application.Services;

public interface IAuditLogService
{
    Task LogAsync(
        Guid? userId,
        string action,
        string targetType,
        Guid targetId);
}
