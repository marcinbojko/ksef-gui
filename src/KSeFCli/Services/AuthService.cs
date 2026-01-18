using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using KSeFCli.Config;
using KSeFCli.Services.Resources;
using Spectre.Console;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Models.Authorization;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Api.Builders.Auth;
using System.Security.Cryptography; // Added for CryptographicException and X509CertificateLoader

namespace KSeFCli.Services
{
    public class AuthService
    {
        private readonly IAuthorizationClient _authorizationClient;
        private readonly TokenStore _tokenStore;
        private readonly AppConfig _appConfig;

        public AuthService(IAuthorizationClient authorizationClient, TokenStore tokenStore, AppConfig appConfig)
        {
            _authorizationClient = authorizationClient;
            _tokenStore = tokenStore;
            _appConfig = appConfig;
        }

        public async Task<Token?> GenerateTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_appConfig.KsefApi.CertificatePath) || !File.Exists(_appConfig.KsefApi.CertificatePath))
            {
                AnsiConsole.MarkupLine(StringResources.CertificatePathError);
                return null;
            }

            X509Certificate2 clientCertificate;
            try
            {
                // Use X509CertificateLoader for loading certificates to avoid SYSLIB0057 warning
                var certificateLoader = new X509Certificate2Loader();
                clientCertificate = certificateLoader.LoadCertificate(_appConfig.KsefApi.CertificatePath, _appConfig.KsefApi.CertificatePassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            catch (CryptographicException ex)
            {
                AnsiConsole.MarkupLine(string.Format(StringResources.CertificateLoadError, ex.Message));
                return null;
            }
            catch (Exception ex) // Catch other potential exceptions during certificate loading
            {
                AnsiConsole.MarkupLine(string.Format(StringResources.CertificateLoadError, ex.Message));
                return null;
            }

            try
            {
                AnsiConsole.MarkupLine(StringResources.GeneratingToken);
                var challengeResponse = await _authorizationClient.GetAuthChallengeAsync(cancellationToken);

                var authTokenRequest = AuthTokenRequestBuilder
                    .Create()
                    .WithChallenge(challengeResponse.Challenge)
                    .WithContext(AuthenticationTokenContextIdentifierType.Nip, "NIP_PLACEHOLDER")
                    .WithIdentifierType(AuthenticationTokenSubjectIdentifierTypeEnum.CertificateSubject)
                    .Build();
                
                var unsignedXml = AuthenticationTokenRequestSerializer.SerializeToXmlString(authTokenRequest);
                var signedXml = SignatureService.Sign(unsignedXml, clientCertificate);
                var sessionTokenResponse = await _authorizationClient.SubmitXadesAuthRequestAsync(signedXml, false, cancellationToken);

                if (sessionTokenResponse == null || string.IsNullOrEmpty(sessionTokenResponse.AuthenticationToken?.Token))
                {
                    AnsiConsole.MarkupLine(StringResources.TokenGenerationError);
                    return null;
                }

                var token = new Token
                {
                    Value = sessionTokenResponse.AuthenticationToken.Token,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    SessionId = sessionTokenResponse.ReferenceNumber
                };

                await _tokenStore.SaveTokenAsync(token);
                AnsiConsole.MarkupLine(StringResources.TokenGenerated);
                return token;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(string.Format(StringResources.TokenGenerationError, ex.Message));
                return null;
            }
        }

        public async Task<Token?> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            var currentToken = await _tokenStore.LoadTokenAsync();
            if (currentToken == null || currentToken.IsExpired)
            {
                AnsiConsole.MarkupLine(StringResources.NoValidTokenFound);
                return await GenerateTokenAsync(cancellationToken);
            }

            if (!currentToken.IsExpired)
            {
                AnsiConsole.MarkupLine(StringResources.TokenStillValid);
                return currentToken;
            }

            AnsiConsole.MarkupLine(StringResources.RefreshingToken);
            try
            {
                var refreshTokenResponse = await _authorizationClient.RefreshAccessTokenAsync(currentToken.Value, cancellationToken);
                if (refreshTokenResponse == null || string.IsNullOrEmpty(refreshTokenResponse.AccessToken?.Token))
                {
                    AnsiConsole.MarkupLine(StringResources.TokenRefreshError);
                    return null;
                }

                var refreshedToken = new Token
                {
                    Value = refreshTokenResponse.AccessToken.Token,
                    ExpiresAt = refreshTokenResponse.AccessToken.ValidUntil,
                    SessionId = ""
                };

                await _tokenStore.SaveTokenAsync(refreshedToken);
                AnsiConsole.MarkupLine(StringResources.TokenRefreshed);
                return refreshedToken;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(string.Format(StringResources.TokenRefreshError, ex.Message));
                return null;
            }
        }
    }
}