namespace BongoTex.Core.Entities;

public class SalesCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SalesTransactionId { get; set; }
    public decimal Amount { get; set; }
    public DateTime CollectedAtUtc { get; set; } = DateTime.UtcNow;
    public string Note { get; set; } = string.Empty;
}
