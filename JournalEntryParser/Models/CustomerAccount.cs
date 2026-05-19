using System;
using System.Collections.Generic;
using System.Text;

namespace JournalEntryParser.Models
{
    public class CustomerAccount
    {
        public CustomerAccountHeader customerAccountHeader { get; set; }
        public List<Transaction> transactions { get; set; } = new List<Transaction>();
        public List<Allocation> allocations { get; set; } = new List<Allocation>();
    }
}
