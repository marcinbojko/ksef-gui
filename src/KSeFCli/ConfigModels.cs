using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

public sealed class KsefCliConfig
{
    public string ActiveProfile { get; init; } = "";
    public Dictionary<string, ProfileConfig> Profiles { get; init; } = new();
}

public sealed class ProfileConfig
{
    public string Environment { get; init; } = "";
    public string Nip { get; init; } = "";
    public CertificateConfig? Certificate { get; init; }
    public string? Token { get; init; }

    public AuthMethod AuthMethod => Certificate != null ? AuthMethod.Xades : AuthMethod.KsefToken;
}

public sealed class CertificateConfig
{
    public string? Private_Key { get; init; }
    public string? Private_Key_File { get; init; }
    public string? Certificate { get; init; }
    public string? Certificate_File { get; init; }
    public string? Password { get; init; }
    public string? Password_Env { get; init; }
    public string? Password_File { get; init; }

    public AuthenticationTokenSubjectIdentifierTypeEnum SubjectIdentifierType => AuthenticationTokenSubjectIdentifierTypeEnum.CertificateSubject;
}

public enum AuthMethod
{
    Xades,
    KsefToken
}
