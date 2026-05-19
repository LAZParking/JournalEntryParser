using System;
using System.Collections.Generic;
using System.Text;

namespace JournalEntryParser.Models
{
    public class Payment
    {
        public PaymentHeader paymentHeader { get; set; }
        public List<CustomerAccount> customerAccounts { get; set; } = new List<CustomerAccount>();
    }
}
