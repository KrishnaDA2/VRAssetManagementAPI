
using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class LoginRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    
   
    public string Password { get; set; } = string.Empty;
}