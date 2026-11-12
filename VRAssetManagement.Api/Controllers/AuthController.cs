
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher<AppUser> _passwordHasher;
    private readonly IConfiguration _configuration;

    private static readonly string[] AllowedRoles =
    {
        "Admin",
        "Developer",
        "3DArtist",
        "3D Artist"
    };

    public AuthController(
        ApplicationDbContext db,
        IPasswordHasher<AppUser> passwordHasher,
        IConfiguration configuration)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
    }

    // POST: api/v1/auth/login
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var username = request.Username?.Trim();

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(new
            {
                message = "Invalid username or password."
            });
        }

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Username == username && u.Active);

        if (user == null ||
            string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return Unauthorized(new
            {
                message = "Invalid username or password."
            });
        }

        // Validate user role.
        if (!AllowedRoles.Contains(
                user.GlobalRole,
                StringComparer.OrdinalIgnoreCase))
        {
            return StatusCode(403, new
            {
                message = "User role is not configured correctly."
            });
        }

        // Verify password.
        var verification = _passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new
            {
                message = "Invalid username or password."
            });
        }

        // Rehash password when required.
        if (verification ==
            PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(
                user,
                request.Password);

            await _db.SaveChangesAsync();
        }

        // JWT configuration.
        var key = _configuration["Jwt:Key"];
        var issuer = _configuration["Jwt:Issuer"];
        var audience = _configuration["Jwt:Audience"];

        if (string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(audience))
        {
            return StatusCode(500, new
            {
                message = "JWT configuration is incomplete."
            });
        }

        if (Encoding.UTF8.GetByteCount(key) < 32)
        {
            return StatusCode(500, new
            {
                message = "JWT signing key must contain at least 32 bytes."
            });
        }

        var expiryMinutes = _configuration.GetValue<int>(
            "Jwt:ExpiryMinutes", 60);

        if (expiryMinutes <= 0)
        {
            return StatusCode(500, new
            {
                message = "JWT expiry configuration is invalid."
            });
        }

        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        // JWT claims.
        var claims = new List<Claim>
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                user.Id.ToString()),

            new Claim(
                ClaimTypes.Name,
                user.Username),

            new Claim(
                ClaimTypes.Email,
                user.Email),

            new Claim(
                ClaimTypes.Role,
                NormalizeRole(user.GlobalRole))
        };

        var signingKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(key));

        var credentials = new SigningCredentials(
            signingKey,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        var tokenHandler = new JwtSecurityTokenHandler();

        var jwtToken = tokenHandler.WriteToken(token);

        return Ok(new LoginResponse
        {
            Id = user.Id,
            Token = jwtToken,
            ExpiresAtUtc = expiresAt,
            Name = user.Name,
            Email = user.Email,
            Role = NormalizeRole(user.GlobalRole)
        });
    }

    // GET: api/v1/auth/me
    // Returns the currently authenticated user and latest DB permissions.
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userIdValue = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdValue, out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid user identity."
            });
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Active);

        if (user == null)
        {
            return Unauthorized(new
            {
                message = "User not found or inactive."
            });
        }

        List<string> permissions;

        try
        {
            permissions = JsonSerializer.Deserialize<List<string>>(
                user.PermissionsJson ?? "[]"
            ) ?? new List<string>();
        }
        catch (JsonException)
        {
            return StatusCode(500, new
            {
                message = "User permissions configuration is invalid."
            });
        }

        return Ok(new
        {
            id = user.Id,
            name = user.Name,
            username = user.Username,
            email = user.Email,
            role = NormalizeRole(user.GlobalRole),
            permissions = permissions
        });
    }
    // POST: api/v1/auth/set-initial-password
    // Only Admin can set an active user's initial password.
    [Authorize(Roles = "Admin")]
    [HttpPost("set-initial-password")]
    public async Task<IActionResult> SetInitialPassword(
        [FromBody] SetInitialPasswordRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var email = request.Email.Trim();

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == email && u.Active);

        if (user == null)
        {
            return NotFound(new
            {
                message = "Active user not found."
            });
        }

        if (!string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return Conflict(new
            {
                message = "Password has already been set."
            });
        }

        user.PasswordHash = _passwordHasher.HashPassword(
            user,
            request.Password);

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Initial password set successfully."
        });
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

        if (role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return "Admin";

        if (role.Equals("Developer", StringComparison.OrdinalIgnoreCase))
            return "Developer";

        return role;
    }
}

// Initial password request DTO.
public class SetInitialPasswordRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(12)]
    public string Password { get; set; } = string.Empty;
}
