using System.IO;
using System.Reflection;

using Xunit;

namespace KSeFCli.Tests;

public class PdfGenerationTests
{
    private static string LoadSampleXml()
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string path = Path.Combine(dir, "TestData", "sample-invoice.xml");
        return File.ReadAllText(path);
    }

    [Fact]
    public void GeneratePdf_DefaultScheme_ReturnsPdfBytes()
    {
        string xml = LoadSampleXml();
        byte[] pdf = KSeFInvoicePdf.FromXml(xml);
        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 1024, "PDF should be larger than 1 KB");
        Assert.Equal((byte)'%', pdf[0]);
        Assert.Equal((byte)'P', pdf[1]);
        Assert.Equal((byte)'D', pdf[2]);
        Assert.Equal((byte)'F', pdf[3]);
    }

    [Theory]
    [InlineData("navy")]
    [InlineData("forest")]
    [InlineData("slate")]
    public void GeneratePdf_NamedScheme_ReturnsPdfBytes(string scheme)
    {
        string xml = LoadSampleXml();
        byte[] pdf = KSeFInvoicePdf.FromXml(xml, scheme);
        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 1024, $"PDF with scheme '{scheme}' should be larger than 1 KB");
        Assert.Equal((byte)'%', pdf[0]);
        Assert.Equal((byte)'P', pdf[1]);
        Assert.Equal((byte)'D', pdf[2]);
        Assert.Equal((byte)'F', pdf[3]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-scheme")]
    public void GeneratePdf_FallbackScheme_ReturnsPdfBytes(string? scheme)
    {
        string xml = LoadSampleXml();
        byte[] pdf = KSeFInvoicePdf.FromXml(xml, scheme);
        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 1024);
    }
}
