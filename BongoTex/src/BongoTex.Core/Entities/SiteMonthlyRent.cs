namespace BongoTex.Core.Entities;

/// <summary>Registered monthly rent for a factory or sales centre site (used for daily P/L allocation).</summary>
public class SiteMonthlyRent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SiteId { get; set; }
    public decimal MonthlyRent { get; set; }
    public string LandlordName { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
