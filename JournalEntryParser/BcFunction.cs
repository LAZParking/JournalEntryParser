using JournalEntryParser.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JournalEntryParser;

public class BcFunction
{
    private readonly ILogger<BcFunction> _logger;
    private readonly BcPaymentService _bcPaymentService;
    private readonly BlobStorageService _blobStorageService;

    public BcFunction(
        ILogger<BcFunction> logger,
        BcPaymentService bcPaymentService,
        BlobStorageService blobStorageService)
    {
        _logger = logger;
        _bcPaymentService = bcPaymentService;
        _blobStorageService = blobStorageService;
    }

    [Function("ProcessBCPayments")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        var fileName = req.Query["file"].ToString();
        if (string.IsNullOrWhiteSpace(fileName))
            return new BadRequestObjectResult("Provide a ?file=<blob name> query parameter.");

        var isRecycled = string.Equals(req.Query["isRecycled"], "true", StringComparison.OrdinalIgnoreCase);

        // Optional ?local=true reads the file from the repo root / build output instead of Blob storage.
        var useLocal = string.Equals(req.Query["local"], "true", StringComparison.OrdinalIgnoreCase);

        // Read the file contents exactly as-is — no parsing — and forward them to BC.
        string content;
        if (_blobStorageService.IsConfigured && !useLocal)
        {
            _logger.LogInformation("Reading BC input file from blob: {File} (isRecycled={IsRecycled})", fileName, isRecycled);
            content = await _blobStorageService.DownloadBcInputTextAsync(fileName);
        }
        else
        {
            var candidatePaths = new[]
            {
                Path.Combine(FindGitRoot(), fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };
            var samplePath = candidatePaths.FirstOrDefault(File.Exists);
            if (samplePath == null)
                return new NotFoundObjectResult($"File '{fileName}' was not found in {FindGitRoot()} or {AppContext.BaseDirectory}");

            content = await File.ReadAllTextAsync(samplePath);
        }

        var bcResponse = await _bcPaymentService.SendFileAsync(content, isRecycled);
        _logger.LogInformation("Sent {File} to BC (isRecycled={IsRecycled})", fileName, isRecycled);

        return new OkObjectResult(new
        {
            file = fileName,
            isRecycled,
            bcResponse
        });
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
