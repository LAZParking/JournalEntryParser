using JournalEntryParser.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JournalEntryParser;

public class ZuoraFunction
{
    private readonly ILogger<ZuoraFunction> _logger;
    private readonly LockboxFileParser _parser;
    private readonly ZuoraPaymentService _zuoraPaymentService;

    public ZuoraFunction(ILogger<ZuoraFunction> logger, LockboxFileParser parser, ZuoraPaymentService zuoraPaymentService)
    {
        _logger = logger;
        _parser = parser;
        _zuoraPaymentService = zuoraPaymentService;
    }

    [Function("ProcessZuoraPayments")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        var isRecycled = string.Equals(req.Query["isRecycled"], "true", StringComparison.OrdinalIgnoreCase);

        string content;

        if (req.Method == HttpMethods.Get)
        {
            var baseDir = AppContext.BaseDirectory;
            string samplePath;

            if (isRecycled)
            {
                samplePath = Path.Combine(baseDir, "LAZZORUSD-ACHWIRES-Recycled-5-15-26-2 1.txt");
                if (!File.Exists(samplePath))
                    samplePath = Path.Combine(baseDir, "LAZZORUSD-ACHWIRES-Recycled-5-15-26-1.txt");
            }
            else
            {
                samplePath = Path.Combine(baseDir, "Lockbox Test Payment Try 4.txt");
                if (!File.Exists(samplePath))
                    samplePath = Path.Combine(baseDir, "Lockbox Test Payment Try 3a.txt");
                if (!File.Exists(samplePath))
                    samplePath = Path.Combine(baseDir, "AA0A15_Zuora_Lockbox_all_data_elements.txt");
                if (!File.Exists(samplePath))
                    samplePath = Path.Combine(baseDir, "AA0A13_Consolidated_Zuora.txt");
            }

            if (!File.Exists(samplePath))
                return new NotFoundObjectResult($"No sample file found in {baseDir}");

            content = await File.ReadAllTextAsync(samplePath);
            _logger.LogInformation("Processing sample file: {File}", Path.GetFileName(samplePath));
        }
        else
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            content = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(content))
                return new BadRequestObjectResult("Request body is empty.");
        }

        var lockboxFile = _parser.Parse(content);
        var results = new List<object>();

        foreach (var payment in lockboxFile.payments)
        {
            foreach (var customerAccount in payment.customerAccounts)
            {
                var cah = customerAccount.customerAccountHeader;

                try
                {
                    if (isRecycled)
                    {
                        await ProcessRecycledPaymentAsync(customerAccount.transactions, cah, results);
                    }
                    else
                    {
                        await ProcessNewPaymentAsync(payment, customerAccount, results);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed processing account {Account}", cah.accountNumber);
                    results.Add(new
                    {
                        accountNumber = cah.accountNumber,
                        error = ex.Message,
                        success = false
                    });
                }
            }
        }

        return new OkObjectResult(results);
    }

    private async Task ProcessNewPaymentAsync(Models.Payment payment, Models.CustomerAccount customerAccount, List<object> results)
    {
        var cah = customerAccount.customerAccountHeader;

        var paymentId = await _zuoraPaymentService.CreatePaymentAsync(payment.paymentHeader, cah);
        _logger.LogInformation("Created payment {PaymentId} for account {Account} — ${Amount}",
            paymentId, cah.accountNumber, cah.creditValue);

        if (customerAccount.transactions.Count > 0)
            await _zuoraPaymentService.ApplyPaymentAsync(paymentId, customerAccount.transactions);

        results.Add(new
        {
            accountNumber = cah.accountNumber,
            paymentId,
            amount = cah.creditValue,
            transactionsApplied = customerAccount.transactions.Count,
            success = true
        });
    }

    private async Task ProcessRecycledPaymentAsync(List<Models.Transaction> transactions, Models.CustomerAccountHeader cah, List<object> results)
    {
        if (transactions.Count == 0) return;

        var accountId = await _zuoraPaymentService.GetAccountIdAsync(cah.accountNumber!);
        _logger.LogInformation("Resolved account {AccountNumber} to ID {AccountId}", cah.accountNumber, accountId);

        var paymentNumber = await _zuoraPaymentService.FindPaymentNumberAsync(
            accountId,
            cah.allocationID?.ToString() ?? "",
            cah.paymentReference ?? ""
        );
        _logger.LogInformation("Found existing payment {PaymentNumber} for account {Account}", paymentNumber, cah.accountNumber);

        await _zuoraPaymentService.ApplyPaymentAsync(paymentNumber, transactions);

        results.Add(new
        {
            accountNumber = cah.accountNumber,
            paymentNumber,
            transactionsApplied = transactions.Count,
            success = true
        });
    }
}
