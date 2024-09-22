﻿using Nrk.FluentCore.Exceptions;
using Nrk.FluentCore.Utils;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nrk.FluentCore.Authentication;

public class YggdrasilAuthenticator
{
    private readonly HttpClient _httpClient;

    private readonly string _serverUrl;
    private readonly string _clientToken;

    public YggdrasilAuthenticator(string serverUrl, string clientToken, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpUtils.HttpClient;

        _serverUrl = serverUrl;
        _clientToken = clientToken;
    }

    public YggdrasilAuthenticator(string serverUrl, HttpClient? httpClient = null)
        : this(serverUrl, Guid.NewGuid().ToString("N"), httpClient) { }
    
    /// <summary>
    /// Login to Yggdrasil account
    /// </summary>
    /// <param name="email">Yggdrasil account email</param>
    /// <param name="password">Yggdrasil account password</param>
    /// <returns>All Minecraft accounts associated with the Yggdrasil account</returns>
    public async Task<YggdrasilAccount[]> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var request = new YggdrasilLoginRequest
        {
            ClientToken = _clientToken,
            UserName = email,
            Password = password
        };

        using var response = await _httpClient.PostAsync(
            $"{_serverUrl}/authserver/authenticate",
            new StringContent(
                JsonSerializer.Serialize(request, AuthenticationJsonSerializerContext.Default.YggdrasilLoginRequest),
                Encoding.UTF8,
                "application/json"),
            cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
    }

    /// <summary>
    /// Refresh all Minecraft accounts associated with the Yggdrasil account
    /// </summary>
    /// <param name="account">Any Yggdrasil Minecraft account to be refreshed</param>
    /// <returns>All Minecraft accounts associated with the Yggdrasil account</returns>
    public async Task<YggdrasilAccount[]> RefreshAsync(YggdrasilAccount account, CancellationToken cancellationToken = default)
    {
        var request = new YggdrasilRefreshRequest
        {
            ClientToken = _clientToken,
            AccessToken = account.AccessToken,
            RequestUser = true
        };

        using var response = await _httpClient.PostAsync(
            $"{_serverUrl}/authserver/refresh",
            new StringContent(JsonSerializer.Serialize(request, AuthenticationJsonSerializerContext.Default.YggdrasilRefreshRequest),
                Encoding.UTF8,
                "application/json"),
            cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
    }

    // Read Yggdrasil accounts from the response for both login and refresh
    private async Task<YggdrasilAccount[]> ParseResponseAsync(HttpResponseMessage responseMessage, CancellationToken cancellationToken = default)
    {
        YggdrasilResponseModel? response = null;
        try
        {
            response = await responseMessage
                .EnsureSuccessStatusCode().Content
                .ReadFromJsonAsync(AuthenticationJsonSerializerContext.Default.YggdrasilResponseModel, cancellationToken);

            if (response?.AvailableProfiles is null)
                throw new FormatException("Response does not contain any profile");
        }
        catch (Exception)
        {
            throw new YggdrasilAuthenticationException(responseMessage.Content.ReadAsString());
        }

        return response.AvailableProfiles.Select(profile =>
            {
                if (profile.Name is null || profile.Id is null || response.AccessToken is null)
                    throw new YggdrasilAuthenticationException(responseMessage.Content.ReadAsString());

                if (!Guid.TryParse(profile.Id, out var uuid))
                    throw new YggdrasilAuthenticationException("Invalid UUID");

                return new YggdrasilAccount(
                    profile.Name,
                    uuid,
                    response.AccessToken,
                    _clientToken,
                    _serverUrl
                );
            }).ToArray();
    }
}
