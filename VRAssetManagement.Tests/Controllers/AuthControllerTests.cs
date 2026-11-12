using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VRAssetManagement.Api.Controllers;
using VRAssetManagement.Application.DTOs;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;
using Xunit;

namespace VRAssetManagement.Tests.Controllers;

public class AuthControllerTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher<AppUser> _passwordHasher;

    public AuthControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new ApplicationDbContext(options);
        _passwordHasher = new PasswordHasher<AppUser>();
    }
    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // ==========================================
    // HELPER METHODS
    // ==========================================

    private AuthController CreateController(
        Dictionary<string, string?>? jwtSettings = null)
    {
        jwtSettings ??= new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "ThisIsATestSecretKeyWithAtLeast32Bytes!",
            ["Jwt:Issuer"] = "VRAssetManagement.Tests",
            ["Jwt:Audience"] = "VRAssetManagement.Tests",
            ["Jwt:ExpiryMinutes"] = "60"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(jwtSettings)
            .Build();

        return new AuthController(
            _db,
            _passwordHasher,
            configuration);
    }

    private async Task<AppUser> CreateUserAsync(
        string username = "testuser",
        string password = "TestPassword123!",
        string role = "Developer",
        bool active = true)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = "Test User",
            Username = username,
            Email = "test@example.com",
            GlobalRole = role,
            Active = active,
            PermissionsJson = "[]"
        };

        user.PasswordHash = _passwordHasher.HashPassword(
            user,
            password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return user;
    }

    private void SetAuthenticatedUser(
        AuthController controller,
        string userId)
    {
        var claims = new[]
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                userId)
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    // ==========================================
    // LOGIN TESTS
    // ==========================================

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOk()
    {
        await CreateUserAsync();

        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<LoginResponse>(okResult.Value);

        Assert.NotEmpty(response.Token);
        Assert.Equal("Developer", response.Role);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        await CreateUserAsync();

        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "WrongPassword123!"
        };

        var result = await controller.Login(request);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_UserDoesNotExist_ReturnsUnauthorized()
    {
        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "unknownuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_InactiveUser_ReturnsUnauthorized()
    {
        await CreateUserAsync(active: false);

        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_InvalidRole_ReturnsForbidden()
    {
        await CreateUserAsync(role: "Guest");

        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        var statusResult = Assert.IsType<ObjectResult>(result);

        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task Login_MissingJwtConfiguration_ReturnsInternalServerError()
    {
        await CreateUserAsync();

        var controller = CreateController(
            new Dictionary<string, string?>
            {
                ["Jwt:Key"] = null,
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience"
            });

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        var statusResult = Assert.IsType<ObjectResult>(result);

        Assert.Equal(500, statusResult.StatusCode);
    }

    [Fact]
    public async Task Login_ShortJwtKey_ReturnsInternalServerError()
    {
        await CreateUserAsync();

        var controller = CreateController(
            new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "short-key",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience"
            });

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        var statusResult = Assert.IsType<ObjectResult>(result);

        Assert.Equal(500, statusResult.StatusCode);
    }

    [Fact]
    public async Task Login_ThreeDArtistRole_ReturnsNormalizedRole()
    {
        await CreateUserAsync(role: "3D Artist");

        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<LoginResponse>(okResult.Value);

        Assert.Equal("3DArtist", response.Role);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsValidJwtToken()
    {
        var user = await CreateUserAsync();

        var controller = CreateController();

        var request = new LoginRequest
        {
            Username = "testuser",
            Password = "TestPassword123!"
        };

        var result = await controller.Login(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<LoginResponse>(okResult.Value);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(response.Token);

        Assert.Equal(
            user.Id.ToString(),
            token.Claims.First(c =>
                c.Type == ClaimTypes.NameIdentifier).Value);

        Assert.Equal(
            "testuser",
            token.Claims.First(c =>
                c.Type == ClaimTypes.Name).Value);
    }

    // ==========================================
    // GET CURRENT USER TESTS
    // ==========================================

    [Fact]
    public async Task GetCurrentUser_ValidUser_ReturnsOk()
    {
        var user = await CreateUserAsync();

        user.PermissionsJson =
            "[\"Models.View\",\"Models.Create\"]";

        await _db.SaveChangesAsync();

        var controller = CreateController();

        SetAuthenticatedUser(controller, user.Id.ToString());

        var result = await controller.GetCurrentUser();

        var okResult = Assert.IsType<OkObjectResult>(result);

        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task GetCurrentUser_InvalidIdentity_ReturnsUnauthorized()
    {
        var controller = CreateController();

        SetAuthenticatedUser(controller, "invalid-guid");

        var result = await controller.GetCurrentUser();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetCurrentUser_InactiveUser_ReturnsUnauthorized()
    {
        var user = await CreateUserAsync(active: false);

        var controller = CreateController();

        SetAuthenticatedUser(controller, user.Id.ToString());

        var result = await controller.GetCurrentUser();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetCurrentUser_InvalidPermissionsJson_ReturnsInternalServerError()
    {
        var user = await CreateUserAsync();

        user.PermissionsJson = "{invalid-json";

        await _db.SaveChangesAsync();

        var controller = CreateController();

        SetAuthenticatedUser(controller, user.Id.ToString());

        var result = await controller.GetCurrentUser();

        var statusResult = Assert.IsType<ObjectResult>(result);

        Assert.Equal(500, statusResult.StatusCode);
    }

    // ==========================================
    // SET INITIAL PASSWORD TESTS
    // ==========================================

    [Fact]
    public async Task SetInitialPassword_ValidRequest_ReturnsOk()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = "New User",
            Username = "newuser",
            Email = "newuser@example.com",
            GlobalRole = "Developer",
            Active = true,
            PasswordHash = null,
            PermissionsJson = "[]"
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var controller = CreateController();

        var request = new SetInitialPasswordRequest
        {
            Email = "newuser@example.com",
            Password = "NewPassword123!"
        };

        var result = await controller.SetInitialPassword(request);

        Assert.IsType<OkObjectResult>(result);

        var updatedUser = await _db.Users.FindAsync(user.Id);

        Assert.NotNull(updatedUser);
        Assert.False(string.IsNullOrWhiteSpace(updatedUser.PasswordHash));

        var verification = _passwordHasher.VerifyHashedPassword(
            updatedUser,
            updatedUser.PasswordHash!,
            "NewPassword123!");

        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            verification);
    }

    [Fact]
    public async Task SetInitialPassword_UserNotFound_ReturnsNotFound()
    {
        var controller = CreateController();

        var request = new SetInitialPasswordRequest
        {
            Email = "missing@example.com",
            Password = "NewPassword123!"
        };

        var result = await controller.SetInitialPassword(request);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SetInitialPassword_PasswordAlreadySet_ReturnsConflict()
    {
        await CreateUserAsync();

        var controller = CreateController();

        var request = new SetInitialPasswordRequest
        {
            Email = "test@example.com",
            Password = "AnotherPassword123!"
        };

        var result = await controller.SetInitialPassword(request);

        Assert.IsType<ConflictObjectResult>(result);
    }



    [Fact]
    public async Task SetInitialPassword_InvalidModelState_ReturnsValidationProblem()
    {
        // Arrange
        var controller = CreateController();

        controller.ModelState.AddModelError(
            "Email",
            "Email is required.");

        var request = new SetInitialPasswordRequest
        {
            Email = "",
            Password = "NewPassword123!"
        };

        // Act
        var result = await controller.SetInitialPassword(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);

        Assert.Equal(400, objectResult.StatusCode);

        var problemDetails =
            Assert.IsType<ValidationProblemDetails>(objectResult.Value);

        Assert.Equal(400, problemDetails.Status);

        Assert.Contains("Email", problemDetails.Errors.Keys);
    }
}