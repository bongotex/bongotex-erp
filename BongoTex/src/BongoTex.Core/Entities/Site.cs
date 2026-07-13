namespace BongoTex.Core.Entities;

public class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Factory or SalesCenter
    public bool IsActive { get; set; } = true;
    public DateTime? ClosedAtUtc { get; set; }
}

public class CashMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MovementNo { get; set; } = string.Empty;
    public string FromPool { get; set; } = string.Empty;
    public string ToPool { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Guid? FromSiteId { get; set; }
    public Guid? ToSiteId { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime MovementDateUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
