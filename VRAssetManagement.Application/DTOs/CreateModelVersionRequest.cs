
using System.ComponentModel.DataAnnotations;

namespace VRAssetManagement.Application.DTOs;

public class CreateModelVersionRequest
{
    
    [StringLength(2000)]
    public string? Notes { get; set; }

    [Required]
    public Guid UploadedBy { get; set; }
}
