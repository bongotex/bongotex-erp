using BongoTex.Core.Entities;

namespace BongoTex.Application.Services;

public class ErpStore
{
    public List<InventoryItem> Inventory { get; } = new();
    public List<Customer> Customers { get; } = new();
    public List<SalesOrder> SalesOrders { get; } = new();
}
