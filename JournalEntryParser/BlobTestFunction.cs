using JournalEntryParser.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JournalEntryParser;

public class BlobTestFunction
{
    private readonly ILogger<BlobTestFunction> _logger;
    private readonly BlobStorageService _blobStorageService;

    public BlobTestFunction(ILogger<BlobTestFunction> logger, BlobStorageService blobStorageService)
    {
        _logger = logger;
        _blobStorageService = blobStorageService;
    }

    /// <summary>
    /// Diagnostic endpoint. Verifies DefaultAzureCredential can both upload and download
    /// against the configured container. Optionally pass ?file=&lt;name&gt; to also test reading
    /// a real blob from the input folder.
    /// </summary>
    [Function("TestBlobConnection")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        if (!_blobStorageService.IsConfigured)
            return new ObjectResult(new { ok = false, error = "BlobStorage:AccountUrl is not configured." }) { StatusCode = 500 };

        var config = new
        {
            accountUrl   = _blobStorageService.AccountUrl,
            container    = _blobStorageService.Container,
            inputFolder  = _blobStorageService.InputFolder,
            outputFolder = _blobStorageService.OutputFolder,
        };

        // Round-trip: upload a probe blob to the output folder, read it back, delete it.
        object roundTrip;
        try
        {
            var result = await _blobStorageService.TestRoundTripAsync();
            _logger.LogInformation("Blob round-trip test: matched={Matched}, blob={Blob}", result.Matched, result.BlobName);
            roundTrip = new
            {
                ok = result.Matched,
                uploadOk = true,
                downloadOk = true,
                contentMatched = result.Matched,
                blobName = result.BlobName,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blob round-trip test failed");
            roundTrip = new { ok = false, error = ex.Message, type = ex.GetType().Name };
        }

        // Optional: verify reading an actual input file the way production does.
        object? inputRead = null;
        var fileName = req.Query["file"].ToString();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            try
            {
                var content = await _blobStorageService.DownloadInputTextAsync(fileName);
                inputRead = new { ok = true, file = fileName, bytes = content.Length };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Input download test failed for {File}", fileName);
                inputRead = new { ok = false, file = fileName, error = ex.Message, type = ex.GetType().Name };
            }
        }

        return new OkObjectResult(new { config, roundTrip, inputRead });
    }
}
