using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateCommentRequest
{
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Body { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public float? PinX { get; set; }

    public float? PinY { get; set; }

    public float? PinZ { get; set; }
}