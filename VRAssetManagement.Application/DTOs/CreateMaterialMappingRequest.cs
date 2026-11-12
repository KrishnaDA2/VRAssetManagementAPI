
using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateMaterialMappingRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string MaterialName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string Slot { get; set; } = "BaseColor";

    public Guid? FileId { get; set; }

    [Required]
    [RegularExpression(
        "^(Linear|SRGB)$",
        ErrorMessage = "ColorSpace must be Linear or SRGB.")]
    public string ColorSpace { get; set; } = "SRGB";

    public bool FlipY { get; set; } = false;

    [Range(0.001, 1000)]
    public float TilingX { get; set; } = 1;

    [Range(0.001, 1000)]
    public float TilingY { get; set; } = 1;

    [Range(-1000, 1000)]
    public float OffsetX { get; set; } = 0;

    [Range(-1000, 1000)]
    public float OffsetY { get; set; } = 0;
}
