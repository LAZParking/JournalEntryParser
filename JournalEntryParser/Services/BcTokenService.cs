using Microsoft.Extensions.Configuration;
using RestSharp;

namespace JournalEntryParser.Services
{
    public class BcTokenService
    {
        private readonly string _tokenUrl;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _scope;

        private string? _cachedToken;
        private DateTime _tokenExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public BcTokenService(IConfiguration config)
        {
            var tenantId = config["BC:TenantId"]!;
            _tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            _clientId = config["BC:ClientId"]!;
            _clientSecret = config["BC:ClientSecret"]!;
            _scope = config["BC:Scope"] ?? "https://api.businesscentral.dynamics.com/.default";
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

                var client = new RestClient(_tokenUrl);
                var request = new RestRequest();
                request.AddParameter("grant_type", "client_credentials");
                request.AddParameter("client_id", _clientId);
                request.AddParameter("client_secret", _clientSecret);
                request.AddParameter("scope", _scope);

                var response = await client.PostAsync<TokenResponse>(request)
                    ?? throw new Exception("Empty response from BC token endpoint.");

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
