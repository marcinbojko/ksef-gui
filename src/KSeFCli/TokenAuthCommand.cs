using System.Diagnostics;
using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSeFCli;

[Verb("TokenAuth", HelpText = "Authenticate using a KSeF token")]
public class TokenAuthCommand : GlobalCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        if (config.AuthMethod != AuthMethod.KsefToken)
            throw new InvalidOperationException("This command requires token authentication.");

        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptographyService = scope.ServiceProvider.GetRequiredService<ICryptographyService>();
        ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("1. Getting challenge");
        AuthenticationChallengeResponse challenge = await ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        long timestampMs = challenge.Timestamp.ToUnixTimeMilliseconds();
        string ksefToken = config.Token ?? throw new InvalidOperationException("KSeF token is missing");
        logger.LogInformation("1. Przygotowanie i szyfrowanie tokena");
        // Przygotuj "token|timestamp" i zaszyfruj RSA-OAEP SHA-256 zgodnie z wymaganiem API
        string tokenWithTimestamp = $"{ksefToken}|{timestampMs}";
        byte[] tokenBytes = System.Text.Encoding.UTF8.GetBytes(tokenWithTimestamp);
        byte[] encrypted = cryptographyService.EncryptKsefTokenWithRSAUsingPublicKey(tokenBytes);
        string encryptedTokenB64 = Convert.ToBase64String(encrypted);
        logger.LogInformation("2. Wysłanie żądania uwierzytelnienia tokenem KSeF");
        Trace.Assert(!string.IsNullOrEmpty(config.Nip), "--nip jest empty");
        AuthenticationKsefTokenRequest request = new AuthenticationKsefTokenRequest
        {
            Challenge = challenge.Challenge,
            ContextIdentifier = new AuthenticationTokenContextIdentifier
            {
                Type = AuthenticationTokenContextIdentifierType.Nip,
                Value = config.Nip
            },
            EncryptedToken = encryptedTokenB64,
            AuthorizationPolicy = null
        };
        SignatureResponse signature = await ksefClient.SubmitKsefTokenAuthRequestAsync(request, new CancellationToken()).ConfigureAwait(false);
        logger.LogInformation("3. Sprawdzenie statusu uwierzytelniania");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do
        {
            status = await ksefClient.GetAuthStatusAsync(signature.ReferenceNumber, signature.AuthenticationToken.Token).ConfigureAwait(false);
            logger.LogInformation($"      Status: {status.Status.Code} - {status.Status.Description} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);
        if (status.Status.Code != 200)
        {
            logger.LogError($"Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania. Kod: {status.Status.Code}, Opis: {status.Status.Description}");
            return 1;
        }
        logger.LogInformation("4. Uzyskanie tokena dostępowego (accessToken)");
        AuthenticationOperationStatusResponse tokenResponse = await ksefClient.GetAccessTokenAsync(signature.AuthenticationToken.Token).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(tokenResponse));
        logger.LogInformation("Zakończono pomyślnie.");
        return 0;
    }
}
