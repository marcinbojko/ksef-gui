using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using KSeF.Client.Api.Builders.Auth;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Spectre.Console.Cli;

namespace KSeFCli;

public class CertAuthCommand : AsyncCommand<CertAuthCommand.Settings> {
    private static void PrintXmlToConsole(string xml, string title) {
        Console.WriteLine($"----- {title} -----");
        Console.WriteLine(xml);
        Console.WriteLine($"----- KONIEC: {title} -----\n");
    }

    public class Settings : GlobalSettings {
        [CommandOption("--certificate-path")]
        [Description("Path to the certificate file (.pfx)")]
        public string CertificatePath { get; set; } = null!;

        [CommandOption("--certificate-password")]
        [Description("Password for the certificate file")]
        public string? CertificatePassword { get; set; }

        [CommandOption("--subject-identifier-type")]
        [Description("Type of subject identifier (e.g., CertificateSubject, CertificateFingerprint)")]
        public AuthenticationTokenSubjectIdentifierTypeEnum SubjectIdentifierType { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings) {

        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword);

        IKSeFClient ksefClient = KSeFClientFactory.CreateKSeFClient(settings, false);

        // 1. Get Auth Challenge
        Console.WriteLine("[2] Pobieranie wyzwania (challenge) z KSeF...");
        AuthenticationChallengeResponse challengeResponse = await ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        Console.WriteLine($"    Challenge: {challengeResponse.Challenge}");

        // 2. Prepare and Sign AuthTokenRequest
        Console.WriteLine("[3] Budowanie AuthTokenRequest (builder)...");
        AuthenticationTokenRequest authTokenRequest = AuthTokenRequestBuilder
            .Create()
            .WithChallenge(challengeResponse.Challenge)
            .WithContext(AuthenticationTokenContextIdentifierType.Nip, settings.Nip)
            .WithIdentifierType(settings.SubjectIdentifierType)
            .Build();

        // 4) Serializacja do XML
        Console.WriteLine("[4] Serializacja żądania do XML (unsigned)...");
        string unsignedXml = AuthenticationTokenRequestSerializer.SerializeToXmlString(authTokenRequest);
        PrintXmlToConsole(unsignedXml, "XML przed podpisem");

        Console.WriteLine("[6] Podpisywanie XML (XAdES)...");
        string signedXml = SignatureService.Sign(unsignedXml, certificate);
        PrintXmlToConsole(signedXml, "XML po podpisie (XAdES)");

        // 7) Przesłanie podpisanego XML do KSeF
        Console.WriteLine("[7] Wysyłanie podpisanego XML do KSeF...");
        SignatureResponse submission = await ksefClient.SubmitXadesAuthRequestAsync(signedXml, verifyCertificateChain: false).ConfigureAwait(false);
        Console.WriteLine($"    ReferenceNumber: {submission.ReferenceNumber}");

        // 8) Odpytanie o status
        Console.WriteLine("[8] Odpytanie o status operacji uwierzytelnienia...");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do {
            status = await ksefClient.GetAuthStatusAsync(submission.ReferenceNumber, submission.AuthenticationToken.Token).ConfigureAwait(false);
            Console.WriteLine($"      Status: {status.Status.Code} - {status.Status.Description} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200) {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);

        if (status.Status.Code != 200) {
            Console.WriteLine("[!] Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania.");
            Console.WriteLine($"    Kod: {status.Status.Code}, Opis: {status.Status.Description}");
            return 1;
        }

        // 9) Pobranie access token
        Console.WriteLine("[9] Pobieranie access token...");
        AuthenticationOperationStatusResponse tokenResponse = await ksefClient.GetAccessTokenAsync(submission.AuthenticationToken.Token).ConfigureAwait(false);

        string accessToken = tokenResponse.AccessToken?.Token ?? string.Empty;
        string refreshToken = tokenResponse.RefreshToken?.Token ?? string.Empty;
        Console.WriteLine($"    AccessToken: {accessToken}");
        Console.WriteLine($"    RefreshToken: {refreshToken}");

        Console.WriteLine("Zakończono pomyślnie.");


        return 0;
    }
}
