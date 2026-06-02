using Microsoft.Extensions.Configuration;
using Renci.SshNet;

namespace JournalEntryParser.Services;

public class SftpService
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;

    public SftpService(IConfiguration config)
    {
        _host     = config["Sftp:Host"] ?? "";
        _port     = int.TryParse(config["Sftp:Port"], out var p) ? p : 22;
        _username = config["Sftp:Username"] ?? "";
        _password = config["Sftp:Password"] ?? "";
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_host);

    public async Task<string> DownloadToTempAsync(string remoteFilePath)
    {
        var fileName  = Path.GetFileName(remoteFilePath);
        var localPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");

        await Task.Run(() =>
        {
            using var client = new SftpClient(_host, _port, _username, _password);
            client.Connect();
            using var fs = File.OpenWrite(localPath);
            client.DownloadFile(remoteFilePath, fs);
            client.Disconnect();
        });

        return localPath;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string remotePath)
    {
        return await Task.Run(() =>
        {
            using var client = new SftpClient(_host, _port, _username, _password);
            client.Connect();
            var files = client.ListDirectory(remotePath)
                .Where(f => f.IsRegularFile)
                .Select(f => f.FullName)
                .ToList();
            client.Disconnect();
            return (IReadOnlyList<string>)files;
        });
    }
}
