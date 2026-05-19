using System;
using System.Collections.Generic;
using System.Text;

namespace JournalEntryParser.Models
{
    public class Allocation
    {
        public char? identifier { get; set; } = 'A';
        public string? postingType { get; set; }
        public string? accountNumber { get; set; }
        public string? division { get; set; }
        public string? paymentComments { get; set; }
        public decimal? debitValue { get; set; }
        public decimal? creditValue { get; set; }
        public string? paymentType { get; set; }
        public DateTime? postingDate { get; set; }
        public string? GLCode { get; set; }
        public int? paymentID { get; set; }
        public int? allocationID { get; set; }
        public string? paymentName { get; set; }
        public string? paymentReference { get; set; }
        public string? custdata1 { get; set; }
        public string? custdata2 { get; set; }
        public string? custdata3 { get; set; }
        public string? custdata4 { get; set; }
        public string? custdata5 { get; set; }
    }
}
