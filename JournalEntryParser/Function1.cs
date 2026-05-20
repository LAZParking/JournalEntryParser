using JournalEntryParser.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JournalEntryParser;

public class Function1
{
    private readonly ILogger<Function1> _logger;
    private readonly LockboxFileParser _parser;
    private readonly CsvGenerator _csvGenerator;

    public Function1(ILogger<Function1> logger, LockboxFileParser parser, CsvGenerator csvGenerator)
    {
        _logger = logger;
        _parser = parser;
        _csvGenerator = csvGenerator;
    }

    [Function("ParseLockboxFile")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        string content;

        if (req.Method == HttpMethods.Get)
        {
            var baseDir = AppContext.BaseDirectory;
            var samplePath = Path.Combine(baseDir, "Lockbox Test Payment Try 4a.txt");
            if (!File.Exists(samplePath))
                samplePath = Path.Combine(baseDir, "Lockbox Test Payment Try 3a.txt");
            if (!File.Exists(samplePath))
                samplePath = Path.Combine(baseDir, "AA0A15_Zuora_Lockbox_all_data_elements.txt");
            if (!File.Exists(samplePath))
                samplePath = Path.Combine(baseDir, "AA0A13_Consolidated_Zuora.txt");
            if (!File.Exists(samplePath))
                return new NotFoundObjectResult($"No sample file found in {baseDir}");

            content = await File.ReadAllTextAsync(samplePath);
            _logger.LogInformation("Processing sample lockbox file: {File}", Path.GetFileName(samplePath));
        }
        else
        {
            using var reader = new StreamReader(req.Body);
            content = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(content))
                return new BadRequestObjectResult("Request body is empty.");

            _logger.LogInformation("Processing uploaded lockbox file.");
        }

        var lockboxFile = _parser.Parse(content);
        var csv = _csvGenerator.Generate(lockboxFile);

        var outputPath = Path.Combine(FindGitRoot(), $"zuora_payments_{DateTime.UtcNow:yyyyMMdd}.csv");
        await File.WriteAllTextAsync(outputPath, csv, Encoding.UTF8);
        _logger.LogInformation("CSV written to {Path}", outputPath);

        var bytes = Encoding.UTF8.GetBytes(csv);
        return new FileContentResult(bytes, "text/csv")
        {
            FileDownloadName = Path.GetFileName(outputPath)
        };
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
