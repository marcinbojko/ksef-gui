using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models.Sessions;
using KSeF.Client.Core.Models.Sessions.BatchSession;
using KSeF.Client.Tests.Utils;

using Microsoft.Extensions.DependencyInjection;



namespace KSeFCli;

[Verb("PrzeslijFaktury", HelpText = "Upload invoices in XML format.")]
public class PrzeslijFakturyCommand : IWithConfigCommand
{
    [Option('f', "files", Required = true, HelpText = "Paths to XML invoice files.")]
    public IEnumerable<string> Pliki { get; set; }

    public static IEnumerable<(string FileName, byte[] Content)> GetFilesWithContent(IEnumerable<string> paths)
    {
        return paths.Select(path => (
            FileName: Path.GetFileName(path),
            Content: File.ReadAllBytes(path)
        ));
    }

    private sealed record OpenBatchSessionResult(
        string ReferenceNumber,
        OpenBatchSessionResponse OpenBatchSessionResponse,
        List<BatchPartSendingInfo> EncryptedParts
    );

    private async Task<OpenBatchSessionResult> PrepareAndOpenBatchSessionAsync(
            IEnumerable<(string FileName, byte[] Content)> invoices,
            IKSeFClient ksefClient,
        ICryptographyService cryptographyService,
        string accessToken)
    {
        EncryptionData encryptionData = cryptographyService.GetEncryptionData();

        Log.LogInformation("1. Przygotowanie paczki ZIP");
        (byte[] zipBytes, FileMetadata zipMeta) =
            BatchUtils.BuildZip(invoices, cryptographyService);

        Log.LogInformation("2. Podział binarny paczki ZIP na części oraz 3. Zaszyfrowanie części paczki");
        List<BatchPartSendingInfo> encryptedParts =
            BatchUtils.EncryptAndSplit(zipBytes, encryptionData, cryptographyService);

        Log.LogInformation("4. Otwarcie sesji wsadowej");
        OpenBatchSessionRequest openBatchRequest =
            BatchUtils.BuildOpenBatchRequest(zipMeta, encryptionData, encryptedParts);

        OpenBatchSessionResponse openBatchSessionResponse =
            await BatchUtils.OpenBatchAsync(ksefClient, openBatchRequest, accessToken).ConfigureAwait(false);

        return new OpenBatchSessionResult(
            openBatchSessionResponse.ReferenceNumber,
            openBatchSessionResponse,
            encryptedParts
        );
    }

    private static async Task PobranieInformacjiNaTematPrzeslanychFaktur(
            IKSeFClient ksefClient,
            string referenceNumber,
            string accessToken,
            CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        string? continuationtoken = null;
        do
        {
            SessionInvoicesResponse sessionInvoices = await ksefClient
                                        .GetSessionInvoicesAsync(
                                        referenceNumber,
                                        accessToken,
                                        pageSize,
                                        continuationtoken,
                                        cancellationToken).ConfigureAwait(false);

            foreach (SessionInvoice sessionInvoice in sessionInvoices.Invoices)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(sessionInvoice, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }

            continuationtoken = sessionInvoices.ContinuationToken;
        }
        while (continuationtoken != null);
    }

    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        IEnumerable<(string FileName, byte[] Content)> invoices = GetFilesWithContent(Pliki);

        string accessToken = await GetAccessToken(scope, cancellationToken).ConfigureAwait(false);
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptographyService = await GetCryptographicService(scope, cancellationToken).ConfigureAwait(false);

        OpenBatchSessionResult result = await PrepareAndOpenBatchSessionAsync(invoices, ksefClient, cryptographyService, accessToken).ConfigureAwait(false);
        string referenceNumber = result.ReferenceNumber;
        Log.LogInformation($"ReferenceNumber={result.ReferenceNumber}");

        Log.LogInformation("5. Przesłanie zadeklarowanych części paczki");
        await ksefClient.SendBatchPartsAsync(result.OpenBatchSessionResponse, result.EncryptedParts).ConfigureAwait(false);

        Log.LogInformation("6. Zamknięcie sesji wsadowej");
        await ksefClient.CloseBatchSessionAsync(result.ReferenceNumber, accessToken).ConfigureAwait(false);

        /* ---------------------------------------------------------------------- */
        Log.LogInformation("sesja-sprawdzenie-stanu-i-pobranie-upo.md");

        Log.LogInformation("4) Oczekiwanie na przetworzenie faktury");
        SessionStatusResponse sessionStatus = await AsyncPollingUtils.PollWithBackoffAsync(
            action: () => ksefClient.GetSessionStatusAsync(referenceNumber, accessToken, cancellationToken),
            result => result is not null && result.SuccessfulInvoiceCount is not null,
            initialDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(5),
            maxAttempts: 30,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // logger.LogInformation("4) Oczekiwanie na trwały zapis faktury w repozytorium KSeF");
        // SessionInvoicesResponse sessionInvoices = await AsyncPollingUtils.PollWithBackoffAsync(
        // action: () => ksefClient.GetSessionInvoicesAsync(
        //     referenceNumber,
        //     accessToken,
        //     cancellationToken: cancellationToken),
        // result => result is not null && result.Invoices.First().PermanentStorageDate is not null,
        // initialDelay: TimeSpan.FromSeconds(1),
        // maxDelay: TimeSpan.FromSeconds(5),
        // maxAttempts: 30,
        // cancellationToken: cancellationToken).ConfigureAwait(false);

        Log.LogInformation("3. Pobranie informacji na temat przesłanych faktur");
        await PobranieInformacjiNaTematPrzeslanychFaktur(ksefClient, referenceNumber, accessToken, cancellationToken).ConfigureAwait(false);

        return 0;
    }
}
