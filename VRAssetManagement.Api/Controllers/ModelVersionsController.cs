
using System.Security.Claims;
using System.Security.Cryptography;

using Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using VRAssetManagement.Api.Services;
using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/models/{modelId:guid}/versions")]
[Authorize]
public class ModelVersionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly GoogleDriveStorageService _googleDrive;

    private const long MaxUploadBytes = 500L * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx",
            ".png",
            ".jpg",
            ".jpeg",
            ".tga",
            ".tif",
            ".tiff",
            ".bmp",
            ".webp",
            ".zip",
            ".glb",
            ".gltf",
            ".obj",
            ".mtl"
        };

    public ModelVersionsController(
        ApplicationDbContext db,
        GoogleDriveStorageService googleDrive)
    {
        _db = db;
        _googleDrive = googleDrive;
    }

    // =====================================================
    // CURRENT USER
    // =====================================================

    private Guid? CurrentUserId
    {
        get
        {
            var value =
                User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                User.FindFirstValue("sub") ??
                User.FindFirstValue("nameid") ??
                User.FindFirstValue("userId") ??
                User.FindFirstValue("uid") ??
                User.FindFirstValue("id");

            return Guid.TryParse(value, out var id)
                ? id
                : null;
        }
    }

    // =====================================================
    // ROLE MANAGEMENT
    // =====================================================

    private static string NormalizeRole(string? role)
    {
        return new string((role ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();
    }

    private async Task<(bool Active, string Role)?>
        GetCurrentUserAccessAsync()
    {
        if (CurrentUserId is not Guid userId)
            return null;

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Active,
                u.GlobalRole
            })
            .FirstOrDefaultAsync();

        return user == null
            ? null
            : (user.Active, NormalizeRole(user.GlobalRole));
    }

    private async Task<bool> CanManageVersionsAsync()
    {
        var access = await GetCurrentUserAccessAsync();

        if (!access.HasValue || !access.Value.Active)
            return false;

        return access.Value.Role is "ADMIN" or "3DARTIST";
    }

    private async Task<bool> CanAccessModelAsync(Guid modelId)
    {
        var access = await GetCurrentUserAccessAsync();

        if (!access.HasValue || !access.Value.Active)
            return false;

        if (access.Value.Role is not
            ("ADMIN" or "DEVELOPER" or "3DARTIST"))
        {
            return false;
        }

        // Authorization and resource existence are checked separately.
        // This allows the action to return 404 for a missing model instead of
        // incorrectly returning 403.
        return true;
    }

    // =====================================================
    // FILE VALIDATION
    // =====================================================

    private static bool IsAllowedFile(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName);

        return AllowedExtensions.Contains(extension);
    }

    private static string GetFileKind(string filename)
    {
        var extension = Path.GetExtension(filename);

        if (extension.Equals(
            ".fbx",
            StringComparison.OrdinalIgnoreCase))
        {
            return "Model";
        }

        if (extension.Equals(
            ".zip",
            StringComparison.OrdinalIgnoreCase))
        {
            return "Archive";
        }

        return "Texture";
    }

    private static string GetContentType(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName);

        return extension.ToLowerInvariant() switch
        {
            ".fbx" => "application/octet-stream",
            ".glb" => "model/gltf-binary",
            ".gltf" => "model/gltf+json",
            ".obj" => "text/plain",
            ".mtl" => "text/plain",
            ".zip" => "application/zip",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            ".tga" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }

    // =====================================================
    // GET VERSIONS
    // GET: api/v1/models/{modelId}/versions
    // =====================================================

    [HttpGet]
    public async Task<IActionResult> GetVersions(Guid modelId)
    {
        if (!await CanAccessModelAsync(modelId))
            return Forbid();

        var modelExists = await _db.Models
            .AsNoTracking()
            .AnyAsync(m => m.Id == modelId);

        if (!modelExists)
        {
            return NotFound(new
            {
                success = false,
                message = "Model not found."
            });
        }

        var versions = await _db.ModelVersions
            .AsNoTracking()
            .Where(v => v.ModelId == modelId)
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new
            {
                v.Id,
                v.ModelId,
                v.VersionNo,
                v.DriveFolderId,
                v.Notes,
                v.Status,
                v.UploadedBy,
                v.StatsJson,
                v.ProcessingStatus,
                v.ErrorMessage,
                v.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            count = versions.Count,
            data = versions
        });
    }

    // =====================================================
    // CREATE EMPTY VERSION
    // POST: api/v1/models/{modelId}/versions
    // Admin and 3D Artist
    // =====================================================

    [HttpPost]
    public async Task<IActionResult> CreateVersion(
        Guid modelId,
        [FromBody] CreateModelVersionRequest request)
    {
        if (request == null)
        {
            return BadRequest(new
            {
                success = false,
                message = "Request body is required."
            });
        }

        if (!await CanAccessModelAsync(modelId))
            return Forbid();

        if (!await CanManageVersionsAsync())
            return Forbid();

        if (CurrentUserId is not Guid uploaderId)
            return Unauthorized();

        var model = await _db.Models
            .FirstOrDefaultAsync(m => m.Id == modelId);

        if (model == null)
        {
            return NotFound(new
            {
                message = "Model not found."
            });
        }

        if (string.IsNullOrWhiteSpace(model.DriveFolderId))
        {
            return Conflict(new
            {
                message = "Model Google Drive folder is not linked."
            });
        }

        var lastVersionNo = await _db.ModelVersions
            .Where(v => v.ModelId == modelId)
            .Select(v => (int?)v.VersionNo)
            .MaxAsync() ?? 0;

        var nextVersionNo = lastVersionNo + 1;

        string versionFolderId;

        try
        {
            versionFolderId =
                await _googleDrive.CreateVersionFolderAsync(
                    model.DriveFolderId,
                    nextVersionNo);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new
            {
                message = ex.Message
            });
        }
        catch (GoogleApiException)
        {
            return StatusCode(502, new
            {
                message = "Google Drive version folder creation failed."
            });
        }

        var version = new ModelVersion
        {
            Id = Guid.NewGuid(),
            ModelId = modelId,
            VersionNo = nextVersionNo,
            DriveFolderId = versionFolderId,
            Notes = request?.Notes?.Trim(),
            Status = "Draft",
            UploadedBy = uploaderId,
            ProcessingStatus = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.ModelVersions.Add(version);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new
            {
                message = "Version could not be saved. Please retry."
            });
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                success = true,
                message = "Version created successfully.",
                data = new
                {
                    version.Id,
                    version.ModelId,
                    version.VersionNo,
                    version.DriveFolderId,
                    version.Notes,
                    version.Status,
                    version.UploadedBy,
                    version.ProcessingStatus,
                    version.CreatedAt
                }
            });
    }

    // =====================================================
    // UPLOAD VERSION WITH FILES
    // POST: api/v1/models/{modelId}/versions/upload
    // Multipart: notes, uploader_id, files
    // Admin and 3D Artist
    // =====================================================

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> UploadVersion(
        Guid modelId,
        [FromForm] UploadVersionForm request)
    {
        if (request == null)
        {
            return BadRequest(new
            {
                success = false,
                message = "Multipart form data is required."
            });
        }

        if (!await CanAccessModelAsync(modelId))
            return Forbid();

        if (!await CanManageVersionsAsync())
            return Forbid();

        if (CurrentUserId is not Guid uploaderId)
            return Unauthorized();

        var model = await _db.Models
            .FirstOrDefaultAsync(m => m.Id == modelId);

        if (model == null)
        {
            return NotFound(new
            {
                message = "Model not found."
            });
        }

        if (string.IsNullOrWhiteSpace(model.DriveFolderId))
        {
            return Conflict(new
            {
                message = "Model Google Drive folder is not linked."
            });
        }

        if (request?.Files == null || request.Files.Count == 0)
        {
            return BadRequest(new
            {
                message = "Select at least one file to upload."
            });
        }

        long totalBytes = 0;
        var hasFbx = false;

        foreach (var file in request.Files)
        {
            if (file == null || file.Length <= 0)
            {
                return BadRequest(new
                {
                    message = "Empty files cannot be uploaded."
                });
            }

            totalBytes += file.Length;

            if (totalBytes > MaxUploadBytes)
            {
                return BadRequest(new
                {
                    message = "Total upload size cannot exceed 500 MB."
                });
            }

            var filename = Path.GetFileName(file.FileName);

            if (string.IsNullOrWhiteSpace(filename))
            {
                return BadRequest(new
                {
                    message = "Invalid filename."
                });
            }

            if (!IsAllowedFile(file))
            {
                return BadRequest(new
                {
                    message = $"File format is not allowed: {filename}"
                });
            }

            if (Path.GetExtension(filename).Equals(
                ".fbx",
                StringComparison.OrdinalIgnoreCase))
            {
                hasFbx = true;
            }
        }

        if (!hasFbx)
        {
            return BadRequest(new
            {
                message = "At least one FBX file is required."
            });
        }

        var lastVersionNo = await _db.ModelVersions
            .Where(v => v.ModelId == modelId)
            .Select(v => (int?)v.VersionNo)
            .MaxAsync() ?? 0;

        var nextVersionNo = lastVersionNo + 1;

        string versionFolderId;

        try
        {
            versionFolderId =
                await _googleDrive.CreateVersionFolderAsync(
                    model.DriveFolderId,
                    nextVersionNo);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new
            {
                message = ex.Message
            });
        }
        catch (GoogleApiException)
        {
            return StatusCode(502, new
            {
                message = "Google Drive version folder creation failed."
            });
        }

        var version = new ModelVersion
        {
            Id = Guid.NewGuid(),
            ModelId = modelId,
            VersionNo = nextVersionNo,
            DriveFolderId = versionFolderId,
            Notes = request.Notes?.Trim() ?? string.Empty,
            Status = "Draft",
            UploadedBy = uploaderId,
            ProcessingStatus = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        var uploadedDriveFileIds = new List<string>();
        var assetFiles = new List<AssetFile>();

        try
        {
            foreach (var formFile in request.Files)
            {
                var filename = Path.GetFileName(formFile.FileName);

                await using var stream = formFile.OpenReadStream();

                // Calculate SHA-256.
                var hash = await SHA256.HashDataAsync(
                    stream,
                    HttpContext.RequestAborted);

                var checksum = Convert.ToHexString(hash)
                    .ToLowerInvariant();

                stream.Position = 0;

                // Upload file directly into Version folder.
                var driveFileId = await _googleDrive.UploadAsync(
                    stream,
                    filename,
                    GetContentType(formFile),
                    versionFolderId);

                uploadedDriveFileIds.Add(driveFileId);

                var assetFile = new AssetFile
                {
                    Id = Guid.NewGuid(),
                    VersionId = version.Id,
                    Kind = GetFileKind(filename),
                    Filename = filename,
                    SizeBytes = formFile.Length,
                    Checksum = checksum,
                    DriveFileId = driveFileId
                };

                assetFiles.Add(assetFile);
            }

            _db.ModelVersions.Add(version);
            _db.Files.AddRange(assetFiles);

            await _db.SaveChangesAsync();
        }
        catch (GoogleApiException)
        {
            await CleanupUploadedFilesAsync(uploadedDriveFileIds);

            return StatusCode(502, new
            {
                message = "Google Drive upload failed. Uploaded temporary files were cleaned up where possible."
            });
        }
        catch (InvalidOperationException ex)
        {
            await CleanupUploadedFilesAsync(uploadedDriveFileIds);

            return StatusCode(503, new
            {
                message = ex.Message
            });
        }
        catch (DbUpdateException)
        {
            await CleanupUploadedFilesAsync(uploadedDriveFileIds);

            return Conflict(new
            {
                message = "Version or file metadata could not be saved. Uploaded temporary files were cleaned up where possible."
            });
        }
        catch (OperationCanceledException)
        {
            await CleanupUploadedFilesAsync(uploadedDriveFileIds);
            throw;
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                success = true,
                message = "Version and files uploaded successfully.",
                data = new
                {
                    version.Id,
                    version.ModelId,
                    version.VersionNo,
                    version.DriveFolderId,
                    version.Notes,
                    version.Status,
                    version.UploadedBy,
                    version.ProcessingStatus,
                    version.CreatedAt,

                    files = assetFiles.Select(f => new
                    {
                        f.Id,
                        f.VersionId,
                        f.Kind,
                        f.Filename,
                        f.SizeBytes,
                        f.Checksum,
                        f.DriveFileId,

                        downloadUrl =
                            $"/api/v1/versions/{version.Id}/files/{f.Id}/download"
                    })
                }
            });
    }

    // =====================================================
    // CLEANUP GOOGLE DRIVE FILES IF DB SAVE FAILS
    // =====================================================

    private async Task CleanupUploadedFilesAsync(
        IEnumerable<string> driveFileIds)
    {
        foreach (var fileId in driveFileIds)
        {
            try
            {
                await _googleDrive.DeleteAsync(fileId);
            }
            catch (Exception ex)
            {
                // Do not hide the original upload/database error.
                // Log cleanup failure for later reconciliation.
                HttpContext.RequestServices
                    .GetRequiredService<ILogger<ModelVersionsController>>()
                    .LogError(
                        ex,
                        "Could not clean up Google Drive file {FileId}",
                        fileId);
            }
        }
    }
}

// =====================================================
// MULTIPART UPLOAD FORM
// =====================================================

public class UploadVersionForm
{
    public string Notes { get; set; } = string.Empty;

    [FromForm(Name = "uploader_id")]
    public string UploaderId { get; set; } = string.Empty;

    public List<IFormFile> Files { get; set; } = new();
}
