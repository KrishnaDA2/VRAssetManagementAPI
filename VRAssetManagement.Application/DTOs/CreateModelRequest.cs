
using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateModelRequest
{
    [Required]
    [StringLength(200, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    [RegularExpression(
        @"^[a-z0-9]+(?:-[a-z0-9]+)*$",
        ErrorMessage = "Slug must contain lowercase letters, numbers and hyphens.")]
    public string Slug { get; set; } = string.Empty;

    [StringLength(100)]
    public string Category { get; set; } = "Prop";

    public string? TagsJson { get; set; }
}
