
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Security.Claims;
using VRAssetManagement.Domain.Entities;
using VRAssetManagement.Infrastructure.Data;

namespace VRAssetManagement.Api.Controllers;

[ApiController]
[Route("api/v1/google-drive")]
[Authorize]
public class GoogleDriveController : ControllerBase
{
    private const string TokenUserId = "vrassetmanagement-dev-user";
    private const long MaxUploadBytes = 500L * 1024 * 1024;

    private static readonly string Scope = DriveService.Scope.DriveFile;

    private static readonly MemoryCache OAuthStates =
        new(new MemoryCacheOptions());

    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _db;

    private bool IsAdmin => User.IsInRole("Admin");

    private bool IsArtist =>
        User.IsInRole("3DArtist") ||
        User.IsInRole("3D Artist");

    private Guid? CurrentUserId
    {
        get
        {
            var value =
                User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                User.FindFirstValue("sub");

            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    private async Task<bool> IsActiveUserAsync()
    {
        if (CurrentUserId is not Guid userId)
            return false;

        return await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.Active);
    }

    private async Task<bool> CanUploadToDriveAsync()
    {
        return (IsAdmin || IsArtist) && await IsActiveUserAsync();
    }

    public GoogleDriveController(
        IConfiguration configuration,
        ApplicationDbContext db)
    {
        _configuration = configuration;
        _db = db;
    }

    private string ClientId =>
        _configuration["GoogleDrive:ClientId"]?.Trim()
        ?? throw new InvalidOperationException(
            "GoogleDrive:ClientId is missing from User Secrets.");

    private string ClientSecret =>
        _configuration["GoogleDrive:ClientSecret"]?.Trim()
        ?? throw new InvalidOperationException(
            "GoogleDrive:ClientSecret is missing from User Secrets.");

    private string RedirectUri =>
        _configuration["GoogleDrive:RedirectUri"]?.Trim()
        ?? throw new InvalidOperationException(
            "GoogleDrive:RedirectUri is missing from User Secrets.");

    private static string TokenFolder =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VRAssetManagement",
            "GoogleOAuth");

    private GoogleAuthorizationCodeFlow CreateFlow()
    {
        return new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = ClientId,
                    ClientSecret = ClientSecret
                },
                Scopes = new[] { Scope },
                DataStore = new FileDataStore(TokenFolder, true)
            });
    }

    private async Task<DriveService?> GetDriveServiceAsync()
    {
        var flow = CreateFlow();

        TokenResponse? token = await flow.LoadTokenAsync(
            TokenUserId,
            CancellationToken.None);

        if (token == null)
            return null;

        var credential = new UserCredential(
            flow,
            TokenUserId,
            token);

        return new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "VRAssetManagement"
            });
    }

    private async Task<string> EnsureVersionFolderAsync(ModelVersion version, DriveService drive)
    {
        if (!string.IsNullOrWhiteSpace(version.DriveFolderId))
            return version.DriveFolderId;

        var folderMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = $"VRAsset_Version_{version.Id}",
            MimeType = "application/vnd.google-apps.folder"
        };

        var createRequest = drive.Files.Create(folderMetadata);
        createRequest.Fields = "id,name,webViewLink";
        var folder = await createRequest.ExecuteAsync();

        if (string.IsNullOrWhiteSpace(folder.Id))
            throw new InvalidOperationException("Google Drive did not return a folder ID.");

        version.DriveFolderId = folder.Id;
        await _db.SaveChangesAsync();
        return folder.Id;
    }

    // GET /api/v1/google-drive/versions/{versionId}/folder
    // Creates a folder for legacy versions that do not yet have one.
    [HttpGet("versions/{versionId:guid}/folder")]
    public async Task<IActionResult> GetVersionFolder([FromRoute] Guid versionId)
    {
        if (!IsAdmin || !await IsActiveUserAsync())
            return Forbid();

        var version = await _db.ModelVersions
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version == null)
            return NotFound(new { message = "Model version not found." });

        var drive = await GetDriveServiceAsync();
        if (drive == null)
            return BadRequest(new { message = "Google Drive is not connected. Call /connect first." });

        using (drive)
        {
            try
            {
                var folderId = await EnsureVersionFolderAsync(version, drive);
                return Ok(new
                {
                    versionId,
                    folderId,
                    folderUrl = $"https://drive.google.com/drive/folders/{folderId}"
                });
            }
            catch (Google.GoogleApiException ex)
            {
                return StatusCode((int)ex.HttpStatusCode, new
                {
                    message = "Could not create or retrieve the version's Google Drive folder.",
                    details = ex.Message
                });
            }
        }
    }

    // GET /api/v1/google-drive/connect
    [HttpGet("connect")]
    public async Task<IActionResult> Connect()
    {
        if (!IsAdmin || !await IsActiveUserAsync())
            return Forbid();

        var state = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        OAuthStates.Set(
            state,
            true,
            TimeSpan.FromMinutes(10));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state
        };

        var authorizationUrl =
            "https://accounts.google.com/o/oauth2/v2/auth?" +
            string.Join(
                "&",
                query.Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}=" +
                    $"{Uri.EscapeDataString(item.Value)}"));

        return Ok(new
        {
            authorizationUrl,
            message =
                "Open the authorization URL in your browser and finish authorization within 10 minutes."
        });
    }

    // GET /api/v1/google-drive/callback
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return BadRequest(new
            {
                message = "Google authorization was not completed.",
                error
            });
        }

        var hasCode = !string.IsNullOrWhiteSpace(code);
        var hasState = !string.IsNullOrWhiteSpace(state);

        var hasSavedState =
            hasState && OAuthStates.TryGetValue(state!, out _);

        if (!hasCode || !hasState || !hasSavedState)
        {
            return BadRequest(new
            {
                message =
                    "OAuth state is missing or expired. Call /connect again.",
                hasCode,
                hasState,
                hasSavedState
            });
        }

        OAuthStates.Remove(state!);

        var flow = CreateFlow();

        await flow.ExchangeCodeForTokenAsync(
            TokenUserId,
            code!,
            RedirectUri,
            CancellationToken.None);

        return Ok(new
        {
            message = "Google Drive connected successfully."
        });
    }

    // GET /api/v1/google-drive/status
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        if (!IsAdmin || !await IsActiveUserAsync())
            return Forbid();

        var drive = await GetDriveServiceAsync();

        if (drive == null)
        {
            return Ok(new
            {
                connected = false,
                message = "Google Drive is not connected. Call /connect first."
            });
        }

        using (drive)
        {
            try
            {
                var aboutRequest = drive.About.Get();
                aboutRequest.Fields = "user(emailAddress)";

                var about = await aboutRequest.ExecuteAsync();

                return Ok(new
                {
                    connected = true,
                    message = "Google Drive is connected.",
                    emailAddress = about.User?.EmailAddress
                });
            }
            catch (Google.GoogleApiException ex)
            {
                return StatusCode((int)ex.HttpStatusCode, new
                {
                    connected = false,
                    message = "Google Drive could not validate the saved token.",
                    details = ex.Message
                });
            }
        }
    }

    // POST /api/v1/google-drive/upload
    // multipart/form-data:
    // file = selected file
    // versionId = existing ModelVersions.Id
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile? file,
        [FromForm] Guid versionId)
    {
        // Admin and 3D Artist may upload binaries to Google Drive.
        // Only Admin can manage OAuth connection and Drive folders.
        if (!await CanUploadToDriveAsync())
            return Forbid();

        if (file == null || file.Length == 0)
        {
            return BadRequest(new
            {
                message = "Select a non-empty file."
            });
        }

        if (file.Length > MaxUploadBytes)
        {
            return BadRequest(new
            {
                message = "Maximum file size is 50 MB."
            });
        }

        if (versionId == Guid.Empty)
        {
            return BadRequest(new
            {
                message = "A valid versionId is required."
            });
        }

        var version = await _db.ModelVersions
            .FirstOrDefaultAsync(v => v.Id == versionId);

        if (version == null)
        {
            return NotFound(new
            {
                message = "Model version not found."
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

        var extension = Path.GetExtension(filename);

        var kind = extension.Equals(
            ".fbx", StringComparison.OrdinalIgnoreCase)
            ? "Model"
            : extension.Equals(
                ".zip", StringComparison.OrdinalIgnoreCase)
                ? "Archive"
                : "Texture";

        string checksum;

        await using (var hashStream = file.OpenReadStream())
        {
            var hashBytes = await SHA256.HashDataAsync(hashStream);
            checksum = Convert.ToHexString(hashBytes);
        }

        var drive = await GetDriveServiceAsync();

        if (drive == null)
        {
            return BadRequest(new
            {
                message =
                    "Google Drive is not connected. Call /connect first."
            });
        }

        using (drive)
        {
            string driveFolderId;
            try
            {
                driveFolderId = await EnsureVersionFolderAsync(version, drive);
            }
            catch (Google.GoogleApiException ex)
            {
                return StatusCode((int)ex.HttpStatusCode, new
                {
                    message = "Could not configure the Google Drive folder for this version.",
                    details = ex.Message
                });
            }

            await using var stream = file.OpenReadStream();
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = filename,
                Parents = new List<string> { driveFolderId }
            };

            var request = drive.Files.Create(
                metadata,
                stream,
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType);

            request.Fields = "id,name,mimeType,size,webViewLink";

            var result = await request.UploadAsync();

            if (result.Status != UploadStatus.Completed)
            {
                return StatusCode(500, new
                {
                    message = "Google Drive upload failed.",
                    details = result.Exception?.Message
                });
            }

            var uploadedFile = request.ResponseBody;

            if (uploadedFile == null ||
                string.IsNullOrWhiteSpace(uploadedFile.Id))
            {
                return StatusCode(500, new
                {
                    message = "Google Drive returned no file ID."
                });
            }

            var assetFile = new AssetFile
            {
                Id = Guid.NewGuid(),
                VersionId = versionId,
                Kind = kind,
                DriveFileId = uploadedFile.Id,
                Filename = filename,
                SizeBytes = file.Length,
                Checksum = checksum
            };

            _db.Files.Add(assetFile);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch
            {
                // The Drive upload has already succeeded.
                // The uploaded file may need reconciliation if SQL fails.
                throw;
            }

            return StatusCode(201, new
            {
                message =
                    "File uploaded to Google Drive and saved in SQL Server.",
                id = assetFile.Id,
                versionId = assetFile.VersionId,
                driveFileId = assetFile.DriveFileId,
                filename = assetFile.Filename,
                sizeBytes = assetFile.SizeBytes,
                checksum = assetFile.Checksum,
                driveFolderId,
                driveFolderUrl = $"https://drive.google.com/drive/folders/{driveFolderId}",
                webViewLink = uploadedFile.WebViewLink
            });
        }
    }

    // GET /api/v1/google-drive/files/{fileId}/download
    [HttpGet("files/{fileId}/download")]
    public async Task<IActionResult> Download(
        [FromRoute] string fileId)
    {
        if (!await CanUploadToDriveAsync())
            return Forbid();

        if (string.IsNullOrWhiteSpace(fileId))
        {
            return BadRequest(new
            {
                message = "File ID is required."
            });
        }

        var drive = await GetDriveServiceAsync();

        if (drive == null)
        {
            return BadRequest(new
            {
                message = "Google Drive is not connected."
            });
        }

        using (drive)
        {
            var metadataRequest = drive.Files.Get(fileId);
            metadataRequest.Fields = "id,name,mimeType,size,webViewLink";

            var metadata = await metadataRequest.ExecuteAsync();

            using var memory = new MemoryStream();

            var downloadRequest = drive.Files.Get(fileId);
            var progress = await downloadRequest.DownloadAsync(memory);

            if (progress.Status != DownloadStatus.Completed)
            {
                return StatusCode(500, new
                {
                    message = "File download failed."
                });
            }

            return File(
                memory.ToArray(),
                string.IsNullOrWhiteSpace(metadata.MimeType)
                    ? "application/octet-stream"
                    : metadata.MimeType,
                metadata.Name ?? "download");
        }
    }
}
