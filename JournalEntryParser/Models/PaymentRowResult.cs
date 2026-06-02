namespace JournalEntryParser.Models;

public class PaymentRowResult
{
    // CSV fields
    public string PaymentDate     { get; set; } = "";
    public string BankLast4       { get; set; } = "";
    public string LockBoxId       { get; set; } = "";
    public string Name            { get; set; } = "";
    public string AccountNumber   { get; set; } = "";
    public string InvoiceNumber   { get; set; } = "";
    public string AppliedAmount   { get; set; } = "";
    public string PaymentAmount   { get; set; } = "";
    public string ReferenceId     { get; set; } = "";
    public string BankPaymentType { get; set; } = "";
    public string BlPaymentRef    { get; set; } = "";
    public string Comments        { get; set; } = "";

    // Zuora result fields (new columns)
    public string? PaymentNumber  { get; set; }
    public string? PaymentUUID    { get; set; }
    public string PassFail        { get; set; } = "Fail";

    // Error log only (not written to CSV)
    public bool    Success        { get; set; }
    public string? ErrorMessage   { get; set; }
}
