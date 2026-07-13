namespace BongoTex.Core.Entities;

/// <summary>Sewing section: pieces sewn per day per item.</summary>
public class SewingEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SewingNo { get; set; } = string.Empty;
    public Guid FactorySiteId { get; set; }
    public string CutLotCode { get; set; } = string.Empty;
    public Guid? InventoryItemId { get; set; }
    public int QuantitySewn { get; set; }
    public DateTime SewnAtUtc { get; set; } = DateTime.UtcNow;
}
