using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JournalEntryParser.Models
{
    public class PaymentHeader
    {
        public char? identifier {  get; set; } = 'P';
        public string? accountNumber {  get; set; }
        public string? division {  get; set; }
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
        public string? currency { get; set; }
        public string? paydata1 { get; set; }
        public string? paydata2 { get; set; }
        public string? paydata3 { get; set; }
        public string? paydata4 { get; set; }
        public string? paydata5 { get; set; }
        public string? paydata6 { get; set; }
    }
}
