using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

// The App Secret of the customer's own Meta app (App settings > Basic), for a manually set up
// connection. Write-only: responses say only whether one is saved.
public sealed class AppSecretRequest
{
    [Required, StringLength(64)]
    public required string AppSecret { get; set; }
}
