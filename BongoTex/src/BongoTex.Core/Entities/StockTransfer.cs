namespace BongoTex.Core.Entities;

public class StockTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TransferNo { get; set; } = string.Empty;
    /// <summary>Shared document number when several lines are transferred together (batch).</summary>
    public string DocumentNo { get; set; } = string.Empty;
    public Guid InventoryItemId { get; set; }
    public Guid FromSiteId { get; set; }
    public Guid ToSiteId { get; set; }
    public int Quantity { get; set; }
    public DateTime TransferredAtUtc { get; set; } = DateTime.UtcNow;
}
