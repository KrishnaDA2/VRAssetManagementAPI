
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.Upload;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace VRAssetManagement.Api.Services;

public sealed class GoogleDriveStorageService
{
    private const string TokenUserId = "vrassetmanagement-dev-user";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    // Drive scope is required to find and reuse manually created folders.
    private static readonly string[] Scopes =
    {
        DriveService.Scope.Drive
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<GoogleDriveStorageService> _logger;

    public GoogleDriveStorageService(
        IConfiguration configuration,
        ILogger<GoogleDriveStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private GoogleAuthorizationCodeFlow CreateFlow()
    {
        var clientId = _configuration["GoogleDrive:ClientId"];
        var clientSecret = _configuration["GoogleDrive:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Google Drive ClientId or ClientSecret is missing.");
        }

        var tokenFolder = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "VRAssetManagement",
            "GoogleOAuth");

        return new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = clientId.Trim(),
                    ClientSecret = clientSecret.Trim()
                },
                Scopes = Scopes,
                DataStore = new FileDataStore(tokenFolder, true)
            });
    }

    public async Task<DriveService?> GetDriveServiceAsync()
    {
        var flow = CreateFlow();

        TokenResponse? token = await flow.LoadTokenAsync(
            TokenUserId,
            CancellationToken.None);

        if (token == null)
            return null;

        var credential = new UserCredential(flow, TokenUserId, token);

        return new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "VRAssetManagement"
            });
    }

    public async Task<bool> IsConnectedAsync()
    {
        using var drive = await GetDriveServiceAsync();

        if (drive == null)
            return false;

        try
        {
            await drive.About.Get().ExecuteAsync();
            return true;
        }
        catch (GoogleApiException ex)
        {
            _logger.LogError(ex, "Google Drive verification failed.");
            return false;
        }
    }

    public string GetRootFolderId()
    {
        var id = _configuration["GoogleDrive:FolderId"];

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                "GoogleDrive:FolderId is missing.");
        }

        return id.Trim();
    }

    private static string EscapeQueryValue(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    // Finds an existing folder under the specified parent.
    // If it does not exist, creates it.
    public async Task<string> EnsureFolderAsync(
        string folderName,
        string parentFolderId)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name is required.");

        if (string.IsNullOrWhiteSpace(parentFolderId))
            throw new ArgumentException("Parent folder ID is required.");

        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        var safeName = folderName.Trim();
        var escapedName = EscapeQueryValue(safeName);

        var query =
            $"'{parentFolderId}' in parents " +
            $"and name = '{escapedName}' " +
            $"and mimeType = '{FolderMimeType}' " +
            "and trashed = false";

        var request = drive.Files.List();
        request.Q = query;
        request.Fields = "nextPageToken,files(id,name,parents)";
        request.PageSize = 100;

        var folders = new List<DriveFile>();

        do
        {
            var result = await request.ExecuteAsync();

            if (result.Files != null)
                folders.AddRange(result.Files);

            request.PageToken = result.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(request.PageToken));

        // Reuse an existing matching folder.
        if (folders.Count > 0)
        {
            if (folders.Count > 1)
            {
                _logger.LogWarning(
                    "Multiple folders named {FolderName} exist under parent {ParentId}. Reusing the first match.",
                    safeName,
                    parentFolderId);
            }

            return folders[0].Id;
        }

        return await CreateFolderAsync(safeName, parentFolderId);
    }

    public async Task<string> CreateFolderAsync(
        string folderName,
        string? parentFolderId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name is required.");

        parentFolderId ??= GetRootFolderId();

        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        var metadata = new DriveFile
        {
            Name = folderName.Trim(),
            MimeType = FolderMimeType,
            Parents = new List<string> { parentFolderId }
        };

        var request = drive.Files.Create(metadata);
        request.Fields = "id,name,mimeType,parents";

        var result = await request.ExecuteAsync();

        if (string.IsNullOrWhiteSpace(result.Id))
            throw new InvalidOperationException(
                "Google Drive did not return a folder ID.");

        _logger.LogInformation(
            "Created Drive folder {FolderName}: {FolderId}",
            folderName,
            result.Id);

        return result.Id;
    }

    public Task<string> CreateModelFolderAsync(
        string modelName,
        string projectFolderId)
    {
        return EnsureFolderAsync(modelName, projectFolderId);
    }

    public Task<string> CreateVersionFolderAsync(
    string modelFolderId,
    int versionNo)
    {
        return EnsureFolderAsync(
            $"Version-{versionNo:D2}",
            modelFolderId);
    }

    public Task<string> UploadAsync(
        Stream content,
        string filename,
        string contentType)
    {
        return UploadAsync(
            content,
            filename,
            contentType,
            GetRootFolderId());
    }

    public async Task<string> UploadAsync(
        Stream content,
        string filename,
        string contentType,
        string parentFolderId)
    {
        if (content == null || !content.CanRead)
            throw new InvalidOperationException(
                "File stream is not readable.");

        if (string.IsNullOrWhiteSpace(filename))
            throw new ArgumentException("Filename is required.");

        if (string.IsNullOrWhiteSpace(parentFolderId))
            throw new ArgumentException("Parent folder ID is required.");

        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        if (content.CanSeek)
            content.Position = 0;

        var mimeType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType;

        var metadata = new DriveFile
        {
            Name = Path.GetFileName(filename),
            MimeType = mimeType,
            Parents = new List<string> { parentFolderId }
        };

        var request = drive.Files.Create(
            metadata,
            content,
            mimeType);

        request.Fields = "id,name,mimeType,size,parents";

        var progress = await request.UploadAsync();

        if (progress.Status != UploadStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Google Drive upload failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
        }

        var uploaded = request.ResponseBody;

        if (uploaded == null || string.IsNullOrWhiteSpace(uploaded.Id))
            throw new InvalidOperationException(
                "Google Drive did not return a file ID.");

        return uploaded.Id;
    }

    public async Task<bool> FileExistsAsync(string driveFileId)
    {
        if (string.IsNullOrWhiteSpace(driveFileId))
            return false;

        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        try
        {
            var request = drive.Files.Get(driveFileId);
            request.Fields = "id,name,trashed";

            var file = await request.ExecuteAsync();

            return !string.IsNullOrWhiteSpace(file.Id) &&
                   file.Trashed != true;
        }
        catch (GoogleApiException ex)
            when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<DriveFile?> GetFileMetadataAsync(string driveFileId)
    {
        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        try
        {
            var request = drive.Files.Get(driveFileId);
            request.Fields = "id,name,mimeType,size,parents,webViewLink,trashed";

            return await request.ExecuteAsync();
        }
        catch (GoogleApiException ex)
            when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<(byte[] Content, string ContentType, string Filename)>
        DownloadAsync(string driveFileId)
    {
        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        var metadataRequest = drive.Files.Get(driveFileId);
        metadataRequest.Fields = "id,name,mimeType,trashed";

        var metadata = await metadataRequest.ExecuteAsync();

        if (metadata.Trashed == true)
            throw new FileNotFoundException(
                "Google Drive file is in trash.");

        using var memory = new MemoryStream();

        var downloadRequest = drive.Files.Get(driveFileId);
        var progress = await downloadRequest.DownloadAsync(memory);

        if (progress.Status !=
            Google.Apis.Download.DownloadStatus.Completed)
        {
            throw new InvalidOperationException(
                "Google Drive download did not complete.");
        }

        return (
            memory.ToArray(),
            metadata.MimeType ?? "application/octet-stream",
            Path.GetFileName(metadata.Name ?? "download"));
    }

    public async Task DeleteAsync(string driveFileId)
    {
        if (string.IsNullOrWhiteSpace(driveFileId))
            return;

        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        await drive.Files.Delete(driveFileId).ExecuteAsync();
    }

    public async Task DeleteFolderAsync(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return;

        using var drive = await GetDriveServiceAsync()
            ?? throw new InvalidOperationException(
                "Google Drive is not connected.");

        await drive.Files.Delete(folderId).ExecuteAsync();
    }
}
