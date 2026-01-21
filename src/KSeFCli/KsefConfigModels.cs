using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

public sealed class KsefCliConfig
{
    public string ActiveProfile { get; set; } = null!;
    public Dictionary<string, ProfileConfig> Profiles { get; set; } = new();
}

public sealed class ProfileConfig
{
    public string Environment { get; set; } = null!;
    public string Nip { get; set; } = null!;

    public CertificateConfig? Certificate { get; set; }
    public string? Token { get; set; }

    public AuthMethod AuthMethod => Certificate != null ? AuthMethod.Xades : AuthMethod.KsefToken;
}

public sealed class CertificateConfig
{
    public string Private_Key { get; set; } = null!;
    public string Certificate { get; set; } = null!;
    public string Password { get; set; } = null!;
    public AuthenticationTokenSubjectIdentifierTypeEnum SubjectIdentifierType => AuthenticationTokenSubjectIdentifierTypeEnum.CertificateSubject;
}

public enum AuthMethod
{
    Xades,
    KsefToken
}
