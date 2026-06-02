using JournalEntryParser.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights()
    .AddSingleton<LockboxFileParser>()
    .AddSingleton<CsvGenerator>()
    .AddSingleton<ErrorLogService>()
    .AddSingleton<SftpService>()
    .AddSingleton<BlobStorageService>()
    .AddSingleton<ZuoraTokenService>()
    .AddSingleton<ZuoraPaymentService>();

builder.Build().Run();
