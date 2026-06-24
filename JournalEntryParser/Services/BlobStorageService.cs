using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace JournalEntryParser.Services;

public class BlobStorageService
{
    private readonly string _accountUrl;
    private readonly string _container;
    private readonly string _inputFolder;
    private readonly string _outputFolder;
    private readonly string _bcInputFolder;

    public BlobStorageService(IConfiguration config)
    {
        _accountUrl    = NormalizeAccountUrl(config["BlobStorage:AccountUrl"] ?? "");
        _container     = config["BlobStorage:Container"] ?? "lockbox";
        _inputFolder   = NormalizeFolder(config["BlobStorage:InputFolder"] ?? "input");
        _outputFolder  = NormalizeFolder(config["BlobStorage:OutputFolder"] ?? "output");
        _bcInputFolder = NormalizeFolder(config["BlobStorage:BcInputFolder"] ?? "journal/bc/input");
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_accountUrl);

    public string AccountUrl    => _accountUrl;
    public string Container      => _container;
    public string InputFolder    => _inputFolder;
    public string OutputFolder   => _outputFolder;
    public string BcInputFolder  => _bcInputFolder;

    /// <summary>Reads a file from the input folder of the container.</summary>
    public async Task<string> DownloadInputTextAsync(string fileName)
    {
        var blobClient = GetContainerClient().GetBlobClient(_inputFolder + fileName);
        var response = await blobClient.DownloadContentAsync();
        return response.Value.Content.ToString();
    }

    /// <summary>Reads a file from the BC input folder of the container.</summary>
    public async Task<string> DownloadBcInputTextAsync(string fileName)
    {
        var blobClient = GetContainerClient().GetBlobClient(_bcInputFolder + fileName);
        var response = await blobClient.DownloadContentAsync();
        return response.Value.Content.ToString();
    }

    /// <summary>
    /// Diagnostic: writes a probe blob to the output folder, reads it back, and deletes it,
    /// exercising DefaultAzureCredential for both upload and download against the live container.
    /// </summary>
    public async Task<BlobRoundTripResult> TestRoundTripAsync()
    {
        var blobName = _outputFolder + $"_conntest_{Guid.NewGuid():N}.txt";
        var expected = $"blob-roundtrip-probe {Guid.NewGuid():N}";

        var blobClient = GetContainerClient().GetBlobClient(blobName);

        // Upload (container is assumed to already exist — no container-management call)
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(expected)))
            await blobClient.UploadAsync(stream, overwrite: true);

        // Download
        var response = await blobClient.DownloadContentAsync();
        var actual = response.Value.Content.ToString();

        // Clean up the probe blob; ignore failures so they don't mask the result.
        try { await blobClient.DeleteIfExistsAsync(); } catch { /* best effort */ }

        return new BlobRoundTripResult(blobName, expected, actual, expected == actual);
    }

    /// <summary>Writes text to a blob in the output folder of the container.</summary>
    public async Task UploadOutputTextAsync(string content, string fileName)
    {
        var blobClient = GetContainerClient().GetBlobClient(_outputFolder + fileName);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    private BlobContainerClient GetContainerClient()
    {
        var serviceClient = new BlobServiceClient(new Uri(_accountUrl), new DefaultAzureCredential());
        return serviceClient.GetBlobContainerClient(_container);
    }

    // Ensure a folder prefix ends with exactly one trailing slash (and no leading slash).
    private static string NormalizeFolder(string folder)
    {
        folder = folder.Trim().Trim('/');
        return folder.Length == 0 ? "" : folder + "/";
    }

    // Accept either a full endpoint URL or a bare account name. A bare name is expanded to the
    // standard commercial blob endpoint; pass a full https:// URL for sovereign/custom clouds.
    private static string NormalizeAccountUrl(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "";
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;
        return $"https://{value}.blob.core.windows.net";
    }
}

public record BlobRoundTripResult(string BlobName, string Expected, string Actual, bool Matched);
