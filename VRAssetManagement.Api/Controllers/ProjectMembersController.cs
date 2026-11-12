
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/members")]
[Authorize]
public class ProjectMembersController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public ProjectMembersController(ApplicationDbContext db)
    {
        _db = db;
    }

    // Current authenticated user ID.
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

    private bool IsAdmin =>
        User.IsInRole("Admin");

    private bool IsDeveloper =>
        User.IsInRole("Developer");

    private bool IsArtist =>
        User.IsInRole("3DArtist") ||
        User.IsInRole("3D Artist");

    private bool HasValidRole =>
        IsAdmin || IsDeveloper || IsArtist;

    // Normalize role names for consistent database storage.
    private static string NormalizeRole(string? role)
    {
        if (string.Equals(
                role,
                "3D Artist",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                role,
                "3DArtist",
                StringComparison.OrdinalIgnoreCase))
        {
            return "3DArtist";
        }

        if (string.Equals(
                role,
                "Developer",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Developer";
        }

        if (string.Equals(
                role,
                "Admin",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Admin";
        }

        return string.Empty;
    }

    // Verify active user.
    private async Task<bool> IsActiveUser()
    {
        if (CurrentUserId is null || !HasValidRole)
            return false;

        return await _db.Users.AnyAsync(u =>
            u.Id == CurrentUserId.Value &&
            u.Active);
    }

    // GET:
    // api/v1/projects/{projectId}/members
    // Admin: all members.
    // Developer / 3D Artist: assigned project members.
    [HttpGet]
    public async Task<IActionResult> GetMembers(Guid projectId)
    {
        if (!await IsActiveUser())
            return Forbid();

        var projectExists = await _db.Projects
            .AnyAsync(p => p.Id == projectId);

        if (!projectExists)
        {
            return NotFound(new
            {
                message = "Project not found."
            });
        }

        // Non-admin users must belong to this project.
        if (!IsAdmin)
        {
            var isMember = await _db.ProjectMembers
                .AnyAsync(pm =>
                    pm.ProjectId == projectId &&
                    pm.UserId == CurrentUserId!.Value);

            if (!isMember)
                return Forbid();
        }

        var members = await _db.ProjectMembers
            .AsNoTracking()
            .Where(pm =>
                pm.ProjectId == projectId &&
                pm.User.Active)
            .OrderBy(pm => pm.User.Name)
            .Select(pm => new
            {
                pm.ProjectId,
                pm.UserId,
                Name = pm.User.Name,
                Username = pm.User.Username,
                Email = pm.User.Email,
                Role = pm.Role
            })
            .ToListAsync();

        return Ok(members);
    }

    // POST:
    // api/v1/projects/{projectId}/members
    // Admin only.
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AddMember(
        Guid projectId,
        [FromBody] AddProjectMemberRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await IsActiveUser())
            return Forbid();

        var projectExists = await _db.Projects
            .AnyAsync(p => p.Id == projectId);

        if (!projectExists)
        {
            return NotFound(new
            {
                message = "Project not found."
            });
        }

        if (request.UserId == Guid.Empty)
        {
            return BadRequest(new
            {
                message = "Valid UserId is required."
            });
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u =>
                u.Id == request.UserId);

        if (user == null)
        {
            return NotFound(new
            {
                message = "User not found."
            });
        }

        if (!user.Active)
        {
            return BadRequest(new
            {
                message = "Inactive users cannot be assigned to projects."
            });
        }

        // Only Developer and 3DArtist can be project members.
        var requestedRole = NormalizeRole(request.Role);

        if (requestedRole != "Developer" &&
            requestedRole != "3DArtist")
        {
            return BadRequest(new
            {
                message = "Project role must be Developer or 3DArtist."
            });
        }

        // Project role must match global account role.
        var accountRole = NormalizeRole(user.GlobalRole);

        if (accountRole != requestedRole)
        {
            return BadRequest(new
            {
                message = "Project role must match the user's account role.",
                accountRole,
                requestedRole
            });
        }

        // Prevent duplicate membership.
        var alreadyMember = await _db.ProjectMembers
            .AnyAsync(pm =>
                pm.ProjectId == projectId &&
                pm.UserId == request.UserId);

        if (alreadyMember)
        {
            return Conflict(new
            {
                message = "User is already a member of this project."
            });
        }

        var member = new ProjectMember
        {
            ProjectId = projectId,
            UserId = request.UserId,
            Role = requestedRole
        };

        _db.ProjectMembers.Add(member);

        await _db.SaveChangesAsync();

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                member.ProjectId,
                member.UserId,
                Name = user.Name,
                Username = user.Username,
                Email = user.Email,
                member.Role,
                message = "Project member added successfully."
            });
    }

    // DELETE:
    // api/v1/projects/{projectId}/members/{userId}
    // Admin only.
    [HttpDelete("{userId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemoveMember(
        Guid projectId,
        Guid userId)
    {
        if (!await IsActiveUser())
            return Forbid();

        var member = await _db.ProjectMembers
            .FirstOrDefaultAsync(pm =>
                pm.ProjectId == projectId &&
                pm.UserId == userId);

        if (member == null)
        {
            return NotFound(new
            {
                message = "Project member not found."
            });
        }

        _db.ProjectMembers.Remove(member);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Project member removed successfully.",
            projectId,
            userId
        });
    }
}
