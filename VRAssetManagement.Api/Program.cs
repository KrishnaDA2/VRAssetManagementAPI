
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using VRAssetManagement.Api.Services;
using VRAssetManagement.Application.Services;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;
using VRAssetManagement.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// =====================================================
// CONFIGURATION
// =====================================================

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(
        optional: true,
        reloadOnChange: true);
}

// =====================================================
// CONTROLLERS AND OPENAPI
// =====================================================

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// =====================================================
// DATABASE CONFIGURATION
// =====================================================

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "DefaultConnection is missing. Configure it in appsettings.json or User Secrets.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseSqlServer(connectionString, sqlOptions =>
    {
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null);
    });
});

// =====================================================
// APPLICATION SERVICES
// =====================================================

builder.Services.AddScoped<IAuditLogService, AuditLogService>();

builder.Services.AddScoped<
    IPasswordHasher<AppUser>,
    PasswordHasher<AppUser>>();

builder.Services.AddScoped<GoogleDriveStorageService>();

// =====================================================
// JWT CONFIGURATION
// =====================================================

var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException(
        "JWT key is missing. Configure Jwt:Key in User Secrets.");
}

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException(
        "JWT key must contain at least 32 bytes.");
}

if (string.IsNullOrWhiteSpace(jwtIssuer))
{
    throw new InvalidOperationException(
        "JWT issuer is missing. Configure Jwt:Issuer.");
}

if (string.IsNullOrWhiteSpace(jwtAudience))
{
    throw new InvalidOperationException(
        "JWT audience is missing. Configure Jwt:Audience.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,

                ValidateAudience = true,
                ValidAudience = jwtAudience,

                ValidateIssuerSigningKey = true,

                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtKey)),

                ValidateLifetime = true,
                RequireExpirationTime = true,

                ClockSkew = TimeSpan.FromMinutes(1),

                RoleClaimType = ClaimTypes.Role,
                NameClaimType = ClaimTypes.Name
            };
    });

// =====================================================
// AUTHORIZATION
// =====================================================

builder.Services.AddAuthorization();

// =====================================================
// FILE UPLOAD CONFIGURATION
// =====================================================

const long MaxUploadSize = 500L * 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxUploadSize;
});

// =====================================================
// BUILD APPLICATION
// =====================================================

var app = builder.Build();

// =====================================================
// DATABASE + BOOTSTRAP ADMIN INITIALIZATION
// =====================================================

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    var logger = services.GetRequiredService<
        ILogger<Program>>();

    var db = services.GetRequiredService<
        ApplicationDbContext>();

    var passwordHasher = services.GetRequiredService<
        IPasswordHasher<AppUser>>();

    var configuration = services.GetRequiredService<
        IConfiguration>();

    // -------------------------------------------------
    // 1. Verify SQL Server connection
    // -------------------------------------------------

    try
    {
        var dbConnection = db.Database.GetDbConnection();

        logger.LogInformation(
            "Connecting to SQL Server. Server: {Server}, Database: {Database}",
            dbConnection.DataSource,
            dbConnection.Database);

        await db.Database.OpenConnectionAsync();

        logger.LogInformation(
            "SQL Server connection successful.");

        await db.Database.CloseConnectionAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "SQL Server connection failed. Check DefaultConnection.");

        throw new InvalidOperationException(
            "Cannot connect to SQL Server. Check DefaultConnection. " +
            "See the inner exception for the actual SQL error.",
            ex);
    }

    // -------------------------------------------------
    // 2. Bootstrap Admin configuration
    // -------------------------------------------------

    var adminName =
        configuration["BootstrapAdmin:Name"]?.Trim();

    var adminEmail =
        configuration["BootstrapAdmin:Email"]?.Trim();

    var adminPassword =
        configuration["BootstrapAdmin:Password"];

    if (string.IsNullOrWhiteSpace(adminName) ||
        string.IsNullOrWhiteSpace(adminEmail) ||
        string.IsNullOrWhiteSpace(adminPassword) ||
        adminPassword.Length < 12)
    {
        throw new InvalidOperationException(
            "Bootstrap Admin configuration is missing or invalid. " +
            "Configure BootstrapAdmin:Name, BootstrapAdmin:Email " +
            "and a password of at least 12 characters.");
    }

    // -------------------------------------------------
    // 3. Find existing Admin
    // -------------------------------------------------

    var normalizedEmail = adminEmail.ToLowerInvariant();

    var existingUser = await db.Users
        .FirstOrDefaultAsync(u =>
            u.Email.ToLower() == normalizedEmail);

    // -------------------------------------------------
    // 4. Create Bootstrap Admin if not exists
    // -------------------------------------------------

    if (existingUser == null)
    {
        var admin = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = adminEmail,
            Name = adminName,
            Email = adminEmail,
            GlobalRole = "Admin",
            Active = true,
            PermissionsJson = "[]"
        };
        admin.PasswordHash =
            passwordHasher.HashPassword(
                admin,
                adminPassword);

        db.Users.Add(admin);

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Bootstrap Admin account created successfully.");
    }
    else
    {
        if (!string.Equals(
            existingUser.GlobalRole,
            "Admin",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Bootstrap email belongs to a non-Admin user. " +
                "Configure a different BootstrapAdmin email.");
        }

        if (!existingUser.Active)
        {
            throw new InvalidOperationException(
                "Bootstrap Admin account is inactive.");
        }

        logger.LogInformation(
            "Bootstrap Admin account already exists.");
    }
}

// =====================================================
// HTTP PIPELINE
// =====================================================

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();
// =====================================================
// RUN
// =====================================================

app.Run();
