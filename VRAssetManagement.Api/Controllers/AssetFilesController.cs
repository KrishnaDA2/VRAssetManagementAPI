
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Api.Services;
using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/versions/{versionId:guid}/files")]
[Authorize]
public class AssetFilesController : ControllerBase
{
    private const long MaxFileSize = 500L * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly GoogleDriveStorageService _googleDrive;

    public AssetFilesController(
        ApplicationDbContext db,
        IWebHostEnvironment environment,
        GoogleDriveStorageService googleDrive)
    {
        _db = db;
        _environment = environment;
        _googleDrive = googleDrive;
    }

    // =====================================================
    // ROLE MANAGEMENT
    // =====================================================

    private Guid? CurrentUserId
    {
        get
        {
            var userId =
                User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");

            return Guid.TryParse(userId, out var id)
                ? id
                : null;
        }
    }

    private bool IsAdmin => User.IsInRole("Admin");

    private bool IsDeveloper => User.IsInRole("Developer");

    private bool IsArtist =>
        User.IsInRole("3DArtist") ||
        User.IsInRole("3D Artist");

    private bool HasValidRole =>
        IsAdmin || IsDeveloper || IsArtist;

    private bool CanManageFiles =>
        IsAdmin || IsArtist;

    private async Task<bool> CanAccessVersion(Guid versionId)
    {
        if (!HasValidRole || CurrentUserId == null)
            return false;

        var userId = CurrentUserId.Value;

        var activeUser = await _db.Users
            .AsNoTracking()
            .AnyAsync(u =>
                u.Id == userId &&
                u.Active);

        if (!activeUser)
            return false;

        // All active Admin, Developer and 3D Artist users can access
        // every project/version. Project membership is not required.
        return await _db.ModelVersions
            .AsNoTracking()
            .AnyAsync(v => v.Id == versionId);
    }

    // =====================================================
    // FILE HELPERS
    // =====================================================

    private static bool IsValidFilename(string? filename)
    {
        return !string.IsNullOrWhiteSpace(filename)
            && filename.Length <= 255
            && filename == Path.GetFileName(filename)
            && !filename.Contains('/')
            && !filename.Contains('\\');
    }

    private static bool IsValidChecksum(string? checksum)
    {
        return string.IsNullOrWhiteSpace(checksum)
            || Regex.IsMatch(
                checksum,
                @"\A[0-9a-fA-F]{64}\z");
    }

    private static readonly Dictionary<string, string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".fbx"] = "application/octet-stream",
            [".glb"] = "model/gltf-binary",
            [".gltf"] = "model/gltf+json",
            [".obj"] = "text/plain",
            [".mtl"] = "text/plain",
            [".zip"] = "application/zip",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".tga"] = "application/octet-stream"
        };

    private string? GetLocalFilePath(
        ModelVersion version,
        string filename)
    {
        if (!IsValidFilename(filename))
            return null;

        var uploadRoot = Path.Combine(
            _environment.ContentRootPath,
            "App_Data",
            "uploads");

        var roots = new[]
        {
            Path.Combine(
                uploadRoot,
                version.ModelId.ToString(),
                $"v{version.VersionNo:D3}"),

            Path.Combine(
                uploadRoot,
                version.ModelId.ToString("N"),
                $"v{version.VersionNo:D3}")
        };

        foreach (var root in roots)
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            var candidate = Path.GetFullPath(
                Path.Combine(root, filename));

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!candidate.StartsWith(
                normalizedRoot,
                comparison))
            {
                continue;
            }

            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string GetContentType(string filename)
    {
        var extension = Path.GetExtension(filename);

        return AllowedExtensions.TryGetValue(
            extension,
            out var contentType)
                ? contentType
                : "application/octet-stream";
    }

    // =====================================================
    // GET FILES
    // Admin, Developer, 3D Artist
    // =====================================================

    [HttpGet]
    public async Task<IActionResult> GetFiles(Guid versionId)
    {
        if (!await CanAccessVersion(versionId))
            return Forbid();

        var files = await _db.Files
            .AsNoTracking()
            .Where(f => f.VersionId == versionId)
            .OrderBy(f => f.Filename)
            .Select(f => new
            {
                f.Id,
                f.VersionId,
                f.Kind,
                f.Filename,
                f.SizeBytes,
                f.Checksum,
                f.DriveFileId
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            count = files.Count,
            data = files.Select(f => new
            {
                f.Id,
                f.VersionId,
                f.Kind,
                f.Filename,
                f.SizeBytes,
                f.Checksum,
                f.DriveFileId,

                downloadUrl =
                    $"/api/v1/versions/{versionId}/files/{f.Id}/download"
            })
        });
    }

    // =====================================================
    // DOWNLOAD FILE
    // Admin, Developer, 3D Artist
    // =====================================================

    [HttpGet("{fileId:guid}/download")]
    public async Task<IActionResult> DownloadFile(
        Guid versionId,
        Guid fileId)
    {
        if (!await CanAccessVersion(versionId))
            return Forbid();

        var version = await _db.ModelVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version == null)
            return NotFound(new
            {
                message = "Model version not found."
            });

        var assetFile = await _db.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f =>
                f.Id == fileId &&
                f.VersionId == versionId);

        if (assetFile == null)
            return NotFound(new
            {
                message = "File record not found."
            });

        var filename = assetFile.Filename;

        if (!IsValidFilename(filename))
            return BadRequest(new
            {
                message = "Invalid stored filename."
            });

        // Google Drive download.
        if (!string.IsNullOrWhiteSpace(assetFile.DriveFileId))
        {
            try
            {
                var downloaded = await _googleDrive.DownloadAsync(
                    assetFile.DriveFileId);

                if (downloaded.Content == null ||
                    downloaded.Content.Length == 0)
                {
                    return NotFound(new
                    {
                        message = "Downloaded file is empty."
                    });
                }

                return File(
                    downloaded.Content,
                    string.IsNullOrWhiteSpace(downloaded.ContentType)
                        ? GetContentType(filename)
                        : downloaded.ContentType,
                    filename);
            }
            catch (GoogleApiException ex)
                when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                return NotFound(new
                {
                    message = "File not found in Google Drive."
                });
            }
            catch (GoogleApiException)
            {
                return StatusCode(502, new
                {
                    message = "Google Drive download failed."
                });
            }
            catch (InvalidOperationException)
            {
                return StatusCode(503, new
                {
                    message = "Google Drive service unavailable."
                });
            }
        }

        // Local storage fallback.
        var localPath = GetLocalFilePath(version, filename);

        if (localPath != null)
        {
            return PhysicalFile(
                localPath,
                GetContentType(filename),
                filename,
                enableRangeProcessing: true);
        }

        return NotFound(new
        {
            message = "File not found in Google Drive or local storage.",
            filename
        });
    }

    // =====================================================
    // UPLOAD FILE
    // Admin, 3D Artist
    // =====================================================


    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFileSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFileSize)]
    public async Task<IActionResult> UploadFile(
        Guid versionId,
        [FromForm] IFormFile file,
        [FromForm] string kind)
    {
        if (!await CanAccessVersion(versionId))
            return Forbid();

        if (!CanManageFiles)
            return Forbid();

        if (file == null || file.Length == 0)
            return BadRequest(new
            {
                message = "Please select a file."
            });

        if (file.Length > MaxFileSize)
            return BadRequest(new
            {
                message = "Maximum file size is 500 MB."
            });

        if (string.IsNullOrWhiteSpace(kind) ||
            kind.Trim().Length > 50)
        {
            return BadRequest(new
            {
                message = "Valid file kind is required."
            });
        }

        var filename = Path.GetFileName(file.FileName);

        if (!IsValidFilename(filename))
            return BadRequest(new
            {
                message = "Invalid filename."
            });

        var extension = Path.GetExtension(filename);

        if (!AllowedExtensions.TryGetValue(
            extension,
            out var contentType))
        {
            return BadRequest(new
            {
                message = "Unsupported file format."
            });
        }

        // Load the actual version and its Drive folder ID.
        var version = await _db.ModelVersions
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version == null)
            return NotFound(new
            {
                message = "Model version not found."
            });

        if (string.IsNullOrWhiteSpace(version.DriveFolderId))
            return Conflict(new
            {
                message = "Version Google Drive folder is not linked."
            });

        string? driveFileId = null;

        try
        {
            await using var stream = file.OpenReadStream();

            // Calculate SHA-256.
            var hash = await SHA256.HashDataAsync(
                stream,
                HttpContext.RequestAborted);

            var checksum = Convert.ToHexString(hash)
                .ToLowerInvariant();

            stream.Position = 0;

            // IMPORTANT: Upload inside the Version folder.
            driveFileId = await _googleDrive.UploadAsync(
                stream,
                filename,
                contentType,
                version.DriveFolderId);

            var assetFile = new AssetFile
            {
                Id = Guid.NewGuid(),
                VersionId = versionId,
                Kind = kind.Trim(),
                Filename = filename,
                SizeBytes = file.Length,
                Checksum = checksum,
                DriveFileId = driveFileId
            };

            _db.Files.Add(assetFile);

            await _db.SaveChangesAsync();

            return StatusCode(201, new
            {
                success = true,
                message = "File uploaded successfully inside Version folder.",
                data = new
                {
                    assetFile.Id,
                    assetFile.VersionId,
                    assetFile.Kind,
                    assetFile.Filename,
                    assetFile.SizeBytes,
                    assetFile.Checksum,
                    assetFile.DriveFileId,
                    versionFolderId = version.DriveFolderId,
                    downloadUrl =
                        $"/api/v1/versions/{versionId}/files/{assetFile.Id}/download"
                }
            });
        }
        catch (DbUpdateException)
        {
            if (!string.IsNullOrWhiteSpace(driveFileId))
            {
                try
                {
                    await _googleDrive.DeleteAsync(driveFileId);
                }
                catch
                {
                    // Log cleanup failure in production.
                }
            }

            return StatusCode(500, new
            {
                message = "File metadata could not be saved."
            });
        }
        catch (GoogleApiException)
        {
            return StatusCode(502, new
            {
                message = "Google Drive upload failed."
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new
            {
                message = ex.Message
            });
        }
    }

    // =====================================================
    // CREATE FILE METADATA
    // Admin, 3D Artist
    // =====================================================

    [HttpPost]
    public async Task<IActionResult> CreateFile(
        Guid versionId,
        [FromBody] CreateAssetFileRequest request)
    {
        if (!await CanAccessVersion(versionId))
            return Forbid();

        if (!CanManageFiles)
            return Forbid();

        if (request == null)
            return BadRequest(new
            {
                message = "Request body is required."
            });

        if (!await _db.ModelVersions.AnyAsync(
            v => v.Id == versionId))
        {
            return NotFound(new
            {
                message = "Model version not found."
            });
        }

        var filename = request.Filename?.Trim();
        var checksum = request.Checksum?.Trim();

        if (!IsValidFilename(filename))
            return BadRequest(new
            {
                message = "Invalid filename."
            });

        if (request.SizeBytes <= 0 ||
            request.SizeBytes > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "Invalid file size."
            });
        }

        if (string.IsNullOrWhiteSpace(request.Kind) ||
            request.Kind.Trim().Length > 50)
        {
            return BadRequest(new
            {
                message = "Valid Kind is required."
            });
        }

        if (!IsValidChecksum(checksum))
            return BadRequest(new
            {
                message = "Invalid SHA-256 checksum."
            });

        var assetFile = new AssetFile
        {
            Id = Guid.NewGuid(),
            VersionId = versionId,
            Kind = request.Kind.Trim(),
            Filename = filename!,
            SizeBytes = request.SizeBytes,
            Checksum = checksum,
            DriveFileId = string.IsNullOrWhiteSpace(request.DriveFileId)
                ? null
                : request.DriveFileId.Trim()
        };

        _db.Files.Add(assetFile);
        await _db.SaveChangesAsync();

        return StatusCode(201, new
        {
            success = true,
            message = "File metadata created successfully.",
            data = assetFile
        });
    }

    // =====================================================
    // UPDATE FILE METADATA
    // Admin, 3D Artist
    // =====================================================

    [HttpPut("{fileId:guid}")]
    public async Task<IActionResult> UpdateFile(
        Guid versionId,
        Guid fileId,
        [FromBody] UpdateAssetFileRequest request)
    {
        if (!await CanAccessVersion(versionId))
            return Forbid();

        if (!CanManageFiles)
            return Forbid();

        if (request == null)
            return BadRequest();

        var assetFile = await _db.Files.FirstOrDefaultAsync(f =>
            f.Id == fileId &&
            f.VersionId == versionId);

        if (assetFile == null)
            return NotFound(new
            {
                message = "File not found."
            });

        var filename = request.Filename?.Trim();
        var checksum = request.Checksum?.Trim();

        if (!IsValidFilename(filename))
            return BadRequest(new
            {
                message = "Invalid filename."
            });

        if (string.IsNullOrWhiteSpace(request.Kind) ||
            request.Kind.Trim().Length > 50)
        {
            return BadRequest(new
            {
                message = "Valid Kind is required."
            });
        }

        if (request.SizeBytes <= 0 ||
            request.SizeBytes > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "Invalid file size."
            });
        }

        if (!IsValidChecksum(checksum))
            return BadRequest(new
            {
                message = "Invalid SHA-256 checksum."
            });

        assetFile.Filename = filename!;
        assetFile.Kind = request.Kind.Trim();
        assetFile.SizeBytes = request.SizeBytes;
        assetFile.Checksum = checksum;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "File metadata updated successfully.",
            data = assetFile
        });
    }

    // =====================================================
    // DELETE FILE
    // Admin only
    // =====================================================

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> DeleteFile(
        Guid versionId,
        Guid fileId)
    {
        if (!await CanAccessVersion(versionId))
            return Forbid();

        if (!IsAdmin)
            return Forbid();

        var version = await _db.ModelVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version == null)
            return NotFound(new
            {
                message = "Model version not found."
            });

        var assetFile = await _db.Files.FirstOrDefaultAsync(f =>
            f.Id == fileId &&
            f.VersionId == versionId);

        if (assetFile == null)
            return NotFound(new
            {
                message = "File not found."
            });

        // Delete Google Drive binary first.
        if (!string.IsNullOrWhiteSpace(assetFile.DriveFileId))
        {
            try
            {
                await _googleDrive.DeleteAsync(
                    assetFile.DriveFileId);
            }
            catch (GoogleApiException ex)
                when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Already deleted from Drive.
            }
            catch (GoogleApiException)
            {
                return StatusCode(502, new
                {
                    message = "Google Drive deletion failed."
                });
            }
            catch (InvalidOperationException)
            {
                return StatusCode(503, new
                {
                    message = "Google Drive service unavailable."
                });
            }
        }
        else
        {
            // Delete local file if it exists.
            var localPath = GetLocalFilePath(
                version,
                assetFile.Filename);

            if (localPath != null)
            {
                try
                {
                    System.IO.File.Delete(localPath);
                }
                catch (IOException)
                {
                    return StatusCode(500, new
                    {
                        message = "Local file could not be deleted."
                    });
                }
            }
        }

        _db.Files.Remove(assetFile);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "File deleted successfully."
        });
    }
}

// =========================================================
// UPDATE DTO
// =========================================================

public class UpdateAssetFileRequest
{
    public string Filename { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string? Checksum { get; set; }
}
