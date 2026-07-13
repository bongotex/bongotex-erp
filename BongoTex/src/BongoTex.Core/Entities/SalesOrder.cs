namespace BongoTex.Core.Entities;

public class SalesOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public DateTime OrderDateUtc { get; set; } = DateTime.UtcNow;
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = "Draft";
}
