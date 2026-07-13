namespace BongoTex.Core.Entities;

/// <summary>Master garment style by SKU prefix (e.g. SP = Polo Shirt). One production cost applies to all SKUs with that prefix.</summary>
public class ProductStyle
{
    public string Prefix { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal ProductionCost { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
