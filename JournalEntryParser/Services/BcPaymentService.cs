using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;

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
        /// Posts the raw journal-entry file contents to the BC import endpoint. The body is built
        /// to be byte-for-byte identical to the ADF pipeline (and the working Postman request): a
        /// {"inputJson":"..."} envelope whose inner JSON is *single*-escaped, so every line break
        /// lands as a raw CRLF inside the content string — the exact shape BC's importer expects.
        /// A plain JsonSerializer would double-escape the envelope and never force CRLF on LF-only
        /// files, producing a different document. Returns the BC response body.
        /// </summary>
        public async Task<string?> SendFileAsync(string fileContent, bool isRecycled)
        {
            var token = await _tokenService.GetTokenAsync();
            var paymentType = isRecycled ? "Recycled" : "Standard";

            // ADF: replace(replace(content, CRLF, '\r\n'), LF, '\r\n'). Every CRLF and lone LF
            // becomes the literal two-char escape \r\n. Trim trailing newlines first so the
            // suffix below contributes exactly one trailing CRLF.
            var escapedContent = fileContent
                .TrimEnd('\r', '\n')
                .Replace("\r\n", "\\r\\n")
                .Replace("\n", "\\r\\n");

            // Hand-built to match the ADF concat exactly (keeps the PaymentType header):
            // {"inputJson":"{\r\n\"header\":{\r\n\"PaymentType\":\"<type>\"\r\n},\"content\": \"<content>\r\n\"\r\n}"}
            var body =
                "{\"inputJson\":\"{\\r\\n\\\"header\\\":{\\r\\n\\\"PaymentType\\\":\\\""
                + paymentType
                + "\\\"\\r\\n},\\\"content\\\": \\\""
                + escapedContent
                + "\\r\\n\\\"\\r\\n}\"}";

            var client = new RestClient(_baseUrl);
            var request = new RestRequest("BlacklineITCJE_ImportJournal", Method.Post);
            request.AddQueryParameter("Company", _company);
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddStringBody(body, DataFormat.Json);   // sent verbatim — no re-serialization

            var response = await client.ExecuteAsync(request);

            _logger.LogInformation("Send file to BC response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Send file to BC failed ({response.StatusCode}): {response.Content}");

            return response.Content;
        }
    }
}
