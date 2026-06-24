using Microsoft.Extensions.Configuration;
using RestSharp;

namespace JournalEntryParser.Services
{
    public class ZuoraTokenService
    {
        private readonly RestClient _client;
        private readonly string _clientId;
        private readonly string _clientSecret;

        private string? _cachedToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public ZuoraTokenService(RestClient client, IConfiguration config)
        {
            _client = client;
            _clientId = config["Zuora:ClientId"]!;
            _clientSecret = config["Zuora:ClientSecret"]!;
        }

        public async Task<string> GetTokenAsync()
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            await _lock.WaitAsync();
            try
            {
                if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
                    return _cachedToken;

                var request = new RestRequest("oauth/token", Method.Post);
                request.AddParameter("client_id", _clientId);
                request.AddParameter("client_secret", _clientSecret);
                request.AddParameter("grant_type", "client_credentials");

                var response = await _client.PostAsync<TokenResponse>(request)
                    ?? throw new Exception("Empty response from Zuora token endpoint.");

                _cachedToken = response.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(response.ExpiresIn - 60);

                return _cachedToken;
            }
            finally
            {
                _lock.Release();
            }
        }

        private class TokenResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
        }
    }
}
