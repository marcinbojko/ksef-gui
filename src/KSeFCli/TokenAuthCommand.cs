using System.Diagnostics;
using System.Text.Json;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace KSeFCli;


public class TokenAuthCommand : AsyncCommand<TokenAuthCommand.Settings>
{
    private readonly IKSeFClient _ksefClient;
    private readonly ICryptographyService _cryptographyService;
    private readonly ILogger<TokenAuthCommand> _logger;

    public TokenAuthCommand(IKSeFClient ksefClient, ICryptographyService cryptographyService, ILogger<TokenAuthCommand> logger)
    {
        _ksefClient = ksefClient;
        _cryptographyService = cryptographyService;
        _logger = logger;
    }

    public class Settings : GlobalSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("1. Getting challenge");
        AuthenticationChallengeResponse challenge = await _ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        long timestampMs = challenge.Timestamp.ToUnixTimeMilliseconds();

        string ksefToken = settings.Token;
        _logger.LogInformation("1. Przygotowanie i szyfrowanie tokena");
        // Przygotuj "token|timestamp" i zaszyfruj RSA-OAEP SHA-256 zgodnie z wymaganiem API
        string tokenWithTimestamp = $"{ksefToken}|{timestampMs}";
        byte[] tokenBytes = System.Text.Encoding.UTF8.GetBytes(tokenWithTimestamp);
        byte[] encrypted = _cryptographyService.EncryptKsefTokenWithRSAUsingPublicKey(tokenBytes);
        string encryptedTokenB64 = Convert.ToBase64String(encrypted);

        _logger.LogInformation("2. Wysłanie żądania uwierzytelnienia tokenem KSeF");
        Trace.Assert(!string.IsNullOrEmpty(settings.Nip), "--nip jest empty");
        AuthenticationKsefTokenRequest request = new AuthenticationKsefTokenRequest
        {
            Challenge = challenge.Challenge,
            ContextIdentifier = new AuthenticationTokenContextIdentifier
            {
                Type = AuthenticationTokenContextIdentifierType.Nip,
                Value = settings.Nip
            },
            EncryptedToken = encryptedTokenB64,
            AuthorizationPolicy = null
        };

        SignatureResponse signature = await _ksefClient.SubmitKsefTokenAuthRequestAsync(request, new CancellationToken()).ConfigureAwait(false);

        _logger.LogInformation("3. Sprawdzenie statusu uwierzytelniania");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do
        {
            status = await _ksefClient.GetAuthStatusAsync(signature.ReferenceNumber, signature.AuthenticationToken.Token).ConfigureAwait(false);
            _logger.LogInformation($"      Status: {status.Status.Code} - {status.Status.Description} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);

        if (status.Status.Code != 200)
        {
            _logger.LogError($"Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania. Kod: {status.Status.Code}, Opis: {status.Status.Description}");
            return 1;
        }

        _logger.LogInformation("4. Uzyskanie tokena dostępowego (accessToken)");
        AuthenticationOperationStatusResponse tokenResponse = await _ksefClient.GetAccessTokenAsync(signature.AuthenticationToken.Token).ConfigureAwait(false);

        Console.Out.WriteLine(JsonSerializer.Serialize(tokenResponse));
        _logger.LogInformation("Zakończono pomyślnie.");
        return 0;
    }
}
