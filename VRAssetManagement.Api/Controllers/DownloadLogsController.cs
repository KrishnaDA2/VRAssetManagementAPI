
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/download-logs")]
[Authorize]
public class DownloadLogsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public DownloadLogsController(ApplicationDbContext db)
    {
        _db = db;
    }

    // Current authenticated user ID from JWT.
    private Guid? CurrentUserId
    {
        get
        {
            var userId = User.FindFirstValue(
                ClaimTypes.NameIdentifier);

            return Guid.TryParse(userId, out var id)
                ? id
                : null;
        }
    }

    private bool IsAdmin =>
        User.IsInRole("Admin");

    private bool IsDeveloper =>
        User.IsInRole("Developer");

    private bool IsArtist =>
        User.IsInRole("3DArtist") ||
        User.IsInRole("3D Artist");

    private bool HasValidRole =>
        IsAdmin || IsDeveloper || IsArtist;

    // GET:
    // api/v1/download-logs?page=1&pageSize=20
    // Returns only the logged-in user's download history.
    [HttpGet]
    public Task<IActionResult> GetDownloadLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        return ReadDownloadLogs(
            page,
            pageSize,
            allUsers: false);
    }

    // GET:
    // api/v1/admin/downloads?page=1&pageSize=20
    // Admin can see all users' download history.
    [HttpGet("/api/v1/admin/downloads")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> GetAdminDownloadLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        return ReadDownloadLogs(
            page,
            pageSize,
            allUsers: true);
    }

    private async Task<IActionResult> ReadDownloadLogs(
        int page,
        int pageSize,
        bool allUsers)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new
            {
                message =
                    "Page must be at least 1 and pageSize must be between 1 and 100."
            });
        }

        if ((long)(page - 1) * pageSize > int.MaxValue)
        {
            return BadRequest(new
            {
                message = "Requested page number is too large."
            });
        }

        if (CurrentUserId is null || !HasValidRole)
            return Forbid();

        var userId = CurrentUserId.Value;

        var activeUser = await _db.Users
            .AnyAsync(u =>
                u.Id == userId &&
                u.Active);

        if (!activeUser)
            return Forbid();

        // Join download logs with files, users,
        // versions and models.
        var query =
            from log in _db.DownloadLogs

            join file in _db.Files
                on log.FileId equals file.Id

            join user in _db.Users
                on log.UserId equals user.Id

            join version in _db.ModelVersions
                on file.VersionId equals version.Id

            join model in _db.Models
                on version.ModelId equals model.Id

            select new
            {
                id = log.Id,
                userId = log.UserId,
                userName = user.Name,
                fileId = log.FileId,
                filename = file.Filename,
                kind = file.Kind,
                sizeBytes = file.SizeBytes,
                modelName = model.Name,
                versionNo = version.VersionNo,
                timestamp = log.At
            };

        // Non-admin users can only see their own logs.
        if (!allUsers)
        {
            query = query.Where(x =>
                x.userId == userId);
        }

        var totalCount = await query.CountAsync();

        var logs = await query
            .OrderByDescending(x => x.timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new
        {
            page,
            pageSize,
            totalCount,
            totalPages = (int)Math.Ceiling(
                totalCount / (double)pageSize),
            data = logs
        });
    }

    // POST:
    // api/v1/download-logs
    // Admin, Developer and 3D Artist.
    // UserId is always taken from JWT.
    [HttpPost]
    [Authorize(Roles = "Admin,Developer,3DArtist")]
    public async Task<IActionResult> CreateDownloadLog(
        [FromBody] CreateDownloadLogRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (CurrentUserId is null || !HasValidRole)
            return Forbid();

        var userId = CurrentUserId.Value;

        var activeUser = await _db.Users
            .AnyAsync(u =>
                u.Id == userId &&
                u.Active);

        if (!activeUser)
            return Forbid();

        if (request.FileId == Guid.Empty)
        {
            return BadRequest(new
            {
                message = "Valid FileId is required."
            });
        }

        // Find file and its project.
        var fileInfo = await _db.Files
            .Where(f => f.Id == request.FileId)
            .Select(f => new
            {
                f.Id,
                ProjectId = f.Version.Model.ProjectId
            })
            .FirstOrDefaultAsync();

        if (fileInfo == null)
        {
            return NotFound(new
            {
                message = "Asset file not found."
            });
        }

        // Admin can access all projects.
        if (!IsAdmin)
        {
            var isProjectMember = await _db.ProjectMembers
                .AnyAsync(pm =>
                    pm.ProjectId == fileInfo.ProjectId &&
                    pm.UserId == userId);

            if (!isProjectMember)
                return Forbid();
        }

        // Never trust UserId sent by frontend.
        var log = new DownloadLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FileId = request.FileId,
            At = DateTime.UtcNow
        };

        _db.DownloadLogs.Add(log);

        await _db.SaveChangesAsync();

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                message = "Download event recorded successfully.",
                id = log.Id,
                userId = log.UserId,
                fileId = log.FileId,
                timestamp = log.At
            });
    }
}

// Request DTO.
public class CreateDownloadLogRequest
{
    // Retained for compatibility with older frontend requests.
    // The API deliberately ignores this property.
    public Guid UserId { get; set; }

    public Guid FileId { get; set; }
}
