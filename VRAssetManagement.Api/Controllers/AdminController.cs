
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.API.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AdminController(ApplicationDbContext db)
    {
        _db = db;
    }

    // GET /api/v1/admin/storage
    [HttpGet("storage")]
    public async Task<IActionResult> GetStorage(
        CancellationToken cancellationToken)
    {
        var files = await _db.Files
            .AsNoTracking()
            .Select(f => new
            {
                f.SizeBytes,
                ProjectId = f.Version.Model.ProjectId,
                ProjectName = f.Version.Model.Project.Name,
                CompanyId = f.Version.Model.Project.CompanyId,
                CompanyName = f.Version.Model.Project.Company.Name
            })
            .ToListAsync(cancellationToken);

        var projectUsage = files
            .GroupBy(f => new
            {
                f.CompanyId,
                f.CompanyName,
                f.ProjectId,
                f.ProjectName
            })
            .Select(g => new
            {
                companyId = g.Key.CompanyId,
                companyName = g.Key.CompanyName,
                projectId = g.Key.ProjectId,
                projectName = g.Key.ProjectName,
                usedBytes = g.Sum(x => (long)x.SizeBytes),
                fileCount = g.Count()
            })
            .OrderBy(x => x.companyName)
            .ThenBy(x => x.projectName)
            .ToList();

        var companyUsage = projectUsage
            .GroupBy(x => new
            {
                x.companyId,
                x.companyName
            })
            .Select(g => new
            {
                companyId = g.Key.companyId,
                companyName = g.Key.companyName,
                usedBytes = g.Sum(x => x.usedBytes),
                fileCount = g.Sum(x => x.fileCount),
                projects = g.Select(x => new
                {
                    x.projectId,
                    x.projectName,
                    x.usedBytes,
                    x.fileCount
                })
            })
            .ToList();

        return Ok(new
        {
            totalUsedBytes = files.Sum(x => (long)x.SizeBytes),
            totalFiles = files.Count,
            companies = companyUsage,
            projects = projectUsage
        });
    }

    // GET /api/v1/admin/audit
    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditLogs(
        CancellationToken cancellationToken)
    {
        var logs = await (
            from a in _db.AuditLogs.AsNoTracking()
            join u in _db.Users.AsNoTracking()
                on a.UserId equals u.Id into userGroup
            from u in userGroup.DefaultIfEmpty()
            orderby a.At descending
            select new
            {
                id = a.Id,
                userId = a.UserId,
                userName = u == null ? "Unknown user" : u.Name,
                action = a.Action,
                targetType = a.TargetType,
                targetId = a.TargetId,
                timestamp = a.At
            })
            .Take(500)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            items = logs,
            count = logs.Count
        });
    }

    // GET /api/v1/admin/downloads
    [HttpGet("downloads")]
    public async Task<IActionResult> GetDownloadLogs(
        CancellationToken cancellationToken)
    {
        var logs = await (
            from d in _db.DownloadLogs.AsNoTracking()
            join u in _db.Users.AsNoTracking()
                on d.UserId equals u.Id into userGroup
            from u in userGroup.DefaultIfEmpty()
            join f in _db.Files.AsNoTracking()
                on d.FileId equals f.Id into fileGroup
            from f in fileGroup.DefaultIfEmpty()
            orderby d.At descending
            select new
            {
                id = d.Id,
                userId = d.UserId,
                userName = u == null ? "Unknown user" : u.Name,
                fileId = d.FileId,
                filename = f == null ? "File unavailable" : f.Filename,
                sizeBytes = f == null ? 0L : (long)f.SizeBytes,
                timestamp = d.At
            })
            .Take(500)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            items = logs,
            count = logs.Count
        });
    }
}
