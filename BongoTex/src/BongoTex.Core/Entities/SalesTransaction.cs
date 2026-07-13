namespace BongoTex.Core.Entities;

public class SalesTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SalesNo { get; set; } = string.Empty;
    /// <summary>Links multiple Sale rows into one invoice. Null / empty = standalone line (legacy).</summary>
    public string? InvoiceNo { get; set; }
    public Guid SiteId { get; set; }
    /// <summary>Null when recording daily totals without per-SKU breakdown (does not deduct stock).</summary>
    public Guid? InventoryItemId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalAmount { get; set; }
    public bool IsCredit { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal DueAmount { get; set; }
    public DateTime SoldAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Snapshot: item was marked print when this sale was recorded.</summary>
    public bool IsPrintItemAtSale { get; set; }
    /// <summary>Snapshot: print charge per piece at sale time (for print P/L internal revenue).</summary>
    public decimal PrintChargePerPieceAtSale { get; set; }
}

public class SalesReturn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ReturnNo { get; set; } = string.Empty;
    public string? InvoiceNo { get; set; }
    public Guid SiteId { get; set; }
    public Guid InventoryItemId { get; set; }
    public string CustomerType { get; set; } = "Regular";
    public string CustomerName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalAmount { get; set; }
    public string ReturnType { get; set; } = "NoInvoice";
    public string ActionType { get; set; } = "Exchange";
    public decimal RefundAmount { get; set; }
    /// <summary>Amount of this return applied against open customer due (store credit / exchange / refund).</summary>
    public decimal DueCreditApplied { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime ReturnedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Finished inventory gifted out (zero revenue) from factory or sales centre.</summary>
public class FinishedItemGiftIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GiftNo { get; set; } = string.Empty;
    public Guid SiteId { get; set; }
    public Guid InventoryItemId { get; set; }
    public int Quantity { get; set; }
    /// <summary>Snapshot of inventory item cost (UnitPrice) at issue time.</summary>
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public string RecipientName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
