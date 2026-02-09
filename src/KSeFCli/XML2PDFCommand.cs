using CommandLine;

namespace KSeFCli;

[Verb("XML2PDF", HelpText = "Convert KSeF XML invoice to PDF.")]
public class XML2PDFCommand : IGlobalCommand
{
    [Value(0, Required = true, HelpText = "Input XML file path.")]
    public required string InputFile { get; set; }

    [Value(1, HelpText = "Output PDF file path.")]
    public string? OutputFile { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        ConfigureLogging();

        if (!File.Exists(InputFile))
        {
            Console.Error.WriteLine($"Error: Input file not found: {InputFile}");
            return 1;
        }

        string outputPdfPath;
        if (string.IsNullOrEmpty(OutputFile))
        {
            if (!InputFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Error: Input file must have a .xml extension when no output file is specified.");
                return 1;
            }
            outputPdfPath = Path.ChangeExtension(InputFile, ".pdf");
            if (File.Exists(outputPdfPath))
            {
                Console.Error.WriteLine($"Error: Output file already exists: {outputPdfPath}");
                return 1;
            }
        }
        else
        {
            outputPdfPath = OutputFile;
        }

        string xmlContent = await File.ReadAllTextAsync(InputFile, cancellationToken).ConfigureAwait(false);
        byte[] pdfContent = await XML2PDF(xmlContent, Quiet, cancellationToken).ConfigureAwait(false);

        await File.WriteAllBytesAsync(outputPdfPath, pdfContent, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"PDF saved to: {outputPdfPath}");

        return 0;
    }

    private static string[] GetPdfCommand(string inputXml, string outputPdf)
    {
        // 1. Check for bundled SEA binary alongside ksefcli
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string bundledPath = Path.Combine(AppContext.BaseDirectory, $"ksef-pdf-generator{ext}");
        if (File.Exists(bundledPath))
            return [bundledPath, "invoice", inputXml, outputPdf];

        // 2. Check for ksef-pdf-generator in PATH
        if (Subprocess.CheckCommandExists("ksef-pdf-generator"))
            return ["ksef-pdf-generator", "invoice", inputXml, outputPdf];

        // 3. Fallback to npx
        if (!Subprocess.CheckCommandExists("npx"))
            throw new InvalidOperationException(
                "ksef-pdf-generator not found. Either place it alongside ksefcli, install Node.js (npx), or disable PDF export.");
        return ["npx", "--yes", "github:kamilcuk/ksef-pdf-generator", "invoice", inputXml, outputPdf];
    }

    public static async Task<byte[]> XML2PDF(string xmlContent, bool quiet, CancellationToken cancellationToken)
    {
        using TemporaryFile tempXml = new(extension: ".xml");
        await File.WriteAllTextAsync(tempXml.Path, xmlContent, cancellationToken).ConfigureAwait(false);
        using TemporaryFile tempPdf = new(extension: ".pdf");

        string[] cmd = GetPdfCommand(tempXml.Path, tempPdf.Path);
        bool usesNpx = cmd[0] == "npx";

        // navigator-shim only needed for npx path (SEA binary has it bundled)
        var env = usesNpx
            ? new Dictionary<string, string?> {
                { "NODE_OPTIONS", $"--require \"{Path.Combine(AppContext.BaseDirectory, "navigator-shim.cjs")}\"" }
              }
            : null;

        Subprocess proc = new(CommandAndArgs: cmd, Environment: env, Quiet: quiet);
        await proc.CheckCallAsync(cancellationToken).ConfigureAwait(false);
        return await File.ReadAllBytesAsync(tempPdf.Path, cancellationToken).ConfigureAwait(false);
    }

    public static void AssertPdfGeneratorAvailable()
    {
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string bundledPath = Path.Combine(AppContext.BaseDirectory, $"ksef-pdf-generator{ext}");
        if (File.Exists(bundledPath)) return;
        if (Subprocess.CheckCommandExists("ksef-pdf-generator")) return;
        if (Subprocess.CheckCommandExists("npx")) return;
        throw new InvalidOperationException(
            "PDF generation requires ksef-pdf-generator binary or Node.js (npx). Neither found.");
    }
}
