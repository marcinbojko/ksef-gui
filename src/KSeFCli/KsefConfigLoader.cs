using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KSeFCli;

public static class KsefConfigLoader
{
    public static KsefConfig Load(string configPath, string? activeProfileNameOverride)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Configuration file not found at {configPath}");
        }

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        KsefConfig config = deserializer.Deserialize<KsefConfig>(
            File.ReadAllText(configPath)
        );

        if (!string.IsNullOrWhiteSpace(activeProfileNameOverride))
        {
            config.ActiveProfile = activeProfileNameOverride;
        }

        if (string.IsNullOrWhiteSpace(config.ActiveProfile))
        {
            if (config.Profiles.Count == 1)
            {
                config.ActiveProfile = config.Profiles.Keys.First();
            }
            else
            {
                throw new InvalidOperationException("Active profile not specified in config file or via --active option.");
            }
        }

        if (!config.Profiles.TryGetValue(config.ActiveProfile, out ProfileConfig? profile))
        {
            throw new InvalidOperationException($"Active profile '{config.ActiveProfile}' not found in configuration.");
        }

        // Expand tilde in certificate paths before validation
        if (profile.Certificate != null)
        {
            profile.Certificate.Private_Key = ExpandTilde(profile.Certificate.Private_Key);
            profile.Certificate.Certificate = ExpandTilde(profile.Certificate.Certificate);
        }

        ValidateProfile(profile);

        return config;
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
            if (!File.Exists(profile.Certificate!.Private_Key))
            {
                throw new FileNotFoundException($"Private key not found: {profile.Certificate.Private_Key}");
            }

            if (!File.Exists(profile.Certificate.Certificate))
            {
                throw new FileNotFoundException($"Certificate not found: {profile.Certificate.Certificate}");
            }

            if (string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable(profile.Certificate.Password_Env)))
            {
                throw new InvalidOperationException($"Certificate password ENV not set: {profile.Certificate.Password_Env}");
            }
        }
    }

    private static string ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("~"))
        {
            return path;
        }

        return path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
