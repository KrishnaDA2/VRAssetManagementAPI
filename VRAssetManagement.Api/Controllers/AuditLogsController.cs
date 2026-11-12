using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/audit-logs")]
[Authorize]
public class AuditLogsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AuditLogsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new
            {
                message = "Page must be at least 1 and pageSize must be between 1 and 100."
            });
        }

        var totalCount = await _db.AuditLogs.CountAsync();

        var logs = await _db.AuditLogs
            .OrderByDescending(log => log.At)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(log => new
            {
                log.Id,
                log.UserId,
                log.Action,
                log.TargetType,
                log.TargetId,
                log.At
            })
            .ToListAsync();

        return Ok(new
        {
            page,
            pageSize,
            totalCount,
            data = logs
        });
    }
}