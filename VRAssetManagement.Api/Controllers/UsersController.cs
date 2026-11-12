
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    private static readonly string[] AllowedRoles =
    {
        "Admin",
        "Developer",
        "3DArtist"
    };

    public UsersController(ApplicationDbContext db)
    {
        _db = db;
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

    private async Task<bool> IsActiveAdmin()
    {
        if (!User.IsInRole("Admin") ||
            CurrentUserId is null)
        {
            return false;
        }

        return await _db.Users.AnyAsync(u =>
            u.Id == CurrentUserId.Value &&
            u.Active);
    }

    private static string NormalizeRole(string role)
    {
        if (role.Equals(
                "3D Artist",
                StringComparison.OrdinalIgnoreCase) ||
            role.Equals(
                "3DArtist",
                StringComparison.OrdinalIgnoreCase))
        {
            return "3DArtist";
        }

        if (role.Equals(
                "Admin",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Admin";
        }

        if (role.Equals(
                "Developer",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Developer";
        }

        return string.Empty;
    }

    // GET: api/v1/users
    // Admin only.
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUsers()
    {
        if (!await IsActiveAdmin())
            return Forbid();

        var users = await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Name)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.Name,
                u.Email,
                u.GlobalRole,
                u.Active
            })
            .ToListAsync();

        return Ok(users);
    }

    // POST: api/v1/users
    // Admin only.
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateUser(
        [FromBody] CreateUserRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!await IsActiveAdmin())
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new
            {
                message = "Username, name and email are required."
            });
        }

        var username = request.Username.Trim();
        var name = request.Name.Trim();
        var email = request.Email.Trim();

        if (username.Length > 100 ||
            name.Length > 150 ||
            email.Length > 256)
        {
            return BadRequest(new
            {
                message = "One or more fields exceed the allowed length."
            });
        }

        // Normalize and validate role.
        var role = NormalizeRole(request.GlobalRole ?? string.Empty);

        if (string.IsNullOrWhiteSpace(role) ||
            !AllowedRoles.Contains(role))
        {
            return BadRequest(new
            {
                message = "Invalid role. Allowed roles: Admin, Developer, 3DArtist."
            });
        }

        // Check duplicate username.
        var usernameExists = await _db.Users.AnyAsync(u =>
            u.Username.ToLower() == username.ToLower());

        if (usernameExists)
        {
            return Conflict(new
            {
                message = "Username already exists."
            });
        }

        // Check duplicate email.
        var emailExists = await _db.Users.AnyAsync(u =>
            u.Email.ToLower() == email.ToLower());

        if (emailExists)
        {
            return Conflict(new
            {
                message = "Email already exists."
            });
        }

        string? googleId = null;

        if (!string.IsNullOrWhiteSpace(request.GoogleId))
        {
            googleId = request.GoogleId.Trim();

            var googleIdExists = await _db.Users.AnyAsync(u =>
                u.GoogleId == googleId);

            if (googleIdExists)
            {
                return Conflict(new
                {
                    message = "Google ID already exists."
                });
            }
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            Name = name,
            Email = email,
            GlobalRole = role,
            GoogleId = googleId,
            Active = true,

            // Initial password is set separately
            // through set-initial-password endpoint.
            PasswordHash = null
        };

        _db.Users.Add(user);

        await _db.SaveChangesAsync();

        return StatusCode(
            StatusCodes.Status201Created,
            new
            {
                user.Id,
                user.Username,
                user.Name,
                user.Email,
                user.GlobalRole,
                user.Active,
                message = "User created. Set the initial password before login."
            });
    }

    // PATCH: api/v1/users/{userId}/status
    // Admin can activate or deactivate a user.
    [HttpPatch("{userId:guid}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateUserStatus(
        Guid userId,
        [FromBody] UpdateUserStatusRequest request)
    {
        if (!await IsActiveAdmin())
            return Forbid();

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return NotFound(new
            {
                message = "User not found."
            });
        }

        if (CurrentUserId == userId && !request.Active)
        {
            return BadRequest(new
            {
                message = "You cannot deactivate your own account."
            });
        }

        // Prevent disabling the last active Admin.
        if (!request.Active &&
            user.GlobalRole == "Admin" &&
            user.Active)
        {
            var activeAdminCount = await _db.Users
                .CountAsync(u =>
                    u.GlobalRole == "Admin" &&
                    u.Active);

            if (activeAdminCount <= 1)
            {
                return Conflict(new
                {
                    message = "The last active Admin cannot be deactivated."
                });
            }
        }

        user.Active = request.Active;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "User status updated successfully.",
            user.Id,
            user.Username,
            user.Active
        });
    }
}

// Request DTO for user status.
public class UpdateUserStatusRequest
{
    public bool Active { get; set; }
}
