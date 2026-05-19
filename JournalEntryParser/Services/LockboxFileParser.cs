using JournalEntryParser.Models;
using System.Globalization;

namespace JournalEntryParser.Services
{
    public class LockboxFileParser
    {
        public LockboxFile Parse(string content)
        {
            var file = new LockboxFile();
            Payment? currentPayment = null;
            CustomerAccount? currentCustomerAccount = null;

            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var fields = trimmed.Split('|');

                switch (fields[0])
                {
                    case "H":
                        file.header = ParseHeader(fields);
                        break;
                    case "P":
                        currentPayment = new Payment { paymentHeader = ParsePaymentHeader(fields) };
                        currentCustomerAccount = null;
                        file.payments.Add(currentPayment);
                        break;
                    case "C":
                        if (currentPayment != null)
                        {
                            currentCustomerAccount = new CustomerAccount { customerAccountHeader = ParseCustomerAccountHeader(fields) };
                            currentPayment.customerAccounts.Add(currentCustomerAccount);
                        }
                        break;
                    case "T":
                        currentCustomerAccount?.transactions.Add(ParseTransaction(fields));
                        break;
                    case "A":
                        currentCustomerAccount?.allocations.Add(ParseAllocation(fields));
                        break;
                }
            }

            return file;
        }

        private Header ParseHeader(string[] f) => new Header
        {
            batchReference = f.ElementAtOrDefault(1),
            lineCount = ParseInt(f.ElementAtOrDefault(2)),
            sumOfPayments = ParseDecimal(f.ElementAtOrDefault(3))
        };

        private PaymentHeader ParsePaymentHeader(string[] f) => new PaymentHeader
        {
            accountNumber = f.ElementAtOrDefault(1),
            division = f.ElementAtOrDefault(2),
            paymentComments = f.ElementAtOrDefault(3),
            debitValue = ParseDecimal(f.ElementAtOrDefault(4)),
            creditValue = ParseDecimal(f.ElementAtOrDefault(5)),
            paymentType = f.ElementAtOrDefault(6),
            postingDate = ParseDate(f.ElementAtOrDefault(7)),
            GLCode = f.ElementAtOrDefault(8),
            paymentID = ParseInt(f.ElementAtOrDefault(9)),
            allocationID = ParseInt(f.ElementAtOrDefault(10)),
            paymentName = f.ElementAtOrDefault(11),
            paymentReference = f.ElementAtOrDefault(12),
            currency = f.ElementAtOrDefault(13),
            paydata1 = f.ElementAtOrDefault(14),
            paydata2 = f.ElementAtOrDefault(15),
            paydata3 = f.ElementAtOrDefault(16),
            paydata4 = f.ElementAtOrDefault(17),
            paydata5 = f.ElementAtOrDefault(18),
            paydata6 = f.ElementAtOrDefault(19)
        };

        private CustomerAccountHeader ParseCustomerAccountHeader(string[] f) => new CustomerAccountHeader
        {
            accountNumber = f.ElementAtOrDefault(1),
            division = f.ElementAtOrDefault(2),
            paymentComments = f.ElementAtOrDefault(3),
            debitValue = ParseDecimal(f.ElementAtOrDefault(4)),
            creditValue = ParseDecimal(f.ElementAtOrDefault(5)),
            paymentType = f.ElementAtOrDefault(6),
            postingDate = ParseDate(f.ElementAtOrDefault(7)),
            GLCode = f.ElementAtOrDefault(8),
            paymentID = ParseInt(f.ElementAtOrDefault(9)),
            allocationID = ParseInt(f.ElementAtOrDefault(10)),
            paymentName = f.ElementAtOrDefault(11),
            paymentReference = f.ElementAtOrDefault(12),
            paydata1 = f.ElementAtOrDefault(13),
            paydata2 = f.ElementAtOrDefault(14),
            paydata3 = f.ElementAtOrDefault(15),
            paydata4 = f.ElementAtOrDefault(16),
            paydata5 = f.ElementAtOrDefault(17),
            paydata6 = f.ElementAtOrDefault(18)
        };

        private Transaction ParseTransaction(string[] f) => new Transaction
        {
            invoiceNumber = f.ElementAtOrDefault(1),
            documentNumber = f.ElementAtOrDefault(2),
            invoiceDate = ParseDate(f.ElementAtOrDefault(3)),
            amountToAllocate = ParseDecimal(f.ElementAtOrDefault(4)),
            discountAmountTaken = ParseDecimal(f.ElementAtOrDefault(5)),
            allocationComments = f.ElementAtOrDefault(6),
            documentType = f.ElementAtOrDefault(7),
            invData1 = f.ElementAtOrDefault(8),
            invData2 = f.ElementAtOrDefault(9),
            invData3 = f.ElementAtOrDefault(10),
            invData4 = f.ElementAtOrDefault(11),
            invData5 = f.ElementAtOrDefault(12),
            invData6 = f.ElementAtOrDefault(13),
            invData7 = f.ElementAtOrDefault(14),
            invData8 = f.ElementAtOrDefault(15),
            remData1 = f.ElementAtOrDefault(16),
            remData2 = f.ElementAtOrDefault(17),
            remData3 = f.ElementAtOrDefault(18),
            remData4 = f.ElementAtOrDefault(19),
            remData5 = f.ElementAtOrDefault(20),
            remData6 = f.ElementAtOrDefault(21),
            remData7 = f.ElementAtOrDefault(22),
            remData8 = f.ElementAtOrDefault(23)
        };

        private Allocation ParseAllocation(string[] f) => new Allocation
        {
            postingType = f.ElementAtOrDefault(1),
            accountNumber = f.ElementAtOrDefault(2),
            division = f.ElementAtOrDefault(3),
            paymentComments = f.ElementAtOrDefault(4),
            debitValue = ParseDecimal(f.ElementAtOrDefault(5)),
            creditValue = ParseDecimal(f.ElementAtOrDefault(6)),
            paymentType = f.ElementAtOrDefault(7),
            postingDate = ParseDate(f.ElementAtOrDefault(8)),
            GLCode = f.ElementAtOrDefault(9),
            paymentID = ParseInt(f.ElementAtOrDefault(10)),
            allocationID = ParseInt(f.ElementAtOrDefault(11)),
            paymentName = f.ElementAtOrDefault(12),
            paymentReference = f.ElementAtOrDefault(13),
            custdata1 = f.ElementAtOrDefault(14),
            custdata2 = f.ElementAtOrDefault(15),
            custdata3 = f.ElementAtOrDefault(16),
            custdata4 = f.ElementAtOrDefault(17),
            custdata5 = f.ElementAtOrDefault(18)
        };

        private decimal? ParseDecimal(string? s) =>
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

        private int? ParseInt(string? s) =>
            int.TryParse(s, out var v) ? v : null;

        private DateTime? ParseDate(string? s) =>
            DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;
    }
}
