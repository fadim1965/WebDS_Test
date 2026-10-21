using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Models;

public sealed class ApplyCouponModel
{
    [Required, StringLength(64)]
    public string Code { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
