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

    public static Task<byte[]> XML2PDF(string xmlContent, bool quiet, CancellationToken cancellationToken, string? colorScheme = null)
    {
        if (!quiet)
            Console.WriteLine("Generating PDF (native renderer)...");

        return Task.FromResult(KSeFInvoicePdf.FromXml(xmlContent, colorScheme));
    }

    public static void AssertPdfGeneratorAvailable()
    {
        // Native renderer — always available, no external dependencies required.
    }
}
