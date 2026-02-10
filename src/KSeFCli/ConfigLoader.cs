using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KSeFCli;

public static class ConfigLoader
{
    private static readonly string TemplateConfig = @"# KSeFCli Configuration File
#
# This is a template configuration file. You need to configure at least one profile.
# For more information, see the documentation.

# active_profile: default

profiles:
  default:
    nip: ""1234567890""
    environment: test  # or 'prod' for production

    # Option 1: Use a long-term token (recommended)
    token: ""YOUR_TOKEN_HERE""

    # Option 2: Use certificate-based authentication
    # certificate:
    #   private_key_file: ~/path/to/private.key
    #   certificate_file: ~/path/to/certificate.pem
    #   password: ""certificate_password""
    #   # Or use environment variable for password:
    #   # password_env: KSEF_CERT_PASSWORD
";

    /// <summary>
    /// Creates the config directory and writes a template config file.
    /// Does nothing if the file already exists.
    /// </summary>
    public static void WriteTemplate(string configPath)
    {
        string? directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            Console.WriteLine($"Created config directory: {directory}");
        }
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, TemplateConfig);
            Console.WriteLine($"Created template config file: {configPath}");
        }
    }

    private static void EnsureConfigExists(string configPath)
    {
        WriteTemplate(configPath);
        throw new InvalidOperationException(
            $"Template configuration created at {configPath}. Please edit it with your credentials and try again.");
    }

    public static KsefCliConfig Load(string configPath, string? activeProfileNameOverride)
    {
        string absoluteConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(absoluteConfigPath))
        {
            EnsureConfigExists(absoluteConfigPath);
        }

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        KsefCliConfig config;
        try
        {
            config = deserializer.Deserialize<KsefCliConfig>(
                File.ReadAllText(absoluteConfigPath)
            );
        }
        catch (YamlException ex)
        {
            throw new Exception($"Exception during deserialization of '{absoluteConfigPath}'", ex);
        }

        string activeProfile = string.IsNullOrWhiteSpace(activeProfileNameOverride)
            ? config.ActiveProfile
            : activeProfileNameOverride;

        if (string.IsNullOrWhiteSpace(activeProfile))
        {
            if (config.Profiles.Count == 1)
            {
                activeProfile = config.Profiles.Keys.First();
            }
            else
            {
                throw new InvalidOperationException("Active profile not specified in config file or via --active option.");
            }
        }

        if (!config.Profiles.TryGetValue(activeProfile, out ProfileConfig? profile))
        {
            throw new InvalidOperationException($"Active profile '{activeProfile}' not found in configuration.");
        }

        string? configDir = Path.GetDirectoryName(absoluteConfigPath);

        Dictionary<string, ProfileConfig> resolvedProfiles = new Dictionary<string, ProfileConfig>();
        foreach ((string? profileName, ProfileConfig? profileConfig) in config.Profiles)
        {
            if (profileConfig.Certificate is not null && configDir is not null)
            {
                CertificateConfig cert = profileConfig.Certificate;
                string? resolvedPrivateKey = ResolveContent(cert.Private_Key, cert.Private_Key_File, configDir);
                string? resolvedCertificate = ResolveContent(cert.Certificate, cert.Certificate_File, configDir);
                string? resolvedPassword = cert.Password ??
                                           (cert.Password_Env is not null ? System.Environment.GetEnvironmentVariable(cert.Password_Env) : null) ??
                                           ResolveContent(null, cert.Password_File, configDir);

                CertificateConfig newCert = new CertificateConfig
                {
                    Private_Key = resolvedPrivateKey,
                    Certificate = resolvedCertificate,
                    Password = resolvedPassword,
                    Private_Key_File = cert.Private_Key_File,
                    Certificate_File = cert.Certificate_File,
                    Password_Env = cert.Password_Env,
                    Password_File = cert.Password_File,
                };

                resolvedProfiles[profileName] = new ProfileConfig
                {
                    Certificate = newCert,
                    Environment = profileConfig.Environment,
                    Nip = profileConfig.Nip,
                    Token = profileConfig.Token,
                };
            }
            else
            {
                resolvedProfiles[profileName] = profileConfig;
            }
        }

        KsefCliConfig finalConfig = new KsefCliConfig
        {
            ActiveProfile = activeProfile,
            Profiles = resolvedProfiles,
        };

        ValidateProfile(finalConfig.Profiles[finalConfig.ActiveProfile]);

        return finalConfig;
    }

    private static string? ResolveContent(string? content, string? filePath, string configDir)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string path = ExpandTilde(filePath);
        if (!Path.IsPathRooted(path))
        {
            path = Path.GetFullPath(Path.Combine(configDir, path));
        }

        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static void ValidateProfile(ProfileConfig profile)
    {
        bool hasCert = profile.Certificate != null;
        bool hasToken = !string.IsNullOrWhiteSpace(profile.Token);

        if (hasCert == hasToken)
        {
            throw new InvalidOperationException(
                "Profile must define either certificate or token, exactly one."
            );
        }

        if (hasCert)
        {
            if (string.IsNullOrEmpty(profile.Certificate!.Private_Key))
            {
                throw new InvalidOperationException("Private key is not configured.");
            }

            if (string.IsNullOrEmpty(profile.Certificate.Certificate))
            {
                throw new InvalidOperationException("Certificate is not configured.");
            }

            if (string.IsNullOrEmpty(profile.Certificate.Password))
            {
                throw new InvalidOperationException("Certificate password is not set.");
            }
        }
    }

    public static string Serialize(KsefCliConfig config)
    {
        ISerializer serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();
        return serializer.Serialize(config);
    }

    private static string ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("~"))
        {
            return path;
        }

        return path.Replace("~", System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
    }
}
