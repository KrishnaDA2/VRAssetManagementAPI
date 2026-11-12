
using System.Security.Claims;
using System.Text.Json;
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
[Route("api/v1/projects/{projectId:guid}/models")]
[Authorize]
public class ModelsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly GoogleDriveStorageService _googleDrive;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(
        ApplicationDbContext db,
        GoogleDriveStorageService googleDrive,
        ILogger<ModelsController> logger)
    {
        _db = db;
        _googleDrive = googleDrive;
        _logger = logger;
    }

    // =====================================================
    // CURRENT USER
    // =====================================================

    private Guid? CurrentUserId
    {
        get
        {
            string? userId =
                User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? User.FindFirstValue("nameid")
                ?? User.FindFirstValue("userId")
                ?? User.FindFirstValue("uid")
                ?? User.FindFirstValue("id");

            return Guid.TryParse(userId, out var id)
                ? id
                : null;
        }
    }

    // =====================================================
    // ROLE MANAGEMENT
    // =====================================================

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return string.Empty;

        return Regex.Replace(
            role.Trim(),
            @"[^a-zA-Z0-9]",
            ""
        ).ToUpperInvariant();
    }

    private async Task<(Guid UserId, string Role)?>
        GetActiveUserRoleAsync(
            CancellationToken cancellationToken = default)
    {
        var userId = CurrentUserId;

        if (userId == null)
            return null;

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new
            {
                u.Id,
                u.GlobalRole,
                u.Active
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null || !user.Active)
            return null;

        return (
            user.Id,
            NormalizeRole(user.GlobalRole)
        );
    }

    private static bool IsSupportedRole(string role)
    {
        return role is "ADMIN" or "DEVELOPER" or "3DARTIST";
    }

    private static bool CanManageModels(string role)
    {
        return role is "ADMIN" or "3DARTIST";
    }

    // =====================================================
    // MODEL VALIDATION
    // =====================================================

    private IActionResult? ValidateModelData(
        string? name,
        string? slug,
        string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new
            {
                success = false,
                message = "Model name is required."
            });
        }

        if (name.Trim().Length < 2 ||
            name.Trim().Length > 200)
        {
            return BadRequest(new
            {
                success = false,
                message = "Model name must contain 2 to 200 characters."
            });
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            return BadRequest(new
            {
                success = false,
                message = "Model slug is required."
            });
        }

        slug = slug.Trim().ToLowerInvariant();

        if (slug.Length > 200 ||
            !Regex.IsMatch(
                slug,
                @"^[a-z0-9]+(?:-[a-z0-9]+)*$"))
        {
            return BadRequest(new
            {
                success = false,
                message = "Invalid model slug."
            });
        }

        if (!string.IsNullOrWhiteSpace(tagsJson))
        {
            try
            {
                using var tags = JsonDocument.Parse(tagsJson);

                if (tags.RootElement.ValueKind !=
                    JsonValueKind.Array)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "TagsJson must contain a JSON array."
                    });
                }
            }
            catch (JsonException)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "TagsJson must be valid JSON."
                });
            }
        }

        return null;
    }

    // =====================================================
    // GET ALL MODELS
    // GET: api/v1/projects/{projectId}/models
    // =====================================================

    [HttpGet]
    public async Task<IActionResult> GetModels(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var access = await GetActiveUserRoleAsync(cancellationToken);

        if (access == null ||
            !IsSupportedRole(access.Value.Role))
        {
            return Forbid();
        }

        var projectExists = await _db.Projects
            .AsNoTracking()
            .AnyAsync(
                p => p.Id == projectId,
                cancellationToken);

        if (!projectExists)
        {
            return NotFound(new
            {
                success = false,
                message = "Project not found."
            });
        }

        var models = await _db.Models
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.Name)
            .Select(m => new
            {
                m.Id,
                m.ProjectId,
                m.Name,
                m.Slug,
                m.DriveFolderId,
                m.Category,
                m.TagsJson,
                m.LatestVersionId,
                m.LatestApprovedVersionId
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            count = models.Count,
            data = models
        });
    }

    // =====================================================
    // GET SINGLE MODEL
    // GET: api/v1/projects/{projectId}/models/{modelId}
    // =====================================================

    [HttpGet("{modelId:guid}")]
    public async Task<IActionResult> GetModel(
        Guid projectId,
        Guid modelId,
        CancellationToken cancellationToken)
    {
        var access = await GetActiveUserRoleAsync(cancellationToken);

        if (access == null ||
            !IsSupportedRole(access.Value.Role))
        {
            return Forbid();
        }

        var model = await _db.Models
            .AsNoTracking()
            .Where(m =>
                m.Id == modelId &&
                m.ProjectId == projectId)
            .Select(m => new
            {
                m.Id,
                m.ProjectId,
                m.Name,
                m.Slug,
                m.DriveFolderId,
                m.Category,
                m.TagsJson,
                m.LatestVersionId,
                m.LatestApprovedVersionId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (model == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Model not found."
            });
        }

        return Ok(new
        {
            success = true,
            data = model
        });
    }

    // =====================================================
    // CREATE MODEL
    // POST: api/v1/projects/{projectId}/models
    // Admin and 3D Artist
    // =====================================================

    [HttpPost]
    public async Task<IActionResult> CreateModel(
        Guid projectId,
        [FromBody] CreateModelRequest request,
        CancellationToken cancellationToken)
    {
        var access = await GetActiveUserRoleAsync(cancellationToken);

        if (access == null ||
            !CanManageModels(access.Value.Role))
        {
            return Forbid();
        }

        if (request == null)
        {
            return BadRequest(new
            {
                success = false,
                message = "Request body is required."
            });
        }

        var validation = ValidateModelData(
            request.Name,
            request.Slug,
            request.TagsJson);

        if (validation != null)
            return validation;

        var project = await _db.Projects
            .FirstOrDefaultAsync(
                p => p.Id == projectId,
                cancellationToken);

        if (project == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Project not found."
            });
        }

        var company = await _db.Companies
            .FirstOrDefaultAsync(
                c => c.Id == project.CompanyId,
                cancellationToken);

        if (company == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Company linked to this Project was not found."
            });
        }

        // Ensure the selected Company folder exists under the configured VR root.
        // Then ensure the selected Project folder exists inside that Company folder.
        try
        {
            var companyFolderId = await _googleDrive.EnsureFolderAsync(
                company.Name,
                _googleDrive.GetRootFolderId());

            company.DriveFolderId = companyFolderId;

            var projectFolderId = await _googleDrive.EnsureFolderAsync(
                project.Name,
                companyFolderId);

            project.DriveFolderId = projectFolderId;

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Google Drive unavailable while ensuring Company/Project folders.");
            return StatusCode(503, new { success = false, message = ex.Message });
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Google Drive Company/Project folder creation failed.");
            return StatusCode(502, new
            {
                success = false,
                message = "Google Drive Company or Project folder creation failed."
            });
        }

        var name = request.Name.Trim();
        var slug = request.Slug.Trim().ToLowerInvariant();

        var slugExists = await _db.Models.AnyAsync(m =>
            m.ProjectId == projectId &&
            m.Slug == slug,
            cancellationToken);

        if (slugExists)
        {
            return Conflict(new
            {
                success = false,
                message = "Model slug already exists in this project."
            });
        }

        var tagsJson = string.IsNullOrWhiteSpace(request.TagsJson)
            ? "[]"
            : request.TagsJson;

        string modelFolderId;

        try
        {
            // Create/reuse Model folder INSIDE Project folder.
            modelFolderId = await _googleDrive.EnsureFolderAsync(
                name,
                project.DriveFolderId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                ex,
                "Google Drive unavailable while creating Model folder.");

            return StatusCode(503, new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(
                ex,
                "Google Drive Model folder creation failed.");

            return StatusCode(502, new
            {
                success = false,
                message = "Google Drive model folder creation failed."
            });
        }

        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            Slug = slug,
            DriveFolderId = modelFolderId,
            Category = string.IsNullOrWhiteSpace(request.Category)
                ? "Prop"
                : request.Category.Trim(),
            TagsJson = tagsJson
        };

        _db.Models.Add(model);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Model database save failed.");

            return Conflict(new
            {
                success = false,
                message = "Model could not be saved. Check duplicate slug or database constraints."
            });
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                success = true,
                message = "Model created successfully inside Project folder.",
                data = new
                {
                    model.Id,
                    model.ProjectId,
                    model.Name,
                    model.Slug,
                    model.DriveFolderId,
                    model.Category,
                    model.TagsJson,
                    model.LatestVersionId,
                    model.LatestApprovedVersionId
                }
            });
    }

    // =====================================================
    // SYNC EXISTING MODEL FOLDERS
    // POST: api/v1/projects/{projectId}/models/sync-drive
    // Admin only
    // =====================================================

    [HttpPost("sync-drive")]
    public async Task<IActionResult> SyncModelFolders(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var access = await GetActiveUserRoleAsync(cancellationToken);

        if (access == null ||
            access.Value.Role != "ADMIN")
        {
            return Forbid();
        }

        var project = await _db.Projects
            .FirstOrDefaultAsync(
                p => p.Id == projectId,
                cancellationToken);

        if (project == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Project not found."
            });
        }

        if (string.IsNullOrWhiteSpace(project.DriveFolderId))
        {
            return Conflict(new
            {
                success = false,
                message = "Project Drive folder is not linked. Synchronize the Company and Project folders first."
            });
        }

        var models = await _db.Models
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken);

        var results = new List<object>();

        foreach (var model in models)
        {
            try
            {
                // IMPORTANT:
                // Parent is this Model's Project folder.
                var correctFolderId =
                    await _googleDrive.EnsureFolderAsync(
                        model.Name,
                        project.DriveFolderId);

                var oldFolderId = model.DriveFolderId;

                model.DriveFolderId = correctFolderId;

                results.Add(new
                {
                    modelId = model.Id,
                    modelName = model.Name,
                    oldFolderId,
                    newFolderId = correctFolderId,
                    success = true
                });
            }
            catch (GoogleApiException ex)
            {
                _logger.LogError(
                    ex,
                    "Google Drive sync failed for Model {ModelId}.",
                    model.Id);

                results.Add(new
                {
                    modelId = model.Id,
                    modelName = model.Name,
                    success = false,
                    message = "Google Drive folder synchronization failed."
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "Google Drive service unavailable for Model {ModelId}.",
                    model.Id);

                results.Add(new
                {
                    modelId = model.Id,
                    modelName = model.Name,
                    success = false,
                    message = ex.Message
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        var failedCount = results.Count(r =>
            r.GetType().GetProperty("success")?
                .GetValue(r) is false);

        return Ok(new
        {
            success = failedCount == 0,
            message = "Model folder synchronization completed.",
            totalModels = models.Count,
            successful = models.Count - failedCount,
            failed = failedCount,
            data = results
        });
    }

    // =====================================================
    // UPDATE MODEL
    // PUT: api/v1/projects/{projectId}/models/{modelId}
    // Admin and 3D Artist
    // =====================================================

    [HttpPut("{modelId:guid}")]
    public async Task<IActionResult> UpdateModel(
        Guid projectId,
        Guid modelId,
        [FromBody] UpdateModelRequest request,
        CancellationToken cancellationToken)
    {
        var access = await GetActiveUserRoleAsync(cancellationToken);

        if (access == null ||
            !CanManageModels(access.Value.Role))
        {
            return Forbid();
        }

        if (request == null)
        {
            return BadRequest(new
            {
                success = false,
                message = "Request body is required."
            });
        }

        var model = await _db.Models
            .FirstOrDefaultAsync(m =>
                m.Id == modelId &&
                m.ProjectId == projectId,
                cancellationToken);

        if (model == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Model not found."
            });
        }

        var validation = ValidateModelData(
            request.Name,
            request.Slug,
            request.TagsJson);

        if (validation != null)
            return validation;

        var name = request.Name.Trim();
        var slug = request.Slug.Trim().ToLowerInvariant();

        var duplicateSlug = await _db.Models.AnyAsync(m =>
            m.ProjectId == projectId &&
            m.Id != modelId &&
            m.Slug == slug,
            cancellationToken);

        if (duplicateSlug)
        {
            return Conflict(new
            {
                success = false,
                message = "Another model already uses this slug."
            });
        }

        model.Name = name;
        model.Slug = slug;

        model.Category = string.IsNullOrWhiteSpace(request.Category)
            ? "Prop"
            : request.Category.Trim();

        model.TagsJson = string.IsNullOrWhiteSpace(request.TagsJson)
            ? "[]"
            : request.TagsJson;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            message = "Model updated successfully.",
            data = new
            {
                model.Id,
                model.ProjectId,
                model.Name,
                model.Slug,
                model.DriveFolderId,
                model.Category,
                model.TagsJson,
                model.LatestVersionId,
                model.LatestApprovedVersionId
            }
        });
    }
}

// =====================================================
// UPDATE MODEL REQUEST DTO
// =====================================================

public sealed class UpdateModelRequest
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Category { get; set; } = "Prop";

    public string TagsJson { get; set; } = "[]";
}
