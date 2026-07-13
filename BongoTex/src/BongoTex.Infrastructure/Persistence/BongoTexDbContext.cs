using BongoTex.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace BongoTex.Infrastructure.Persistence;

public class BongoTexDbContext(DbContextOptions<BongoTexDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryStock> InventoryStocks => Set<InventoryStock>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<CuttingEntry> CuttingEntries => Set<CuttingEntry>();
    public DbSet<SewingEntry> SewingEntries => Set<SewingEntry>();
    public DbSet<FinishingEntry> FinishingEntries => Set<FinishingEntry>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<SalesTransaction> SalesTransactions => Set<SalesTransaction>();
    public DbSet<SalesCollection> SalesCollections => Set<SalesCollection>();
    public DbSet<SalesReturn> SalesReturns => Set<SalesReturn>();
    public DbSet<FinishedItemGiftIssue> FinishedItemGiftIssues => Set<FinishedItemGiftIssue>();
    public DbSet<ExpenseEntry> ExpenseEntries => Set<ExpenseEntry>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();
    public DbSet<FactoryAttendanceDay> FactoryAttendanceDays => Set<FactoryAttendanceDay>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<SupplierPurchase> SupplierPurchases => Set<SupplierPurchase>();
    public DbSet<SupplierPurchaseLine> SupplierPurchaseLines => Set<SupplierPurchaseLine>();
    public DbSet<RawMaterial> RawMaterials => Set<RawMaterial>();
    public DbSet<RawMaterialStock> RawMaterialStocks => Set<RawMaterialStock>();
    public DbSet<RawMaterialMovement> RawMaterialMovements => Set<RawMaterialMovement>();
    public DbSet<RawMaterialScrapSale> RawMaterialScrapSales => Set<RawMaterialScrapSale>();
    public DbSet<PrintFactoryPurchase> PrintFactoryPurchases => Set<PrintFactoryPurchase>();
    public DbSet<PrintFactoryPurchaseLine> PrintFactoryPurchaseLines => Set<PrintFactoryPurchaseLine>();
    public DbSet<PrintFactorySale> PrintFactorySales => Set<PrintFactorySale>();
    public DbSet<PrintFactorySaleLine> PrintFactorySaleLines => Set<PrintFactorySaleLine>();
    public DbSet<PrintFactoryCashEntry> PrintFactoryCashEntries => Set<PrintFactoryCashEntry>();
    public DbSet<PrintFactoryMonthStockLine> PrintFactoryMonthStockLines => Set<PrintFactoryMonthStockLine>();
    public DbSet<SiteMonthlyRent> SiteMonthlyRents => Set<SiteMonthlyRent>();
    public DbSet<ProductStyle> ProductStyles => Set<ProductStyle>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(60).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(120);
            entity.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<InventoryItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Sku).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CuttingNumber).HasMaxLength(80).IsRequired();
            entity.Property(x => x.UnitPrice).HasPrecision(18, 2);
            entity.Property(x => x.ProductionCost).HasPrecision(18, 2);
            entity.Property(x => x.SalesPrice).HasPrecision(18, 2);
            entity.Property(x => x.DiscountPrice).HasPrecision(18, 2);
            entity.Property(x => x.PrintChargePerPiece).HasPrecision(18, 2);
            entity.Property(x => x.ItemImageBase64);
            entity.HasIndex(x => x.Sku).IsUnique();
            entity.HasIndex(x => x.CuttingNumber).IsUnique();
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CustomerCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ShopName).HasMaxLength(200);
            entity.Property(x => x.Phone).HasMaxLength(30);
            entity.Property(x => x.Email).HasMaxLength(120);
            entity.Property(x => x.Address).HasMaxLength(400);
            entity.HasIndex(x => x.CustomerCode).IsUnique();
        });

        modelBuilder.Entity<SalesOrder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrderNumber).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.HasIndex(x => x.OrderNumber).IsUnique();
        });

        modelBuilder.Entity<Site>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<InventoryStock>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.InventoryItemId, x.SiteId }).IsUnique();
        });

        modelBuilder.Entity<ProductionOrder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductionNo).HasMaxLength(40).IsRequired();
            entity.HasIndex(x => x.ProductionNo).IsUnique();
        });

        modelBuilder.Entity<CuttingEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CuttingNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CutLotCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.FabricKg).HasPrecision(18, 4);
            entity.Property(x => x.FabricPricePerKg).HasPrecision(18, 4);
            entity.Property(x => x.FabricAmount).HasPrecision(18, 2);
            entity.HasIndex(x => x.CuttingNo).IsUnique();
            entity.HasIndex(x => x.CutAtUtc);
            entity.HasIndex(x => x.InventoryItemId);
            entity.HasIndex(x => x.RawMaterialId);
            entity.HasIndex(x => new { x.FactorySiteId, x.CutLotCode })
                .IsUnique()
                .HasFilter("[CutLotCode] <> N''");
            entity.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).IsRequired(false);
        });

        modelBuilder.Entity<RawMaterial>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<RawMaterialStock>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuantityOnHand).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.RawMaterialId, x.SiteId }).IsUnique();
        });

        modelBuilder.Entity<RawMaterialMovement>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MovementNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.MovementType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.TotalCost).HasPrecision(18, 2);
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.Property(x => x.CutLotCode).HasMaxLength(80);
            entity.HasIndex(x => x.MovementNo).IsUnique();
            entity.HasIndex(x => x.MovementDateUtc);
            entity.HasIndex(x => x.RawMaterialId);
            entity.HasIndex(x => x.SiteId);
            entity.HasIndex(x => x.SupplierPurchaseId);
            entity.HasIndex(x => x.CuttingEntryId);
            entity.HasIndex(x => x.FinishingEntryId);
            entity.HasIndex(x => x.ScrapSaleId);
        });

        modelBuilder.Entity<RawMaterialScrapSale>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SaleNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ScrapType).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.UnitRate).HasPrecision(18, 4);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.BuyerName).HasMaxLength(120);
            entity.Property(x => x.PaidAmount).HasPrecision(18, 2);
            entity.Property(x => x.DueAmount).HasPrecision(18, 2);
            entity.Property(x => x.Note).HasMaxLength(250);
            entity.HasIndex(x => x.SaleNo).IsUnique();
            entity.HasIndex(x => x.SoldAtUtc);
            entity.HasIndex(x => x.SiteId);
            entity.HasIndex(x => x.InventoryItemId);
        });

        modelBuilder.Entity<SewingEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SewingNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CutLotCode).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.SewingNo).IsUnique();
            entity.HasIndex(x => x.SewnAtUtc);
            entity.HasIndex(x => x.InventoryItemId);
            entity.HasIndex(x => new { x.FactorySiteId, x.CutLotCode });
            entity.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).IsRequired(false);
        });

        modelBuilder.Entity<FinishingEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FinishingNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CutLotCode).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.FinishingNo).IsUnique();
            entity.HasIndex(x => x.FinishedAtUtc);
            entity.HasIndex(x => x.InventoryItemId);
        });

        modelBuilder.Entity<StockTransfer>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TransferNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.DocumentNo).HasMaxLength(50);
            entity.HasIndex(x => x.TransferNo).IsUnique();
            entity.HasIndex(x => x.DocumentNo);
        });

        modelBuilder.Entity<SalesTransaction>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SalesNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.InvoiceNo).HasMaxLength(46);
            entity.HasIndex(x => x.InvoiceNo);
            entity.Property(x => x.CustomerName).HasMaxLength(120);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 2);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.PaidAmount).HasPrecision(18, 2);
            entity.Property(x => x.DueAmount).HasPrecision(18, 2);
            entity.Property(x => x.PrintChargePerPieceAtSale).HasPrecision(18, 2);
            entity.Property(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.SalesNo).IsUnique();
            entity.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).IsRequired(false);
        });

        modelBuilder.Entity<SalesCollection>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Note).HasMaxLength(250);
        });

        modelBuilder.Entity<SalesReturn>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ReturnNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.InvoiceNo).HasMaxLength(46);
            entity.Property(x => x.CustomerType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.CustomerName).HasMaxLength(120);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 2);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.ReturnType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ActionType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.RefundAmount).HasPrecision(18, 2);
            entity.Property(x => x.DueCreditApplied).HasPrecision(18, 2);
            entity.Property(x => x.Reason).HasMaxLength(250);
            entity.HasIndex(x => x.ReturnNo).IsUnique();
        });

        modelBuilder.Entity<FinishedItemGiftIssue>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.GiftNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.UnitCost).HasPrecision(18, 2);
            entity.Property(x => x.TotalCost).HasPrecision(18, 2);
            entity.Property(x => x.RecipientName).HasMaxLength(120);
            entity.Property(x => x.Reason).HasMaxLength(250);
            entity.HasIndex(x => x.GiftNo).IsUnique();
            entity.HasIndex(x => x.IssuedAtUtc);
        });

        modelBuilder.Entity<ExpenseEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExpenseNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PartyName).HasMaxLength(120);
            entity.Property(x => x.ExpenseScope).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Department).HasMaxLength(40);
            entity.Property(x => x.Description).HasMaxLength(250);
            entity.Property(x => x.SalaryPaymentType).HasMaxLength(20);
            entity.Property(x => x.SalaryForMonth).HasMaxLength(7);
            entity.Property(x => x.CashflowDirection).HasMaxLength(10);
            entity.Property(x => x.CashbookType).HasMaxLength(30);
            entity.Property(x => x.CashbookNote).HasMaxLength(250);
            entity.HasIndex(x => x.ExpenseNo).IsUnique();
        });

        modelBuilder.Entity<CashMovement>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MovementNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.FromPool).HasMaxLength(30).IsRequired();
            entity.Property(x => x.ToPool).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.HasIndex(x => x.MovementNo).IsUnique();
            entity.HasIndex(x => x.MovementDateUtc);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SupplierCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CompanyName).HasMaxLength(200);
            entity.Property(x => x.Category).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Phone).HasMaxLength(30);
            entity.Property(x => x.Address).HasMaxLength(400);
            entity.HasIndex(x => x.SupplierCode).IsUnique();
        });

        modelBuilder.Entity<SupplierPurchase>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PurchaseNo).HasMaxLength(40).IsRequired();
            entity.Property(x => x.InvoiceRef).HasMaxLength(80);
            entity.Property(x => x.Description).HasMaxLength(250);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.PaidAmount).HasPrecision(18, 2);
            entity.Property(x => x.DueAmount).HasPrecision(18, 2);
            entity.HasIndex(x => x.PurchaseNo).IsUnique();
            entity.HasIndex(x => x.PurchasedAtUtc);
            entity.HasIndex(x => x.SupplierId);
        });

        modelBuilder.Entity<SupplierPurchaseLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UnitCost).HasPrecision(18, 2);
            entity.Property(x => x.LineTotal).HasPrecision(18, 2);
            entity.HasIndex(x => x.SupplierPurchaseId);
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.EmployeeType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.EmployeeCategory).HasMaxLength(40);
            entity.Property(x => x.SiteId);
            entity.Property(x => x.MonthlySalary).HasPrecision(18, 2);
            entity.Property(x => x.MobileNumber).HasMaxLength(30).IsRequired();
            entity.Property(x => x.NationalIdNumber).HasMaxLength(60).IsRequired();
            entity.Property(x => x.NationalIdImageBase64);
            entity.Property(x => x.Address).HasMaxLength(400);
            entity.HasIndex(x => x.EmployeeCode).IsUnique();
            entity.HasIndex(x => x.NationalIdNumber).IsUnique();
            entity.Property(x => x.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<SiteMonthlyRent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MonthlyRent).HasPrecision(18, 2);
            entity.Property(x => x.LandlordName).HasMaxLength(200);
            entity.HasIndex(x => x.SiteId).IsUnique();
        });

        modelBuilder.Entity<ProductStyle>(entity =>
        {
            entity.HasKey(x => x.Prefix);
            entity.Property(x => x.Prefix).HasMaxLength(10).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ProductionCost).HasPrecision(18, 2);
        });

        modelBuilder.Entity<PayrollRun>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MonthKey).HasMaxLength(7).IsRequired();
            entity.Property(x => x.ExpenseScope).HasMaxLength(20).IsRequired();
            entity.Property(x => x.SiteId);
            entity.Property(x => x.CreatedAtUtc);
            entity.HasIndex(x => new { x.MonthKey, x.ExpenseScope, x.SiteId });
        });

        modelBuilder.Entity<PayrollLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.EmployeeCategory).HasMaxLength(40).IsRequired();
            entity.Property(x => x.MonthlySalary).HasPrecision(18, 2);
            entity.Property(x => x.AttendanceDays).HasPrecision(18, 2);
            entity.Property(x => x.AttendanceSalaryAmount).HasPrecision(18, 2);
            entity.Property(x => x.OvertimeHours).HasPrecision(18, 2);
            entity.Property(x => x.OvertimeAmount).HasPrecision(18, 2);
            entity.Property(x => x.AttendanceBonus).HasPrecision(18, 2);
            entity.Property(x => x.SnakesPay).HasPrecision(18, 2);
            entity.Property(x => x.AdvancePaid).HasPrecision(18, 2);
            entity.Property(x => x.CurrentPaid).HasPrecision(18, 2);
            entity.Property(x => x.DuePaid).HasPrecision(18, 2);
            entity.Property(x => x.NetPayable).HasPrecision(18, 2);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.UpdatedAtUtc);
            entity.HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique();
        });

        modelBuilder.Entity<FactoryAttendanceDay>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MonthKey).HasMaxLength(7).IsRequired();
            entity.Property(x => x.MarkCode).HasMaxLength(1).IsRequired();
            entity.Property(x => x.DayValue).HasPrecision(18, 2);
            entity.Property(x => x.AttendanceDateUtc);
            entity.Property(x => x.UpdatedAtUtc);
            entity.HasIndex(x => new { x.EmployeeId, x.AttendanceDateUtc }).IsUnique();
            entity.HasIndex(x => x.MonthKey);
        });

        modelBuilder.Entity<PrintFactoryPurchase>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.VoucherNo).HasMaxLength(50).IsRequired();
            entity.Property(x => x.SupplierName).HasMaxLength(200);
            entity.Property(x => x.InvoiceRef).HasMaxLength(80);
            entity.Property(x => x.Description).HasMaxLength(250);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.PaidAmount).HasPrecision(18, 2);
            entity.Property(x => x.DueAmount).HasPrecision(18, 2);
            entity.HasIndex(x => x.VoucherNo).IsUnique();
            entity.HasIndex(x => x.PurchasedAtUtc);
        });

        modelBuilder.Entity<PrintFactoryPurchaseLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Description).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.LineTotal).HasPrecision(18, 2);
            entity.HasIndex(x => x.PrintFactoryPurchaseId);
        });

        modelBuilder.Entity<PrintFactorySale>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.VoucherNo).HasMaxLength(50).IsRequired();
            entity.Property(x => x.BuyerType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.BuyerName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.InvoiceRef).HasMaxLength(80);
            entity.Property(x => x.Description).HasMaxLength(250);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.ReceivedAmount).HasPrecision(18, 2);
            entity.Property(x => x.DueAmount).HasPrecision(18, 2);
            entity.HasIndex(x => x.VoucherNo).IsUnique();
            entity.HasIndex(x => x.SoldAtUtc);
        });

        modelBuilder.Entity<PrintFactorySaleLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Description).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.UnitRate).HasPrecision(18, 4);
            entity.Property(x => x.LineTotal).HasPrecision(18, 2);
            entity.HasIndex(x => x.PrintFactorySaleId);
        });

        modelBuilder.Entity<PrintFactoryCashEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.VoucherNo).HasMaxLength(50).IsRequired();
            entity.Property(x => x.EntryType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.PartyName).HasMaxLength(200);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Note).HasMaxLength(250);
            entity.HasIndex(x => x.VoucherNo).IsUnique();
            entity.HasIndex(x => x.EntryDateUtc);
        });

        modelBuilder.Entity<PrintFactoryMonthStockLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MonthKey).HasMaxLength(7).IsRequired();
            entity.Property(x => x.ItemDescription).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ClosingQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ClosingValue).HasPrecision(18, 2);
            entity.HasIndex(x => x.MonthKey);
        });
    }
}
