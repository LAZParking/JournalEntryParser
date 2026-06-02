using JournalEntryParser.Models;
using System.Text;

namespace JournalEntryParser.Services;

public class ErrorLogService
{
    public string Generate(IReadOnlyList<PaymentRowResult> rows, DateTime runDateTime)
    {
        var failures = rows.Where(r => !r.Success).ToList();
        var sb = new StringBuilder();

        sb.AppendLine("========================================");
        sb.AppendLine("PAYMENT PROCESSING ERROR LOG");
        sb.AppendLine($"Run Date/Time  : {runDateTime:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Total Processed: {rows.Count}");
        sb.AppendLine($"Total Failures : {failures.Count}");
        sb.AppendLine("========================================");

        if (failures.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("All records processed successfully. No failures.");
            return sb.ToString();
        }

        for (int i = 0; i < failures.Count; i++)
        {
            var f = failures[i];
            sb.AppendLine();
            sb.AppendLine($"--- FAILURE {i + 1} of {failures.Count} ---");
            sb.AppendLine($"Account  : {f.AccountNumber}");
            sb.AppendLine($"Invoice  : {(string.IsNullOrEmpty(f.InvoiceNumber) ? "(none — allocation row)" : f.InvoiceNumber)}");
            sb.AppendLine($"Amount   : ${f.AppliedAmount}");
            sb.AppendLine($"Error    : {f.ErrorMessage}");
        }

        sb.AppendLine();
        sb.AppendLine("========================================");
        sb.AppendLine("END OF ERROR LOG");
        sb.AppendLine("========================================");

        return sb.ToString();
    }
}
