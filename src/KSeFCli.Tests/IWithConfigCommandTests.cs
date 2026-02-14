using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace KSeFCli.Tests;

public class IWithConfigCommandTests : IDisposable
{
    private readonly string _tempConfigFile;
    private readonly string _tempCacheFile;

    public IWithConfigCommandTests()
    {
        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"test-config-{Guid.NewGuid()}.yaml");
        _tempCacheFile = Path.Combine(Path.GetTempPath(), $"test-cache-{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        try { File.Delete(_tempConfigFile); } catch { }
        try { File.Delete(_tempCacheFile); } catch { }
    }

    private class TestCommand : IWithConfigCommand
    {
        public override Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    [Fact]
    public void ConfigFile_DefaultValue_UsesEnvironmentVariableIfSet()
    {
        string testPath = "/tmp/test-config.yaml";
        Environment.SetEnvironmentVariable("KSEFCLI_CONFIG", testPath);
        try
        {
            TestCommand cmd = new TestCommand();
            Assert.Equal(testPath, cmd.ConfigFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KSEFCLI_CONFIG", null);
        }
    }

    [Fact]
    public void ConfigFile_DefaultValue_ChecksCurrentDirectory()
    {
        Environment.SetEnvironmentVariable("KSEFCLI_CONFIG", null);
        TestCommand cmd = new TestCommand();
        Assert.NotNull(cmd.ConfigFile);
        Assert.NotEmpty(cmd.ConfigFile);
    }

    [Fact]
    public void ActiveProfile_DefaultValue_UsesEnvironmentVariableIfSet()
    {
        string testProfile = "test-profile";
        Environment.SetEnvironmentVariable("KSEFCLI_ACTIVE", testProfile);
        try
        {
            TestCommand cmd = new TestCommand();
            Assert.Equal(testProfile, cmd.ActiveProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KSEFCLI_ACTIVE", null);
        }
    }

    [Fact]
    public void ActiveProfile_DefaultValue_EmptyWhenNoEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("KSEFCLI_ACTIVE", null);
        TestCommand cmd = new TestCommand();
        Assert.Equal("", cmd.ActiveProfile);
    }

    [Fact]
    public void TokenCache_DefaultValue_UsesUserCacheDirectory()
    {
        TestCommand cmd = new TestCommand();
        Assert.NotNull(cmd.TokenCache);
        Assert.Contains(".cache", cmd.TokenCache);
        Assert.Contains("ksefcli", cmd.TokenCache);
    }

    [Fact]
    public void NoTokenCache_DefaultValue_IsFalse()
    {
        TestCommand cmd = new TestCommand();
        Assert.False(cmd.NoTokenCache);
    }

    [Fact]
    public void ResetCachedConfig_ClearsCachedData()
    {
        string validConfig = @"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: demo
    token: test-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "test"
        };
        ProfileConfig config1 = cmd.Config();
        Assert.Equal("1234567890", config1.Nip);
        cmd.ResetCachedConfig();
        ProfileConfig config2 = cmd.Config();
        Assert.Equal("1234567890", config2.Nip);
    }

    [Fact]
    public void GetTokenStore_ReturnsTokenStoreInstance()
    {
        TestCommand cmd = new TestCommand { TokenCache = _tempCacheFile };
        TokenStore tokenStore = cmd.GetTokenStore();
        Assert.NotNull(tokenStore);
    }

    [Fact]
    public void Config_LoadsProfileFromConfigFile()
    {
        string validConfig = @"
active_profile: prod
profiles:
  prod:
    nip: '9876543210'
    environment: prod
    token: prod-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "prod"
        };
        ProfileConfig config = cmd.Config();
        Assert.Equal("9876543210", config.Nip);
        Assert.Equal("prod", config.Environment);
        Assert.Equal("prod-token", config.Token);
    }

    [Fact]
    public void GetTokenStoreKey_ReturnsKeyWithActiveProfile()
    {
        string validConfig = @"
active_profile: demo
profiles:
  demo:
    nip: '1111111111'
    environment: demo
    token: demo-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "demo"
        };
        TokenStore.Key key = cmd.GetTokenStoreKey();
        Assert.NotNull(key);
    }

    [Fact]
    public void Config_ThrowsWhenProfileNotFound()
    {
        string validConfig = @"
active_profile: nonexistent
profiles:
  demo:
    nip: '1111111111'
    environment: demo
    token: demo-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "nonexistent"
        };
        Assert.Throws<InvalidOperationException>(() => cmd.Config());
    }

    [Fact]
    public void Config_ThrowsWhenConfigFileNotFound()
    {
        TestCommand cmd = new TestCommand
        {
            ConfigFile = Path.Combine(Path.GetTempPath(), $"ksefcli_test_{Guid.NewGuid()}.yaml")
        };
        // ConfigLoader creates a template and throws InvalidOperationException when the file is absent.
        Assert.Throws<InvalidOperationException>(() => cmd.Config());
    }

    [Fact]
    public void GetScope_CreatesServiceScope()
    {
        string validConfig = @"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: demo
    token: test-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "test"
        };
        using IServiceScope scope = cmd.GetScope();
        Assert.NotNull(scope);
        Assert.NotNull(scope.ServiceProvider);
    }

    [Fact]
    public void GetScope_ThrowsForInvalidEnvironment()
    {
        string invalidConfig = @"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: invalid_env
    token: test-token
";
        File.WriteAllText(_tempConfigFile, invalidConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "test"
        };
        Assert.Throws<Exception>(() => cmd.GetScope());
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData("demo")]
    [InlineData("DEMO")]
    [InlineData("test")]
    [InlineData("TEST")]
    public void GetScope_AcceptsValidEnvironments(string environment)
    {
        string validConfig = $@"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: {environment}
    token: test-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "test"
        };
        using IServiceScope scope = cmd.GetScope();
        Assert.NotNull(scope);
    }

    [Fact]
    public void LogConfigSource_OutputsConfigPath()
    {
        string validConfig = @"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: demo
    token: test-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile
        };
        cmd.LogConfigSource();
    }

    [Fact]
    public void Config_SupportsCertificateAuthentication()
    {
        string certConfig = @"
active_profile: cert-profile
profiles:
  cert-profile:
    nip: '5555555555'
    environment: demo
    certificate:
      private_key: fake-private-key-content
      certificate: fake-certificate-content
      password: test-password
";
        File.WriteAllText(_tempConfigFile, certConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "cert-profile"
        };
        ProfileConfig config = cmd.Config();
        Assert.NotNull(config.Certificate);
        Assert.Equal("fake-private-key-content", config.Certificate.Private_Key);
        Assert.Equal("fake-certificate-content", config.Certificate.Certificate);
    }

    [Fact]
    public void MultipleProfiles_CanSwitchBetween()
    {
        string multiConfig = @"
active_profile: profile1
profiles:
  profile1:
    nip: '1111111111'
    environment: demo
    token: token1
  profile2:
    nip: '2222222222'
    environment: prod
    token: token2
";
        File.WriteAllText(_tempConfigFile, multiConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "profile1"
        };
        ProfileConfig config1 = cmd.Config();
        Assert.Equal("1111111111", config1.Nip);
        cmd.ActiveProfile = "profile2";
        cmd.ResetCachedConfig();
        ProfileConfig config2 = cmd.Config();
        Assert.Equal("2222222222", config2.Nip);
    }

    [Fact]
    public void TokenCache_CanBeDisabled()
    {
        TestCommand cmd = new TestCommand
        {
            NoTokenCache = true
        };
        Assert.True(cmd.NoTokenCache);
    }

    [Fact]
    public void Config_CachesLoadedData()
    {
        string validConfig = @"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: demo
    token: test-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "test"
        };
        ProfileConfig config1 = cmd.Config();
        File.WriteAllText(_tempConfigFile, @"
active_profile: test
profiles:
  test:
    nip: '9999999999'
    environment: demo
    token: test-token
");
        ProfileConfig config2 = cmd.Config();
        Assert.Equal(config1.Nip, config2.Nip);
    }

    [Fact]
    public void ResetCachedConfig_ReloadsFromDisk()
    {
        string validConfig = @"
active_profile: test
profiles:
  test:
    nip: '1234567890'
    environment: demo
    token: test-token
";
        File.WriteAllText(_tempConfigFile, validConfig);
        TestCommand cmd = new TestCommand
        {
            ConfigFile = _tempConfigFile,
            ActiveProfile = "test"
        };
        ProfileConfig config1 = cmd.Config();
        Assert.Equal("1234567890", config1.Nip);
        File.WriteAllText(_tempConfigFile, @"
active_profile: test
profiles:
  test:
    nip: '9999999999'
    environment: demo
    token: test-token
");
        cmd.ResetCachedConfig();
        ProfileConfig config2 = cmd.Config();
        Assert.Equal("9999999999", config2.Nip);
    }
}
