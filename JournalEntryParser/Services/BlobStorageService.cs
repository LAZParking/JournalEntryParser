using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace JournalEntryParser.Services;

public class BlobStorageService
{
    private readonly string _accountUrl;
    private readonly string _outputContainer;
    private readonly string _archiveContainer;

    public BlobStorageService(IConfiguration config)
    {
        _accountUrl       = config["BlobStorage:AccountUrl"] ?? "";
        _outputContainer  = config["BlobStorage:OutputContainer"] ?? "payment-output";
        _archiveContainer = config["BlobStorage:ArchiveContainer"] ?? "payment-archive";
    }

    public async Task UploadFileAsync(string localPath, string containerName, string blobName)
    {
        var containerClient = GetContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();
        var blobClient = containerClient.GetBlobClient(blobName);
        await blobClient.UploadAsync(localPath, overwrite: true);
    }

    public async Task UploadTextAsync(string content, string containerName, string blobName)
    {
        var containerClient = GetContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();
        var blobClient = containerClient.GetBlobClient(blobName);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true);
    }

    public string OutputContainer  => _outputContainer;
    public string ArchiveContainer => _archiveContainer;

    private BlobContainerClient GetContainerClient(string containerName)
    {
        var serviceClient = new BlobServiceClient(new Uri(_accountUrl), new DefaultAzureCredential());
        return serviceClient.GetBlobContainerClient(containerName);
    }
}
