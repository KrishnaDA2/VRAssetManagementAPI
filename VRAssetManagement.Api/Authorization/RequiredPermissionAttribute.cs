
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : TypeFilterAttribute
{
    public RequirePermissionAttribute(string permission)
        : base(typeof(RequirePermissionFilter))
    {
        Arguments = new object[] { permission };
    }
}

public sealed class RequirePermissionFilter : IAsyncAuthorizationFilter
{
    private readonly ApplicationDbContext _db;
    private readonly string _permission;

    public RequirePermissionFilter(
        ApplicationDbContext db,
        string permission)
    {
        _db = db;
        _permission = permission;
    }

    public async Task OnAuthorizationAsync(
        AuthorizationFilterContext context)
    {
        var principal = context.HttpContext.User;

        if (principal.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var userIdValue = principal.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdValue, out var userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.Active)
            .Select(u => new { u.PermissionsJson })
            .FirstOrDefaultAsync(
                context.HttpContext.RequestAborted);

        if (user == null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        List<string> permissions;

        try
        {
            permissions = JsonSerializer.Deserialize<List<string>>(
                user.PermissionsJson ?? "[]") ?? new List<string>();
        }
        catch (JsonException)
        {
            context.Result = new ObjectResult(new
            {
                message = "User permission configuration is invalid."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        var allowed = permissions.Any(p =>
            string.Equals(
                p,
                _permission,
                StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            context.Result = new ObjectResult(new
            {
                message = "You do not have permission to perform this action.",
                requiredPermission = _permission
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}
