namespace IamOrchestrator.Models;

public class CertificateResponse
{
    public string CustomerName { get; set; } = string.Empty;
    public string CertificateData { get; set; } = string.Empty; // Base64 encoded PFX
    public string Thumbprint { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Password { get; set; } = string.Empty;
}
