using System.Security.Claims;
using System.Text.RegularExpressions;
using Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Api.Services;
using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/companies/{companyId:guid}/projects")]
[Authorize]
public sealed class ProjectsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly GoogleDriveStorageService _googleDrive;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(
        ApplicationDbContext db,
        GoogleDriveStorageService googleDrive,
        ILogger<ProjectsController> logger)
    {
        _db = db;
        _googleDrive = googleDrive;
        _logger = logger;
    }

    private Guid? CurrentUserId
    {
        get
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? User.FindFirstValue("nameid")
                ?? User.FindFirstValue("userId")
                ?? User.FindFirstValue("uid")
                ?? User.FindFirstValue("id");
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    private static string NormalizeRole(string? role) =>
        Regex.Replace(role?.Trim() ?? string.Empty, "[^a-zA-Z0-9]", "")
            .ToUpperInvariant();

    private async Task<string?> GetActiveRoleAsync(CancellationToken ct)
    {
        if (CurrentUserId is not Guid userId)
            return null;

        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Active, u.GlobalRole })
            .FirstOrDefaultAsync(ct);

        return user == null || !user.Active ? null : NormalizeRole(user.GlobalRole);
    }

    private async Task<bool> CanCreateProjectAsync(CancellationToken ct)
    {
        var role = await GetActiveRoleAsync(ct);
        return role is "ADMIN" or "3DARTIST";
    }

    // GET: api/v1/companies/{companyId}/projects
    [HttpGet]
    public async Task<IActionResult> GetProjects(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var role = await GetActiveRoleAsync(cancellationToken);
        if (role is not ("ADMIN" or "DEVELOPER" or "3DARTIST"))
            return Forbid();

        var companyExists = await _db.Companies
            .AnyAsync(c => c.Id == companyId, cancellationToken);
        if (!companyExists)
            return NotFound(new { message = "Company not found." });

        var projects = await _db.Projects.AsNoTracking()
            .Where(p => p.CompanyId == companyId)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.CompanyId,
                p.Name,
                p.Slug,
                p.Description,
                p.Status,
                p.DriveFolderId
            })
            .ToListAsync(cancellationToken);

        return Ok(new { success = true, count = projects.Count, data = projects });
    }

    // POST: api/v1/companies/{companyId}/projects
    // Admin and 3D Artist can create projects.
    [HttpPost]
    public async Task<IActionResult> CreateProject(
        Guid companyId,
        [FromBody] CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanCreateProjectAsync(cancellationToken))
            return Forbid();

        if (request == null ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Slug))
        {
            return BadRequest(new { message = "Project name and slug are required." });
        }

        var company = await _db.Companies.FirstOrDefaultAsync(
            c => c.Id == companyId, cancellationToken);
        if (company == null)
            return NotFound(new { message = "Company not found." });

        var name = request.Name.Trim();
        var slug = request.Slug.Trim().ToLowerInvariant();

        var slugExists = await _db.Projects.AnyAsync(
            p => p.CompanyId == companyId && p.Slug == slug,
            cancellationToken);
        if (slugExists)
            return Conflict(new { message = "Project slug already exists in this company." });

        try
        {
            // Company folder is automatically found/created below the manually-created VR root.
            var companyFolderId = await _googleDrive.EnsureFolderAsync(
                company.Name, _googleDrive.GetRootFolderId());

            // Project folder is automatically found/created inside the selected Company folder.
            var projectFolderId = await _googleDrive.EnsureFolderAsync(
                name, companyFolderId);

            var project = new Project
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                Name = name,
                Slug = slug,
                Description = request.Description?.Trim(),
                Status = "Active",
                DriveFolderId = projectFolderId
            };

            company.DriveFolderId = companyFolderId;
            _db.Projects.Add(project);
            await _db.SaveChangesAsync(cancellationToken);

            return StatusCode(StatusCodes.Status201Created, new
            {
                success = true,
                message = "Company and Project folders created/linked successfully.",
                data = new
                {
                    companyId = company.Id,
                    companyName = company.Name,
                    companyDriveFolderId = company.DriveFolderId,
                    projectId = project.Id,
                    projectName = project.Name,
                    project.Slug,
                    project.Description,
                    project.Status,
                    projectDriveFolderId = project.DriveFolderId
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Google Drive is unavailable or not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = ex.Message });
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Google Drive Company/Project folder creation failed.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "Google Drive Company or Project folder creation failed." });
        }
    }

    // POST: api/v1/companies/{companyId}/projects/sync-drive
    // Admin-only repair for existing projects. Does not delete Drive folders.
    [HttpPost("sync-drive")]
    public async Task<IActionResult> SyncProjectFolders(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var role = await GetActiveRoleAsync(cancellationToken);
        if (role != "ADMIN")
            return Forbid();

        var company = await _db.Companies.FirstOrDefaultAsync(
            c => c.Id == companyId, cancellationToken);
        if (company == null)
            return NotFound(new { message = "Company not found." });

        try
        {
            var companyFolderId = await _googleDrive.EnsureFolderAsync(
                company.Name, _googleDrive.GetRootFolderId());
            company.DriveFolderId = companyFolderId;

            var projects = await _db.Projects
                .Where(p => p.CompanyId == companyId)
                .OrderBy(p => p.Name)
                .ToListAsync(cancellationToken);

            var results = new List<object>();
            foreach (var project in projects)
            {
                try
                {
                    var oldId = project.DriveFolderId;
                    var correctId = await _googleDrive.EnsureFolderAsync(
                        project.Name, companyFolderId);
                    project.DriveFolderId = correctId;
                    results.Add(new
                    {
                        projectId = project.Id,
                        projectName = project.Name,
                        oldFolderId = oldId,
                        newFolderId = correctId,
                        success = true
                    });
                }
                catch (Exception ex) when (ex is GoogleApiException or InvalidOperationException)
                {
                    _logger.LogError(ex, "Project folder sync failed for {ProjectId}", project.Id);
                    results.Add(new
                    {
                        projectId = project.Id,
                        projectName = project.Name,
                        success = false,
                        message = ex.Message
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            var failed = results.Count(x =>
                x.GetType().GetProperty("success")?.GetValue(x) is false);

            return Ok(new
            {
                success = failed == 0,
                companyFolderId,
                totalProjects = projects.Count,
                successful = projects.Count - failed,
                failed,
                data = results
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Company folder sync failed.");
            return StatusCode(503, new { message = ex.Message });
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Company folder sync failed.");
            return StatusCode(502, new { message = "Google Drive synchronization failed." });
        }
    }
}
