namespace KSeFCli.Config
{
    public class AppConfig
    {
        public KsefApiSettings KsefApi { get; set; } = new KsefApiSettings();
        public TokenStoreSettings TokenStore { get; set; } = new TokenStoreSettings();

        // Add other global settings here
    }

    public class KsefApiSettings
    {
        public string BaseUrl { get; set; } = "https://ksef.mf.gov.pl/api/"; // Default KSeF API base URL
        public string Environment { get; set; } = "prod"; // e.g., "prod", "test"
        public string CertificatePath { get; set; } = string.Empty; // Path to client certificate
        public string CertificatePassword { get; set; } = string.Empty; // Password for the certificate
    }

    public class TokenStoreSettings
    {
        public string Path { get; set; } = "~/.config/ksefcli/tokens.json"; // Default path for token cache
    }
}