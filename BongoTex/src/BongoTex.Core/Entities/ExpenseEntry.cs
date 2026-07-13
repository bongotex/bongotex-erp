namespace BongoTex.Core.Entities;

public class ExpenseEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExpenseNo { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // SupplierPayment, Salary, Rent, DailyExpense, ManagerRemittance, OwnerDraw
    public string PartyName { get; set; } = string.Empty; // Supplier/Employee/Landlord/etc
    public string ExpenseScope { get; set; } = string.Empty; // Factory, SalesCenter, or Owner (OwnerDraw only)
    public Guid? SiteId { get; set; } // required when ExpenseScope=SalesCenter
    public decimal Amount { get; set; }
    public string Department { get; set; } = string.Empty; // Print, FactoryGeneral, etc.
    public string Description { get; set; } = string.Empty;
    public string SalaryPaymentType { get; set; } = string.Empty; // Advance, Due, Current (Salary only)
    public string SalaryForMonth { get; set; } = string.Empty; // yyyy-MM (Salary only)
    // Manager cashbook tags (In/Out, bucket, free-text note)
    public string CashflowDirection { get; set; } = string.Empty;
    public string CashbookType { get; set; } = string.Empty;
    public string CashbookNote { get; set; } = string.Empty;
    public DateTime ExpenseDateUtc { get; set; } = DateTime.UtcNow;
}

public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SupplierCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Category { get; set; } = "Others";
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Manual serial within factory, print factory, or sales-centre shop series.</summary>
    public int SerialNumber { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EmployeeType { get; set; } = "SalesCenter";
    public string EmployeeCategory { get; set; } = string.Empty;
    public Guid? SiteId { get; set; }
    public decimal MonthlySalary { get; set; }
    public string MobileNumber { get; set; } = string.Empty;
    public string NationalIdNumber { get; set; } = string.Empty;
    public string NationalIdImageBase64 { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LeftAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PayrollRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MonthKey { get; set; } = string.Empty; // yyyy-MM
    public string ExpenseScope { get; set; } = "Factory"; // Factory or SalesCenter
    public Guid? SiteId { get; set; } // required for SalesCenter payroll run
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PayrollLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    /// <summary>Factory designation at payroll time (Operator, Helper, Staff, etc.).</summary>
    public string EmployeeCategory { get; set; } = string.Empty;
    public decimal MonthlySalary { get; set; }
    /// <summary>Factory payroll only: days present in the payroll month.</summary>
    public decimal AttendanceDays { get; set; }
    /// <summary>Computed (factory): (MonthlySalary / daysInMonth) * AttendanceDays</summary>
    public decimal AttendanceSalaryAmount { get; set; }
    /// <summary>Factory payroll only: overtime hours for the payroll month.</summary>
    public decimal OvertimeHours { get; set; }
    /// <summary>Computed: (MonthlySalary / daysInMonth / 12) * OvertimeHours</summary>
    public decimal OvertimeAmount { get; set; }
    /// <summary>Factory: computed when attendance covers full calendar month — one day's pay (Operator/Helper only).</summary>
    public decimal AttendanceBonus { get; set; }
    /// <summary>Factory: snack/snakes pay allowance (added to gross).</summary>
    public decimal SnakesPay { get; set; }
    public decimal AdvancePaid { get; set; }
    public decimal CurrentPaid { get; set; }
    public decimal DuePaid { get; set; }
    public decimal NetPayable { get; set; }
    public string Status { get; set; } = "Unpaid"; // Unpaid, Partial, Paid
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Daily factory attendance; summed into PayrollLine.AttendanceDays for the month.</summary>
public class FactoryAttendanceDay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string MonthKey { get; set; } = string.Empty; // yyyy-MM
    public DateTime AttendanceDateUtc { get; set; }
    /// <summary>P = present (counts toward payroll days), A = absent (recorded, does not count).</summary>
    public string MarkCode { get; set; } = "P";
    public decimal DayValue { get; set; } = 1m;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class SupplierPurchase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PurchaseNo { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Guid FactorySiteId { get; set; }
    public string InvoiceRef { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal DueAmount { get; set; }
    public DateTime PurchasedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<SupplierPurchaseLine> Lines { get; set; } = new List<SupplierPurchaseLine>();
}

public class SupplierPurchaseLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierPurchaseId { get; set; }
    public Guid InventoryItemId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}

public class RawMaterial
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Others";
    public string Unit { get; set; } = "kg";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class RawMaterialStock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RawMaterialId { get; set; }
    public Guid SiteId { get; set; }
    public decimal QuantityOnHand { get; set; }
}

public class RawMaterialMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MovementNo { get; set; } = string.Empty;
    public Guid RawMaterialId { get; set; }
    public Guid SiteId { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public DateTime MovementDateUtc { get; set; } = DateTime.UtcNow;
    public string Note { get; set; } = string.Empty;
    public Guid? SupplierPurchaseId { get; set; }
    public Guid? CuttingEntryId { get; set; }
    public Guid? FinishingEntryId { get; set; }
    public string CutLotCode { get; set; } = string.Empty;
    public Guid? ScrapSaleId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Factory sale of scrap, cutting fabric wastage, or reject garments.</summary>
public class RawMaterialScrapSale
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SaleNo { get; set; } = string.Empty;
    public Guid SiteId { get; set; }
    /// <summary>Raw material for scrap pile or fabric type label (cutting wastage).</summary>
    public Guid? RawMaterialId { get; set; }
    /// <summary>Finished SKU for reject garment sales (no inventory deduction).</summary>
    public Guid? InventoryItemId { get; set; }
    /// <summary>ScrapStock, CuttingWastage, or RejectGarment (legacy: Wastage, Reject).</summary>
    public string ScrapType { get; set; } = "ScrapStock";
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "kg";
    public decimal UnitRate { get; set; }
    public decimal TotalAmount { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public bool IsCredit { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal DueAmount { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime SoldAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PrintFactoryPurchase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VoucherNo { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string InvoiceRef { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal DueAmount { get; set; }
    public DateTime PurchasedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<PrintFactoryPurchaseLine> Lines { get; set; } = new List<PrintFactoryPurchaseLine>();
}

public class PrintFactoryPurchaseLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PrintFactoryPurchaseId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}

public class PrintFactorySale
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VoucherNo { get; set; } = string.Empty;
    public string BuyerType { get; set; } = "Internal";
    public string BuyerName { get; set; } = string.Empty;
    public string InvoiceRef { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal ReceivedAmount { get; set; }
    public decimal DueAmount { get; set; }
    public DateTime SoldAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<PrintFactorySaleLine> Lines { get; set; } = new List<PrintFactorySaleLine>();
}

public class PrintFactorySaleLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PrintFactorySaleId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal UnitRate { get; set; }
    public decimal LineTotal { get; set; }
}

public class PrintFactoryCashEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VoucherNo { get; set; } = string.Empty;
    public string EntryType { get; set; } = string.Empty;
    public Guid? PurchaseId { get; set; }
    public Guid? SaleId { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime EntryDateUtc { get; set; } = DateTime.UtcNow;
    public Guid? ExpenseEntryId { get; set; }
}

/// <summary>Month-end closing stock for Print Factory materials (adjusts P/L for unused bulk purchases).</summary>
public class PrintFactoryMonthStockLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MonthKey { get; set; } = string.Empty;
    public string ItemDescription { get; set; } = string.Empty;
    public string Unit { get; set; } = "pcs";
    public decimal ClosingQuantity { get; set; }
    public decimal ClosingValue { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
