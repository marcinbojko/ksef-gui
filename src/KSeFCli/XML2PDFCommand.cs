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

    public static async Task<byte[]> XML2PDF(string xmlContent, bool quiet, CancellationToken cancellationToken)
    {
        AssertNpxExists();
        using TemporaryFile tempXml = new TemporaryFile(extension: ".xml");
        await File.WriteAllTextAsync(tempXml.Path, xmlContent, cancellationToken).ConfigureAwait(false);
        using TemporaryFile tempPdf = new TemporaryFile(extension: ".pdf");
        string shimPath = Path.Combine(AppContext.BaseDirectory, "navigator-shim.cjs");
        Subprocess nodeScript = new(
            CommandAndArgs: new[] { "npx", "--yes", "github:kamilcuk/ksef-pdf-generator", "invoice", tempXml.Path, tempPdf.Path, },
            Environment: new Dictionary<string, string?> { { "NODE_OPTIONS", $"--require \"{shimPath}\"" } },
            Quiet: quiet
        );
        await nodeScript.CheckCallAsync(cancellationToken).ConfigureAwait(false);
        byte[] pdfBytes = await File.ReadAllBytesAsync(tempPdf.Path, cancellationToken).ConfigureAwait(false);
        return pdfBytes;
    }

    public static void AssertNpxExists()
    {
        if (!Subprocess.CheckCommandExists("npx"))
        {
            throw new InvalidOperationException("Command `npx` not found. Please install Node.js and npm to use this functionality.");
        }
    }
}
