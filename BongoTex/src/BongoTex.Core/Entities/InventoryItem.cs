namespace BongoTex.Core.Entities;

public class InventoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CuttingNumber { get; set; } = string.Empty;
    /// <summary>Full product cost (production overhead + materials) — used as COGS at sales centres.</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>Fixed factory overhead per piece (salary + rent + daily share only — no fabric/yarn/accessories).</summary>
    public decimal ProductionCost { get; set; }
    public decimal SalesPrice { get; set; }
    public decimal DiscountPrice { get; set; }
    public bool IsPrintItem { get; set; }
    public decimal PrintChargePerPiece { get; set; }
    /// <summary>When the item was first marked as a print item (UTC). Used to avoid counting older sales after a late toggle.</summary>
    public DateTime? PrintItemMarkedAtUtc { get; set; }
    public string ItemImageBase64 { get; set; } = string.Empty;
    public int QuantityOnHand { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
