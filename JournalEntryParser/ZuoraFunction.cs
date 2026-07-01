using JournalEntryParser.Models;
using JournalEntryParser.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JournalEntryParser;

public class ZuoraFunction
{
    private readonly ILogger<ZuoraFunction> _logger;
    private readonly LockboxFileParser _parser;
    private readonly ZuoraPaymentService _zuoraPaymentService;
    private readonly CsvGenerator _csvGenerator;
    private readonly ErrorLogService _errorLogService;
    private readonly BlobStorageService _blobStorageService;
    private readonly IConfiguration _config;

    public ZuoraFunction(
        ILogger<ZuoraFunction> logger,
        LockboxFileParser parser,
        ZuoraPaymentService zuoraPaymentService,
        CsvGenerator csvGenerator,
        ErrorLogService errorLogService,
        BlobStorageService blobStorageService,
        IConfiguration config)
    {
        _logger = logger;
        _parser = parser;
        _zuoraPaymentService = zuoraPaymentService;
        _csvGenerator = csvGenerator;
        _errorLogService = errorLogService;
        _blobStorageService = blobStorageService;
        _config = config;
    }

    [Function("ProcessZuoraPayments")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        var fileName = req.Query["file"].ToString();
        if (string.IsNullOrWhiteSpace(fileName))
            return new BadRequestObjectResult("Provide a ?file=<blob name> query parameter.");

        var isRecycled = string.Equals(req.Query["isRecycled"], "true", StringComparison.OrdinalIgnoreCase);

        // Optional ?local=true (defaults to false) forces disk mode even when blob
        // storage is configured: the file is read from the repo root or build output,
        // and the CSV + error log are written to the repo root.
        var useLocal = string.Equals(req.Query["local"], "true", StringComparison.OrdinalIgnoreCase);

        // Production path: read the named file from the Blob input folder, process,
        // and write the CSV + error log to the Blob output folder.
        if (_blobStorageService.IsConfigured && !useLocal)
            return await RunBlobModeAsync(fileName, isRecycled);

        // Local test mode: read the file from the repo root, falling back to the build output folder.
        var candidatePaths = new[]
        {
            Path.Combine(FindGitRoot(), fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };
        var samplePath = candidatePaths.FirstOrDefault(File.Exists);
        if (samplePath == null)
            return new NotFoundObjectResult($"File '{fileName}' was not found in {FindGitRoot()} or {AppContext.BaseDirectory}");

        var localContent = await File.ReadAllTextAsync(samplePath);
        var localRunTime = DateTime.UtcNow;
        var localRows = await ProcessContentAsync(localContent, isRecycled);
        return await WriteLocalOutputAsync(localRows, localRunTime);
    }

    private async Task<IActionResult> RunBlobModeAsync(string fileName, bool isRecycled)
    {
        _logger.LogInformation("Processing blob input file: {File} (isRecycled={IsRecycled})", fileName, isRecycled);

        var content = await _blobStorageService.DownloadInputTextAsync(fileName);

        var runTime = DateTime.UtcNow;
        var rows = await ProcessContentAsync(content, isRecycled);

        var runStamp = runTime.ToString("yyyyMMdd_HHmmss");
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        var csv = _csvGenerator.GenerateFromResults(rows);
        await _blobStorageService.UploadOutputTextAsync(csv, $"{baseName}_{runStamp}.csv");

        var errorLog = _errorLogService.Generate(rows, runTime);
        await _blobStorageService.UploadOutputTextAsync(errorLog, $"{baseName}_{runStamp}_errors.txt");

        var failures = rows.Count(r => !r.Success);
        _logger.LogInformation("Wrote output for {File}: {Rows} rows, {Failures} failures", fileName, rows.Count, failures);

        return new OkObjectResult(new
        {
            file = fileName,
            isRecycled,
            totalRows = rows.Count,
            failures,
            csvBlob = $"{baseName}_{runStamp}.csv",
            errorBlob = $"{baseName}_{runStamp}_errors.txt"
        });
    }

    private async Task<IActionResult> WriteLocalOutputAsync(List<PaymentRowResult> rows, DateTime runTime)
    {
        var runStamp = runTime.ToString("yyyyMMdd_HHmmss");
        var gitRoot = FindGitRoot();

        var csv = _csvGenerator.GenerateFromResults(rows);
        var csvPath = Path.Combine(gitRoot, $"zuora_payments_{runStamp}.csv");
        await File.WriteAllTextAsync(csvPath, csv, Encoding.UTF8);
        _logger.LogInformation("CSV written to {Path}", csvPath);

        var errorLog = _errorLogService.Generate(rows, runTime);
        var errorLogPath = Path.Combine(gitRoot, $"zuora_errors_{runStamp}.txt");
        await File.WriteAllTextAsync(errorLogPath, errorLog, Encoding.UTF8);
        _logger.LogInformation("Error log written to {Path}", errorLogPath);

        var bytes = Encoding.UTF8.GetBytes(csv);
        return new FileContentResult(bytes, "text/csv")
        {
            FileDownloadName = Path.GetFileName(csvPath)
        };
    }

    private async Task<List<PaymentRowResult>> ProcessContentAsync(string content, bool isRecycled)
    {
        var lockboxFile = _parser.Parse(content);

        // Flatten every account into an ordered work list, tagging each item with its original
        // position so results can be written back in file order regardless of completion order.
        var work = lockboxFile.payments
            .SelectMany(p => p.customerAccounts.Select(account => (payment: p, account)))
            .Select((item, index) => (item.payment, item.account, index))
            .ToList();

        // Bounded parallelism. Each account makes up to 2 sequential Zuora calls, so N concurrent
        // accounts ≈ N concurrent calls — keep N well under Zuora's 40-concurrent limit.
        var maxConcurrency = int.TryParse(_config["Zuora:MaxConcurrency"], out var c) && c > 0 ? c : 20;
        using var gate = new SemaphoreSlim(maxConcurrency);

        // Results kept per-work-item by index so the CSV stays in original file order.
        var perAccount = new List<PaymentRowResult>[work.Count];

        // Zuora locks a customer account while a payment is being created/applied, so two
        // concurrent calls against the SAME account collide with a "locking contention" error.
        // Group the work by account number and process each group's items SEQUENTIALLY, while
        // running different accounts in PARALLEL under the gate. Distinct accounts (101 and 102)
        // still run together; a repeated account (a second 101) waits its turn instead of racing
        // the first. Items with no account number can't collide, so each becomes its own group.
        var groups = work
            .GroupBy(w => w.account.customerAccountHeader?.accountNumber is { Length: > 0 } acct
                ? acct
                : $"__no_account_{w.index}")
            .ToList();

        // A file dominated by one account is the only remaining timeout risk: its items run
        // serially, so a very large group drains alone at the run's tail. Surface it for visibility.
        var fatThreshold = int.TryParse(_config["Zuora:FatAccountWarnThreshold"], out var f) && f > 0 ? f : 25;
        foreach (var g in groups)
        {
            var count = g.Count();
            if (count > fatThreshold)
                _logger.LogWarning(
                    "Account {Account} has {Count} work items in this file; these process sequentially " +
                    "to avoid Zuora lock contention and may dominate the run's tail latency.",
                    g.Key, count);
        }

        var tasks = groups.Select(async group =>
        {
            await gate.WaitAsync();
            try
            {
                // Sequential within one account so we never race ourselves into a Zuora lock.
                foreach (var item in group)
                {
                    var cah = item.account.customerAccountHeader;
                    var rows = new List<PaymentRowResult>();   // local list -> no shared-state race
                    try
                    {
                        if (isRecycled)
                            await ProcessRecycledPaymentAsync(item.account.transactions, cah, rows);
                        else
                            await ProcessNewPaymentAsync(item.payment, item.account, rows);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed processing account {Account}", cah?.accountNumber);
                        MarkAccountRowsFailed(rows, cah, ex.Message);
                    }
                    perAccount[item.index] = rows;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return perAccount.Where(r => r is not null).SelectMany(r => r).ToList();
    }

    private async Task ProcessNewPaymentAsync(Payment payment, CustomerAccount customerAccount, List<PaymentRowResult> rows)
    {
        var cah = customerAccount.customerAccountHeader;
        var (bankPaymentType, bankLast4, lockBoxId) = ParsePaymentType(cah?.paymentType);
        var paymentAmount = cah?.creditValue?.ToString("F2") ?? "";
        var blPaymentRef = cah?.allocationID?.ToString() ?? "";

        // Build rows for all transactions and allocations first (before API calls)
        var accountRows = new List<PaymentRowResult>();

        foreach (var t in customerAccount.transactions)
        {
            accountRows.Add(new PaymentRowResult
            {
                PaymentDate     = t.invoiceDate?.ToString("MM/dd/yyyy") ?? "",
                BankLast4       = bankLast4,
                LockBoxId       = lockBoxId,
                Name            = cah?.paymentName ?? "",
                AccountNumber   = cah?.accountNumber ?? "",
                InvoiceNumber   = t.invoiceNumber ?? "",
                AppliedAmount   = t.amountToAllocate?.ToString("F2") ?? "",
                PaymentAmount   = paymentAmount,
                ReferenceId     = cah?.paymentReference ?? "",
                BankPaymentType = bankPaymentType,
                BlPaymentRef    = blPaymentRef,
                Comments        = cah?.paymentName ?? "",
            });
        }

        foreach (var a in customerAccount.allocations)
        {
            accountRows.Add(new PaymentRowResult
            {
                PaymentDate     = a.postingDate?.ToString("MM/dd/yyyy") ?? "",
                BankLast4       = bankLast4,
                LockBoxId       = lockBoxId,
                Name            = cah?.paymentName ?? "",
                AccountNumber   = cah?.accountNumber ?? "",
                InvoiceNumber   = "",
                AppliedAmount   = a.creditValue?.ToString("F2") ?? "",
                PaymentAmount   = paymentAmount,
                ReferenceId     = cah?.paymentReference ?? "",
                BankPaymentType = bankPaymentType,
                BlPaymentRef    = blPaymentRef,
                Comments        = cah?.paymentName ?? "",
            });
        }

        rows.AddRange(accountRows);

        // Create payment in Zuora
        var (paymentUUID, paymentNumber) = await _zuoraPaymentService.CreatePaymentAsync(payment.paymentHeader, cah!);
        _logger.LogInformation("Created payment {Number} ({UUID}) for account {Account} — ${Amount}",
            paymentNumber, paymentUUID, cah?.accountNumber, cah?.creditValue);

        foreach (var row in accountRows)
        {
            row.PaymentUUID   = paymentUUID;
            row.PaymentNumber = paymentNumber;
        }

        // Apply transactions
        if (customerAccount.transactions.Count > 0)
            await _zuoraPaymentService.ApplyPaymentAsync(paymentUUID, cah?.postingDate, customerAccount.transactions);

        foreach (var row in accountRows)
        {
            row.Success  = true;
            row.PassFail = "Pass";
        }
    }

    private async Task ProcessRecycledPaymentAsync(List<Transaction> transactions, CustomerAccountHeader? cah, List<PaymentRowResult> rows)
    {
        if (transactions.Count == 0) return;

        var (bankPaymentType, bankLast4, lockBoxId) = ParsePaymentType(cah?.paymentType);
        var paymentAmount = cah?.creditValue?.ToString("F2") ?? "";
        var blPaymentRef = cah?.allocationID?.ToString() ?? "";

        var accountRows = transactions.Select(t => new PaymentRowResult
        {
            PaymentDate     = t.invoiceDate?.ToString("MM/dd/yyyy") ?? "",
            BankLast4       = bankLast4,
            LockBoxId       = lockBoxId,
            Name            = cah?.paymentName ?? "",
            AccountNumber   = cah?.accountNumber ?? "",
            InvoiceNumber   = t.invoiceNumber ?? "",
            AppliedAmount   = t.amountToAllocate?.ToString("F2") ?? "",
            PaymentAmount   = paymentAmount,
            ReferenceId     = cah?.paymentReference ?? "",
            BankPaymentType = bankPaymentType,
            BlPaymentRef    = blPaymentRef,
            Comments        = cah?.paymentName ?? "",
        }).ToList();

        rows.AddRange(accountRows);

        var accountId = await _zuoraPaymentService.GetAccountIdAsync(cah!.accountNumber!);
        _logger.LogInformation("Resolved account {AccountNumber} to ID {AccountId}", cah.accountNumber, accountId);

        var paymentNumber = await _zuoraPaymentService.FindPaymentNumberAsync(
            accountId,
            cah.allocationID?.ToString() ?? "",
            cah.paymentReference ?? "");
        _logger.LogInformation("Found existing payment {PaymentNumber} for account {Account}", paymentNumber, cah.accountNumber);

        foreach (var row in accountRows)
            row.PaymentNumber = paymentNumber;

        await _zuoraPaymentService.ApplyPaymentAsync(paymentNumber, cah?.postingDate, transactions);

        foreach (var row in accountRows)
        {
            row.Success  = true;
            row.PassFail = "Pass";
        }
    }

    // Called from the outer catch when an entire account's processing fails
    private static void MarkAccountRowsFailed(List<PaymentRowResult> rows, CustomerAccountHeader? cah, string errorMessage)
    {
        // Mark any rows just added for this account (those still at PassFail default "Fail" with no PaymentNumber)
        foreach (var row in rows.Where(r => r.AccountNumber == (cah?.accountNumber ?? "") && !r.Success))
        {
            row.Success      = false;
            row.PassFail     = "Fail";
            row.ErrorMessage = errorMessage;
        }
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

    private static string FindGitRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
