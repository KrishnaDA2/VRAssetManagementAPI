using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateUserRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string GlobalRole { get; set; } = string.Empty;

    [StringLength(500)]
    public string? GoogleId { get; set; }
}