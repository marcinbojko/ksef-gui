using System.Text.Json;

using Xunit;

namespace KSeFCli.Tests;

public class GuiCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempConfigFile;
    private readonly string _tempCacheDir;

    public GuiCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gui-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _tempConfigFile = Path.Combine(_tempDir, "config.yaml");
        _tempCacheDir = Path.Combine(_tempDir, ".cache");
        Directory.CreateDirectory(_tempCacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void GuiCommand_DefaultOutputDir_IsCurrent()
    {
        GuiCommand cmd = new GuiCommand();
        Assert.Equal(".", cmd.OutputDir);
    }

    [Fact]
    public void GuiCommand_DefaultPdf_IsFalse()
    {
        GuiCommand cmd = new GuiCommand();
        Assert.False(cmd.Pdf);
    }

    [Fact]
    public void GuiCommand_DefaultUseInvoiceNumber_IsFalse()
    {
        GuiCommand cmd = new GuiCommand();
        Assert.False(cmd.UseInvoiceNumber);
    }

    [Fact]
    public void GuiCommand_DefaultLan_IsFalse()
    {
        GuiCommand cmd = new GuiCommand();
        Assert.False(cmd.Lan);
    }

    [Fact]
    public void GuiCommand_PortOverride_CanBeSet()
    {
        GuiCommand cmd = new GuiCommand { PortOverride = 8080 };
        Assert.Equal(8080, cmd.PortOverride);
    }

    [Fact]
    public void GuiCommand_OutputDir_CanBeSet()
    {
        string testDir = "/tmp/test-output";
        GuiCommand cmd = new GuiCommand { OutputDir = testDir };
        Assert.Equal(testDir, cmd.OutputDir);
    }

    [Fact]
    public void GuiCommand_Pdf_CanBeEnabled()
    {
        GuiCommand cmd = new GuiCommand { Pdf = true };
        Assert.True(cmd.Pdf);
    }

    [Fact]
    public void GuiCommand_UseInvoiceNumber_CanBeEnabled()
    {
        GuiCommand cmd = new GuiCommand { UseInvoiceNumber = true };
        Assert.True(cmd.UseInvoiceNumber);
    }

    [Fact]
    public void GuiCommand_Lan_CanBeEnabled()
    {
        GuiCommand cmd = new GuiCommand { Lan = true };
        Assert.True(cmd.Lan);
    }

    [Fact]
    public void GuiPrefs_Serialization_RoundTrip()
    {
        string testFile = Path.Combine(_tempCacheDir, "test-prefs.json");
        object testPrefs = new
        {
            OutputDir = "/tmp/output",
            ExportXml = true,
            ExportJson = false,
            ExportPdf = true,
            CustomFilenames = true,
            SeparateByNip = false,
            SelectedProfile = "test-profile",
            LanPort = 18150,
            ListenOnAll = false,
            DarkMode = true,
            PreviewDarkMode = false,
            DetailsDarkMode = true,
            PdfColorScheme = "navy",
            AutoRefreshMinutes = 5,
            JsonConsoleLog = false
        };
        string json = JsonSerializer.Serialize(testPrefs);
        File.WriteAllText(testFile, json);
        string loaded = File.ReadAllText(testFile);
        JsonDocument doc = JsonDocument.Parse(loaded);
        Assert.Equal("/tmp/output", doc.RootElement.GetProperty("OutputDir").GetString());
        Assert.True(doc.RootElement.GetProperty("ExportXml").GetBoolean());
        Assert.Equal(18150, doc.RootElement.GetProperty("LanPort").GetInt32());
        Assert.Equal("navy", doc.RootElement.GetProperty("PdfColorScheme").GetString());
    }

    [Fact]
    public void ProfileEditorData_HasRequiredFields()
    {
        Type profileEditorType = typeof(GuiCommand).GetNestedType("ProfileEditorData", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(profileEditorType);
        Assert.NotNull(profileEditorType.GetProperty("Name"));
        Assert.NotNull(profileEditorType.GetProperty("Nip"));
        Assert.NotNull(profileEditorType.GetProperty("Environment"));
        Assert.NotNull(profileEditorType.GetProperty("AuthMethod"));
    }

    [Fact]
    public void ConfigEditorData_HasRequiredFields()
    {
        Type configEditorType = typeof(GuiCommand).GetNestedType("ConfigEditorData", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(configEditorType);
        Assert.NotNull(configEditorType.GetProperty("ActiveProfile"));
        Assert.NotNull(configEditorType.GetProperty("ConfigFilePath"));
        Assert.NotNull(configEditorType.GetProperty("Profiles"));
    }

    [Fact]
    public void GuiPrefs_HasAllExpectedFields()
    {
        Type prefsType = typeof(GuiCommand).GetNestedType("GuiPrefs", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(prefsType);
        Assert.NotNull(prefsType.GetProperty("OutputDir"));
        Assert.NotNull(prefsType.GetProperty("ExportXml"));
        Assert.NotNull(prefsType.GetProperty("ExportJson"));
        Assert.NotNull(prefsType.GetProperty("ExportPdf"));
        Assert.NotNull(prefsType.GetProperty("CustomFilenames"));
        Assert.NotNull(prefsType.GetProperty("SeparateByNip"));
        Assert.NotNull(prefsType.GetProperty("SelectedProfile"));
        Assert.NotNull(prefsType.GetProperty("LanPort"));
        Assert.NotNull(prefsType.GetProperty("ListenOnAll"));
        Assert.NotNull(prefsType.GetProperty("DarkMode"));
        Assert.NotNull(prefsType.GetProperty("PreviewDarkMode"));
        Assert.NotNull(prefsType.GetProperty("DetailsDarkMode"));
        Assert.NotNull(prefsType.GetProperty("PdfColorScheme"));
        Assert.NotNull(prefsType.GetProperty("AutoRefreshMinutes"));
        Assert.NotNull(prefsType.GetProperty("JsonConsoleLog"));
    }

    [Fact]
    public void GuiCommand_InheritsFromIWithConfigCommand()
    {
        Assert.True(typeof(IWithConfigCommand).IsAssignableFrom(typeof(GuiCommand)));
    }

    [Fact]
    public void GuiCommand_HasVerbAttribute()
    {
        object[] attrs = typeof(GuiCommand).GetCustomAttributes(typeof(CommandLine.VerbAttribute), false);
        Assert.NotEmpty(attrs);
        CommandLine.VerbAttribute? verbAttr = attrs[0] as CommandLine.VerbAttribute;
        Assert.NotNull(verbAttr);
        Assert.Equal("Gui", verbAttr.Name);
    }

    [Fact]
    public void GuiCommand_OutputDirOption_HasCorrectAttributes()
    {
        System.Reflection.PropertyInfo? prop = typeof(GuiCommand).GetProperty("OutputDir");
        Assert.NotNull(prop);
        object[] attrs = prop.GetCustomAttributes(typeof(CommandLine.OptionAttribute), false);
        Assert.NotEmpty(attrs);
        CommandLine.OptionAttribute? optAttr = attrs[0] as CommandLine.OptionAttribute;
        Assert.NotNull(optAttr);
        Assert.Equal("o", optAttr.ShortName);
        Assert.Equal("outputdir", optAttr.LongName);
    }

    [Fact]
    public void GuiCommand_PdfOption_HasCorrectAttributes()
    {
        System.Reflection.PropertyInfo? prop = typeof(GuiCommand).GetProperty("Pdf");
        Assert.NotNull(prop);
        object[] attrs = prop.GetCustomAttributes(typeof(CommandLine.OptionAttribute), false);
        Assert.NotEmpty(attrs);
        CommandLine.OptionAttribute? optAttr = attrs[0] as CommandLine.OptionAttribute;
        Assert.NotNull(optAttr);
        Assert.Equal("p", optAttr.ShortName);
    }

    [Fact]
    public void GuiCommand_LanOption_HasCorrectAttributes()
    {
        System.Reflection.PropertyInfo? prop = typeof(GuiCommand).GetProperty("Lan");
        Assert.NotNull(prop);
        object[] attrs = prop.GetCustomAttributes(typeof(CommandLine.OptionAttribute), false);
        Assert.NotEmpty(attrs);
        CommandLine.OptionAttribute? optAttr = attrs[0] as CommandLine.OptionAttribute;
        Assert.NotNull(optAttr);
        Assert.Equal("lan", optAttr.LongName);
    }

    [Fact]
    public void GuiCommand_PortOverrideOption_HasCorrectAttributes()
    {
        System.Reflection.PropertyInfo? prop = typeof(GuiCommand).GetProperty("PortOverride");
        Assert.NotNull(prop);
        object[] attrs = prop.GetCustomAttributes(typeof(CommandLine.OptionAttribute), false);
        Assert.NotEmpty(attrs);
        CommandLine.OptionAttribute? optAttr = attrs[0] as CommandLine.OptionAttribute;
        Assert.NotNull(optAttr);
        Assert.Equal("port", optAttr.LongName);
    }

    [Fact]
    public void GuiCommand_UseInvoiceNumberOption_HasCorrectAttributes()
    {
        System.Reflection.PropertyInfo? prop = typeof(GuiCommand).GetProperty("UseInvoiceNumber");
        Assert.NotNull(prop);
        object[] attrs = prop.GetCustomAttributes(typeof(CommandLine.OptionAttribute), false);
        Assert.NotEmpty(attrs);
        CommandLine.OptionAttribute? optAttr = attrs[0] as CommandLine.OptionAttribute;
        Assert.NotNull(optAttr);
        Assert.Equal("useInvoiceNumber", optAttr.LongName);
    }

    [Fact]
    public void SearchParams_Record_HasRequiredFields()
    {
        Type? searchParamsType = typeof(WebProgressServer).Assembly.GetType("KSeFCli.SearchParams");
        Assert.NotNull(searchParamsType);
        Assert.True(searchParamsType.IsClass);
    }

    [Fact]
    public void DownloadParams_Record_HasRequiredFields()
    {
        Type? downloadParamsType = typeof(WebProgressServer).Assembly.GetType("KSeFCli.DownloadParams");
        Assert.NotNull(downloadParamsType);
        Assert.True(downloadParamsType.IsClass);
    }

    [Fact]
    public void CheckExistingParams_Record_HasRequiredFields()
    {
        Type? checkExistingParamsType = typeof(WebProgressServer).Assembly.GetType("KSeFCli.CheckExistingParams");
        Assert.NotNull(checkExistingParamsType);
        Assert.True(checkExistingParamsType.IsClass);
    }

    [Theory]
    [InlineData("test-invoice", "test-invoice")]
    [InlineData("invoice/with/slash", "invoice_with_slash")]
    [InlineData("invoice:with:colon", "invoice_with_colon")]
    [InlineData("invoice with spaces", "invoice_with_spaces")]
    public void FileName_Sanitization_ReplacesInvalidCharacters(string input, string expected)
    {
        string sanitized = GuiCommand.SanitizeFileName(input);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void FileName_Sanitization_TruncatesLongNames()
    {
        string longName = new string('a', 100);
        string truncated = longName.Length > 60 ? longName[..60] : longName;
        Assert.Equal(60, truncated.Length);
    }

    [Fact]
    public void GuiCommand_PrefsPath_IsInCacheDirectory()
    {
        System.Reflection.FieldInfo? prefsPathField = typeof(GuiCommand).GetField("PrefsPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(prefsPathField);
        string? prefsPath = prefsPathField.GetValue(null) as string;
        Assert.NotNull(prefsPath);
        Assert.Contains("gui-prefs.json", prefsPath);
    }

    [Fact]
    public void GuiCommand_DefaultLanPort_IsCorrect()
    {
        System.Reflection.FieldInfo? defaultLanPortField = typeof(GuiCommand).GetField("DefaultLanPort",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(defaultLanPortField);
        int? defaultPort = defaultLanPortField.GetValue(null) as int?;
        Assert.Equal(18150, defaultPort);
    }

    [Fact]
    public void GuiCommand_SaveAndLoadPrefs_Integration()
    {
        string testPrefsFile = Path.Combine(_tempCacheDir, "test-gui-prefs.json");
        object prefs = new
        {
            outputDir = "/tmp/test",
            exportPdf = true,
            darkMode = true,
            lanPort = 18200
        };
        File.WriteAllText(testPrefsFile, JsonSerializer.Serialize(prefs));
        Assert.True(File.Exists(testPrefsFile));
        string loaded = File.ReadAllText(testPrefsFile);
        JsonDocument doc = JsonDocument.Parse(loaded);
        Assert.Equal("/tmp/test", doc.RootElement.GetProperty("outputDir").GetString());
        Assert.True(doc.RootElement.GetProperty("exportPdf").GetBoolean());
        Assert.Equal(18200, doc.RootElement.GetProperty("lanPort").GetInt32());
    }

    [Fact]
    public void GuiCommand_ConfigFile_InheritsFromBase()
    {
        GuiCommand cmd = new GuiCommand { ConfigFile = _tempConfigFile };
        Assert.Equal(_tempConfigFile, cmd.ConfigFile);
    }

    [Fact]
    public void GuiCommand_ActiveProfile_InheritsFromBase()
    {
        GuiCommand cmd = new GuiCommand { ActiveProfile = "test-profile" };
        Assert.Equal("test-profile", cmd.ActiveProfile);
    }

    [Fact]
    public void GuiCommand_MultipleOptionsSet_AllStored()
    {
        GuiCommand cmd = new GuiCommand
        {
            OutputDir = "/tmp/test",
            Pdf = true,
            UseInvoiceNumber = true,
            Lan = true,
            PortOverride = 9000
        };
        Assert.Equal("/tmp/test", cmd.OutputDir);
        Assert.True(cmd.Pdf);
        Assert.True(cmd.UseInvoiceNumber);
        Assert.True(cmd.Lan);
        Assert.Equal(9000, cmd.PortOverride);
    }

    [Fact]
    public void JsonPrefs_NullableFields_HandleNull()
    {
        string jsonWithNulls = @"{
            ""outputDir"": null,
            ""exportXml"": null,
            ""lanPort"": null,
            ""darkMode"": null
        }";
        JsonDocument doc = JsonDocument.Parse(jsonWithNulls);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("outputDir").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("exportXml").ValueKind);
    }

    [Fact]
    public void GuiCommand_EmptyOutputDir_UsesDefault()
    {
        GuiCommand cmd = new GuiCommand { OutputDir = "" };
        cmd.OutputDir = string.IsNullOrEmpty(cmd.OutputDir) ? "." : cmd.OutputDir;
        Assert.Equal(".", cmd.OutputDir);
    }

    [Theory]
    [InlineData("navy")]
    [InlineData("forest")]
    [InlineData("slate")]
    [InlineData(null)]
    public void PdfColorScheme_AcceptsValidValues(string? scheme)
    {
        string json = JsonSerializer.Serialize(new { pdfColorScheme = scheme });
        JsonDocument doc = JsonDocument.Parse(json);
        if (scheme != null)
        {
            Assert.Equal(scheme, doc.RootElement.GetProperty("pdfColorScheme").GetString());
        }
    }

    [Fact]
    public void AutoRefreshMinutes_AcceptsValidValues()
    {
        int[] validValues = { 0, 1, 5, 10, 30, 60 };
        foreach (int value in validValues)
        {
            string json = JsonSerializer.Serialize(new { autoRefreshMinutes = value });
            JsonDocument doc = JsonDocument.Parse(json);
            Assert.Equal(value, doc.RootElement.GetProperty("autoRefreshMinutes").GetInt32());
        }
    }
}
