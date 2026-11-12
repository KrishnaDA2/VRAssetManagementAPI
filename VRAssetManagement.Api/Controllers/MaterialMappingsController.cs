
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/versions/{versionId:guid}/material-mappings")]
[Authorize]
public class MaterialMappingsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public MaterialMappingsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetMappings(Guid versionId)
    {
        var versionExists = await _db.ModelVersions
            .AnyAsync(v => v.Id == versionId);

        if (!versionExists)
            return NotFound(new { message = "Model version not found." });

        var mappings = await _db.MaterialMappings
            .Where(m => m.VersionId == versionId)
            .OrderBy(m => m.MaterialName)
            .Select(m => new
            {
                m.Id,
                m.VersionId,
                m.FileId,
                m.MaterialName,
                m.Slot,
                m.ColorSpace,
                m.FlipY,
                m.TilingX,
                m.TilingY,
                m.OffsetX,
                m.OffsetY
            })
            .ToListAsync();

        return Ok(mappings);
    }

    [HttpPost]
    public async Task<IActionResult> CreateMapping(
        Guid versionId,
        [FromBody] CreateMaterialMappingRequest request)
    {
        var versionExists = await _db.ModelVersions
            .AnyAsync(v => v.Id == versionId);

        if (!versionExists)
            return NotFound(new { message = "Model version not found." });

        if (request.FileId.HasValue)
        {
            var fileExists = await _db.Files.AnyAsync(f =>
                f.Id == request.FileId.Value &&
                f.VersionId == versionId);

            if (!fileExists)
                return BadRequest(new
                {
                    message = "File must exist and belong to this model version."
                });
        }

        var mapping = new MaterialMapping
        {
            Id = Guid.NewGuid(),
            VersionId = versionId,
            FileId = request.FileId,
            MaterialName = request.MaterialName.Trim(),
            Slot = request.Slot,
            ColorSpace = request.ColorSpace,
            FlipY = request.FlipY,
            TilingX = request.TilingX,
            TilingY = request.TilingY,
            OffsetX = request.OffsetX,
            OffsetY = request.OffsetY
        };

        _db.MaterialMappings.Add(mapping);
        await _db.SaveChangesAsync();

        return StatusCode(StatusCodes.Status201Created, new
        {
            mapping.Id,
            mapping.VersionId,
            mapping.FileId,
            mapping.MaterialName,
            mapping.Slot,
            mapping.ColorSpace,
            mapping.FlipY,
            mapping.TilingX,
            mapping.TilingY,
            mapping.OffsetX,
            mapping.OffsetY
        });
    }
}
