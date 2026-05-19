using JournalEntryParser.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace JournalEntryParser.Services
{
    public class ZuoraPaymentService
    {
        private readonly ZuoraTokenService _tokenService;
        private readonly string _baseUrl;
        private readonly ILogger<ZuoraPaymentService> _logger;

        public ZuoraPaymentService(ZuoraTokenService tokenService, IConfiguration config, ILogger<ZuoraPaymentService> logger)
        {
            _tokenService = tokenService;
            _baseUrl = config["Zuora:BaseUrl"]!;
            _logger = logger;
        }

        public async Task<string> CreatePaymentAsync(PaymentHeader paymentHeader, CustomerAccountHeader cah)
        {
            var token = await _tokenService.GetTokenAsync();
            var (bankPaymentType, bankLast4, lockBoxId) = ParsePaymentType(cah.paymentType);

            var body = new Dictionary<string, object?>
            {
                ["accountNumber"]      = cah.accountNumber,
                ["amount"]             = cah.creditValue,
                ["currency"]           = paymentHeader.currency ?? "USD",
                ["effectiveDate"]      = cah.postingDate?.ToString("yyyy-MM-dd"),
                ["type"]               = "External",
                ["paymentMethodType"]  = "Check",
                ["referenceId"]        = cah.paymentReference,
                ["comment"]            = cah.paymentName,
                ["BankPaymentType__c"] = bankPaymentType,
                ["BankNumber__c"]      = bankLast4,
                ["LockBoxID__c"]       = lockBoxId,
                ["BLPaymentRef__c"]    = cah.allocationID?.ToString()
            };

            var client = new RestClient(_baseUrl);
            var request = new RestRequest("/v1/payments", Method.Post);
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddJsonBody(body);

            var response = await client.ExecuteAsync(request);

            _logger.LogInformation("Create payment response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Create payment failed ({response.StatusCode}): {response.Content}");

            var json = System.Text.Json.JsonDocument.Parse(response.Content!);
            if (!json.RootElement.TryGetProperty("id", out var idElement))
                throw new Exception($"Zuora response did not include a payment ID. Response: {response.Content}");

            return idElement.GetString()
                ?? throw new Exception("Zuora payment ID was null.");
        }

        public async Task ApplyPaymentAsync(string paymentId, List<Transaction> transactions)
        {
            if (transactions.Count == 0) return;

            var token = await _tokenService.GetTokenAsync();

            var effectiveDate = transactions.First().invoiceDate
                ?? throw new Exception("Transaction invoiceDate is null; cannot apply payment.");

            var invoices = transactions
                .Select(t => new { invoiceNumber = t.invoiceNumber, amount = t.amountToAllocate })
                .ToList();

            var client = new RestClient(_baseUrl);
            var request = new RestRequest($"/v1/payments/{paymentId}/apply", Method.Put);
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddJsonBody(new
            {
                effectiveDate = effectiveDate.ToString("yyyy-MM-dd"),
                invoices
            });

            var response = await client.ExecuteAsync(request);

            _logger.LogInformation("Apply payment response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Apply payment failed ({response.StatusCode}): {response.Content}");

            var applyJson = System.Text.Json.JsonDocument.Parse(response.Content!);
            if (applyJson.RootElement.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
                throw new Exception($"Apply payment returned success=false: {response.Content}");

            _logger.LogInformation("Applied {Count} invoice(s) to payment {PaymentId}", invoices.Count, paymentId);
        }

        public async Task<string> GetAccountIdAsync(string accountNumber)
        {
            var token = await _tokenService.GetTokenAsync();

            var client = new RestClient(_baseUrl);
            var request = new RestRequest($"/v1/accounts/{accountNumber}");
            request.AddHeader("Authorization", $"Bearer {token}");

            var response = await client.ExecuteAsync(request);
            _logger.LogInformation("Get account response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Get account failed ({response.StatusCode}): {response.Content}");

            var json = System.Text.Json.JsonDocument.Parse(response.Content!);
            return json.RootElement
                .GetProperty("basicInfo")
                .GetProperty("id")
                .GetString()
                ?? throw new Exception($"Account ID not found in response for {accountNumber}");
        }

        public async Task<string> FindPaymentNumberAsync(string accountId, string allocationId, string paymentReference)
        {
            var token = await _tokenService.GetTokenAsync();

            var client = new RestClient(_baseUrl);
            var request = new RestRequest("/v1/payments");
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddQueryParameter("accountId", accountId);

            var response = await client.ExecuteAsync(request);
            _logger.LogInformation("List payments response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"List payments failed ({response.StatusCode}): {response.Content}");

            var json = System.Text.Json.JsonDocument.Parse(response.Content!);
            var root = json.RootElement;

            System.Text.Json.JsonElement paymentsArray;
            if (root.TryGetProperty("data", out paymentsArray)) { }
            else if (root.TryGetProperty("payments", out paymentsArray)) { }
            else
                throw new Exception($"Unexpected List Payments response structure — no 'data' or 'payments' key found. Response: {response.Content}");

            foreach (var payment in paymentsArray.EnumerateArray())
            {
                var blRef = payment.TryGetProperty("BLPaymentRef__c", out var blProp) ? blProp.GetString() : null;
                var refId = payment.TryGetProperty("referenceId", out var refProp) ? refProp.GetString() : null;

                if (blRef == allocationId && refId == paymentReference)
                {
                    if (!payment.TryGetProperty("number", out var numProp))
                        throw new Exception($"Matched payment has no 'number' field. Payment: {payment}");

                    return numProp.GetString()
                        ?? throw new Exception("Matched payment number was null.");
                }
            }

            throw new Exception($"No payment found with BLPaymentRef__c={allocationId} and referenceId={paymentReference} for account {accountId}. Response: {response.Content}");
        }

        private static (string bankPaymentType, string bankLast4, string lockBoxId) ParsePaymentType(string? paymentType)
        {
            if (string.IsNullOrEmpty(paymentType)) return ("", "", "");
            var parts = paymentType.Split("::");
            return (
                parts.ElementAtOrDefault(0) ?? "",
                parts.ElementAtOrDefault(1) ?? "",
                parts.ElementAtOrDefault(2) ?? ""
            );
        }
    }
}
