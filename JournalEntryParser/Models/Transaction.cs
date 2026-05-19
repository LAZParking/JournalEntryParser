using System;
using System.Collections.Generic;
using System.Text;

namespace JournalEntryParser.Models
{
    public class Transaction
    {
        public char identifier { get; set; } = 'T';
        public string? invoiceNumber { get; set; }
        public string? documentNumber { get; set; }
        public DateTime? invoiceDate { get; set; }
        public decimal? amountToAllocate { get; set; }
        public decimal? discountAmountTaken { get; set; }
        public string? allocationComments { get; set; }
        public string? documentType { get; set; }
        public string? invData1 { get; set; }
        public string? invData2 { get; set; }
        public string? invData3 { get; set; }
        public string? invData4 { get; set; }
        public string? invData5 { get; set; }
        public string? invData6 { get; set; }
        public string? invData7 { get; set; }
        public string? invData8 { get; set; }
        public string? remData1 { get; set; }
        public string? remData2 { get; set; }
        public string? remData3 { get; set; }
        public string? remData4 { get; set; }
        public string? remData5 { get; set; }
        public string? remData6 { get; set; }
        public string? remData7 { get; set; }
        public string? remData8 { get; set; }
    }
}
