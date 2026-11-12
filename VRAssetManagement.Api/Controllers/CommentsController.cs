
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VRAssetManagement.Api.Authorization;
using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Application.Services;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/versions/{versionId:guid}/comments")]
[Authorize]
public class CommentsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAuditLogService _auditLogService;

    public CommentsController(
        ApplicationDbContext db,
        IAuditLogService auditLogService)
    {
        _db = db;
        _auditLogService = auditLogService;
    }

    private Guid? CurrentUserId
    {
        get
        {
            var value = User.FindFirstValue(
                ClaimTypes.NameIdentifier);

            return Guid.TryParse(value, out var id)
                ? id
                : null;
        }
    }

    private bool IsAdmin => User.IsInRole("Admin");

    private async Task<bool> CanAccessVersionAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId is not Guid userId)
            return false;

        var activeUser = await _db.Users
            .AsNoTracking()
            .AnyAsync(
                u => u.Id == userId && u.Active,
                cancellationToken);

        if (!activeUser)
            return false;

        var version = await _db.ModelVersions
            .AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => new
            {
                v.Id,
                v.Model.ProjectId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (version == null)
            return false;

        // Admin still needs the endpoint's required permission.
        // This only preserves the existing Admin project-scope exemption.
        if (IsAdmin)
            return true;

        return await _db.ProjectMembers
            .AsNoTracking()
            .AnyAsync(
                pm => pm.ProjectId == version.ProjectId &&
                      pm.UserId == userId,
                cancellationToken);
    }

    // GET: api/v1/versions/{versionId}/comments
    [HttpGet]
    [RequirePermission("preview")]
    public async Task<IActionResult> GetComments(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var versionExists = await _db.ModelVersions
            .AnyAsync(
                v => v.Id == versionId,
                cancellationToken);

        if (!versionExists)
        {
            return NotFound(new
            {
                message = "Model version not found."
            });
        }

        if (!await CanAccessVersionAsync(
                versionId,
                cancellationToken))
        {
            return Forbid();
        }

        var comments = await _db.Comments
            .AsNoTracking()
            .Where(c => c.VersionId == versionId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.VersionId,
                c.UserId,
                c.Body,
                c.PinX,
                c.PinY,
                c.PinZ,
                c.Resolved,
                c.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(comments);
    }

    // POST: api/v1/versions/{versionId}/comments
    [HttpPost]
    [RequirePermission("comment")]
    public async Task<IActionResult> CreateComment(
        Guid versionId,
        [FromBody] CreateCommentRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (request == null ||
            string.IsNullOrWhiteSpace(request.Body))
        {
            return BadRequest(new
            {
                message = "Comment body is required."
            });
        }

        if (CurrentUserId is not Guid userId)
            return Unauthorized();

        var versionExists = await _db.ModelVersions
            .AnyAsync(
                v => v.Id == versionId,
                cancellationToken);

        if (!versionExists)
        {
            return NotFound(new
            {
                message = "Model version not found."
            });
        }

        if (!await CanAccessVersionAsync(
                versionId,
                cancellationToken))
        {
            return Forbid();
        }

        var body = request.Body.Trim();

        if (body.Length > 5000)
        {
            return BadRequest(new
            {
                message = "Comment cannot exceed 5000 characters."
            });
        }

        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            VersionId = versionId,
            UserId = userId,
            Body = body,
            PinX = request.PinX,
            PinY = request.PinY,
            PinZ = request.PinZ,
            Resolved = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.Comments.Add(comment);

        await _db.SaveChangesAsync(cancellationToken);

        await _auditLogService.LogAsync(
            userId,
            "CommentCreated",
            "Comment",
            comment.Id);

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                comment.Id,
                comment.VersionId,
                comment.UserId,
                comment.Body,
                comment.PinX,
                comment.PinY,
                comment.PinZ,
                comment.Resolved,
                comment.CreatedAt
            });
    }

    // PATCH: api/v1/versions/{versionId}/comments/{commentId}/resolve
    [HttpPatch("{commentId:guid}/resolve")]
    [RequirePermission("comment")]
    public async Task<IActionResult> ResolveComment(
        Guid versionId,
        Guid commentId,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId is not Guid userId)
            return Unauthorized();

        if (!await CanAccessVersionAsync(
                versionId,
                cancellationToken))
        {
            return Forbid();
        }

        var comment = await _db.Comments
            .FirstOrDefaultAsync(
                c => c.Id == commentId &&
                     c.VersionId == versionId,
                cancellationToken);

        if (comment == null)
        {
            return NotFound(new
            {
                message = "Comment not found."
            });
        }

        if (comment.Resolved)
        {
            return Ok(new
            {
                message = "Comment is already resolved.",
                comment.Id,
                comment.Resolved
            });
        }

        comment.Resolved = true;

        await _db.SaveChangesAsync(cancellationToken);

        await _auditLogService.LogAsync(
            userId,
            "CommentResolved",
            "Comment",
            comment.Id);

        return Ok(new
        {
            message = "Comment resolved successfully.",
            comment.Id,
            comment.Resolved
        });
    }
}
