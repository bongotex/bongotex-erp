namespace BongoTex.Core.Entities;

/// <summary>Cutting section: fabric used and pieces cut for an inventory style; feeds sewing WIP.</summary>
public class CuttingEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CuttingNo { get; set; } = string.Empty;
    public Guid FactorySiteId { get; set; }
    /// <summary>User lot / bundle id linking cutting → sewing → finishing before SKU is known.</summary>
    public string CutLotCode { get; set; } = string.Empty;
    public Guid? InventoryItemId { get; set; }
    /// <summary>Raw material consumed (e.g. fabric) — stock issued when cutting is saved.</summary>
    public Guid? RawMaterialId { get; set; }
    /// <summary>Pieces cut for this item (same unit as sewing / finishing).</summary>
    public int QuantityCut { get; set; }
    public decimal FabricKg { get; set; }
    public decimal FabricPricePerKg { get; set; }
    /// <summary>FabricKg times FabricPricePerKg at entry time.</summary>
    public decimal FabricAmount { get; set; }
    public DateTime CutAtUtc { get; set; } = DateTime.UtcNow;
}
