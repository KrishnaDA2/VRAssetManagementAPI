namespace VRAssetManagement.Application.DTOs;

public class LoginResponse
{
    public Guid Id { get; set; }
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;
}