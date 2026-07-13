namespace BongoTex.Core.Entities;

/// <summary>Finishing section: pieces finished per day per item.</summary>
public class FinishingEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FinishingNo { get; set; } = string.Empty;
    public Guid FactorySiteId { get; set; }
    public string CutLotCode { get; set; } = string.Empty;
    public Guid InventoryItemId { get; set; }
    public int QuantityFinished { get; set; }
    public DateTime FinishedAtUtc { get; set; } = DateTime.UtcNow;
}
