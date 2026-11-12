
using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateAssetFileRequest
{
    [Required]
    [StringLength(50)]
    public string Kind { get; set; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Filename { get; set; } = string.Empty;

    [Range(1, 2147483648L)]
    public long SizeBytes { get; set; }

    [RegularExpression(
        @"\A[0-9a-fA-F]{64}\z",
        ErrorMessage = "Checksum must be a valid SHA-256 hash.")]
    public string? Checksum { get; set; }

    [StringLength(500)]
    public string? DriveFileId { get; set; }
}
