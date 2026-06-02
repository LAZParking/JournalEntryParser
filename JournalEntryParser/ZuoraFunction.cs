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
    private readonly SftpService _sftpService;
    private readonly BlobStorageService _blobStorageService;
    private readonly string _sftpRemotePath;

    public ZuoraFunction(
        ILogger<ZuoraFunction> logger,
        LockboxFileParser parser,
        ZuoraPaymentService zuoraPaymentService,
        CsvGenerator csvGenerator,
        ErrorLogService errorLogService,
        SftpService sftpService,
        BlobStorageService blobStorageService,
        IConfiguration config)
    {
        _logger = logger;
        _parser = parser;
        _zuoraPaymentService = zuoraPaymentService;
        _csvGenerator = csvGenerator;
        _errorLogService = errorLogService;
        _sftpService = sftpService;
        _blobStorageService = blobStorageService;
        _sftpRemotePath = config["Sftp:RemotePath"] ?? "/outbound/lockbox/";
    }

    [Function("ProcessZuoraPayments")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        var isRecycled = string.Equals(req.Query["isRecycled"], "true", StringComparison.OrdinalIgnoreCase);

        // SFTP mode: download files from SFTP, process each, upload results to Blob Storage
        if (_sftpService.IsConfigured)
            return await RunSftpModeAsync(isRecycled);

        // Local test mode: read a single file specified via ?file= query param
        string content;

        if (req.Method == HttpMethods.Get)
        {
            var fileName = req.Query["file"].ToString();
            if (string.IsNullOrWhiteSpace(fileName))
                return new BadRequestObjectResult("Provide a ?file=filename.txt query parameter.");

            var samplePath = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(samplePath))
                return new NotFoundObjectResult($"File '{fileName}' not found in {AppContext.BaseDirectory}");

            content = await File.ReadAllTextAsync(samplePath);
            _logger.LogInformation("Processing sample file: {File}", fileName);
        }
        else
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            content = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(content))
                return new BadRequestObjectResult("Request body is empty.");
        }

        var runTime = DateTime.UtcNow;
        var rows = await ProcessContentAsync(content, isRecycled);
        return await WriteLocalOutputAsync(rows, runTime);
    }

    private async Task<IActionResult> RunSftpModeAsync(bool isRecycled)
    {
        var remoteFiles = await _sftpService.ListFilesAsync(_sftpRemotePath);
        _logger.LogInformation("SFTP mode: found {Count} file(s) at {Path}", remoteFiles.Count, _sftpRemotePath);

        var allRows = new List<PaymentRowResult>();

        foreach (var remoteFile in remoteFiles)
        {
            var tempPath = await _sftpService.DownloadToTempAsync(remoteFile);
            try
            {
                var content = await File.ReadAllTextAsync(tempPath);
                _logger.LogInformation("Processing SFTP file: {File}", Path.GetFileName(remoteFile));

                var runTime = DateTime.UtcNow;
                var rows = await ProcessContentAsync(content, isRecycled);
                allRows.AddRange(rows);

                var runStamp = runTime.ToString("yyyyMMdd_HHmmss");
                var baseName = Path.GetFileNameWithoutExtension(remoteFile);

                var csv = _csvGenerator.GenerateFromResults(rows);
                await _blobStorageService.UploadTextAsync(csv, _blobStorageService.OutputContainer, $"{baseName}_{runStamp}.csv");

                var errorLog = _errorLogService.Generate(rows, runTime);
                await _blobStorageService.UploadTextAsync(errorLog, _blobStorageService.OutputContainer, $"{baseName}_{runStamp}_errors.txt");

                await _blobStorageService.UploadFileAsync(tempPath, _blobStorageService.ArchiveContainer, $"{baseName}_{runStamp}{Path.GetExtension(remoteFile)}");

                _logger.LogInformation("Uploaded output and archived {File} to Blob Storage", Path.GetFileName(remoteFile));
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        return new OkObjectResult(new { filesProcessed = remoteFiles.Count, totalRows = allRows.Count });
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
        var rows = new List<PaymentRowResult>();

        foreach (var payment in lockboxFile.payments)
        {
            foreach (var customerAccount in payment.customerAccounts)
            {
                var cah = customerAccount.customerAccountHeader;

                try
                {
                    if (isRecycled)
                        await ProcessRecycledPaymentAsync(customerAccount.transactions, cah, rows);
                    else
                        await ProcessNewPaymentAsync(payment, customerAccount, rows);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed processing account {Account}", cah?.accountNumber);
                    MarkAccountRowsFailed(rows, cah, ex.Message);
                }
            }
        }

        return rows;
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
