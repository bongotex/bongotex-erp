namespace BongoTex.Core.Entities;

public class ProductionOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductionNo { get; set; } = string.Empty;
    public Guid FactorySiteId { get; set; }
    public Guid InventoryItemId { get; set; }
    public int QuantityProduced { get; set; }
    public DateTime ProducedAtUtc { get; set; } = DateTime.UtcNow;
}
