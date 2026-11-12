
using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateCompanyRequest
{
    [Required]
    [StringLength(200, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}
