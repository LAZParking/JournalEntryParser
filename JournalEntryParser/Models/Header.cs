using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JournalEntryParser.Models
{
    public class Header
    {
        public char? identifier {  get; set; } = 'H';
        public string? batchReference { get; set; }
        public int? lineCount { get; set; }
        public decimal? sumOfPayments { get; set; }
    }
}
