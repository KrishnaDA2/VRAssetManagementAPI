using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class AddProjectMemberRequest
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public string Role { get; set; } = string.Empty;
}