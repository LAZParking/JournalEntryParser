using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;
using System.Text.Json;

namespace JournalEntryParser.Services
{
    public class BcPaymentService
    {
        private readonly BcTokenService _tokenService;
        private readonly string _baseUrl;
        private readonly string _company;
        private readonly ILogger<BcPaymentService> _logger;

        public BcPaymentService(BcTokenService tokenService, IConfiguration config, ILogger<BcPaymentService> logger)
        {
            _tokenService = tokenService;
            _logger = logger;

            var apiBase     = config["BC:BaseUrl"] ?? "https://api.businesscentral.dynamics.com/v2.0";
            var tenantId    = config["BC:TenantId"]!;
            var environment = config["BC:Environment"]!;   // LAZ_US (PROD) or LAZ_US_Support (DEV)
            _company        = config["BC:Company"] ?? "PRODUCTION";

            _baseUrl = $"{apiBase}/{tenantId}/{environment}/ODataV4";
        }

        /// <summary>
        /// Posts the raw journal-entry file contents to the BC import endpoint. The body mirrors
        /// the ADF pipeline: a JSON envelope whose inputJson field is itself a JSON string holding
        /// the PaymentType (Standard/Recycled) header and the file contents. Returns the BC response body.
        /// </summary>
        public async Task<string?> SendFileAsync(string fileContent, bool isRecycled)
        {
            var token = await _tokenService.GetTokenAsync();
            var paymentType = isRecycled ? "Recycled" : "Standard";

            var inputJson = JsonSerializer.Serialize(new
            {
                header = new { PaymentType = paymentType },
                content = fileContent
            });

            var client = new RestClient(_baseUrl);
            var request = new RestRequest("BlacklineITCJE_ImportJournal", Method.Post);
            request.AddQueryParameter("Company", _company);
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddJsonBody(new { inputJson });

            var response = await client.ExecuteAsync(request);

            _logger.LogInformation("Send file to BC response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Send file to BC failed ({response.StatusCode}): {response.Content}");

            return response.Content;
        }
    }
}
