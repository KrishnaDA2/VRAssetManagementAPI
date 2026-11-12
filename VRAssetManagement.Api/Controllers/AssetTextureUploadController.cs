using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/versions/{versionId:guid}/files")]
[Authorize]
public sealed class AssetTextureUploadController : ControllerBase
{
    private const long MaxUploadBytes = 2_147_483_648L;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".webp"
    };

    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public AssetTextureUploadController(ApplicationDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    [HttpPost("upload-textures")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> UploadTextures(Guid versionId, [FromForm] List<IFormFile> files)
    {
        var version = await _db.ModelVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version is null)
            return NotFound(new { message = "Model version not found." });

        if (files is null || files.Count == 0)
            return BadRequest(new { message = "Select at least one texture image." });

        long totalBytes = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(name);
            if (string.IsNullOrWhiteSpace(name) || name != file.FileName ||
                name.Contains('/') || name.Contains('\\'))
                return BadRequest(new { message = "Invalid texture filename." });

            if (!AllowedExtensions.Contains(extension))
                return BadRequest(new { message = $"Texture type '{extension}' is not allowed." });

            if (file.Length <= 0)
                return BadRequest(new { message = $"Texture '{name}' is empty." });

            totalBytes += file.Length;
            if (totalBytes > MaxUploadBytes)
                return BadRequest(new { message = "Total texture upload size cannot exceed 2 GB." });
        }

        var uploadRoot = Path.Combine(
            _environment.ContentRootPath, "App_Data", "uploads",
            version.ModelId.ToString("N"), $"v{version.VersionNo:D3}");

        Directory.CreateDirectory(uploadRoot);
        var createdPaths = new List<string>();
        await using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            var results = new List<object>();
            foreach (var file in files)
            {
                var originalName = Path.GetFileName(file.FileName);
                var extension = Path.GetExtension(originalName);
                var baseName = Path.GetFileNameWithoutExtension(originalName);
                var safeName = originalName;
                var path = Path.Combine(uploadRoot, safeName);

                if (System.IO.File.Exists(path))
                {
                    safeName = $"{baseName}_{Guid.NewGuid():N}{extension}";
                    path = Path.Combine(uploadRoot, safeName);
                }

                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await file.CopyToAsync(stream);
                createdPaths.Add(path);

                string checksum;
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    checksum = Convert.ToHexString(await SHA256.HashDataAsync(input));

                var assetFile = new AssetFile
                {
                    Id = Guid.NewGuid(),
                    VersionId = versionId,
                    Kind = "Texture",
                    Filename = safeName,
                    SizeBytes = file.Length,
                    Checksum = checksum,
                    DriveFileId = null
                };

                _db.Files.Add(assetFile);
                results.Add(new
                {
                    assetFile.Id,
                    assetFile.VersionId,
                    assetFile.Kind,
                    assetFile.Filename,
                    assetFile.SizeBytes,
                    assetFile.Checksum,
                    downloadUrl = $"/api/v1/versions/{versionId}/files/{assetFile.Id}/download"
                });
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return StatusCode(StatusCodes.Status201Created, results);
        }
        catch
        {
            try { await transaction.RollbackAsync(); } catch { /* preserve original exception */ }
            foreach (var path in createdPaths)
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* best effort cleanup */ }
            }
            throw;
        }
    }
}
