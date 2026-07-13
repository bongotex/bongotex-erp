namespace BongoTex.Core.Entities;

public class InventoryStock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InventoryItemId { get; set; }
    public Guid SiteId { get; set; }
    public int Quantity { get; set; }
}
