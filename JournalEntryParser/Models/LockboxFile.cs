using System;
using System.Collections.Generic;
using System.Text;

namespace JournalEntryParser.Models
{
    public class LockboxFile
    {
        public Header header { get; set; }
        public List<Payment> payments { get; set; } = new List<Payment>();
    }
}
