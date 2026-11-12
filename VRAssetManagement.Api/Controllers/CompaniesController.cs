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
[Route("api/v1/companies")]
[Authorize]
public sealed class CompaniesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly GoogleDriveStorageService _googleDrive;
    private readonly ILogger<CompaniesController> _logger;

    public CompaniesController(
        ApplicationDbContext db,
        GoogleDriveStorageService googleDrive,
        ILogger<CompaniesController> logger)
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

    [HttpGet]
    public async Task<IActionResult> GetCompanies(CancellationToken cancellationToken)
    {
        var role = await GetActiveRoleAsync(cancellationToken);
        if (role is not ("ADMIN" or "DEVELOPER" or "3DARTIST"))
            return Forbid();

        var companies = await _db.Companies.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Slug,
                c.DriveFolderId,
                c.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new { success = true, count = companies.Count, data = companies });
    }

    // Admin and 3DArtist can create Company records. Drive folder is automatically created/reused under VR root.
    [HttpPost]
    public async Task<IActionResult> CreateCompany(
        [FromBody] CreateCompanyRequest request,
        CancellationToken cancellationToken)
    {
        var role = await GetActiveRoleAsync(cancellationToken);
        if (role is not ("ADMIN" or "3DARTIST"))
            return Forbid();

        if (request == null ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Slug))
            return BadRequest(new { message = "Company name and slug are required." });

        var name = request.Name.Trim();
        var slug = request.Slug.Trim().ToLowerInvariant();

        var exists = await _db.Companies.AnyAsync(
            c => c.Slug == slug, cancellationToken);
        if (exists)
            return Conflict(new { message = "This company slug already exists." });

        try
        {
            var folderId = await _googleDrive.EnsureFolderAsync(
                name, _googleDrive.GetRootFolderId());

            var company = new Company
            {
                Name = name,
                Slug = slug,
                DriveFolderId = folderId
            };

            _db.Companies.Add(company);
            await _db.SaveChangesAsync(cancellationToken);

            return Created($"/api/v1/companies/{company.Id}", new
            {
                success = true,
                data = new
                {
                    company.Id,
                    company.Name,
                    company.Slug,
                    company.DriveFolderId,
                    company.CreatedAt
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Google Drive is unavailable or not configured.");
            return StatusCode(503, new { message = ex.Message });
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Company folder creation failed.");
            return StatusCode(502, new { message = "Google Drive company folder creation failed." });
        }
    }

    // Admin-only sync for an existing Company record. Finds/creates Company folder under VR root.
    [HttpPost("{companyId:guid}/sync-drive")]
    public async Task<IActionResult> SyncCompanyFolder(
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
            var folderId = await _googleDrive.EnsureFolderAsync(
                company.Name, _googleDrive.GetRootFolderId());

            company.DriveFolderId = folderId;
            await _db.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                message = "Company folder created/reused and linked successfully.",
                companyId = company.Id,
                companyName = company.Name,
                driveFolderId = company.DriveFolderId
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
            return StatusCode(502, new { message = "Google Drive company folder synchronization failed." });
        }
    }
}
