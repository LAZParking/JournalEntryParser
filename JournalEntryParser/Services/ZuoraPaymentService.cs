using JournalEntryParser.Models;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace JournalEntryParser.Services
{
    public class ZuoraPaymentService
    {
        private readonly ZuoraTokenService _tokenService;
        private readonly RestClient _client;
        private readonly ILogger<ZuoraPaymentService> _logger;

        public ZuoraPaymentService(ZuoraTokenService tokenService, RestClient client, ILogger<ZuoraPaymentService> logger)
        {
            _tokenService = tokenService;
            _client = client;
            _logger = logger;
        }

        public async Task<(string Id, string Number)> CreatePaymentAsync(PaymentHeader paymentHeader, CustomerAccountHeader cah)
        {
            var token = await _tokenService.GetTokenAsync();
            var (bankPaymentType, bankLast4, lockBoxId) = ParsePaymentType(cah.paymentType);

            var body = new Dictionary<string, object?>
            {
                ["accountNumber"]      = cah.accountNumber,
                ["amount"]             = cah.creditValue,
                ["currency"]           = "USD",
                ["effectiveDate"]      = cah.postingDate?.ToString("yyyy-MM-dd"),
                ["type"]               = "External",
                ["paymentMethodType"]  = "Check",
                ["referenceId"]        = cah.paymentReference,
                ["comment"]            = cah.paymentName,
                ["BankPaymentType__c"] = bankPaymentType,
                ["BankLast4__c"]      = bankLast4,
                ["LockBoxID__c"]       = lockBoxId,
                ["BLPaymentRef__c"]    = cah.allocationID?.ToString()
            };

            var request = new RestRequest("/v1/payments", Method.Post);
            request.AddHeader("Authorization", $"Bearer {token}");
            // Deterministic idempotency key from the payment's natural key: a retry — or a
            // re-run of the whole file after a partial failure — reuses the same key, so Zuora
            // returns the original payment instead of creating a duplicate.
            request.AddHeader("Idempotency-Key", BuildCreateIdempotencyKey(cah));
            request.AddJsonBody(body);

            var response = await ExecuteWithLockRetryAsync(request, "Create payment");

            _logger.LogInformation("Create payment response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Create payment failed ({response.StatusCode}): {response.Content}");

            var json = System.Text.Json.JsonDocument.Parse(response.Content!);

            if (!json.RootElement.TryGetProperty("id", out var idElement))
                throw new Exception($"Zuora response did not include a payment ID. Response: {response.Content}");

            var id = idElement.GetString()
                ?? throw new Exception("Zuora payment ID was null.");

            var number = json.RootElement.TryGetProperty("number", out var numElement)
                ? numElement.GetString() ?? ""
                : "";

            return (id, number);
        }

        public async Task ApplyPaymentAsync(string paymentId, DateTime? postingDate, List<Transaction> transactions)
        {
            if (transactions.Count == 0) return;

            var token = await _tokenService.GetTokenAsync();

            var postingEffectiveDate = postingDate
                ?? throw new Exception("CustomerAccountHeader postingDate is null; cannot apply payment.");

            // Zuora rejects applying a payment whose effective date is earlier than an invoice
            // it's applied to. The T-line invoice date (4th column) can be later than the
            // C-record posting date, so when any transaction's invoice date is ahead, apply
            // using the latest invoice date across this account's transactions.
            var maxInvoiceDate = transactions
                .Where(t => t.invoiceDate.HasValue)
                .Select(t => t.invoiceDate!.Value)
                .DefaultIfEmpty(postingEffectiveDate)
                .Max();

            var effectiveDate = maxInvoiceDate > postingEffectiveDate ? maxInvoiceDate : postingEffectiveDate;

            if (effectiveDate != postingEffectiveDate)
                _logger.LogInformation(
                    "Apply effectiveDate bumped from posting date {PostingDate:yyyy-MM-dd} to latest invoice date {InvoiceDate:yyyy-MM-dd} for payment {PaymentId}",
                    postingEffectiveDate, effectiveDate, paymentId);

            // Debit memo numbers arrive in the same T-line field as invoice numbers;
            // the documentType field is unreliable (says "INV" for debit memos), so
            // the DM number prefix is the discriminator.
            var invoices = transactions
                .Where(t => !IsDebitMemo(t.invoiceNumber))
                .Select(t => (object)new { invoiceNumber = t.invoiceNumber, amount = t.amountToAllocate })
                .ToList();

            var debitMemos = transactions
                .Where(t => IsDebitMemo(t.invoiceNumber))
                .Select(t => (object)new { debitMemoNumber = t.invoiceNumber, amount = t.amountToAllocate })
                .ToList();

            var body = new Dictionary<string, object?>
            {
                ["effectiveDate"] = effectiveDate.ToString("yyyy-MM-dd")
            };
            if (invoices.Count > 0) body["invoices"] = invoices;
            if (debitMemos.Count > 0) body["debitMemos"] = debitMemos;

            var request = new RestRequest($"/v1/payments/{paymentId}/apply", Method.Put);
            request.AddHeader("Authorization", $"Bearer {token}");
            // No Idempotency-Key here: Zuora only honors it on POST/PATCH and documents that PUT
            // (intrinsically idempotent) must not send one. Re-applying the same payment is safe.
            request.AddJsonBody(body);

            var response = await ExecuteWithLockRetryAsync(request, "Apply payment");

            _logger.LogInformation("Apply payment response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Apply payment failed ({response.StatusCode}): {response.Content}");

            var applyJson = System.Text.Json.JsonDocument.Parse(response.Content!);
            if (applyJson.RootElement.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
                throw new Exception($"Apply payment returned success=false: {response.Content}");

            _logger.LogInformation("Applied {InvoiceCount} invoice(s) and {DebitMemoCount} debit memo(s) to payment {PaymentId}",
                invoices.Count, debitMemos.Count, paymentId);
        }

        private static bool IsDebitMemo(string? documentNumber) =>
            documentNumber?.StartsWith("DM", StringComparison.OrdinalIgnoreCase) == true;

        public async Task<string> GetAccountIdAsync(string accountNumber)
        {
            var token = await _tokenService.GetTokenAsync();

            var request = new RestRequest($"/v1/accounts/{accountNumber}");
            request.AddHeader("Authorization", $"Bearer {token}");

            var response = await _client.ExecuteAsync(request);
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

            var request = new RestRequest("/v1/payments");
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddQueryParameter("accountId", accountId);

            var response = await _client.ExecuteAsync(request);
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
                //status = processed

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

        // Max total attempts for an operation that hits Zuora "locking contention" (category 50).
        // Per-account serialization upstream stops us from racing ourselves, but a UI user or a
        // batch job can still touch the same account mid-run. The resilient HttpClient handler does
        // NOT cover code 50 (Zuora returns it in the body, not as a retriable 429/5xx), so we retry
        // it explicitly here with the limited exponential backoff Zuora recommends.
        private const int LockRetryMaxAttempts = 5;
        private static readonly TimeSpan LockRetryBaseDelay = TimeSpan.FromSeconds(1);

        private async Task<RestResponse> ExecuteWithLockRetryAsync(RestRequest request, string operation)
        {
            for (var attempt = 1; ; attempt++)
            {
                var response = await _client.ExecuteAsync(request);

                if (attempt >= LockRetryMaxAttempts || !IsLockContention(response))
                    return response;

                // Exponential backoff (1s, 2s, 4s, 8s) + jitter so the parallel account workers
                // don't resynchronize their retries onto the same instant.
                var delay = LockRetryBaseDelay * Math.Pow(2, attempt - 1)
                            + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));

                _logger.LogWarning(
                    "{Operation} hit Zuora lock contention (attempt {Attempt}/{Max}); retrying in {DelayMs:n0}ms. Response: {Body}",
                    operation, attempt, LockRetryMaxAttempts, delay.TotalMilliseconds, response.Content);

                await Task.Delay(delay);
            }
        }

        // Detects Zuora "locking contention": the objects being modified are held by a competing
        // API call, UI op, or batch job. Error codes are 8 digits — a 6-digit resource code plus a
        // 2-digit category — and category 50 is locking contention, so the code ends in "50".
        // Matching the structured reasons[].code (not raw text) avoids false-matching our own
        // "LockBoxID__c" field echoed back in the response body.
        private static bool IsLockContention(RestResponse response)
        {
            if (string.IsNullOrEmpty(response.Content)) return false;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response.Content);
                if (!doc.RootElement.TryGetProperty("reasons", out var reasons)
                    || reasons.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return false;

                foreach (var reason in reasons.EnumerateArray())
                {
                    if (!reason.TryGetProperty("code", out var codeProp)) continue;

                    // code is numeric in REST responses, but tolerate a string form just in case.
                    var code = codeProp.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? codeProp.GetInt64().ToString()
                        : codeProp.GetString();

                    if (code is { Length: >= 2 } && code.EndsWith("50"))
                        return true;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Non-JSON body (e.g. a gateway HTML error page) — not a recognizable lock error.
            }

            return false;
        }

        // Idempotency key for creating a payment, derived from the payment's natural key so the
        // same logical payment always maps to the same key across retries and file re-runs.
        private static string BuildCreateIdempotencyKey(CustomerAccountHeader cah) =>
            DeterministicUuid($"create|{cah.accountNumber}|{cah.allocationID}|{cah.paymentReference}|{cah.creditValue}");

        // Hashes an arbitrary string into a stable UUID-v4-formatted string (Zuora requires v4 form).
        private static string DeterministicUuid(string input)
        {
            var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x40); // version 4
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
            return new Guid(hash).ToString();
        }
    }
}
