using JournalEntryParser.Models;
using System.Text;

namespace JournalEntryParser.Services
{
    public class CsvGenerator
    {
        private static readonly string[] Headers = new[]
        {
            "PaymentDate", "BankLast4", "LockBoxID", "Name", "AccountNumber",
            "InvoiceNumber", "AppliedAmount", "PaymentAmount", "ReferenceID", "BankPaymentType",
            "BlacklinePaymentReference", "Comments", "PaymentType"
        };

        public string Generate(LockboxFile lockboxFile)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Headers));

            foreach (var payment in lockboxFile.payments)
            {
                foreach (var customerAccount in payment.customerAccounts)
                {
                    var cah = customerAccount.customerAccountHeader;
                    var (bankPaymentType, bankLast4, lockBoxId) = ParsePaymentType(cah?.paymentType);
                    var paymentAmount = cah?.creditValue?.ToString("F2") ?? "";

                    foreach (var transaction in customerAccount.transactions)
                    {
                        sb.AppendLine(BuildRow(
                            paymentDate: transaction.invoiceDate?.ToString("MM/dd/yyyy") ?? "",
                            bankLast4: bankLast4,
                            lockBoxId: lockBoxId,
                            name: cah?.paymentName ?? "",
                            accountNumber: cah?.accountNumber ?? "",
                            invoiceNumber: transaction.invoiceNumber ?? "",
                            appliedAmount: transaction.amountToAllocate?.ToString("F2") ?? "",
                            paymentAmount: paymentAmount,
                            referenceId: cah?.paymentReference ?? "",
                            bankPaymentType: bankPaymentType,
                            blPaymentRef: cah?.allocationID?.ToString() ?? ""
                        ));
                    }

                    foreach (var allocation in customerAccount.allocations)
                    {
                        sb.AppendLine(BuildRow(
                            paymentDate: allocation.postingDate?.ToString("MM/dd/yyyy") ?? "",
                            bankLast4: bankLast4,
                            lockBoxId: lockBoxId,
                            name: cah?.paymentName ?? "",
                            accountNumber: cah?.accountNumber ?? "",
                            invoiceNumber: "",
                            appliedAmount: allocation.creditValue?.ToString("F2") ?? "",
                            paymentAmount: paymentAmount,
                            referenceId: cah?.paymentReference ?? "",
                            bankPaymentType: bankPaymentType,
                            blPaymentRef: cah?.allocationID?.ToString() ?? ""
                        ));
                    }
                }
            }

            return sb.ToString();
        }

        private static (string bankPaymentType, string bankLast4, string lockBoxId) ParsePaymentType(string? paymentType)
        {
            if (string.IsNullOrEmpty(paymentType))
                return ("", "", "");

            var parts = paymentType.Split("::");
            return (
                parts.ElementAtOrDefault(0) ?? "",
                parts.ElementAtOrDefault(1) ?? "",
                parts.ElementAtOrDefault(2) ?? ""
            );
        }

        private string BuildRow(string paymentDate, string bankLast4, string lockBoxId,
            string name, string accountNumber, string invoiceNumber, string appliedAmount,
            string paymentAmount, string referenceId, string bankPaymentType, string blPaymentRef)
        {
            return string.Join(",", new[]
            {
                Escape(paymentDate),
                Escape(bankLast4),
                Escape(lockBoxId),
                Escape(name),
                Escape(accountNumber),
                Escape(invoiceNumber),
                Escape(appliedAmount),
                Escape(paymentAmount),
                Escape(referenceId),
                Escape(bankPaymentType),
                Escape(blPaymentRef),
                Escape(name),         // Comments - same as Name
                "EXTERNAL"            // PaymentType - hardcoded
            });
        }

        private string Escape(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }
}
