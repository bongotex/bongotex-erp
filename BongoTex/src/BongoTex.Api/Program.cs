using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BongoTex.Api;
using BongoTex.Core.Entities;
using BongoTex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BongoTexDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseSqlServer(connectionString);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

var saleBodyJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true
};

Func<string, IResult> apiErr = msg => Results.Json(new { error = msg }, statusCode: StatusCodes.Status400BadRequest);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BongoTexDbContext>();
    try
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("""
        IF COL_LENGTH('InventoryItems', 'SalesPrice') IS NULL
            ALTER TABLE InventoryItems ADD SalesPrice decimal(18,2) NOT NULL CONSTRAINT DF_InventoryItems_SalesPrice DEFAULT(0);
        IF COL_LENGTH('InventoryItems', 'DiscountPrice') IS NULL
            ALTER TABLE InventoryItems ADD DiscountPrice decimal(18,2) NOT NULL CONSTRAINT DF_InventoryItems_DiscountPrice DEFAULT(0);
        IF COL_LENGTH('InventoryItems', 'CuttingNumber') IS NULL
            ALTER TABLE InventoryItems ADD CuttingNumber nvarchar(80) NOT NULL CONSTRAINT DF_InventoryItems_CuttingNumber DEFAULT('');
        IF COL_LENGTH('InventoryItems', 'ItemImageBase64') IS NULL
            ALTER TABLE InventoryItems ADD ItemImageBase64 nvarchar(max) NOT NULL CONSTRAINT DF_InventoryItems_ItemImageBase64 DEFAULT('');
        IF COL_LENGTH('InventoryItems', 'IsPrintItem') IS NULL
            ALTER TABLE InventoryItems ADD IsPrintItem bit NOT NULL CONSTRAINT DF_InventoryItems_IsPrintItem DEFAULT(0);
        IF COL_LENGTH('InventoryItems', 'PrintChargePerPiece') IS NULL
            ALTER TABLE InventoryItems ADD PrintChargePerPiece decimal(18,2) NOT NULL CONSTRAINT DF_InventoryItems_PrintChargePerPiece DEFAULT(0);
        IF COL_LENGTH('InventoryItems', 'CreatedAtUtc') IS NULL
            ALTER TABLE InventoryItems ADD CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_InventoryItems_CreatedAtUtc DEFAULT(GETUTCDATE());
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InventoryItems_CuttingNumber' AND object_id = OBJECT_ID('InventoryItems'))
           AND NOT EXISTS (
               SELECT CuttingNumber
               FROM InventoryItems
               GROUP BY CuttingNumber
               HAVING COUNT(*) > 1
           )
           AND NOT EXISTS (
               SELECT 1
               FROM InventoryItems
               WHERE CuttingNumber IS NULL OR LTRIM(RTRIM(CuttingNumber)) = ''
           )
        BEGIN
            CREATE UNIQUE INDEX IX_InventoryItems_CuttingNumber ON InventoryItems(CuttingNumber);
        END

        UPDATE Sites SET Name = 'City Plaza' WHERE Code = 'SC-01';
        UPDATE Sites SET Name = 'Nagar Plaza' WHERE Code = 'SC-02';
        UPDATE Sites SET Name = 'Trade Center' WHERE Code = 'SC-03';

        IF COL_LENGTH('Customers', 'ShopName') IS NULL
            ALTER TABLE Customers ADD ShopName nvarchar(200) NOT NULL CONSTRAINT DF_Customers_ShopName DEFAULT('');
        IF COL_LENGTH('Customers', 'Address') IS NULL
            ALTER TABLE Customers ADD Address nvarchar(400) NOT NULL CONSTRAINT DF_Customers_Address DEFAULT('');

        IF OBJECT_ID('SalesTransactions', 'U') IS NULL
        BEGIN
            CREATE TABLE SalesTransactions
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SalesNo nvarchar(40) NOT NULL,
                SiteId uniqueidentifier NOT NULL,
                InventoryItemId uniqueidentifier NOT NULL,
                CustomerName nvarchar(120) NOT NULL CONSTRAINT DF_SalesTransactions_CustomerName DEFAULT(''),
                Quantity int NOT NULL,
                UnitPrice decimal(18,2) NOT NULL,
                TotalAmount decimal(18,2) NOT NULL,
                SoldAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_SalesTransactions_SalesNo ON SalesTransactions(SalesNo);
        END

        IF COL_LENGTH('SalesTransactions', 'IsCredit') IS NULL
            ALTER TABLE SalesTransactions ADD IsCredit bit NOT NULL CONSTRAINT DF_SalesTransactions_IsCredit DEFAULT(0);
        IF COL_LENGTH('SalesTransactions', 'PaidAmount') IS NULL
            ALTER TABLE SalesTransactions ADD PaidAmount decimal(18,2) NOT NULL CONSTRAINT DF_SalesTransactions_PaidAmount DEFAULT(0);
        IF COL_LENGTH('SalesTransactions', 'DueAmount') IS NULL
            ALTER TABLE SalesTransactions ADD DueAmount decimal(18,2) NOT NULL CONSTRAINT DF_SalesTransactions_DueAmount DEFAULT(0);

        IF COL_LENGTH('SalesTransactions', 'InvoiceNo') IS NULL
            ALTER TABLE SalesTransactions ADD InvoiceNo nvarchar(46) NULL;
        IF COL_LENGTH('SalesTransactions', 'CreatedAtUtc') IS NULL
        BEGIN
            ALTER TABLE SalesTransactions ADD CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_SalesTransactions_CreatedAtUtc DEFAULT(GETUTCDATE());
        END

        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes WHERE name = 'IX_SalesTransactions_InvoiceNo' AND object_id = OBJECT_ID('SalesTransactions'))
            CREATE INDEX IX_SalesTransactions_InvoiceNo ON SalesTransactions(InvoiceNo);

        IF COL_LENGTH('SalesTransactions', 'IsPrintItemAtSale') IS NULL
            ALTER TABLE SalesTransactions ADD IsPrintItemAtSale bit NOT NULL CONSTRAINT DF_SalesTransactions_IsPrintItemAtSale DEFAULT(0);
        IF COL_LENGTH('SalesTransactions', 'PrintChargePerPieceAtSale') IS NULL
            ALTER TABLE SalesTransactions ADD PrintChargePerPieceAtSale decimal(18,2) NOT NULL CONSTRAINT DF_SalesTransactions_PrintChargeAtSale DEFAULT(0);
        IF COL_LENGTH('InventoryItems', 'PrintItemMarkedAtUtc') IS NULL
            ALTER TABLE InventoryItems ADD PrintItemMarkedAtUtc datetime2 NULL;

        DECLARE @salesTxFk sysname;
        DECLARE @dropFkSql nvarchar(512);
        SELECT @salesTxFk = fk.name
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fk.object_id = fkc.constraint_object_id
        WHERE fk.parent_object_id = OBJECT_ID(N'SalesTransactions')
          AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'InventoryItemId';
        IF @salesTxFk IS NOT NULL
        BEGIN
            SET @dropFkSql = N'ALTER TABLE SalesTransactions DROP CONSTRAINT ' + QUOTENAME(@salesTxFk);
            EXEC (@dropFkSql);
        END
        IF EXISTS (
            SELECT 1 FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            WHERE t.name = N'SalesTransactions' AND c.name = N'InventoryItemId' AND c.is_nullable = 0)
            ALTER TABLE SalesTransactions ALTER COLUMN InventoryItemId uniqueidentifier NULL;

        IF OBJECT_ID('SalesCollections', 'U') IS NULL
        BEGIN
            CREATE TABLE SalesCollections
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SalesTransactionId uniqueidentifier NOT NULL,
                Amount decimal(18,2) NOT NULL,
                CollectedAtUtc datetime2 NOT NULL,
                Note nvarchar(250) NOT NULL CONSTRAINT DF_SalesCollections_Note DEFAULT('')
            );
        END

        IF OBJECT_ID('ExpenseEntries', 'U') IS NULL
        BEGIN
            CREATE TABLE ExpenseEntries
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ExpenseNo nvarchar(40) NOT NULL,
                Category nvarchar(40) NOT NULL,
                PartyName nvarchar(120) NOT NULL CONSTRAINT DF_ExpenseEntries_PartyName DEFAULT(''),
                ExpenseScope nvarchar(20) NOT NULL CONSTRAINT DF_ExpenseEntries_ExpenseScope DEFAULT('Factory'),
                SiteId uniqueidentifier NULL,
                Amount decimal(18,2) NOT NULL,
                Description nvarchar(250) NOT NULL CONSTRAINT DF_ExpenseEntries_Description DEFAULT(''),
                SalaryPaymentType nvarchar(20) NOT NULL CONSTRAINT DF_ExpenseEntries_SalaryPaymentType DEFAULT(''),
                SalaryForMonth nvarchar(7) NOT NULL CONSTRAINT DF_ExpenseEntries_SalaryForMonth DEFAULT(''),
                CashflowDirection nvarchar(10) NOT NULL CONSTRAINT DF_ExpenseEntries_CashflowDirection DEFAULT(''),
                CashbookType nvarchar(30) NOT NULL CONSTRAINT DF_ExpenseEntries_CashbookType DEFAULT(''),
                CashbookNote nvarchar(250) NOT NULL CONSTRAINT DF_ExpenseEntries_CashbookNote DEFAULT(''),
                ExpenseDateUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_ExpenseEntries_ExpenseNo ON ExpenseEntries(ExpenseNo);
        END
        IF COL_LENGTH('ExpenseEntries', 'PartyName') IS NULL
            ALTER TABLE ExpenseEntries ADD PartyName nvarchar(120) NOT NULL CONSTRAINT DF_ExpenseEntries_PartyName2 DEFAULT('');
        IF COL_LENGTH('ExpenseEntries', 'ExpenseScope') IS NULL
            ALTER TABLE ExpenseEntries ADD ExpenseScope nvarchar(20) NOT NULL CONSTRAINT DF_ExpenseEntries_ExpenseScope2 DEFAULT('Factory');
        IF COL_LENGTH('ExpenseEntries', 'SiteId') IS NULL
            ALTER TABLE ExpenseEntries ADD SiteId uniqueidentifier NULL;
        IF COL_LENGTH('ExpenseEntries', 'SalaryPaymentType') IS NULL
            ALTER TABLE ExpenseEntries ADD SalaryPaymentType nvarchar(20) NOT NULL CONSTRAINT DF_ExpenseEntries_SalaryPaymentType2 DEFAULT('');
        IF COL_LENGTH('ExpenseEntries', 'SalaryForMonth') IS NULL
            ALTER TABLE ExpenseEntries ADD SalaryForMonth nvarchar(7) NOT NULL CONSTRAINT DF_ExpenseEntries_SalaryForMonth2 DEFAULT('');
        IF COL_LENGTH('ExpenseEntries', 'CashflowDirection') IS NULL
            ALTER TABLE ExpenseEntries ADD CashflowDirection nvarchar(10) NOT NULL CONSTRAINT DF_ExpenseEntries_CashflowDirection2 DEFAULT('');
        IF COL_LENGTH('ExpenseEntries', 'CashbookType') IS NULL
            ALTER TABLE ExpenseEntries ADD CashbookType nvarchar(30) NOT NULL CONSTRAINT DF_ExpenseEntries_CashbookType2 DEFAULT('');
        IF COL_LENGTH('ExpenseEntries', 'CashbookNote') IS NULL
            ALTER TABLE ExpenseEntries ADD CashbookNote nvarchar(250) NOT NULL CONSTRAINT DF_ExpenseEntries_CashbookNote2 DEFAULT('');
        IF COL_LENGTH('ExpenseEntries', 'Department') IS NULL
            ALTER TABLE ExpenseEntries ADD Department nvarchar(40) NOT NULL CONSTRAINT DF_ExpenseEntries_Department DEFAULT('');

        IF OBJECT_ID('CashMovements', 'U') IS NULL
        BEGIN
            CREATE TABLE CashMovements
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                MovementNo nvarchar(40) NOT NULL,
                FromPool nvarchar(30) NOT NULL,
                ToPool nvarchar(30) NOT NULL,
                Amount decimal(18,2) NOT NULL,
                FromSiteId uniqueidentifier NULL,
                ToSiteId uniqueidentifier NULL,
                Note nvarchar(500) NOT NULL CONSTRAINT DF_CashMovements_Note DEFAULT(''),
                MovementDateUtc datetime2 NOT NULL,
                CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_CashMovements_CreatedAtUtc DEFAULT(GETUTCDATE())
            );
            CREATE UNIQUE INDEX IX_CashMovements_MovementNo ON CashMovements(MovementNo);
            CREATE INDEX IX_CashMovements_MovementDateUtc ON CashMovements(MovementDateUtc);
        END

        IF OBJECT_ID('Suppliers', 'U') IS NULL
        BEGIN
            CREATE TABLE Suppliers
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SupplierCode nvarchar(50) NOT NULL,
                Name nvarchar(200) NOT NULL,
                CompanyName nvarchar(200) NOT NULL CONSTRAINT DF_Suppliers_CompanyName DEFAULT(''),
                Category nvarchar(40) NOT NULL CONSTRAINT DF_Suppliers_Category DEFAULT('Others'),
                Phone nvarchar(30) NOT NULL CONSTRAINT DF_Suppliers_Phone DEFAULT(''),
                Address nvarchar(400) NOT NULL CONSTRAINT DF_Suppliers_Address DEFAULT(''),
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_Suppliers_SupplierCode ON Suppliers(SupplierCode);
        END
        IF COL_LENGTH('Suppliers', 'CompanyName') IS NULL
            ALTER TABLE Suppliers ADD CompanyName nvarchar(200) NOT NULL CONSTRAINT DF_Suppliers_CompanyName2 DEFAULT('');
        IF COL_LENGTH('Suppliers', 'Category') IS NULL
            ALTER TABLE Suppliers ADD Category nvarchar(40) NOT NULL CONSTRAINT DF_Suppliers_Category2 DEFAULT('Others');
        UPDATE Suppliers SET Category = N'PrintOutside' WHERE Category = N'Print';

        IF OBJECT_ID('SupplierPurchases', 'U') IS NULL
        BEGIN
            CREATE TABLE SupplierPurchases
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                PurchaseNo nvarchar(40) NOT NULL,
                SupplierId uniqueidentifier NOT NULL,
                FactorySiteId uniqueidentifier NOT NULL,
                InvoiceRef nvarchar(80) NOT NULL CONSTRAINT DF_SupplierPurchases_InvoiceRef DEFAULT(''),
                Description nvarchar(250) NOT NULL CONSTRAINT DF_SupplierPurchases_Description DEFAULT(''),
                TotalAmount decimal(18,2) NOT NULL,
                PaidAmount decimal(18,2) NOT NULL,
                DueAmount decimal(18,2) NOT NULL,
                PurchasedAtUtc datetime2 NOT NULL,
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_SupplierPurchases_PurchaseNo ON SupplierPurchases(PurchaseNo);
            CREATE INDEX IX_SupplierPurchases_PurchasedAtUtc ON SupplierPurchases(PurchasedAtUtc);
            CREATE INDEX IX_SupplierPurchases_SupplierId ON SupplierPurchases(SupplierId);
        END
        IF OBJECT_ID('SupplierPurchaseLines', 'U') IS NULL
        BEGIN
            CREATE TABLE SupplierPurchaseLines
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SupplierPurchaseId uniqueidentifier NOT NULL,
                InventoryItemId uniqueidentifier NOT NULL,
                Quantity int NOT NULL,
                UnitCost decimal(18,2) NOT NULL,
                LineTotal decimal(18,2) NOT NULL
            );
            CREATE INDEX IX_SupplierPurchaseLines_PurchaseId ON SupplierPurchaseLines(SupplierPurchaseId);
        END

        IF OBJECT_ID('RawMaterials', 'U') IS NULL
        BEGIN
            CREATE TABLE RawMaterials
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Code nvarchar(50) NOT NULL,
                Name nvarchar(200) NOT NULL,
                Category nvarchar(40) NOT NULL CONSTRAINT DF_RawMaterials_Category DEFAULT('Others'),
                Unit nvarchar(20) NOT NULL CONSTRAINT DF_RawMaterials_Unit DEFAULT('kg'),
                IsActive bit NOT NULL CONSTRAINT DF_RawMaterials_IsActive DEFAULT(1),
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_RawMaterials_Code ON RawMaterials(Code);
        END
        IF OBJECT_ID('RawMaterialStocks', 'U') IS NULL
        BEGIN
            CREATE TABLE RawMaterialStocks
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                RawMaterialId uniqueidentifier NOT NULL,
                SiteId uniqueidentifier NOT NULL,
                QuantityOnHand decimal(18,4) NOT NULL CONSTRAINT DF_RawMaterialStocks_Qty DEFAULT(0)
            );
            CREATE UNIQUE INDEX IX_RawMaterialStocks_MaterialSite ON RawMaterialStocks(RawMaterialId, SiteId);
        END
        IF OBJECT_ID('RawMaterialMovements', 'U') IS NULL
        BEGIN
            CREATE TABLE RawMaterialMovements
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                MovementNo nvarchar(40) NOT NULL,
                RawMaterialId uniqueidentifier NOT NULL,
                SiteId uniqueidentifier NOT NULL,
                MovementType nvarchar(20) NOT NULL,
                Quantity decimal(18,4) NOT NULL,
                UnitCost decimal(18,4) NOT NULL CONSTRAINT DF_RawMaterialMovements_UnitCost DEFAULT(0),
                TotalCost decimal(18,2) NOT NULL CONSTRAINT DF_RawMaterialMovements_TotalCost DEFAULT(0),
                MovementDateUtc datetime2 NOT NULL,
                Note nvarchar(500) NOT NULL CONSTRAINT DF_RawMaterialMovements_Note DEFAULT(''),
                SupplierPurchaseId uniqueidentifier NULL,
                CuttingEntryId uniqueidentifier NULL,
                FinishingEntryId uniqueidentifier NULL,
                CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_RawMaterialMovements_CutLotCode DEFAULT(''),
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_RawMaterialMovements_MovementNo ON RawMaterialMovements(MovementNo);
            CREATE INDEX IX_RawMaterialMovements_MovementDateUtc ON RawMaterialMovements(MovementDateUtc);
            CREATE INDEX IX_RawMaterialMovements_RawMaterialId ON RawMaterialMovements(RawMaterialId);
            CREATE INDEX IX_RawMaterialMovements_SiteId ON RawMaterialMovements(SiteId);
        END
        IF COL_LENGTH('CuttingEntries', 'RawMaterialId') IS NULL
            ALTER TABLE CuttingEntries ADD RawMaterialId uniqueidentifier NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CuttingEntries_RawMaterialId' AND object_id = OBJECT_ID('CuttingEntries'))
            CREATE INDEX IX_CuttingEntries_RawMaterialId ON CuttingEntries(RawMaterialId);
        IF COL_LENGTH('RawMaterialMovements', 'FinishingEntryId') IS NULL
            ALTER TABLE RawMaterialMovements ADD FinishingEntryId uniqueidentifier NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawMaterialMovements_FinishingEntryId' AND object_id = OBJECT_ID('RawMaterialMovements'))
            CREATE INDEX IX_RawMaterialMovements_FinishingEntryId ON RawMaterialMovements(FinishingEntryId);
        IF COL_LENGTH('RawMaterialMovements', 'ScrapSaleId') IS NULL
            ALTER TABLE RawMaterialMovements ADD ScrapSaleId uniqueidentifier NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawMaterialMovements_ScrapSaleId' AND object_id = OBJECT_ID('RawMaterialMovements'))
            CREATE INDEX IX_RawMaterialMovements_ScrapSaleId ON RawMaterialMovements(ScrapSaleId);

        IF OBJECT_ID('RawMaterialScrapSales', 'U') IS NULL
        BEGIN
            CREATE TABLE RawMaterialScrapSales
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SaleNo nvarchar(40) NOT NULL,
                SiteId uniqueidentifier NOT NULL,
                RawMaterialId uniqueidentifier NOT NULL,
                ScrapType nvarchar(20) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_ScrapType DEFAULT('Wastage'),
                Quantity decimal(18,4) NOT NULL,
                Unit nvarchar(20) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Unit DEFAULT('kg'),
                UnitRate decimal(18,4) NOT NULL,
                TotalAmount decimal(18,2) NOT NULL,
                BuyerName nvarchar(120) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Buyer DEFAULT(''),
                IsCredit bit NOT NULL CONSTRAINT DF_RawMaterialScrapSales_IsCredit DEFAULT(0),
                PaidAmount decimal(18,2) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Paid DEFAULT(0),
                DueAmount decimal(18,2) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Due DEFAULT(0),
                Note nvarchar(250) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Note DEFAULT(''),
                SoldAtUtc datetime2 NOT NULL,
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_RawMaterialScrapSales_SaleNo ON RawMaterialScrapSales(SaleNo);
            CREATE INDEX IX_RawMaterialScrapSales_SoldAtUtc ON RawMaterialScrapSales(SoldAtUtc);
            CREATE INDEX IX_RawMaterialScrapSales_SiteId ON RawMaterialScrapSales(SiteId);
        END
        IF COL_LENGTH('RawMaterialScrapSales', 'InventoryItemId') IS NULL
            ALTER TABLE RawMaterialScrapSales ADD InventoryItemId uniqueidentifier NULL;
        IF COL_LENGTH('RawMaterialScrapSales', 'RawMaterialId') IS NOT NULL
            ALTER TABLE RawMaterialScrapSales ALTER COLUMN RawMaterialId uniqueidentifier NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawMaterialScrapSales_InventoryItemId' AND object_id = OBJECT_ID('RawMaterialScrapSales'))
            CREATE INDEX IX_RawMaterialScrapSales_InventoryItemId ON RawMaterialScrapSales(InventoryItemId);

        IF OBJECT_ID('Employees', 'U') IS NULL
        BEGIN
            CREATE TABLE Employees
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                EmployeeCode nvarchar(50) NOT NULL,
                Name nvarchar(200) NOT NULL,
                EmployeeType nvarchar(20) NOT NULL CONSTRAINT DF_Employees_EmployeeType DEFAULT('SalesCenter'),
                MonthlySalary decimal(18,2) NOT NULL CONSTRAINT DF_Employees_MonthlySalary DEFAULT(0),
                MobileNumber nvarchar(30) NOT NULL,
                NationalIdNumber nvarchar(60) NOT NULL,
                NationalIdImageBase64 nvarchar(max) NOT NULL CONSTRAINT DF_Employees_NationalIdImageBase64 DEFAULT(''),
                Address nvarchar(400) NOT NULL CONSTRAINT DF_Employees_Address DEFAULT(''),
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_Employees_EmployeeCode ON Employees(EmployeeCode);
            CREATE UNIQUE INDEX IX_Employees_NationalIdNumber ON Employees(NationalIdNumber);
        END
        IF COL_LENGTH('Employees', 'EmployeeType') IS NULL
            ALTER TABLE Employees ADD EmployeeType nvarchar(20) NOT NULL CONSTRAINT DF_Employees_EmployeeType2 DEFAULT('SalesCenter');
        IF COL_LENGTH('Employees', 'MonthlySalary') IS NULL
            ALTER TABLE Employees ADD MonthlySalary decimal(18,2) NOT NULL CONSTRAINT DF_Employees_MonthlySalary2 DEFAULT(0);
        IF COL_LENGTH('Employees', 'EmployeeCategory') IS NULL
            ALTER TABLE Employees ADD EmployeeCategory nvarchar(40) NOT NULL CONSTRAINT DF_Employees_EmployeeCategory DEFAULT('');
        IF COL_LENGTH('Employees', 'SiteId') IS NULL
            ALTER TABLE Employees ADD SiteId uniqueidentifier NULL;
        IF COL_LENGTH('Employees', 'IsActive') IS NULL
            ALTER TABLE Employees ADD IsActive bit NOT NULL CONSTRAINT DF_Employees_IsActive DEFAULT(1);
        IF COL_LENGTH('Employees', 'LeftAtUtc') IS NULL
            ALTER TABLE Employees ADD LeftAtUtc datetime2 NULL;
        IF COL_LENGTH('Employees', 'SerialNumber') IS NULL
            ALTER TABLE Employees ADD SerialNumber int NOT NULL CONSTRAINT DF_Employees_SerialNumber DEFAULT(0);

        IF COL_LENGTH('Sites', 'IsActive') IS NULL
            ALTER TABLE Sites ADD IsActive bit NOT NULL CONSTRAINT DF_Sites_IsActive DEFAULT(1);
        IF COL_LENGTH('Sites', 'ClosedAtUtc') IS NULL
            ALTER TABLE Sites ADD ClosedAtUtc datetime2 NULL;

        IF COL_LENGTH('InventoryItems', 'ProductionCost') IS NULL
            ALTER TABLE InventoryItems ADD ProductionCost decimal(18,2) NOT NULL CONSTRAINT DF_InventoryItems_ProductionCost DEFAULT(0);

        IF OBJECT_ID('SiteMonthlyRents', 'U') IS NULL
        BEGIN
            CREATE TABLE SiteMonthlyRents
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SiteId uniqueidentifier NOT NULL,
                MonthlyRent decimal(18,2) NOT NULL CONSTRAINT DF_SiteMonthlyRents_MonthlyRent DEFAULT(0),
                LandlordName nvarchar(200) NOT NULL CONSTRAINT DF_SiteMonthlyRents_LandlordName DEFAULT(''),
                UpdatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_SiteMonthlyRents_SiteId ON SiteMonthlyRents(SiteId);
        END

        IF OBJECT_ID('ProductStyles', 'U') IS NULL
        BEGIN
            CREATE TABLE ProductStyles
            (
                Prefix nvarchar(10) NOT NULL PRIMARY KEY,
                Name nvarchar(120) NOT NULL,
                ProductionCost decimal(18,2) NOT NULL CONSTRAINT DF_ProductStyles_ProductionCost DEFAULT(0),
                CreatedAtUtc datetime2 NOT NULL,
                UpdatedAtUtc datetime2 NOT NULL
            );
        END

        IF OBJECT_ID('PayrollRuns', 'U') IS NULL
        BEGIN
            CREATE TABLE PayrollRuns
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                MonthKey nvarchar(7) NOT NULL,
                ExpenseScope nvarchar(20) NOT NULL CONSTRAINT DF_PayrollRuns_ExpenseScope DEFAULT('Factory'),
                SiteId uniqueidentifier NULL,
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE INDEX IX_PayrollRuns_MonthScopeSite ON PayrollRuns(MonthKey, ExpenseScope, SiteId);
        END

        IF OBJECT_ID('PayrollLines', 'U') IS NULL
        BEGIN
            CREATE TABLE PayrollLines
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                PayrollRunId uniqueidentifier NOT NULL,
                EmployeeId uniqueidentifier NOT NULL,
                EmployeeName nvarchar(200) NOT NULL CONSTRAINT DF_PayrollLines_EmployeeName DEFAULT(''),
                MonthlySalary decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_MonthlySalary DEFAULT(0),
                AdvancePaid decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_AdvancePaid DEFAULT(0),
                CurrentPaid decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_CurrentPaid DEFAULT(0),
                DuePaid decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_DuePaid DEFAULT(0),
                NetPayable decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_NetPayable DEFAULT(0),
                Status nvarchar(20) NOT NULL CONSTRAINT DF_PayrollLines_Status DEFAULT('Unpaid'),
                UpdatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_PayrollLines_RunEmployee ON PayrollLines(PayrollRunId, EmployeeId);
        END
        IF COL_LENGTH('PayrollLines', 'OvertimeHours') IS NULL
            ALTER TABLE PayrollLines ADD OvertimeHours decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_OvertimeHours DEFAULT(0);
        IF COL_LENGTH('PayrollLines', 'OvertimeAmount') IS NULL
            ALTER TABLE PayrollLines ADD OvertimeAmount decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_OvertimeAmount DEFAULT(0);
        IF COL_LENGTH('PayrollLines', 'AttendanceDays') IS NULL
            ALTER TABLE PayrollLines ADD AttendanceDays decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_AttendanceDays DEFAULT(0);
        IF COL_LENGTH('PayrollLines', 'AttendanceSalaryAmount') IS NULL
            ALTER TABLE PayrollLines ADD AttendanceSalaryAmount decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_AttendanceSalaryAmount DEFAULT(0);
        IF COL_LENGTH('PayrollLines', 'AttendanceBonus') IS NULL
            ALTER TABLE PayrollLines ADD AttendanceBonus decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_AttendanceBonus DEFAULT(0);
        IF COL_LENGTH('PayrollLines', 'SnakesPay') IS NULL
            ALTER TABLE PayrollLines ADD SnakesPay decimal(18,2) NOT NULL CONSTRAINT DF_PayrollLines_SnakesPay DEFAULT(0);
        IF COL_LENGTH('PayrollLines', 'EmployeeCategory') IS NULL
            ALTER TABLE PayrollLines ADD EmployeeCategory nvarchar(40) NOT NULL CONSTRAINT DF_PayrollLines_EmployeeCategory DEFAULT('');

        IF OBJECT_ID('FactoryAttendanceDays', 'U') IS NULL
        BEGIN
            CREATE TABLE FactoryAttendanceDays
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                EmployeeId uniqueidentifier NOT NULL,
                MonthKey nvarchar(7) NOT NULL,
                AttendanceDateUtc datetime2 NOT NULL,
                MarkCode nvarchar(1) NOT NULL CONSTRAINT DF_FactoryAttendanceDays_MarkCode DEFAULT('P'),
                DayValue decimal(18,2) NOT NULL CONSTRAINT DF_FactoryAttendanceDays_DayValue DEFAULT(1),
                UpdatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_FactoryAttendanceDays_EmployeeDate ON FactoryAttendanceDays(EmployeeId, AttendanceDateUtc);
            CREATE INDEX IX_FactoryAttendanceDays_MonthKey ON FactoryAttendanceDays(MonthKey);
        END
        IF COL_LENGTH('FactoryAttendanceDays', 'DayValue') IS NULL
            ALTER TABLE FactoryAttendanceDays ADD DayValue decimal(18,2) NOT NULL CONSTRAINT DF_FactoryAttendanceDays_DayValue DEFAULT(1);
        IF COL_LENGTH('FactoryAttendanceDays', 'MarkCode') IS NULL
            ALTER TABLE FactoryAttendanceDays ADD MarkCode nvarchar(1) NOT NULL CONSTRAINT DF_FactoryAttendanceDays_MarkCode DEFAULT('P');

        IF OBJECT_ID('PrintFactoryPurchases', 'U') IS NULL
        BEGIN
            CREATE TABLE PrintFactoryPurchases
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                VoucherNo nvarchar(50) NOT NULL,
                SupplierId uniqueidentifier NOT NULL,
                SupplierName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Purch_SupplierName DEFAULT(''),
                InvoiceRef nvarchar(80) NOT NULL CONSTRAINT DF_PF_Purch_InvoiceRef DEFAULT(''),
                Description nvarchar(250) NOT NULL CONSTRAINT DF_PF_Purch_Desc DEFAULT(''),
                TotalAmount decimal(18,2) NOT NULL,
                PaidAmount decimal(18,2) NOT NULL CONSTRAINT DF_PF_Purch_Paid DEFAULT(0),
                DueAmount decimal(18,2) NOT NULL,
                PurchasedAtUtc datetime2 NOT NULL,
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_PrintFactoryPurchases_VoucherNo ON PrintFactoryPurchases(VoucherNo);
            CREATE INDEX IX_PrintFactoryPurchases_PurchasedAtUtc ON PrintFactoryPurchases(PurchasedAtUtc);
        END
        IF COL_LENGTH('PrintFactoryPurchases', 'SupplierName') IS NULL
            ALTER TABLE PrintFactoryPurchases ADD SupplierName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Purch_SupplierName DEFAULT('');
        IF OBJECT_ID('PrintFactoryPurchaseLines', 'U') IS NULL
        BEGIN
            CREATE TABLE PrintFactoryPurchaseLines
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                PrintFactoryPurchaseId uniqueidentifier NOT NULL,
                Description nvarchar(200) NOT NULL,
                Quantity decimal(18,4) NOT NULL,
                Unit nvarchar(20) NOT NULL CONSTRAINT DF_PF_PurchLine_Unit DEFAULT('pcs'),
                UnitCost decimal(18,4) NOT NULL,
                LineTotal decimal(18,2) NOT NULL
            );
            CREATE INDEX IX_PrintFactoryPurchaseLines_PurchaseId ON PrintFactoryPurchaseLines(PrintFactoryPurchaseId);
        END
        IF OBJECT_ID('PrintFactorySales', 'U') IS NULL
        BEGIN
            CREATE TABLE PrintFactorySales
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                VoucherNo nvarchar(50) NOT NULL,
                BuyerType nvarchar(20) NOT NULL CONSTRAINT DF_PF_Sale_BuyerType DEFAULT('Internal'),
                BuyerName nvarchar(200) NOT NULL,
                InvoiceRef nvarchar(80) NOT NULL CONSTRAINT DF_PF_Sale_InvoiceRef DEFAULT(''),
                Description nvarchar(250) NOT NULL CONSTRAINT DF_PF_Sale_Desc DEFAULT(''),
                TotalAmount decimal(18,2) NOT NULL,
                ReceivedAmount decimal(18,2) NOT NULL CONSTRAINT DF_PF_Sale_Received DEFAULT(0),
                DueAmount decimal(18,2) NOT NULL,
                SoldAtUtc datetime2 NOT NULL,
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_PrintFactorySales_VoucherNo ON PrintFactorySales(VoucherNo);
            CREATE INDEX IX_PrintFactorySales_SoldAtUtc ON PrintFactorySales(SoldAtUtc);
        END
        IF OBJECT_ID('PrintFactorySaleLines', 'U') IS NULL
        BEGIN
            CREATE TABLE PrintFactorySaleLines
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                PrintFactorySaleId uniqueidentifier NOT NULL,
                Description nvarchar(200) NOT NULL,
                Quantity decimal(18,4) NOT NULL,
                Unit nvarchar(20) NOT NULL CONSTRAINT DF_PF_SaleLine_Unit DEFAULT('pcs'),
                UnitRate decimal(18,4) NOT NULL,
                LineTotal decimal(18,2) NOT NULL
            );
            CREATE INDEX IX_PrintFactorySaleLines_SaleId ON PrintFactorySaleLines(PrintFactorySaleId);
        END
        IF OBJECT_ID('PrintFactoryCashEntries', 'U') IS NULL
        BEGIN
            CREATE TABLE PrintFactoryCashEntries
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                VoucherNo nvarchar(50) NOT NULL,
                EntryType nvarchar(20) NOT NULL,
                PurchaseId uniqueidentifier NULL,
                SaleId uniqueidentifier NULL,
                PartyName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Cash_Party DEFAULT(''),
                Amount decimal(18,2) NOT NULL,
                Note nvarchar(250) NOT NULL CONSTRAINT DF_PF_Cash_Note DEFAULT(''),
                EntryDateUtc datetime2 NOT NULL,
                ExpenseEntryId uniqueidentifier NULL
            );
            CREATE UNIQUE INDEX IX_PrintFactoryCashEntries_VoucherNo ON PrintFactoryCashEntries(VoucherNo);
            CREATE INDEX IX_PrintFactoryCashEntries_EntryDateUtc ON PrintFactoryCashEntries(EntryDateUtc);
        END

        IF OBJECT_ID('SalesReturns', 'U') IS NULL
        BEGIN
            CREATE TABLE SalesReturns
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ReturnNo nvarchar(40) NOT NULL,
                InvoiceNo nvarchar(46) NULL,
                SiteId uniqueidentifier NOT NULL,
                InventoryItemId uniqueidentifier NOT NULL,
                CustomerType nvarchar(20) NOT NULL CONSTRAINT DF_SalesReturns_CustomerType DEFAULT('Regular'),
                CustomerName nvarchar(120) NOT NULL CONSTRAINT DF_SalesReturns_CustomerName DEFAULT(''),
                Quantity int NOT NULL,
                UnitPrice decimal(18,2) NOT NULL,
                TotalAmount decimal(18,2) NOT NULL,
                ReturnType nvarchar(20) NOT NULL CONSTRAINT DF_SalesReturns_ReturnType DEFAULT('NoInvoice'),
                ActionType nvarchar(20) NOT NULL CONSTRAINT DF_SalesReturns_ActionType DEFAULT('Exchange'),
                RefundAmount decimal(18,2) NOT NULL CONSTRAINT DF_SalesReturns_RefundAmount DEFAULT(0),
                Reason nvarchar(250) NOT NULL CONSTRAINT DF_SalesReturns_Reason DEFAULT(''),
                ReturnedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_SalesReturns_ReturnNo ON SalesReturns(ReturnNo);
        END
        IF COL_LENGTH('SalesReturns', 'CustomerType') IS NULL
            ALTER TABLE SalesReturns ADD CustomerType nvarchar(20) NOT NULL CONSTRAINT DF_SalesReturns_CustomerType2 DEFAULT('Regular');
        IF COL_LENGTH('SalesReturns', 'DueCreditApplied') IS NULL
            ALTER TABLE SalesReturns ADD DueCreditApplied decimal(18,2) NOT NULL CONSTRAINT DF_SalesReturns_DueCreditApplied DEFAULT(0);

        IF OBJECT_ID('FinishedItemGiftIssues', 'U') IS NULL
        BEGIN
            CREATE TABLE FinishedItemGiftIssues
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                GiftNo nvarchar(40) NOT NULL,
                SiteId uniqueidentifier NOT NULL,
                InventoryItemId uniqueidentifier NOT NULL,
                Quantity int NOT NULL,
                UnitCost decimal(18,2) NOT NULL,
                TotalCost decimal(18,2) NOT NULL,
                RecipientName nvarchar(120) NOT NULL CONSTRAINT DF_FinishedItemGiftIssues_Recipient DEFAULT(''),
                Reason nvarchar(250) NOT NULL CONSTRAINT DF_FinishedItemGiftIssues_Reason DEFAULT(''),
                IssuedAtUtc datetime2 NOT NULL,
                CreatedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_FinishedItemGiftIssues_GiftNo ON FinishedItemGiftIssues(GiftNo);
            CREATE INDEX IX_FinishedItemGiftIssues_IssuedAtUtc ON FinishedItemGiftIssues(IssuedAtUtc);
            CREATE INDEX IX_FinishedItemGiftIssues_SiteId ON FinishedItemGiftIssues(SiteId);
        END

        IF OBJECT_ID('CuttingEntries', 'U') IS NULL
        BEGIN
            CREATE TABLE CuttingEntries
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                CuttingNo nvarchar(40) NOT NULL,
                FactorySiteId uniqueidentifier NOT NULL,
                CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_CuttingEntries_InitCutLot DEFAULT(''),
                InventoryItemId uniqueidentifier NULL,
                QuantityCut int NOT NULL,
                FabricKg decimal(18,4) NOT NULL,
                FabricPricePerKg decimal(18,4) NOT NULL,
                FabricAmount decimal(18,2) NOT NULL,
                CutAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_CuttingEntries_CuttingNo ON CuttingEntries(CuttingNo);
            CREATE INDEX IX_CuttingEntries_CutAtUtc ON CuttingEntries(CutAtUtc);
            CREATE INDEX IX_CuttingEntries_Item ON CuttingEntries(InventoryItemId);
        END

        IF OBJECT_ID('SewingEntries', 'U') IS NULL
        BEGIN
            CREATE TABLE SewingEntries
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                SewingNo nvarchar(40) NOT NULL,
                FactorySiteId uniqueidentifier NOT NULL,
                CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_SewingEntries_InitCutLot DEFAULT(''),
                InventoryItemId uniqueidentifier NULL,
                QuantitySewn int NOT NULL,
                SewnAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_SewingEntries_SewingNo ON SewingEntries(SewingNo);
            CREATE INDEX IX_SewingEntries_SewnAtUtc ON SewingEntries(SewnAtUtc);
            CREATE INDEX IX_SewingEntries_Item ON SewingEntries(InventoryItemId);
        END

        IF OBJECT_ID('FinishingEntries', 'U') IS NULL
        BEGIN
            CREATE TABLE FinishingEntries
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                FinishingNo nvarchar(40) NOT NULL,
                FactorySiteId uniqueidentifier NOT NULL,
                CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_FinishingEntries_InitCutLot DEFAULT(''),
                InventoryItemId uniqueidentifier NOT NULL,
                QuantityFinished int NOT NULL,
                FinishedAtUtc datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX IX_FinishingEntries_FinishingNo ON FinishingEntries(FinishingNo);
            CREATE INDEX IX_FinishingEntries_FinishedAtUtc ON FinishingEntries(FinishedAtUtc);
            CREATE INDEX IX_FinishingEntries_Item ON FinishingEntries(InventoryItemId);
        END

        IF COL_LENGTH('CuttingEntries', 'CutLotCode') IS NULL
            ALTER TABLE CuttingEntries ADD CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_CuttingEntries_CutLotCode DEFAULT('');
        IF COL_LENGTH('SewingEntries', 'CutLotCode') IS NULL
            ALTER TABLE SewingEntries ADD CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_SewingEntries_CutLotCode DEFAULT('');
        IF COL_LENGTH('FinishingEntries', 'CutLotCode') IS NULL
            ALTER TABLE FinishingEntries ADD CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_FinishingEntries_CutLotCode DEFAULT('');

        IF OBJECT_ID('StockTransfers', 'U') IS NOT NULL AND COL_LENGTH('StockTransfers', 'DocumentNo') IS NULL
            ALTER TABLE StockTransfers ADD DocumentNo nvarchar(50) NOT NULL CONSTRAINT DF_StockTransfers_DocumentNo DEFAULT('');

        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CuttingEntries') AND name = 'InventoryItemId' AND is_nullable = 0)
            ALTER TABLE CuttingEntries ALTER COLUMN InventoryItemId uniqueidentifier NULL;
        IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SewingEntries') AND name = 'InventoryItemId' AND is_nullable = 0)
            ALTER TABLE SewingEntries ALTER COLUMN InventoryItemId uniqueidentifier NULL;
        """);
    // SerialNumber backfill must run in a separate batch (SQL Server cannot reference a column added in the same batch).
    db.Database.ExecuteSqlRaw("""
        IF COL_LENGTH('Employees', 'SerialNumber') IS NOT NULL
        BEGIN
            ;WITH ranked AS (
                SELECT Id,
                    ROW_NUMBER() OVER (
                        PARTITION BY EmployeeType, ISNULL(SiteId, '00000000-0000-0000-0000-000000000000')
                        ORDER BY CreatedAtUtc, Name) AS rn
                FROM Employees
                WHERE SerialNumber = 0
            )
            UPDATE e SET SerialNumber = r.rn
            FROM Employees e
            INNER JOIN ranked r ON e.Id = r.Id
            WHERE e.SerialNumber = 0;
        END
        """);
    db.Database.ExecuteSqlRaw("""
        IF OBJECT_ID('PrintFactoryPurchases', 'U') IS NOT NULL
           AND COL_LENGTH('PrintFactoryPurchases', 'SupplierName') IS NULL
            ALTER TABLE PrintFactoryPurchases ADD SupplierName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Purch_SupplierName2 DEFAULT('');
        """);
    db.Database.ExecuteSqlRaw("""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CuttingEntries_Factory_CutLot' AND object_id = OBJECT_ID('CuttingEntries'))
        BEGIN
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CuttingEntries_Factory_CutLotCode' AND object_id = OBJECT_ID('CuttingEntries'))
                DROP INDEX IX_CuttingEntries_Factory_CutLotCode ON CuttingEntries;
            IF NOT EXISTS (
                SELECT FactorySiteId, CutLotCode
                FROM CuttingEntries
                WHERE CutLotCode IS NOT NULL AND LEN(LTRIM(RTRIM(CutLotCode))) > 0
                GROUP BY FactorySiteId, CutLotCode
                HAVING COUNT(*) > 1
            )
                CREATE UNIQUE INDEX UX_CuttingEntries_Factory_CutLot ON CuttingEntries(FactorySiteId, CutLotCode) WHERE CutLotCode <> N'';
        END
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SewingEntries_Factory_CutLotCode' AND object_id = OBJECT_ID('SewingEntries'))
            CREATE INDEX IX_SewingEntries_Factory_CutLotCode ON SewingEntries(FactorySiteId, CutLotCode);

        INSERT INTO InventoryStocks (Id, InventoryItemId, SiteId, Quantity)
        SELECT NEWID(), i.Id, s.Id, 0
        FROM InventoryItems i
        CROSS JOIN Sites s
        WHERE NOT EXISTS (
            SELECT 1
            FROM InventoryStocks st
            WHERE st.InventoryItemId = i.Id AND st.SiteId = s.Id
        );
        """);
    // MarkCode backfill must run in a separate batch (SQL Server cannot reference a column added in the same batch).
    db.Database.ExecuteSqlRaw("""
        IF OBJECT_ID('FactoryAttendanceDays', 'U') IS NOT NULL
           AND COL_LENGTH('FactoryAttendanceDays', 'MarkCode') IS NOT NULL
           AND COL_LENGTH('FactoryAttendanceDays', 'DayValue') IS NOT NULL
        BEGIN
            UPDATE FactoryAttendanceDays SET MarkCode = 'P' WHERE DayValue > 0 AND MarkCode <> 'P';
            UPDATE FactoryAttendanceDays SET MarkCode = 'A', DayValue = 0 WHERE DayValue <= 0 AND MarkCode <> 'A';
        END
        """);
    db.Database.ExecuteSqlRaw("""
        UPDATE InventoryItems SET PrintItemMarkedAtUtc = CreatedAtUtc WHERE IsPrintItem = 1 AND PrintItemMarkedAtUtc IS NULL;
        UPDATE st SET IsPrintItemAtSale = 1, PrintChargePerPieceAtSale = i.PrintChargePerPiece
        FROM SalesTransactions st
        INNER JOIN InventoryItems i ON st.InventoryItemId = i.Id
        WHERE i.IsPrintItem = 1 AND i.PrintItemMarkedAtUtc IS NOT NULL
            AND st.SoldAtUtc >= i.PrintItemMarkedAtUtc
            AND st.IsPrintItemAtSale = 0 AND st.PrintChargePerPieceAtSale = 0;
        """);

    try
    {
        _ = await db.RawMaterials.CountAsync();
        var fabMixDeactivated = await db.Database.ExecuteSqlRawAsync("""
            UPDATE RawMaterials SET IsActive = 0 WHERE Code = 'FAB-MIX' AND IsActive = 1;
            """);
        if (fabMixDeactivated > 0)
            Console.WriteLine("[BongoTex] Deactivated legacy raw material FAB-MIX.");
        Console.WriteLine("[BongoTex] Raw material inventory tables are ready.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BongoTex] WARNING: Raw material tables missing or inaccessible: {ex.Message}");
        Console.WriteLine("[BongoTex] Restart the API after build; startup SQL should create RawMaterials / RawMaterialStocks / RawMaterialMovements.");
    }

    try
    {
        await PrintFactorySchema.EnsureAsync(db);
        Console.WriteLine("[BongoTex] Print Factory tables are ready.");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[BongoTex] WARNING: Print Factory schema: {ex.InnerException?.Message ?? ex.Message}");
        Console.ResetColor();
    }

    try
    {
        await RawMaterialScrapSchema.EnsureAsync(db);
        Console.WriteLine("[BongoTex] Raw material scrap sale tables are ready.");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[BongoTex] WARNING: Raw material scrap schema: {ex.InnerException?.Message ?? ex.Message}");
        Console.ResetColor();
    }

    try
    {
        await ProductStyleOps.EnsureDefaultsAsync(db);
        Console.WriteLine("[BongoTex] Product style master (prefix + production cost) is ready.");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[BongoTex] WARNING: ProductStyles schema: {ex.InnerException?.Message ?? ex.Message}");
        Console.ResetColor();
    }

    try
    {
        db.Database.ExecuteSqlRaw("""
        IF OBJECT_ID('AppUsers', 'U') IS NULL
        BEGIN
            CREATE TABLE AppUsers
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                Username nvarchar(60) NOT NULL,
                DisplayName nvarchar(120) NOT NULL CONSTRAINT DF_AppUsers_DisplayName DEFAULT(''),
                PasswordHash nvarchar(200) NOT NULL,
                Role nvarchar(20) NOT NULL,
                IsActive bit NOT NULL CONSTRAINT DF_AppUsers_IsActive DEFAULT(1),
                CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_AppUsers_CreatedAtUtc DEFAULT(GETUTCDATE()),
                LastLoginUtc datetime2 NULL
            );
            CREATE UNIQUE INDEX IX_AppUsers_Username ON AppUsers(Username);
        END
        """);

        var hasActiveAdmin = await db.AppUsers.AnyAsync(u => u.Role == "Admin" && u.IsActive);
        if (!hasActiveAdmin)
        {
            var admin = await db.AppUsers.FirstOrDefaultAsync(u => u.Username == "admin");
            if (admin == null)
            {
                db.AppUsers.Add(new AppUser
                {
                    Username = "admin",
                    DisplayName = "Administrator",
                    Role = "Admin",
                    IsActive = true,
                    PasswordHash = PasswordHasher.Hash("admin123")
                });
            }
            else
            {
                admin.Role = "Admin";
                admin.IsActive = true;
                admin.PasswordHash = PasswordHasher.Hash("admin123");
            }
            await db.SaveChangesAsync();
            Console.WriteLine("[BongoTex] Ensured default admin (username: admin / password: admin123). Change this password after first login.");
        }
        Console.WriteLine("[BongoTex] User accounts table is ready.");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[BongoTex] WARNING: AppUsers schema: {ex.InnerException?.Message ?? ex.Message}");
        Console.ResetColor();
    }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[BongoTex] STARTUP FAILED — database migration error:");
        Console.WriteLine(ex.InnerException?.Message ?? ex.Message);
        Console.ResetColor();
        throw;
    }
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
        }
    }
});

// ---------------- Authentication gate + role-based access ----------------
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var lower = path.ToLowerInvariant();

    // Public endpoints (no login required)
    if (lower == "/api/health" || lower == "/api/auth/login")
    {
        await next();
        return;
    }

    var session = SessionStore.Get(SessionStore.ExtractToken(context));
    if (session == null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Not authenticated. Please sign in." });
        return;
    }
    context.Items["AuthSession"] = session;
    var role = session.Role;

    // Personal account endpoints - any signed-in user
    if (lower.StartsWith("/api/auth/"))
    {
        await next();
        return;
    }

    // Admin-only areas: user management, data reset, site setup
    bool adminOnly = lower.StartsWith("/api/users")
        || lower.StartsWith("/api/admin/")
        || lower.StartsWith("/api/setup/");
    if (adminOnly)
    {
        if (role != Roles.Admin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Admin access required." });
            return;
        }
        await next();
        return;
    }

    // Reads: any signed-in user may view
    var method = context.Request.Method;
    if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
    {
        await next();
        return;
    }

    // Writes: Admin + Manager everywhere; Accountant only on finance/accounting
    bool financeWrite = lower.StartsWith("/api/expense-entries")
        || lower.StartsWith("/api/finance/")
        || lower.StartsWith("/api/cash-movements")
        || lower.StartsWith("/api/payroll/")
        || (lower.StartsWith("/api/sales-transactions/") && lower.EndsWith("/collections"));

    bool allowed = role == Roles.Admin
        || role == Roles.Manager
        || (role == Roles.Accountant && financeWrite);

    if (!allowed)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "You do not have permission for this action." });
        return;
    }

    await next();
});

app.MapGet("/api/health", async (BongoTexDbContext db) =>
{
    var rawMaterialTablesOk = true;
    string? rawMaterialNote = null;
    var scrapSalesTablesOk = true;
    string? scrapSalesNote = null;
    try
    {
        _ = await db.RawMaterials.CountAsync();
    }
    catch (Exception ex)
    {
        rawMaterialTablesOk = false;
        rawMaterialNote = ex.Message;
    }
    try
    {
        _ = await db.RawMaterialScrapSales.CountAsync();
    }
    catch (Exception ex)
    {
        scrapSalesTablesOk = false;
        scrapSalesNote = ex.Message;
    }

    return Results.Ok(new
    {
        App = "BongoTex ERP API",
        Status = "Running",
        TimeUtc = DateTime.UtcNow,
        RawMaterialTablesOk = rawMaterialTablesOk,
        RawMaterialNote = rawMaterialNote,
        ScrapSalesApiReady = scrapSalesTablesOk,
        ScrapSalesNote = scrapSalesNote
    });
});

// ---------------- Authentication & user management ----------------
async Task<bool> OtherActiveAdminExists(BongoTexDbContext database, Guid excludeId)
    => await database.AppUsers.AnyAsync(u => u.Id != excludeId && u.Role == Roles.Admin && u.IsActive);

app.MapPost("/api/auth/login", async (LoginRequest req, BongoTexDbContext db) =>
{
    var username = (req.Username ?? string.Empty).Trim();
    var password = req.Password ?? string.Empty;
    if (username.Length == 0 || password.Length == 0)
        return Results.Json(new { error = "Username and password are required." }, statusCode: StatusCodes.Status400BadRequest);

    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
    if (user == null || !user.IsActive || !PasswordHasher.Verify(password, user.PasswordHash))
        return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);

    var token = SessionStore.Create(new AuthSession
    {
        UserId = user.Id,
        Username = user.Username,
        Role = user.Role,
        DisplayName = user.DisplayName
    });
    user.LastLoginUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { token, username = user.Username, displayName = user.DisplayName, role = user.Role });
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    SessionStore.Remove(SessionStore.ExtractToken(ctx));
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    if (ctx.Items["AuthSession"] is not AuthSession s)
        return Results.Json(new { error = "Not authenticated." }, statusCode: StatusCodes.Status401Unauthorized);
    return Results.Ok(new { username = s.Username, displayName = s.DisplayName, role = s.Role });
});

app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, HttpContext ctx, BongoTexDbContext db) =>
{
    if (ctx.Items["AuthSession"] is not AuthSession s)
        return Results.Json(new { error = "Not authenticated." }, statusCode: StatusCodes.Status401Unauthorized);
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == s.UserId);
    if (user == null)
        return Results.Json(new { error = "User not found." }, statusCode: StatusCodes.Status404NotFound);
    if (!PasswordHasher.Verify(req.CurrentPassword ?? string.Empty, user.PasswordHash))
        return Results.Json(new { error = "Current password is incorrect." }, statusCode: StatusCodes.Status400BadRequest);
    if ((req.NewPassword ?? string.Empty).Length < 4)
        return Results.Json(new { error = "New password must be at least 4 characters." }, statusCode: StatusCodes.Status400BadRequest);
    user.PasswordHash = PasswordHasher.Hash(req.NewPassword!);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/users", async (BongoTexDbContext db) =>
{
    var users = await db.AppUsers.OrderBy(u => u.Username)
        .Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.CreatedAtUtc, u.LastLoginUtc })
        .ToListAsync();
    return Results.Ok(users);
});

app.MapPost("/api/users", async (CreateUserRequest req, BongoTexDbContext db) =>
{
    var username = (req.Username ?? string.Empty).Trim();
    if (username.Length < 3)
        return Results.Json(new { error = "Username must be at least 3 characters." }, statusCode: StatusCodes.Status400BadRequest);
    if ((req.Password ?? string.Empty).Length < 4)
        return Results.Json(new { error = "Password must be at least 4 characters." }, statusCode: StatusCodes.Status400BadRequest);
    if (!Roles.IsValid(req.Role))
        return Results.Json(new { error = "Role must be Admin, Manager, or Accountant." }, statusCode: StatusCodes.Status400BadRequest);
    if (await db.AppUsers.AnyAsync(u => u.Username == username))
        return Results.Json(new { error = "Username already exists." }, statusCode: StatusCodes.Status400BadRequest);

    var user = new AppUser
    {
        Username = username,
        DisplayName = (req.DisplayName ?? string.Empty).Trim(),
        Role = req.Role!,
        IsActive = true,
        PasswordHash = PasswordHasher.Hash(req.Password!)
    };
    db.AppUsers.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { user.Id, user.Username, user.DisplayName, user.Role, user.IsActive });
});

app.MapPut("/api/users/{id:guid}", async (Guid id, UpdateUserRequest req, BongoTexDbContext db) =>
{
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == id);
    if (user == null)
        return Results.Json(new { error = "User not found." }, statusCode: StatusCodes.Status404NotFound);

    if (req.Role != null)
    {
        if (!Roles.IsValid(req.Role))
            return Results.Json(new { error = "Invalid role." }, statusCode: StatusCodes.Status400BadRequest);
        if (user.Role == Roles.Admin && req.Role != Roles.Admin && !await OtherActiveAdminExists(db, user.Id))
            return Results.Json(new { error = "Cannot change the role of the only active admin." }, statusCode: StatusCodes.Status400BadRequest);
        user.Role = req.Role;
    }
    if (req.IsActive.HasValue)
    {
        if (!req.IsActive.Value && user.Role == Roles.Admin && !await OtherActiveAdminExists(db, user.Id))
            return Results.Json(new { error = "Cannot deactivate the only active admin." }, statusCode: StatusCodes.Status400BadRequest);
        user.IsActive = req.IsActive.Value;
        if (!user.IsActive) SessionStore.RemoveUser(user.Id);
    }
    if (req.DisplayName != null)
        user.DisplayName = req.DisplayName.Trim();

    await db.SaveChangesAsync();
    return Results.Ok(new { user.Id, user.Username, user.DisplayName, user.Role, user.IsActive });
});

app.MapPost("/api/users/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest req, BongoTexDbContext db) =>
{
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == id);
    if (user == null)
        return Results.Json(new { error = "User not found." }, statusCode: StatusCodes.Status404NotFound);
    if ((req.NewPassword ?? string.Empty).Length < 4)
        return Results.Json(new { error = "Password must be at least 4 characters." }, statusCode: StatusCodes.Status400BadRequest);
    user.PasswordHash = PasswordHasher.Hash(req.NewPassword!);
    SessionStore.RemoveUser(user.Id);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/users/{id:guid}", async (Guid id, HttpContext ctx, BongoTexDbContext db) =>
{
    var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == id);
    if (user == null)
        return Results.Json(new { error = "User not found." }, statusCode: StatusCodes.Status404NotFound);
    if (ctx.Items["AuthSession"] is AuthSession me && me.UserId == id)
        return Results.Json(new { error = "You cannot delete your own account." }, statusCode: StatusCodes.Status400BadRequest);
    if (user.Role == Roles.Admin && !await OtherActiveAdminExists(db, user.Id))
        return Results.Json(new { error = "Cannot delete the only active admin." }, statusCode: StatusCodes.Status400BadRequest);
    db.AppUsers.Remove(user);
    SessionStore.RemoveUser(user.Id);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/admin/reset-all-data", async (ResetAllDataRequest req, BongoTexDbContext db) =>
{
    const string confirmPhrase = "DELETE ALL BONGOTEX DATA";
    if (!string.Equals((req.Confirm ?? string.Empty).Trim(), confirmPhrase, StringComparison.Ordinal))
    {
        return Results.BadRequest(new
        {
            error = "Confirmation required.",
            detail = $"Send JSON body: {{ \"confirm\": \"{confirmPhrase}\" }}"
        });
    }

    try
    {
        await DataResetOps.ResetAllAsync(db);
        return Results.Ok(new
        {
            message = "All BongoTex data has been erased.",
            defaultSites = "Factory + 3 sales centers were recreated.",
            hint = "Register customers, suppliers, employees, and inventory from scratch."
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "Reset failed.", detail = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/inventory", async (BongoTexDbContext db) =>
{
    var items = await db.InventoryItems
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(items);
});

app.MapGet("/api/product-styles", async (BongoTexDbContext db) =>
{
    var rows = await db.ProductStyles.AsNoTracking()
        .OrderBy(x => x.Prefix)
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapPost("/api/product-styles", async (UpsertProductStyleRequest req, BongoTexDbContext db) =>
{
    var prefix = ProductStyleOps.NormalizePrefix(req.Prefix);
    if (prefix is null)
        return Results.BadRequest("Prefix must be 2–10 letters (A–Z), e.g. ST, SP, JK.");
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Style name is required.");
    if (req.ProductionCost < 0)
        return Results.BadRequest("Production cost cannot be negative.");
    if (await db.ProductStyles.AnyAsync(x => x.Prefix == prefix))
        return Results.BadRequest($"Prefix {prefix} is already registered.");

    var now = DateTime.UtcNow;
    var style = new ProductStyle
    {
        Prefix = prefix,
        Name = req.Name.Trim(),
        ProductionCost = req.ProductionCost,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };
    db.ProductStyles.Add(style);
    await db.SaveChangesAsync();
    return Results.Created($"/api/product-styles/{prefix}", style);
});

app.MapPut("/api/product-styles/{prefix}", async (string prefix, UpsertProductStyleRequest req, BongoTexDbContext db) =>
{
    var key = ProductStyleOps.NormalizePrefix(prefix);
    if (key is null)
        return Results.BadRequest("Invalid prefix.");
    var style = await db.ProductStyles.FirstOrDefaultAsync(x => x.Prefix == key);
    if (style is null)
        return Results.NotFound("Style not found.");
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Style name is required.");
    if (req.ProductionCost < 0)
        return Results.BadRequest("Production cost cannot be negative.");

    style.Name = req.Name.Trim();
    style.ProductionCost = req.ProductionCost;
    style.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();

    var skus = await db.InventoryItems.Where(i => i.Sku.StartsWith(key + "-")).ToListAsync();
    foreach (var item in skus)
        item.ProductionCost = style.ProductionCost;
    if (skus.Count > 0)
        await db.SaveChangesAsync();

    return Results.Ok(style);
});

app.MapGet("/api/inventory/next-sku/{prefix}", async (string prefix, BongoTexDbContext db) =>
{
    var normalizedPrefix = ProductStyleOps.NormalizePrefix(prefix);
    if (normalizedPrefix is null)
        return Results.BadRequest("Invalid SKU prefix.");
    if (!await db.ProductStyles.AnyAsync(x => x.Prefix == normalizedPrefix))
        return Results.BadRequest($"Register prefix {normalizedPrefix} on Registration first.");

    var likePrefix = $"{normalizedPrefix}-%";
    var existingSkus = await db.InventoryItems
        .Where(x => EF.Functions.Like(x.Sku, likePrefix))
        .Select(x => x.Sku)
        .ToListAsync();

    var maxNumber = 0;
    foreach (var sku in existingSkus)
    {
        var parts = sku.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var n) && n > maxNumber)
        {
            maxNumber = n;
        }
    }

    var nextSku = $"{normalizedPrefix}-{(maxNumber + 1):D3}";
    return Results.Ok(new { Sku = nextSku });
});

/// <summary>Pipeline totals for an item: cumulative pcs finished, cumulative pcs cut, and damaged = cut − finished (optional: through end of UTC calendar day <paramref name="through"/>).</summary>
app.MapGet("/api/inventory/{id:guid}/finished-pcs-total", async (Guid id, string? through, BongoTexDbContext db) =>
{
    if (!await db.InventoryItems.AnyAsync(x => x.Id == id))
        return Results.NotFound("Inventory item not found.");

    DateTime? endExclusive = null;
    if (!string.IsNullOrWhiteSpace(through) && DateOnly.TryParse(through, CultureInfo.InvariantCulture, out var throughDay))
    {
        var dayStart = throughDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        endExclusive = dayStart.AddDays(1);
    }

    long finTotal;
    long cutTotal;
    if (endExclusive.HasValue)
    {
        var excl = endExclusive.Value;
        finTotal = await db.FinishingEntries.AsNoTracking()
            .Where(f => f.InventoryItemId == id && f.FinishedAtUtc < excl)
            .SumAsync(f => (long)f.QuantityFinished);
        cutTotal = await db.CuttingEntries.AsNoTracking()
            .Where(c => c.InventoryItemId == id && c.CutAtUtc < excl)
            .SumAsync(c => (long)c.QuantityCut);
    }
    else
    {
        finTotal = await db.FinishingEntries.AsNoTracking()
            .Where(f => f.InventoryItemId == id)
            .SumAsync(f => (long)f.QuantityFinished);
        cutTotal = await db.CuttingEntries.AsNoTracking()
            .Where(c => c.InventoryItemId == id)
            .SumAsync(c => (long)c.QuantityCut);
    }

    var damaged = cutTotal - finTotal;
    return Results.Ok(new
    {
        inventoryItemId = id,
        quantityCutTotal = cutTotal,
        quantityFinishedTotal = finTotal,
        damagedPcs = damaged
    });
});

app.MapPost("/api/inventory", async (CreateInventoryItemRequest req, BongoTexDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
    {
        return Results.BadRequest("Name is required.");
    }
    if (string.IsNullOrWhiteSpace(req.CuttingNumber))
    {
        return Results.BadRequest("Cutting number is required.");
    }
    var cuttingNumber = req.CuttingNumber.Trim();
    var duplicateCuttingNumber = await db.InventoryItems.AnyAsync(x => x.CuttingNumber == cuttingNumber);
    if (duplicateCuttingNumber)
    {
        return Results.BadRequest("Cutting number already exists. Please use a unique cutting number.");
    }
    if (!req.UnitPrice.HasValue)
    {
        return Results.BadRequest("Product cost is required.");
    }
    if (!req.SalesPrice.HasValue)
    {
        return Results.BadRequest("Sales price is required.");
    }
    if (!req.DiscountPrice.HasValue)
    {
        return Results.BadRequest("Discount price is required.");
    }
    if (req.ProductionCost.HasValue && req.ProductionCost.Value < 0)
    {
        return Results.BadRequest("Production cost cannot be negative.");
    }
    if (req.UnitPrice.Value <= 0)
    {
        return Results.BadRequest("Product cost must be greater than zero.");
    }
    if (req.SalesPrice.Value <= 0)
    {
        return Results.BadRequest("Sales price must be greater than zero.");
    }
    if (req.DiscountPrice.Value < 0)
    {
        return Results.BadRequest("Discount price cannot be negative.");
    }
    if (req.DiscountPrice.Value > req.SalesPrice.Value)
    {
        return Results.BadRequest("Discount price cannot be greater than sales price.");
    }
    if (req.PrintChargePerPiece.HasValue && req.PrintChargePerPiece.Value < 0)
    {
        return Results.BadRequest("Print charge per piece cannot be negative.");
    }

    var normalizedPrefix = ProductStyleOps.NormalizePrefix(req.SkuPrefix);
    if (normalizedPrefix is null)
        return Results.BadRequest("Invalid SKU prefix.");
    var style = await db.ProductStyles.AsNoTracking().FirstOrDefaultAsync(x => x.Prefix == normalizedPrefix);
    if (style is null)
        return Results.BadRequest($"Register prefix {normalizedPrefix} on Registration before adding inventory items.");

    var explicitSku = (req.Sku ?? "").Trim();
    string skuToUse;
    if (!string.IsNullOrEmpty(explicitSku))
    {
        var dashIdx = explicitSku.IndexOf('-');
        if (dashIdx <= 0 || dashIdx >= explicitSku.Length - 1)
        {
            return Results.BadRequest("Item number (SKU) must include a prefix and suffix, e.g. ST-001.");
        }
        var p = explicitSku[..dashIdx].Trim().ToUpperInvariant();
        if (p != normalizedPrefix)
        {
            return Results.BadRequest("Item number prefix must match the selected item type.");
        }
        var rest = explicitSku[(dashIdx + 1)..].Trim();
        if (string.IsNullOrEmpty(rest))
        {
            return Results.BadRequest("Item number (SKU) must include a suffix after the hyphen.");
        }
        if (await db.InventoryItems.AnyAsync(x => x.Sku.ToLower() == explicitSku.ToLower()))
        {
            return Results.BadRequest("That item number already exists in inventory. Pick another from the list or use a different SKU.");
        }
        skuToUse = $"{normalizedPrefix}-{rest}";
    }
    else
    {
        var likePrefix = $"{normalizedPrefix}-%";
        var existingSkus = await db.InventoryItems
            .Where(x => EF.Functions.Like(x.Sku, likePrefix))
            .Select(x => x.Sku)
            .ToListAsync();

        var maxNumber = 0;
        foreach (var sku in existingSkus)
        {
            var parts = sku.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out var n) && n > maxNumber)
            {
                maxNumber = n;
            }
        }

        skuToUse = $"{normalizedPrefix}-{(maxNumber + 1):D3}";
    }

    var item = new InventoryItem
    {
        Sku = skuToUse,
        Name = req.Name.Trim(),
        CuttingNumber = cuttingNumber,
        UnitPrice = req.UnitPrice.Value,
        ProductionCost = style.ProductionCost,
        SalesPrice = req.SalesPrice.Value,
        DiscountPrice = req.DiscountPrice.Value,
        IsPrintItem = req.IsPrintItem ?? false,
        PrintChargePerPiece = req.PrintChargePerPiece ?? 0,
        PrintItemMarkedAtUtc = (req.IsPrintItem ?? false) ? DateTime.UtcNow : null,
        ItemImageBase64 = req.ItemImageBase64?.Trim() ?? string.Empty,
        QuantityOnHand = 0
    };

    try
    {
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var sites = await db.Sites.ToListAsync();
        foreach (var site in sites)
        {
            var exists = await db.InventoryStocks.AnyAsync(x => x.InventoryItemId == item.Id && x.SiteId == site.Id);
            if (!exists)
            {
                db.InventoryStocks.Add(new InventoryStock
                {
                    InventoryItemId = item.Id,
                    SiteId = site.Id,
                    Quantity = 0
                });
            }
        }
        await db.SaveChangesAsync();

        return Results.Created($"/api/inventory/{item.Id}", item);
    }
    catch (DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (message.Contains("IX_InventoryItems_Sku", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("SKU already exists. Please use a unique SKU.");
        }
        if (message.Contains("IX_InventoryItems_CuttingNumber", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Cutting number already exists. Please use a unique cutting number.");
        }

        return Results.BadRequest("Unable to save inventory item. Check database connection/schema.");
    }
});

app.MapPut("/api/inventory/{id:guid}", async (Guid id, UpdateInventoryItemRequest req, BongoTexDbContext db) =>
{
    var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == id);
    if (item is null) return Results.NotFound("Inventory item not found.");
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Name is required.");
    var cuttingNumber = req.CuttingNumber.Trim();
    if (string.IsNullOrWhiteSpace(cuttingNumber))
        return Results.BadRequest("Cutting number is required.");
    if (await db.InventoryItems.AnyAsync(x => x.CuttingNumber == cuttingNumber && x.Id != id))
        return Results.BadRequest("Cutting number already exists on another item.");
    if (req.UnitPrice <= 0)
        return Results.BadRequest("Product cost must be greater than zero.");
    if (req.SalesPrice <= 0)
        return Results.BadRequest("Sales price must be greater than zero.");
    if (req.DiscountPrice < 0)
        return Results.BadRequest("Discount price cannot be negative.");
    if (req.DiscountPrice > req.SalesPrice)
        return Results.BadRequest("Discount price cannot be greater than sales price.");
    if (req.PrintChargePerPiece < 0)
        return Results.BadRequest("Print charge per piece cannot be negative.");

    var wasPrint = item.IsPrintItem;
    item.Name = req.Name.Trim();
    item.CuttingNumber = cuttingNumber;
    item.UnitPrice = req.UnitPrice;
    var itemPrefix = ProductStyleOps.TryGetPrefixFromSku(item.Sku);
    if (itemPrefix is not null)
    {
        var style = await db.ProductStyles.AsNoTracking().FirstOrDefaultAsync(x => x.Prefix == itemPrefix);
        if (style is not null)
            item.ProductionCost = style.ProductionCost;
    }
    item.SalesPrice = req.SalesPrice;
    item.DiscountPrice = req.DiscountPrice;
    item.IsPrintItem = req.IsPrintItem;
    item.PrintChargePerPiece = req.PrintChargePerPiece;
    if (req.IsPrintItem && !wasPrint)
    {
        item.PrintItemMarkedAtUtc = DateTime.UtcNow;
        await SalesPrintSnapshot.ClearSnapshotsForItemAsync(db, item.Id);
    }
    else if (!req.IsPrintItem)
    {
        item.PrintItemMarkedAtUtc = null;
        await SalesPrintSnapshot.ClearSnapshotsForItemAsync(db, item.Id);
    }
    else if (req.IsPrintItem && item.PrintItemMarkedAtUtc is null)
    {
        item.PrintItemMarkedAtUtc = item.CreatedAtUtc;
    }
    if (!string.IsNullOrWhiteSpace(req.ItemImageBase64))
        item.ItemImageBase64 = req.ItemImageBase64.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(item);
});

app.MapPut("/api/inventory/{id:guid}/image", async (Guid id, UpdateInventoryItemImageRequest req, BongoTexDbContext db) =>
{
    var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == id);
    if (item is null) return Results.NotFound("Inventory item not found.");
    var img = req.ItemImageBase64?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(img)) return Results.BadRequest("Item image is required.");
    item.ItemImageBase64 = img;
    await db.SaveChangesAsync();
    return Results.Ok(new { item.Id, item.Sku, item.Name });
});

app.MapGet("/api/customers", async (BongoTexDbContext db) =>
{
    var customers = await db.Customers
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(customers);
});
app.MapPost("/api/customers", async (CreateCustomerRequest req, BongoTexDbContext db) =>
{
    var name = req.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest("Customer name is required.");

    var phone = req.Phone?.Trim() ?? string.Empty;
    var code = (req.CustomerCode ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(code))
        code = $"CUS-{DateTime.UtcNow:yyyyMMddHHmmss}";

    if (await db.Customers.AnyAsync(x => x.CustomerCode == code))
        return Results.BadRequest("Customer code already exists.");

    var customer = new Customer
    {
        CustomerCode = code,
        Name = name,
        ShopName = req.ShopName?.Trim() ?? string.Empty,
        Phone = phone,
        Email = req.Email?.Trim() ?? string.Empty,
        Address = req.Address?.Trim() ?? string.Empty
    };

    db.Customers.Add(customer);
    await db.SaveChangesAsync();
    return Results.Created($"/api/customers/{customer.Id}", customer);
});

app.MapGet("/api/suppliers", async (BongoTexDbContext db) =>
{
    var suppliers = await db.Suppliers
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(suppliers.Select(s => new
    {
        s.Id,
        s.SupplierCode,
        s.Name,
        s.CompanyName,
        s.Category,
        CategoryLabel = SupplierConventions.DisplayLabel(s.Category),
        IsPrintSupplier = SupplierConventions.IsPrintCategory(s.Category),
        IsInHousePrint = SupplierConventions.IsInHousePrint(s.Category),
        IsOutsidePrint = SupplierConventions.IsOutsidePrint(s.Category),
        s.Phone,
        s.Address,
        s.CreatedAtUtc
    }));
});

app.MapGet("/api/suppliers/categories", () => Results.Ok(new
{
    Categories = SupplierConventions.AllCategories.Select(c => new
    {
        Value = c,
        Label = SupplierConventions.DisplayLabel(c)
    }),
    FilterOptions = SupplierConventions.FilterOptions.Select(o => new { o.Value, o.Label }),
    PrintInHouse = SupplierConventions.PrintInHouse,
    PrintOutside = SupplierConventions.PrintOutside,
    PrintAllFilter = SupplierConventions.PrintAllFilter
}));

app.MapPost("/api/suppliers", async (CreateSupplierRequest req, BongoTexDbContext db) =>
{
    var name = req.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest("Supplier name is required.");
    var category = SupplierConventions.NormalizeCategory(req.Category);
    if (string.IsNullOrEmpty(category) || !SupplierConventions.IsAllowedCategory(category))
        return Results.BadRequest("Invalid supplier category.");

    var code = (req.SupplierCode ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(code))
        code = $"SUP-{DateTime.UtcNow:yyyyMMddHHmmss}";

    if (await db.Suppliers.AnyAsync(x => x.SupplierCode == code))
        return Results.BadRequest("Supplier code already exists.");

    var supplier = new Supplier
    {
        SupplierCode = code,
        Name = name,
        CompanyName = req.CompanyName?.Trim() ?? string.Empty,
        Category = category,
        Phone = req.Phone?.Trim() ?? string.Empty,
        Address = req.Address?.Trim() ?? string.Empty
    };

    db.Suppliers.Add(supplier);
    await db.SaveChangesAsync();
    return Results.Created($"/api/suppliers/{supplier.Id}", supplier);
});
app.MapPut("/api/suppliers/{id:guid}/category", async (Guid id, UpdateSupplierCategoryRequest req, BongoTexDbContext db) =>
{
    var supplier = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id);
    if (supplier is null) return Results.NotFound("Supplier not found.");
    var category = SupplierConventions.NormalizeCategory(req.Category);
    if (string.IsNullOrEmpty(category) || !SupplierConventions.IsAllowedCategory(category))
        return Results.BadRequest("Invalid supplier category.");
    supplier.Category = category;
    await db.SaveChangesAsync();
    return Results.Ok(new { supplier.Id, supplier.Name, supplier.Category });
});

app.MapGet("/api/employees", async (BongoTexDbContext db) =>
{
    var employees = await (
        from e in db.Employees
        join s in db.Sites on e.SiteId equals s.Id into siteJoin
        from s in siteJoin.DefaultIfEmpty()
        orderby e.Name
        select new
        {
            e.Id,
            e.SerialNumber,
            e.EmployeeCode,
            e.Name,
            e.EmployeeType,
            e.EmployeeCategory,
            e.SiteId,
            SiteCode = s != null ? s.Code : "",
            SiteName = s != null ? s.Name : "",
            e.MonthlySalary,
            e.MobileNumber,
            e.NationalIdNumber,
            e.NationalIdImageBase64,
            e.Address,
            e.IsActive,
            e.LeftAtUtc,
            e.CreatedAtUtc,
            HasNationalIdImage = !string.IsNullOrWhiteSpace(e.NationalIdImageBase64)
        }).ToListAsync();
    return Results.Ok(employees);
});
app.MapGet("/api/employees/{id:guid}/nid-image", async (Guid id, BongoTexDbContext db) =>
{
    var row = await db.Employees
        .Where(x => x.Id == id)
        .Select(x => new { x.Id, x.Name, x.NationalIdImageBase64 })
        .FirstOrDefaultAsync();
    if (row is null) return Results.NotFound("Employee not found.");
    if (string.IsNullOrWhiteSpace(row.NationalIdImageBase64)) return Results.NotFound("National ID image not found.");
    return Results.Ok(new { row.Id, row.Name, row.NationalIdImageBase64 });
});
app.MapPost("/api/employees", async (CreateEmployeeRequest req, BongoTexDbContext db) =>
{
    var name = req.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest("Employee name is required.");
    var mobile = req.MobileNumber?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(mobile)) return Results.BadRequest("Mobile number is required.");
    var nid = req.NationalIdNumber?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(nid)) return Results.BadRequest("National ID number is required.");
    if (string.IsNullOrWhiteSpace(req.NationalIdImageBase64)) return Results.BadRequest("National ID image is required.");
    if (req.MonthlySalary <= 0) return Results.BadRequest("Monthly salary must be greater than zero.");
    var employeeType = (req.EmployeeType ?? string.Empty).Trim();
    if (employeeType != "SalesCenter" && employeeType != "Factory" && employeeType != "PrintFactory")
        return Results.BadRequest("Employee type must be SalesCenter, Factory, or PrintFactory.");
    var employeeCategory = (req.EmployeeCategory ?? string.Empty).Trim();
    Guid? siteId = null;
    if (employeeType == "PrintFactory")
    {
        employeeCategory = "PrintFactory";
    }
    else if (employeeType == "Factory")
    {
        if (!EmployeeCategoryCatalog.FactoryDesignations.Contains(employeeCategory))
            return Results.BadRequest("Factory designation must be Owner, Manager, Accountant, Designer, Cutting Master, Line Man, Labour, Operator, Helper, Print, or Security.");
    }
    else
    {
        if (!req.SiteId.HasValue || req.SiteId.Value == Guid.Empty)
            return Results.BadRequest("Select sales center for sales center employee.");
        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId.Value && x.Type == "SalesCenter" && x.IsActive);
        if (site is null) return Results.BadRequest("Invalid or closed sales center selected.");
        siteId = site.Id;
        employeeCategory = "SalesCenter";
    }
    if (await db.Employees.AnyAsync(x => x.NationalIdNumber == nid)) return Results.BadRequest("National ID number already exists.");

    var serialError = await EmployeeSerialOps.ValidateUniqueAsync(db, req.SerialNumber, employeeType, siteId, null);
    if (serialError is not null) return Results.BadRequest(serialError);

    var code = (req.EmployeeCode ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(code)) code = $"EMP-{DateTime.UtcNow:yyyyMMddHHmmss}";
    if (await db.Employees.AnyAsync(x => x.EmployeeCode == code)) return Results.BadRequest("Employee code already exists.");

    var employee = new Employee
    {
        SerialNumber = req.SerialNumber,
        EmployeeCode = code,
        Name = name,
        EmployeeType = employeeType,
        EmployeeCategory = employeeCategory,
        SiteId = siteId,
        MonthlySalary = req.MonthlySalary,
        MobileNumber = mobile,
        NationalIdNumber = nid,
        NationalIdImageBase64 = req.NationalIdImageBase64.Trim(),
        Address = req.Address?.Trim() ?? string.Empty,
        CreatedAtUtc = DateTime.UtcNow
    };
    db.Employees.Add(employee);
    await db.SaveChangesAsync();
    return Results.Created($"/api/employees/{employee.Id}", employee);
});

app.MapPut("/api/employees/{id:guid}", async (Guid id, UpdateEmployeeRequest req, BongoTexDbContext db) =>
{
    var emp = await db.Employees.FirstOrDefaultAsync(x => x.Id == id);
    if (emp is null) return Results.NotFound("Employee not found.");

    var name = req.Name?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest("Employee name is required.");
    var mobile = req.MobileNumber?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(mobile)) return Results.BadRequest("Mobile number is required.");
    var nid = req.NationalIdNumber?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(nid)) return Results.BadRequest("National ID number is required.");
    if (req.MonthlySalary <= 0) return Results.BadRequest("Monthly salary must be greater than zero.");
    var employeeType = (req.EmployeeType ?? string.Empty).Trim();
    if (employeeType != "SalesCenter" && employeeType != "Factory" && employeeType != "PrintFactory")
        return Results.BadRequest("Employee type must be SalesCenter, Factory, or PrintFactory.");
    var employeeCategory = (req.EmployeeCategory ?? string.Empty).Trim();
    Guid? siteId = null;
    if (employeeType == "PrintFactory")
    {
        employeeCategory = "PrintFactory";
    }
    else if (employeeType == "Factory")
    {
        if (!EmployeeCategoryCatalog.FactoryDesignations.Contains(employeeCategory))
            return Results.BadRequest("Factory designation must be Owner, Manager, Accountant, Designer, Cutting Master, Line Man, Labour, Operator, Helper, Print, or Security.");
    }
    else
    {
        if (!req.SiteId.HasValue || req.SiteId.Value == Guid.Empty)
            return Results.BadRequest("Select sales center for sales center employee.");
        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId.Value && x.Type == "SalesCenter" && x.IsActive);
        if (site is null) return Results.BadRequest("Invalid or closed sales center selected.");
        siteId = site.Id;
        employeeCategory = "SalesCenter";
    }
    if (await db.Employees.AnyAsync(x => x.NationalIdNumber == nid && x.Id != id))
        return Results.BadRequest("National ID number already exists.");

    var serialError = await EmployeeSerialOps.ValidateUniqueAsync(db, req.SerialNumber, employeeType, siteId, id);
    if (serialError is not null) return Results.BadRequest(serialError);

    var imageBase64 = req.NationalIdImageBase64?.Trim();
    if (!string.IsNullOrWhiteSpace(imageBase64))
        emp.NationalIdImageBase64 = imageBase64;
    else if (string.IsNullOrWhiteSpace(emp.NationalIdImageBase64))
        return Results.BadRequest("National ID image is required.");

    emp.Name = name;
    emp.SerialNumber = req.SerialNumber;
    emp.EmployeeType = employeeType;
    emp.EmployeeCategory = employeeCategory;
    emp.SiteId = siteId;
    emp.MonthlySalary = req.MonthlySalary;
    emp.MobileNumber = mobile;
    emp.NationalIdNumber = nid;
    emp.Address = req.Address?.Trim() ?? string.Empty;
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        emp.Id,
        emp.SerialNumber,
        emp.Name,
        emp.EmployeeType,
        emp.EmployeeCategory,
        emp.SiteId,
        emp.MonthlySalary,
        emp.MobileNumber,
        emp.NationalIdNumber,
        emp.Address,
        emp.IsActive,
        HasNationalIdImage = !string.IsNullOrWhiteSpace(emp.NationalIdImageBase64)
    });
});

app.MapPut("/api/employees/{id:guid}/active", async (Guid id, SetEmployeeActiveRequest req, BongoTexDbContext db) =>
{
    var emp = await db.Employees.FirstOrDefaultAsync(x => x.Id == id);
    if (emp is null) return Results.NotFound("Employee not found.");
    emp.IsActive = req.IsActive;
    emp.LeftAtUtc = req.IsActive ? null : (req.LeftAtUtc?.ToUniversalTime() ?? DateTime.UtcNow);
    await db.SaveChangesAsync();
    return Results.Ok(new { emp.Id, emp.Name, emp.IsActive, emp.LeftAtUtc });
});

app.MapDelete("/api/employees/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var emp = await db.Employees.FirstOrDefaultAsync(x => x.Id == id);
    if (emp is null) return Results.NotFound("Employee not found.");
    if (emp.IsActive)
        return Results.BadRequest(new { error = "Disable the employee first, then delete." });

    var attendance = await db.FactoryAttendanceDays.Where(x => x.EmployeeId == id).ToListAsync();
    if (attendance.Count > 0)
        db.FactoryAttendanceDays.RemoveRange(attendance);

    var payrollLines = await db.PayrollLines.Where(x => x.EmployeeId == id).ToListAsync();
    if (payrollLines.Count > 0)
        db.PayrollLines.RemoveRange(payrollLines);

    db.Employees.Remove(emp);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/api/sites/monthly-rents", async (BongoTexDbContext db) =>
{
    var sites = await db.Sites.AsNoTracking()
        .Where(s => s.Type == "Factory" || s.Type == "SalesCenter")
        .OrderBy(s => s.Type).ThenBy(s => s.Code)
        .ToListAsync();
    var factories = sites.Where(s => s.Type == "Factory").OrderBy(s => s.Code).ToList();
    var centers = sites.Where(s => s.Type == "SalesCenter").OrderBy(s => s.Code).ToList();
    var rents = await db.SiteMonthlyRents.AsNoTracking().ToDictionaryAsync(r => r.SiteId);
    var rows = new List<object>();

    foreach (var s in factories)
    {
        rents.TryGetValue(s.Id, out var rent);
        rows.Add(new
        {
            s.Id,
            s.Code,
            s.Name,
            s.Type,
            s.IsActive,
            s.ClosedAtUtc,
            MonthlyRent = rent?.MonthlyRent ?? 0m,
            LandlordName = rent?.LandlordName ?? "",
            UpdatedAtUtc = rent?.UpdatedAtUtc,
            CanEdit = true,
            CanQuit = false
        });
    }

    rents.TryGetValue(PrintFactoryConventions.RentRegistryId, out var pfRent);
    rows.Add(new
    {
        Id = PrintFactoryConventions.RentRegistryId,
        Code = "PRINT-FACTORY",
        Name = "Print Factory (separate books)",
        Type = "PrintFactory",
        IsActive = true,
        ClosedAtUtc = (DateTime?)null,
        MonthlyRent = pfRent?.MonthlyRent ?? 0m,
        LandlordName = pfRent?.LandlordName ?? "",
        UpdatedAtUtc = pfRent?.UpdatedAtUtc,
        CanEdit = false,
        CanQuit = false
    });

    foreach (var s in centers)
    {
        rents.TryGetValue(s.Id, out var rent);
        rows.Add(new
        {
            s.Id,
            s.Code,
            s.Name,
            s.Type,
            s.IsActive,
            s.ClosedAtUtc,
            MonthlyRent = rent?.MonthlyRent ?? 0m,
            LandlordName = rent?.LandlordName ?? "",
            UpdatedAtUtc = rent?.UpdatedAtUtc,
            CanEdit = true,
            CanQuit = true
        });
    }

    return Results.Ok(new { Rows = rows });
});

app.MapPost("/api/sites/sales-centers", async (CreateSalesCenterRequest req, BongoTexDbContext db) =>
{
    var name = (req.Name ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest("Sales centre name is required.");
    if (req.MonthlyRent < 0)
        return Results.BadRequest("Monthly rent cannot be negative.");

    var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(code))
        code = await SiteOps.NextSalesCenterCodeAsync(db);
    if (code.Length > 30)
        return Results.BadRequest("Site code is too long.");
    if (await db.Sites.AnyAsync(x => x.Code.ToLower() == code.ToLower()))
        return Results.BadRequest("That site code already exists.");

    var site = new Site
    {
        Code = code,
        Name = name,
        Type = "SalesCenter",
        IsActive = true
    };
    db.Sites.Add(site);
    await db.SaveChangesAsync();

    await SiteOps.EnsureInventoryStocksForSiteAsync(db, site.Id);
    if (req.MonthlyRent > 0 || !string.IsNullOrWhiteSpace(req.LandlordName))
    {
        db.SiteMonthlyRents.Add(new SiteMonthlyRent
        {
            SiteId = site.Id,
            MonthlyRent = req.MonthlyRent,
            LandlordName = (req.LandlordName ?? string.Empty).Trim(),
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    return Results.Created($"/api/sites/{site.Id}", site);
});

app.MapPut("/api/sites/{id:guid}", async (Guid id, UpdateSiteRequest req, BongoTexDbContext db) =>
{
    if (id == PrintFactoryConventions.RentRegistryId)
        return Results.BadRequest("Print factory rent is edited via the rent row only.");
    var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == id);
    if (site is null)
        return Results.NotFound("Site not found.");
    if (site.Type is not ("Factory" or "SalesCenter"))
        return Results.BadRequest("Only factory and sales centre sites can be edited.");

    var name = (req.Name ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest("Name is required.");

    var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
    if (site.Type == "SalesCenter")
    {
        if (string.IsNullOrWhiteSpace(code))
            return Results.BadRequest("Sales centre code is required.");
        if (code.Length > 30)
            return Results.BadRequest("Site code is too long.");
        if (await db.Sites.AnyAsync(x => x.Code.ToLower() == code.ToLower() && x.Id != id))
            return Results.BadRequest("That site code already exists.");
        site.Code = code;
    }

    site.Name = name;
    await db.SaveChangesAsync();
    return Results.Ok(new { site.Id, site.Code, site.Name, site.Type, site.IsActive, site.ClosedAtUtc });
});

app.MapPut("/api/sites/{id:guid}/active", async (Guid id, SetSiteActiveRequest req, BongoTexDbContext db) =>
{
    var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == id);
    if (site is null)
        return Results.NotFound("Site not found.");
    if (site.Type != "SalesCenter")
        return Results.BadRequest("Only sales centres can be closed or re-opened.");
    site.IsActive = req.IsActive;
    site.ClosedAtUtc = req.IsActive ? null : (req.ClosedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow);
    await db.SaveChangesAsync();
    return Results.Ok(new { site.Id, site.Code, site.Name, site.Type, site.IsActive, site.ClosedAtUtc });
});

app.MapPut("/api/sites/{id:guid}/monthly-rent", async (Guid id, SetSiteMonthlyRentRequest req, BongoTexDbContext db) =>
{
    var isPrintFactoryRent = id == PrintFactoryConventions.RentRegistryId;
    Site? site = null;
    if (!isPrintFactoryRent)
    {
        site = await db.Sites.FirstOrDefaultAsync(x => x.Id == id);
        if (site is null)
            return Results.NotFound("Site not found.");
        if (site.Type is not ("Factory" or "SalesCenter"))
            return Results.BadRequest("Rent can only be registered for factory or sales centre sites.");
    }
    if (req.MonthlyRent < 0)
        return Results.BadRequest("Monthly rent cannot be negative.");
    var row = await db.SiteMonthlyRents.FirstOrDefaultAsync(x => x.SiteId == id);
    if (row is null)
    {
        row = new SiteMonthlyRent { SiteId = id };
        db.SiteMonthlyRents.Add(row);
    }
    row.MonthlyRent = req.MonthlyRent;
    row.LandlordName = (req.LandlordName ?? string.Empty).Trim();
    row.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    if (isPrintFactoryRent)
    {
        return Results.Ok(new
        {
            Id = PrintFactoryConventions.RentRegistryId,
            Code = "PRINT-FACTORY",
            Name = "Print Factory (separate books)",
            Type = "PrintFactory",
            row.MonthlyRent,
            row.LandlordName,
            row.UpdatedAtUtc
        });
    }
    return Results.Ok(new { site!.Id, site.Code, site.Name, site.Type, row.MonthlyRent, row.LandlordName, row.UpdatedAtUtc });
});

app.MapGet("/api/orders", async (BongoTexDbContext db) =>
{
    var orders = await db.SalesOrders
        .OrderByDescending(x => x.OrderDateUtc)
        .ToListAsync();
    return Results.Ok(orders);
});
app.MapPost("/api/orders", async (SalesOrder order, BongoTexDbContext db) =>
{
    db.SalesOrders.Add(order);
    await db.SaveChangesAsync();
    return Results.Created($"/api/orders/{order.Id}", order);
});

app.MapPost("/api/setup/sites/default", async (BongoTexDbContext db) =>
{
    if (await db.Sites.AnyAsync())
    {
        return Results.BadRequest("Sites already configured.");
    }

    var sites = await DataResetOps.SeedDefaultSitesAsync(db);
    return Results.Ok(sites);
});

app.MapGet("/api/sites", async (BongoTexDbContext db) =>
{
    var sites = await db.Sites.OrderBy(x => x.Type).ThenBy(x => x.Code).ToListAsync();
    return Results.Ok(sites);
});

app.MapGet("/api/stocks", async (BongoTexDbContext db) =>
{
    var stocks = await (
        from stock in db.InventoryStocks
        join item in db.InventoryItems on stock.InventoryItemId equals item.Id
        join site in db.Sites on stock.SiteId equals site.Id
        orderby item.Name, site.Code
        select new
        {
            stock.Id,
            stock.InventoryItemId,
            ItemSku = item.Sku,
            ItemName = item.Name,
            ItemImageBase64 = item.ItemImageBase64,
            CostPrice = item.UnitPrice,
            SalesPrice = item.SalesPrice,
            DiscountPrice = item.DiscountPrice,
            stock.SiteId,
            SiteCode = site.Code,
            SiteName = site.Name,
            stock.Quantity
        }).ToListAsync();

    return Results.Ok(stocks);
});

app.MapPost("/api/production-orders", async (CreateProductionOrderRequest req, BongoTexDbContext db) =>
{
    var factory = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.FactorySiteId && x.Type == "Factory");
    if (factory is null)
    {
        return Results.BadRequest("Factory site not found.");
    }

    var itemExists = await db.InventoryItems.AnyAsync(x => x.Id == req.InventoryItemId);
    if (!itemExists)
    {
        return Results.BadRequest("Inventory item not found.");
    }

    if (req.QuantityProduced <= 0)
    {
        return Results.BadRequest("Produced quantity must be greater than zero.");
    }

    var order = new ProductionOrder
    {
        ProductionNo = $"PO-{DateTime.UtcNow:yyyyMMddHHmmss}",
        FactorySiteId = req.FactorySiteId,
        InventoryItemId = req.InventoryItemId,
        QuantityProduced = req.QuantityProduced,
        ProducedAtUtc = req.ProducedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow
    };

    var stock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == req.InventoryItemId && x.SiteId == req.FactorySiteId);

    if (stock is null)
    {
        stock = new InventoryStock
        {
            InventoryItemId = req.InventoryItemId,
            SiteId = req.FactorySiteId,
            Quantity = 0
        };
        db.InventoryStocks.Add(stock);
    }

    stock.Quantity += req.QuantityProduced;
    db.ProductionOrders.Add(order);
    await db.SaveChangesAsync();

    return Results.Created($"/api/production-orders/{order.Id}", order);
});

app.MapGet("/api/production-orders", async (BongoTexDbContext db) =>
{
    var rows = await (
        from p in db.ProductionOrders
        join i in db.InventoryItems on p.InventoryItemId equals i.Id
        join s in db.Sites on p.FactorySiteId equals s.Id
        orderby p.ProducedAtUtc descending
        select new
        {
            p.Id,
            p.ProductionNo,
            p.ProducedAtUtc,
            p.QuantityProduced,
            p.InventoryItemId,
            ItemSku = i.Sku,
            CuttingNumber = i.CuttingNumber,
            ItemName = i.Name,
            p.FactorySiteId,
            FactoryCode = s.Code,
            FactoryName = s.Name
        }).Take(100).ToListAsync();

    return Results.Ok(rows);
});

app.MapPut("/api/production-orders/{id:guid}", async (Guid id, UpdateProductionOrderRequest req, BongoTexDbContext db) =>
{
    var order = await db.ProductionOrders.FirstOrDefaultAsync(x => x.Id == id);
    if (order is null)
    {
        return Results.NotFound("Production order not found.");
    }

    if (req.QuantityProduced <= 0)
    {
        return Results.BadRequest("Produced quantity must be greater than zero.");
    }

    var stock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == order.InventoryItemId && x.SiteId == order.FactorySiteId);
    if (stock is null)
    {
        return Results.BadRequest("Factory stock record not found.");
    }

    var delta = req.QuantityProduced - order.QuantityProduced;
    if (stock.Quantity + delta < 0)
    {
        return Results.BadRequest("Cannot edit production. Factory stock would become negative.");
    }

    stock.Quantity += delta;
    order.QuantityProduced = req.QuantityProduced;
    order.ProducedAtUtc = req.ProducedAtUtc?.ToUniversalTime() ?? order.ProducedAtUtc;

    await db.SaveChangesAsync();
    return Results.Ok(order);
});

app.MapDelete("/api/production-orders/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var order = await db.ProductionOrders.FirstOrDefaultAsync(x => x.Id == id);
    if (order is null)
    {
        return Results.NotFound("Production order not found.");
    }

    var stock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == order.InventoryItemId && x.SiteId == order.FactorySiteId);
    if (stock is null || stock.Quantity < order.QuantityProduced)
    {
        return Results.BadRequest("Cannot delete production. Insufficient factory stock to reverse.");
    }

    stock.Quantity -= order.QuantityProduced;
    db.ProductionOrders.Remove(order);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/cutting-entries", async (CreateCuttingEntryRequest req, BongoTexDbContext db) =>
{
    var factory = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.FactorySiteId && x.Type == "Factory");
    if (factory is null) return Results.BadRequest("Factory site not found.");
    var cutAtUtc = req.CutAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
    var lot = PipelineEntryValidation.NormalizePipelineCutLot(req.CutLotCode, cutAtUtc);
    var hasItem = req.InventoryItemId.HasValue && req.InventoryItemId.Value != Guid.Empty;
    if (!hasItem && string.IsNullOrEmpty(lot))
        return Results.BadRequest("Enter a cutting number (lot), or select an item.");
    if (hasItem && !await db.InventoryItems.AnyAsync(x => x.Id == req.InventoryItemId!.Value))
        return Results.BadRequest("Inventory item not found.");
    if (!string.IsNullOrEmpty(lot) &&
        await db.CuttingEntries.AnyAsync(c => c.FactorySiteId == req.FactorySiteId && c.CutLotCode == lot))
        return Results.BadRequest("This cutting number is already used at this factory. Each cutting number must be unique per factory (edit the existing cutting line or choose a new number).");
    if (req.QuantityCut <= 0) return Results.BadRequest("Cut pieces must be greater than zero.");
    if (req.FabricKg <= 0) return Results.BadRequest("Fabric kg must be greater than zero.");
    if (req.FabricPricePerKg < 0) return Results.BadRequest("Fabric price per kg cannot be negative.");
    var rawMaterialId = req.RawMaterialId is { } rmId && rmId != Guid.Empty ? rmId : (Guid?)null;
    if (rawMaterialId.HasValue && !await db.RawMaterials.AnyAsync(x => x.Id == rawMaterialId.Value && x.IsActive))
        return Results.BadRequest("Raw material not found or inactive.");
    var fabricAmount = decimal.Round(req.FabricKg * req.FabricPricePerKg, 2, MidpointRounding.AwayFromZero);
    var row = new CuttingEntry
    {
        CuttingNo = $"CUT-{DateTime.UtcNow:yyyyMMddHHmmss}",
        FactorySiteId = req.FactorySiteId,
        CutLotCode = lot,
        InventoryItemId = hasItem ? req.InventoryItemId : null,
        RawMaterialId = rawMaterialId,
        QuantityCut = req.QuantityCut,
        FabricKg = req.FabricKg,
        FabricPricePerKg = req.FabricPricePerKg,
        FabricAmount = fabricAmount,
        CutAtUtc = cutAtUtc
    };
    db.CuttingEntries.Add(row);
    if (rawMaterialId.HasValue)
    {
        var stockErr = await RawMaterialOps.IssueForCuttingAsync(db, row, rawMaterialId.Value, req.FabricKg, lot);
        if (stockErr is not null)
            return Results.BadRequest(stockErr);
    }
    await db.SaveChangesAsync();
    return Results.Created($"/api/cutting-entries/{row.Id}", row);
});

app.MapGet("/api/cutting-entries", async (BongoTexDbContext db) =>
{
    var rows = await (
        from c in db.CuttingEntries.AsNoTracking()
        join i in db.InventoryItems.AsNoTracking() on c.InventoryItemId equals i.Id into ig
        from i in ig.DefaultIfEmpty()
        join rm in db.RawMaterials.AsNoTracking() on c.RawMaterialId equals rm.Id into rmg
        from rm in rmg.DefaultIfEmpty()
        join s in db.Sites.AsNoTracking() on c.FactorySiteId equals s.Id
        orderby c.CutAtUtc descending
        select new
        {
            c.Id,
            c.CuttingNo,
            c.CutLotCode,
            c.CutAtUtc,
            c.QuantityCut,
            c.FabricKg,
            c.FabricPricePerKg,
            c.FabricAmount,
            c.InventoryItemId,
            c.RawMaterialId,
            ItemSku = i != null ? i.Sku : null,
            ItemName = i != null ? i.Name : null,
            RawMaterialCode = rm != null ? rm.Code : null,
            RawMaterialName = rm != null ? rm.Name : null,
            c.FactorySiteId,
            FactoryCode = s.Code,
            FactoryName = s.Name
        }).Take(300).ToListAsync();
    return Results.Ok(rows);
});

app.MapPut("/api/cutting-entries/{id:guid}", async (Guid id, UpdateCuttingEntryRequest req, BongoTexDbContext db) =>
{
    var row = await db.CuttingEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Cutting entry not found.");
    if (req.QuantityCut <= 0) return Results.BadRequest("Cut pieces must be greater than zero.");
    if (req.FabricKg <= 0) return Results.BadRequest("Fabric kg must be greater than zero.");
    if (req.FabricPricePerKg < 0) return Results.BadRequest("Fabric price per kg cannot be negative.");
    var cutErr = await PipelineEntryValidation.SewingExceedsCutAfterCutChangeAsync(
        db, row.InventoryItemId, row.FactorySiteId, row.CutLotCode ?? "", row.Id, req.QuantityCut, req.CutAtUtc?.ToUniversalTime() ?? row.CutAtUtc, removeCutRow: false);
    if (cutErr is not null) return Results.BadRequest(cutErr);
    var reverseErr = await RawMaterialOps.ReverseCuttingIssueAsync(db, row.Id);
    if (reverseErr is not null) return Results.BadRequest(reverseErr);
    row.QuantityCut = req.QuantityCut;
    row.FabricKg = req.FabricKg;
    row.FabricPricePerKg = req.FabricPricePerKg;
    row.FabricAmount = decimal.Round(req.FabricKg * req.FabricPricePerKg, 2, MidpointRounding.AwayFromZero);
    row.CutAtUtc = req.CutAtUtc?.ToUniversalTime() ?? row.CutAtUtc;
    var rawMaterialId = req.RawMaterialId is { } rmId && rmId != Guid.Empty ? rmId : row.RawMaterialId;
    row.RawMaterialId = rawMaterialId;
    if (rawMaterialId.HasValue)
    {
        var issueErr = await RawMaterialOps.IssueForCuttingAsync(db, row, rawMaterialId.Value, req.FabricKg, row.CutLotCode ?? "");
        if (issueErr is not null) return Results.BadRequest(issueErr);
    }
    await db.SaveChangesAsync();
    return Results.Ok(row);
});

app.MapDelete("/api/cutting-entries/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var row = await db.CuttingEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Cutting entry not found.");
    var cutErr = await PipelineEntryValidation.SewingExceedsCutAfterCutChangeAsync(
        db, row.InventoryItemId, row.FactorySiteId, row.CutLotCode ?? "", row.Id, null, null, removeCutRow: true);
    if (cutErr is not null) return Results.BadRequest(cutErr);
    var reverseErr = await RawMaterialOps.ReverseCuttingIssueAsync(db, row.Id);
    if (reverseErr is not null) return Results.BadRequest(reverseErr);
    db.CuttingEntries.Remove(row);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/sewing-entries", async (CreateSewingEntryRequest req, BongoTexDbContext db) =>
{
    var factory = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.FactorySiteId && x.Type == "Factory");
    if (factory is null) return Results.BadRequest("Factory site not found.");
    var sewnAtUtc = req.SewnAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
    var lot = PipelineEntryValidation.NormalizePipelineCutLot(req.CutLotCode, sewnAtUtc);
    var hasItem = req.InventoryItemId.HasValue && req.InventoryItemId.Value != Guid.Empty;
    if (!hasItem && string.IsNullOrEmpty(lot))
        return Results.BadRequest("Enter a cutting number (lot), or select an item.");
    if (hasItem && !await db.InventoryItems.AnyAsync(x => x.Id == req.InventoryItemId!.Value))
        return Results.BadRequest("Inventory item not found.");
    if (req.QuantitySewn <= 0) return Results.BadRequest("Sewn quantity must be greater than zero.");
    if (hasItem)
    {
        if (!await PipelineEntryValidation.SewingFitsCutThroughSewDayAsync(db, req.InventoryItemId!.Value, sewnAtUtc, req.QuantitySewn, excludeSewingId: null))
            return Results.BadRequest("Sewn quantity cannot exceed cut quantity for this style through that UTC calendar day. Add or increase a cutting entry first, or pick another date.");
    }
    else
    {
        if (!await PipelineEntryValidation.SewingFitsCutThroughSewDayForLotAsync(db, req.FactorySiteId, lot, sewnAtUtc, req.QuantitySewn, excludeSewingId: null))
            return Results.BadRequest("Sewn quantity cannot exceed cut quantity for this cutting lot through that UTC calendar day. Add or increase a cutting entry with the same factory and cutting number, or pick another date.");
    }

    var row = new SewingEntry
    {
        SewingNo = $"SEW-{DateTime.UtcNow:yyyyMMddHHmmss}",
        FactorySiteId = req.FactorySiteId,
        CutLotCode = lot,
        InventoryItemId = hasItem ? req.InventoryItemId : null,
        QuantitySewn = req.QuantitySewn,
        SewnAtUtc = sewnAtUtc
    };
    db.SewingEntries.Add(row);
    await db.SaveChangesAsync();
    return Results.Created($"/api/sewing-entries/{row.Id}", row);
});

app.MapGet("/api/sewing-entries", async (BongoTexDbContext db) =>
{
    var rows = await (
        from c in db.SewingEntries.AsNoTracking()
        join i in db.InventoryItems.AsNoTracking() on c.InventoryItemId equals i.Id into ig
        from i in ig.DefaultIfEmpty()
        join s in db.Sites.AsNoTracking() on c.FactorySiteId equals s.Id
        orderby c.SewnAtUtc descending
        select new
        {
            c.Id,
            c.SewingNo,
            c.CutLotCode,
            c.SewnAtUtc,
            c.QuantitySewn,
            c.InventoryItemId,
            ItemSku = i != null ? i.Sku : null,
            ItemName = i != null ? i.Name : null,
            c.FactorySiteId,
            FactoryCode = s.Code,
            FactoryName = s.Name
        }).Take(300).ToListAsync();
    return Results.Ok(rows);
});

app.MapPut("/api/sewing-entries/{id:guid}", async (Guid id, UpdateSewingEntryRequest req, BongoTexDbContext db) =>
{
    var row = await db.SewingEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Sewing entry not found.");
    if (req.QuantitySewn <= 0) return Results.BadRequest("Sewn quantity must be greater than zero.");
    var sewnAtUtc = req.SewnAtUtc?.ToUniversalTime() ?? row.SewnAtUtc;
    var hasItem = row.InventoryItemId.HasValue && row.InventoryItemId.Value != Guid.Empty;
    if (hasItem)
    {
        if (!await PipelineEntryValidation.SewingFitsCutThroughSewDayAsync(db, row.InventoryItemId!.Value, sewnAtUtc, req.QuantitySewn, excludeSewingId: id))
            return Results.BadRequest("Sewn quantity cannot exceed cut quantity for this style through that UTC calendar day. Add or increase a cutting entry first, or lower the sewn amount / change the date.");
    }
    else
    {
        var lot = (row.CutLotCode ?? "").Trim();
        if (string.IsNullOrEmpty(lot))
            return Results.BadRequest("This sewing row has no cutting number (lot); edit is blocked.");
        if (!await PipelineEntryValidation.SewingFitsCutThroughSewDayForLotAsync(db, row.FactorySiteId, lot, sewnAtUtc, req.QuantitySewn, excludeSewingId: id))
            return Results.BadRequest("Sewn quantity cannot exceed cut quantity for this cutting lot through that UTC calendar day. Add or increase a cutting entry with the same factory and cutting number, or lower the sewn amount / change the date.");
    }

    var sewFinErr = await PipelineEntryValidation.FinishingExceedsSewAfterSewChangeAsync(
        db, row.InventoryItemId, row.FactorySiteId, row.CutLotCode ?? "", row.Id, req.QuantitySewn, sewnAtUtc, removeSewRow: false);
    if (sewFinErr is not null) return Results.BadRequest(sewFinErr);
    row.QuantitySewn = req.QuantitySewn;
    row.SewnAtUtc = sewnAtUtc;
    await db.SaveChangesAsync();
    return Results.Ok(row);
});

app.MapDelete("/api/sewing-entries/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var row = await db.SewingEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Sewing entry not found.");
    var sewFinErr = await PipelineEntryValidation.FinishingExceedsSewAfterSewChangeAsync(
        db, row.InventoryItemId, row.FactorySiteId, row.CutLotCode ?? "", row.Id, null, null, removeSewRow: true);
    if (sewFinErr is not null) return Results.BadRequest(sewFinErr);
    db.SewingEntries.Remove(row);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/finishing-entries", async (CreateFinishingEntryRequest req, BongoTexDbContext db) =>
{
    var factory = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.FactorySiteId && x.Type == "Factory");
    if (factory is null) return Results.BadRequest("Factory site not found.");
    if (req.QuantityFinished <= 0) return Results.BadRequest("Finished quantity must be greater than zero.");
    var finishedAtUtc = req.FinishedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
    var lot = PipelineEntryValidation.NormalizePipelineCutLot(req.CutLotCode, finishedAtUtc);
    var key = (req.ItemSku ?? "").Trim();
    if (string.IsNullOrEmpty(key))
        return Results.BadRequest("Enter the item number (inventory SKU).");
    var keyLower = key.ToLowerInvariant();
    var item = await db.InventoryItems.FirstOrDefaultAsync(i => i.Sku == key);
    if (item is null)
        item = await db.InventoryItems.FirstOrDefaultAsync(i => i.Sku.ToLower() == keyLower);
    var autoProvisioned = false;
    if (item is null)
    {
        var created = await FinishingInventoryBootstrap.TryCreateItemForNewFinishingSkuAsync(db, key, lot);
        if (created is null)
            return Results.BadRequest(
                "No inventory item matches that SKU. Use a code like ST-009 (ST, SP, WS, or WH prefix), add the style under Inventory first, or check the spelling.");
        item = created;
        autoProvisioned = true;
    }
    var inventoryItemId = item.Id;
    if (!await PipelineEntryValidation.FinishingFitsSewThroughFinishDayAsync(
            db, inventoryItemId, finishedAtUtc, req.QuantityFinished, excludeFinishingId: null, req.FactorySiteId, lot))
    {
        if (autoProvisioned)
            await FinishingInventoryBootstrap.DeleteAutoProvisionedItemAsync(db, inventoryItemId);
        return Results.BadRequest("Finished quantity cannot exceed sewn quantity for this style through that UTC calendar day. Add or increase a sewing entry first, or lower the finished amount / change the date. If you use a cutting number (lot), include it so the system can count sewing done before the item was linked.");
    }

    var materialLines = (req.MaterialLines ?? [])
        .Where(l => l.RawMaterialId != Guid.Empty && l.Quantity > 0)
        .ToList();

    await RawMaterialOps.EnsureFinishingEntryIdColumnAsync(db);

    await using var tx = await db.Database.BeginTransactionAsync();
    try
    {
        var row = new FinishingEntry
        {
            FinishingNo = $"FIN-{DateTime.UtcNow:yyyyMMddHHmmss}",
            FactorySiteId = req.FactorySiteId,
            CutLotCode = lot,
            InventoryItemId = inventoryItemId,
            QuantityFinished = req.QuantityFinished,
            FinishedAtUtc = finishedAtUtc
        };
        db.FinishingEntries.Add(row);
        await db.SaveChangesAsync();

        var issuedMaterials = new List<object>();
        var lineSeq = 0;
        foreach (var line in materialLines)
        {
            lineSeq++;
            var issueErr = await RawMaterialOps.IssueForFinishingAsync(
                db, row, line.RawMaterialId, line.Quantity, line.UnitCost, lot, item.Sku, lineSeq);
            if (issueErr is not null)
            {
                await tx.RollbackAsync();
                if (autoProvisioned)
                    await FinishingInventoryBootstrap.DeleteAutoProvisionedItemAsync(db, inventoryItemId);
                return Results.BadRequest(new { error = issueErr, rawMaterialId = line.RawMaterialId });
            }

            var mat = await db.RawMaterials.AsNoTracking().FirstAsync(m => m.Id == line.RawMaterialId);
            issuedMaterials.Add(new
            {
                mat.Code,
                mat.Name,
                mat.Unit,
                line.Quantity,
                line.UnitCost
            });
        }

        if (!string.IsNullOrEmpty(lot))
        {
            await db.CuttingEntries.Where(c => c.FactorySiteId == req.FactorySiteId && c.CutLotCode == lot && c.InventoryItemId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.InventoryItemId, inventoryItemId));
            await db.SewingEntries.Where(s => s.FactorySiteId == req.FactorySiteId && s.CutLotCode == lot && s.InventoryItemId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.InventoryItemId, inventoryItemId));
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        return Results.Created($"/api/finishing-entries/{row.Id}", new
        {
            row.Id,
            row.FinishingNo,
            row.CutLotCode,
            row.QuantityFinished,
            MaterialsIssued = issuedMaterials
        });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        if (autoProvisioned)
            await FinishingInventoryBootstrap.DeleteAutoProvisionedItemAsync(db, inventoryItemId);
        var detail = ex.InnerException?.Message ?? ex.Message;
        return Results.Json(new { error = "Could not save finishing entry.", detail }, statusCode: 500);
    }
});

app.MapGet("/api/finishing-entries", async (BongoTexDbContext db) =>
{
    var rows = await (
        from c in db.FinishingEntries.AsNoTracking()
        join i in db.InventoryItems.AsNoTracking() on c.InventoryItemId equals i.Id
        join s in db.Sites.AsNoTracking() on c.FactorySiteId equals s.Id
        orderby c.FinishedAtUtc descending
        select new
        {
            c.Id,
            c.FinishingNo,
            c.CutLotCode,
            c.FinishedAtUtc,
            c.QuantityFinished,
            c.InventoryItemId,
            ItemSku = i.Sku,
            ItemName = i.Name,
            c.FactorySiteId,
            FactoryCode = s.Code,
            FactoryName = s.Name
        }).Take(300).ToListAsync();

    var finIds = rows.Select(r => r.Id).ToList();
    var issueRows = await (
        from m in db.RawMaterialMovements.AsNoTracking()
        join mat in db.RawMaterials.AsNoTracking() on m.RawMaterialId equals mat.Id
        where m.FinishingEntryId != null && finIds.Contains(m.FinishingEntryId.Value)
            && m.MovementType == RawMaterialOps.TypeIssue
        select new { m.FinishingEntryId, mat.Code, m.Quantity, mat.Unit, m.TotalCost }
    ).ToListAsync();

    var materialsByFin = issueRows
        .GroupBy(x => x.FinishingEntryId!.Value)
        .ToDictionary(
            g => g.Key,
            g => string.Join(", ", g.Select(x => $"{x.Code} {x.Quantity:0.####}{x.Unit}")));

    var result = rows.Select(r => new
    {
        r.Id,
        r.FinishingNo,
        r.CutLotCode,
        r.FinishedAtUtc,
        r.QuantityFinished,
        r.InventoryItemId,
        r.ItemSku,
        r.ItemName,
        r.FactorySiteId,
        r.FactoryCode,
        r.FactoryName,
        MaterialsUsed = materialsByFin.TryGetValue(r.Id, out var mu) ? mu : ""
    }).ToList();

    return Results.Ok(result);
});

app.MapPut("/api/finishing-entries/{id:guid}", async (Guid id, UpdateFinishingEntryRequest req, BongoTexDbContext db) =>
{
    var row = await db.FinishingEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Finishing entry not found.");
    if (req.QuantityFinished <= 0) return Results.BadRequest("Finished quantity must be greater than zero.");
    var finishedAtUtc = req.FinishedAtUtc?.ToUniversalTime() ?? row.FinishedAtUtc;
    var lot = (row.CutLotCode ?? "").Trim();
    if (!await PipelineEntryValidation.FinishingFitsSewThroughFinishDayAsync(
            db, row.InventoryItemId, finishedAtUtc, req.QuantityFinished, excludeFinishingId: id, row.FactorySiteId, lot))
        return Results.BadRequest("Finished quantity cannot exceed sewn quantity for this style through that UTC calendar day. Add or increase a sewing entry first, or lower the finished amount / change the date.");
    row.QuantityFinished = req.QuantityFinished;
    row.FinishedAtUtc = finishedAtUtc;
    await db.SaveChangesAsync();
    return Results.Ok(row);
});

app.MapDelete("/api/finishing-entries/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var row = await db.FinishingEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Finishing entry not found.");
    await RawMaterialOps.ReverseFinishingIssuesAsync(db, id);
    db.FinishingEntries.Remove(row);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/production-pipeline/daily-wip", async (string? from, string? to, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusiveStart, out var endExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var cuts = await db.CuttingEntries.AsNoTracking()
        .Where(c => c.CutAtUtc < endExclusive)
        .Select(c => new { c.InventoryItemId, c.CutAtUtc, c.QuantityCut })
        .ToListAsync();
    var sews = await db.SewingEntries.AsNoTracking()
        .Where(c => c.SewnAtUtc < endExclusive)
        .Select(c => new { c.InventoryItemId, c.SewnAtUtc, c.QuantitySewn })
        .ToListAsync();
    var fins = await db.FinishingEntries.AsNoTracking()
        .Where(c => c.FinishedAtUtc < endExclusive)
        .Select(c => new { c.InventoryItemId, c.FinishedAtUtc, c.QuantityFinished })
        .ToListAsync();

    var allIds = cuts.Where(c => c.InventoryItemId.HasValue).Select(c => c.InventoryItemId!.Value)
        .Concat(sews.Where(s => s.InventoryItemId.HasValue).Select(s => s.InventoryItemId!.Value))
        .Concat(fins.Select(f => f.InventoryItemId))
        .Distinct()
        .ToList();

    var itemLookup = await db.InventoryItems.AsNoTracking()
        .Where(i => allIds.Contains(i.Id))
        .ToDictionaryAsync(i => i.Id, i => new { i.Sku, i.Name });

    var days = new List<object>();
    for (var dayStart = fromUtc; dayStart < endExclusive; dayStart = dayStart.AddDays(1))
    {
        var dayEndExclusive = dayStart.AddDays(1);
        var dateStr = dayStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rowItems = new List<object>();
        foreach (var itemId in allIds.OrderBy(id => itemLookup.TryGetValue(id, out var inf) ? inf.Sku : id.ToString()))
        {
            itemLookup.TryGetValue(itemId, out var info);
            var sku = info?.Sku ?? "(removed item)";
            var name = info?.Name ?? "";
            var cutD = cuts.Where(c => c.InventoryItemId == itemId && c.CutAtUtc >= dayStart && c.CutAtUtc < dayEndExclusive).Sum(c => c.QuantityCut);
            var sewD = sews.Where(c => c.InventoryItemId == itemId && c.SewnAtUtc >= dayStart && c.SewnAtUtc < dayEndExclusive).Sum(c => c.QuantitySewn);
            var finD = fins.Where(c => c.InventoryItemId == itemId && c.FinishedAtUtc >= dayStart && c.FinishedAtUtc < dayEndExclusive).Sum(c => c.QuantityFinished);
            var cutC = cuts.Where(c => c.InventoryItemId == itemId && c.CutAtUtc < dayEndExclusive).Sum(c => c.QuantityCut);
            var sewC = sews.Where(c => c.InventoryItemId == itemId && c.SewnAtUtc < dayEndExclusive).Sum(c => c.QuantitySewn);
            var finC = fins.Where(c => c.InventoryItemId == itemId && c.FinishedAtUtc < dayEndExclusive).Sum(c => c.QuantityFinished);
            if (cutD == 0 && sewD == 0 && finD == 0 && cutC == 0 && sewC == 0 && finC == 0)
                continue;
            rowItems.Add(new
            {
                inventoryItemId = itemId,
                itemSku = sku,
                itemName = name,
                cutPcsToday = cutD,
                sewPcsToday = sewD,
                finishedPcsToday = finD,
                cutPcsCumulative = cutC,
                sewPcsCumulative = sewC,
                finishedPcsCumulative = finC,
                unsewingWip = cutC - sewC,
                unfinishedWip = sewC - finC
            });
        }

        days.Add(new { date = dateStr, items = rowItems });
    }

    return Results.Ok(new
    {
        rangeFromUtc = fromUtc,
        rangeToInclusiveUtc = toInclusiveStart,
        rangeEndExclusiveUtc = endExclusive,
        note = "Per UTC calendar day: today columns are sums that day; WIP columns are cumulative through end of that day (all cut − sewn = unsewing backlog; sewn − finished = unfinished backlog).",
        days
    });
});

app.MapPost("/api/stock-transfers", async (CreateStockTransferRequest req, BongoTexDbContext db) =>
{
    var result = await StockTransferOps.CreateOneAsync(db, req.InventoryItemId, req.FromSiteId, req.ToSiteId, req.Quantity, null, null);
    return result.Error is not null
        ? Results.BadRequest(new { error = result.Error })
        : Results.Created($"/api/stock-transfers/{result.Transfer!.Id}", result.Transfer);
});

app.MapPost("/api/stock-transfers/batch", async (CreateStockTransferBatchRequest req, BongoTexDbContext db) =>
{
    var lines = (req.Lines ?? []).Where(l => l.InventoryItemId != Guid.Empty && l.Quantity > 0).ToList();
    if (lines.Count == 0)
        return Results.BadRequest(new { error = "Add at least one item line with quantity." });

    var fromSiteCheck = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.FromSiteId);
    var toSiteCheck = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.ToSiteId);
    if (fromSiteCheck is null || toSiteCheck is null)
        return Results.BadRequest(new { error = "Invalid source or destination site." });

    var docPrefix = fromSiteCheck.Type == "SalesCenter" && toSiteCheck.Type == "Factory" ? "RTD"
        : fromSiteCheck.Type == "SalesCenter" && toSiteCheck.Type == "SalesCenter" ? "SCD"
        : "TRD";
    var documentNo = $"{docPrefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
    await using var tx = await db.Database.BeginTransactionAsync();
    try
    {
        var created = new List<StockTransferBatchLineDto>();
        var seq = 0;
        foreach (var line in lines)
        {
            seq++;
            var result = await StockTransferOps.CreateOneAsync(
                db, line.InventoryItemId, req.FromSiteId, req.ToSiteId, line.Quantity, documentNo, seq, save: false);
            if (result.Error is not null)
            {
                await tx.RollbackAsync();
                return Results.BadRequest(new { error = result.Error, line = seq });
            }

            var t = result.Transfer!;
            var item = await db.InventoryItems.AsNoTracking().FirstAsync(i => i.Id == t.InventoryItemId);
            var unit = item.SalesPrice > 0 ? item.SalesPrice : item.UnitPrice;
            created.Add(new StockTransferBatchLineDto(
                t.Id,
                t.TransferNo,
                t.DocumentNo,
                t.Quantity,
                item.Sku,
                item.Name,
                unit,
                t.Quantity * unit));
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        return Results.Ok(new
        {
            DocumentNo = documentNo,
            FromSiteCode = fromSiteCheck.Code,
            FromSiteName = fromSiteCheck.Name,
            ToSiteCode = toSiteCheck.Code,
            ToSiteName = toSiteCheck.Name,
            TransferredAtUtc = DateTime.UtcNow,
            TotalQuantity = created.Sum(x => x.Quantity),
            TotalAmount = created.Sum(x => x.LineAmount),
            Lines = created
        });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Json(new { error = "Batch transfer failed.", detail = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/stock-transfers/document/{documentNo}", async (string documentNo, BongoTexDbContext db) =>
{
    var key = (documentNo ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(key))
        return Results.BadRequest(new { error = "Document number is required." });

    var rows = await (
        from t in db.StockTransfers.AsNoTracking()
        join i in db.InventoryItems.AsNoTracking() on t.InventoryItemId equals i.Id
        join fromSite in db.Sites.AsNoTracking() on t.FromSiteId equals fromSite.Id
        join toSite in db.Sites.AsNoTracking() on t.ToSiteId equals toSite.Id
        where t.DocumentNo == key || (t.DocumentNo == "" && t.TransferNo == key)
        orderby t.TransferNo
        select new
        {
            t.Id,
            t.TransferNo,
            t.DocumentNo,
            t.TransferredAtUtc,
            t.Quantity,
            ItemSku = i.Sku,
            ItemName = i.Name,
            UnitPrice = i.SalesPrice > 0 ? i.SalesPrice : i.UnitPrice,
            FromSiteCode = fromSite.Code,
            FromSiteName = fromSite.Name,
            ToSiteCode = toSite.Code,
            ToSiteName = toSite.Name
        }).ToListAsync();

    if (rows.Count == 0)
        return Results.NotFound(new { error = "Transfer document not found." });

    var first = rows[0];
    var lineDtos = rows.Select(r => new
    {
        r.Id,
        r.TransferNo,
        r.ItemSku,
        r.ItemName,
        r.Quantity,
        r.UnitPrice,
        LineAmount = r.Quantity * r.UnitPrice
    }).ToList();

    return Results.Ok(new
    {
        DocumentNo = string.IsNullOrEmpty(first.DocumentNo) ? first.TransferNo : first.DocumentNo,
        first.TransferredAtUtc,
        first.FromSiteCode,
        first.FromSiteName,
        first.ToSiteCode,
        first.ToSiteName,
        TotalQuantity = lineDtos.Sum(x => x.Quantity),
        TotalAmount = lineDtos.Sum(x => x.LineAmount),
        Lines = lineDtos
    });
});

app.MapGet("/api/stock-transfers", async (BongoTexDbContext db) =>
{
    var rows = await (
        from t in db.StockTransfers
        join i in db.InventoryItems on t.InventoryItemId equals i.Id
        join fromSite in db.Sites on t.FromSiteId equals fromSite.Id
        join toSite in db.Sites on t.ToSiteId equals toSite.Id
        orderby t.TransferredAtUtc descending
        select new
        {
            t.Id,
            t.TransferNo,
            t.DocumentNo,
            t.TransferredAtUtc,
            t.Quantity,
            t.InventoryItemId,
            ItemSku = i.Sku,
            ItemName = i.Name,
            t.FromSiteId,
            FromSiteCode = fromSite.Code,
            FromSiteName = fromSite.Name,
            t.ToSiteId,
            ToSiteCode = toSite.Code,
            ToSiteName = toSite.Name,
            TransferAmount = t.Quantity * (i.SalesPrice > 0 ? i.SalesPrice : i.UnitPrice)
        }).Take(100).ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/api/stock-transfers/summary-by-route", async (string? from, string? to, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusiveStart, out var endExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var baseRows =
        from t in db.StockTransfers.AsNoTracking()
        join i in db.InventoryItems.AsNoTracking() on t.InventoryItemId equals i.Id
        join fromSite in db.Sites.AsNoTracking() on t.FromSiteId equals fromSite.Id
        join toSite in db.Sites.AsNoTracking() on t.ToSiteId equals toSite.Id
        where t.TransferredAtUtc >= fromUtc && t.TransferredAtUtc < endExclusive
        where fromSite.Type == "Factory" && toSite.Type == "SalesCenter"
        select new
        {
            t.Quantity,
            LineAmount = t.Quantity * (i.SalesPrice > 0 ? i.SalesPrice : i.UnitPrice),
            FromSiteId = fromSite.Id,
            FromSiteCode = fromSite.Code,
            FromSiteName = fromSite.Name,
            ToSiteId = toSite.Id,
            ToSiteCode = toSite.Code,
            ToSiteName = toSite.Name
        };

    var grouped = await baseRows
        .GroupBy(x => new { x.FromSiteId, x.ToSiteId, x.FromSiteCode, x.FromSiteName, x.ToSiteCode, x.ToSiteName })
        .Select(g => new
        {
            g.Key.FromSiteId,
            g.Key.ToSiteId,
            g.Key.FromSiteCode,
            g.Key.FromSiteName,
            g.Key.ToSiteCode,
            g.Key.ToSiteName,
            TotalPieces = g.Sum(x => x.Quantity),
            TotalAmount = g.Sum(x => x.LineAmount)
        })
        .OrderBy(x => x.FromSiteCode)
        .ThenBy(x => x.ToSiteCode)
        .ToListAsync();

    var grandTotalPieces = grouped.Sum(x => x.TotalPieces);
    var grandTotalAmount = grouped.Sum(x => x.TotalAmount);

    return Results.Ok(new
    {
        rangeFromUtc = fromUtc,
        rangeToInclusiveUtc = toInclusiveStart,
        rangeEndExclusiveUtc = endExclusive,
        rows = grouped,
        grandTotalPieces,
        grandTotalAmount
    });
});

app.MapPost("/api/sales-transactions", async (HttpRequest httpRequest, BongoTexDbContext db) =>
{
    CreateSalesTransactionRequest req;
    try
    {
        req = await httpRequest.ReadFromJsonAsync<CreateSalesTransactionRequest>(
                  saleBodyJsonOptions,
                  httpRequest.HttpContext.RequestAborted)
              ?? throw new InvalidOperationException("Empty request body.");
    }
    catch (Exception ex)
    {
        return SaleErrors.Bad($"Invalid sale JSON ({ex.GetType().Name}): {ex.Message}");
    }

    if (req.PaidAmount < 0)
    {
        return SaleErrors.Bad("Paid amount cannot be negative.");
    }
    if (req.IsCredit && string.IsNullOrWhiteSpace(req.CustomerName))
    {
        return SaleErrors.Bad("Customer name is required for due/credit sale.");
    }
    if (req.Quantity <= 0)
    {
        return SaleErrors.Bad("Sales quantity must be greater than zero.");
    }

    var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId);
    if (!SalesSiteRules.AllowsStockSale(site))
    {
        return SaleErrors.Bad(
            $"No factory or sales center matched siteId={req.SiteId}. Pick a sell-from site again.");
    }

    var itemId = req.InventoryItemId is { } inv && inv != Guid.Empty ? inv : (Guid?)null;

    if (itemId is null)
    {
        var manualTotal = req.ManualTotalAmount ?? 0;
        if (manualTotal < 0)
        {
            return SaleErrors.Bad("Total amount cannot be negative.");
        }

        var txNoSku = new SalesTransaction
        {
            SalesNo = $"SL-{DateTime.UtcNow:yyyyMMddHHmmss}",
            SiteId = req.SiteId,
            InventoryItemId = null,
            CustomerName = req.CustomerName?.Trim() ?? string.Empty,
            Quantity = req.Quantity,
            UnitPrice = req.Quantity > 0 ? manualTotal / req.Quantity : 0,
            TotalAmount = manualTotal,
            IsCredit = req.IsCredit,
            PaidAmount = req.IsCredit ? req.PaidAmount : manualTotal,
            DueAmount = req.IsCredit ? manualTotal - req.PaidAmount : 0,
            SoldAtUtc = req.SoldAtUtc?.ToUniversalTime() ?? DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

        if (txNoSku.DueAmount < 0)
        {
            return SaleErrors.Bad("Paid amount cannot be greater than total amount.");
        }

        db.SalesTransactions.Add(txNoSku);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            return SaleErrors.Bad($"Could not save sale: {ex.InnerException?.Message ?? ex.Message}");
        }

        return Results.Created($"/api/sales-transactions/{txNoSku.Id}", txNoSku);
    }

    var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == itemId);
    if (item is null)
    {
        return SaleErrors.Bad("Inventory item not found.");
    }

    var stock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == itemId && x.SiteId == req.SiteId);
    if (stock is null || stock.Quantity < req.Quantity)
    {
        return SaleErrors.Bad($"Insufficient stock at selected {SalesSiteRules.LocationLabel(site!)}.");
    }

    if (!SalesPricing.TryResolveUnitPrice(item, req.UseDiscountPrice, req.SellingUnitPrice, out var appliedUnitPrice, out var resolveErr))
    {
        return SaleErrors.Bad(resolveErr ?? "Invalid final selling price. Check Sales Price and Discount Price.");
    }

    var tx = new SalesTransaction
    {
        SalesNo = $"SL-{DateTime.UtcNow:yyyyMMddHHmmss}",
        SiteId = req.SiteId,
        InventoryItemId = itemId,
        CustomerName = req.CustomerName?.Trim() ?? string.Empty,
        Quantity = req.Quantity,
        UnitPrice = appliedUnitPrice,
        TotalAmount = appliedUnitPrice * req.Quantity,
        IsCredit = req.IsCredit,
        PaidAmount = req.IsCredit ? req.PaidAmount : appliedUnitPrice * req.Quantity,
        DueAmount = req.IsCredit ? (appliedUnitPrice * req.Quantity) - req.PaidAmount : 0,
        SoldAtUtc = req.SoldAtUtc?.ToUniversalTime() ?? DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow
    };

    if (tx.DueAmount < 0)
    {
        return SaleErrors.Bad("Paid amount cannot be greater than total amount.");
    }

    stock.Quantity -= req.Quantity;
    SalesPrintSnapshot.ApplyFromItem(tx, item);
    db.SalesTransactions.Add(tx);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex)
    {
        return SaleErrors.Bad($"Could not save sale: {ex.InnerException?.Message ?? ex.Message}");
    }

        return Results.Created($"/api/sales-transactions/{tx.Id}", tx);
});

app.MapPost("/api/sales-transactions/batch", async (HttpRequest httpRequest, BongoTexDbContext db) =>
{
    CreateSalesInvoiceRequest req;
    try
    {
        req = await httpRequest.ReadFromJsonAsync<CreateSalesInvoiceRequest>(
                  saleBodyJsonOptions,
                  httpRequest.HttpContext.RequestAborted)
              ?? throw new InvalidOperationException("Empty request body.");
    }
    catch (Exception ex)
    {
        return SaleErrors.Bad($"Invalid invoice JSON ({ex.GetType().Name}): {ex.Message}");
    }

    if (req.Lines is null || req.Lines.Count == 0)
    {
        return SaleErrors.Bad("Add at least one item line.");
    }

    if (req.PaidAmount < 0)
    {
        return SaleErrors.Bad("Paid amount cannot be negative.");
    }
    if (req.IsCredit && string.IsNullOrWhiteSpace(req.CustomerName))
    {
        return SaleErrors.Bad("Customer name is required for due/credit sale.");
    }

    var ct = httpRequest.HttpContext.RequestAborted;

    var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId, ct);
    if (!SalesSiteRules.AllowsStockSale(site))
    {
        return SaleErrors.Bad(
            $"No factory or sales center matched siteId={req.SiteId}. Pick a sell-from site again.");
    }

    var rawLines = req.Lines.Where(l => l.InventoryItemId != Guid.Empty && l.Quantity > 0).ToList();
    if (rawLines.Count == 0)
    {
        return SaleErrors.Bad("Each line needs a selected item and quantity greater than zero.");
    }

    var itemIds = rawLines.Select(l => l.InventoryItemId).Distinct().ToList();
    var itemsLookup = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

    foreach (var line in rawLines)
    {
        if (!itemsLookup.TryGetValue(line.InventoryItemId, out _))
        {
            return SaleErrors.Bad("Inventory item not found.");
        }
    }

    var resolvedLines = new List<(Guid ItemId, int Quantity, decimal UnitPrice)>();
    foreach (var line in rawLines)
    {
        var item = itemsLookup[line.InventoryItemId];
        if (!SalesPricing.TryResolveUnitPrice(item, req.UseDiscountPrice, line.SellingUnitPrice, out var unitPrice, out var priceError))
        {
            return SaleErrors.Bad(priceError ?? $"Invalid selling price for {item.Sku}.");
        }

        resolvedLines.Add((line.InventoryItemId, line.Quantity, unitPrice));
    }

    var qtyByItemId = resolvedLines.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

    var stocksLookup = await db.InventoryStocks
        .Where(s => itemIds.Contains(s.InventoryItemId) && s.SiteId == req.SiteId)
        .ToDictionaryAsync(s => s.InventoryItemId, ct);

    foreach (var kv in qtyByItemId)
    {
        if (!stocksLookup.TryGetValue(kv.Key, out var st) || st.Quantity < kv.Value)
        {
            return SaleErrors.Bad($"Insufficient stock at selected {SalesSiteRules.LocationLabel(site!)}.");
        }
    }

    decimal invoiceTotal = 0;
    var linePayloads = new List<(InventoryItem Item, int Quantity, decimal UnitPrice, decimal LineTotal)>();

    foreach (var rl in resolvedLines)
    {
        var item = itemsLookup[rl.ItemId];
        var lineTotal = rl.UnitPrice * rl.Quantity;
        invoiceTotal += lineTotal;
        linePayloads.Add((item, rl.Quantity, rl.UnitPrice, lineTotal));
    }

    if (req.IsCredit && req.PaidAmount > invoiceTotal)
    {
        return SaleErrors.Bad("Paid amount cannot be greater than invoice total.");
    }

    var soldAtUtc = req.SoldAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
    var invoiceNo = (req.InvoiceNo ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(invoiceNo))
    {
        return SaleErrors.Bad("Invoice number is required.");
    }
    if (invoiceNo.Length > 46)
    {
        return SaleErrors.Bad("Invoice number is too long (max 46 characters).");
    }
    if (await db.SalesTransactions.AnyAsync(x => x.InvoiceNo == invoiceNo, ct))
    {
        return SaleErrors.Bad($"Invoice number '{invoiceNo}' already exists. Use next sequential number.");
    }
    var customerName = req.CustomerName?.Trim() ?? string.Empty;

    await using var dbTx = await db.Database.BeginTransactionAsync(ct);
    try
    {
        for (var idx = 0; idx < linePayloads.Count; idx++)
        {
            var lp = linePayloads[idx];
            var stockRow = stocksLookup[lp.Item.Id];
            stockRow.Quantity -= lp.Quantity;

            decimal linePaid;
            decimal lineDue;
            if (req.IsCredit)
            {
                if (idx == 0)
                {
                    linePaid = req.PaidAmount;
                    lineDue = invoiceTotal - req.PaidAmount;
                }
                else
                {
                    linePaid = 0;
                    lineDue = 0;
                }
            }
            else
            {
                linePaid = lp.LineTotal;
                lineDue = 0;
            }

            var salesNoBase = $"{invoiceNo}-{(idx + 1):D2}";
            if (salesNoBase.Length > 40)
                salesNoBase = salesNoBase[..40];

            var txRow = new SalesTransaction
            {
                SalesNo = salesNoBase,
                InvoiceNo = invoiceNo,
                SiteId = req.SiteId,
                InventoryItemId = lp.Item.Id,
                CustomerName = customerName,
                Quantity = lp.Quantity,
                UnitPrice = lp.UnitPrice,
                TotalAmount = lp.LineTotal,
                IsCredit = req.IsCredit,
                PaidAmount = linePaid,
                DueAmount = lineDue,
                SoldAtUtc = soldAtUtc,
                CreatedAtUtc = DateTime.UtcNow
            };

            if (txRow.DueAmount < 0)
            {
                await dbTx.RollbackAsync(ct);
                return SaleErrors.Bad("Paid amount cannot be greater than total amount.");
            }

            SalesPrintSnapshot.ApplyFromItem(txRow, lp.Item);
            db.SalesTransactions.Add(txRow);
        }

        await db.SaveChangesAsync(ct);
        await dbTx.CommitAsync(ct);

        var createdRows = await db.SalesTransactions
            .Where(x => x.InvoiceNo == invoiceNo)
            .Select(x => new { x.Id, x.SalesNo, x.InventoryItemId, x.Quantity, x.TotalAmount })
            .ToListAsync(ct);

        return Results.Created($"/api/sales-transactions?invoiceNo={Uri.EscapeDataString(invoiceNo)}",
            new { invoiceNo, totalAmount = invoiceTotal, lines = createdRows });
    }
    catch (DbUpdateException ex)
    {
        await dbTx.RollbackAsync(ct);
        var message = ex.InnerException?.Message ?? ex.Message;
        return SaleErrors.Bad($"Could not save invoice: {message}");
    }
});

app.MapDelete("/api/sales-transactions/by-invoice/{invoiceNo}", async (string invoiceNo, BongoTexDbContext db) =>
{
    var key = Uri.UnescapeDataString(invoiceNo ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(key))
    {
        return SaleErrors.Bad("Invoice number required.");
    }

    var txs = await db.SalesTransactions.Where(x => x.InvoiceNo == key).ToListAsync();
    if (txs.Count == 0)
    {
        return Results.NotFound("No transactions for this invoice.");
    }

    var ids = txs.Select(t => t.Id).ToList();
    var cols = await db.SalesCollections.Where(c => ids.Contains(c.SalesTransactionId)).ToListAsync();
    if (cols.Count > 0)
    {
        db.SalesCollections.RemoveRange(cols);
    }

    foreach (var tx in txs)
    {
        if (tx.InventoryItemId is { } invId)
        {
            var stock = await db.InventoryStocks
                .FirstOrDefaultAsync(x => x.InventoryItemId == invId && x.SiteId == tx.SiteId);

            if (stock is null)
            {
                return SaleErrors.Bad("Stock record missing while reversing invoice. Check data.");
            }

            stock.Quantity += tx.Quantity;
        }
    }

    db.SalesTransactions.RemoveRange(txs);
    await db.SaveChangesAsync();

    return Results.Ok(new { removed = txs.Count });
});

app.MapGet("/api/sales-transactions", async (Guid? siteId, string? from, string? to, BongoTexDbContext db) =>
{
    DateTime? rangeFromUtc = null;
    DateTime? rangeEndExclusive = null;
    if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
    {
        if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out _, out var endExclusive, out var rangeError))
        {
            return Results.BadRequest(new { error = rangeError });
        }

        rangeFromUtc = fromUtc;
        rangeEndExclusive = endExclusive;
    }

    var salesQuery = db.SalesTransactions.AsNoTracking();
    if (siteId is { } sid && sid != Guid.Empty)
    {
        salesQuery = salesQuery.Where(s => s.SiteId == sid);
    }

    if (rangeFromUtc.HasValue)
    {
        salesQuery = salesQuery.Where(s => s.SoldAtUtc >= rangeFromUtc.Value && s.SoldAtUtc < rangeEndExclusive!.Value);
    }

    var takeLimit = siteId.HasValue || rangeFromUtc.HasValue ? 2000 : 200;
    var rows = await (
        from s in salesQuery
        join site in db.Sites.AsNoTracking() on s.SiteId equals site.Id
        join i in db.InventoryItems.AsNoTracking() on s.InventoryItemId equals i.Id into itemJoin
        from i in itemJoin.DefaultIfEmpty()
        orderby s.CreatedAtUtc descending, s.SoldAtUtc descending
        select new
        {
            s.Id,
            s.SalesNo,
            s.InvoiceNo,
            s.CreatedAtUtc,
            s.SoldAtUtc,
            s.CustomerName,
            s.Quantity,
            s.UnitPrice,
            s.TotalAmount,
            s.IsCredit,
            s.PaidAmount,
            s.DueAmount,
            s.InventoryItemId,
            ItemName = i != null ? i.Name : "Daily total (no SKU)",
            ItemSku = i != null ? i.Sku : "",
            s.SiteId,
            SiteCode = site.Code,
            SiteName = site.Name,
            SiteType = site.Type
        }).Take(takeLimit).ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/api/sales-transactions/last-sold-price", async (
    Guid siteId,
    Guid inventoryItemId,
    string customerName,
    BongoTexDbContext db) =>
{
    if (siteId == Guid.Empty || inventoryItemId == Guid.Empty)
        return Results.BadRequest(new { error = "siteId and inventoryItemId are required." });

    var customer = (customerName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(customer))
        return Results.BadRequest(new { error = "customerName is required." });

    var sales = await db.SalesTransactions
        .AsNoTracking()
        .Where(x => x.SiteId == siteId
                    && x.InventoryItemId == inventoryItemId
                    && x.CustomerName != null
                    && x.CustomerName != "")
        .ToListAsync();

    var match = sales
        .Where(x => string.Equals(x.CustomerName.Trim(), customer, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.SoldAtUtc)
        .ThenByDescending(x => x.CreatedAtUtc)
        .FirstOrDefault();

    if (match is null)
        return Results.Ok(new { found = false });

    var item = await db.InventoryItems.AsNoTracking()
        .FirstOrDefaultAsync(i => i.Id == inventoryItemId);
    var listPrice = item?.SalesPrice ?? 0m;
    var netUnit = match.UnitPrice;
    var discountPerUnit = Math.Max(0m, listPrice - netUnit);

    return Results.Ok(new
    {
        found = true,
        unitPrice = netUnit,
        listPrice,
        discountPerUnit,
        invoiceNo = match.InvoiceNo ?? match.SalesNo,
        soldAtUtc = match.SoldAtUtc,
        quantity = match.Quantity
    });
});

app.MapPut("/api/sales-transactions/{id:guid}", async (Guid id, HttpRequest httpRequest, BongoTexDbContext db) =>
{
    UpdateSalesTransactionRequest req;
    try
    {
        req = await httpRequest.ReadFromJsonAsync<UpdateSalesTransactionRequest>(
                  saleBodyJsonOptions,
                  httpRequest.HttpContext.RequestAborted)
              ?? throw new InvalidOperationException("Empty request body.");
    }
    catch (Exception ex)
    {
        return SaleErrors.Bad($"Invalid sale update JSON ({ex.GetType().Name}): {ex.Message}");
    }

    var tx = await db.SalesTransactions.FirstOrDefaultAsync(x => x.Id == id);
    if (tx is null)
    {
        return Results.NotFound("Sales transaction not found.");
    }

    if (req.Quantity <= 0)
    {
        return SaleErrors.Bad("Sales quantity must be greater than zero.");
    }
    if (req.PaidAmount < 0)
    {
        return SaleErrors.Bad("Paid amount cannot be negative.");
    }
    if (req.IsCredit && string.IsNullOrWhiteSpace(req.CustomerName))
    {
        return SaleErrors.Bad("Customer name is required for due/credit sale.");
    }

    if (tx.InventoryItemId is null)
    {
        tx.Quantity = req.Quantity;
        if (req.ManualTotalAmount is { } mt && mt >= 0)
        {
            tx.TotalAmount = mt;
        }
        tx.UnitPrice = tx.Quantity > 0 ? tx.TotalAmount / tx.Quantity : 0;
        tx.IsCredit = req.IsCredit;
        tx.PaidAmount = req.IsCredit ? req.PaidAmount : tx.TotalAmount;
        tx.DueAmount = req.IsCredit ? tx.TotalAmount - tx.PaidAmount : 0;
        tx.CustomerName = req.CustomerName?.Trim() ?? string.Empty;
        tx.SoldAtUtc = req.SoldAtUtc?.ToUniversalTime() ?? tx.SoldAtUtc;

        if (tx.DueAmount < 0)
        {
            return SaleErrors.Bad("Paid amount cannot be greater than total amount.");
        }

        await db.SaveChangesAsync();
        return Results.Ok(tx);
    }

    var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == tx.InventoryItemId);
    if (item is null)
    {
        return SaleErrors.Bad("Inventory item not found.");
    }

    var stock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == tx.InventoryItemId && x.SiteId == tx.SiteId);
    var saleSite = await db.Sites.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tx.SiteId);
    if (stock is null)
    {
        return SaleErrors.Bad(saleSite is null
            ? "Stock record not found for this sale site."
            : $"Stock record not found for this {SalesSiteRules.LocationLabel(saleSite)}.");
    }

    var delta = req.Quantity - tx.Quantity;
    if (delta > 0 && stock.Quantity < delta)
    {
        return SaleErrors.Bad(saleSite is null
            ? "Insufficient stock at sale site for edit."
            : $"Insufficient stock at selected {SalesSiteRules.LocationLabel(saleSite)} for edit.");
    }

    stock.Quantity -= delta;

    if (!SalesPricing.TryResolveUnitPrice(item, req.UseDiscountPrice, req.SellingUnitPrice, out var appliedUnitPrice, out var resolveErrSku))
    {
        return SaleErrors.Bad(resolveErrSku ?? "Invalid final selling price. Check Sales Price and Discount Price.");
    }
    tx.Quantity = req.Quantity;
    tx.UnitPrice = appliedUnitPrice;
    tx.TotalAmount = appliedUnitPrice * req.Quantity;
    tx.IsCredit = req.IsCredit;
    tx.PaidAmount = req.IsCredit ? req.PaidAmount : tx.TotalAmount;
    tx.DueAmount = req.IsCredit ? tx.TotalAmount - tx.PaidAmount : 0;
    tx.CustomerName = req.CustomerName?.Trim() ?? string.Empty;
    tx.SoldAtUtc = req.SoldAtUtc?.ToUniversalTime() ?? tx.SoldAtUtc;
    SalesPrintSnapshot.ApplyFromItem(tx, item);

    if (tx.DueAmount < 0)
    {
        return SaleErrors.Bad("Paid amount cannot be greater than total amount.");
    }

    await db.SaveChangesAsync();
    return Results.Ok(tx);
});

app.MapDelete("/api/sales-transactions/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var tx = await db.SalesTransactions.FirstOrDefaultAsync(x => x.Id == id);
    if (tx is null)
    {
        return Results.NotFound("Sales transaction not found.");
    }

    if (tx.InventoryItemId is not null)
    {
        var stock = await db.InventoryStocks
            .FirstOrDefaultAsync(x => x.InventoryItemId == tx.InventoryItemId && x.SiteId == tx.SiteId);
        if (stock is null)
        {
            return SaleErrors.Bad("Stock record not found for this sale site.");
        }

        stock.Quantity += tx.Quantity;
    }

    db.SalesTransactions.Remove(tx);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/sales-transactions/{id:guid}/collections", async (Guid id, CreateSalesCollectionRequest req, BongoTexDbContext db) =>
{
    if (req.Amount <= 0)
    {
        return Results.BadRequest("Collection amount must be greater than zero.");
    }

    var tx = await db.SalesTransactions.FirstOrDefaultAsync(x => x.Id == id);
    if (tx is null)
    {
        return Results.NotFound("Sales transaction not found.");
    }
    if (tx.DueAmount <= 0)
    {
        return Results.BadRequest("This transaction has no due amount.");
    }
    if (req.Amount > tx.DueAmount)
    {
        return Results.BadRequest("Collection amount cannot exceed due amount.");
    }

    var collection = new SalesCollection
    {
        SalesTransactionId = id,
        Amount = req.Amount,
        CollectedAtUtc = req.CollectedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow,
        Note = req.Note?.Trim() ?? string.Empty
    };

    tx.PaidAmount += req.Amount;
    tx.DueAmount -= req.Amount;
    db.SalesCollections.Add(collection);
    await db.SaveChangesAsync();
    return Results.Created($"/api/sales-transactions/{id}/collections/{collection.Id}", collection);
});

app.MapGet("/api/sales-returns", async (BongoTexDbContext db) =>
{
    var rows = await (
        from r in db.SalesReturns
        join s in db.Sites on r.SiteId equals s.Id
        join i in db.InventoryItems on r.InventoryItemId equals i.Id
        orderby r.ReturnedAtUtc descending
        select new
        {
            r.Id,
            r.ReturnNo,
            r.InvoiceNo,
            r.CustomerType,
            r.CustomerName,
            r.Quantity,
            r.UnitPrice,
            r.TotalAmount,
            r.ReturnType,
            r.ActionType,
            r.RefundAmount,
            r.DueCreditApplied,
            r.Reason,
            r.ReturnedAtUtc,
            r.SiteId,
            SiteCode = s.Code,
            SiteName = s.Name,
            r.InventoryItemId,
            ItemSku = i.Sku,
            ItemName = i.Name
        }).Take(300).ToListAsync();
    return Results.Ok(rows);
});

app.MapPost("/api/sales-returns/no-invoice", async (CreateNoInvoiceSalesReturnRequest req, BongoTexDbContext db) =>
{
    if (req.SiteId == Guid.Empty || req.InventoryItemId == Guid.Empty)
        return Results.BadRequest("Site and item are required.");
    if (req.Quantity <= 0)
        return Results.BadRequest("Return quantity must be greater than zero.");
    if (req.UnitPrice <= 0)
        return Results.BadRequest("Unit price must be greater than zero.");
    if (req.RefundAmount < 0)
        return Results.BadRequest("Refund amount cannot be negative.");

    var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId);
    if (!SalesSiteRules.AllowsStockSale(site))
        return Results.BadRequest("Invalid factory or sales center.");

    var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == req.InventoryItemId);
    if (item is null)
        return Results.BadRequest("Inventory item not found.");

    var stock = await db.InventoryStocks.FirstOrDefaultAsync(x => x.SiteId == req.SiteId && x.InventoryItemId == req.InventoryItemId);
    if (stock is null)
        return Results.BadRequest($"Stock row missing for selected {SalesSiteRules.LocationLabel(site!)} and item.");

    var returnNo = $"RET-{site.Code}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    while (await db.SalesReturns.AnyAsync(x => x.ReturnNo == returnNo))
        returnNo = $"RET-{site.Code}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    var customerType = string.IsNullOrWhiteSpace(req.CustomerType) ? "Regular" : req.CustomerType.Trim();
    if (customerType != "Registered" && customerType != "Regular")
        return Results.BadRequest("Customer type must be Registered or Regular.");
    if (string.IsNullOrWhiteSpace(req.CustomerName))
        return Results.BadRequest("Customer name is required.");

    var totalAmount = req.UnitPrice * req.Quantity;
    var actionType = string.IsNullOrWhiteSpace(req.ActionType) ? "Exchange" : req.ActionType.Trim();
    var allowedAction = new[] { "Refund", "Exchange", "StoreCredit" };
    if (!allowedAction.Contains(actionType))
        return Results.BadRequest("Action type must be Refund, Exchange, or StoreCredit.");

    var row = new SalesReturn
    {
        ReturnNo = returnNo,
        InvoiceNo = null,
        SiteId = req.SiteId,
        InventoryItemId = req.InventoryItemId,
        CustomerType = customerType,
        CustomerName = req.CustomerName?.Trim() ?? string.Empty,
        Quantity = req.Quantity,
        UnitPrice = req.UnitPrice,
        TotalAmount = totalAmount,
        ReturnType = "NoInvoice",
        ActionType = actionType,
        RefundAmount = actionType == "Refund" ? req.RefundAmount : 0,
        Reason = req.Reason?.Trim() ?? string.Empty,
        ReturnedAtUtc = req.ReturnedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow
    };

    await using var dbTx = await db.Database.BeginTransactionAsync();
    try
    {
        stock.Quantity += req.Quantity;
        db.SalesReturns.Add(row);

        var dueCreditApplied = 0m;
        if (CustomerDueCreditAdjuster.ReducesCustomerDue(actionType))
        {
            dueCreditApplied = await CustomerDueCreditAdjuster.ApplyReturnCreditAsync(
                db,
                row.CustomerName,
                totalAmount,
                returnNo,
                row.ReturnedAtUtc);
        }

        row.DueCreditApplied = dueCreditApplied;

        await db.SaveChangesAsync();
        await dbTx.CommitAsync();

        return Results.Created($"/api/sales-returns/{row.Id}", new
        {
            row.Id,
            row.ReturnNo,
            row.CustomerName,
            row.TotalAmount,
            row.ActionType,
            DueCreditApplied = dueCreditApplied
        });
    }
    catch (Exception ex)
    {
        await dbTx.RollbackAsync();
        return Results.BadRequest($"Could not post return: {ex.Message}");
    }
});

app.MapGet("/api/finished-item-gifts", async (
    Guid? siteId,
    DateTime? from,
    DateTime? to,
    BongoTexDbContext db) =>
{
    var q = from g in db.FinishedItemGiftIssues
            join s in db.Sites on g.SiteId equals s.Id
            join i in db.InventoryItems on g.InventoryItemId equals i.Id
            select new { g, s, i };

    if (siteId is { } sid && sid != Guid.Empty)
        q = q.Where(x => x.g.SiteId == sid);

    if (from is { } fromUtc)
    {
        var start = DateTime.SpecifyKind(fromUtc.Date, DateTimeKind.Utc);
        q = q.Where(x => x.g.IssuedAtUtc >= start);
    }

    if (to is { } toUtc)
    {
        var endExclusive = DateTime.SpecifyKind(toUtc.Date, DateTimeKind.Utc).AddDays(1);
        q = q.Where(x => x.g.IssuedAtUtc < endExclusive);
    }

    var rows = await q
        .OrderByDescending(x => x.g.IssuedAtUtc)
        .Select(x => new
        {
            x.g.Id,
            x.g.GiftNo,
            x.g.SiteId,
            SiteCode = x.s.Code,
            SiteName = x.s.Name,
            SiteType = x.s.Type,
            x.g.InventoryItemId,
            ItemSku = x.i.Sku,
            ItemName = x.i.Name,
            x.g.Quantity,
            x.g.UnitCost,
            x.g.TotalCost,
            x.g.RecipientName,
            x.g.Reason,
            x.g.IssuedAtUtc,
            x.g.CreatedAtUtc
        })
        .Take(500)
        .ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/api/finished-item-gifts/summary", async (
    string? month,
    Guid? siteId,
    BongoTexDbContext db) =>
{
    var mk = (month ?? string.Empty).Trim();
    if (!Regex.IsMatch(mk, @"^\d{4}-(0[1-9]|1[0-2])$"))
        return Results.BadRequest("Month is required in YYYY-MM format.");

    if (!DateTime.TryParseExact(
            mk + "-01",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var monthStart))
        return Results.BadRequest("Invalid month.");

    var monthEndExclusive = monthStart.AddMonths(1);

    var q = db.FinishedItemGiftIssues.AsNoTracking()
        .Where(x => x.IssuedAtUtc >= monthStart && x.IssuedAtUtc < monthEndExclusive);

    if (siteId is { } sid && sid != Guid.Empty)
        q = q.Where(x => x.SiteId == sid);

    var totalQty = await q.SumAsync(x => (int?)x.Quantity) ?? 0;
    var totalCost = await q.SumAsync(x => (decimal?)x.TotalCost) ?? 0m;
    var issueCount = await q.CountAsync();

    var bySite = await (
        from g in q
        join s in db.Sites.AsNoTracking() on g.SiteId equals s.Id
        group g by new { s.Id, s.Code, s.Name, s.Type } into grp
        select new
        {
            SiteId = grp.Key.Id,
            SiteCode = grp.Key.Code,
            SiteName = grp.Key.Name,
            SiteType = grp.Key.Type,
            IssueCount = grp.Count(),
            TotalQuantity = grp.Sum(x => x.Quantity),
            TotalCost = grp.Sum(x => x.TotalCost)
        }).OrderBy(x => x.SiteCode).ToListAsync();

    return Results.Ok(new
    {
        MonthKey = mk,
        SiteId = siteId,
        IssueCount = issueCount,
        TotalQuantity = totalQty,
        TotalCost = totalCost,
        BySite = bySite
    });
});

app.MapPost("/api/finished-item-gifts", async (CreateFinishedItemGiftIssueRequest req, BongoTexDbContext db) =>
{
    if (req.SiteId == Guid.Empty || req.InventoryItemId == Guid.Empty)
        return Results.BadRequest("Site and item are required.");
    if (req.Quantity <= 0)
        return Results.BadRequest("Gift quantity must be greater than zero.");

    var recipient = (req.RecipientName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(recipient))
        return Results.BadRequest("Recipient name is required.");

    var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId);
    if (!SalesSiteRules.AllowsStockSale(site))
        return Results.BadRequest("Gifts can only be issued from a factory or sales centre.");

    var item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == req.InventoryItemId);
    if (item is null)
        return Results.BadRequest("Inventory item not found.");

    var stock = await db.InventoryStocks.FirstOrDefaultAsync(x => x.SiteId == req.SiteId && x.InventoryItemId == req.InventoryItemId);
    if (stock is null)
        return Results.BadRequest($"Stock row missing for selected {SalesSiteRules.LocationLabel(site!)} and item.");
    if (stock.Quantity < req.Quantity)
        return Results.BadRequest($"Insufficient stock at selected {SalesSiteRules.LocationLabel(site!)}.");

    var giftNo = $"GIFT-{site!.Code}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    while (await db.FinishedItemGiftIssues.AnyAsync(x => x.GiftNo == giftNo))
        giftNo = $"GIFT-{site.Code}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    var unitCost = item.UnitPrice;
    var totalCost = unitCost * req.Quantity;
    var issuedAt = req.IssuedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;

    var row = new FinishedItemGiftIssue
    {
        GiftNo = giftNo,
        SiteId = req.SiteId,
        InventoryItemId = req.InventoryItemId,
        Quantity = req.Quantity,
        UnitCost = unitCost,
        TotalCost = totalCost,
        RecipientName = recipient,
        Reason = (req.Reason ?? string.Empty).Trim(),
        IssuedAtUtc = issuedAt,
        CreatedAtUtc = DateTime.UtcNow
    };

    await using var dbTx = await db.Database.BeginTransactionAsync();
    try
    {
        stock.Quantity -= req.Quantity;
        db.FinishedItemGiftIssues.Add(row);
        await db.SaveChangesAsync();
        await dbTx.CommitAsync();

        return Results.Created($"/api/finished-item-gifts/{row.Id}", new
        {
            row.Id,
            row.GiftNo,
            row.SiteId,
            row.InventoryItemId,
            row.Quantity,
            row.UnitCost,
            row.TotalCost,
            row.RecipientName,
            row.Reason,
            row.IssuedAtUtc
        });
    }
    catch (Exception ex)
    {
        await dbTx.RollbackAsync();
        return Results.BadRequest($"Could not post gift issue: {ex.Message}");
    }
});

app.MapPost("/api/sales-returns/reconcile-due-credits", async (string? customer, BongoTexDbContext db) =>
{
    var applied = await CustomerDueCreditAdjuster.ReconcilePendingReturnsAsync(db, customer);
    return Results.Ok(new { DueCreditApplied = applied, Customer = customer });
});

app.MapGet("/api/expense-entries", async (BongoTexDbContext db) =>
{
    var rows = await db.ExpenseEntries
        .OrderByDescending(x => x.ExpenseDateUtc)
        .Take(300)
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapPost("/api/expense-entries", async (CreateExpenseEntryRequest req, BongoTexDbContext db) =>
{
    try
    {
    var category = (req.Category ?? string.Empty).Trim();
    if (string.Equals(category, "Salary", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Salary is paid through Payroll, not Add Expense.");
    var partyName = (req.PartyName ?? string.Empty).Trim();
    if (string.Equals(category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal))
        partyName = FinanceConventions.ManagerFloatPartyName;
    if (string.Equals(category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
        partyName = FinanceConventions.OwnerDrawPartyName;

    var description = (req.Description ?? string.Empty).Trim();
    if (category == "DailyExpense" && string.IsNullOrWhiteSpace(partyName))
        partyName = string.IsNullOrWhiteSpace(description) ? "Daily expense" : description;

    if (string.IsNullOrWhiteSpace(partyName))
    {
        return Results.BadRequest("Party name is required.");
    }
    if (string.IsNullOrWhiteSpace(category))
    {
        return Results.BadRequest("Expense category is required.");
    }
    if (req.Amount <= 0)
    {
        return Results.BadRequest("Expense amount must be greater than zero.");
    }

    var rawScope = (req.ExpenseScope ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(rawScope))
    {
        return Results.BadRequest("Expense scope is required.");
    }
    var expenseScope = rawScope.Equals("Factory", StringComparison.OrdinalIgnoreCase) ? "Factory"
        : rawScope.Equals("SalesCenter", StringComparison.OrdinalIgnoreCase) ? "SalesCenter"
        : rawScope.Equals("Owner", StringComparison.OrdinalIgnoreCase) ? "Owner"
        : rawScope.Equals("PrintFactory", StringComparison.OrdinalIgnoreCase) ? "PrintFactory"
        : rawScope;
    if (expenseScope is not ("Factory" or "SalesCenter" or "Owner" or "PrintFactory"))
    {
        return Results.BadRequest("Expense scope must be Factory, SalesCenter, Owner, or PrintFactory.");
    }
    if (expenseScope == "Owner" && !string.Equals(category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
    {
        return Results.BadRequest("Owner expense scope is only for owner draw.");
    }

    var allowed = new[] { "SupplierPayment", "Salary", "Rent", "DailyExpense", FinanceConventions.ManagerRemittanceCategory, FinanceConventions.OwnerDrawCategory };
    if (!allowed.Contains(category))
    {
        return Results.BadRequest("Invalid category.");
    }
    if (string.Equals(category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
        && expenseScope != "SalesCenter")
    {
        return Results.BadRequest("Manager remittance must use Sales Center expense scope and a sales center site.");
    }
    if (string.Equals(category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal)
        && expenseScope != "Owner"
        && expenseScope != "Factory")
    {
        return Results.BadRequest("Owner draw must use Owner expense scope (recommended) or Factory (legacy).");
    }
    var department = (req.Department ?? string.Empty).Trim();
    if (department.Length > 40)
        return Results.BadRequest("Department is too long.");
    if (string.IsNullOrWhiteSpace(department))
    {
        department = expenseScope switch
        {
            "PrintFactory" => "Print",
            "Factory" => "FactoryGeneral",
            _ => string.Empty
        };
    }
    if (category == "SupplierPayment")
    {
        var supplierExists = await db.Suppliers.AnyAsync(x => x.Name == partyName);
        if (!supplierExists)
        {
            return Results.BadRequest("Supplier must be selected from registered suppliers.");
        }
    }
    var salaryPaymentType = (req.SalaryPaymentType ?? string.Empty).Trim();
    var salaryForMonth = (req.SalaryForMonth ?? string.Empty).Trim();
    if (category == "Salary")
    {
        var allowedSalaryTypes = new[] { "Advance", "Due", "Current" };
        if (!allowedSalaryTypes.Contains(salaryPaymentType))
            return Results.BadRequest("Salary payment type must be Advance, Due, or Current.");
        if (!Regex.IsMatch(salaryForMonth, @"^\d{4}-(0[1-9]|1[0-2])$"))
            return Results.BadRequest("Salary month is required in YYYY-MM format.");
        var employee = await db.Employees
            .Where(x => x.Name == partyName || x.EmployeeCode == partyName)
            .OrderBy(x => x.Name)
            .FirstOrDefaultAsync();
        if (employee is null)
            return Results.BadRequest("Employee must be selected from registered employees.");

        var monthlySalary = employee.MonthlySalary;
        if (monthlySalary <= 0)
            return Results.BadRequest("Selected employee has no monthly salary configured.");

        var payrollCap = await (
            from l in db.PayrollLines
            join r in db.PayrollRuns on l.PayrollRunId equals r.Id
            where r.MonthKey == salaryForMonth && l.EmployeeId == employee.Id
            orderby r.CreatedAtUtc descending
            select new { l.AttendanceSalaryAmount, l.OvertimeAmount, l.AttendanceBonus, l.SnakesPay }).FirstOrDefaultAsync();
        var grossCap = payrollCap != null
            ? payrollCap.AttendanceSalaryAmount + payrollCap.OvertimeAmount + payrollCap.AttendanceBonus + payrollCap.SnakesPay
            : monthlySalary;

        var yearMonthStartUtc = DateTime.ParseExact(
            salaryForMonth + "-01",
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var nextMonthStartUtc = yearMonthStartUtc.AddMonths(1);

        var alreadyAdvanceOrCurrent = await db.ExpenseEntries
            .Where(x => x.Category == "Salary"
                        && x.PartyName == employee.Name
                        && (
                            // New rows: explicit month + type.
                            (x.SalaryForMonth == salaryForMonth
                                && (x.SalaryPaymentType == "Advance" || x.SalaryPaymentType == "Current"))
                            // Legacy rows: month/type may be blank; infer month from expense date and treat blank type as Current.
                            || ((x.SalaryForMonth == null || x.SalaryForMonth == "")
                                && x.ExpenseDateUtc >= yearMonthStartUtc
                                && x.ExpenseDateUtc < nextMonthStartUtc
                                && (x.SalaryPaymentType == "Advance" || x.SalaryPaymentType == "Current" || x.SalaryPaymentType == null || x.SalaryPaymentType == ""))
                           ))
            .SumAsync(x => (decimal?)x.Amount) ?? 0m;
        var remainingBeforeThis = grossCap - alreadyAdvanceOrCurrent;

        if (salaryPaymentType == "Current")
        {
            if (remainingBeforeThis <= 0)
                return Results.BadRequest($"No current salary remaining for {employee.Name} in {salaryForMonth}. Advance/current already reached gross cap {grossCap:0.##}.");
            if (req.Amount > remainingBeforeThis)
                return Results.BadRequest($"Current salary exceeds remaining amount. Gross cap: {grossCap:0.##}, already advance/current paid: {alreadyAdvanceOrCurrent:0.##}, remaining: {remainingBeforeThis:0.##}.");
        }

        if (salaryPaymentType == "Advance")
        {
            if (req.Amount > remainingBeforeThis)
                return Results.BadRequest($"Advance salary exceeds limit. Gross cap: {grossCap:0.##}, already advance/current paid: {alreadyAdvanceOrCurrent:0.##}, remaining payable: {Math.Max(0, remainingBeforeThis):0.##}.");
        }

        // Persist employee name as canonical party name for stable month-wise salary calculations.
        partyName = employee.Name;
    }
    Guid? siteId = null;
    if (!string.IsNullOrWhiteSpace(req.SiteId))
    {
        if (!Guid.TryParse(req.SiteId.Trim(), out var parsedSiteId))
            return Results.BadRequest("Invalid sales center id.");
        siteId = parsedSiteId;
    }

    if (expenseScope == "SalesCenter")
    {
        if (!siteId.HasValue)
            return Results.BadRequest("Sales center is required for sales center expense.");
        var salesCenter = await db.Sites.FirstOrDefaultAsync(x => x.Id == siteId.Value && x.Type == "SalesCenter");
        if (salesCenter is null)
            return Results.BadRequest("Invalid sales center.");
    }

    var entry = new ExpenseEntry
    {
        ExpenseNo = FinanceConventions.NewExpenseNo(),
        Category = category,
        PartyName = partyName,
        ExpenseScope = expenseScope,
        SiteId = expenseScope == "SalesCenter" ? siteId : null,
        Amount = req.Amount,
        Department = department,
        Description = description,
        SalaryPaymentType = category == "Salary" ? salaryPaymentType : string.Empty,
        SalaryForMonth = category == "Salary" ? salaryForMonth : string.Empty,
        ExpenseDateUtc = req.ExpenseDateUtc?.ToUniversalTime() ?? DateTime.UtcNow
    };

    var cashbookNote = (req.CashbookNote ?? string.Empty).Trim();
    if (cashbookNote.Length > 250)
        cashbookNote = cashbookNote[..250];
    ManagerCashbook.AssignCashbookForNewEntry(entry, category, partyName, expenseScope, cashbookNote);

    var cashErr = await DailyCashBalanceReport.ValidateExpenseCashAsync(
        db, expenseScope, category, entry.SiteId, entry.Amount, entry.ExpenseDateUtc);
    if (cashErr is not null)
        return Results.BadRequest(new { error = cashErr });

    db.ExpenseEntries.Add(entry);
    await db.SaveChangesAsync();
    if (category == "Salary")
    {
        await PayrollSalaryOps.SyncPayrollLineFromSalaryExpensesAsync(
            db, partyName, salaryForMonth, expenseScope, entry.SiteId);
        await db.SaveChangesAsync();
    }
    return Results.Created($"/api/expense-entries/{entry.Id}", entry);
    }
    catch (DbUpdateException ex)
    {
        var detail = ex.InnerException?.Message ?? ex.Message;
        return Results.Json(new { error = "Could not save expense to database. Restart the API after rebuild.", detail }, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message, detail = ex.InnerException?.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/finance/summary", async (BongoTexDbContext db, CancellationToken cancellationToken) =>
{
    var totalSales = await db.SalesTransactions.SumAsync(x => (decimal?)x.TotalAmount, cancellationToken) ?? 0;
    var totalReceived = await db.SalesTransactions.SumAsync(x => (decimal?)x.PaidAmount, cancellationToken) ?? 0;
    var totalDue = await db.SalesTransactions.SumAsync(x => (decimal?)x.DueAmount, cancellationToken) ?? 0;
    var totalExpenses = await db.ExpenseEntries.SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0;
    var netCash = totalReceived - totalExpenses;

    var utc = DateTime.UtcNow;
    var throughEndOfTodayUtcExclusive = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
    var maxMovementUtc = await db.CashMovements.AsNoTracking()
        .Select(x => (DateTime?)x.MovementDateUtc)
        .MaxAsync(cancellationToken);
    var asOfExclusive = throughEndOfTodayUtcExclusive;
    if (maxMovementUtc is { } mvRaw)
    {
        var mv = mvRaw.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(mvRaw, DateTimeKind.Utc) : mvRaw.ToUniversalTime();
        var movementDayStartUtc = new DateTime(mv.Year, mv.Month, mv.Day, 0, 0, 0, DateTimeKind.Utc);
        var throughEndOfLatestMovementDayExclusive = movementDayStartUtc.AddDays(1);
        if (throughEndOfLatestMovementDayExclusive > asOfExclusive)
            asOfExclusive = throughEndOfLatestMovementDayExclusive;
    }

    var wallets = await DailyCashBalanceReport.ComputeWalletBalancesAsOfAsync(
        db,
        asOfExclusive,
        0m,
        0m,
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
        cancellationToken);

    return Results.Ok(new
    {
        TotalSales = totalSales,
        TotalReceived = totalReceived,
        TotalDue = totalDue,
        TotalExpenses = totalExpenses,
        NetCash = netCash,
        WalletAsOfExclusiveUtc = wallets.AsOfExclusiveUtc,
        ManagerCashBalance = wallets.ManagerCashBalance,
        FactoryPettyCashBalance = wallets.FactoryPettyCashBalance,
        FactoryPettyCashShortfall = wallets.FactoryPettyCashShortfall,
        SalesCenterCashBalances = wallets.SalesCenters,
        TotalModeledCash = wallets.TotalModeledCash,
        WalletHint = wallets.Hint
    });
});

app.MapGet("/api/finance/print-pl", async (string? from, string? to, BongoTexDbContext db) =>
{
    try
    {
        if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out _, out var toExclusive, out var rangeError))
            return Results.BadRequest(new { error = rangeError });

        await PrintFactorySchema.EnsureAsync(db);
        var pf = await PrintFactoryOps.GetSummaryAsync(db, fromUtc, toExclusive);

        // Legacy garment-side print department (Department=Print, scope Factory) — older entries only.
        var legacyPrintExpenses = await db.ExpenseEntries.AsNoTracking()
            .Where(e => e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive
                && e.ExpenseScope == "Factory"
                && e.Department == "Print")
            .ToListAsync();

        var legacyPrintSalesRows = await db.SalesTransactions.AsNoTracking()
            .Where(s => s.InventoryItemId != null && s.IsPrintItemAtSale
                && s.SoldAtUtc >= fromUtc && s.SoldAtUtc < toExclusive)
            .Select(s => new { s.TotalAmount, s.Quantity, s.PrintChargePerPieceAtSale })
            .ToListAsync();

        return Results.Ok(new
        {
            FromUtc = fromUtc,
            ToExclusiveUtc = toExclusive,
            SalesTotal = pf.SalesTotal,
            SalesInternal = pf.SalesInternal,
            SalesExternal = pf.SalesExternal,
            SalesDue = pf.SalesDue,
            PurchaseTotal = pf.PurchaseTotal,
            PurchaseDue = pf.PurchaseDue,
            ExpenseTotal = pf.ExpenseTotal,
            SalaryTotal = pf.SalaryTotal,
            RentTotal = pf.RentTotal,
            DailyExpenseTotal = pf.DailyExpenseTotal,
            SupplierPaymentTotal = pf.SupplierPaymentTotal,
            OtherExpenseTotal = pf.OtherExpenseTotal,
            NetProfitLoss = pf.NetProfitLoss,
            ExpenseByCategory = pf.ExpenseByCategory,
            StocktakeMonthKey = pf.StocktakeMonthKey,
            OpeningStockValue = pf.OpeningStockValue,
            ClosingStockValue = pf.ClosingStockValue,
            MaterialCostUsed = pf.MaterialCostUsed,
            OperatingExpenses = pf.OperatingExpenses,
            AdjustedNetProfitLoss = pf.AdjustedNetProfitLoss,
            HasClosingStocktake = pf.HasClosingStocktake,
            ExternalRevenue = pf.SalesExternal,
            InternalRevenue = pf.SalesInternal,
            TotalExpense = pf.ExpenseTotal,
            ExternalProfitLoss = pf.SalesExternal - pf.PurchaseTotal - pf.ExpenseTotal,
            InternalProfitLoss = pf.SalesInternal - pf.PurchaseTotal - pf.ExpenseTotal,
            RevenueGap = pf.SalesExternal - pf.SalesInternal,
            LegacyGarmentPrint = new
            {
                RetailExternalRevenue = legacyPrintSalesRows.Sum(x => x.TotalAmount),
                RetailInternalRevenue = legacyPrintSalesRows.Sum(x => x.Quantity * x.PrintChargePerPieceAtSale),
                ExpenseTotal = legacyPrintExpenses.Sum(x => x.Amount),
                ExpenseByCategory = legacyPrintExpenses
                    .GroupBy(x => x.Category)
                    .Select(g => new { Category = g.Key, Amount = g.Sum(x => x.Amount) })
                    .OrderByDescending(x => x.Amount)
                    .ToList()
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "Print P/L report failed.", detail = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/finance/sales-center-closing", async (Guid siteId, string? from, string? to, decimal? opening, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusive, out var toExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId && s.Type == "SalesCenter");
    if (site is null)
        return Results.BadRequest("Invalid sales center.");

    var salesPaid = await db.SalesTransactions.AsNoTracking()
        .Where(x => x.SiteId == siteId && x.SoldAtUtc >= fromUtc && x.SoldAtUtc < toExclusive)
        .SumAsync(x => (decimal?)x.PaidAmount) ?? 0m;

    var dueCollections = await (
        from c in db.SalesCollections.AsNoTracking()
        join s in db.SalesTransactions.AsNoTracking() on c.SalesTransactionId equals s.Id
        where s.SiteId == siteId && c.CollectedAtUtc >= fromUtc && c.CollectedAtUtc < toExclusive
        select (decimal?)c.Amount
    ).SumAsync() ?? 0m;

    var scopedExpenses = db.ExpenseEntries.AsNoTracking()
        .Where(e => e.ExpenseScope == "SalesCenter"
            && e.SiteId == siteId
            && e.ExpenseDateUtc >= fromUtc
            && e.ExpenseDateUtc < toExclusive);

    var remittedToManager = await scopedExpenses
        .Where(e => e.Category == FinanceConventions.ManagerRemittanceCategory)
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;

    var salary = await scopedExpenses.Where(e => e.Category == "Salary")
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;
    var dailyExpense = await scopedExpenses.Where(e => e.Category == "DailyExpense")
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;
    var rent = await scopedExpenses.Where(e => e.Category == "Rent")
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;
    var electricity = await scopedExpenses.Where(e => e.Category == "Electricity")
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;

    var known = new[]
    {
        "Salary", "DailyExpense", "Rent", "Electricity", FinanceConventions.ManagerRemittanceCategory
    };
    var other = await scopedExpenses.Where(e => !known.Contains(e.Category))
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;

    var totalExpense = salary + dailyExpense + rent + electricity + other;
    var openingCash = opening ?? 0m;
    var totalInflow = salesPaid + dueCollections;
    var closingCash = openingCash + totalInflow - totalExpense - remittedToManager;

    return Results.Ok(new
    {
        SiteId = site.Id,
        SiteCode = site.Code,
        SiteName = site.Name,
        FromUtc = fromUtc,
        ToUtc = toInclusive,
        OpeningCash = openingCash,
        SalesPaid = salesPaid,
        DueCollections = dueCollections,
        TotalInflow = totalInflow,
        Expenses = new
        {
            Salary = salary,
            DailyExpense = dailyExpense,
            Rent = rent,
            Electricity = electricity,
            Other = other,
            Total = totalExpense
        },
        RemittedToManager = remittedToManager,
        ClosingCash = closingCash,
        Formula = "opening + inflow - expenses - remittedToManager"
    });
});

app.MapGet("/api/finance/manager-cash-split", async (string? from, string? to, decimal? opening, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusive, out var toExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var remittanceIn = await db.ExpenseEntries.AsNoTracking()
        .Where(e => e.Category == FinanceConventions.ManagerRemittanceCategory
            && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive)
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;

    var ownerDrawOut = await db.ExpenseEntries.AsNoTracking()
        .Where(e => e.Category == FinanceConventions.OwnerDrawCategory
            && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive)
        .SumAsync(e => (decimal?)e.Amount) ?? 0m;

    var toFactoryPetty = await db.CashMovements.AsNoTracking()
        .Where(m => m.FromPool == FinanceConventions.CashPoolManager
            && m.ToPool == FinanceConventions.CashPoolFactoryPetty
            && m.MovementDateUtc >= fromUtc && m.MovementDateUtc < toExclusive)
        .SumAsync(m => (decimal?)m.Amount) ?? 0m;

    var openingManagerCash = opening ?? 0m;
    var totalInflows = remittanceIn;
    var totalOutflows = ownerDrawOut + toFactoryPetty;
    var closingManagerCash = openingManagerCash + totalInflows - totalOutflows;

    var breakdownExpense = await db.ExpenseEntries.AsNoTracking()
        .Where(e =>
            (e.Category == FinanceConventions.ManagerRemittanceCategory || e.Category == FinanceConventions.OwnerDrawCategory)
            && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive)
        .Select(e => new
        {
            DateUtc = e.ExpenseDateUtc,
            Type = e.Category,
            Amount = e.Amount,
            ReferenceNo = e.ExpenseNo,
            Note = e.Description
        })
        .ToListAsync();

    var breakdownMovements = await db.CashMovements.AsNoTracking()
        .Where(m =>
            (m.FromPool == FinanceConventions.CashPoolManager || m.ToPool == FinanceConventions.CashPoolManager)
            && m.MovementDateUtc >= fromUtc && m.MovementDateUtc < toExclusive)
        .Select(m => new
        {
            DateUtc = m.MovementDateUtc,
            Type = $"{m.FromPool}->{m.ToPool}",
            Amount = m.Amount,
            ReferenceNo = m.MovementNo,
            Note = m.Note
        })
        .ToListAsync();

    var breakdown = breakdownExpense
        .Concat(breakdownMovements)
        .OrderBy(x => x.DateUtc)
        .ThenBy(x => x.ReferenceNo)
        .ToList();

    return Results.Ok(new
    {
        FromUtc = fromUtc,
        ToUtc = toInclusive,
        OpeningManagerCash = openingManagerCash,
        Inflows = new { SalesCenterRemittance = remittanceIn, OtherIn = 0m, Total = totalInflows },
        Outflows = new { OwnerDraw = ownerDrawOut, ToFactoryPetty = toFactoryPetty, OtherOut = 0m, Total = totalOutflows },
        ClosingManagerCash = closingManagerCash,
        Formula = "opening + inflows - outflows",
        Breakdown = breakdown
    });
});

app.MapGet("/api/finance/factory-petty-spend", async (string? from, string? to, decimal? opening, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusive, out var toExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var fromManager = await db.CashMovements.AsNoTracking()
        .Where(m => m.ToPool == FinanceConventions.CashPoolFactoryPetty
            && m.MovementDateUtc >= fromUtc && m.MovementDateUtc < toExclusive)
        .SumAsync(m => (decimal?)m.Amount) ?? 0m;

    var scrapCashIn = await FactoryPettyCashbook.SumScrapSaleCashInAsync(db, fromUtc, toExclusive);

    static async Task<(decimal SupplierPayment, decimal Salary, decimal DailyExpense, decimal Rent)> SumOperatingSpendAsync(IQueryable<ExpenseEntry> q)
    {
        var supplierPayment = await q.Where(e => e.Category == "SupplierPayment").SumAsync(e => (decimal?)e.Amount) ?? 0m;
        var salary = await q.Where(e => e.Category == "Salary").SumAsync(e => (decimal?)e.Amount) ?? 0m;
        var dailyExpense = await q.Where(e => e.Category == "DailyExpense").SumAsync(e => (decimal?)e.Amount) ?? 0m;
        var rent = await q.Where(e => e.Category == "Rent").SumAsync(e => (decimal?)e.Amount) ?? 0m;
        return (supplierPayment, salary, dailyExpense, rent);
    }

    var garmentOperatingQ = db.ExpenseEntries.AsNoTracking()
        .Where(e => e.ExpenseScope == "Factory"
            && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive
            && (e.Category == "SupplierPayment" || e.Category == "Salary" || e.Category == "DailyExpense" || e.Category == "Rent"));
    var printOperatingQ = db.ExpenseEntries.AsNoTracking()
        .Where(e => e.ExpenseScope == PrintFactoryConventions.ExpenseScope
            && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive
            && (e.Category == "SupplierPayment" || e.Category == "Salary" || e.Category == "DailyExpense" || e.Category == "Rent"));

    var (gSp, gSal, gDaily, gRent) = await SumOperatingSpendAsync(garmentOperatingQ);
    var (pSp, pSal, pDaily, pRent) = await SumOperatingSpendAsync(printOperatingQ);

    var garmentOperating = new { SupplierPayment = gSp, Salary = gSal, DailyExpense = gDaily, Rent = gRent, Total = gSp + gSal + gDaily + gRent };
    var printOperating = new { SupplierPayment = pSp, Salary = pSal, DailyExpense = pDaily, Rent = pRent, Total = pSp + pSal + pDaily + pRent };

    var garmentOtherQ = db.ExpenseEntries.AsNoTracking()
        .Where(e => e.ExpenseScope == "Factory"
            && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < toExclusive
            && e.Category != FinanceConventions.OwnerDrawCategory
            && e.Category != "SupplierPayment" && e.Category != "Salary" && e.Category != "DailyExpense" && e.Category != "Rent");
    var electricity = await garmentOtherQ.Where(e => e.Category == "Electricity").SumAsync(e => (decimal?)e.Amount) ?? 0m;
    var printCost = await garmentOtherQ.Where(e => e.Department == "Print").SumAsync(e => (decimal?)e.Amount) ?? 0m;
    var garmentOther = await garmentOtherQ.SumAsync(e => (decimal?)e.Amount) ?? 0m;

    var pettyOperatingTotal = garmentOperating.Total + printOperating.Total;
    var openingFactoryPetty = opening ?? 0m;
    var closingFactoryPettyComputed = openingFactoryPetty + fromManager + scrapCashIn - pettyOperatingTotal;
    var (closingFactoryPetty, closingShortfall) = FactoryPettyBalanceOps.Normalize(closingFactoryPettyComputed);

    return Results.Ok(new
    {
        FromUtc = fromUtc,
        ToUtc = toInclusive,
        OpeningFactoryPetty = openingFactoryPetty,
        Inflows = new { FromManager = fromManager, ScrapWastageSales = scrapCashIn, Total = fromManager + scrapCashIn },
        SpendGarment = garmentOperating,
        SpendPrintFactory = printOperating,
        Spend = new
        {
            SupplierPayment = gSp + pSp,
            Salary = gSal + pSal,
            PrintCost = printCost,
            DailyExpense = gDaily + pDaily,
            Electricity = electricity,
            Rent = gRent + pRent,
            Other = garmentOther,
            GarmentOtherNonPetty = garmentOther,
            Total = pettyOperatingTotal
        },
        ClosingFactoryPetty = closingFactoryPetty,
        ClosingFactoryPettyShortfall = closingShortfall,
        Formula = "opening + inflows (manager transfers + scrap/wastage sale cash) - garment & print factory operating spend (same petty cash box). On hand is never below 0; shortfall = unrecorded inflow."
    });
});

app.MapGet("/api/finance/conventions", () =>
    Results.Ok(new
    {
        ManagerRemittanceCategory = FinanceConventions.ManagerRemittanceCategory,
        ManagerFloatPartyName = FinanceConventions.ManagerFloatPartyName,
        OwnerDrawCategory = FinanceConventions.OwnerDrawCategory,
        OwnerDrawPartyName = FinanceConventions.OwnerDrawPartyName,
        CashflowIn = FinanceConventions.CashflowIn,
        CashflowOut = FinanceConventions.CashflowOut,
        CashbookManagerFloatIn = FinanceConventions.CashbookManagerFloatIn,
        CashbookFactorySpend = FinanceConventions.CashbookFactorySpend,
        CashbookOwnerDraw = FinanceConventions.CashbookOwnerDraw,
        CashbookOtherOutflow = FinanceConventions.CashbookOtherOutflow,
        CashPoolSalesCenter = FinanceConventions.CashPoolSalesCenter,
        CashPoolManager = FinanceConventions.CashPoolManager,
        CashPoolFactoryPetty = FinanceConventions.CashPoolFactoryPetty
    }));

app.MapGet("/api/finance/manager-float-summary", async (string? from, string? to, decimal? openingStated, decimal? closingStated, BongoTexDbContext db, CancellationToken ct) =>
    await ManagerCashbook.SummaryAsync(db, from, to, openingStated, closingStated, ct));

app.MapGet("/api/finance/manager-cashbook/summary", async (string? from, string? to, decimal? openingStated, decimal? closingStated, BongoTexDbContext db, CancellationToken ct) =>
    await ManagerCashbook.SummaryAsync(db, from, to, openingStated, closingStated, ct));

app.MapGet("/api/finance/manager-cashbook", async (string? from, string? to, BongoTexDbContext db, CancellationToken ct) =>
    await ManagerCashbook.DetailLedgerAsync(db, from, to, ct));

app.MapPost("/api/cash-movements", async (CreateCashMovementRequest req, BongoTexDbContext db) =>
{
    var from = (req.FromPool ?? string.Empty).Trim();
    var to = (req.ToPool ?? string.Empty).Trim();
    if (!FinanceConventions.IsKnownCashPool(from) || !FinanceConventions.IsKnownCashPool(to))
        return Results.BadRequest("FromPool and ToPool must each be SalesCenter, Manager, or FactoryPetty (exact spelling).");
    if (string.Equals(from, to, StringComparison.Ordinal))
        return Results.BadRequest("From and to pool must differ.");
    if (string.Equals(from, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal)
        && string.Equals(to, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal))
        return Results.BadRequest("Sales center cannot transfer directly to factory petty. Transfer to Manager first.");
    if (req.Amount <= 0)
        return Results.BadRequest("Amount must be greater than zero.");

    Site? fromSalesCenterSite = null;
    if (string.Equals(from, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal))
    {
        if (!req.FromSiteId.HasValue || req.FromSiteId.Value == Guid.Empty)
            return Results.BadRequest("FromSiteId is required when moving cash from a sales center.");
        fromSalesCenterSite = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.FromSiteId.Value && x.Type == "SalesCenter");
        if (fromSalesCenterSite is null)
            return Results.BadRequest("Invalid from sales center site.");
    }
    else if (req.FromSiteId.HasValue && req.FromSiteId.Value != Guid.Empty)
        return Results.BadRequest("FromSiteId must be empty unless from pool is SalesCenter.");

    if (string.Equals(to, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal))
    {
        if (!req.ToSiteId.HasValue || req.ToSiteId.Value == Guid.Empty)
            return Results.BadRequest("ToSiteId is required when moving cash to a sales center.");
        var toSite = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.ToSiteId.Value && x.Type == "SalesCenter");
        if (toSite is null)
            return Results.BadRequest("Invalid to sales center site.");
    }
    else if (req.ToSiteId.HasValue && req.ToSiteId.Value != Guid.Empty)
        return Results.BadRequest("ToSiteId must be empty unless to pool is SalesCenter.");

    var note = (req.Note ?? string.Empty).Trim();
    if (note.Length > 500)
        note = note[..500];

    var movementDate = req.MovementDateUtc?.ToUniversalTime() ?? DateTime.UtcNow;
    var fromSiteIdForBalance = string.Equals(from, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal)
        ? req.FromSiteId
        : null;
    var available = await DailyCashBalanceReport.GetCashPoolBalanceAsOfAsync(
        db, from, fromSiteIdForBalance, movementDate);
    if (req.Amount > available + 0.0001m)
    {
        var poolLabel = DailyCashBalanceReport.DescribeCashPool(from, fromSalesCenterSite);
        return Results.BadRequest(new
        {
            error = $"Insufficient cash in {poolLabel}. Available balance is {available:0.00}; transfer amount is {req.Amount:0.00}.",
            availableBalance = available,
            requestedAmount = req.Amount,
            fromPool = from,
            fromSiteId = fromSiteIdForBalance
        });
    }

    var row = new CashMovement
    {
        MovementNo = $"CM-{DateTime.UtcNow:yyyyMMddHHmmss}",
        FromPool = from,
        ToPool = to,
        Amount = req.Amount,
        FromSiteId = string.Equals(from, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) ? req.FromSiteId : null,
        ToSiteId = string.Equals(to, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) ? req.ToSiteId : null,
        Note = note,
        MovementDateUtc = movementDate,
        CreatedAtUtc = DateTime.UtcNow
    };

    db.CashMovements.Add(row);
    await db.SaveChangesAsync();
    return Results.Created($"/api/cash-movements/{row.Id}", row);
});

app.MapGet("/api/cash-movements", async (string? from, string? to, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusiveStart, out var endExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var rows = await db.CashMovements
        .Where(x => x.MovementDateUtc >= fromUtc && x.MovementDateUtc < endExclusive)
        .OrderByDescending(x => x.MovementDateUtc)
        .ThenByDescending(x => x.MovementNo)
        .Take(500)
        .ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/api/finance/cash-pool-summary", async (string? from, string? to, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusiveStart, out var endExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var movements = await db.CashMovements
        .Where(x => x.MovementDateUtc >= fromUtc && x.MovementDateUtc < endExclusive)
        .OrderBy(x => x.MovementDateUtc)
        .ThenBy(x => x.MovementNo)
        .ToListAsync();

    static string PoolDeltaKey(string pool, Guid? siteId)
    {
        if (string.Equals(pool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal))
        {
            if (siteId.HasValue && siteId.Value != Guid.Empty)
                return $"{FinanceConventions.CashPoolSalesCenter}:{siteId.Value:N}";
            return $"{FinanceConventions.CashPoolSalesCenter}:unspecified";
        }

        return pool;
    }

    var deltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
    foreach (var m in movements)
    {
        var fromKey = PoolDeltaKey(m.FromPool, m.FromSiteId);
        var toKey = PoolDeltaKey(m.ToPool, m.ToSiteId);
        if (!deltas.TryGetValue(fromKey, out var fromVal))
            fromVal = 0;
        deltas[fromKey] = fromVal - m.Amount;
        if (!deltas.TryGetValue(toKey, out var toVal))
            toVal = 0;
        deltas[toKey] = toVal + m.Amount;
    }

    var orderedDeltas = deltas.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);

    var siteIds = movements.Select(x => x.FromSiteId).Concat(movements.Select(x => x.ToSiteId))
        .Where(x => x.HasValue && x.Value != Guid.Empty)
        .Select(x => x!.Value)
        .Distinct()
        .ToList();
    var siteLookup = siteIds.Count == 0
        ? new Dictionary<Guid, string>()
        : await db.Sites.Where(x => siteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => $"{x.Code} - {x.Name}");

    return Results.Ok(new
    {
        FromUtc = fromUtc,
        ToInclusiveUtc = toInclusiveStart,
        PoolDeltas = orderedDeltas,
        Movements = movements.Select(m => new
        {
            m.Id,
            m.MovementNo,
            m.FromPool,
            m.ToPool,
            m.Amount,
            m.FromSiteId,
            m.ToSiteId,
            FromSiteLabel = m.FromSiteId.HasValue && siteLookup.TryGetValue(m.FromSiteId.Value, out var fl) ? fl : "",
            ToSiteLabel = m.ToSiteId.HasValue && siteLookup.TryGetValue(m.ToSiteId.Value, out var tl) ? tl : "",
            m.Note,
            m.MovementDateUtc,
            m.CreatedAtUtc
        })
    });
});

app.MapGet("/api/finance/customer-due-ledger", async (string? q, BongoTexDbContext db) =>
{
    var salesTxs = await db.SalesTransactions
        .AsNoTracking()
        .Where(x => x.CustomerName != null && x.CustomerName != "")
        .ToListAsync();

    var rows = salesTxs
        .GroupBy(x => x.CustomerName.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(g => new
        {
            CustomerName = g.Key,
            TotalSales = g.Sum(x => x.TotalAmount),
            TotalPaid = g.Sum(x => x.PaidAmount),
            TotalDue = g.Sum(x => x.DueAmount),
            LastSoldAtUtc = g.Max(x => x.SoldAtUtc),
            InvoiceCount = g.Select(x => x.InvoiceNo ?? x.SalesNo).Distinct().Count()
        })
        .Where(x => x.TotalDue > 0)
        .OrderByDescending(x => x.TotalDue)
        .ThenBy(x => x.CustomerName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var keyword = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    if (keyword is not null)
    {
        rows = rows
            .Where(x => string.Equals(x.CustomerName, keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    if (keyword is not null)
        await CustomerDueCreditAdjuster.ReconcilePendingReturnsAsync(db, keyword);

    var invoiceRows = new List<CustomerDueInvoiceRow>();
    var transactionRows = new List<CustomerDueTransactionRow>();
    decimal totalCashCollected = 0;
    decimal totalSettlementDiscount = 0;
    if (keyword is not null)
    {
        // Re-read after reconcile may have updated paid/due balances.
        salesTxs = await db.SalesTransactions
            .AsNoTracking()
            .Where(x => x.CustomerName != null && x.CustomerName != "")
            .ToListAsync();
        rows = salesTxs
            .GroupBy(x => x.CustomerName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                CustomerName = g.Key,
                TotalSales = g.Sum(x => x.TotalAmount),
                TotalPaid = g.Sum(x => x.PaidAmount),
                TotalDue = g.Sum(x => x.DueAmount),
                LastSoldAtUtc = g.Max(x => x.SoldAtUtc),
                InvoiceCount = g.Select(x => x.InvoiceNo ?? x.SalesNo).Distinct().Count()
            })
            .Where(x => x.TotalDue > 0)
            .Where(x => string.Equals(x.CustomerName, keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.TotalDue)
            .ThenBy(x => x.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var customerTxs = salesTxs
            .Where(x => string.Equals(x.CustomerName.Trim(), keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var itemIds = customerTxs
            .Where(x => x.InventoryItemId.HasValue)
            .Select(x => x.InventoryItemId!.Value)
            .Distinct()
            .ToList();
        var itemLookup = await db.InventoryItems
            .AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        var txIds = customerTxs.Select(x => x.Id).ToList();
        var collections = await db.SalesCollections
            .AsNoTracking()
            .Where(c => txIds.Contains(c.SalesTransactionId))
            .ToListAsync();

        var collectionsByTx = collections
            .GroupBy(c => c.SalesTransactionId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(c => CustomerDueLedgerBuilder.ClassifyCollectionNote(c.Note))
                    .ToDictionary(gg => gg.Key, gg => gg.Sum(x => x.Amount)));

        decimal SumClassified(HashSet<Guid> lineIds, string code) =>
            lineIds.Sum(id => collectionsByTx.TryGetValue(id, out var map) && map.TryGetValue(code, out var amt) ? amt : 0m);

        invoiceRows = customerTxs
            .GroupBy(x => (x.InvoiceNo ?? x.SalesNo).Trim())
            .Select(g =>
            {
                var lines = g.ToList();
                var lineIds = lines.Select(x => x.Id).ToHashSet();
                var (totalDiscount, maxAllowedDiscount) = CustomerDueLedgerDiscount.Compute(lines, itemLookup);
                var cashCollected = SumClassified(lineIds, "Payment");
                var settlementDiscount = SumClassified(lineIds, "SettlementDiscount");
                var returnCredit = SumClassified(lineIds, "ReturnCredit");
                return new CustomerDueInvoiceRow(
                    g.Key,
                    g.First().CustomerName.Trim(),
                    g.Sum(x => x.TotalAmount),
                    g.Sum(x => x.PaidAmount),
                    g.Sum(x => x.DueAmount),
                    cashCollected,
                    settlementDiscount,
                    returnCredit,
                    totalDiscount,
                    maxAllowedDiscount,
                    g.Max(x => x.SoldAtUtc),
                    g.Any(x => x.IsCredit));
            })
            .Where(x => x.TotalDue > 0)
            .OrderByDescending(x => x.SoldAtUtc)
            .ThenBy(x => x.InvoiceNo)
            .ToList();

        transactionRows = CustomerDueLedgerBuilder.BuildTransactionHistory(customerTxs, collections);
        totalCashCollected = collections
            .Where(c => CustomerDueLedgerBuilder.ClassifyCollectionNote(c.Note) == "Payment")
            .Sum(c => c.Amount);
        totalSettlementDiscount = collections
            .Where(c => CustomerDueLedgerBuilder.ClassifyCollectionNote(c.Note) == "SettlementDiscount")
            .Sum(c => c.Amount);
    }

    var totalDue = invoiceRows.Count > 0
        ? invoiceRows.Sum(x => x.TotalDue)
        : rows.Sum(x => x.TotalDue);

    decimal totalReturnCreditApplied = 0;
    if (keyword is not null)
    {
        var customerReturns = await db.SalesReturns.AsNoTracking().ToListAsync();
        totalReturnCreditApplied = customerReturns
            .Where(r => string.Equals(r.CustomerName.Trim(), keyword, StringComparison.OrdinalIgnoreCase)
                        && CustomerDueCreditAdjuster.ReducesCustomerDue(r.ActionType))
            .Sum(r => r.DueCreditApplied);
    }

    return Results.Ok(new
    {
        TotalDue = totalDue,
        TotalCashCollected = totalCashCollected,
        TotalSettlementDiscount = totalSettlementDiscount,
        TotalReturnCreditApplied = totalReturnCreditApplied,
        Rows = rows,
        InvoiceRows = invoiceRows,
        TransactionRows = transactionRows
    });
});

app.MapPost("/api/finance/customer-due-collection", async (CustomerDueCollectionRequest req, BongoTexDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.CustomerName))
        return Results.BadRequest("Customer name is required.");
    if (req.AmountReceived < 0 || req.SettlementDiscount < 0)
        return Results.BadRequest("Amounts cannot be negative.");
    if (req.AmountReceived <= 0 && req.SettlementDiscount <= 0)
        return Results.BadRequest("Enter cash received and/or settlement discount.");

    try
    {
        var result = await CustomerDueCreditAdjuster.ApplyDueCollectionAsync(
            db,
            req.CustomerName.Trim(),
            req.AmountReceived,
            req.SettlementDiscount,
            req.Note,
            req.CollectedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow);
        await db.SaveChangesAsync();
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/supplier-purchases", async (HttpContext http, BongoTexDbContext db) =>
{
    CreateSupplierPurchaseRequest req;
    try
    {
        req = await http.Request.ReadFromJsonAsync<CreateSupplierPurchaseRequest>(saleBodyJsonOptions)
            ?? throw new JsonException("Request body is empty.");
    }
    catch (JsonException ex)
    {
        return apiErr($"Invalid purchase data: {ex.Message}");
    }

    if (req.SupplierId == Guid.Empty)
        return apiErr("Select a supplier.");

    try
    {
        if (req.RawMaterialId is { } rmCheck && rmCheck != Guid.Empty
            || (req.RawMaterialLines?.Count ?? 0) > 0)
            await SupplierPurchaseOps.EnsureRawMaterialSchemaAsync(db);

        var result = await SupplierPurchaseOps.CreateAsync(db, req);
        if (result.Error is not null)
            return apiErr(result.Error);

        var p = result.Purchase!;
        return Results.Json(new
        {
            id = p.Id,
            purchaseNo = p.PurchaseNo,
            totalAmount = p.TotalAmount,
            paidAmount = p.PaidAmount,
            dueAmount = p.DueAmount
        }, statusCode: StatusCodes.Status201Created);
    }
    catch (DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (message.Contains("RawMaterial", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))
        {
            return apiErr(
                "Raw material database tables are missing. Stop the API, run \"dotnet run\" from the Api project folder, then try again.");
        }

        return apiErr($"Could not save supplier purchase: {message}");
    }
    catch (Exception ex)
    {
        return apiErr($"Supplier purchase failed: {ex.Message}");
    }
});

app.MapGet("/api/supplier-purchases", async (string? month, Guid? supplierId, BongoTexDbContext db) =>
{
    var rows = await SupplierPurchaseOps.ListAsync(db, month, supplierId);
    return Results.Ok(rows);
});

app.MapPost("/api/supplier-purchases/{id:guid}/pay", async (Guid id, PaySupplierPurchaseRequest req, BongoTexDbContext db) =>
{
    var result = await SupplierPurchaseOps.PayAsync(db, id, req);
    return result.Error is not null
        ? Results.BadRequest(new { error = result.Error })
        : Results.Ok(result.Purchase);
});

app.MapGet("/api/finance/supplier-ledger", async (string? month, Guid? supplierId, string? partyName, BongoTexDbContext db) =>
{
    Supplier? supplier = null;
    if (supplierId is { } sid && sid != Guid.Empty)
    {
        supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
        if (supplier is null)
            return Results.BadRequest(new { error = "Supplier not found." });
    }

    var partyFilter = (partyName ?? string.Empty).Trim();
    if (supplier is not null)
        partyFilter = supplier.Name.Trim();

    var report = await SupplierLedgerOps.BuildAsync(db, month, supplier, partyFilter);
    return Results.Ok(report);
});

app.MapGet("/api/finance/salary-register", async (string? month, string? expenseScope, BongoTexDbContext db) =>
{
    var query = db.ExpenseEntries.Where(x => x.Category == "Salary");
    if (!string.IsNullOrWhiteSpace(expenseScope))
    {
        var scope = expenseScope.Trim();
        query = query.Where(x => x.ExpenseScope == scope);
    }

    var entries = await query.ToListAsync();
    if (!string.IsNullOrWhiteSpace(month) && DateTime.TryParse($"{month}-01", out var m))
    {
        var start = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var monthKey = month.Trim();
        entries = entries
            .Where(x => PayrollSalaryOps.MatchesSalaryMonth(x, monthKey, start, end))
            .ToList();
    }

    var rows = entries
        .OrderByDescending(x => x.ExpenseDateUtc)
        .Select(x => new
        {
            x.Id,
            x.ExpenseNo,
            x.PartyName,
            SalaryPaymentType = string.IsNullOrWhiteSpace(x.SalaryPaymentType) ? "Current" : x.SalaryPaymentType,
            SalaryForMonth = string.IsNullOrWhiteSpace(x.SalaryForMonth) ? x.ExpenseDateUtc.ToString("yyyy-MM") : x.SalaryForMonth,
            x.Amount,
            x.Description,
            x.ExpenseDateUtc
        })
        .ToList();

    var total = rows.Sum(x => x.Amount);
    return Results.Ok(new { TotalAmount = total, Rows = rows });
});

app.MapPut("/api/finance/salary-expense/{id:guid}", async (Guid id, UpdateSalaryExpenseRequest req, BongoTexDbContext db) =>
{
    var entry = await db.ExpenseEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (entry is null) return Results.NotFound("Salary payment not found.");
    if (!string.Equals(entry.Category, "Salary", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only salary payments can be edited here.");
    if (req.Amount <= 0) return Results.BadRequest("Amount must be greater than zero.");

    var salaryPaymentType = (req.SalaryPaymentType ?? entry.SalaryPaymentType ?? "Current").Trim();
    var monthKey = PayrollSalaryOps.ResolveSalaryMonthKey(entry);
    if (monthKey is null) return Results.BadRequest("Salary month could not be determined for this payment.");

    var employee = await db.Employees.FirstOrDefaultAsync(x => x.Name == entry.PartyName);
    if (employee is null) return Results.BadRequest("Employee not found for this payment.");

    var validateErr = await PayrollSalaryOps.ValidateSalaryPaymentAmountAsync(
        db, employee, monthKey, salaryPaymentType, req.Amount, excludeExpenseId: id);
    if (validateErr is not null) return Results.BadRequest(new { error = validateErr });

    var oldAmount = entry.Amount;
    if (req.Amount > oldAmount)
    {
        var cashErr = await DailyCashBalanceReport.ValidateExpenseCashAsync(
            db, entry.ExpenseScope, entry.Category, entry.SiteId, req.Amount - oldAmount, entry.ExpenseDateUtc);
        if (cashErr is not null) return Results.BadRequest(new { error = cashErr });
    }

    var cashReturned = oldAmount > req.Amount ? oldAmount - req.Amount : 0m;
    var cashReturnSnapshot = entry;

    entry.Amount = req.Amount;
    entry.SalaryPaymentType = salaryPaymentType;
    if (req.Description is not null) entry.Description = req.Description.Trim();
    if (string.IsNullOrWhiteSpace(entry.SalaryForMonth)) entry.SalaryForMonth = monthKey;
    if (cashReturned > 0)
    {
        var note = $"[Salary edit return {cashReturned:0.00}]";
        entry.Description = string.IsNullOrWhiteSpace(entry.Description)
            ? note
            : $"{entry.Description} {note}".Trim();
        if (entry.Description.Length > 250) entry.Description = entry.Description[..250];
    }

    var line = await PayrollSalaryOps.SyncPayrollLineFromSalaryExpensesAsync(
        db, entry.PartyName, monthKey, entry.ExpenseScope, entry.SiteId);
    var cashReturn = await PayrollSalaryCashOps.BuildCashReturnPayloadAsync(db, cashReturnSnapshot, cashReturned);
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        entry.Id,
        entry.ExpenseNo,
        entry.PartyName,
        entry.Amount,
        entry.SalaryPaymentType,
        entry.SalaryForMonth,
        PayrollLineId = line?.Id,
        NetPayable = line?.NetPayable,
        Status = line?.Status,
        AdvancePaid = line?.AdvancePaid,
        CurrentPaid = line?.CurrentPaid,
        DuePaid = line?.DuePaid,
        CashReturn = cashReturn
    });
});

app.MapDelete("/api/finance/salary-expense/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var entry = await db.ExpenseEntries.FirstOrDefaultAsync(x => x.Id == id);
    if (entry is null) return Results.NotFound("Salary payment not found.");
    if (!string.Equals(entry.Category, "Salary", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only salary payments can be voided here.");

    var monthKey = PayrollSalaryOps.ResolveSalaryMonthKey(entry);
    var partyName = entry.PartyName;
    var scope = entry.ExpenseScope;
    var siteId = entry.SiteId;
    var cashReturned = entry.Amount;
    var cashReturnSnapshot = entry;

    db.ExpenseEntries.Remove(entry);

    PayrollLine? line = null;
    if (monthKey is not null)
        line = await PayrollSalaryOps.SyncPayrollLineFromSalaryExpensesAsync(db, partyName, monthKey, scope, siteId);

    var cashReturn = await PayrollSalaryCashOps.BuildCashReturnPayloadAsync(db, cashReturnSnapshot, cashReturned);
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        RemovedId = id,
        PartyName = partyName,
        PayrollLineId = line?.Id,
        NetPayable = line?.NetPayable,
        Status = line?.Status,
        AdvancePaid = line?.AdvancePaid,
        CurrentPaid = line?.CurrentPaid,
        DuePaid = line?.DuePaid,
        CashReturn = cashReturn
    });
});

app.MapPost("/api/finance/payroll/generate", async (GeneratePayrollRequest req, BongoTexDbContext db) =>
{
    var monthKey = (req.Month ?? string.Empty).Trim();
    if (!Regex.IsMatch(monthKey, @"^\d{4}-(0[1-9]|1[0-2])$"))
        return Results.BadRequest("Payroll month is required in YYYY-MM format.");
    var scope = (req.ExpenseScope ?? "Factory").Trim();
    if (!PayrollScopeOps.IsValid(scope))
        return Results.BadRequest("Expense scope must be Factory, PrintFactory, or SalesCenter.");
    if (scope == "SalesCenter" && (!req.SiteId.HasValue || req.SiteId.Value == Guid.Empty))
        return Results.BadRequest("Site is required for sales center payroll.");

    Guid? siteId = null;
    if (scope == "SalesCenter")
    {
        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId!.Value && x.Type == "SalesCenter");
        if (site is null) return Results.BadRequest("Invalid sales center.");
        siteId = site.Id;
    }

    var employeesQuery = db.Employees.AsQueryable().Where(e => e.IsActive);
    if (string.Equals(scope, PrintFactoryConventions.ExpenseScope, StringComparison.OrdinalIgnoreCase))
        employeesQuery = employeesQuery.Where(e => e.EmployeeType == "PrintFactory");
    else if (scope == "Factory")
    {
        employeesQuery = employeesQuery.Where(e =>
            e.EmployeeType != "PrintFactory"
            && (e.EmployeeType == "Factory"
            // Legacy rows may have role in EmployeeType.
            || e.EmployeeType == "Staff"
            || e.EmployeeType == "Operator"
            || e.EmployeeType == "Helper"
            || e.EmployeeType == "Print"
            || e.EmployeeType == "Security"
            // Legacy/partial rows: infer factory if no site assigned.
            || e.SiteId == null));
    }
    else
        employeesQuery = employeesQuery.Where(e => e.SiteId == siteId);

    var employees = PayrollFormulas.OrderByEmployeeSerial(
            await employeesQuery.ToListAsync(),
            e => e.SerialNumber,
            e => e.Name)
        .ToList();

    var run = await db.PayrollRuns
        .Where(x => x.MonthKey == monthKey && x.ExpenseScope == scope && x.SiteId == siteId)
        .OrderByDescending(x => x.CreatedAtUtc)
        .FirstOrDefaultAsync();
    if (run is null)
    {
        run = new PayrollRun
        {
            MonthKey = monthKey,
            ExpenseScope = scope,
            SiteId = siteId,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
    }

    var monthStartUtc = DateTime.ParseExact(
        monthKey + "-01",
        "yyyy-MM-dd",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    var monthEndUtc = monthStartUtc.AddMonths(1);

    var oldLines = await db.PayrollLines.Where(x => x.PayrollRunId == run.Id).ToListAsync();
    var prevOtByEmp = oldLines.ToDictionary(x => x.EmployeeId, x => x.OvertimeHours);
    var prevSnakesByEmp = oldLines.ToDictionary(x => x.EmployeeId, x => x.SnakesPay);
    var attSumByEmp = PayrollScopeOps.IsFactoryStyle(scope)
        ? await FactoryAttendanceOps.GetAttendanceTotalsByEmployeeAsync(db, monthKey)
        : new Dictionary<Guid, decimal>();
    var employeesWithSheet = attSumByEmp.Keys.ToHashSet();

    if (oldLines.Count > 0) db.PayrollLines.RemoveRange(oldLines);

    var salaryByParty = await PayrollSalaryOps.LoadPaidByPartyNameAsync(
        db, scope, monthKey, monthStartUtc, monthEndUtc);

    var lines = new List<PayrollLine>();
    foreach (var e in employees)
    {
        if (!salaryByParty.TryGetValue(e.Name.Trim(), out var paid))
            paid = (Advance: 0m, Current: 0m, Due: 0m);

        var otH = PayrollScopeOps.IsFactoryStyle(scope) && prevOtByEmp.TryGetValue(e.Id, out var prevH) ? prevH : 0;
        var attDays = PayrollScopeOps.IsFactoryStyle(scope)
            ? (employeesWithSheet.Contains(e.Id)
                ? attSumByEmp.GetValueOrDefault(e.Id, 0m)
                : 0m)
            : 0;
        var snakes = PayrollScopeOps.IsFactoryStyle(scope) && prevSnakesByEmp.ContainsKey(e.Id) ? prevSnakesByEmp[e.Id] : 0;
        var line = new PayrollLine
        {
            PayrollRunId = run.Id,
            EmployeeId = e.Id,
            EmployeeName = e.Name,
            EmployeeCategory = PayrollFormulas.ResolvePayrollEmployeeCategory(e),
            MonthlySalary = e.MonthlySalary,
            AdvancePaid = paid.Advance,
            CurrentPaid = paid.Current,
            DuePaid = paid.Due,
            OvertimeHours = otH,
            AttendanceDays = attDays,
            SnakesPay = snakes
        };
        PayrollFormulas.RecalculateLine(line, monthKey, scope);
        lines.Add(line);
    }

    if (lines.Count > 0) db.PayrollLines.AddRange(lines);
    await db.SaveChangesAsync();
    return Results.Ok(new { run.Id, run.MonthKey, run.ExpenseScope, run.SiteId, GeneratedLines = lines.Count });
});

app.MapGet("/api/finance/payroll-run", async (string month, string? expenseScope, Guid? siteId, BongoTexDbContext db) =>
{
    var monthKey = (month ?? string.Empty).Trim();
    if (!Regex.IsMatch(monthKey, @"^\d{4}-(0[1-9]|1[0-2])$"))
        return Results.BadRequest("Payroll month is required in YYYY-MM format.");
    var scope = (expenseScope ?? "Factory").Trim();
    if (!PayrollScopeOps.IsValid(scope))
        return Results.BadRequest("Expense scope must be Factory, PrintFactory, or SalesCenter.");
    if (scope == "SalesCenter" && (!siteId.HasValue || siteId.Value == Guid.Empty))
        return Results.BadRequest("Site is required for sales center payroll.");

    var run = await db.PayrollRuns
        .Where(x => x.MonthKey == monthKey && x.ExpenseScope == scope && x.SiteId == (scope == "SalesCenter" ? siteId : null))
        .OrderByDescending(x => x.CreatedAtUtc)
        .FirstOrDefaultAsync();
    if (run is null)
        return Results.Ok(new { MonthKey = monthKey, ExpenseScope = scope, SiteId = siteId, Rows = Array.Empty<object>(), TotalSalary = 0m, TotalBaseSalary = 0m, TotalAttendanceSalary = 0m, TotalOvertime = 0m, TotalAttendanceBonus = 0m, TotalSnakesPay = 0m, TotalGross = 0m, TotalNetPayable = 0m });

    var rows = await db.PayrollLines
        .Where(x => x.PayrollRunId == run.Id)
        .ToListAsync();

    var empIds = rows.Select(x => x.EmployeeId).Distinct().ToList();
    var empById = empIds.Count == 0
        ? new Dictionary<Guid, Employee>()
        : await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);

    if (PayrollFormulas.TryGetMonthStart(monthKey, out var monthStartUtc))
    {
        var monthEndUtc = monthStartUtc.AddMonths(1);
        var salaryByParty = await PayrollSalaryOps.LoadPaidByPartyNameAsync(
            db, scope, monthKey, monthStartUtc, monthEndUtc);
        var paidDirty = false;
        foreach (var line in rows)
        {
            var nameKey = empById.TryGetValue(line.EmployeeId, out var empForPaid)
                ? empForPaid.Name.Trim()
                : line.EmployeeName.Trim();
            if (!salaryByParty.TryGetValue(nameKey, out var paid))
                paid = (0m, 0m, 0m);
            if (line.AdvancePaid != paid.Advance || line.CurrentPaid != paid.Current || line.DuePaid != paid.Due)
            {
                line.AdvancePaid = paid.Advance;
                line.CurrentPaid = paid.Current;
                line.DuePaid = paid.Due;
                paidDirty = true;
            }
        }
        if (paidDirty)
            await db.SaveChangesAsync();
    }

    foreach (var line in rows)
    {
        if (empById.TryGetValue(line.EmployeeId, out var emp))
            line.EmployeeCategory = PayrollFormulas.ResolvePayrollEmployeeCategory(emp);
        PayrollFormulas.RecalculateLine(line, run.MonthKey, run.ExpenseScope);
    }

    var dtoRows = PayrollFormulas.OrderByEmployeeSerial(
            rows,
            x => empById.TryGetValue(x.EmployeeId, out var emp) ? emp.SerialNumber : 0,
            x => x.EmployeeName)
        .Select(x => new
    {
        x.Id,
        x.EmployeeId,
        SerialNumber = empById.TryGetValue(x.EmployeeId, out var empSn) ? empSn.SerialNumber : 0,
        x.EmployeeName,
        x.EmployeeCategory,
        x.MonthlySalary,
        x.AttendanceDays,
        x.AttendanceSalaryAmount,
        x.OvertimeHours,
        x.OvertimeAmount,
        x.AttendanceBonus,
        x.SnakesPay,
        x.AdvancePaid,
        x.CurrentPaid,
        x.DuePaid,
        x.NetPayable,
        x.Status,
        x.UpdatedAtUtc
    }).ToList();

    return Results.Ok(new
    {
        run.Id,
        run.MonthKey,
        run.ExpenseScope,
        run.SiteId,
        TotalBaseSalary = dtoRows.Sum(x => x.MonthlySalary),
        TotalAttendanceSalary = dtoRows.Sum(x => x.AttendanceSalaryAmount),
        TotalOvertime = dtoRows.Sum(x => x.OvertimeAmount),
        TotalAttendanceBonus = dtoRows.Sum(x => x.AttendanceBonus),
        TotalSnakesPay = dtoRows.Sum(x => x.SnakesPay),
        TotalGross = dtoRows.Sum(x => x.AttendanceSalaryAmount + x.OvertimeAmount + x.AttendanceBonus + x.SnakesPay),
        TotalSalary = dtoRows.Sum(x => x.MonthlySalary),
        TotalNetPayable = dtoRows.Sum(x => x.NetPayable),
        Rows = dtoRows
    });
});

app.MapPost("/api/finance/payroll/pay", async (PayPayrollLineRequest req, BongoTexDbContext db) =>
{
    if (req.PayrollLineId == Guid.Empty && (!req.EmployeeId.HasValue || req.EmployeeId.Value == Guid.Empty))
        return Results.BadRequest("Payroll line is required.");
    if (req.Amount <= 0) return Results.BadRequest("Pay amount must be greater than zero.");
    var salaryPaymentType = (req.SalaryPaymentType ?? "Current").Trim();
    var allowedSalaryTypes = new[] { "Advance", "Due", "Current" };
    if (!allowedSalaryTypes.Contains(salaryPaymentType))
        return Results.BadRequest("Salary payment type must be Advance, Due, or Current.");

    var line = await PayrollScopeOps.FindPayrollLineAsync(
        db, req.PayrollLineId, req.EmployeeId, req.Month, req.ExpenseScope, req.SiteId);
    if (line is null) return Results.NotFound("Payroll line not found. Generate or load payroll for this month first.");
    var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == line.PayrollRunId);
    if (run is null) return Results.BadRequest("Payroll run not found.");
    var employee = await db.Employees.FirstOrDefaultAsync(x => x.Id == line.EmployeeId);
    if (employee is null) return Results.BadRequest("Employee not found.");

    if (salaryPaymentType == "Current" || salaryPaymentType == "Advance")
    {
        PayrollFormulas.RecalculateLine(line, run.MonthKey, run.ExpenseScope);
        if (req.Amount > line.NetPayable)
            return Results.BadRequest($"Amount exceeds remaining net payable {line.NetPayable:0.##}.");
    }

    var entry = new ExpenseEntry
    {
        ExpenseNo = FinanceConventions.NewExpenseNo(),
        Category = "Salary",
        PartyName = employee.Name,
        ExpenseScope = run.ExpenseScope,
        SiteId = run.ExpenseScope == "SalesCenter" ? run.SiteId : null,
        Amount = req.Amount,
        Description = (req.Description ?? string.Empty).Trim(),
        SalaryPaymentType = salaryPaymentType,
        SalaryForMonth = run.MonthKey,
        ExpenseDateUtc = DateTime.UtcNow
    };
    ManagerCashbook.AssignCashbookForNewEntry(entry, "Salary", employee.Name, run.ExpenseScope, "");
    var cashErr = await DailyCashBalanceReport.ValidateExpenseCashAsync(
        db, entry.ExpenseScope, entry.Category, entry.SiteId, entry.Amount, entry.ExpenseDateUtc);
    if (cashErr is not null)
        return Results.BadRequest(new { error = cashErr });
    db.ExpenseEntries.Add(entry);
    await db.SaveChangesAsync();

    line = await PayrollSalaryOps.SyncPayrollLineFromSalaryExpensesAsync(
               db, employee.Name, run.MonthKey, run.ExpenseScope, run.SiteId)
           ?? line;

    await db.SaveChangesAsync();
    return Results.Ok(new { line.Id, line.EmployeeName, line.NetPayable, line.Status, Paid = req.Amount, PaymentType = salaryPaymentType });
});

app.MapPut("/api/finance/payroll-line/{id:guid}/overtime", async (Guid id, UpdatePayrollOvertimeRequest req, BongoTexDbContext db) =>
{
    var line = await db.PayrollLines.FirstOrDefaultAsync(x => x.Id == id);
    if (line is null) return Results.NotFound("Payroll line not found.");
    var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == line.PayrollRunId);
    if (run is null) return Results.BadRequest("Payroll run not found.");
    if (!PayrollScopeOps.IsFactoryStyle(run.ExpenseScope))
        return Results.BadRequest("Overtime applies only to factory or print factory payroll.");
    if (req.OvertimeHours < 0)
        return Results.BadRequest("Overtime hours cannot be negative.");
    line.OvertimeHours = req.OvertimeHours;
    PayrollFormulas.RecalculateLine(line, run.MonthKey, run.ExpenseScope);
    await db.SaveChangesAsync();
    return Results.Ok(new { line.Id, line.OvertimeHours, line.OvertimeAmount, line.AttendanceSalaryAmount, line.NetPayable, line.Status });
});

app.MapGet("/api/payroll/factory-attendance-sheet", async (string month, string? expenseScope, BongoTexDbContext db) =>
{
    var monthKey = (month ?? string.Empty).Trim();
    if (!Regex.IsMatch(monthKey, @"^\d{4}-(0[1-9]|1[0-2])$"))
        return Results.BadRequest("Month is required in YYYY-MM format.");
    var scope = (expenseScope ?? "Factory").Trim();
    if (!PayrollScopeOps.IsFactoryStyle(scope))
        return Results.BadRequest("Attendance sheet applies to Factory or PrintFactory payroll.");
    var sheet = await FactoryAttendanceOps.GetSheetAsync(db, monthKey, scope);
    return Results.Ok(sheet);
});

app.MapPut("/api/payroll/factory-attendance-day", async (SetFactoryAttendanceDayRequest req, BongoTexDbContext db) =>
{
    var result = await FactoryAttendanceOps.SetDayAsync(db, req);
    return result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Payload);
});

app.MapPut("/api/finance/payroll-line/{id:guid}/attendance", async (Guid id, UpdatePayrollAttendanceRequest req, BongoTexDbContext db) =>
{
    var line = await db.PayrollLines.FirstOrDefaultAsync(x => x.Id == id);
    if (line is null) return Results.NotFound("Payroll line not found.");
    var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == line.PayrollRunId);
    if (run is null) return Results.BadRequest("Payroll run not found.");
    if (!PayrollScopeOps.IsFactoryStyle(run.ExpenseScope))
        return Results.BadRequest("Attendance applies only to factory or print factory payroll.");
    if (req.AttendanceDays < 0)
        return Results.BadRequest("Attendance days cannot be negative.");
    line.AttendanceDays = req.AttendanceDays;
    PayrollFormulas.RecalculateLine(line, run.MonthKey, run.ExpenseScope);
    await db.SaveChangesAsync();
    return Results.Ok(new { line.Id, line.AttendanceDays, line.AttendanceSalaryAmount, line.AttendanceBonus, line.NetPayable, line.Status });
});

app.MapPut("/api/finance/payroll-line/{id:guid}/snakes-pay", async (Guid id, UpdatePayrollSnakesPayRequest req, BongoTexDbContext db) =>
{
    var line = await db.PayrollLines.FirstOrDefaultAsync(x => x.Id == id);
    if (line is null) return Results.NotFound("Payroll line not found.");
    var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == line.PayrollRunId);
    if (run is null) return Results.BadRequest("Payroll run not found.");
    if (!PayrollScopeOps.IsFactoryStyle(run.ExpenseScope))
        return Results.BadRequest("Snakes pay applies only to factory or print factory payroll.");
    if (req.SnakesPay < 0)
        return Results.BadRequest("Snakes pay cannot be negative.");
    line.SnakesPay = req.SnakesPay;
    PayrollFormulas.RecalculateLine(line, run.MonthKey, run.ExpenseScope);
    await db.SaveChangesAsync();
    return Results.Ok(new { line.Id, line.SnakesPay, line.NetPayable, line.Status });
});

app.MapPut("/api/stock-transfers/{id:guid}", async (Guid id, UpdateStockTransferRequest req, BongoTexDbContext db) =>
{
    var transfer = await db.StockTransfers.FirstOrDefaultAsync(x => x.Id == id);
    if (transfer is null)
    {
        return Results.NotFound("Transfer not found.");
    }

    if (req.Quantity <= 0)
    {
        return Results.BadRequest("Transfer quantity must be greater than zero.");
    }

    var sourceStock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == transfer.InventoryItemId && x.SiteId == transfer.FromSiteId);
    var destinationStock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == transfer.InventoryItemId && x.SiteId == transfer.ToSiteId);

    if (sourceStock is null || destinationStock is null)
    {
        return Results.BadRequest("Stock records not found for this transfer.");
    }

    var delta = req.Quantity - transfer.Quantity;
    if (delta > 0 && sourceStock.Quantity < delta)
    {
        return Results.BadRequest("Insufficient stock at source site for edit.");
    }
    if (delta < 0 && destinationStock.Quantity < -delta)
    {
        return Results.BadRequest("Insufficient stock at destination site to reduce transfer.");
    }

    sourceStock.Quantity -= delta;
    destinationStock.Quantity += delta;
    transfer.Quantity = req.Quantity;
    transfer.TransferredAtUtc = req.TransferredAtUtc?.ToUniversalTime() ?? transfer.TransferredAtUtc;

    await db.SaveChangesAsync();
    return Results.Ok(transfer);
});

app.MapDelete("/api/stock-transfers/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var transfer = await db.StockTransfers.FirstOrDefaultAsync(x => x.Id == id);
    if (transfer is null)
    {
        return Results.NotFound("Transfer not found.");
    }

    var sourceStock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == transfer.InventoryItemId && x.SiteId == transfer.FromSiteId);
    var destinationStock = await db.InventoryStocks
        .FirstOrDefaultAsync(x => x.InventoryItemId == transfer.InventoryItemId && x.SiteId == transfer.ToSiteId);

    if (sourceStock is null || destinationStock is null || destinationStock.Quantity < transfer.Quantity)
    {
        return Results.BadRequest("Cannot delete transfer. Insufficient destination stock to reverse.");
    }

    sourceStock.Quantity += transfer.Quantity;
    destinationStock.Quantity -= transfer.Quantity;
    db.StockTransfers.Remove(transfer);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/finance/sales-center-report", async (string? from, string? to, BongoTexDbContext db, CancellationToken cancellationToken) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusive, out var rangeEndExclusive, out var rangeError))
        return Results.BadRequest(rangeError);

    var report = await SalesCenterPeriodReport.BuildAsync(db, fromUtc, toInclusive, rangeEndExclusive, cancellationToken);
    return Results.Ok(report);
});

app.MapGet("/api/finance/daily-pl/factory", async (string? from, string? to, Guid? siteId, BongoTexDbContext db, CancellationToken ct) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusive, out var rangeEndExclusive, out var rangeError))
        return Results.BadRequest(rangeError);
    var report = await DailyProfitLossReport.BuildFactoryAsync(db, fromUtc, toInclusive, rangeEndExclusive, siteId, ct);
    return Results.Ok(report);
});

app.MapGet("/api/finance/daily-pl/sales-center", async (string? from, string? to, Guid? siteId, BongoTexDbContext db, CancellationToken ct) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusive, out var rangeEndExclusive, out var rangeError))
        return Results.BadRequest(rangeError);
    var report = await DailyProfitLossReport.BuildSalesCenterAsync(db, fromUtc, toInclusive, rangeEndExclusive, siteId, ct);
    return Results.Ok(report);
});

app.MapPost("/api/finance/daily-cash-balances", DailyCashBalanceReport.HandleAsync);

app.MapGet("/api/raw-materials/purchase-rates", async (Guid? siteId, BongoTexDbContext db) =>
{
    var q = db.RawMaterialMovements.AsNoTracking()
        .Where(m => m.MovementType == RawMaterialOps.TypePurchase && m.UnitCost > 0);
    if (siteId is { } sid && sid != Guid.Empty)
        q = q.Where(m => m.SiteId == sid);

    var purchases = await q
        .OrderByDescending(m => m.MovementDateUtc)
        .ThenByDescending(m => m.MovementNo)
        .Select(m => new { m.RawMaterialId, m.UnitCost, m.MovementDateUtc, m.SiteId })
        .ToListAsync();

    var rates = purchases
        .GroupBy(x => x.RawMaterialId)
        .Select(g =>
        {
            var last = g.First();
            return new
            {
                last.RawMaterialId,
                UnitCost = last.UnitCost,
                LastPurchaseDateUtc = last.MovementDateUtc,
                SiteId = last.SiteId
            };
        })
        .OrderBy(x => x.RawMaterialId)
        .ToList();

    return Results.Ok(new { Rates = rates });
});

app.MapGet("/api/raw-materials/next-code", async (string? category, BongoTexDbContext db) =>
{
    var next = await RawMaterialRules.GenerateNextCodeAsync(db, category);
    return Results.Ok(new { Code = next, Category = RawMaterialRules.NormalizeCategory(category) });
});

app.MapGet("/api/raw-materials/build-code", (string? category, string? memoNo) =>
{
    var cat = RawMaterialRules.NormalizeCategory(category);
    var code = RawMaterialRules.BuildCodeFromCategoryAndMemo(cat, memoNo, out var error);
    if (error is not null)
        return Results.BadRequest(new { error });
    return Results.Ok(new { Code = code, Category = cat });
});

app.MapGet("/api/raw-materials", async (bool? activeOnly, BongoTexDbContext db) =>
{
    var q = db.RawMaterials.AsNoTracking();
    if (activeOnly == true)
        q = q.Where(x => x.IsActive);
    var rows = await q.OrderBy(x => x.Category).ThenBy(x => x.Code).ToListAsync();
    return Results.Ok(rows);
});

app.MapPost("/api/raw-materials", async (CreateRawMaterialRequest req, BongoTexDbContext db) =>
{
    var name = (req.Name ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(name))
        return Results.BadRequest("Name is required.");

    string code;
    if (!string.IsNullOrWhiteSpace(req.MemoNo))
    {
        code = RawMaterialRules.BuildCodeFromCategoryAndMemo(req.Category, req.MemoNo, out var memoError);
        if (memoError is not null)
            return Results.BadRequest(memoError);
    }
    else
    {
        code = (req.Code ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(code))
            return Results.BadRequest("Select item type and enter memo number.");
    }

    if (await db.RawMaterials.AnyAsync(x => x.Code.ToLower() == code.ToLower()))
        return Results.BadRequest($"Material code {code} already exists for this item type and memo.");
    var cat = RawMaterialRules.NormalizeCategory(req.Category);
    var unit = RawMaterialRules.NormalizeUnit(req.Unit);
    var row = new RawMaterial
    {
        Code = code,
        Name = name,
        Category = cat,
        Unit = unit,
        IsActive = req.IsActive ?? true,
        CreatedAtUtc = DateTime.UtcNow
    };
    db.RawMaterials.Add(row);
    await db.SaveChangesAsync();
    return Results.Created($"/api/raw-materials/{row.Id}", row);
});

app.MapPut("/api/raw-materials/{id:guid}", async (Guid id, UpdateRawMaterialRequest req, BongoTexDbContext db) =>
{
    var row = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == id);
    if (row is null) return Results.NotFound("Raw material not found.");
    var name = (req.Name ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest("Name is required.");
    row.Name = name;
    row.Category = RawMaterialRules.NormalizeCategory(req.Category ?? row.Category);
    row.Unit = RawMaterialRules.NormalizeUnit(req.Unit ?? row.Unit);
    if (req.IsActive.HasValue) row.IsActive = req.IsActive.Value;
    await db.SaveChangesAsync();
    return Results.Ok(row);
});

app.MapGet("/api/raw-materials/stock", async (Guid? siteId, Guid? materialId, BongoTexDbContext db) =>
{
    var report = await RawMaterialOps.GetStockReportAsync(db, siteId, materialId);
    return Results.Ok(report);
});

app.MapGet("/api/raw-materials/reconciliation", async (Guid? siteId, BongoTexDbContext db) =>
{
    try
    {
        await RawMaterialScrapSchema.EnsureAsync(db);
        var report = await RawMaterialOps.GetReconciliationReportAsync(db, siteId);
        return Results.Ok(report);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Could not load raw material stock.", detail = ex.InnerException?.Message ?? ex.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/raw-materials/movements", async (string? from, string? to, Guid? siteId, Guid? materialId, BongoTexDbContext db) =>
{
    DateTime? fromUtc = null;
    DateTime? toExclusive = null;
    if (!string.IsNullOrWhiteSpace(from) && DateTime.TryParse(from, out var f))
        fromUtc = new DateTime(f.Year, f.Month, f.Day, 0, 0, 0, DateTimeKind.Utc);
    if (!string.IsNullOrWhiteSpace(to) && DateTime.TryParse(to, out var t))
        toExclusive = new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
    var rows = await RawMaterialOps.ListMovementsAsync(db, fromUtc, toExclusive, siteId, materialId);
    return Results.Ok(rows);
});

app.MapPost("/api/raw-materials/issue", async (RawMaterialIssueRequest req, BongoTexDbContext db) =>
{
    var err = await RawMaterialOps.IssueManualAsync(db, req);
    if (err is not null) return Results.BadRequest(new { error = err });
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/raw-materials/adjust", async (RawMaterialAdjustRequest req, BongoTexDbContext db) =>
{
    var err = await RawMaterialOps.AdjustAsync(db, req);
    if (err is not null) return Results.BadRequest(new { error = err });
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/raw-materials/split-preview", async (Guid materialId, Guid siteId, string? invoiceRef, BongoTexDbContext db) =>
{
    if (materialId == Guid.Empty || siteId == Guid.Empty)
        return Results.BadRequest(new { error = "Material and factory are required." });
    var preview = await RawMaterialOps.GetSplitPreviewAsync(db, materialId, siteId, invoiceRef);
    return preview.Error is not null
        ? Results.BadRequest(new { error = preview.Error })
        : Results.Ok(preview.Body);
});

app.MapPost("/api/raw-materials/split-merged", async (SplitMergedRawMaterialRequest req, BongoTexDbContext db) =>
{
    var err = await RawMaterialOps.SplitMergedStockAsync(db, req);
    if (err is not null) return Results.BadRequest(new { error = err });
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/raw-materials/fix-hyphen-codes", async (FixHyphenRawMaterialCodesRequest req, BongoTexDbContext db) =>
{
    var (renamed, err) = await RawMaterialOps.FixHyphenCodesAsync(db, req.InvoiceRef);
    if (err is not null) return Results.BadRequest(new { error = err });
    await db.SaveChangesAsync();
    return Results.Ok(new { renamed });
});

app.MapGet("/api/raw-materials/scrap-sales/capabilities", () =>
    Results.Ok(new
    {
        supportsPost = true,
        version = "scrap-v2",
        scrapTypes = new[] { "ScrapStock", "CuttingWastage", "RejectGarment" }
    }));

app.MapPost("/api/raw-materials/scrap-sales", async (CreateRawMaterialScrapSaleRequest req, BongoTexDbContext db) =>
{
    try
    {
        await RawMaterialScrapSchema.EnsureAsync(db);
        var (sale, error) = await RawMaterialOps.CreateScrapSaleAsync(db, req);
        return error is not null
            ? Results.BadRequest(new { error })
            : Results.Created($"/api/raw-materials/scrap-sales/{sale!.Id}", sale);
    }
    catch (DbUpdateException ex)
    {
        var detail = ex.InnerException?.Message ?? ex.Message;
        return Results.Json(
            new { error = "Could not save scrap sale. Restart the API after rebuild.", detail },
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message, detail = ex.InnerException?.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/raw-materials/scrap-sales/{id:guid}/collect", async (
    Guid id,
    CollectRawMaterialScrapSaleRequest req,
    BongoTexDbContext db) =>
{
    await RawMaterialScrapSchema.EnsureAsync(db);
    var err = await RawMaterialOps.CollectScrapSaleDueAsync(db, id, req.Amount);
    if (err is not null)
        return Results.BadRequest(new { error = err });
    var sale = await db.RawMaterialScrapSales.AsNoTracking().FirstAsync(x => x.Id == id);
    return Results.Ok(sale);
});

app.MapGet("/api/raw-materials/scrap-sales", async (
    string? from,
    string? to,
    Guid? siteId,
    string? scrapType,
    BongoTexDbContext db) =>
{
    DateTime? fromUtc = null;
    DateTime? toExclusive = null;
    if (!string.IsNullOrWhiteSpace(from) && DateTime.TryParse(from, out var f))
        fromUtc = new DateTime(f.Year, f.Month, f.Day, 0, 0, 0, DateTimeKind.Utc);
    if (!string.IsNullOrWhiteSpace(to) && DateTime.TryParse(to, out var t))
        toExclusive = new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);

    var q = from s in db.RawMaterialScrapSales.AsNoTracking()
            join site in db.Sites.AsNoTracking() on s.SiteId equals site.Id
            join m in db.RawMaterials.AsNoTracking() on s.RawMaterialId equals m.Id into mj
            from m in mj.DefaultIfEmpty()
            join i in db.InventoryItems.AsNoTracking() on s.InventoryItemId equals i.Id into ij
            from i in ij.DefaultIfEmpty()
            select new { s, site, m, i };

    if (siteId is { } sid && sid != Guid.Empty)
        q = q.Where(x => x.s.SiteId == sid);
    if (fromUtc is { } start)
        q = q.Where(x => x.s.SoldAtUtc >= start);
    if (toExclusive is { } end)
        q = q.Where(x => x.s.SoldAtUtc < end);
    if (!string.IsNullOrWhiteSpace(scrapType))
    {
        var norm = RawMaterialOps.NormalizeScrapType(scrapType);
        if (norm == RawMaterialOps.ScrapTypeScrapStock)
            q = q.Where(x => x.s.ScrapType == norm || x.s.ScrapType == "Wastage" || x.s.ScrapType == "Reject");
        else
            q = q.Where(x => x.s.ScrapType == norm);
    }

    var rows = await q
        .OrderByDescending(x => x.s.SoldAtUtc)
        .Select(x => new
        {
            x.s.Id,
            x.s.SaleNo,
            x.s.SiteId,
            SiteCode = x.site.Code,
            SiteName = x.site.Name,
            x.s.RawMaterialId,
            MaterialCode = x.m != null ? x.m.Code : null,
            MaterialName = x.m != null ? x.m.Name : null,
            MaterialCategory = x.m != null ? x.m.Category : null,
            x.s.InventoryItemId,
            ItemSku = x.i != null ? x.i.Sku : null,
            ItemName = x.i != null ? x.i.Name : null,
            x.s.ScrapType,
            DeductedStock = RawMaterialOps.ScrapTypeDeductsStock(x.s.ScrapType),
            x.s.Quantity,
            x.s.Unit,
            x.s.UnitRate,
            x.s.TotalAmount,
            x.s.BuyerName,
            x.s.IsCredit,
            x.s.PaidAmount,
            x.s.DueAmount,
            x.s.Note,
            x.s.SoldAtUtc,
            x.s.CreatedAtUtc
        })
        .Take(500)
        .ToListAsync();

    return Results.Ok(rows);
});

app.MapGet("/api/print-factory/meta", () => Results.Ok(new
{
    ExpenseScope = PrintFactoryConventions.ExpenseScope,
    InternalBuyerName = PrintFactoryConventions.InternalBuyerName,
    BuyerTypes = new[] { PrintFactoryConventions.BuyerTypeInternal, PrintFactoryConventions.BuyerTypeExternal }
}));

app.MapGet("/api/print-factory/summary", async (string? from, string? to, BongoTexDbContext db) =>
{
    if (!ManagerCashbook.TryResolveFinanceRange(from, to, out var fromUtc, out _, out var toExclusive, out var rangeError))
        return Results.BadRequest(new { error = rangeError });
    await PrintFactorySchema.EnsureAsync(db);
    var summary = await PrintFactoryOps.GetSummaryAsync(db, fromUtc, toExclusive);
    return Results.Ok(summary);
});

app.MapGet("/api/print-factory/stocktake", async (string? month, BongoTexDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(month))
        return Results.BadRequest(new { error = "Month (yyyy-MM) is required." });
    var mk = month.Trim();
    if (mk.Length != 7 || !DateTime.TryParse($"{mk}-01", out _))
        return Results.BadRequest(new { error = "Month must be yyyy-MM." });
    await PrintFactorySchema.EnsureAsync(db);
    return Results.Ok(await PrintFactoryOps.GetStocktakeAsync(db, mk));
});

app.MapPost("/api/print-factory/stocktake", async (SavePrintFactoryStocktakeRequest req, BongoTexDbContext db) =>
{
    await PrintFactorySchema.EnsureAsync(db);
    var (payload, error) = await PrintFactoryOps.SaveStocktakeAsync(db, req);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(payload);
});

app.MapGet("/api/print-factory/purchases", async (string? month, Guid? supplierId, BongoTexDbContext db) =>
{
    try
    {
        await PrintFactorySchema.EnsureAsync(db);
        return Results.Ok(await PrintFactoryOps.ListPurchasesAsync(db, month, supplierId));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Could not load purchase vouchers.", detail = ex.InnerException?.Message ?? ex.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/print-factory/purchases/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var doc = await PrintFactoryOps.GetPurchaseAsync(db, id);
    return doc is null ? Results.NotFound("Purchase voucher not found.") : Results.Ok(doc);
});

app.MapPost("/api/print-factory/purchases", async (CreatePrintFactoryPurchaseRequest req, BongoTexDbContext db) =>
{
    try
    {
        await PrintFactorySchema.EnsureAsync(db);
        var result = await PrintFactoryOps.CreatePurchaseAsync(db, req);
        return result.Error is not null
            ? Results.BadRequest(new { error = result.Error })
            : Results.Created($"/api/print-factory/purchases/{result.Purchase!.Id}", result.Purchase);
    }
    catch (DbUpdateException ex)
    {
        var detail = ex.InnerException?.Message ?? ex.Message;
        return Results.Json(
            new { error = "Could not save purchase voucher. Restart the API after rebuild.", detail },
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message, detail = ex.InnerException?.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/print-factory/purchases/{id:guid}/pay", async (Guid id, PrintFactoryPayRequest req, BongoTexDbContext db) =>
{
    var result = await PrintFactoryOps.PayPurchaseAsync(db, id, req);
    return result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Payload);
});

app.MapGet("/api/print-factory/sales", async (string? month, string? buyerType, BongoTexDbContext db) =>
{
    try
    {
        await PrintFactorySchema.EnsureAsync(db);
        return Results.Ok(await PrintFactoryOps.ListSalesAsync(db, month, buyerType));
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "Could not load sales vouchers.", detail = ex.InnerException?.Message ?? ex.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/print-factory/sales/{id:guid}", async (Guid id, BongoTexDbContext db) =>
{
    var doc = await PrintFactoryOps.GetSaleAsync(db, id);
    return doc is null ? Results.NotFound("Sales voucher not found.") : Results.Ok(doc);
});

app.MapPost("/api/print-factory/sales", async (CreatePrintFactorySaleRequest req, BongoTexDbContext db) =>
{
    var result = await PrintFactoryOps.CreateSaleAsync(db, req);
    return result.Error is not null
        ? Results.BadRequest(new { error = result.Error })
        : Results.Created($"/api/print-factory/sales/{result.Sale!.Id}", result.Sale);
});

app.MapPost("/api/print-factory/sales/{id:guid}/collect", async (Guid id, PrintFactoryCollectRequest req, BongoTexDbContext db) =>
{
    var result = await PrintFactoryOps.CollectSaleAsync(db, id, req);
    return result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Payload);
});

app.Run("http://0.0.0.0:5080");

internal static class DailyProfitLossReport
{
    /// <summary>Weekly holiday for daily P/L rent/salary allocation (Friday off).</summary>
    public static bool IsWeeklyHoliday(DateTime dayUtc) =>
        dayUtc.DayOfWeek == DayOfWeek.Friday;

    public static int WorkingDaysInMonth(int year, int month)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var count = 0;
        for (var d = 1; d <= daysInMonth; d++)
        {
            var day = new DateTime(year, month, d, 0, 0, 0, DateTimeKind.Utc);
            if (!IsWeeklyHoliday(day))
                count++;
        }
        return count;
    }

    /// <summary>Monthly rent or salary for one working day; zero on Friday.</summary>
    public static decimal MonthlyToWorkingDayShare(decimal monthlyAmount, DateTime dayUtc)
    {
        if (monthlyAmount == 0 || IsWeeklyHoliday(dayUtc))
            return 0;
        var workingDays = WorkingDaysInMonth(dayUtc.Year, dayUtc.Month);
        if (workingDays <= 0)
            return 0;
        return decimal.Round(monthlyAmount / workingDays, 2, MidpointRounding.AwayFromZero);
    }

    public static async Task<object> BuildFactoryAsync(
        BongoTexDbContext db,
        DateTime fromUtc,
        DateTime toInclusiveUtc,
        DateTime rangeEndExclusive,
        Guid? factorySiteId,
        CancellationToken ct)
    {
        var factories = await db.Sites.AsNoTracking()
            .Where(s => s.Type == "Factory")
            .Where(s => !factorySiteId.HasValue || factorySiteId.Value == Guid.Empty || s.Id == factorySiteId.Value)
            .OrderBy(s => s.Code)
            .ToListAsync(ct);
        var factoryIds = factories.Select(f => f.Id).ToHashSet();

        var items = await db.InventoryItems.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var stylesByPrefix = await ProductStyleOps.LoadByPrefixAsync(db, ct);
        var rents = await db.SiteMonthlyRents.AsNoTracking()
            .Where(r => factoryIds.Contains(r.SiteId))
            .ToDictionaryAsync(r => r.SiteId, ct);

        var finishing = await db.FinishingEntries.AsNoTracking()
            .Where(f => f.FinishedAtUtc >= fromUtc && f.FinishedAtUtc < rangeEndExclusive)
            .Where(f => factoryIds.Contains(f.FactorySiteId))
            .ToListAsync(ct);

        var scrapSales = await db.RawMaterialScrapSales.AsNoTracking()
            .Where(s => s.SoldAtUtc >= fromUtc && s.SoldAtUtc < rangeEndExclusive)
            .Where(s => factoryIds.Contains(s.SiteId))
            .ToListAsync(ct);

        var expenses = await db.ExpenseEntries.AsNoTracking()
            .Where(e => e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < rangeEndExclusive)
            .Where(e => e.ExpenseScope == "Factory")
            .ToListAsync(ct);

        var factoryEmployees = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive && e.EmployeeType != "PrintFactory"
                && (e.EmployeeType == "Factory" || e.EmployeeType == "Staff"
                    || e.EmployeeType == "Operator" || e.EmployeeType == "Helper"
                    || e.EmployeeType == "Print" || e.EmployeeType == "Security" || e.SiteId == null))
            .ToListAsync(ct);
        var totalFactoryMonthlySalary = factoryEmployees.Sum(e => e.MonthlySalary);

        var days = new List<object>();
        for (var day = fromUtc.Date; day <= toInclusiveUtc.Date; day = day.AddDays(1))
        {
            var dayEnd = day.AddDays(1);
            var dayFinish = finishing.Where(f => f.FinishedAtUtc >= day && f.FinishedAtUtc < dayEnd).ToList();
            var totalPiecesDay = dayFinish.Sum(f => f.QuantityFinished);
            var dailySalaryPool = MonthlyToWorkingDayShare(totalFactoryMonthlySalary, day);
            var totalDailyFactory = expenses
                .Where(e => e.Category == "DailyExpense"
                    && e.ExpenseDateUtc >= day && e.ExpenseDateUtc < dayEnd)
                .Sum(e => e.Amount);

            var siteRows = new List<FactoryPlDaySiteRow>();
            foreach (var factory in factories)
            {
                var siteFinish = dayFinish.Where(f => f.FactorySiteId == factory.Id).ToList();
                var piecesFinished = siteFinish.Sum(f => f.QuantityFinished);
                decimal productionValue = 0;
                foreach (var f in siteFinish)
                {
                    if (!items.TryGetValue(f.InventoryItemId, out var item)) continue;
                    productionValue += f.QuantityFinished
                        * ProductStyleOps.GetProductionCostForSku(stylesByPrefix, item.Sku);
                }

                rents.TryGetValue(factory.Id, out var rentRow);
                var rentShare = MonthlyToWorkingDayShare(rentRow?.MonthlyRent ?? 0m, day);

                var salaryShare = totalPiecesDay > 0
                    ? decimal.Round(dailySalaryPool * piecesFinished / totalPiecesDay, 2, MidpointRounding.AwayFromZero)
                    : factories.Count > 0
                        ? decimal.Round(dailySalaryPool / factories.Count, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                var dailyExpense = totalPiecesDay > 0
                    ? decimal.Round(totalDailyFactory * piecesFinished / totalPiecesDay, 2, MidpointRounding.AwayFromZero)
                    : factories.Count > 0
                        ? decimal.Round(totalDailyFactory / factories.Count, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                var scrapIncome = scrapSales
                    .Where(s => s.SiteId == factory.Id && s.SoldAtUtc >= day && s.SoldAtUtc < dayEnd)
                    .Sum(s => s.PaidAmount);

                var totalOverhead = rentShare + salaryShare + dailyExpense;
                siteRows.Add(new FactoryPlDaySiteRow(
                    factory.Id, factory.Code, factory.Name,
                    piecesFinished, productionValue, scrapIncome,
                    rentShare, salaryShare, dailyExpense, totalOverhead,
                    productionValue + scrapIncome - totalOverhead));
            }

            days.Add(new { DateUtc = day, Sites = siteRows, Totals = SumFactory(siteRows) });
        }

        return new
        {
            FromUtc = fromUtc,
            ToInclusiveUtc = toInclusiveUtc,
            Legend = "Factory P/L: finishing pcs × current registered production cost (by SKU prefix) + scrap cash − rent share − salary share − daily expenses. Rent and salary use monthly amount ÷ working days in month (Fridays off).",
            Days = days
        };
    }

    public static async Task<object> BuildSalesCenterAsync(
        BongoTexDbContext db,
        DateTime fromUtc,
        DateTime toInclusiveUtc,
        DateTime rangeEndExclusive,
        Guid? salesCenterSiteId,
        CancellationToken ct)
    {
        var centers = await db.Sites.AsNoTracking()
            .Where(s => s.Type == "SalesCenter" && s.IsActive)
            .Where(s => !salesCenterSiteId.HasValue || salesCenterSiteId.Value == Guid.Empty || s.Id == salesCenterSiteId.Value)
            .OrderBy(s => s.Code)
            .ToListAsync(ct);
        var centerIds = centers.Select(c => c.Id).ToHashSet();

        var items = await db.InventoryItems.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var rents = await db.SiteMonthlyRents.AsNoTracking()
            .Where(r => centerIds.Contains(r.SiteId))
            .ToDictionaryAsync(r => r.SiteId, ct);

        var sales = await db.SalesTransactions.AsNoTracking()
            .Where(s => s.SoldAtUtc >= fromUtc && s.SoldAtUtc < rangeEndExclusive)
            .Where(s => centerIds.Contains(s.SiteId))
            .ToListAsync(ct);
        var returns = await db.SalesReturns.AsNoTracking()
            .Where(r => r.ReturnedAtUtc >= fromUtc && r.ReturnedAtUtc < rangeEndExclusive)
            .Where(r => centerIds.Contains(r.SiteId))
            .ToListAsync(ct);
        var expenses = await db.ExpenseEntries.AsNoTracking()
            .Where(e => e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < rangeEndExclusive)
            .Where(e => e.ExpenseScope == "SalesCenter")
            .ToListAsync(ct);

        var scEmployees = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive && e.EmployeeType == "SalesCenter" && e.SiteId != null)
            .ToListAsync(ct);
        var salaryBySite = scEmployees.GroupBy(e => e.SiteId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.MonthlySalary));

        var days = new List<object>();
        for (var day = fromUtc.Date; day <= toInclusiveUtc.Date; day = day.AddDays(1))
        {
            var dayEnd = day.AddDays(1);
            var siteRows = new List<SalesCenterPlDaySiteRow>();
            foreach (var center in centers)
            {
                var daySales = sales.Where(s => s.SiteId == center.Id && s.SoldAtUtc >= day && s.SoldAtUtc < dayEnd).ToList();
                var dayReturns = returns.Where(r => r.SiteId == center.Id && r.ReturnedAtUtc >= day && r.ReturnedAtUtc < dayEnd).ToList();
                var salesAmount = daySales.Sum(s => s.TotalAmount);
                var returnAmount = dayReturns.Sum(r => r.TotalAmount);
                var netSales = salesAmount - returnAmount;
                var piecesSold = daySales.Where(s => s.InventoryItemId.HasValue).Sum(s => s.Quantity)
                    - dayReturns.Sum(r => r.Quantity);

                decimal cogs = 0;
                foreach (var s in daySales)
                {
                    if (!s.InventoryItemId.HasValue || !items.TryGetValue(s.InventoryItemId.Value, out var item)) continue;
                    cogs += s.Quantity * ProductCost(item);
                }
                foreach (var r in dayReturns)
                {
                    if (!items.TryGetValue(r.InventoryItemId, out var item)) continue;
                    cogs -= r.Quantity * ProductCost(item);
                }

                rents.TryGetValue(center.Id, out var rentRow);
                var rentShare = MonthlyToWorkingDayShare(rentRow?.MonthlyRent ?? 0m, day);
                var salaryShare = MonthlyToWorkingDayShare(salaryBySite.GetValueOrDefault(center.Id, 0m), day);
                var dailyExpense = expenses
                    .Where(e => e.SiteId == center.Id && e.Category == "DailyExpense"
                        && e.ExpenseDateUtc >= day && e.ExpenseDateUtc < dayEnd)
                    .Sum(e => e.Amount);

                var grossProfit = netSales - cogs;
                var totalOverhead = rentShare + salaryShare + dailyExpense;
                siteRows.Add(new SalesCenterPlDaySiteRow(
                    center.Id, center.Code, center.Name,
                    piecesSold, salesAmount, returnAmount, netSales, cogs, grossProfit,
                    rentShare, salaryShare, dailyExpense, totalOverhead,
                    grossProfit - totalOverhead));
            }

            days.Add(new { DateUtc = day, Sites = siteRows, Totals = SumSalesCenter(siteRows) });
        }

        return new
        {
            FromUtc = fromUtc,
            ToInclusiveUtc = toInclusiveUtc,
            Legend = "Sales centre P/L: net sales − COGS (registered product cost) − rent share − salary share − daily expenses. Rent and salary use monthly amount ÷ working days in month (Fridays off).",
            Days = days
        };
    }

    private static decimal ProductCost(InventoryItem item) =>
        item.UnitPrice > 0 ? item.UnitPrice : item.ProductionCost;

    private static FactoryPlDayTotals SumFactory(List<FactoryPlDaySiteRow> rows) => new(
        rows.Sum(r => r.PiecesFinished),
        rows.Sum(r => r.ProductionValue),
        rows.Sum(r => r.ScrapIncome),
        rows.Sum(r => r.RentShare),
        rows.Sum(r => r.SalaryShare),
        rows.Sum(r => r.DailyExpense),
        rows.Sum(r => r.TotalOverhead),
        rows.Sum(r => r.NetProfitLoss));

    private static SalesCenterPlDayTotals SumSalesCenter(List<SalesCenterPlDaySiteRow> rows) => new(
        rows.Sum(r => r.PiecesSold),
        rows.Sum(r => r.SalesAmount),
        rows.Sum(r => r.ReturnAmount),
        rows.Sum(r => r.NetSales),
        rows.Sum(r => r.Cogs),
        rows.Sum(r => r.GrossProfit),
        rows.Sum(r => r.RentShare),
        rows.Sum(r => r.SalaryShare),
        rows.Sum(r => r.DailyExpense),
        rows.Sum(r => r.TotalOverhead),
        rows.Sum(r => r.NetProfitLoss));

    private sealed record FactoryPlDaySiteRow(
        Guid Id, string Code, string Name,
        int PiecesFinished, decimal ProductionValue, decimal ScrapIncome,
        decimal RentShare, decimal SalaryShare, decimal DailyExpense, decimal TotalOverhead,
        decimal NetProfitLoss);

    private sealed record FactoryPlDayTotals(
        int PiecesFinished, decimal ProductionValue, decimal ScrapIncome,
        decimal RentShare, decimal SalaryShare, decimal DailyExpense, decimal TotalOverhead,
        decimal NetProfitLoss);

    private sealed record SalesCenterPlDaySiteRow(
        Guid Id, string Code, string Name,
        int PiecesSold, decimal SalesAmount, decimal ReturnAmount, decimal NetSales,
        decimal Cogs, decimal GrossProfit,
        decimal RentShare, decimal SalaryShare, decimal DailyExpense, decimal TotalOverhead,
        decimal NetProfitLoss);

    private sealed record SalesCenterPlDayTotals(
        int PiecesSold, decimal SalesAmount, decimal ReturnAmount, decimal NetSales,
        decimal Cogs, decimal GrossProfit,
        decimal RentShare, decimal SalaryShare, decimal DailyExpense, decimal TotalOverhead,
        decimal NetProfitLoss);
}

internal sealed record SalesCenterPeriodRow(
    Guid SiteId,
    string SiteCode,
    string SiteName,
    int PiecesFromFactory,
    decimal TransferAmountFromFactory,
    int StockPiecesOnHand,
    decimal StockValueOnHand,
    int PiecesSold,
    decimal SalesAmount,
    decimal InitialPaymentAtSale,
    decimal DueCollectionCash,
    decimal CreditSalesAmount,
    decimal DueRemainingOnPeriodSales,
    int ReturnPieces,
    decimal ReturnAmount,
    decimal SalesDiscount,
    decimal SettlementDiscount,
    decimal TotalDiscount,
    decimal MaxAllowedSalesDiscount,
    decimal PaidToManager,
    decimal DailyCost,
    decimal SalaryPaid,
    decimal Rent,
    decimal OtherCost);

internal static class SalesCenterPeriodReport
{
    public static async Task<object> BuildAsync(
        BongoTexDbContext db,
        DateTime fromUtc,
        DateTime toInclusiveUtc,
        DateTime rangeEndExclusive,
        CancellationToken cancellationToken)
    {
        var centers = await db.Sites.AsNoTracking()
            .Where(x => x.Type == "SalesCenter")
            .OrderBy(x => x.Code)
            .ToListAsync(cancellationToken);

        var salesTx = await db.SalesTransactions.AsNoTracking()
            .Where(x => x.SoldAtUtc >= fromUtc && x.SoldAtUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);

        var returns = await db.SalesReturns.AsNoTracking()
            .Where(x => x.ReturnedAtUtc >= fromUtc && x.ReturnedAtUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);

        var colls = await db.SalesCollections.AsNoTracking()
            .Where(x => x.CollectedAtUtc >= fromUtc && x.CollectedAtUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);

        var collTxnIds = colls.Select(x => x.SalesTransactionId).Distinct().ToList();
        var collTxns = collTxnIds.Count == 0
            ? []
            : await db.SalesTransactions.AsNoTracking()
                .Where(t => collTxnIds.Contains(t.Id))
                .ToListAsync(cancellationToken);

        var allTxnIdsForColl = collTxns.Select(t => t.Id).ToHashSet();
        var txnById = salesTx.ToDictionary(t => t.Id);
        foreach (var t in collTxns)
            txnById[t.Id] = t;

        var itemIds = salesTx
            .Where(x => x.InventoryItemId.HasValue)
            .Select(x => x.InventoryItemId!.Value)
            .Distinct()
            .ToList();
        var itemLookup = itemIds.Count == 0
            ? new Dictionary<Guid, InventoryItem>()
            : await db.InventoryItems.AsNoTracking()
                .Where(i => itemIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, cancellationToken);

        var expenses = await db.ExpenseEntries.AsNoTracking()
            .Where(x => x.ExpenseDateUtc >= fromUtc && x.ExpenseDateUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);

        var factoryTransfers = await (
            from t in db.StockTransfers.AsNoTracking()
            join i in db.InventoryItems.AsNoTracking() on t.InventoryItemId equals i.Id
            join fromSite in db.Sites.AsNoTracking() on t.FromSiteId equals fromSite.Id
            join toSite in db.Sites.AsNoTracking() on t.ToSiteId equals toSite.Id
            where t.TransferredAtUtc >= fromUtc && t.TransferredAtUtc < rangeEndExclusive
            where fromSite.Type == "Factory" && toSite.Type == "SalesCenter"
            select new
            {
                ToSiteId = toSite.Id,
                t.Quantity,
                LineAmount = t.Quantity * (i.SalesPrice > 0 ? i.SalesPrice : i.UnitPrice)
            }).ToListAsync(cancellationToken);

        var transfersByCenter = factoryTransfers
            .GroupBy(x => x.ToSiteId)
            .ToDictionary(
                g => g.Key,
                g => (Pieces: g.Sum(x => x.Quantity), Amount: g.Sum(x => x.LineAmount)));

        var centerIds = centers.Select(c => c.Id).ToList();
        var stockOnHand = centerIds.Count == 0
            ? []
            : await (
                from stock in db.InventoryStocks.AsNoTracking()
                join item in db.InventoryItems.AsNoTracking() on stock.InventoryItemId equals item.Id
                where centerIds.Contains(stock.SiteId) && stock.Quantity > 0
                select new
                {
                    stock.SiteId,
                    stock.Quantity,
                    LineValue = stock.Quantity * (item.SalesPrice > 0 ? item.SalesPrice : item.UnitPrice)
                }).ToListAsync(cancellationToken);

        var stockByCenter = stockOnHand
            .GroupBy(x => x.SiteId)
            .ToDictionary(
                g => g.Key,
                g => (Pieces: g.Sum(x => x.Quantity), Value: g.Sum(x => x.LineValue)));

        var rows = new List<SalesCenterPeriodRow>();
        foreach (var center in centers)
        {
            var siteSales = salesTx.Where(x => x.SiteId == center.Id).ToList();
            var siteReturns = returns.Where(x => x.SiteId == center.Id).ToList();
            var siteLineIds = siteSales.Select(x => x.Id).ToHashSet();
            var siteColls = colls
                .Where(c => allTxnIdsForColl.Contains(c.SalesTransactionId) && txnById.TryGetValue(c.SalesTransactionId, out var st) && st.SiteId == center.Id)
                .ToList();

            var piecesSold = siteSales.Where(x => x.InventoryItemId.HasValue).Sum(x => x.Quantity);
            var salesAmount = siteSales.Sum(x => x.TotalAmount);
            var creditSalesAmount = siteSales.Where(x => x.IsCredit).Sum(x => x.TotalAmount);
            var dueRemainingOnPeriodSales = siteSales.Sum(x => x.DueAmount);

            decimal initialPaymentAtSale = 0;
            decimal dueCollectionCash = 0;
            decimal settlementDiscount = 0;
            decimal salesDiscount = 0;
            decimal maxAllowedSalesDiscount = 0;

            foreach (var invGroup in siteSales.GroupBy(x => (x.InvoiceNo ?? x.SalesNo).Trim()))
            {
                var lines = invGroup.ToList();
                var invTotal = lines.Sum(x => x.TotalAmount);
                var dueNow = lines.Sum(x => x.DueAmount);
                var lineIds = lines.Select(x => x.Id).ToHashSet();
                var collsInRangeOnInv = siteColls.Where(c => lineIds.Contains(c.SalesTransactionId)).ToList();
                var collsInRangeSum = collsInRangeOnInv.Sum(c => c.Amount);

                if (lines.Any(x => x.IsCredit))
                    initialPaymentAtSale += Math.Max(0m, invTotal - dueNow - collsInRangeSum);
                else
                    initialPaymentAtSale += invTotal;

                var (disc, maxDisc) = CustomerDueLedgerDiscount.Compute(lines, itemLookup);
                salesDiscount += disc;
                maxAllowedSalesDiscount += maxDisc;
            }

            foreach (var c in siteColls)
            {
                var kind = CustomerDueLedgerBuilder.ClassifyCollectionNote(c.Note);
                if (kind == "Payment")
                    dueCollectionCash += c.Amount;
                else if (kind == "SettlementDiscount")
                    settlementDiscount += c.Amount;
            }

            var scExpenses = expenses.Where(e =>
                string.Equals(e.ExpenseScope, "SalesCenter", StringComparison.OrdinalIgnoreCase)
                && e.SiteId == center.Id).ToList();

            var paidToManager = scExpenses
                .Where(e => string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);
            var dailyCost = scExpenses
                .Where(e => string.Equals(e.Category, "DailyExpense", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);
            var salaryPaid = scExpenses
                .Where(e => string.Equals(e.Category, "Salary", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);
            var rent = scExpenses
                .Where(e => string.Equals(e.Category, "Rent", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);
            var otherCost = scExpenses
                .Where(e => !string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(e.Category, "DailyExpense", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(e.Category, "Salary", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(e.Category, "Rent", StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Amount);

            transfersByCenter.TryGetValue(center.Id, out var xfer);
            var piecesFromFactory = xfer.Pieces;
            var transferAmountFromFactory = xfer.Amount;
            stockByCenter.TryGetValue(center.Id, out var stock);
            var stockPiecesOnHand = stock.Pieces;
            var stockValueOnHand = stock.Value;

            rows.Add(new SalesCenterPeriodRow(
                center.Id,
                center.Code,
                center.Name,
                piecesFromFactory,
                transferAmountFromFactory,
                stockPiecesOnHand,
                stockValueOnHand,
                piecesSold,
                salesAmount,
                initialPaymentAtSale,
                dueCollectionCash,
                creditSalesAmount,
                dueRemainingOnPeriodSales,
                siteReturns.Sum(x => x.Quantity),
                siteReturns.Sum(x => x.TotalAmount),
                salesDiscount,
                settlementDiscount,
                salesDiscount + settlementDiscount,
                maxAllowedSalesDiscount,
                paidToManager,
                dailyCost,
                salaryPaid,
                rent,
                otherCost));
        }

        SalesCenterPeriodRow SumRows(string code, string name) => new(
            Guid.Empty,
            code,
            name,
            rows.Sum(x => x.PiecesFromFactory),
            rows.Sum(x => x.TransferAmountFromFactory),
            rows.Sum(x => x.StockPiecesOnHand),
            rows.Sum(x => x.StockValueOnHand),
            rows.Sum(x => x.PiecesSold),
            rows.Sum(x => x.SalesAmount),
            rows.Sum(x => x.InitialPaymentAtSale),
            rows.Sum(x => x.DueCollectionCash),
            rows.Sum(x => x.CreditSalesAmount),
            rows.Sum(x => x.DueRemainingOnPeriodSales),
            rows.Sum(x => x.ReturnPieces),
            rows.Sum(x => x.ReturnAmount),
            rows.Sum(x => x.SalesDiscount),
            rows.Sum(x => x.SettlementDiscount),
            rows.Sum(x => x.TotalDiscount),
            rows.Sum(x => x.MaxAllowedSalesDiscount),
            rows.Sum(x => x.PaidToManager),
            rows.Sum(x => x.DailyCost),
            rows.Sum(x => x.SalaryPaid),
            rows.Sum(x => x.Rent),
            rows.Sum(x => x.OtherCost));

        return new
        {
            FromUtc = fromUtc,
            ToInclusiveUtc = toInclusiveUtc,
            Rows = rows,
            Totals = SumRows("ALL", "All sales centers")
        };
    }
}

internal static class FactoryPettyBalanceOps
{
    /// <summary>Petty on hand is never negative; shortfall is unrecorded inflow needed to match posted spend.</summary>
    public static (decimal OnHand, decimal Shortfall) Normalize(decimal computed) =>
        computed < 0 ? (0m, -computed) : (computed, 0m);
}

internal static class FactoryPettyCashbook
{
    /// <summary>Cash received from factory scrap/wastage sales (initial paid + later due collections on PaidAmount).</summary>
    public static async Task<decimal> SumScrapSaleCashInAsync(
        BongoTexDbContext db,
        DateTime? fromUtcInclusive,
        DateTime toExclusiveUtc,
        CancellationToken cancellationToken = default)
    {
        var q = db.RawMaterialScrapSales.AsNoTracking()
            .Where(x => x.SoldAtUtc < toExclusiveUtc);
        if (fromUtcInclusive is { } from)
            q = q.Where(x => x.SoldAtUtc >= from);
        return await q.SumAsync(x => (decimal?)x.PaidAmount, cancellationToken) ?? 0m;
    }
}

internal static class DailyCashBalanceReport
{
    public static async Task<IResult> HandleAsync(
        DailyCashBalanceRequest req,
        BongoTexDbContext db,
        CancellationToken cancellationToken)
    {
        if (!ManagerCashbook.TryResolveFinanceRange(req.From, req.To, out var fromUtc, out var toInclusive, out var rangeEndExclusive, out var rangeError))
            return Results.BadRequest(rangeError);

        var openingMgr = req.OpeningManager ?? 0m;
        var openingPetty = req.OpeningFactoryPetty ?? 0m;
        var centerOpenings = req.OpeningsBySalesCenter ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var centers = await db.Sites.AsNoTracking()
            .Where(x => x.Type == "SalesCenter")
            .OrderBy(x => x.Code)
            .ToListAsync(cancellationToken);

        var expenses = await db.ExpenseEntries.AsNoTracking()
            .Where(x => x.ExpenseDateUtc >= fromUtc && x.ExpenseDateUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);
        var movements = await db.CashMovements.AsNoTracking()
            .Where(x => x.MovementDateUtc >= fromUtc && x.MovementDateUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);
        var salesTx = await db.SalesTransactions.AsNoTracking()
            .Where(x => x.SoldAtUtc >= fromUtc && x.SoldAtUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);
        var scrapSales = await db.RawMaterialScrapSales.AsNoTracking()
            .Where(x => x.SoldAtUtc >= fromUtc && x.SoldAtUtc < rangeEndExclusive)
            .ToListAsync(cancellationToken);

        var days = new List<object>();
        for (var day = fromUtc; day <= toInclusive; day = day.AddDays(1))
        {
            var dayEndExclusive = day.AddDays(1);

            var remit = expenses.Where(e =>
                    string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
                    && string.Equals(e.PartyName, FinanceConventions.ManagerFloatPartyName, StringComparison.Ordinal)
                    && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < dayEndExclusive)
                .Sum(x => x.Amount);
            var ownerDr = expenses.Where(e =>
                    string.Equals(e.Category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal)
                    && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < dayEndExclusive)
                .Sum(x => x.Amount);
            var mvs = movements.Where(m => m.MovementDateUtc >= fromUtc && m.MovementDateUtc < dayEndExclusive).ToList();
            var outMgr = mvs.Where(m => string.Equals(m.FromPool, FinanceConventions.CashPoolManager, StringComparison.Ordinal)).Sum(x => x.Amount);
            var inMgr = mvs.Where(m => string.Equals(m.ToPool, FinanceConventions.CashPoolManager, StringComparison.Ordinal)).Sum(x => x.Amount);
            var manager = openingMgr + remit - ownerDr + inMgr - outMgr;

            var pettyIn = mvs.Where(m => string.Equals(m.ToPool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal)).Sum(x => x.Amount);
            var pettyOut = mvs.Where(m => string.Equals(m.FromPool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal)).Sum(x => x.Amount);
            var facOp = expenses.Where(e =>
                    ManagerCashbook.ExpenseUsesFactoryPettyCash(e)
                    && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < dayEndExclusive)
                .Sum(x => x.Amount);
            var scrapCashIn = scrapSales.Where(s =>
                    s.SoldAtUtc >= fromUtc && s.SoldAtUtc < dayEndExclusive)
                .Sum(s => s.PaidAmount);
            var factoryPettyComputed = openingPetty + pettyIn - pettyOut - facOp + scrapCashIn;
            var (factoryPetty, factoryPettyShortfall) = FactoryPettyBalanceOps.Normalize(factoryPettyComputed);

            var scRows = new List<object>();
            foreach (var c in centers)
            {
                var openC = 0m;
                if (centerOpenings.TryGetValue(c.Id.ToString("N"), out var vN))
                    openC = vN;
                else if (centerOpenings.TryGetValue(c.Id.ToString("D"), out var vD))
                    openC = vD;

                var paid = salesTx.Where(t =>
                        t.SiteId == c.Id
                        && t.SoldAtUtc >= fromUtc && t.SoldAtUtc < dayEndExclusive)
                    .Sum(t => t.PaidAmount);

                var remitFromCenter = expenses.Where(e =>
                        string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
                        && string.Equals(e.PartyName, FinanceConventions.ManagerFloatPartyName, StringComparison.Ordinal)
                        && e.SiteId == c.Id
                        && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < dayEndExclusive)
                    .Sum(x => x.Amount);
                var scOther = expenses.Where(e =>
                        string.Equals(e.ExpenseScope, "SalesCenter", StringComparison.Ordinal)
                        && e.SiteId == c.Id
                        && !string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
                        && e.ExpenseDateUtc >= fromUtc && e.ExpenseDateUtc < dayEndExclusive)
                    .Sum(x => x.Amount);
                var cin = mvs.Where(m =>
                    string.Equals(m.ToPool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) && m.ToSiteId == c.Id).Sum(x => x.Amount);
                var cout = mvs.Where(m =>
                    string.Equals(m.FromPool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) && m.FromSiteId == c.Id).Sum(x => x.Amount);
                var centerBal = openC + paid - remitFromCenter - scOther + cin - cout;
                scRows.Add(new { SiteId = c.Id, c.Code, c.Name, Balance = centerBal });
            }

            days.Add(new
            {
                DateUtc = day,
                ManagerCash = manager,
                FactoryPettyCash = factoryPetty,
                FactoryPettyCashShortfall = factoryPettyShortfall,
                SalesCenters = scRows
            });
        }

        return Results.Ok(new
        {
            FromUtc = fromUtc,
            ToInclusiveUtc = toInclusive,
            Legend = "Manager: opening + remittances - owner draw + (transfers to manager - from manager). Factory petty: opening + transfers in + scrap/wastage sale cash received - transfers out - garment factory and print factory supplier/salary/rent/daily expenses (same cash box; print still tracked separately by scope). Each sales center: opening + sales PaidAmount (includes due collections) - remittances from that center - other sales-center expenses + (pool transfers to center - from center). All amounts are cumulative within the selected date range through each calendar day (UTC).",
            Days = days
        });
    }

    public static async Task<FinanceWalletSummary> ComputeWalletBalancesAsOfAsync(
        BongoTexDbContext db,
        DateTime asOfExclusiveUtc,
        decimal openingManager,
        decimal openingFactoryPetty,
        Dictionary<string, decimal> openingsBySalesCenter,
        CancellationToken cancellationToken)
    {
        var centers = await db.Sites.AsNoTracking()
            .Where(x => x.Type == "SalesCenter")
            .OrderBy(x => x.Code)
            .ToListAsync(cancellationToken);

        var expenses = await db.ExpenseEntries.AsNoTracking()
            .Where(x => x.ExpenseDateUtc < asOfExclusiveUtc)
            .ToListAsync(cancellationToken);
        var movements = await db.CashMovements.AsNoTracking()
            .Where(x => x.MovementDateUtc < asOfExclusiveUtc)
            .ToListAsync(cancellationToken);
        var salesTx = await db.SalesTransactions.AsNoTracking()
            .Where(x => x.SoldAtUtc < asOfExclusiveUtc)
            .ToListAsync(cancellationToken);
        var scrapCashIn = await FactoryPettyCashbook.SumScrapSaleCashInAsync(
            db, null, asOfExclusiveUtc, cancellationToken);

        var remit = expenses.Where(e =>
                string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
                && string.Equals(e.PartyName, FinanceConventions.ManagerFloatPartyName, StringComparison.Ordinal))
            .Sum(x => x.Amount);
        var ownerDr = expenses.Where(e =>
                string.Equals(e.Category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
            .Sum(x => x.Amount);
        var outMgr = movements.Where(m => string.Equals(m.FromPool, FinanceConventions.CashPoolManager, StringComparison.Ordinal)).Sum(x => x.Amount);
        var inMgr = movements.Where(m => string.Equals(m.ToPool, FinanceConventions.CashPoolManager, StringComparison.Ordinal)).Sum(x => x.Amount);
        var manager = openingManager + remit - ownerDr + inMgr - outMgr;

        var pettyIn = movements.Where(m => string.Equals(m.ToPool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal)).Sum(x => x.Amount);
        var pettyOut = movements.Where(m => string.Equals(m.FromPool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal)).Sum(x => x.Amount);
        var facOp = expenses.Where(e => ManagerCashbook.ExpenseUsesFactoryPettyCash(e))
            .Sum(x => x.Amount);
        var factoryPettyComputed = openingFactoryPetty + pettyIn - pettyOut - facOp + scrapCashIn;
        var (factoryPetty, factoryPettyShortfall) = FactoryPettyBalanceOps.Normalize(factoryPettyComputed);

        var scRows = new List<FinanceSiteCashRow>();
        foreach (var c in centers)
        {
            var openC = 0m;
            if (openingsBySalesCenter.TryGetValue(c.Id.ToString("N"), out var vN))
                openC = vN;
            else if (openingsBySalesCenter.TryGetValue(c.Id.ToString("D"), out var vD))
                openC = vD;

            var paid = salesTx.Where(t => t.SiteId == c.Id).Sum(t => t.PaidAmount);

            var remitFromCenter = expenses.Where(e =>
                    string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
                    && string.Equals(e.PartyName, FinanceConventions.ManagerFloatPartyName, StringComparison.Ordinal)
                    && e.SiteId == c.Id)
                .Sum(x => x.Amount);
            var scOther = expenses.Where(e =>
                    string.Equals(e.ExpenseScope, "SalesCenter", StringComparison.Ordinal)
                    && e.SiteId == c.Id
                    && !string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal))
                .Sum(x => x.Amount);
            var cin = movements.Where(m =>
                string.Equals(m.ToPool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) && m.ToSiteId == c.Id).Sum(x => x.Amount);
            var cout = movements.Where(m =>
                string.Equals(m.FromPool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) && m.FromSiteId == c.Id).Sum(x => x.Amount);
            var centerBal = openC + paid - remitFromCenter - scOther + cin - cout;
            scRows.Add(new FinanceSiteCashRow(c.Id, c.Code, c.Name, centerBal));
        }

        var totalModeledCash = manager + factoryPetty + scRows.Sum(x => x.Balance);
        var hint = "Model cash from posted data before the snapshot cutoff (UTC, exclusive). If the latest cash pool transfer is dated on a later UTC calendar day than \"today\", that day is included so same-day transfers are not hidden. Openings assumed 0. Manager excludes supplier/salary (treated as petty); see Daily cash balances for range and custom openings. Sales-center PaidAmount already includes due collections (not added twice). Factory petty includes cash received from factory scrap/wastage sales (PaidAmount, including later due collections). Factory petty on hand is never shown below 0; supplier/salary/rent/daily factory payments are blocked when on hand is insufficient.";
        if (factoryPettyShortfall > 0)
            hint += $" Factory petty shortfall {factoryPettyShortfall:0.##}: record Manager → Factory petty transfer or opening petty (expenses were posted without matching inflow).";

        return new FinanceWalletSummary(
            asOfExclusiveUtc,
            manager,
            factoryPetty,
            factoryPettyShortfall,
            scRows,
            totalModeledCash,
            hint);
    }

    /// <summary>Available cash in a pool through end of <paramref name="movementDateUtc"/> calendar day (UTC).</summary>
    public static async Task<decimal> GetCashPoolBalanceAsOfAsync(
        BongoTexDbContext db,
        string pool,
        Guid? salesCenterSiteId,
        DateTime movementDateUtc,
        CancellationToken cancellationToken = default)
    {
        var mv = movementDateUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(movementDateUtc, DateTimeKind.Utc)
            : movementDateUtc.ToUniversalTime();
        var asOfExclusive = new DateTime(mv.Year, mv.Month, mv.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);

        var wallets = await ComputeWalletBalancesAsOfAsync(
            db, asOfExclusive, 0m, 0m, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase), cancellationToken);

        if (string.Equals(pool, FinanceConventions.CashPoolManager, StringComparison.Ordinal))
            return wallets.ManagerCashBalance;
        if (string.Equals(pool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal))
            return wallets.FactoryPettyCashBalance;
        if (string.Equals(pool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) && salesCenterSiteId is { } siteId)
        {
            var row = wallets.SalesCenters.FirstOrDefault(x => x.SiteId == siteId);
            return row?.Balance ?? 0m;
        }

        return 0m;
    }

    /// <summary>Rejects expense if the paying cash pool would go negative (same rules as finance summary balances).</summary>
    public static async Task<string?> ValidateExpenseCashAsync(
        BongoTexDbContext db,
        string expenseScope,
        string category,
        Guid? siteId,
        decimal amount,
        DateTime expenseDateUtc,
        CancellationToken cancellationToken = default)
    {
        const decimal tolerance = 0.0001m;
        if (amount <= 0)
            return null;

        if (string.Equals(category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
        {
            var managerAvail = await GetCashPoolBalanceAsOfAsync(
                db, FinanceConventions.CashPoolManager, null, expenseDateUtc, cancellationToken);
            if (amount > managerAvail + tolerance)
                return $"Insufficient manager cash (on hand {managerAvail:0.00}). Record sales-centre remittances or transfer cash to manager first.";
            return null;
        }

        if (ManagerCashbook.UsesFactoryPettyCash(expenseScope, category))
        {
            var pettyAvail = await GetCashPoolBalanceAsOfAsync(
                db, FinanceConventions.CashPoolFactoryPetty, null, expenseDateUtc, cancellationToken);
            if (amount > pettyAvail + tolerance)
            {
                if (pettyAvail <= tolerance)
                    return "No factory petty cash on hand. Transfer cash to factory petty (Manager → Factory petty) or set opening petty in Daily cash balances before paying supplier/salary/rent/daily expenses.";
                return $"Insufficient factory petty cash (on hand {pettyAvail:0.00}). Transfer cash to factory petty (e.g. Manager → Factory petty) before this expense.";
            }
            return null;
        }

        if (string.Equals(expenseScope, "SalesCenter", StringComparison.OrdinalIgnoreCase))
        {
            if (siteId is not { } sid || sid == Guid.Empty)
                return "Sales center is required.";
            var site = await db.Sites.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == sid && x.Type == "SalesCenter", cancellationToken);
            if (site is null)
                return "Invalid sales center.";
            var centerAvail = await GetCashPoolBalanceAsOfAsync(
                db, FinanceConventions.CashPoolSalesCenter, sid, expenseDateUtc, cancellationToken);
            if (amount > centerAvail + tolerance)
                return $"Insufficient cash at {site.Code} (on hand {centerAvail:0.00}). Sales/collections must cover this payment.";
            return null;
        }

        return null;
    }

    public static string DescribeCashPool(string pool, Site? salesCenterSite = null)
    {
        if (string.Equals(pool, FinanceConventions.CashPoolManager, StringComparison.Ordinal))
            return "Manager";
        if (string.Equals(pool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal))
            return "Factory petty";
        if (string.Equals(pool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal))
            return salesCenterSite is null ? "Sales center" : $"{salesCenterSite.Code} - {salesCenterSite.Name}";
        return pool;
    }
}

internal static class SalesPrintSnapshot
{
    public static void ApplyFromItem(SalesTransaction tx, InventoryItem item)
    {
        tx.IsPrintItemAtSale = item.IsPrintItem;
        tx.PrintChargePerPieceAtSale = item.IsPrintItem ? item.PrintChargePerPiece : 0m;
    }

    public static async Task ClearSnapshotsForItemAsync(BongoTexDbContext db, Guid inventoryItemId)
    {
        var rows = await db.SalesTransactions
            .Where(s => s.InventoryItemId == inventoryItemId && (s.IsPrintItemAtSale || s.PrintChargePerPieceAtSale != 0))
            .ToListAsync();
        foreach (var s in rows)
        {
            s.IsPrintItemAtSale = false;
            s.PrintChargePerPieceAtSale = 0m;
        }
    }
}

internal static class SalesPricing
{
    /// <summary>Resolves per-unit selling price: explicit sellingUnitPrice wins; otherwise catalogue list vs discount flag.</summary>
    public static bool TryResolveUnitPrice(
        InventoryItem item,
        bool useCatalogDiscountDefault,
        decimal? sellingUnitPrice,
        out decimal unitPrice,
        out string? error)
    {
        var baseSalesPrice = item.SalesPrice > 0 ? item.SalesPrice : item.UnitPrice;
        if (sellingUnitPrice is { } supVal && supVal > 0)
        {
            unitPrice = supVal;
            error = null;
            return true;
        }

        unitPrice = useCatalogDiscountDefault ? baseSalesPrice - item.DiscountPrice : baseSalesPrice;
        if (unitPrice <= 0)
        {
            error = $"Invalid selling price for {item.Sku}. Check sales/discount pricing.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>Manager float cashbook: date ranges, persisted/inferred bucket tags, and reconciliation helpers.</summary>
internal static class ManagerCashbook
{
    public static bool TryDayStartUtc(string? s, out DateTime utcMidnight)
    {
        utcMidnight = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        if (!DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return false;
        utcMidnight = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    public static bool TryResolveFinanceRange(string? from, string? to, out DateTime fromUtc, out DateTime toInclusiveStart, out DateTime endExclusive, out string? error)
    {
        var now = DateTime.UtcNow;
        var defaultFrom = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var defaultTo = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 0, 0, 0, DateTimeKind.Utc);
        fromUtc = TryDayStartUtc(from, out var f) ? f : defaultFrom;
        toInclusiveStart = TryDayStartUtc(to, out var t) ? t : defaultTo;
        if (toInclusiveStart < fromUtc)
        {
            error = "End date must be on or after start date.";
            endExclusive = default;
            return false;
        }

        error = null;
        endExclusive = toInclusiveStart.AddDays(1);
        return true;
    }

    public static bool IsFactoryOperatingCategory(string category) =>
        category == "SupplierPayment" || category == "Salary" || category == "Rent" || category == "DailyExpense";

    /// <summary>Operating expenses paid from the shared BongoTex factory petty cash pool (garment or print scope).</summary>
    public static bool UsesFactoryPettyCash(string expenseScope, string category)
    {
        if (!IsFactoryOperatingCategory(category))
            return false;
        return string.Equals(expenseScope, "Factory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(expenseScope, PrintFactoryConventions.ExpenseScope, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ExpenseUsesFactoryPettyCash(ExpenseEntry e) =>
        UsesFactoryPettyCash(e.ExpenseScope, e.Category);

    /// <summary>Factory-side outflows in the manager cashbook: factory-scope expenses, plus owner-scope owner draw (not factory ops).</summary>
    public static IQueryable<ExpenseEntry> QueryCashbookOutflowRows(IQueryable<ExpenseEntry> set, DateTime fromUtc, DateTime endExclusive) =>
        set.Where(x => x.ExpenseDateUtc >= fromUtc && x.ExpenseDateUtc < endExclusive
            && (x.ExpenseScope == "Factory"
                || (x.ExpenseScope == "Owner" && x.Category == FinanceConventions.OwnerDrawCategory)));

    public static string InferCashbookType(ExpenseEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.CashbookType))
            return e.CashbookType.Trim();
        if (string.Equals(e.Category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
            && string.Equals(e.PartyName, FinanceConventions.ManagerFloatPartyName, StringComparison.Ordinal))
            return FinanceConventions.CashbookManagerFloatIn;
        if (string.Equals(e.Category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
            return FinanceConventions.CashbookOwnerDraw;
        if (string.Equals(e.ExpenseScope, "Owner", StringComparison.Ordinal))
            return string.Equals(e.Category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal)
                ? FinanceConventions.CashbookOwnerDraw
                : FinanceConventions.CashbookOtherOutflow;
        if (string.Equals(e.ExpenseScope, "Factory", StringComparison.Ordinal))
            return IsFactoryOperatingCategory(e.Category)
                ? FinanceConventions.CashbookFactorySpend
                : FinanceConventions.CashbookOtherOutflow;
        if (string.Equals(e.ExpenseScope, "SalesCenter", StringComparison.Ordinal))
            return FinanceConventions.CashbookOtherOutflow;
        return string.Empty;
    }

    public static string InferCashflowDirection(ExpenseEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.CashflowDirection))
            return e.CashflowDirection.Trim();
        var t = InferCashbookType(e);
        if (t == FinanceConventions.CashbookManagerFloatIn)
            return FinanceConventions.CashflowIn;
        if (t is FinanceConventions.CashbookFactorySpend or FinanceConventions.CashbookPrintFactorySpend
            or FinanceConventions.CashbookOwnerDraw or FinanceConventions.CashbookOtherOutflow)
            return FinanceConventions.CashflowOut;
        return string.Empty;
    }

    public static void AssignCashbookForNewEntry(
        ExpenseEntry entry,
        string category,
        string partyName,
        string expenseScope,
        string cashbookNote)
    {
        entry.CashbookNote = cashbookNote;
        if (string.Equals(category, FinanceConventions.ManagerRemittanceCategory, StringComparison.Ordinal)
            && string.Equals(partyName, FinanceConventions.ManagerFloatPartyName, StringComparison.Ordinal))
        {
            entry.CashflowDirection = FinanceConventions.CashflowIn;
            entry.CashbookType = FinanceConventions.CashbookManagerFloatIn;
            return;
        }

        if (string.Equals(category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
        {
            entry.CashflowDirection = FinanceConventions.CashflowOut;
            entry.CashbookType = FinanceConventions.CashbookOwnerDraw;
            return;
        }

        if (string.Equals(expenseScope, "Factory", StringComparison.Ordinal))
        {
            entry.CashflowDirection = FinanceConventions.CashflowOut;
            entry.CashbookType = IsFactoryOperatingCategory(category)
                ? FinanceConventions.CashbookFactorySpend
                : FinanceConventions.CashbookOtherOutflow;
            return;
        }

        if (string.Equals(expenseScope, PrintFactoryConventions.ExpenseScope, StringComparison.Ordinal))
        {
            entry.CashflowDirection = FinanceConventions.CashflowOut;
            entry.CashbookType = FinanceConventions.CashbookPrintFactorySpend;
            return;
        }

        entry.CashflowDirection = FinanceConventions.CashflowOut;
        entry.CashbookType = FinanceConventions.CashbookOtherOutflow;
    }

    public static async Task<IResult> SummaryAsync(
        BongoTexDbContext db,
        string? from,
        string? to,
        decimal? openingStated,
        decimal? closingStated,
        CancellationToken cancellationToken)
    {
        if (!TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusiveStart, out var endExclusive, out var rangeError))
            return Results.BadRequest(rangeError);

        var remittanceRows = await db.ExpenseEntries
            .Where(x => x.Category == FinanceConventions.ManagerRemittanceCategory
                        && x.PartyName == FinanceConventions.ManagerFloatPartyName
                        && x.ExpenseDateUtc >= fromUtc && x.ExpenseDateUtc < endExclusive)
            .OrderByDescending(x => x.ExpenseDateUtc)
            .ToListAsync(cancellationToken);

        var totalRemittances = remittanceRows.Sum(x => x.Amount);
        var siteIds = remittanceRows.Select(x => x.SiteId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var siteLookup = siteIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Sites.Where(x => siteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => $"{x.Code} - {x.Name}", cancellationToken);

        var remittancesBySite = remittanceRows
            .GroupBy(x => x.SiteId)
            .Select(g => new
            {
                SiteId = g.Key,
                SiteLabel = g.Key.HasValue && siteLookup.TryGetValue(g.Key.Value, out var lbl) ? lbl : "",
                Amount = g.Sum(x => x.Amount)
            })
            .OrderByDescending(x => x.Amount)
            .ToList();

        var factoryRows = await ManagerCashbook.QueryCashbookOutflowRows(db.ExpenseEntries.AsNoTracking(), fromUtc, endExclusive)
            .ToListAsync(cancellationToken);

        var totalFactoryExpenses = factoryRows.Sum(x => x.Amount);
        var totalOutOwnerDraw = factoryRows
            .Where(x => string.Equals(x.Category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal))
            .Sum(x => x.Amount);
        var totalOutFactorySpend = factoryRows
            .Where(x =>
                !string.Equals(x.Category, FinanceConventions.OwnerDrawCategory, StringComparison.Ordinal)
                && IsFactoryOperatingCategory(x.Category))
            .Sum(x => x.Amount);
        var totalOutFactoryOther = totalFactoryExpenses - totalOutOwnerDraw - totalOutFactorySpend;

        var netRemittanceMinusFactory = totalRemittances - totalFactoryExpenses;
        var impliedClosingFloat = openingStated.HasValue
            ? openingStated.Value + totalRemittances - totalFactoryExpenses
            : (decimal?)null;
        var varianceVsManagerClosing = impliedClosingFloat.HasValue && closingStated.HasValue
            ? closingStated.Value - impliedClosingFloat.Value
            : (decimal?)null;

        var cashMoves = await db.CashMovements
            .Where(x => x.MovementDateUtc >= fromUtc && x.MovementDateUtc < endExclusive)
            .ToListAsync(cancellationToken);

        var cashTransferOutFromManager = cashMoves
            .Where(x => string.Equals(x.FromPool, FinanceConventions.CashPoolManager, StringComparison.Ordinal))
            .Sum(x => x.Amount);
        var cashTransferInToManager = cashMoves
            .Where(x => string.Equals(x.ToPool, FinanceConventions.CashPoolManager, StringComparison.Ordinal))
            .Sum(x => x.Amount);
        var cashTransferNetManager = cashTransferInToManager - cashTransferOutFromManager;

        var impliedClosingFloatAfterPoolTransfers = impliedClosingFloat.HasValue
            ? impliedClosingFloat.Value + cashTransferNetManager
            : (decimal?)null;
        var varianceVsManagerClosingAfterTransfers = impliedClosingFloatAfterPoolTransfers.HasValue && closingStated.HasValue
            ? closingStated.Value - impliedClosingFloatAfterPoolTransfers.Value
            : (decimal?)null;
        var netIncludingTransfersSameAsBookRow = netRemittanceMinusFactory + cashTransferNetManager;

        return Results.Ok(new
        {
            FromUtc = fromUtc,
            ToInclusiveUtc = toInclusiveStart,
            Conventions = new
            {
                FinanceConventions.ManagerRemittanceCategory,
                FinanceConventions.ManagerFloatPartyName,
                FinanceConventions.OwnerDrawCategory,
                FinanceConventions.OwnerDrawPartyName,
                FinanceConventions.CashflowIn,
                FinanceConventions.CashflowOut,
                FinanceConventions.CashbookManagerFloatIn,
                FinanceConventions.CashbookFactorySpend,
                FinanceConventions.CashbookOwnerDraw,
                FinanceConventions.CashbookOtherOutflow
            },
            TotalRemittances = totalRemittances,
            TotalInflows = totalRemittances,
            RemittancesBySite = remittancesBySite,
            TotalFactoryExpenses = totalFactoryExpenses,
            TotalOutOwnerDraw = totalOutOwnerDraw,
            TotalOutFactorySpend = totalOutFactorySpend,
            TotalOutFactoryOther = totalOutFactoryOther,
            NetRemittanceMinusFactory = netRemittanceMinusFactory,
            CashTransferOutFromManager = cashTransferOutFromManager,
            CashTransferInToManager = cashTransferInToManager,
            CashTransferNetManager = cashTransferNetManager,
            OpeningStated = openingStated,
            ImpliedClosingFloat = impliedClosingFloat,
            ImpliedClosingFloatAfterPoolTransfers = impliedClosingFloatAfterPoolTransfers,
            ClosingStated = closingStated,
            VarianceVsManagerClosing = varianceVsManagerClosing,
            VarianceVsManagerClosingAfterTransfers = varianceVsManagerClosingAfterTransfers,
            NetRemittanceMinusFactoryIncludingPoolTransfers = netIncludingTransfersSameAsBookRow,
            RemittanceLines = remittanceRows.Select(x => new
            {
                x.ExpenseNo,
                x.ExpenseDateUtc,
                x.SiteId,
                SiteLabel = x.SiteId.HasValue && siteLookup.TryGetValue(x.SiteId.Value, out var lbl) ? lbl : "",
                x.Amount,
                x.Description,
                CashflowDirection = InferCashflowDirection(x),
                CashbookType = InferCashbookType(x),
                x.CashbookNote
            })
        });
    }

    public static async Task<IResult> DetailLedgerAsync(
        BongoTexDbContext db,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        if (!TryResolveFinanceRange(from, to, out var fromUtc, out var toInclusiveStart, out var endExclusive, out var rangeError))
            return Results.BadRequest(rangeError);

        var remittanceRows = await db.ExpenseEntries
            .Where(x => x.Category == FinanceConventions.ManagerRemittanceCategory
                        && x.PartyName == FinanceConventions.ManagerFloatPartyName
                        && x.ExpenseDateUtc >= fromUtc && x.ExpenseDateUtc < endExclusive)
            .OrderBy(x => x.ExpenseDateUtc)
            .ThenBy(x => x.ExpenseNo)
            .ToListAsync(cancellationToken);

        var factoryRows = await ManagerCashbook.QueryCashbookOutflowRows(db.ExpenseEntries.AsNoTracking(), fromUtc, endExclusive)
            .OrderBy(x => x.ExpenseDateUtc)
            .ThenBy(x => x.ExpenseNo)
            .ToListAsync(cancellationToken);

        var siteIds = remittanceRows.Select(x => x.SiteId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var siteLookup = siteIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Sites.Where(x => siteIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => $"{x.Code} - {x.Name}", cancellationToken);

        var lines = new List<(ExpenseEntry e, string flow)>();
        foreach (var x in remittanceRows)
            lines.Add((x, FinanceConventions.CashflowIn));
        foreach (var x in factoryRows)
            lines.Add((x, FinanceConventions.CashflowOut));
        lines.Sort((a, b) =>
        {
            var c = a.e.ExpenseDateUtc.CompareTo(b.e.ExpenseDateUtc);
            return c != 0 ? c : string.CompareOrdinal(a.e.ExpenseNo, b.e.ExpenseNo);
        });

        var projected = lines.Select(t =>
        {
            var x = t.e;
            var siteLabel = x.SiteId.HasValue && siteLookup.TryGetValue(x.SiteId.Value, out var lbl) ? lbl : "";
            return new
            {
                x.ExpenseNo,
                x.ExpenseDateUtc,
                Direction = t.flow,
                Category = x.Category,
                x.PartyName,
                x.ExpenseScope,
                x.SiteId,
                SiteLabel = siteLabel,
                x.Amount,
                x.Description,
                CashbookType = string.IsNullOrWhiteSpace(x.CashbookType) ? InferCashbookType(x) : x.CashbookType.Trim(),
                CashflowDirection = string.IsNullOrWhiteSpace(x.CashflowDirection) ? InferCashflowDirection(x) : x.CashflowDirection.Trim(),
                x.CashbookNote
            };
        }).ToList();

        return Results.Ok(new
        {
            FromUtc = fromUtc,
            ToInclusiveUtc = toInclusiveStart,
            Lines = projected
        });
    }
}

/// <summary>
/// Canonical strings for cross-cutting finance flows. Do not change <see cref="ManagerFloatPartyName"/> or <see cref="OwnerDrawPartyName"/>
/// without migrating existing <see cref="ExpenseEntry"/> rows; reports match on exact party + category.
/// </summary>
/// <remarks>
/// FUTURE: InternalTransfer / CashMovement entity (optional-app spec) — add table CashMovements (Id, FromScope,
/// FromSiteId nullable, ToScope, ToSiteId nullable, Amount, TransferDateUtc, Note). POST records a movement without
/// double-counting in P&amp;L: either (a) exclude from expense totals and show on a separate "cash position" report, or
/// (b) pair two rows. Manager remittance today is modeled as a SalesCenter expense with category ManagerRemittance
/// so center net cash stays correct; factory bills remain Factory-scope supplier/salary expenses.
/// Owner draw uses <c>ExpenseScope=Owner</c> (recommended) so it is not grouped with factory operations; legacy rows may use Factory scope.
/// </remarks>
internal static class SupplierConventions
{
    public const string PrintInHouse = "PrintInHouse";
    public const string PrintOutside = "PrintOutside";
    public const string PrintFactory = "PrintFactory";
    public const string LegacyPrint = "Print";
    public const string PrintAllFilter = "PrintAll";

    public static readonly string[] AllCategories =
    [
        "Fabrics", "Collar", "Embroidery", PrintInHouse, PrintOutside, PrintFactory,
        "Accessories", "Sewing Less Glue", "Dyeing", "Others"
    ];

    public static readonly (string Value, string Label)[] FilterOptions =
    [
        ("", "All supplier categories"),
        ..AllCategories.Select(c => (c, DisplayLabel(c))),
        (PrintAllFilter, "All print suppliers (in-house + outside)")
    ];

    public static bool IsAllowedCategory(string? category)
    {
        var norm = NormalizeCategory(category);
        if (string.IsNullOrEmpty(norm) || norm.Equals(PrintAllFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        return AllCategories.Any(x => x.Equals(norm, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeCategory(string? category)
    {
        var c = (category ?? string.Empty).Trim();
        if (c.Equals(LegacyPrint, StringComparison.OrdinalIgnoreCase))
            return PrintOutside;
        if (c.Equals(PrintAllFilter, StringComparison.OrdinalIgnoreCase))
            return PrintAllFilter;
        if (c.Equals(PrintFactory, StringComparison.OrdinalIgnoreCase)
            || c.Equals("Print Factory", StringComparison.OrdinalIgnoreCase)
            || c.Equals("Print factory supplier", StringComparison.OrdinalIgnoreCase))
            return PrintFactory;
        return AllCategories.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    public static string DisplayLabel(string? category)
    {
        var c = (category ?? string.Empty).Trim();
        if (c.Equals(PrintInHouse, StringComparison.OrdinalIgnoreCase))
            return "Print — In-house (own factory)";
        if (c.Equals(PrintFactory, StringComparison.OrdinalIgnoreCase))
            return "Print Factory supplier";
        if (c.Equals(PrintOutside, StringComparison.OrdinalIgnoreCase))
            return "Print — Outside";
        if (c.Equals(LegacyPrint, StringComparison.OrdinalIgnoreCase))
            return "Print — Outside (legacy)";
        if (c.Equals(PrintAllFilter, StringComparison.OrdinalIgnoreCase))
            return "All print suppliers";
        return AllCategories.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? c;
    }

    public static bool IsPrintFactorySupplier(string? category) =>
        (category ?? string.Empty).Trim().Equals(PrintFactory, StringComparison.OrdinalIgnoreCase);

    public static bool IsPrintCategory(string? category)
    {
        var c = (category ?? string.Empty).Trim();
        return c.Equals(PrintInHouse, StringComparison.OrdinalIgnoreCase)
            || c.Equals(PrintOutside, StringComparison.OrdinalIgnoreCase)
            || c.Equals(LegacyPrint, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInHousePrint(string? category) =>
        (category ?? string.Empty).Trim().Equals(PrintInHouse, StringComparison.OrdinalIgnoreCase);

    public static bool IsOutsidePrint(string? category)
    {
        var c = (category ?? string.Empty).Trim();
        return c.Equals(PrintOutside, StringComparison.OrdinalIgnoreCase)
            || c.Equals(LegacyPrint, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CategoryMatchesFilter(string? supplierCategory, string? filterCategory)
    {
        if (string.IsNullOrWhiteSpace(filterCategory))
            return true;
        var f = filterCategory.Trim();
        if (f.Equals(PrintAllFilter, StringComparison.OrdinalIgnoreCase))
            return IsPrintCategory(supplierCategory);
        var norm = NormalizeCategory(supplierCategory);
        var normFilter = NormalizeCategory(f);
        if (normFilter == PrintAllFilter)
            return IsPrintCategory(supplierCategory);
        return !string.IsNullOrEmpty(normFilter)
            && norm.Equals(normFilter, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class FinanceConventions
{
    /// <summary>Unique expense reference (ms timestamp + random suffix; avoids IX_ExpenseEntries_ExpenseNo clashes).</summary>
    public static string NewExpenseNo() =>
        $"EX-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Random.Shared.Next(100, 999)}";

    public const string ManagerRemittanceCategory = "ManagerRemittance";

    public const string OwnerDrawCategory = "OwnerDraw";

    /// <summary>ASCII hyphen only — stable for matching in reports.</summary>
    public const string ManagerFloatPartyName = "Manager - factory float";

    /// <summary>Canonical party for owner payouts from the manager float.</summary>
    public const string OwnerDrawPartyName = "Owner - draw from manager";

    public const string CashflowIn = "In";

    public const string CashflowOut = "Out";

    public const string CashbookManagerFloatIn = "ManagerFloatIn";

    public const string CashbookFactorySpend = "FactorySpend";

    public const string CashbookOwnerDraw = "OwnerDraw";

    public const string CashbookOtherOutflow = "OtherOutflow";

    /// <summary>Physical cash pool keys for <see cref="CashMovement"/>.</summary>
    public const string CashPoolSalesCenter = "SalesCenter";

    public const string CashPoolManager = "Manager";

    public const string CashPoolFactoryPetty = "FactoryPetty";

    public const string CashbookPrintFactorySpend = "PrintFactorySpend";

    public static bool IsKnownCashPool(string? pool) =>
        string.Equals(pool, CashPoolSalesCenter, StringComparison.Ordinal)
        || string.Equals(pool, CashPoolManager, StringComparison.Ordinal)
        || string.Equals(pool, CashPoolFactoryPetty, StringComparison.Ordinal);
}

internal static class SalesSiteRules
{
    public static bool AllowsStockSale(Site? site) =>
        site is not null && (
            string.Equals(site.Type, "SalesCenter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(site.Type, "Factory", StringComparison.OrdinalIgnoreCase));

    public static string LocationLabel(Site site) =>
        string.Equals(site.Type, "Factory", StringComparison.OrdinalIgnoreCase) ? "factory" : "sales center";
}

internal static class SaleErrors
{
    public static IResult Bad(string message) =>
        TypedResults.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
}

internal static class PayrollSalaryOps
{
    public static bool PartyNameMatches(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool MatchesSalaryMonth(ExpenseEntry x, string monthKey, DateTime monthStartUtc, DateTime monthEndUtc)
    {
        var m = (x.SalaryForMonth ?? string.Empty).Trim();
        if (m == monthKey) return true;
        if (string.IsNullOrEmpty(m) && x.ExpenseDateUtc >= monthStartUtc && x.ExpenseDateUtc < monthEndUtc)
            return true;
        return false;
    }

    public static (decimal Advance, decimal Current, decimal Due) SummarizeSalaryPayments(IEnumerable<ExpenseEntry> rows)
    {
        var list = rows as IList<ExpenseEntry> ?? rows.ToList();
        var advance = list.Where(x => x.SalaryPaymentType == "Advance").Sum(x => x.Amount);
        var due = list.Where(x => x.SalaryPaymentType == "Due").Sum(x => x.Amount);
        var current = list.Where(x =>
                x.SalaryPaymentType == "Current"
                || x.SalaryPaymentType == ""
                || x.SalaryPaymentType == null)
            .Sum(x => x.Amount);
        return (advance, current, due);
    }

    public static async Task<Employee?> FindEmployeeByPartyNameAsync(BongoTexDbContext db, string partyName)
    {
        var key = (partyName ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        var employees = await db.Employees.AsNoTracking().ToListAsync();
        return employees.FirstOrDefault(e => PartyNameMatches(e.Name, key));
    }

    public static async Task<List<ExpenseEntry>> LoadSalaryExpensesForPartyAsync(
        BongoTexDbContext db,
        string partyName,
        string monthKey,
        string expenseScope,
        Guid? siteId)
    {
        if (!PayrollFormulas.TryGetMonthStart(monthKey, out var monthStartUtc))
            return new List<ExpenseEntry>();
        var monthEndUtc = monthStartUtc.AddMonths(1);

        var query = db.ExpenseEntries.AsNoTracking()
            .Where(x => x.Category == "Salary" && x.ExpenseScope == expenseScope);
        if (expenseScope == "SalesCenter" && siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId);

        var all = await query.ToListAsync();
        return all
            .Where(x => PartyNameMatches(x.PartyName, partyName)
                        && MatchesSalaryMonth(x, monthKey, monthStartUtc, monthEndUtc))
            .ToList();
    }

    public static async Task<Dictionary<string, (decimal Advance, decimal Current, decimal Due)>> LoadPaidByPartyNameAsync(
        BongoTexDbContext db,
        string expenseScope,
        string monthKey,
        DateTime monthStartUtc,
        DateTime monthEndUtc)
    {
        var all = await db.ExpenseEntries.AsNoTracking()
            .Where(x => x.Category == "Salary" && x.ExpenseScope == expenseScope)
            .ToListAsync();
        return all
            .Where(x => MatchesSalaryMonth(x, monthKey, monthStartUtc, monthEndUtc))
            .GroupBy(x => x.PartyName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => SummarizeSalaryPayments(g),
                StringComparer.OrdinalIgnoreCase);
    }

    public static string? ResolveSalaryMonthKey(ExpenseEntry entry)
    {
        var m = (entry.SalaryForMonth ?? "").Trim();
        if (Regex.IsMatch(m, @"^\d{4}-(0[1-9]|1[0-2])$")) return m;
        var d = entry.ExpenseDateUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(entry.ExpenseDateUtc, DateTimeKind.Utc)
            : entry.ExpenseDateUtc.ToUniversalTime();
        return $"{d.Year:0000}-{d.Month:00}";
    }

    public static async Task<string?> ValidateSalaryPaymentAmountAsync(
        BongoTexDbContext db,
        Employee employee,
        string salaryForMonth,
        string salaryPaymentType,
        decimal amount,
        Guid? excludeExpenseId)
    {
        if (amount <= 0) return "Amount must be greater than zero.";
        var allowed = new[] { "Advance", "Due", "Current" };
        if (!allowed.Contains(salaryPaymentType))
            return "Salary payment type must be Advance, Due, or Current.";
        if (salaryPaymentType == "Due") return null;

        var payrollCap = await (
            from l in db.PayrollLines
            join r in db.PayrollRuns on l.PayrollRunId equals r.Id
            where r.MonthKey == salaryForMonth && l.EmployeeId == employee.Id
            orderby r.CreatedAtUtc descending
            select new { l.AttendanceSalaryAmount, l.OvertimeAmount, l.AttendanceBonus, l.SnakesPay }).FirstOrDefaultAsync();
        var grossCap = payrollCap != null
            ? payrollCap.AttendanceSalaryAmount + payrollCap.OvertimeAmount + payrollCap.AttendanceBonus + payrollCap.SnakesPay
            : employee.MonthlySalary;
        if (grossCap <= 0) grossCap = employee.MonthlySalary;

        var yearMonthStartUtc = DateTime.ParseExact(
            salaryForMonth + "-01",
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var nextMonthStartUtc = yearMonthStartUtc.AddMonths(1);

        var query = db.ExpenseEntries.Where(x => x.Category == "Salary"
            && x.PartyName == employee.Name
            && (
                (x.SalaryForMonth == salaryForMonth
                    && (x.SalaryPaymentType == "Advance" || x.SalaryPaymentType == "Current"))
                || ((x.SalaryForMonth == null || x.SalaryForMonth == "")
                    && x.ExpenseDateUtc >= yearMonthStartUtc
                    && x.ExpenseDateUtc < nextMonthStartUtc
                    && (x.SalaryPaymentType == "Advance" || x.SalaryPaymentType == "Current"
                        || x.SalaryPaymentType == null || x.SalaryPaymentType == ""))));
        if (excludeExpenseId.HasValue && excludeExpenseId.Value != Guid.Empty)
            query = query.Where(x => x.Id != excludeExpenseId.Value);

        var alreadyAdvanceOrCurrent = await query.SumAsync(x => (decimal?)x.Amount) ?? 0m;
        var remaining = grossCap - alreadyAdvanceOrCurrent;

        if (salaryPaymentType == "Current")
        {
            if (remaining <= 0)
                return $"No current salary remaining for {employee.Name} in {salaryForMonth}. Gross cap {grossCap:0.##} already reached.";
            if (amount > remaining)
                return $"Current salary exceeds remaining {remaining:0.##} (gross cap {grossCap:0.##}).";
        }

        if (salaryPaymentType == "Advance" && amount > remaining)
            return $"Advance exceeds remaining payable {Math.Max(0, remaining):0.##} (gross cap {grossCap:0.##}).";

        return null;
    }

    public static async Task<PayrollLine?> SyncPayrollLineFromSalaryExpensesAsync(
        BongoTexDbContext db,
        string partyName,
        string monthKey,
        string expenseScope,
        Guid? siteId)
    {
        var run = await db.PayrollRuns
            .Where(x => x.MonthKey == monthKey && x.ExpenseScope == expenseScope
                && x.SiteId == (expenseScope == "SalesCenter" ? siteId : null))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (run is null) return null;

        var employee = await FindEmployeeByPartyNameAsync(db, partyName);
        if (employee is null) return null;

        var line = await db.PayrollLines.FirstOrDefaultAsync(x => x.PayrollRunId == run.Id && x.EmployeeId == employee.Id);
        if (line is null) return null;

        var rows = await LoadSalaryExpensesForPartyAsync(db, employee.Name, monthKey, expenseScope, siteId);
        var paid = SummarizeSalaryPayments(rows);
        line.AdvancePaid = paid.Advance;
        line.CurrentPaid = paid.Current;
        line.DuePaid = paid.Due;
        PayrollFormulas.RecalculateLine(line, run.MonthKey, run.ExpenseScope);
        return line;
    }
}

internal static class PayrollSalaryCashOps
{
    public static bool TryGetSalaryCashPool(ExpenseEntry entry, out string pool, out Guid? siteId, out string poolLabel)
    {
        pool = "";
        siteId = null;
        poolLabel = "";
        if (ManagerCashbook.UsesFactoryPettyCash(entry.ExpenseScope, entry.Category))
        {
            pool = FinanceConventions.CashPoolFactoryPetty;
            poolLabel = "Factory petty";
            return true;
        }
        if (string.Equals(entry.ExpenseScope, "SalesCenter", StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Category, "Salary", StringComparison.OrdinalIgnoreCase))
        {
            pool = FinanceConventions.CashPoolSalesCenter;
            siteId = entry.SiteId;
            poolLabel = "Sales center drawer";
            return true;
        }
        return false;
    }

    public static async Task<object> BuildCashReturnPayloadAsync(
        BongoTexDbContext db,
        ExpenseEntry entry,
        decimal cashReturned,
        CancellationToken cancellationToken = default)
    {
        if (cashReturned <= 0.0001m)
            return new { CashReturned = 0m };

        if (!TryGetSalaryCashPool(entry, out var pool, out var siteId, out var poolLabel))
            return new { CashReturned = cashReturned, CashPool = "", PoolLabel = "", PoolBalanceAfter = (decimal?)null };

        var utc = DateTime.UtcNow;
        var asOfExclusive = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
        var wallets = await DailyCashBalanceReport.ComputeWalletBalancesAsOfAsync(
            db, asOfExclusive, 0m, 0m, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase), cancellationToken);

        decimal balanceAfter = pool switch
        {
            _ when string.Equals(pool, FinanceConventions.CashPoolFactoryPetty, StringComparison.Ordinal)
                => wallets.FactoryPettyCashBalance,
            _ when string.Equals(pool, FinanceConventions.CashPoolSalesCenter, StringComparison.Ordinal) && siteId.HasValue
                => wallets.SalesCenters.FirstOrDefault(s => s.SiteId == siteId.Value)?.Balance ?? 0m,
            _ => 0m
        };

        return new
        {
            CashReturned = cashReturned,
            CashPool = pool,
            PoolLabel = poolLabel,
            PoolBalanceAfter = balanceAfter,
            Message = $"{cashReturned:0.00} returned to {poolLabel} (on hand now {balanceAfter:0.00})."
        };
    }
}

internal static class PayrollScopeOps
{
    public static bool IsValid(string? scope) =>
        string.Equals(scope?.Trim(), "Factory", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope?.Trim(), "SalesCenter", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope?.Trim(), PrintFactoryConventions.ExpenseScope, StringComparison.OrdinalIgnoreCase);

    public static bool IsFactoryStyle(string? scope) =>
        string.Equals(scope?.Trim(), "Factory", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope?.Trim(), PrintFactoryConventions.ExpenseScope, StringComparison.OrdinalIgnoreCase);

    public static string ScopeForEmployee(Employee e) =>
        string.Equals(e.EmployeeType, "PrintFactory", StringComparison.OrdinalIgnoreCase)
            ? PrintFactoryConventions.ExpenseScope
            : "Factory";

    public static async Task<PayrollLine?> FindPayrollLineAsync(
        BongoTexDbContext db,
        Guid payrollLineId,
        Guid? employeeId,
        string? monthKey,
        string? expenseScope,
        Guid? siteId)
    {
        if (payrollLineId != Guid.Empty)
        {
            var byId = await db.PayrollLines.FirstOrDefaultAsync(x => x.Id == payrollLineId);
            if (byId is not null) return byId;
        }

        if (!employeeId.HasValue || employeeId.Value == Guid.Empty) return null;
        var month = (monthKey ?? string.Empty).Trim();
        if (!Regex.IsMatch(month, @"^\d{4}-(0[1-9]|1[0-2])$")) return null;
        var scope = (expenseScope ?? "Factory").Trim();
        if (!IsValid(scope)) return null;
        if (scope == "SalesCenter" && (!siteId.HasValue || siteId.Value == Guid.Empty)) return null;

        var run = await db.PayrollRuns
            .Where(x => x.MonthKey == month && x.ExpenseScope == scope && x.SiteId == (scope == "SalesCenter" ? siteId : null))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (run is null) return null;
        return await db.PayrollLines.FirstOrDefaultAsync(x => x.PayrollRunId == run.Id && x.EmployeeId == employeeId.Value);
    }
}

internal static class PayrollFormulas
{
    public static bool TryGetMonthStart(string monthKey, out DateTime monthStart)
    {
        return DateTime.TryParseExact(
            monthKey + "-01",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out monthStart);
    }

    /// <summary>Attendance salary = (monthly salary / calendar days in month) * attendance days.</summary>
    public static decimal ComputeAttendanceSalaryAmount(decimal monthlySalary, string monthKey, decimal attendanceDays)
    {
        if (monthlySalary <= 0 || !TryGetMonthStart(monthKey, out var monthStart))
            return 0;
        var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        if (days <= 0 || attendanceDays < 0) return 0;
        if (attendanceDays == 0) return 0;
        var effectiveDays = Math.Min(attendanceDays, days);
        return decimal.Round(monthlySalary / days * effectiveDays, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Overtime = (monthly salary / calendar days in month / 12 hours) * overtime hours.</summary>
    public static decimal ComputeOvertimeAmount(decimal monthlySalary, string monthKey, decimal overtimeHours)
    {
        if (overtimeHours <= 0 || monthlySalary <= 0) return 0;
        if (!TryGetMonthStart(monthKey, out var monthStart))
            return 0;
        var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        if (days <= 0) return 0;
        var perHour = monthlySalary / days / 12m;
        return decimal.Round(perHour * overtimeHours, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>True when attendance covers every calendar day in the month (same notion as capped attendance salary).</summary>
    public static bool IsFullCalendarMonthAttendance(string monthKey, decimal attendanceDays)
    {
        if (!TryGetMonthStart(monthKey, out var monthStart))
            return false;
        var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        if (days <= 0)
            return false;
        return attendanceDays >= days;
    }

    /// <summary>One calendar day's salary when attendance covers the full month — Operator and Helper only.</summary>
    public static bool IsAttendanceBonusEligible(string? employeeCategory)
    {
        var cat = (employeeCategory ?? string.Empty).Trim();
        return cat.Equals("Operator", StringComparison.OrdinalIgnoreCase)
            || cat.Equals("Helper", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolvePayrollEmployeeCategory(Employee e)
    {
        var cat = (e.EmployeeCategory ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(cat))
            return cat;
        var typ = (e.EmployeeType ?? string.Empty).Trim();
        if (typ.Equals("Operator", StringComparison.OrdinalIgnoreCase)
            || typ.Equals("Helper", StringComparison.OrdinalIgnoreCase))
            return typ;
        return string.Empty;
    }

    /// <summary>Helper → Operator → other, then monthly salary high to low within each group.</summary>
    public static int PayrollDisplaySortKey(string? employeeCategory)
    {
        var cat = (employeeCategory ?? string.Empty).Trim();
        if (cat.Equals("Helper", StringComparison.OrdinalIgnoreCase)) return 0;
        if (cat.Equals("Operator", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    public static IEnumerable<T> OrderPayrollRowsForDisplay<T>(
        IEnumerable<T> rows,
        Func<T, string?> categorySelector,
        Func<T, decimal> salarySelector,
        Func<T, string?> nameSelector) =>
        rows.OrderBy(r => PayrollDisplaySortKey(categorySelector(r)))
            .ThenByDescending(salarySelector)
            .ThenBy(r => nameSelector(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<T> OrderByEmployeeSerial<T>(
        IEnumerable<T> rows,
        Func<T, int> serialSelector,
        Func<T, string?> nameSelector) =>
        rows.OrderBy(r =>
        {
            var s = serialSelector(r);
            return s <= 0 ? int.MaxValue : s;
        }).ThenBy(r => nameSelector(r) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>One calendar day's salary when attendance covers the full month (same day-rate as attendance salary).</summary>
    public static decimal ComputeAttendanceBonus(decimal monthlySalary, string monthKey, decimal attendanceDays)
    {
        if (monthlySalary <= 0 || !IsFullCalendarMonthAttendance(monthKey, attendanceDays))
            return 0;
        if (!TryGetMonthStart(monthKey, out var monthStart))
            return 0;
        var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        if (days <= 0)
            return 0;
        return decimal.Round(monthlySalary / days, 2, MidpointRounding.AwayFromZero);
    }

    public static void RecalculateLine(PayrollLine line, string monthKey, string expenseScope)
    {
        var isFactory = PayrollScopeOps.IsFactoryStyle(expenseScope);

        decimal newAttSalary;
        decimal newOtAmt;
        decimal newBonus;
        decimal newSnakes;

        if (!isFactory)
        {
            newAttSalary = line.MonthlySalary;
            newOtAmt = 0;
            newBonus = 0;
            newSnakes = 0;
        }
        else
        {
            newAttSalary = ComputeAttendanceSalaryAmount(line.MonthlySalary, monthKey, line.AttendanceDays);
            newOtAmt = ComputeOvertimeAmount(line.MonthlySalary, monthKey, line.OvertimeHours);
            newBonus = IsAttendanceBonusEligible(line.EmployeeCategory)
                ? ComputeAttendanceBonus(line.MonthlySalary, monthKey, line.AttendanceDays)
                : 0;
            newSnakes = Math.Max(0, line.SnakesPay);
        }

        var gross = newAttSalary + newOtAmt + newBonus + newSnakes;
        var newNet = Math.Max(0, gross - (line.AdvancePaid + line.CurrentPaid));
        var newStatus = newNet <= 0 ? "Paid" : ((line.AdvancePaid + line.CurrentPaid) > 0 ? "Partial" : "Unpaid");

        var dirty = false;
        if (!isFactory)
        {
            if (line.OvertimeHours != 0) { line.OvertimeHours = 0; dirty = true; }
            if (line.OvertimeAmount != 0) { line.OvertimeAmount = 0; dirty = true; }
            if (line.AttendanceDays != 0) { line.AttendanceDays = 0; dirty = true; }
            if (line.AttendanceSalaryAmount != newAttSalary) { line.AttendanceSalaryAmount = newAttSalary; dirty = true; }
            if (line.AttendanceBonus != 0) { line.AttendanceBonus = 0; dirty = true; }
            if (line.SnakesPay != 0) { line.SnakesPay = 0; dirty = true; }
        }
        else
        {
            if (line.AttendanceSalaryAmount != newAttSalary) { line.AttendanceSalaryAmount = newAttSalary; dirty = true; }
            if (line.OvertimeAmount != newOtAmt) { line.OvertimeAmount = newOtAmt; dirty = true; }
            if (line.AttendanceBonus != newBonus) { line.AttendanceBonus = newBonus; dirty = true; }
            if (line.SnakesPay != newSnakes) { line.SnakesPay = newSnakes; dirty = true; }
        }

        if (line.NetPayable != newNet) { line.NetPayable = newNet; dirty = true; }
        if (line.Status != newStatus) { line.Status = newStatus; dirty = true; }
        if (dirty)
            line.UpdatedAtUtc = DateTime.UtcNow;
    }
}

namespace BongoTex.Api
{
internal static class PipelineEntryValidation
{
    public static DateTime UtcCalendarDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).Date;

    /// <summary>Exclusive UTC end of the calendar day that contains <paramref name="utcInstant"/> (same rule as daily-wip).</summary>
    public static DateTime UtcDayEndExclusive(DateTime utcInstant)
    {
        var u = utcInstant.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc)
            : utcInstant.ToUniversalTime();
        return u.Date.AddDays(1);
    }

    /// <summary>
    /// Trims user input; if not empty and it does not already start with a 4-digit year and hyphen, prefixes with the UTC calendar year of <paramref name="referenceUtc"/>.
    /// Example: " 1 " with reference 2026-05-13 UTC becomes "2026-1".
    /// </summary>
    public static string NormalizePipelineCutLot(string? rawLot, DateTime referenceUtc)
    {
        var trimmed = (rawLot ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "";
        if (Regex.IsMatch(trimmed, @"^\d{4}-", RegexOptions.CultureInvariant))
            return trimmed;
        var utc = referenceUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(referenceUtc, DateTimeKind.Utc)
            : referenceUtc.ToUniversalTime();
        return $"{utc.Year}-{trimmed}";
    }

    public static async Task<bool> SewingFitsCutThroughSewDayAsync(
        BongoTexDbContext db,
        Guid inventoryItemId,
        DateTime sewnAtUtc,
        int proposedSewnQty,
        Guid? excludeSewingId,
        CancellationToken ct = default)
    {
        var dayEnd = UtcDayEndExclusive(sewnAtUtc);
        var cutSum = await db.CuttingEntries.AsNoTracking()
            .Where(c => c.InventoryItemId == inventoryItemId && c.CutAtUtc < dayEnd)
            .SumAsync(c => (long)c.QuantityCut, ct);

        var sewSum = await db.SewingEntries.AsNoTracking()
            .Where(s => s.InventoryItemId == inventoryItemId && s.SewnAtUtc < dayEnd
                        && (!excludeSewingId.HasValue || s.Id != excludeSewingId.Value))
            .SumAsync(s => (long)s.QuantitySewn, ct);

        return sewSum + proposedSewnQty <= cutSum;
    }

    /// <summary>When style (inventory item) is not known yet, cap sewing by factory + cutting lot through the sew UTC day.</summary>
    public static async Task<bool> SewingFitsCutThroughSewDayForLotAsync(
        BongoTexDbContext db,
        Guid factorySiteId,
        string cutLotNorm,
        DateTime sewnAtUtc,
        int proposedSewnQty,
        Guid? excludeSewingId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cutLotNorm)) return false;
        var dayEnd = UtcDayEndExclusive(sewnAtUtc);
        var cutSum = await db.CuttingEntries.AsNoTracking()
            .Where(c => c.FactorySiteId == factorySiteId && c.CutLotCode == cutLotNorm && c.CutAtUtc < dayEnd)
            .SumAsync(c => (long)c.QuantityCut, ct);

        var sewSum = await db.SewingEntries.AsNoTracking()
            .Where(s => s.FactorySiteId == factorySiteId && s.CutLotCode == cutLotNorm && s.SewnAtUtc < dayEnd
                        && (!excludeSewingId.HasValue || s.Id != excludeSewingId.Value))
            .SumAsync(s => (long)s.QuantitySewn, ct);

        return sewSum + proposedSewnQty <= cutSum;
    }

    public static async Task<string?> SewingExceedsCutAfterCutChangeAsync(
        BongoTexDbContext db,
        Guid? inventoryItemId,
        Guid factorySiteId,
        string cutLotCode,
        Guid? cutRowId,
        int? replacementCutQty,
        DateTime? replacementCutAtUtc,
        bool removeCutRow,
        CancellationToken ct = default)
    {
        var lot = (cutLotCode ?? "").Trim();
        if (inventoryItemId.HasValue && inventoryItemId.Value != Guid.Empty)
        {
            var itemId = inventoryItemId.Value;
            var sewTimes = await db.SewingEntries.AsNoTracking()
                .Where(s => s.InventoryItemId == itemId)
                .Select(s => s.SewnAtUtc)
                .ToListAsync(ct);

            var cutRows = await db.CuttingEntries.AsNoTracking()
                .Where(c => c.InventoryItemId == itemId)
                .ToListAsync(ct);

            List<(DateTime CutAtUtc, int QuantityCut)> effectiveCuts;
            if (removeCutRow && cutRowId.HasValue)
            {
                effectiveCuts = cutRows
                    .Where(c => c.Id != cutRowId.Value)
                    .Select(c => (c.CutAtUtc, c.QuantityCut))
                    .ToList();
            }
            else if (cutRowId.HasValue && replacementCutQty.HasValue)
            {
                var rid = cutRowId.Value;
                var qty = replacementCutQty.Value;
                var at = replacementCutAtUtc
                         ?? cutRows.FirstOrDefault(c => c.Id == rid)?.CutAtUtc
                         ?? DateTime.UtcNow;
                effectiveCuts = cutRows
                    .Where(c => c.Id != rid)
                    .Select(c => (c.CutAtUtc, c.QuantityCut))
                    .Append((at, qty))
                    .ToList();
            }
            else
            {
                effectiveCuts = cutRows.Select(c => (c.CutAtUtc, c.QuantityCut)).ToList();
            }

            foreach (var day in sewTimes.Select(UtcCalendarDate).Distinct())
            {
                var dayEnd = day.AddDays(1);
                var cutSum = effectiveCuts.Where(c => c.CutAtUtc < dayEnd).Sum(c => (long)c.QuantityCut);
                var sewSum = await db.SewingEntries.AsNoTracking()
                    .Where(s => s.InventoryItemId == itemId && s.SewnAtUtc < dayEnd)
                    .SumAsync(s => (long)s.QuantitySewn, ct);

                if (sewSum > cutSum)
                {
                    return $"Through {day:yyyy-MM-dd} UTC, sewn total is {sewSum} pcs but cut total is only {cutSum} pcs. " +
                           "Increase cutting or reduce/delete sewing entries for this style before lowering cuts.";
                }
            }

            return null;
        }

        if (string.IsNullOrEmpty(lot))
            return "Cutting number (lot) is missing on this cutting row; cannot validate sewing totals.";

        var sewTimesLot = await db.SewingEntries.AsNoTracking()
            .Where(s => s.FactorySiteId == factorySiteId && s.CutLotCode == lot)
            .Select(s => s.SewnAtUtc)
            .ToListAsync(ct);

        var cutRowsLot = await db.CuttingEntries.AsNoTracking()
            .Where(c => c.FactorySiteId == factorySiteId && c.CutLotCode == lot)
            .ToListAsync(ct);

        List<(DateTime CutAtUtc, int QuantityCut)> effectiveCutsLot;
        if (removeCutRow && cutRowId.HasValue)
        {
            effectiveCutsLot = cutRowsLot
                .Where(c => c.Id != cutRowId.Value)
                .Select(c => (c.CutAtUtc, c.QuantityCut))
                .ToList();
        }
        else if (cutRowId.HasValue && replacementCutQty.HasValue)
        {
            var rid = cutRowId.Value;
            var qty = replacementCutQty.Value;
            var at = replacementCutAtUtc
                     ?? cutRowsLot.FirstOrDefault(c => c.Id == rid)?.CutAtUtc
                     ?? DateTime.UtcNow;
            effectiveCutsLot = cutRowsLot
                .Where(c => c.Id != rid)
                .Select(c => (c.CutAtUtc, c.QuantityCut))
                .Append((at, qty))
                .ToList();
        }
        else
        {
            effectiveCutsLot = cutRowsLot.Select(c => (c.CutAtUtc, c.QuantityCut)).ToList();
        }

        foreach (var day in sewTimesLot.Select(UtcCalendarDate).Distinct())
        {
            var dayEnd = day.AddDays(1);
            var cutSum = effectiveCutsLot.Where(c => c.CutAtUtc < dayEnd).Sum(c => (long)c.QuantityCut);
            var sewSum = await db.SewingEntries.AsNoTracking()
                .Where(s => s.FactorySiteId == factorySiteId && s.CutLotCode == lot && s.SewnAtUtc < dayEnd)
                .SumAsync(s => (long)s.QuantitySewn, ct);

            if (sewSum > cutSum)
            {
                return $"Through {day:yyyy-MM-dd} UTC, sewn total is {sewSum} pcs but cut total is only {cutSum} pcs for this cutting lot. " +
                       "Increase cutting or reduce/delete sewing entries for this lot before lowering cuts.";
            }
        }

        return null;
    }

    /// <summary>
    /// When <paramref name="factorySiteId"/> and <paramref name="cutLotNorm"/> are set, sewn totals include rows for that lot with no item yet,
    /// and finished totals are scoped to the same factory and lot. Otherwise legacy global-per-item behaviour (no factory filter).
    /// </summary>
    public static async Task<bool> FinishingFitsSewThroughFinishDayAsync(
        BongoTexDbContext db,
        Guid inventoryItemId,
        DateTime finishedAtUtc,
        int proposedFinishedQty,
        Guid? excludeFinishingId,
        Guid? factorySiteId = null,
        string? cutLotNorm = null,
        CancellationToken ct = default)
    {
        var dayEnd = UtcDayEndExclusive(finishedAtUtc);
        var lot = (cutLotNorm ?? "").Trim();
        var useFactoryLot = factorySiteId.HasValue && factorySiteId.Value != Guid.Empty && !string.IsNullOrEmpty(lot);

        long sewSum;
        if (!useFactoryLot)
        {
            sewSum = await db.SewingEntries.AsNoTracking()
                .Where(s => s.InventoryItemId == inventoryItemId && s.SewnAtUtc < dayEnd)
                .SumAsync(s => (long)s.QuantitySewn, ct);
        }
        else
        {
            var fid = factorySiteId!.Value;
            sewSum = await db.SewingEntries.AsNoTracking()
                .Where(s => s.FactorySiteId == fid && s.SewnAtUtc < dayEnd
                    && (s.InventoryItemId == inventoryItemId
                        || (s.InventoryItemId == null && s.CutLotCode == lot)))
                .SumAsync(s => (long)s.QuantitySewn, ct);
        }

        long finSum;
        if (!useFactoryLot)
        {
            finSum = await db.FinishingEntries.AsNoTracking()
                .Where(f => f.InventoryItemId == inventoryItemId && f.FinishedAtUtc < dayEnd
                            && (!excludeFinishingId.HasValue || f.Id != excludeFinishingId.Value))
                .SumAsync(f => (long)f.QuantityFinished, ct);
        }
        else
        {
            var fid = factorySiteId!.Value;
            finSum = await db.FinishingEntries.AsNoTracking()
                .Where(f => f.FactorySiteId == fid && f.InventoryItemId == inventoryItemId && f.FinishedAtUtc < dayEnd
                            && f.CutLotCode == lot
                            && (!excludeFinishingId.HasValue || f.Id != excludeFinishingId.Value))
                .SumAsync(f => (long)f.QuantityFinished, ct);
        }

        return finSum + proposedFinishedQty <= sewSum;
    }

    /// <summary>After changing or removing a sewing row, ensure finished never exceeds sewn for any UTC day that has finishing.</summary>
    public static async Task<string?> FinishingExceedsSewAfterSewChangeAsync(
        BongoTexDbContext db,
        Guid? inventoryItemId,
        Guid factorySiteId,
        string cutLotCode,
        Guid? sewRowId,
        int? replacementSewQty,
        DateTime? replacementSewAtUtc,
        bool removeSewRow,
        CancellationToken ct = default)
    {
        var lot = (cutLotCode ?? "").Trim();
        if (inventoryItemId.HasValue && inventoryItemId.Value != Guid.Empty)
        {
            var itemId = inventoryItemId.Value;
            var finTimes = await db.FinishingEntries.AsNoTracking()
                .Where(f => f.InventoryItemId == itemId)
                .Select(f => f.FinishedAtUtc)
                .ToListAsync(ct);

            var sewRows = await db.SewingEntries.AsNoTracking()
                .Where(s => s.InventoryItemId == itemId)
                .ToListAsync(ct);

            List<(DateTime SewnAtUtc, int QuantitySewn)> effectiveSew;
            if (removeSewRow && sewRowId.HasValue)
            {
                effectiveSew = sewRows
                    .Where(s => s.Id != sewRowId.Value)
                    .Select(s => (s.SewnAtUtc, s.QuantitySewn))
                    .ToList();
            }
            else if (sewRowId.HasValue && replacementSewQty.HasValue)
            {
                var rid = sewRowId.Value;
                var qty = replacementSewQty.Value;
                var at = replacementSewAtUtc
                         ?? sewRows.FirstOrDefault(s => s.Id == rid)?.SewnAtUtc
                         ?? DateTime.UtcNow;
                effectiveSew = sewRows
                    .Where(s => s.Id != rid)
                    .Select(s => (s.SewnAtUtc, s.QuantitySewn))
                    .Append((at, qty))
                    .ToList();
            }
            else
            {
                effectiveSew = sewRows.Select(s => (s.SewnAtUtc, s.QuantitySewn)).ToList();
            }

            foreach (var day in finTimes.Select(UtcCalendarDate).Distinct())
            {
                var dayEnd = day.AddDays(1);
                var sewSum = effectiveSew.Where(s => s.SewnAtUtc < dayEnd).Sum(s => (long)s.QuantitySewn);
                var finSum = await db.FinishingEntries.AsNoTracking()
                    .Where(f => f.InventoryItemId == itemId && f.FinishedAtUtc < dayEnd)
                    .SumAsync(f => (long)f.QuantityFinished, ct);

                if (finSum > sewSum)
                {
                    return $"Through {day:yyyy-MM-dd} UTC, finished total is {finSum} pcs but sewn total is only {sewSum} pcs. " +
                           "Increase sewing or reduce/delete finishing entries for this style before lowering sewing.";
                }
            }

            return null;
        }

        if (string.IsNullOrEmpty(lot))
            return "Cutting number (lot) is missing on this sewing row; cannot validate finishing totals.";

        var finTimesLot = await db.FinishingEntries.AsNoTracking()
            .Where(f => f.FactorySiteId == factorySiteId && f.CutLotCode == lot)
            .Select(f => f.FinishedAtUtc)
            .ToListAsync(ct);

        var sewRowsLot = await db.SewingEntries.AsNoTracking()
            .Where(s => s.FactorySiteId == factorySiteId && s.CutLotCode == lot)
            .ToListAsync(ct);

        List<(DateTime SewnAtUtc, int QuantitySewn)> effectiveSewLot;
        if (removeSewRow && sewRowId.HasValue)
        {
            effectiveSewLot = sewRowsLot
                .Where(s => s.Id != sewRowId.Value)
                .Select(s => (s.SewnAtUtc, s.QuantitySewn))
                .ToList();
        }
        else if (sewRowId.HasValue && replacementSewQty.HasValue)
        {
            var rid = sewRowId.Value;
            var qty = replacementSewQty.Value;
            var at = replacementSewAtUtc
                     ?? sewRowsLot.FirstOrDefault(s => s.Id == rid)?.SewnAtUtc
                     ?? DateTime.UtcNow;
            effectiveSewLot = sewRowsLot
                .Where(s => s.Id != rid)
                .Select(s => (s.SewnAtUtc, s.QuantitySewn))
                .Append((at, qty))
                .ToList();
        }
        else
        {
            effectiveSewLot = sewRowsLot.Select(s => (s.SewnAtUtc, s.QuantitySewn)).ToList();
        }

        foreach (var day in finTimesLot.Select(UtcCalendarDate).Distinct())
        {
            var dayEnd = day.AddDays(1);
            var sewSum = effectiveSewLot.Where(s => s.SewnAtUtc < dayEnd).Sum(s => (long)s.QuantitySewn);
            var finSum = await db.FinishingEntries.AsNoTracking()
                .Where(f => f.FactorySiteId == factorySiteId && f.CutLotCode == lot && f.FinishedAtUtc < dayEnd)
                .SumAsync(f => (long)f.QuantityFinished, ct);

            if (finSum > sewSum)
            {
                return $"Through {day:yyyy-MM-dd} UTC, finished total is {finSum} pcs but sewn total is only {sewSum} pcs for this cutting lot. " +
                       "Increase sewing or reduce/delete finishing entries for this lot before lowering sewing.";
            }
        }

        return null;
    }
}

internal static class FinishingInventoryBootstrap
{
    public static async Task<InventoryItem?> TryCreateItemForNewFinishingSkuAsync(BongoTexDbContext db, string key, string normalizedLot)
    {
        if (!TryParsePipelineSku(key, out var skuCanon, out var prefix))
            return null;

        var style = await db.ProductStyles.AsNoTracking().FirstOrDefaultAsync(x => x.Prefix == prefix);
        if (style is null)
            return null;

        var existing = await db.InventoryItems.FirstOrDefaultAsync(x => x.Sku.ToLower() == skuCanon.ToLower());
        if (existing is not null)
            return existing;

        var cuttingBase = string.IsNullOrEmpty(normalizedLot)
            ? $"PIPE-{skuCanon}"
            : $"{normalizedLot}::{skuCanon}";
        var cutting = TruncCuttingNumber(cuttingBase);
        for (var i = 0; i < 30; i++)
        {
            if (!await db.InventoryItems.AnyAsync(x => x.CuttingNumber == cutting))
                break;
            cutting = TruncCuttingNumber($"{cuttingBase}:{Guid.NewGuid():N}");
        }

        var item = new InventoryItem
        {
            Sku = skuCanon,
            Name = style.Name,
            CuttingNumber = cutting,
            UnitPrice = 0.01m,
            ProductionCost = style.ProductionCost,
            SalesPrice = 0.01m,
            DiscountPrice = 0m,
            ItemImageBase64 = "",
            QuantityOnHand = 0
        };
        db.InventoryItems.Add(item);
        await db.SaveChangesAsync();

        var sites = await db.Sites.ToListAsync();
        foreach (var site in sites)
        {
            if (await db.InventoryStocks.AnyAsync(x => x.InventoryItemId == item.Id && x.SiteId == site.Id))
                continue;
            db.InventoryStocks.Add(new InventoryStock
            {
                InventoryItemId = item.Id,
                SiteId = site.Id,
                Quantity = 0
            });
        }

        await db.SaveChangesAsync();
        return item;
    }

    private static bool TryParsePipelineSku(string key, out string canonicalSku, out string prefix)
    {
        canonicalSku = "";
        prefix = "";
        var t = key.Trim();
        var dash = t.IndexOf('-');
        if (dash <= 0 || dash >= t.Length - 1)
            return false;
        var p = ProductStyleOps.NormalizePrefix(t[..dash]);
        if (p is null)
            return false;
        var suffix = t[(dash + 1)..].Trim();
        if (suffix.Length == 0)
            return false;
        prefix = p;
        canonicalSku = $"{p}-{suffix}";
        return true;
    }

    private static string TruncCuttingNumber(string s) =>
        s.Length <= 80 ? s : s[..80];

    public static async Task DeleteAutoProvisionedItemAsync(BongoTexDbContext db, Guid inventoryItemId)
    {
        await db.InventoryStocks.Where(s => s.InventoryItemId == inventoryItemId).ExecuteDeleteAsync();
        await db.InventoryItems.Where(i => i.Id == inventoryItemId).ExecuteDeleteAsync();
    }
}

}

public record CreateCuttingEntryRequest(Guid FactorySiteId, Guid? InventoryItemId, Guid? RawMaterialId, string? CutLotCode, int QuantityCut, decimal FabricKg, decimal FabricPricePerKg, DateTime? CutAtUtc);
public record UpdateCuttingEntryRequest(int QuantityCut, decimal FabricKg, decimal FabricPricePerKg, DateTime? CutAtUtc, Guid? RawMaterialId);
public record CreateSewingEntryRequest(Guid FactorySiteId, Guid? InventoryItemId, string? CutLotCode, int QuantitySewn, DateTime? SewnAtUtc);
public record UpdateSewingEntryRequest(int QuantitySewn, DateTime? SewnAtUtc);
public record CreateFinishingEntryRequest(
    Guid FactorySiteId,
    string? ItemSku,
    string? CutLotCode,
    int QuantityFinished,
    DateTime? FinishedAtUtc,
    List<FinishingMaterialLineRequest>? MaterialLines);
public sealed class FinishingMaterialLineRequest
{
    public Guid RawMaterialId { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitCost { get; set; }
}
public record UpdateFinishingEntryRequest(int QuantityFinished, DateTime? FinishedAtUtc);
public record CreateProductionOrderRequest(Guid FactorySiteId, Guid InventoryItemId, int QuantityProduced, DateTime? ProducedAtUtc);
public record UpdateProductionOrderRequest(int QuantityProduced, DateTime? ProducedAtUtc);
public sealed class UpsertProductStyleRequest
{
    public string Prefix { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal ProductionCost { get; set; }
}

public sealed class CreateInventoryItemRequest
{
    public string SkuPrefix { get; set; } = string.Empty;
    /// <summary>When set (e.g. ST-009 from finishing UI), creates that exact SKU if it does not already exist. Must match <see cref="SkuPrefix"/>.</summary>
    public string? Sku { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CuttingNumber { get; set; } = string.Empty;
    public decimal? UnitPrice { get; set; }
    public decimal? ProductionCost { get; set; }
    public decimal? SalesPrice { get; set; }
    public decimal? DiscountPrice { get; set; }
    public bool? IsPrintItem { get; set; }
    public decimal? PrintChargePerPiece { get; set; }
    public string? ItemImageBase64 { get; set; }
}
public sealed class UpdateInventoryItemRequest
{
    public string Name { get; set; } = string.Empty;
    public string CuttingNumber { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal ProductionCost { get; set; }
    public decimal SalesPrice { get; set; }
    public decimal DiscountPrice { get; set; }
    public bool IsPrintItem { get; set; }
    public decimal PrintChargePerPiece { get; set; }
    /// <summary>When provided, replaces the stored image. Omit or leave empty to keep the current image.</summary>
    public string? ItemImageBase64 { get; set; }
}
public sealed class UpdateInventoryItemImageRequest
{
    public string? ItemImageBase64 { get; set; }
}

internal sealed record CustomerDueCollectionResult(
    decimal AmountReceivedApplied,
    decimal SettlementDiscountApplied,
    decimal TotalApplied,
    decimal RemainingDue);

internal sealed record CustomerDueInvoiceRow(
    string InvoiceNo,
    string CustomerName,
    decimal TotalSales,
    decimal TotalPaid,
    decimal TotalDue,
    decimal CashCollected,
    decimal SettlementDiscountApplied,
    decimal ReturnCreditApplied,
    decimal TotalDiscount,
    decimal MaxAllowedDiscount,
    DateTime SoldAtUtc,
    bool IsCredit);

internal sealed record CustomerDueTransactionRow(
    DateTime OccurredAtUtc,
    string TransactionType,
    string TypeCode,
    string InvoiceNo,
    decimal Amount,
    string Note);

internal static class CustomerDueLedgerBuilder
{
    public static string ClassifyCollectionNote(string? note)
    {
        var n = note ?? string.Empty;
        if (n.StartsWith("Return credit", StringComparison.OrdinalIgnoreCase))
            return "ReturnCredit";
        if (n.Contains("(settlement discount)", StringComparison.OrdinalIgnoreCase))
            return "SettlementDiscount";
        return "Payment";
    }

    public static string TransactionTypeLabel(string typeCode) => typeCode switch
    {
        "CreditSale" => "Credit sale",
        "Payment" => "Payment (cash)",
        "SettlementDiscount" => "Settlement discount",
        "ReturnCredit" => "Return credit",
        _ => typeCode
    };

    public static List<CustomerDueTransactionRow> BuildTransactionHistory(
        IReadOnlyList<SalesTransaction> customerTxs,
        IReadOnlyList<SalesCollection> collections)
    {
        var txById = customerTxs.ToDictionary(t => t.Id);
        var rows = new List<CustomerDueTransactionRow>();

        foreach (var g in customerTxs.GroupBy(x => (x.InvoiceNo ?? x.SalesNo).Trim()))
        {
            var lines = g.ToList();
            if (!lines.Any(x => x.IsCredit))
                continue;

            var inv = g.Key;
            var sold = lines.Min(x => x.SoldAtUtc);
            var totalSales = lines.Sum(x => x.TotalAmount);
            var totalDue = lines.Sum(x => x.DueAmount);
            rows.Add(new CustomerDueTransactionRow(
                sold,
                TransactionTypeLabel("CreditSale"),
                "CreditSale",
                inv,
                totalSales,
                $"Credit invoice posted · open due {totalDue:F2}"));
        }

        var collectionsByInvoice = collections
            .Where(c => txById.ContainsKey(c.SalesTransactionId))
            .GroupBy(c => (txById[c.SalesTransactionId].InvoiceNo ?? txById[c.SalesTransactionId].SalesNo).Trim())
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var g in customerTxs.GroupBy(x => (x.InvoiceNo ?? x.SalesNo).Trim()))
        {
            if (!g.Any(x => x.IsCredit))
                continue;

            var inv = g.Key;
            var lines = g.ToList();
            var totalPaid = lines.Sum(x => x.PaidAmount);
            var fromCollections = collectionsByInvoice.TryGetValue(inv, out var invCols)
                ? invCols.Sum(c => c.Amount)
                : 0m;
            var initialPaid = totalPaid - fromCollections;
            if (initialPaid > 0.0001m)
            {
                rows.Add(new CustomerDueTransactionRow(
                    lines.Min(x => x.SoldAtUtc),
                    "Initial payment (at sale)",
                    "Payment",
                    inv,
                    initialPaid,
                    "Paid when credit invoice was posted"));
            }
        }

        foreach (var c in collections)
        {
            if (!txById.TryGetValue(c.SalesTransactionId, out var tx))
                continue;

            var code = ClassifyCollectionNote(c.Note);
            var inv = (tx.InvoiceNo ?? tx.SalesNo).Trim();
            rows.Add(new CustomerDueTransactionRow(
                c.CollectedAtUtc,
                TransactionTypeLabel(code),
                code,
                inv,
                c.Amount,
                string.IsNullOrWhiteSpace(c.Note) ? "" : c.Note.Trim()));
        }

        return rows
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Amount)
            .ToList();
    }
}

internal static class CustomerDueCreditAdjuster
{
    private static readonly string[] DueReducingActions = ["StoreCredit", "Exchange", "Refund"];

    public static bool ReducesCustomerDue(string? actionType) =>
        DueReducingActions.Contains((actionType ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

    public static async Task<List<SalesTransaction>> LoadOpenDueForCustomerAsync(
        BongoTexDbContext db,
        string customerName)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            return [];

        var key = customerName.Trim();
        var rows = await db.SalesTransactions
            .Where(x => x.DueAmount > 0 && x.CustomerName != null && x.CustomerName != "")
            .OrderBy(x => x.SoldAtUtc)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync();

        return rows
            .Where(x => string.Equals(x.CustomerName.Trim(), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Applies return value against open customer due (FIFO by sale date). Returns amount applied.</summary>
    public static async Task<decimal> ApplyReturnCreditAsync(
        BongoTexDbContext db,
        string customerName,
        decimal returnValue,
        string returnNo,
        DateTime returnedAtUtc)
    {
        if (returnValue <= 0 || string.IsNullOrWhiteSpace(customerName))
            return 0;

        var openDue = await LoadOpenDueForCustomerAsync(db, customerName);
        var remaining = returnValue;
        var applied = 0m;
        foreach (var tx in openDue)
        {
            if (remaining <= 0)
                break;

            var apply = Math.Min(remaining, tx.DueAmount);
            tx.PaidAmount += apply;
            tx.DueAmount -= apply;
            remaining -= apply;
            applied += apply;

            db.SalesCollections.Add(new SalesCollection
            {
                SalesTransactionId = tx.Id,
                Amount = apply,
                CollectedAtUtc = returnedAtUtc,
                Note = ReturnCreditNote(returnNo)
            });
        }

        return applied;
    }

    /// <summary>Cash + settlement discount applied to open due (FIFO). Records separate collection lines for cash vs discount.</summary>
    public static async Task<CustomerDueCollectionResult> ApplyDueCollectionAsync(
        BongoTexDbContext db,
        string customerName,
        decimal amountReceived,
        decimal settlementDiscount,
        string? note,
        DateTime collectedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new InvalidOperationException("Customer name is required.");

        var openDue = await LoadOpenDueForCustomerAsync(db, customerName);
        var totalOpen = openDue.Sum(x => x.DueAmount);
        if (totalOpen <= 0)
            throw new InvalidOperationException("This customer has no open due.");

        var requested = amountReceived + settlementDiscount;
        if (requested <= 0)
            throw new InvalidOperationException("Enter cash received and/or settlement discount.");

        if (requested > totalOpen + 0.0001m)
        {
            throw new InvalidOperationException(
                $"Cash plus discount ({requested:F2}) cannot exceed open due ({totalOpen:F2}).");
        }

        var batchRef = $"LEDGER-{collectedAtUtc:yyyyMMddHHmmss}";
        var baseNote = string.IsNullOrWhiteSpace(note) ? "Due ledger collection" : note.Trim();
        var remainingCash = amountReceived;
        var remainingDiscount = settlementDiscount;
        var cashApplied = 0m;
        var discountApplied = 0m;

        foreach (var tx in openDue)
        {
            var pool = remainingCash + remainingDiscount;
            if (pool <= 0)
                break;

            var apply = Math.Min(pool, tx.DueAmount);
            var cashPart = Math.Min(apply, remainingCash);
            var discPart = apply - cashPart;

            if (cashPart > 0)
            {
                db.SalesCollections.Add(new SalesCollection
                {
                    SalesTransactionId = tx.Id,
                    Amount = cashPart,
                    CollectedAtUtc = collectedAtUtc,
                    Note = $"{baseNote} (cash) [{batchRef}]"
                });
                remainingCash -= cashPart;
                cashApplied += cashPart;
            }

            if (discPart > 0)
            {
                db.SalesCollections.Add(new SalesCollection
                {
                    SalesTransactionId = tx.Id,
                    Amount = discPart,
                    CollectedAtUtc = collectedAtUtc,
                    Note = $"{baseNote} (settlement discount) [{batchRef}]"
                });
                remainingDiscount -= discPart;
                discountApplied += discPart;
            }

            tx.PaidAmount += apply;
            tx.DueAmount -= apply;
        }

        var remainingDue = openDue.Sum(x => x.DueAmount);
        return new CustomerDueCollectionResult(
            cashApplied,
            discountApplied,
            cashApplied + discountApplied,
            remainingDue);
    }

    public static async Task<decimal> AmountAlreadyAppliedForReturnAsync(
        BongoTexDbContext db,
        string returnNo)
    {
        var marker = ReturnCreditNote(returnNo);
        return await db.SalesCollections
            .Where(c => c.Note == marker)
            .SumAsync(c => (decimal?)c.Amount) ?? 0m;
    }

    public static string ReturnCreditNote(string returnNo) => $"Return credit {returnNo}";

    /// <summary>Applies due credit for returns not yet linked to sales collections (e.g. posted before this feature).</summary>
    public static async Task<decimal> ReconcilePendingReturnsAsync(
        BongoTexDbContext db,
        string? customerNameFilter = null)
    {
        var returns = await db.SalesReturns.ToListAsync();
        if (!string.IsNullOrWhiteSpace(customerNameFilter))
        {
            var key = customerNameFilter.Trim();
            returns = returns
                .Where(r => string.Equals(r.CustomerName.Trim(), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var totalApplied = 0m;
        foreach (var tracked in returns
                     .Where(r => ReducesCustomerDue(r.ActionType))
                     .OrderBy(r => r.ReturnedAtUtc)
                     .ThenBy(r => r.ReturnNo))
        {
            var already = await AmountAlreadyAppliedForReturnAsync(db, tracked.ReturnNo);
            var remaining = tracked.TotalAmount - already;
            if (remaining <= 0.0001m)
            {
                tracked.DueCreditApplied = already;
                continue;
            }

            var applied = await ApplyReturnCreditAsync(
                db,
                tracked.CustomerName,
                remaining,
                tracked.ReturnNo,
                tracked.ReturnedAtUtc);
            tracked.DueCreditApplied = already + applied;
            totalApplied += applied;
        }

        if (totalApplied > 0)
            await db.SaveChangesAsync();

        return totalApplied;
    }
}

internal static class CustomerDueLedgerDiscount
{
    public static (decimal TotalDiscount, decimal MaxAllowedDiscount) Compute(
        IEnumerable<SalesTransaction> lines,
        IReadOnlyDictionary<Guid, InventoryItem> items)
    {
        decimal totalDiscount = 0;
        decimal maxAllowedDiscount = 0;
        foreach (var line in lines)
        {
            if (!line.InventoryItemId.HasValue) continue;
            if (!items.TryGetValue(line.InventoryItemId.Value, out var item)) continue;
            var listPrice = item.SalesPrice > 0 ? item.SalesPrice : item.UnitPrice;
            var maxPerUnit = Math.Max(0, item.DiscountPrice);
            var actualPerUnit = Math.Max(0, listPrice - line.UnitPrice);
            totalDiscount += actualPerUnit * line.Quantity;
            maxAllowedDiscount += maxPerUnit * line.Quantity;
        }

        return (totalDiscount, maxAllowedDiscount);
    }
}

public sealed class CreateCustomerRequest
{
    public string? CustomerCode { get; set; }
    public string? Name { get; set; }
    public string? ShopName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
}
public sealed class CreateSupplierRequest
{
    public string? SupplierCode { get; set; }
    public string? Name { get; set; }
    public string? CompanyName { get; set; }
    public string? Category { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
}
public sealed class UpdateSupplierCategoryRequest
{
    public string? Category { get; set; }
}
static class EmployeeCategoryCatalog
{
    public static readonly string[] FactoryDesignations =
    [
        "Owner", "Manager", "Accountant", "Designer", "Cutting Master", "Line Man", "Labour",
        "Operator", "Helper", "Print", "Security", "Staff"
    ];
}
static class EmployeeSerialOps
{
    public static async Task<string?> ValidateUniqueAsync(
        BongoTexDbContext db, int serialNumber, string employeeType, Guid? siteId, Guid? excludeEmployeeId)
    {
        if (serialNumber < 1)
            return "Serial number must be at least 1.";
        if (employeeType == "SalesCenter")
        {
            if (!siteId.HasValue || siteId.Value == Guid.Empty)
                return "Select sales center for sales center employee.";
            var exists = await db.Employees.AnyAsync(e =>
                e.EmployeeType == "SalesCenter"
                && e.SiteId == siteId
                && e.SerialNumber == serialNumber
                && (!excludeEmployeeId.HasValue || e.Id != excludeEmployeeId.Value));
            if (exists)
                return "Serial number already used for this sales centre.";
        }
        else
        {
            var exists = await db.Employees.AnyAsync(e =>
                e.EmployeeType == employeeType
                && e.SerialNumber == serialNumber
                && (!excludeEmployeeId.HasValue || e.Id != excludeEmployeeId.Value));
            if (exists)
                return employeeType == "PrintFactory"
                    ? "Serial number already used in print factory series."
                    : "Serial number already used in factory series.";
        }
        return null;
    }
}
public sealed class CreateEmployeeRequest
{
    public string? EmployeeCode { get; set; }
    public int SerialNumber { get; set; }
    public string? Name { get; set; }
    public string? EmployeeType { get; set; }
    public string? EmployeeCategory { get; set; }
    public Guid? SiteId { get; set; }
    public decimal MonthlySalary { get; set; }
    public string? MobileNumber { get; set; }
    public string? NationalIdNumber { get; set; }
    public string? NationalIdImageBase64 { get; set; }
    public string? Address { get; set; }
}
public sealed class UpdateEmployeeRequest
{
    public int SerialNumber { get; set; }
    public string? Name { get; set; }
    public string? EmployeeType { get; set; }
    public string? EmployeeCategory { get; set; }
    public Guid? SiteId { get; set; }
    public decimal MonthlySalary { get; set; }
    public string? MobileNumber { get; set; }
    public string? NationalIdNumber { get; set; }
    public string? NationalIdImageBase64 { get; set; }
    public string? Address { get; set; }
}
public sealed class SetEmployeeActiveRequest
{
    public bool IsActive { get; set; } = true;
    public DateTime? LeftAtUtc { get; set; }
}
public sealed class SetSiteMonthlyRentRequest
{
    public decimal MonthlyRent { get; set; }
    public string? LandlordName { get; set; }
}
public sealed class CreateSalesCenterRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public decimal MonthlyRent { get; set; }
    public string? LandlordName { get; set; }
}
public sealed class UpdateSiteRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
}
public sealed class SetSiteActiveRequest
{
    public bool IsActive { get; set; } = true;
    public DateTime? ClosedAtUtc { get; set; }
}
public sealed class CreateSalesInvoiceRequest
{
    public Guid SiteId { get; set; }
    public string? InvoiceNo { get; set; }
    public List<SalesInvoiceLineRequest> Lines { get; set; } = [];
    public string? CustomerName { get; set; }
    public bool UseDiscountPrice { get; set; }
    public bool IsCredit { get; set; }
    public decimal PaidAmount { get; set; }
    public DateTime? SoldAtUtc { get; set; }
}

public sealed class SalesInvoiceLineRequest
{
    public Guid InventoryItemId { get; set; }
    public int Quantity { get; set; }
    /// <summary>Explicit unit selling price. When omitted, invoice <see cref="CreateSalesInvoiceRequest.UseDiscountPrice"/> selects list vs catalogue max-discount formula.</summary>
    public decimal? SellingUnitPrice { get; set; }
}

public sealed class CreateSalesTransactionRequest
{
    public Guid SiteId { get; set; }
    public Guid? InventoryItemId { get; set; }
    public int Quantity { get; set; }
    public string? CustomerName { get; set; }
    public bool UseDiscountPrice { get; set; }
    public bool IsCredit { get; set; }
    public decimal PaidAmount { get; set; }
    public DateTime? SoldAtUtc { get; set; }
    public decimal? ManualTotalAmount { get; set; }
    public decimal? SellingUnitPrice { get; set; }
}

public sealed class UpdateSalesTransactionRequest
{
    public int Quantity { get; set; }
    public string? CustomerName { get; set; }
    public bool UseDiscountPrice { get; set; }
    public bool IsCredit { get; set; }
    public decimal PaidAmount { get; set; }
    public DateTime? SoldAtUtc { get; set; }
    public decimal? ManualTotalAmount { get; set; }
    public decimal? SellingUnitPrice { get; set; }
}
public sealed class CreateNoInvoiceSalesReturnRequest
{
    public Guid SiteId { get; set; }
    public Guid InventoryItemId { get; set; }
    public string? CustomerType { get; set; }
    public string? CustomerName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? ActionType { get; set; }
    public decimal RefundAmount { get; set; }
    public string? Reason { get; set; }
    public DateTime? ReturnedAtUtc { get; set; }
}
public sealed class CreateFinishedItemGiftIssueRequest
{
    public Guid SiteId { get; set; }
    public Guid InventoryItemId { get; set; }
    public int Quantity { get; set; }
    public string? RecipientName { get; set; }
    public string? Reason { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
}
public record CreateSalesCollectionRequest(decimal Amount, string? Note, DateTime? CollectedAtUtc);
public sealed class CustomerDueCollectionRequest
{
    public string? CustomerName { get; set; }
    public decimal AmountReceived { get; set; }
    public decimal SettlementDiscount { get; set; }
    public string? Note { get; set; }
    public DateTime? CollectedAtUtc { get; set; }
}
public sealed class CreateCashMovementRequest
{
    public string FromPool { get; set; } = string.Empty;
    public string ToPool { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Guid? FromSiteId { get; set; }
    public Guid? ToSiteId { get; set; }
    public string? Note { get; set; }
    public DateTime? MovementDateUtc { get; set; }
}
public sealed class DailyCashBalanceRequest
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public decimal? OpeningManager { get; set; }
    public decimal? OpeningFactoryPetty { get; set; }
    public Dictionary<string, decimal>? OpeningsBySalesCenter { get; set; }
}
internal sealed record FinanceSiteCashRow(Guid SiteId, string Code, string Name, decimal Balance);
internal sealed record FinanceWalletSummary(
    DateTime AsOfExclusiveUtc,
    decimal ManagerCashBalance,
    decimal FactoryPettyCashBalance,
    decimal FactoryPettyCashShortfall,
    IReadOnlyList<FinanceSiteCashRow> SalesCenters,
    decimal TotalModeledCash,
    string Hint);
public sealed class CreateSupplierPurchaseRequest
{
    public Guid SupplierId { get; set; }
    public Guid? FactorySiteId { get; set; }
    public string? InvoiceRef { get; set; }
    public string? Description { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? PaidAmount { get; set; }
    public DateTime? PurchasedAtUtc { get; set; }
    public List<SupplierPurchaseLineRequest>? Lines { get; set; }
    /// <summary>When set with quantity, records raw material receipt at factory.</summary>
    public Guid? RawMaterialId { get; set; }
    public decimal? RawMaterialQuantity { get; set; }
    public decimal? RawMaterialUnitCost { get; set; }
    /// <summary>Multiple raw material lines on one supplier bill (creates materials if needed).</summary>
    public List<RawMaterialPurchaseLineRequest>? RawMaterialLines { get; set; }
}

public sealed class SupplierPurchaseLineRequest
{
    public Guid InventoryItemId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class RawMaterialPurchaseLineRequest
{
    public string? Category { get; set; }
    public string? MemoNo { get; set; }
    public string? Name { get; set; }
    public string? Unit { get; set; }
    public Guid? RawMaterialId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class PaySupplierPurchaseRequest
{
    public decimal Amount { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public string? Note { get; set; }
}

public sealed class ResetAllDataRequest
{
    public string? Confirm { get; set; }
}

public sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

public sealed class CreateUserRequest
{
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
    public string? Role { get; set; }
}

public sealed class UpdateUserRequest
{
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class ResetPasswordRequest
{
    public string? NewPassword { get; set; }
}

internal static class ProductStyleOps
{
    public static readonly (string Prefix, string Name)[] DefaultStyles =
    [
        ("ST", "T Shirt"),
        ("SP", "Polo Shirt"),
        ("WS", "Long Sleeve"),
        ("WH", "Hoodie"),
    ];

    public static async Task EnsureDefaultsAsync(BongoTexDbContext db)
    {
        if (await db.ProductStyles.AnyAsync())
            return;
        var now = DateTime.UtcNow;
        foreach (var (prefix, name) in DefaultStyles)
        {
            db.ProductStyles.Add(new ProductStyle
            {
                Prefix = prefix,
                Name = name,
                ProductionCost = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        await db.SaveChangesAsync();
    }

    public static string? NormalizePrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var p = raw.Trim().ToUpperInvariant();
        if (p.Length is < 2 or > 10)
            return null;
        foreach (var ch in p)
        {
            if (ch is < 'A' or > 'Z')
                return null;
        }
        return p;
    }

    public static string? TryGetPrefixFromSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return null;
        var dash = sku.Trim().IndexOf('-');
        if (dash <= 0)
            return null;
        return NormalizePrefix(sku[..dash]);
    }

    public static async Task<Dictionary<string, ProductStyle>> LoadByPrefixAsync(
        BongoTexDbContext db,
        CancellationToken ct = default) =>
        await db.ProductStyles.AsNoTracking().ToDictionaryAsync(s => s.Prefix, ct);

    public static decimal GetProductionCostForSku(
        IReadOnlyDictionary<string, ProductStyle> byPrefix,
        string sku)
    {
        var prefix = TryGetPrefixFromSku(sku);
        if (prefix is null)
            return 0;
        return byPrefix.TryGetValue(prefix, out var style) ? style.ProductionCost : 0;
    }
}

internal static class SiteOps
{
    public static async Task<string> NextSalesCenterCodeAsync(BongoTexDbContext db)
    {
        var codes = await db.Sites.AsNoTracking()
            .Where(s => s.Type == "SalesCenter")
            .Select(s => s.Code)
            .ToListAsync();
        var max = 0;
        foreach (var code in codes)
        {
            if (!code.StartsWith("SC-", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = code.Length > 3 ? code[3..] : "";
            if (int.TryParse(suffix, out var n) && n > max)
                max = n;
        }
        return $"SC-{(max + 1):D2}";
    }

    public static async Task EnsureInventoryStocksForSiteAsync(BongoTexDbContext db, Guid siteId)
    {
        var itemIds = await db.InventoryItems.Select(i => i.Id).ToListAsync();
        foreach (var itemId in itemIds)
        {
            if (await db.InventoryStocks.AnyAsync(s => s.InventoryItemId == itemId && s.SiteId == siteId))
                continue;
            db.InventoryStocks.Add(new InventoryStock
            {
                InventoryItemId = itemId,
                SiteId = siteId,
                Quantity = 0
            });
        }
        await db.SaveChangesAsync();
    }
}

internal static class DataResetOps
{
    public static async Task ResetAllAsync(BongoTexDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM PayrollLines;
            DELETE FROM PayrollRuns;
            DELETE FROM SupplierPurchaseLines;
            DELETE FROM SupplierPurchases;
            DELETE FROM RawMaterialMovements;
            DELETE FROM RawMaterialScrapSales;
            DELETE FROM SalesCollections;
            DELETE FROM SalesTransactions;
            DELETE FROM SalesReturns;
            DELETE FROM FinishedItemGiftIssues;
            DELETE FROM ExpenseEntries;
            DELETE FROM CashMovements;
            DELETE FROM StockTransfers;
            DELETE FROM FinishingEntries;
            DELETE FROM SewingEntries;
            DELETE FROM CuttingEntries;
            DELETE FROM ProductionOrders;
            DELETE FROM InventoryStocks;
            DELETE FROM RawMaterialStocks;
            DELETE FROM InventoryItems;
            DELETE FROM RawMaterials;
            DELETE FROM Employees;
            DELETE FROM Suppliers;
            DELETE FROM Customers;
            DELETE FROM SalesOrders;
            DELETE FROM SiteMonthlyRents;
            DELETE FROM ProductStyles;
            DELETE FROM Sites;
            """);

        await SeedDefaultSitesAsync(db);
        await ProductStyleOps.EnsureDefaultsAsync(db);
    }

    public static async Task<List<Site>> SeedDefaultSitesAsync(BongoTexDbContext db)
    {
        if (await db.Sites.AnyAsync())
            return await db.Sites.OrderBy(x => x.Code).ToListAsync();

        var sites = new List<Site>
        {
            new() { Code = "FACTORY-01", Name = "BongoTex Main Factory", Type = "Factory" },
            new() { Code = "SC-01", Name = "City Plaza", Type = "SalesCenter" },
            new() { Code = "SC-02", Name = "Nagar Plaza", Type = "SalesCenter" },
            new() { Code = "SC-03", Name = "Trade Center", Type = "SalesCenter" }
        };
        db.Sites.AddRange(sites);
        await db.SaveChangesAsync();
        return sites;
    }
}

public sealed class CreateExpenseEntryRequest
{
    public string Category { get; set; } = string.Empty;
    public string PartyName { get; set; } = string.Empty;
    public string ExpenseScope { get; set; } = string.Empty;
    /// <summary>Site GUID string from UI; empty/null when not a sales-center expense.</summary>
    public string? SiteId { get; set; }
    public decimal Amount { get; set; }
    public string? Department { get; set; }
    public string? Description { get; set; }
    public DateTime? ExpenseDateUtc { get; set; }
    public string? SalaryPaymentType { get; set; }
    public string? SalaryForMonth { get; set; }
    public string? CashbookNote { get; set; }
}
public record GeneratePayrollRequest(string Month, string ExpenseScope, Guid? SiteId);
public record PayPayrollLineRequest(
    Guid PayrollLineId,
    decimal Amount,
    string? SalaryPaymentType,
    string? Description,
    Guid? EmployeeId = null,
    string? Month = null,
    string? ExpenseScope = null,
    Guid? SiteId = null);
public record UpdatePayrollOvertimeRequest(decimal OvertimeHours);
public record UpdatePayrollAttendanceRequest(decimal AttendanceDays);
public record UpdateSalaryExpenseRequest(decimal Amount, string? SalaryPaymentType, string? Description);

public record SetFactoryAttendanceDayRequest(string? Month, Guid EmployeeId, int Day, string? Mark);

internal static class FactoryAttendanceOps
{
    public static bool IsPresentMark(FactoryAttendanceDay x) =>
        string.Equals(x.MarkCode, "P", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(x.MarkCode) && x.DayValue > 0);

    public static string GetDisplayMark(FactoryAttendanceDay x)
    {
        var c = (x.MarkCode ?? "").Trim().ToUpperInvariant();
        if (c == "P" || c == "A") return c;
        return x.DayValue > 0 ? "P" : "A";
    }

    public static decimal CountPresentDays(IEnumerable<FactoryAttendanceDay> rows) =>
        rows.Where(IsPresentMark).Sum(x => x.DayValue > 0 ? x.DayValue : 1m);

    public static IQueryable<Employee> FactoryEmployeesQuery(BongoTexDbContext db) =>
        db.Employees.Where(e =>
            e.IsActive
            && e.EmployeeType != "PrintFactory"
            && (e.EmployeeType == "Factory"
            || e.EmployeeType == "Staff"
            || e.EmployeeType == "Operator"
            || e.EmployeeType == "Helper"
            || e.EmployeeType == "Print"
            || e.EmployeeType == "Security"
            || e.SiteId == null));

    public static IQueryable<Employee> PrintFactoryEmployeesQuery(BongoTexDbContext db) =>
        db.Employees.Where(e => e.IsActive && e.EmployeeType == "PrintFactory");

    public static IQueryable<Employee> PayrollEmployeesQuery(BongoTexDbContext db, string expenseScope) =>
        string.Equals(expenseScope, PrintFactoryConventions.ExpenseScope, StringComparison.OrdinalIgnoreCase)
            ? PrintFactoryEmployeesQuery(db)
            : FactoryEmployeesQuery(db);

    public static async Task<Employee?> FindPayrollAttendanceEmployeeAsync(BongoTexDbContext db, Guid employeeId)
    {
        var garment = await FactoryEmployeesQuery(db).FirstOrDefaultAsync(e => e.Id == employeeId);
        if (garment is not null) return garment;
        return await PrintFactoryEmployeesQuery(db).FirstOrDefaultAsync(e => e.Id == employeeId);
    }

    public static async Task<Dictionary<Guid, decimal>> GetAttendanceTotalsByEmployeeAsync(BongoTexDbContext db, string monthKey) =>
        (await db.FactoryAttendanceDays.AsNoTracking()
            .Where(x => x.MonthKey == monthKey)
            .ToListAsync())
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => CountPresentDays(g));

    public static async Task<object> GetSheetAsync(BongoTexDbContext db, string monthKey, string expenseScope)
    {
        if (!PayrollFormulas.TryGetMonthStart(monthKey, out var monthStart))
            throw new InvalidOperationException("Invalid month.");
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var employees = PayrollFormulas.OrderByEmployeeSerial(
                await PayrollEmployeesQuery(db, expenseScope).ToListAsync(),
                e => e.SerialNumber,
                e => e.Name)
            .ToList();
        var marks = await db.FactoryAttendanceDays.AsNoTracking()
            .Where(x => x.MonthKey == monthKey)
            .ToListAsync();
        var markLookup = marks.ToDictionary(
            x => (x.EmployeeId, x.AttendanceDateUtc.Day),
            GetDisplayMark);
        var rows = employees.Select(e =>
        {
            var dayMarks = new string[daysInMonth];
            for (var d = 1; d <= daysInMonth; d++)
            {
                if (markLookup.TryGetValue((e.Id, d), out var code))
                    dayMarks[d - 1] = code;
            }
            var empMarks = marks.Where(m => m.EmployeeId == e.Id).ToList();
            var total = CountPresentDays(empMarks);
            return new
            {
                e.Id,
                EmployeeId = e.Id,
                e.SerialNumber,
                e.Name,
                EmployeeName = e.Name,
                DayMarks = dayMarks,
                TotalDays = total
            };
        }).ToList();
        return new { MonthKey = monthKey, DaysInMonth = daysInMonth, Employees = rows };
    }

    public static async Task<(string? Error, object? Payload)> SetDayAsync(BongoTexDbContext db, SetFactoryAttendanceDayRequest req)
    {
        var monthKey = (req.Month ?? string.Empty).Trim();
        if (!Regex.IsMatch(monthKey, @"^\d{4}-(0[1-9]|1[0-2])$"))
            return ("Month is required in YYYY-MM format.", null);
        if (req.EmployeeId == Guid.Empty)
            return ("Employee is required.", null);
        if (req.Day < 1)
            return ("Day must be at least 1.", null);
        if (!PayrollFormulas.TryGetMonthStart(monthKey, out var monthStart))
            return ("Invalid month.", null);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        if (req.Day > daysInMonth)
            return ($"Day must be 1–{daysInMonth} for {monthKey}.", null);

        var emp = await FindPayrollAttendanceEmployeeAsync(db, req.EmployeeId);
        if (emp is null)
            return ("Employee not found or not a factory / print factory employee.", null);

        var payrollScope = PayrollScopeOps.ScopeForEmployee(emp);

        var dateUtc = new DateTime(monthStart.Year, monthStart.Month, req.Day, 0, 0, 0, DateTimeKind.Utc);
        var mark = (req.Mark ?? string.Empty).Trim().ToUpperInvariant();
        if (mark is not ("P" or "A" or ""))
            return ("Mark must be P (present), A (absent), or empty to clear.", null);

        var existing = await db.FactoryAttendanceDays
            .FirstOrDefaultAsync(x => x.EmployeeId == req.EmployeeId && x.AttendanceDateUtc == dateUtc);

        if (mark == "")
        {
            if (existing is not null)
                db.FactoryAttendanceDays.Remove(existing);
        }
        else
        {
            var dayValue = mark == "P" ? 1m : 0m;
            if (existing is null)
            {
                db.FactoryAttendanceDays.Add(new FactoryAttendanceDay
                {
                    EmployeeId = req.EmployeeId,
                    MonthKey = monthKey,
                    AttendanceDateUtc = dateUtc,
                    MarkCode = mark,
                    DayValue = dayValue,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.MarkCode = mark;
                existing.DayValue = dayValue;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        var empMarks = await db.FactoryAttendanceDays
            .Where(x => x.MonthKey == monthKey && x.EmployeeId == req.EmployeeId)
            .ToListAsync();
        var totalDays = CountPresentDays(empMarks);

        await EnsurePayrollLineAsync(db, monthKey, req.EmployeeId, totalDays, payrollScope);
        var payrollLine = await SyncPayrollLineAttendanceAsync(db, monthKey, req.EmployeeId, totalDays, payrollScope);

        return (null, new
        {
            req.EmployeeId,
            req.Day,
            Mark = mark,
            TotalDays = totalDays,
            MonthKey = monthKey,
            Payroll = payrollLine is null ? null : new
            {
                payrollLine.Id,
                payrollLine.AttendanceDays,
                payrollLine.AttendanceSalaryAmount,
                payrollLine.AttendanceBonus,
                payrollLine.NetPayable
            }
        });
    }

    public static async Task<PayrollRun> GetOrCreatePayrollRunAsync(BongoTexDbContext db, string monthKey, string expenseScope)
    {
        var run = await db.PayrollRuns
            .Where(x => x.MonthKey == monthKey && x.ExpenseScope == expenseScope && x.SiteId == null)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (run is not null)
            return run;
        run = new PayrollRun
        {
            MonthKey = monthKey,
            ExpenseScope = expenseScope,
            SiteId = null,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public static async Task EnsurePayrollLineAsync(BongoTexDbContext db, string monthKey, Guid employeeId, decimal attendanceDays, string expenseScope)
    {
        var emp = await PayrollEmployeesQuery(db, expenseScope).FirstOrDefaultAsync(e => e.Id == employeeId);
        if (emp is null)
            return;

        var run = await GetOrCreatePayrollRunAsync(db, monthKey, expenseScope);
        var existing = await db.PayrollLines.FirstOrDefaultAsync(x => x.PayrollRunId == run.Id && x.EmployeeId == employeeId);
        if (existing is not null)
        {
            existing.AttendanceDays = attendanceDays;
            existing.EmployeeCategory = PayrollFormulas.ResolvePayrollEmployeeCategory(emp);
            PayrollFormulas.RecalculateLine(existing, monthKey, expenseScope);
            await db.SaveChangesAsync();
            return;
        }

        if (!PayrollFormulas.TryGetMonthStart(monthKey, out _))
            return;

        var salaryRows = await PayrollSalaryOps.LoadSalaryExpensesForPartyAsync(
            db, emp.Name, monthKey, expenseScope, run.SiteId);
        var paid = PayrollSalaryOps.SummarizeSalaryPayments(salaryRows);
        var advancePaid = paid.Advance;
        var duePaid = paid.Due;
        var currentPaid = paid.Current;

        var line = new PayrollLine
        {
            PayrollRunId = run.Id,
            EmployeeId = emp.Id,
            EmployeeName = emp.Name,
            EmployeeCategory = PayrollFormulas.ResolvePayrollEmployeeCategory(emp),
            MonthlySalary = emp.MonthlySalary,
            AdvancePaid = advancePaid,
            CurrentPaid = currentPaid,
            DuePaid = duePaid,
            AttendanceDays = attendanceDays,
            OvertimeHours = 0,
            SnakesPay = 0
        };
        PayrollFormulas.RecalculateLine(line, monthKey, expenseScope);
        db.PayrollLines.Add(line);
        await db.SaveChangesAsync();
    }

    public static async Task<PayrollLine?> SyncPayrollLineAttendanceAsync(BongoTexDbContext db, string monthKey, Guid employeeId, decimal totalDays, string expenseScope)
    {
        var run = await db.PayrollRuns
            .Where(x => x.MonthKey == monthKey && x.ExpenseScope == expenseScope && x.SiteId == null)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync();
        if (run is null)
            return null;

        var line = await db.PayrollLines.FirstOrDefaultAsync(x => x.PayrollRunId == run.Id && x.EmployeeId == employeeId);
        if (line is null)
            return null;

        var emp = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId);
        if (emp is not null)
            line.EmployeeCategory = PayrollFormulas.ResolvePayrollEmployeeCategory(emp);
        line.AttendanceDays = totalDays;
        PayrollFormulas.RecalculateLine(line, monthKey, expenseScope);
        await db.SaveChangesAsync();
        return line;
    }
}
public record UpdatePayrollSnakesPayRequest(decimal SnakesPay);
public record CreateStockTransferRequest(Guid InventoryItemId, Guid FromSiteId, Guid ToSiteId, int Quantity);
public record UpdateStockTransferRequest(int Quantity, DateTime? TransferredAtUtc);
public sealed class CreateStockTransferBatchRequest
{
    public Guid FromSiteId { get; set; }
    public Guid ToSiteId { get; set; }
    public List<StockTransferBatchLineRequest>? Lines { get; set; }
}
public sealed class StockTransferBatchLineRequest
{
    public Guid InventoryItemId { get; set; }
    public int Quantity { get; set; }
}
internal sealed record StockTransferBatchLineDto(
    Guid Id,
    string TransferNo,
    string DocumentNo,
    int Quantity,
    string ItemSku,
    string ItemName,
    decimal UnitPrice,
    decimal LineAmount);

internal static class StockTransferOps
{
    public sealed record CreateResult(StockTransfer? Transfer, string? Error);

    public static async Task<CreateResult> CreateOneAsync(
        BongoTexDbContext db,
        Guid inventoryItemId,
        Guid fromSiteId,
        Guid toSiteId,
        int quantity,
        string? documentNo,
        int? lineSequence,
        bool save = true)
    {
        if (quantity <= 0)
            return new(null, "Transfer quantity must be greater than zero.");

        var fromSite = await db.Sites.FirstOrDefaultAsync(x => x.Id == fromSiteId);
        var toSite = await db.Sites.FirstOrDefaultAsync(x => x.Id == toSiteId);
        if (fromSite is null || toSite is null)
            return new(null, "Invalid source or destination site.");

        if (fromSiteId == toSiteId)
            return new(null, "Source and destination sites must be different.");

        var isFactoryToCenter = fromSite.Type == "Factory" && toSite.Type == "SalesCenter";
        var isCenterToFactory = fromSite.Type == "SalesCenter" && toSite.Type == "Factory";
        var isCenterToCenter = fromSite.Type == "SalesCenter" && toSite.Type == "SalesCenter";
        if (!isFactoryToCenter && !isCenterToFactory && !isCenterToCenter)
            return new(null, "Transfers must be factory → sales center (send stock), sales center → factory (return stock), or sales center → sales center.");

        var sourceStock = await db.InventoryStocks
            .FirstOrDefaultAsync(x => x.InventoryItemId == inventoryItemId && x.SiteId == fromSiteId);
        if (sourceStock is null || sourceStock.Quantity < quantity)
            return new(null, "Insufficient stock at source site for one or more items.");

        var destinationStock = await db.InventoryStocks
            .FirstOrDefaultAsync(x => x.InventoryItemId == inventoryItemId && x.SiteId == toSiteId);
        if (destinationStock is null)
        {
            destinationStock = new InventoryStock
            {
                InventoryItemId = inventoryItemId,
                SiteId = toSiteId,
                Quantity = 0
            };
            db.InventoryStocks.Add(destinationStock);
        }

        sourceStock.Quantity -= quantity;
        destinationStock.Quantity += quantity;

        var transferPrefix = isCenterToFactory ? "RT" : isCenterToCenter ? "SC" : "TR";
        var stem = (documentNo ?? string.Empty).Trim();
        string transferNo;
        string docNo;
        if (!string.IsNullOrEmpty(stem))
        {
            docNo = stem;
            transferNo = lineSequence is > 0
                ? $"{transferPrefix}-{stem}-{lineSequence.Value:00}"
                : $"{transferPrefix}-{stem}";
        }
        else
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            transferNo = $"{transferPrefix}-{stamp}";
            docNo = transferNo;
        }

        var transfer = new StockTransfer
        {
            TransferNo = transferNo,
            DocumentNo = docNo,
            InventoryItemId = inventoryItemId,
            FromSiteId = fromSiteId,
            ToSiteId = toSiteId,
            Quantity = quantity,
            TransferredAtUtc = DateTime.UtcNow
        };

        db.StockTransfers.Add(transfer);
        if (save)
            await db.SaveChangesAsync();

        return new(transfer, null);
    }
}

internal static class SupplierLedgerOps
{
    public static async Task<object> BuildAsync(
        BongoTexDbContext db,
        string? month,
        Supplier? supplier,
        string partyFilter)
    {
        DateTime? start = null;
        DateTime? end = null;
        if (!string.IsNullOrWhiteSpace(month) && DateTime.TryParse($"{month}-01", out var m))
        {
            start = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            end = start.Value.AddMonths(1);
        }

        var ledgerRows = new List<SupplierLedgerRow>();

        var paymentQuery = db.ExpenseEntries.AsNoTracking().Where(x => x.Category == "SupplierPayment");
        if (start.HasValue)
            paymentQuery = paymentQuery.Where(x => x.ExpenseDateUtc >= start && x.ExpenseDateUtc < end);
        var payments = await paymentQuery.OrderByDescending(x => x.ExpenseDateUtc).ToListAsync();
        if (!string.IsNullOrWhiteSpace(partyFilter))
        {
            payments = payments
                .Where(x => string.Equals((x.PartyName ?? string.Empty).Trim(), partyFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var pay in payments)
        {
            ledgerRows.Add(new SupplierLedgerRow
            {
                RowKind = "payment",
                EntryDateUtc = pay.ExpenseDateUtc,
                Reference = pay.ExpenseNo,
                TypeLabel = "Payment",
                PaidAmount = pay.Amount,
                Description = pay.Description,
                ExpenseScope = pay.ExpenseScope,
                SortDate = pay.ExpenseDateUtc,
                SortOrder = 2
            });
        }

        decimal purchaseTotalInPeriod = 0m;
        decimal? totalDue = null;

        if (supplier is not null)
        {
            var purchQuery = db.SupplierPurchases.AsNoTracking().Where(p => p.SupplierId == supplier.Id);
            if (start.HasValue)
                purchQuery = purchQuery.Where(p => p.PurchasedAtUtc >= start && p.PurchasedAtUtc < end);
            var purchases = await purchQuery.OrderByDescending(p => p.PurchasedAtUtc).ToListAsync();
            purchaseTotalInPeriod += purchases.Sum(p => p.TotalAmount);

            var purchaseIds = purchases.Select(p => p.Id).ToList();
            var invLines = purchaseIds.Count == 0
                ? []
                : await db.SupplierPurchaseLines.AsNoTracking()
                    .Where(l => purchaseIds.Contains(l.SupplierPurchaseId))
                    .ToListAsync();
            var itemIds = invLines.Select(l => l.InventoryItemId).Distinct().ToList();
            var items = itemIds.Count == 0
                ? new Dictionary<Guid, InventoryItem>()
                : await db.InventoryItems.AsNoTracking()
                    .Where(i => itemIds.Contains(i.Id))
                    .ToDictionaryAsync(i => i.Id);

            var rmMovs = purchaseIds.Count == 0
                ? []
                : await db.RawMaterialMovements.AsNoTracking()
                    .Where(m => m.SupplierPurchaseId != null
                                && purchaseIds.Contains(m.SupplierPurchaseId.Value)
                                && m.MovementType == RawMaterialOps.TypePurchase)
                    .ToListAsync();
            var matIds = rmMovs.Select(m => m.RawMaterialId).Distinct().ToList();
            var materials = matIds.Count == 0
                ? new Dictionary<Guid, RawMaterial>()
                : await db.RawMaterials.AsNoTracking()
                    .Where(m => matIds.Contains(m.Id))
                    .ToDictionaryAsync(m => m.Id);

            foreach (var purchase in purchases)
            {
                var rmForPurchase = rmMovs.Where(m => m.SupplierPurchaseId == purchase.Id).ToList();
                var invForPurchase = invLines.Where(l => l.SupplierPurchaseId == purchase.Id).ToList();
                var lineCount = rmForPurchase.Count + invForPurchase.Count;

                ledgerRows.Add(new SupplierLedgerRow
                {
                    RowKind = "purchaseHeader",
                    EntryDateUtc = purchase.PurchasedAtUtc,
                    Reference = purchase.PurchaseNo,
                    InvoiceRef = purchase.InvoiceRef,
                    TypeLabel = "Bill",
                    ItemLabel = lineCount > 0 ? $"Supplier bill ({lineCount} line(s))" : "Supplier bill",
                    BillTotal = purchase.TotalAmount,
                    PaidAmount = purchase.PaidAmount,
                    DueAmount = purchase.DueAmount,
                    Description = purchase.Description,
                    SortDate = purchase.PurchasedAtUtc,
                    SortOrder = 0
                });

                foreach (var mov in rmForPurchase.OrderBy(m => m.MovementNo))
                {
                    materials.TryGetValue(mov.RawMaterialId, out var mat);
                    var code = mat?.Code ?? "";
                    var name = mat?.Name ?? "";
                    ledgerRows.Add(new SupplierLedgerRow
                    {
                        RowKind = "purchaseLine",
                        EntryDateUtc = purchase.PurchasedAtUtc,
                        Reference = purchase.PurchaseNo,
                        InvoiceRef = purchase.InvoiceRef,
                        TypeLabel = "·",
                        ItemCode = code,
                        ItemLabel = string.IsNullOrEmpty(code) ? name : $"{code} — {name}",
                        Quantity = mov.Quantity,
                        Unit = mat?.Unit ?? "kg",
                        UnitCost = mov.UnitCost,
                        LineAmount = mov.TotalCost,
                        SortDate = purchase.PurchasedAtUtc,
                        SortOrder = 1
                    });
                }

                foreach (var line in invForPurchase)
                {
                    items.TryGetValue(line.InventoryItemId, out var item);
                    var sku = item?.Sku ?? "";
                    var name = item?.Name ?? "";
                    ledgerRows.Add(new SupplierLedgerRow
                    {
                        RowKind = "purchaseLine",
                        EntryDateUtc = purchase.PurchasedAtUtc,
                        Reference = purchase.PurchaseNo,
                        InvoiceRef = purchase.InvoiceRef,
                        TypeLabel = "·",
                        ItemCode = sku,
                        ItemLabel = string.IsNullOrEmpty(sku) ? name : $"{sku} — {name}",
                        Quantity = line.Quantity,
                        Unit = "pcs",
                        UnitCost = line.UnitCost,
                        LineAmount = line.LineTotal,
                        SortDate = purchase.PurchasedAtUtc,
                        SortOrder = 1
                    });
                }
            }

            var pfQuery = db.PrintFactoryPurchases.AsNoTracking()
                .Where(p => p.SupplierId == supplier.Id
                    || (p.SupplierId == Guid.Empty && p.SupplierName == supplier.Name));
            if (start.HasValue)
                pfQuery = pfQuery.Where(p => p.PurchasedAtUtc >= start && p.PurchasedAtUtc < end);
            var pfPurchases = await pfQuery.OrderByDescending(p => p.PurchasedAtUtc).ToListAsync();
            purchaseTotalInPeriod += pfPurchases.Sum(p => p.TotalAmount);

            await PrintFactorySchema.EnsureAsync(db);

            var pfIds = pfPurchases.Select(p => p.Id).ToList();
            var pfLines = pfIds.Count == 0
                ? []
                : await db.PrintFactoryPurchaseLines.AsNoTracking()
                    .Where(l => pfIds.Contains(l.PrintFactoryPurchaseId))
                    .ToListAsync();

            foreach (var purchase in pfPurchases)
            {
                var linesForPurchase = pfLines.Where(l => l.PrintFactoryPurchaseId == purchase.Id).ToList();
                ledgerRows.Add(new SupplierLedgerRow
                {
                    RowKind = "purchaseHeader",
                    EntryDateUtc = purchase.PurchasedAtUtc,
                    Reference = purchase.VoucherNo,
                    InvoiceRef = purchase.InvoiceRef,
                    TypeLabel = "Print bill",
                    ItemLabel = linesForPurchase.Count > 0
                        ? $"Print factory bill ({linesForPurchase.Count} line(s))"
                        : "Print factory bill",
                    BillTotal = purchase.TotalAmount,
                    PaidAmount = purchase.PaidAmount,
                    DueAmount = purchase.DueAmount,
                    Description = purchase.Description,
                    ExpenseScope = "PrintFactory",
                    SortDate = purchase.PurchasedAtUtc,
                    SortOrder = 0
                });

                foreach (var line in linesForPurchase)
                {
                    ledgerRows.Add(new SupplierLedgerRow
                    {
                        RowKind = "purchaseLine",
                        EntryDateUtc = purchase.PurchasedAtUtc,
                        Reference = purchase.VoucherNo,
                        InvoiceRef = purchase.InvoiceRef,
                        TypeLabel = "·",
                        ItemLabel = line.Description,
                        Quantity = line.Quantity,
                        Unit = line.Unit,
                        UnitCost = line.UnitCost,
                        LineAmount = line.LineTotal,
                        SortDate = purchase.PurchasedAtUtc,
                        SortOrder = 1,
                        ExpenseScope = "PrintFactory"
                    });
                }
            }

            totalDue = Math.Max(0m, purchaseTotalInPeriod - payments.Sum(x => x.Amount));
        }

        var paymentTotal = payments.Sum(x => x.Amount);

        var orderedRows = ledgerRows
            .OrderByDescending(r => r.SortDate)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.Reference)
            .Select(r => new
            {
                r.RowKind,
                r.EntryDateUtc,
                r.Reference,
                r.InvoiceRef,
                r.TypeLabel,
                r.ItemCode,
                r.ItemLabel,
                r.Quantity,
                r.Unit,
                r.UnitCost,
                r.LineAmount,
                r.BillTotal,
                BillPaid = r.RowKind == "purchaseHeader" ? r.PaidAmount : (decimal?)null,
                BillDue = r.RowKind == "purchaseHeader" ? r.DueAmount : (decimal?)null,
                PaymentAmount = r.RowKind == "payment" ? r.PaidAmount : (decimal?)null,
                r.Description,
                r.ExpenseScope,
                // Legacy fields for payment-only rows
                ExpenseNo = r.RowKind == "payment" ? r.Reference : null,
                Amount = r.RowKind == "payment" ? r.PaidAmount : r.LineAmount,
                PartyName = partyFilter
            })
            .ToList();

        return new
        {
            TotalAmount = paymentTotal,
            PurchaseTotalInPeriod = supplier is not null ? purchaseTotalInPeriod : (decimal?)null,
            Rows = orderedRows,
            Filter = supplier is null && string.IsNullOrWhiteSpace(partyFilter)
                ? null
                : new
                {
                    SupplierId = supplier?.Id,
                    PartyName = partyFilter,
                    SupplierName = supplier?.Name ?? partyFilter,
                    SupplierCategoryLabel = supplier is not null ? SupplierConventions.DisplayLabel(supplier.Category) : null,
                    TotalDue = totalDue,
                    PurchaseTotalInPeriod = supplier is not null ? purchaseTotalInPeriod : (decimal?)null
                }
        };
    }

    sealed class SupplierLedgerRow
    {
        public string RowKind { get; set; } = "";
        public DateTime EntryDateUtc { get; set; }
        public string Reference { get; set; } = "";
        public string InvoiceRef { get; set; } = "";
        public string TypeLabel { get; set; } = "";
        public string ItemCode { get; set; } = "";
        public string ItemLabel { get; set; } = "";
        public decimal? Quantity { get; set; }
        public string Unit { get; set; } = "";
        public decimal? UnitCost { get; set; }
        public decimal? LineAmount { get; set; }
        public decimal? BillTotal { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal DueAmount { get; set; }
        public string Description { get; set; } = "";
        public string ExpenseScope { get; set; } = "";
        public DateTime SortDate { get; set; }
        public int SortOrder { get; set; }
    }
}

internal static class SupplierPurchaseOps
{
    public sealed record CreateResult(SupplierPurchase? Purchase, string? Error);

    public static async Task EnsureRawMaterialSchemaAsync(BongoTexDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID('RawMaterials', 'U') IS NULL
            BEGIN
                CREATE TABLE RawMaterials
                (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    Code nvarchar(50) NOT NULL,
                    Name nvarchar(200) NOT NULL,
                    Category nvarchar(40) NOT NULL CONSTRAINT DF_RawMaterials_Category2 DEFAULT('Others'),
                    Unit nvarchar(20) NOT NULL CONSTRAINT DF_RawMaterials_Unit2 DEFAULT('kg'),
                    IsActive bit NOT NULL CONSTRAINT DF_RawMaterials_IsActive2 DEFAULT(1),
                    CreatedAtUtc datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX IX_RawMaterials_Code ON RawMaterials(Code);
            END
            IF OBJECT_ID('RawMaterialStocks', 'U') IS NULL
            BEGIN
                CREATE TABLE RawMaterialStocks
                (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    RawMaterialId uniqueidentifier NOT NULL,
                    SiteId uniqueidentifier NOT NULL,
                    QuantityOnHand decimal(18,4) NOT NULL CONSTRAINT DF_RawMaterialStocks_Qty2 DEFAULT(0)
                );
                CREATE UNIQUE INDEX IX_RawMaterialStocks_MaterialSite ON RawMaterialStocks(RawMaterialId, SiteId);
            END
            IF OBJECT_ID('RawMaterialMovements', 'U') IS NULL
            BEGIN
                CREATE TABLE RawMaterialMovements
                (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    MovementNo nvarchar(40) NOT NULL,
                    RawMaterialId uniqueidentifier NOT NULL,
                    SiteId uniqueidentifier NOT NULL,
                    MovementType nvarchar(20) NOT NULL,
                    Quantity decimal(18,4) NOT NULL,
                    UnitCost decimal(18,4) NOT NULL CONSTRAINT DF_RawMaterialMovements_UnitCost2 DEFAULT(0),
                    TotalCost decimal(18,2) NOT NULL CONSTRAINT DF_RawMaterialMovements_TotalCost2 DEFAULT(0),
                    MovementDateUtc datetime2 NOT NULL,
                    Note nvarchar(500) NOT NULL CONSTRAINT DF_RawMaterialMovements_Note2 DEFAULT(''),
                    SupplierPurchaseId uniqueidentifier NULL,
                    CuttingEntryId uniqueidentifier NULL,
                    CutLotCode nvarchar(80) NOT NULL CONSTRAINT DF_RawMaterialMovements_CutLotCode2 DEFAULT(''),
                    CreatedAtUtc datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX IX_RawMaterialMovements_MovementNo ON RawMaterialMovements(MovementNo);
                CREATE INDEX IX_RawMaterialMovements_MovementDateUtc ON RawMaterialMovements(MovementDateUtc);
                CREATE INDEX IX_RawMaterialMovements_RawMaterialId ON RawMaterialMovements(RawMaterialId);
                CREATE INDEX IX_RawMaterialMovements_SiteId ON RawMaterialMovements(SiteId);
            END
            """);
    }

    public static async Task<(RawMaterial? Material, string? Error)> ResolveOrCreateRawMaterialAsync(
        BongoTexDbContext db, RawMaterialPurchaseLineRequest line)
    {
        if (line.RawMaterialId is Guid id && id != Guid.Empty)
        {
            var existing = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
            return existing is null ? (null, "Raw material not found or inactive.") : (existing, null);
        }

        var name = (line.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return (null, "Each line needs a material name.");

        var code = RawMaterialRules.BuildCodeFromCategoryAndMemo(line.Category, line.MemoNo, out var codeErr);
        if (codeErr is not null)
            return (null, codeErr);

        var row = await db.RawMaterials.FirstOrDefaultAsync(x => x.Code.ToLower() == code.ToLower());
        if (row is not null)
        {
            if (!row.IsActive)
                return (null, $"Material {code} is inactive.");
            return (row, null);
        }

        row = new RawMaterial
        {
            Code = code,
            Name = name,
            Category = RawMaterialRules.NormalizeCategory(line.Category),
            Unit = RawMaterialRules.NormalizeUnit(line.Unit),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.RawMaterials.Add(row);
        return (row, null);
    }

    public static async Task<CreateResult> CreateAsync(BongoTexDbContext db, CreateSupplierPurchaseRequest req)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == req.SupplierId);
        if (supplier is null)
            return new(null, "Supplier not found.");

        var factorySiteId = req.FactorySiteId ?? Guid.Empty;
        if (factorySiteId == Guid.Empty)
        {
            var defaultFactory = await db.Sites.FirstOrDefaultAsync(x => x.Type == "Factory");
            if (defaultFactory is null)
                return new(null, "No factory site configured.");
            factorySiteId = defaultFactory.Id;
        }
        else
        {
            var factory = await db.Sites.FirstOrDefaultAsync(x => x.Id == factorySiteId && x.Type == "Factory");
            if (factory is null)
                return new(null, "Invalid factory site.");
        }

        var invLines = (req.Lines ?? [])
            .Where(l => l.InventoryItemId != Guid.Empty && l.Quantity > 0)
            .ToList();

        var rmPurchaseLines = (req.RawMaterialLines ?? [])
            .Where(l => l.Quantity > 0)
            .ToList();

        if (req.RawMaterialId is { } legacyRmId && legacyRmId != Guid.Empty && rmPurchaseLines.Count > 0)
            return new(null, "Use either rawMaterialLines or rawMaterialId, not both.");

        decimal totalAmount;
        var lineEntities = new List<SupplierPurchaseLine>();
        if (invLines.Count > 0)
        {
            var itemIds = invLines.Select(l => l.InventoryItemId).Distinct().ToList();
            var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
            foreach (var line in invLines)
            {
                if (!items.ContainsKey(line.InventoryItemId))
                    return new(null, "Inventory item not found on a purchase line.");
                if (line.UnitCost <= 0)
                    return new(null, "Each line needs unit cost greater than zero.");
            }

            foreach (var line in invLines)
            {
                var lineTotal = line.UnitCost * line.Quantity;
                lineEntities.Add(new SupplierPurchaseLine
                {
                    InventoryItemId = line.InventoryItemId,
                    Quantity = line.Quantity,
                    UnitCost = line.UnitCost,
                    LineTotal = lineTotal
                });
            }

            totalAmount = lineEntities.Sum(x => x.LineTotal);
        }
        else
        {
            totalAmount = 0;
        }

        decimal rmLinesTotal = 0;
        var resolvedRmLines = new List<(RawMaterial Material, decimal Quantity, decimal UnitCost)>();
        var rmCodeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in rmPurchaseLines)
        {
            if (line.UnitCost <= 0)
                return new(null, "Each raw material line needs rate greater than zero.");
            var previewCode = RawMaterialRules.BuildCodeFromCategoryAndMemo(line.Category, line.MemoNo, out var previewErr);
            if (previewErr is not null)
                return new(null, previewErr);
            if (!rmCodeKeys.Add(previewCode))
                return new(null, $"Duplicate material ID {previewCode} on this bill. Use a unique line memo (1, 2, 3…) per line.");
            var (material, matErr) = await SupplierPurchaseOps.ResolveOrCreateRawMaterialAsync(db, line);
            if (matErr is not null)
                return new(null, matErr);
            var lineTotal = decimal.Round(line.Quantity * line.UnitCost, 2, MidpointRounding.AwayFromZero);
            rmLinesTotal += lineTotal;
            resolvedRmLines.Add((material!, line.Quantity, line.UnitCost));
        }

        if (resolvedRmLines.Count > 0)
            totalAmount += rmLinesTotal;

        if (resolvedRmLines.Count == 0 && invLines.Count == 0)
        {
            totalAmount = req.TotalAmount ?? 0;
            if (totalAmount <= 0)
                return new(null, "Add at least one purchase line with quantity and rate.");
        }

        if (req.TotalAmount is { } manualTotal && manualTotal > 0 && invLines.Count > 0
            && Math.Abs(manualTotal - totalAmount) > 0.01m)
        {
            return new(null, "Line totals do not match the purchase total.");
        }

        var paidAmount = req.PaidAmount ?? totalAmount;
        if (paidAmount < 0)
            return new(null, "Paid amount cannot be negative.");
        if (paidAmount > totalAmount)
            return new(null, "Paid amount cannot exceed purchase total.");

        var purchasedAt = req.PurchasedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var purchase = new SupplierPurchase
        {
            PurchaseNo = $"SP-{DateTime.UtcNow:yyyyMMddHHmmss}",
            SupplierId = supplier.Id,
            FactorySiteId = factorySiteId,
            InvoiceRef = (req.InvoiceRef ?? string.Empty).Trim(),
            Description = (req.Description ?? string.Empty).Trim(),
            TotalAmount = totalAmount,
            PaidAmount = paidAmount,
            DueAmount = totalAmount - paidAmount,
            PurchasedAtUtc = purchasedAt,
            CreatedAtUtc = DateTime.UtcNow
        };

        foreach (var line in lineEntities)
        {
            line.SupplierPurchaseId = purchase.Id;
            purchase.Lines.Add(line);

            var stock = await db.InventoryStocks
                .FirstOrDefaultAsync(x => x.InventoryItemId == line.InventoryItemId && x.SiteId == factorySiteId);
            if (stock is null)
            {
                stock = new InventoryStock
                {
                    InventoryItemId = line.InventoryItemId,
                    SiteId = factorySiteId,
                    Quantity = 0
                };
                db.InventoryStocks.Add(stock);
            }

            stock.Quantity += line.Quantity;
        }

        db.SupplierPurchases.Add(purchase);

        foreach (var (material, qty, unitCost) in resolvedRmLines)
        {
            var recvErr = await RawMaterialOps.ReceiveForPurchaseAsync(
                db, material.Id, factorySiteId, qty, unitCost, purchasedAt, purchase.Id,
                $"Supplier purchase {purchase.PurchaseNo} — {material.Code}");
            if (recvErr is not null)
                return new(null, recvErr);
        }

        if (req.RawMaterialId is { } matId && matId != Guid.Empty && resolvedRmLines.Count == 0)
        {
            var rmQty = req.RawMaterialQuantity ?? 0;
            if (rmQty <= 0)
                return new(null, "Enter raw material quantity for stock receipt.");
            var unitCost = req.RawMaterialUnitCost ?? 0;
            if (unitCost <= 0 && totalAmount > 0)
                unitCost = totalAmount / rmQty;
            var recvErr = await RawMaterialOps.ReceiveForPurchaseAsync(
                db, matId, factorySiteId, rmQty, unitCost, purchasedAt, purchase.Id,
                $"Supplier purchase {purchase.PurchaseNo}");
            if (recvErr is not null)
                return new(null, recvErr);
        }

        if (paidAmount > 0)
        {
            var payErr = await AddSupplierPaymentExpenseAsync(
                db, supplier.Name, paidAmount, purchasedAt,
                $"Payment for purchase {purchase.PurchaseNo}" +
                (string.IsNullOrWhiteSpace(purchase.Description) ? "" : $" — {purchase.Description}"));
            if (payErr is not null)
                return new(null, payErr);
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            if (message.Contains("RawMaterial", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))
            {
                return new(null,
                    "Raw material tables are missing in the database. Restart the API (dotnet run) to create them, then post again.");
            }

            return new(null, $"Could not save: {message}");
        }

        return new(purchase, null);
    }

    public static async Task<object> ListAsync(BongoTexDbContext db, string? month, Guid? supplierId)
    {
        var query = db.SupplierPurchases.AsNoTracking();
        if (supplierId is { } sid && sid != Guid.Empty)
            query = query.Where(x => x.SupplierId == sid);

        if (!string.IsNullOrWhiteSpace(month) && DateTime.TryParse($"{month}-01", out var m))
        {
            var start = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = start.AddMonths(1);
            query = query.Where(x => x.PurchasedAtUtc >= start && x.PurchasedAtUtc < end);
        }

        var purchases = await query
            .OrderByDescending(x => x.PurchasedAtUtc)
            .ToListAsync();

        var purchaseIds = purchases.Select(x => x.Id).ToList();
        var lines = purchaseIds.Count == 0
            ? []
            : await db.SupplierPurchaseLines.AsNoTracking()
                .Where(l => purchaseIds.Contains(l.SupplierPurchaseId))
                .ToListAsync();

        var supplierIds = purchases.Select(x => x.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);

        var itemIds = lines.Select(l => l.InventoryItemId).Distinct().ToList();
        var items = itemIds.Count == 0
            ? new Dictionary<Guid, InventoryItem>()
            : await db.InventoryItems.AsNoTracking()
                .Where(i => itemIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id);

        var rows = purchases.Select(p =>
        {
            suppliers.TryGetValue(p.SupplierId, out var sup);
            var plines = lines.Where(l => l.SupplierPurchaseId == p.Id).Select(l =>
            {
                items.TryGetValue(l.InventoryItemId, out var item);
                return new
                {
                    l.InventoryItemId,
                    ItemSku = item?.Sku ?? "",
                    ItemName = item?.Name ?? "",
                    l.Quantity,
                    l.UnitCost,
                    l.LineTotal
                };
            }).ToList();

            return new
            {
                p.Id,
                p.PurchaseNo,
                p.SupplierId,
                SupplierName = sup?.Name ?? "",
                SupplierCode = sup?.SupplierCode ?? "",
                SupplierCategory = sup?.Category ?? "",
                SupplierCategoryLabel = SupplierConventions.DisplayLabel(sup?.Category),
                IsInHousePrint = sup != null && SupplierConventions.IsInHousePrint(sup.Category),
                IsOutsidePrint = sup != null && SupplierConventions.IsOutsidePrint(sup.Category),
                p.FactorySiteId,
                p.InvoiceRef,
                p.Description,
                p.TotalAmount,
                p.PaidAmount,
                p.DueAmount,
                p.PurchasedAtUtc,
                LineCount = plines.Count,
                Lines = plines
            };
        }).ToList();

        return new
        {
            TotalAmount = rows.Sum(x => x.TotalAmount),
            TotalDue = rows.Sum(x => x.DueAmount),
            Rows = rows
        };
    }

    public static async Task<CreateResult> PayAsync(BongoTexDbContext db, Guid id, PaySupplierPurchaseRequest req)
    {
        var purchase = await db.SupplierPurchases.FirstOrDefaultAsync(x => x.Id == id);
        if (purchase is null)
            return new(null, "Purchase not found.");

        if (req.Amount <= 0)
            return new(null, "Payment amount must be greater than zero.");
        if (req.Amount > purchase.DueAmount)
            return new(null, $"Payment exceeds due balance ({purchase.DueAmount:0.##}).");

        var supplier = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == purchase.SupplierId);
        if (supplier is null)
            return new(null, "Supplier not found.");

        var payDate = req.PaidAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var payErr = await AddSupplierPaymentExpenseAsync(
            db, supplier.Name, req.Amount, payDate,
            $"Payment on purchase {purchase.PurchaseNo}" +
            (string.IsNullOrWhiteSpace(req.Note) ? "" : $" — {req.Note.Trim()}"));
        if (payErr is not null)
            return new(null, payErr);

        purchase.PaidAmount += req.Amount;
        purchase.DueAmount = purchase.TotalAmount - purchase.PaidAmount;
        await db.SaveChangesAsync();
        return new(purchase, null);
    }

    private static async Task<string?> AddSupplierPaymentExpenseAsync(
        BongoTexDbContext db,
        string supplierName,
        decimal amount,
        DateTime expenseDateUtc,
        string description)
    {
        var cashErr = await DailyCashBalanceReport.ValidateExpenseCashAsync(
            db, "Factory", "SupplierPayment", null, amount, expenseDateUtc);
        if (cashErr is not null)
            return cashErr;

        var entry = new ExpenseEntry
        {
            ExpenseNo = FinanceConventions.NewExpenseNo(),
            Category = "SupplierPayment",
            PartyName = supplierName,
            ExpenseScope = "Factory",
            SiteId = null,
            Amount = amount,
            Description = description.Length > 250 ? description[..250] : description,
            ExpenseDateUtc = expenseDateUtc
        };
        ManagerCashbook.AssignCashbookForNewEntry(entry, "SupplierPayment", supplierName, "Factory", "");
        db.ExpenseEntries.Add(entry);
        return null;
    }
}

internal static class PrintFactoryConventions
{
    public const string ExpenseScope = "PrintFactory";
    /// <summary>Virtual key for print-factory monthly rent in SiteMonthlyRents (not a physical site).</summary>
    public static readonly Guid RentRegistryId = new("a1b2c3d4-e5f6-4789-a012-3456789abcde");
    public const string InternalBuyerName = "BongoTex Garments";
    public const string BuyerTypeInternal = "Internal";
    public const string BuyerTypeExternal = "External";

    public static string NewPurchaseVoucherNo() =>
        $"PF-PUR-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Random.Shared.Next(100, 999)}";

    public static string NewSaleVoucherNo() =>
        $"PF-SAL-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Random.Shared.Next(100, 999)}";

    public static string NewPaymentVoucherNo() =>
        $"PF-PAY-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Random.Shared.Next(100, 999)}";

    public static string NewReceiptVoucherNo() =>
        $"PF-RCV-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Random.Shared.Next(100, 999)}";

    public static string NormalizeBuyerType(string? buyerType)
    {
        var t = (buyerType ?? string.Empty).Trim();
        if (t.Equals(BuyerTypeExternal, StringComparison.OrdinalIgnoreCase))
            return BuyerTypeExternal;
        return BuyerTypeInternal;
    }

    public static string ResolveBuyerName(string buyerType, string? buyerName)
    {
        if (buyerType == BuyerTypeInternal)
            return InternalBuyerName;
        var n = (buyerName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(n) ? "External buyer" : n;
    }
}

public sealed class PrintFactoryLineRequest
{
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class CreatePrintFactoryPurchaseRequest
{
    public Guid? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public string? InvoiceRef { get; set; }
    public string? Description { get; set; }
    public DateTime? PurchasedAtUtc { get; set; }
    public decimal? PaidAmount { get; set; }
    public bool? CashPurchase { get; set; }
    public decimal? TotalAmount { get; set; }
    public List<PrintFactoryLineRequest>? Lines { get; set; }
}

public sealed class CreatePrintFactorySaleRequest
{
    public string? BuyerType { get; set; }
    public string? BuyerName { get; set; }
    public string? InvoiceRef { get; set; }
    public string? Description { get; set; }
    public DateTime? SoldAtUtc { get; set; }
    public decimal? ReceivedAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public List<PrintFactoryLineRequest>? Lines { get; set; }
}

public sealed class PrintFactoryPayRequest
{
    public decimal Amount { get; set; }
    public DateTime? PaidAtUtc { get; set; }
    public string? Note { get; set; }
}

public sealed class PrintFactoryCollectRequest
{
    public decimal Amount { get; set; }
    public DateTime? CollectedAtUtc { get; set; }
    public string? Note { get; set; }
}

public sealed class PrintFactoryStocktakeLineRequest
{
    public string? Description { get; set; }
    public string? Unit { get; set; }
    public decimal ClosingQuantity { get; set; }
    public decimal ClosingValue { get; set; }
}

public sealed class SavePrintFactoryStocktakeRequest
{
    public string? MonthKey { get; set; }
    public List<PrintFactoryStocktakeLineRequest>? Lines { get; set; }
}

internal static class RawMaterialScrapSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _ready;

    public static async Task EnsureAsync(BongoTexDbContext db, CancellationToken cancellationToken = default)
    {
        if (_ready) return;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return;
            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH('RawMaterialMovements', 'ScrapSaleId') IS NULL
                    ALTER TABLE RawMaterialMovements ADD ScrapSaleId uniqueidentifier NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawMaterialMovements_ScrapSaleId' AND object_id = OBJECT_ID('RawMaterialMovements'))
                    CREATE INDEX IX_RawMaterialMovements_ScrapSaleId ON RawMaterialMovements(ScrapSaleId);

                IF OBJECT_ID('RawMaterialScrapSales', 'U') IS NULL
                BEGIN
                    CREATE TABLE RawMaterialScrapSales
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        SaleNo nvarchar(40) NOT NULL,
                        SiteId uniqueidentifier NOT NULL,
                        RawMaterialId uniqueidentifier NOT NULL,
                        ScrapType nvarchar(20) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_ScrapType DEFAULT('Wastage'),
                        Quantity decimal(18,4) NOT NULL,
                        Unit nvarchar(20) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Unit DEFAULT('kg'),
                        UnitRate decimal(18,4) NOT NULL,
                        TotalAmount decimal(18,2) NOT NULL,
                        BuyerName nvarchar(120) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Buyer DEFAULT(''),
                        IsCredit bit NOT NULL CONSTRAINT DF_RawMaterialScrapSales_IsCredit DEFAULT(0),
                        PaidAmount decimal(18,2) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Paid DEFAULT(0),
                        DueAmount decimal(18,2) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Due DEFAULT(0),
                        Note nvarchar(250) NOT NULL CONSTRAINT DF_RawMaterialScrapSales_Note DEFAULT(''),
                        SoldAtUtc datetime2 NOT NULL,
                        CreatedAtUtc datetime2 NOT NULL
                    );
                    CREATE UNIQUE INDEX IX_RawMaterialScrapSales_SaleNo ON RawMaterialScrapSales(SaleNo);
                    CREATE INDEX IX_RawMaterialScrapSales_SoldAtUtc ON RawMaterialScrapSales(SoldAtUtc);
                    CREATE INDEX IX_RawMaterialScrapSales_SiteId ON RawMaterialScrapSales(SiteId);
                END
                IF COL_LENGTH('RawMaterialScrapSales', 'InventoryItemId') IS NULL
                    ALTER TABLE RawMaterialScrapSales ADD InventoryItemId uniqueidentifier NULL;
                IF COL_LENGTH('RawMaterialScrapSales', 'RawMaterialId') IS NOT NULL
                    ALTER TABLE RawMaterialScrapSales ALTER COLUMN RawMaterialId uniqueidentifier NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawMaterialScrapSales_InventoryItemId' AND object_id = OBJECT_ID('RawMaterialScrapSales'))
                    CREATE INDEX IX_RawMaterialScrapSales_InventoryItemId ON RawMaterialScrapSales(InventoryItemId);
                """, cancellationToken);
            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}

internal static class PrintFactorySchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _ready;

    public static async Task EnsureAsync(BongoTexDbContext db, CancellationToken cancellationToken = default)
    {
        if (_ready) return;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return;
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID('PrintFactoryPurchases', 'U') IS NULL
                BEGIN
                    CREATE TABLE PrintFactoryPurchases
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        VoucherNo nvarchar(50) NOT NULL,
                        SupplierId uniqueidentifier NOT NULL,
                        SupplierName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Purch_SupplierName DEFAULT(''),
                        InvoiceRef nvarchar(80) NOT NULL CONSTRAINT DF_PF_Purch_InvoiceRef DEFAULT(''),
                        Description nvarchar(250) NOT NULL CONSTRAINT DF_PF_Purch_Desc DEFAULT(''),
                        TotalAmount decimal(18,2) NOT NULL,
                        PaidAmount decimal(18,2) NOT NULL CONSTRAINT DF_PF_Purch_Paid DEFAULT(0),
                        DueAmount decimal(18,2) NOT NULL,
                        PurchasedAtUtc datetime2 NOT NULL,
                        CreatedAtUtc datetime2 NOT NULL
                    );
                    CREATE UNIQUE INDEX IX_PrintFactoryPurchases_VoucherNo ON PrintFactoryPurchases(VoucherNo);
                    CREATE INDEX IX_PrintFactoryPurchases_PurchasedAtUtc ON PrintFactoryPurchases(PurchasedAtUtc);
                END
                IF OBJECT_ID('PrintFactoryPurchaseLines', 'U') IS NULL
                BEGIN
                    CREATE TABLE PrintFactoryPurchaseLines
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        PrintFactoryPurchaseId uniqueidentifier NOT NULL,
                        Description nvarchar(200) NOT NULL,
                        Quantity decimal(18,4) NOT NULL,
                        Unit nvarchar(20) NOT NULL CONSTRAINT DF_PF_PurchLine_Unit DEFAULT('pcs'),
                        UnitCost decimal(18,4) NOT NULL,
                        LineTotal decimal(18,2) NOT NULL
                    );
                    CREATE INDEX IX_PrintFactoryPurchaseLines_PurchaseId ON PrintFactoryPurchaseLines(PrintFactoryPurchaseId);
                END
                IF OBJECT_ID('PrintFactorySales', 'U') IS NULL
                BEGIN
                    CREATE TABLE PrintFactorySales
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        VoucherNo nvarchar(50) NOT NULL,
                        BuyerType nvarchar(20) NOT NULL CONSTRAINT DF_PF_Sale_BuyerType DEFAULT('Internal'),
                        BuyerName nvarchar(200) NOT NULL,
                        InvoiceRef nvarchar(80) NOT NULL CONSTRAINT DF_PF_Sale_InvoiceRef DEFAULT(''),
                        Description nvarchar(250) NOT NULL CONSTRAINT DF_PF_Sale_Desc DEFAULT(''),
                        TotalAmount decimal(18,2) NOT NULL,
                        ReceivedAmount decimal(18,2) NOT NULL CONSTRAINT DF_PF_Sale_Received DEFAULT(0),
                        DueAmount decimal(18,2) NOT NULL,
                        SoldAtUtc datetime2 NOT NULL,
                        CreatedAtUtc datetime2 NOT NULL
                    );
                    CREATE UNIQUE INDEX IX_PrintFactorySales_VoucherNo ON PrintFactorySales(VoucherNo);
                    CREATE INDEX IX_PrintFactorySales_SoldAtUtc ON PrintFactorySales(SoldAtUtc);
                END
                IF OBJECT_ID('PrintFactorySaleLines', 'U') IS NULL
                BEGIN
                    CREATE TABLE PrintFactorySaleLines
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        PrintFactorySaleId uniqueidentifier NOT NULL,
                        Description nvarchar(200) NOT NULL,
                        Quantity decimal(18,4) NOT NULL,
                        Unit nvarchar(20) NOT NULL CONSTRAINT DF_PF_SaleLine_Unit DEFAULT('pcs'),
                        UnitRate decimal(18,4) NOT NULL,
                        LineTotal decimal(18,2) NOT NULL
                    );
                    CREATE INDEX IX_PrintFactorySaleLines_SaleId ON PrintFactorySaleLines(PrintFactorySaleId);
                END
                IF OBJECT_ID('PrintFactoryCashEntries', 'U') IS NULL
                BEGIN
                    CREATE TABLE PrintFactoryCashEntries
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        VoucherNo nvarchar(50) NOT NULL,
                        EntryType nvarchar(20) NOT NULL,
                        PurchaseId uniqueidentifier NULL,
                        SaleId uniqueidentifier NULL,
                        PartyName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Cash_Party DEFAULT(''),
                        Amount decimal(18,2) NOT NULL,
                        Note nvarchar(250) NOT NULL CONSTRAINT DF_PF_Cash_Note DEFAULT(''),
                        EntryDateUtc datetime2 NOT NULL,
                        ExpenseEntryId uniqueidentifier NULL
                    );
                    CREATE UNIQUE INDEX IX_PrintFactoryCashEntries_VoucherNo ON PrintFactoryCashEntries(VoucherNo);
                    CREATE INDEX IX_PrintFactoryCashEntries_EntryDateUtc ON PrintFactoryCashEntries(EntryDateUtc);
                END
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID('PrintFactoryPurchases', 'U') IS NOT NULL
                   AND COL_LENGTH('PrintFactoryPurchases', 'SupplierName') IS NULL
                    ALTER TABLE PrintFactoryPurchases ADD SupplierName nvarchar(200) NOT NULL CONSTRAINT DF_PF_Purch_SupplierName2 DEFAULT('');
                IF OBJECT_ID('PrintFactoryMonthStockLines', 'U') IS NULL
                BEGIN
                    CREATE TABLE PrintFactoryMonthStockLines
                    (
                        Id uniqueidentifier NOT NULL PRIMARY KEY,
                        MonthKey nvarchar(7) NOT NULL,
                        ItemDescription nvarchar(200) NOT NULL,
                        Unit nvarchar(20) NOT NULL CONSTRAINT DF_PF_MStock_Unit DEFAULT('pcs'),
                        ClosingQuantity decimal(18,4) NOT NULL CONSTRAINT DF_PF_MStock_Qty DEFAULT(0),
                        ClosingValue decimal(18,2) NOT NULL CONSTRAINT DF_PF_MStock_Val DEFAULT(0),
                        UpdatedAtUtc datetime2 NOT NULL
                    );
                    CREATE INDEX IX_PrintFactoryMonthStockLines_MonthKey ON PrintFactoryMonthStockLines(MonthKey);
                END
                """, cancellationToken);
            _ = await db.PrintFactoryPurchases.CountAsync(cancellationToken);
            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}

internal static class PrintFactoryOps
{
    public sealed record PurchaseResult(PrintFactoryPurchase? Purchase, string? Error);
    public sealed record SaleResult(PrintFactorySale? Sale, string? Error);
    public sealed record PayCollectResult(object? Payload, string? Error);

    public sealed record CategoryAmount(string Category, decimal Amount);

    public sealed record PrintFactorySummaryResult(
        decimal SalesTotal,
        decimal SalesInternal,
        decimal SalesExternal,
        decimal SalesDue,
        decimal PurchaseTotal,
        decimal PurchaseDue,
        decimal ExpenseTotal,
        decimal SalaryTotal,
        decimal RentTotal,
        decimal DailyExpenseTotal,
        decimal SupplierPaymentTotal,
        decimal OtherExpenseTotal,
        decimal NetProfitLoss,
        IReadOnlyList<CategoryAmount> ExpenseByCategory,
        string? StocktakeMonthKey,
        decimal OpeningStockValue,
        decimal ClosingStockValue,
        decimal MaterialCostUsed,
        decimal OperatingExpenses,
        decimal? AdjustedNetProfitLoss,
        bool HasClosingStocktake);

    public static string? TryResolveStocktakeMonthKey(DateTime fromUtc, DateTime toExclusive)
    {
        if (toExclusive <= fromUtc)
            return null;
        var lastInclusive = toExclusive.AddDays(-1);
        return $"{lastInclusive.Year:D4}-{lastInclusive.Month:D2}";
    }

    public static string PreviousMonthKey(string monthKey)
    {
        if (!DateTime.TryParse($"{monthKey}-01", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            return string.Empty;
        return d.AddMonths(-1).ToString("yyyy-MM");
    }

    public static async Task<decimal> SumClosingStockValueAsync(BongoTexDbContext db, string monthKey) =>
        await db.PrintFactoryMonthStockLines.AsNoTracking()
            .Where(x => x.MonthKey == monthKey)
            .SumAsync(x => (decimal?)x.ClosingValue) ?? 0m;

    public static async Task<object> GetStocktakeAsync(BongoTexDbContext db, string monthKey)
    {
        var mk = monthKey.Trim();
        if (!DateTime.TryParse($"{mk}-01", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var monthStart))
            throw new ArgumentException("Month must be yyyy-MM.", nameof(monthKey));

        var monthEnd = monthStart.AddMonths(1);
        var prevKey = PreviousMonthKey(mk);
        var opening = string.IsNullOrEmpty(prevKey) ? 0m : await SumClosingStockValueAsync(db, prevKey);

        var purchaseTotal = await db.PrintFactoryPurchases.AsNoTracking()
            .Where(x => x.PurchasedAtUtc >= monthStart && x.PurchasedAtUtc < monthEnd)
            .SumAsync(x => (decimal?)x.TotalAmount) ?? 0m;

        var purchaseIds = await db.PrintFactoryPurchases.AsNoTracking()
            .Where(x => x.PurchasedAtUtc >= monthStart && x.PurchasedAtUtc < monthEnd)
            .Select(x => x.Id)
            .ToListAsync();

        var purchaseLineHints = purchaseIds.Count == 0
            ? []
            : (await db.PrintFactoryPurchaseLines.AsNoTracking()
                .Where(l => purchaseIds.Contains(l.PrintFactoryPurchaseId))
                .ToListAsync())
                .GroupBy(l => new { Desc = l.Description.Trim(), l.Unit })
                .Select(g => new
                {
                    Description = g.Key.Desc,
                    Unit = g.Key.Unit,
                    PurchasedQuantity = g.Sum(x => x.Quantity),
                    PurchasedValue = g.Sum(x => x.LineTotal)
                })
                .OrderBy(x => x.Description)
                .ToList();

        var lines = await db.PrintFactoryMonthStockLines.AsNoTracking()
            .Where(x => x.MonthKey == mk)
            .OrderBy(x => x.ItemDescription)
            .Select(x => new
            {
                x.Id,
                x.ItemDescription,
                x.Unit,
                x.ClosingQuantity,
                x.ClosingValue
            })
            .ToListAsync();

        var closing = lines.Sum(x => x.ClosingValue);
        var materialUsed = opening + purchaseTotal - closing;
        var hasClosing = lines.Count > 0;

        return new
        {
            MonthKey = mk,
            PreviousMonthKey = prevKey,
            OpeningStockValue = opening,
            PurchaseTotal = purchaseTotal,
            ClosingStockValue = closing,
            MaterialCostUsed = materialUsed,
            HasClosingStocktake = hasClosing,
            Lines = lines,
            PurchaseHints = purchaseLineHints
        };
    }

    public static async Task<(object? Payload, string? Error)> SaveStocktakeAsync(
        BongoTexDbContext db,
        SavePrintFactoryStocktakeRequest req)
    {
        var mk = (req.MonthKey ?? string.Empty).Trim();
        if (mk.Length != 7 || !DateTime.TryParse($"{mk}-01", out _))
            return (null, "Month must be yyyy-MM.");

        var rawLines = (req.Lines ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l.Description))
            .ToList();
        if (rawLines.Count == 0)
            return (null, "Add at least one stock line with item description and closing value.");

        foreach (var line in rawLines)
        {
            if (line.ClosingValue < 0 || line.ClosingQuantity < 0)
                return (null, "Closing quantity and value cannot be negative.");
        }

        var existing = await db.PrintFactoryMonthStockLines.Where(x => x.MonthKey == mk).ToListAsync();
        if (existing.Count > 0)
            db.PrintFactoryMonthStockLines.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var line in rawLines)
        {
            db.PrintFactoryMonthStockLines.Add(new PrintFactoryMonthStockLine
            {
                MonthKey = mk,
                ItemDescription = line.Description!.Trim(),
                Unit = string.IsNullOrWhiteSpace(line.Unit) ? "pcs" : line.Unit.Trim(),
                ClosingQuantity = line.ClosingQuantity,
                ClosingValue = line.ClosingValue,
                UpdatedAtUtc = now
            });
        }

        await db.SaveChangesAsync();
        return (await GetStocktakeAsync(db, mk), null);
    }

    public static async Task<PrintFactorySummaryResult> GetSummaryAsync(BongoTexDbContext db, DateTime fromUtc, DateTime toExclusive)
    {
        var purchases = await db.PrintFactoryPurchases.AsNoTracking()
            .Where(x => x.PurchasedAtUtc >= fromUtc && x.PurchasedAtUtc < toExclusive)
            .ToListAsync();
        var sales = await db.PrintFactorySales.AsNoTracking()
            .Where(x => x.SoldAtUtc >= fromUtc && x.SoldAtUtc < toExclusive)
            .ToListAsync();
        var expenses = await db.ExpenseEntries.AsNoTracking()
            .Where(x => x.ExpenseScope == PrintFactoryConventions.ExpenseScope
                && x.ExpenseDateUtc >= fromUtc && x.ExpenseDateUtc < toExclusive)
            .ToListAsync();

        var purchaseTotal = purchases.Sum(x => x.TotalAmount);
        var salesTotal = sales.Sum(x => x.TotalAmount);
        var expenseTotal = expenses.Sum(x => x.Amount);
        var salary = expenses.Where(x => x.Category == "Salary").Sum(x => x.Amount);
        var rent = expenses.Where(x => x.Category == "Rent").Sum(x => x.Amount);
        var daily = expenses.Where(x => x.Category == "DailyExpense").Sum(x => x.Amount);
        var supplierPay = expenses.Where(x => x.Category == "SupplierPayment").Sum(x => x.Amount);
        var otherExp = expenseTotal - salary - rent - daily - supplierPay;
        var operatingExpenses = salary + rent + daily + otherExp;

        var stocktakeMonth = TryResolveStocktakeMonthKey(fromUtc, toExclusive);
        decimal openingStock = 0m;
        decimal closingStock = 0m;
        var hasClosingStocktake = false;
        if (!string.IsNullOrEmpty(stocktakeMonth))
        {
            var prevKey = PreviousMonthKey(stocktakeMonth);
            if (!string.IsNullOrEmpty(prevKey))
                openingStock = await SumClosingStockValueAsync(db, prevKey);
            closingStock = await SumClosingStockValueAsync(db, stocktakeMonth);
            hasClosingStocktake = await db.PrintFactoryMonthStockLines.AsNoTracking()
                .AnyAsync(x => x.MonthKey == stocktakeMonth);
        }

        var materialCostUsed = openingStock + purchaseTotal - closingStock;
        decimal? adjustedNet = hasClosingStocktake
            ? salesTotal - materialCostUsed - operatingExpenses
            : null;

        return new PrintFactorySummaryResult(
            salesTotal,
            sales.Where(x => x.BuyerType == PrintFactoryConventions.BuyerTypeInternal).Sum(x => x.TotalAmount),
            sales.Where(x => x.BuyerType == PrintFactoryConventions.BuyerTypeExternal).Sum(x => x.TotalAmount),
            sales.Sum(x => x.DueAmount),
            purchaseTotal,
            purchases.Sum(x => x.DueAmount),
            expenseTotal,
            salary,
            rent,
            daily,
            supplierPay,
            otherExp,
            salesTotal - purchaseTotal - expenseTotal,
            expenses.GroupBy(x => x.Category)
                .Select(g => new CategoryAmount(g.Key, g.Sum(x => x.Amount)))
                .OrderByDescending(x => x.Amount)
                .ToList(),
            stocktakeMonth,
            openingStock,
            closingStock,
            materialCostUsed,
            operatingExpenses,
            adjustedNet,
            hasClosingStocktake);
    }

    public static async Task<PurchaseResult> CreatePurchaseAsync(BongoTexDbContext db, CreatePrintFactoryPurchaseRequest req)
    {
        string supplierName;
        Guid supplierId;
        if (req.CashPurchase == true)
        {
            supplierName = (req.SupplierName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(supplierName))
                return new(null, "Enter supplier name for cash purchase.");
            supplierId = Guid.Empty;
        }
        else
        {
            if (req.SupplierId is not { } sid || sid == Guid.Empty)
                return new(null, "Select a supplier.");
            var supplier = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == sid);
            if (supplier is null)
                return new(null, "Supplier not found.");
            if (!SupplierConventions.IsPrintFactorySupplier(supplier.Category))
                return new(null, "Select a Print Factory supplier (Registration → Print factory supplier). Garment print categories (In-house / Outside) are not used here.");
            supplierId = supplier.Id;
            supplierName = supplier.Name;
        }

        var (total, purchaseLines, err) = BuildPurchaseLines(req.Lines, req.TotalAmount);
        if (err is not null)
            return new(null, err);

        var paid = Math.Max(0, req.PaidAmount ?? 0);
        if (req.CashPurchase == true)
            paid = total;
        if (paid > total)
            return new(null, "Paid amount cannot exceed bill total.");

        var purchasedAt = req.PurchasedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var purchase = new PrintFactoryPurchase
        {
            VoucherNo = PrintFactoryConventions.NewPurchaseVoucherNo(),
            SupplierId = supplierId,
            SupplierName = supplierName,
            InvoiceRef = (req.InvoiceRef ?? string.Empty).Trim(),
            Description = (req.Description ?? string.Empty).Trim(),
            TotalAmount = total,
            PaidAmount = paid,
            DueAmount = total - paid,
            PurchasedAtUtc = purchasedAt,
            CreatedAtUtc = DateTime.UtcNow
        };
        foreach (var line in purchaseLines)
            line.PrintFactoryPurchaseId = purchase.Id;
        purchase.Lines = purchaseLines;

        db.PrintFactoryPurchases.Add(purchase);

        if (paid > 0)
        {
            var cashErr = await DailyCashBalanceReport.ValidateExpenseCashAsync(
                db, PrintFactoryConventions.ExpenseScope, "SupplierPayment", null, paid, purchasedAt);
            if (cashErr is not null)
                return new(null, cashErr);

            var payNote = req.CashPurchase == true
                ? "Cash purchase — paid with purchase voucher"
                : "Paid with purchase voucher";
            AddPurchasePaymentEntities(db, purchase, supplierName, paid, purchasedAt, payNote);
        }

        await db.SaveChangesAsync();
        return new(purchase, null);
    }

    public static async Task<SaleResult> CreateSaleAsync(BongoTexDbContext db, CreatePrintFactorySaleRequest req)
    {
        var buyerType = PrintFactoryConventions.NormalizeBuyerType(req.BuyerType);
        var buyerName = PrintFactoryConventions.ResolveBuyerName(buyerType, req.BuyerName);

        var (total, saleLines, err) = BuildSaleLines(req.Lines, req.TotalAmount);
        if (err is not null)
            return new(null, err);

        var received = Math.Max(0, req.ReceivedAmount ?? 0);
        if (received > total)
            return new(null, "Received amount cannot exceed bill total.");

        var soldAt = req.SoldAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var sale = new PrintFactorySale
        {
            VoucherNo = PrintFactoryConventions.NewSaleVoucherNo(),
            BuyerType = buyerType,
            BuyerName = buyerName,
            InvoiceRef = (req.InvoiceRef ?? string.Empty).Trim(),
            Description = (req.Description ?? string.Empty).Trim(),
            TotalAmount = total,
            ReceivedAmount = received,
            DueAmount = total - received,
            SoldAtUtc = soldAt,
            CreatedAtUtc = DateTime.UtcNow
        };
        foreach (var line in saleLines)
            line.PrintFactorySaleId = sale.Id;
        sale.Lines = saleLines;

        db.PrintFactorySales.Add(sale);

        if (received > 0)
            AddSaleReceiptEntity(db, sale, buyerName, received, soldAt, "Received with sales voucher");

        await db.SaveChangesAsync();
        return new(sale, null);
    }

    public static async Task<PayCollectResult> PayPurchaseAsync(BongoTexDbContext db, Guid id, PrintFactoryPayRequest req)
    {
        var purchase = await db.PrintFactoryPurchases.FirstOrDefaultAsync(x => x.Id == id);
        if (purchase is null)
            return new(null, "Purchase voucher not found.");
        if (req.Amount <= 0)
            return new(null, "Payment amount must be greater than zero.");
        if (req.Amount > purchase.DueAmount)
            return new(null, $"Payment exceeds due balance ({purchase.DueAmount:0.##}).");

        var supplier = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == purchase.SupplierId);
        var partyName = ResolvePurchaseSupplierName(purchase, supplier);

        var payDate = req.PaidAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var cashErr = await DailyCashBalanceReport.ValidateExpenseCashAsync(
            db, PrintFactoryConventions.ExpenseScope, "SupplierPayment", null, req.Amount, payDate);
        if (cashErr is not null)
            return new(null, cashErr);

        AddPurchasePaymentEntities(
            db, purchase, partyName, req.Amount, payDate,
            $"Payment on {purchase.VoucherNo}" + (string.IsNullOrWhiteSpace(req.Note) ? "" : $" — {req.Note.Trim()}"));

        purchase.PaidAmount += req.Amount;
        purchase.DueAmount = purchase.TotalAmount - purchase.PaidAmount;
        await db.SaveChangesAsync();
        return new(new { purchase.Id, purchase.VoucherNo, purchase.PaidAmount, purchase.DueAmount }, null);
    }

    public static async Task<PayCollectResult> CollectSaleAsync(BongoTexDbContext db, Guid id, PrintFactoryCollectRequest req)
    {
        var sale = await db.PrintFactorySales.FirstOrDefaultAsync(x => x.Id == id);
        if (sale is null)
            return new(null, "Sales voucher not found.");
        if (req.Amount <= 0)
            return new(null, "Collection amount must be greater than zero.");
        if (req.Amount > sale.DueAmount)
            return new(null, $"Collection exceeds due balance ({sale.DueAmount:0.##}).");

        var collectDate = req.CollectedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        AddSaleReceiptEntity(
            db, sale, sale.BuyerName, req.Amount, collectDate,
            $"Receipt on {sale.VoucherNo}" + (string.IsNullOrWhiteSpace(req.Note) ? "" : $" — {req.Note.Trim()}"));

        sale.ReceivedAmount += req.Amount;
        sale.DueAmount = sale.TotalAmount - sale.ReceivedAmount;
        await db.SaveChangesAsync();
        return new(new { sale.Id, sale.VoucherNo, sale.ReceivedAmount, sale.DueAmount }, null);
    }

    public static async Task<object> ListPurchasesAsync(BongoTexDbContext db, string? month, Guid? supplierId)
    {
        var query = db.PrintFactoryPurchases.AsNoTracking();
        if (supplierId is { } sid && sid != Guid.Empty)
            query = query.Where(x => x.SupplierId == sid);
        if (!string.IsNullOrWhiteSpace(month) && DateTime.TryParse($"{month}-01", out var m))
        {
            var start = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            query = query.Where(x => x.PurchasedAtUtc >= start && x.PurchasedAtUtc < start.AddMonths(1));
        }

        var rows = await query.OrderByDescending(x => x.PurchasedAtUtc).ToListAsync();
        var supplierIds = rows.Select(r => r.SupplierId).Where(id => id != Guid.Empty).Distinct().ToList();
        var supplierMap = supplierIds.Count == 0
            ? new Dictionary<Guid, Supplier>()
            : await db.Suppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);

        return new
        {
            TotalAmount = rows.Sum(x => x.TotalAmount),
            TotalDue = rows.Sum(x => x.DueAmount),
            Rows = rows.Select(p =>
            {
                supplierMap.TryGetValue(p.SupplierId, out var sup);
                return new
                {
                    p.Id,
                    p.VoucherNo,
                    p.SupplierId,
                    SupplierName = ResolvePurchaseSupplierName(p, sup),
                    p.InvoiceRef,
                    p.Description,
                    p.TotalAmount,
                    p.PaidAmount,
                    p.DueAmount,
                    p.PurchasedAtUtc
                };
            }).ToList()
        };
    }

    public static async Task<object?> GetPurchaseAsync(BongoTexDbContext db, Guid id)
    {
        var p = await db.PrintFactoryPurchases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;
        var lines = await db.PrintFactoryPurchaseLines.AsNoTracking()
            .Where(x => x.PrintFactoryPurchaseId == id).ToListAsync();
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.SupplierId);
        var payments = await db.PrintFactoryCashEntries.AsNoTracking()
            .Where(x => x.PurchaseId == id && x.EntryType == "Payment")
            .OrderBy(x => x.EntryDateUtc).ToListAsync();
        return new
        {
            p.Id,
            p.VoucherNo,
            DocumentType = "Purchase",
            p.SupplierId,
            SupplierName = ResolvePurchaseSupplierName(p, supplier),
            p.InvoiceRef,
            p.Description,
            p.TotalAmount,
            p.PaidAmount,
            p.DueAmount,
            p.PurchasedAtUtc,
            Lines = lines.Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitCost, l.LineTotal }),
            Payments = payments.Select(x => new { x.VoucherNo, x.Amount, x.EntryDateUtc, x.Note })
        };
    }

    public static async Task<object> ListSalesAsync(BongoTexDbContext db, string? month, string? buyerType)
    {
        var query = db.PrintFactorySales.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(buyerType))
        {
            var bt = PrintFactoryConventions.NormalizeBuyerType(buyerType);
            query = query.Where(x => x.BuyerType == bt);
        }
        if (!string.IsNullOrWhiteSpace(month) && DateTime.TryParse($"{month}-01", out var m))
        {
            var start = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            query = query.Where(x => x.SoldAtUtc >= start && x.SoldAtUtc < start.AddMonths(1));
        }

        var rows = await query.OrderByDescending(x => x.SoldAtUtc).ToListAsync();
        return new
        {
            TotalAmount = rows.Sum(x => x.TotalAmount),
            TotalDue = rows.Sum(x => x.DueAmount),
            Rows = rows.Select(s => new
            {
                s.Id,
                s.VoucherNo,
                s.BuyerType,
                s.BuyerName,
                s.InvoiceRef,
                s.Description,
                s.TotalAmount,
                s.ReceivedAmount,
                s.DueAmount,
                s.SoldAtUtc
            }).ToList()
        };
    }

    public static async Task<object?> GetSaleAsync(BongoTexDbContext db, Guid id)
    {
        var s = await db.PrintFactorySales.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return null;
        var lines = await db.PrintFactorySaleLines.AsNoTracking()
            .Where(x => x.PrintFactorySaleId == id).ToListAsync();
        var receipts = await db.PrintFactoryCashEntries.AsNoTracking()
            .Where(x => x.SaleId == id && x.EntryType == "Receipt")
            .OrderBy(x => x.EntryDateUtc).ToListAsync();
        return new
        {
            s.Id,
            s.VoucherNo,
            DocumentType = "Sale",
            s.BuyerType,
            s.BuyerName,
            s.InvoiceRef,
            s.Description,
            s.TotalAmount,
            s.ReceivedAmount,
            s.DueAmount,
            s.SoldAtUtc,
            Lines = lines.Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitRate, l.LineTotal }),
            Receipts = receipts.Select(x => new { x.VoucherNo, x.Amount, x.EntryDateUtc, x.Note })
        };
    }

    private static (decimal Total, List<PrintFactoryPurchaseLine> Lines, string? Error) BuildPurchaseLines(
        List<PrintFactoryLineRequest>? lines,
        decimal? manualTotal)
    {
        var raw = (lines ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Description) && l.Quantity > 0).ToList();
        if (raw.Count == 0)
        {
            var total = manualTotal ?? 0;
            if (total <= 0)
                return (0, [], "Enter line items or a bill total.");
            return (total, [], null);
        }

        decimal sum = 0;
        var entities = new List<PrintFactoryPurchaseLine>();
        foreach (var line in raw)
        {
            if (line.UnitPrice <= 0)
                return (0, [], "Each line needs rate/cost greater than zero.");
            var lineTotal = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            sum += lineTotal;
            entities.Add(new PrintFactoryPurchaseLine
            {
                Description = line.Description!.Trim(),
                Quantity = line.Quantity,
                Unit = string.IsNullOrWhiteSpace(line.Unit) ? "pcs" : line.Unit.Trim(),
                UnitCost = line.UnitPrice,
                LineTotal = lineTotal
            });
        }

        if (manualTotal is { } mt && mt > 0 && Math.Abs(mt - sum) > 0.02m)
            return (0, [], "Line total does not match bill total.");
        return (sum, entities, null);
    }

    private static (decimal Total, List<PrintFactorySaleLine> Lines, string? Error) BuildSaleLines(
        List<PrintFactoryLineRequest>? lines,
        decimal? manualTotal)
    {
        var raw = (lines ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Description) && l.Quantity > 0).ToList();
        if (raw.Count == 0)
        {
            var total = manualTotal ?? 0;
            if (total <= 0)
                return (0, [], "Enter line items or a bill total.");
            return (total, [], null);
        }

        decimal sum = 0;
        var entities = new List<PrintFactorySaleLine>();
        foreach (var line in raw)
        {
            if (line.UnitPrice <= 0)
                return (0, [], "Each line needs rate/cost greater than zero.");
            var lineTotal = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            sum += lineTotal;
            entities.Add(new PrintFactorySaleLine
            {
                Description = line.Description!.Trim(),
                Quantity = line.Quantity,
                Unit = string.IsNullOrWhiteSpace(line.Unit) ? "pcs" : line.Unit.Trim(),
                UnitRate = line.UnitPrice,
                LineTotal = lineTotal
            });
        }

        if (manualTotal is { } mt && mt > 0 && Math.Abs(mt - sum) > 0.02m)
            return (0, [], "Line total does not match bill total.");
        return (sum, entities, null);
    }

    private static string ResolvePurchaseSupplierName(PrintFactoryPurchase purchase, Supplier? supplier)
        => !string.IsNullOrWhiteSpace(purchase.SupplierName)
            ? purchase.SupplierName
            : supplier?.Name ?? "";

    private static void AddPurchasePaymentEntities(
        BongoTexDbContext db,
        PrintFactoryPurchase purchase,
        string supplierName,
        decimal amount,
        DateTime payDate,
        string note)
    {
        var expense = new ExpenseEntry
        {
            ExpenseNo = PrintFactoryConventions.NewPaymentVoucherNo(),
            Category = "SupplierPayment",
            PartyName = supplierName,
            ExpenseScope = PrintFactoryConventions.ExpenseScope,
            Amount = amount,
            Description = note.Length > 250 ? note[..250] : note,
            ExpenseDateUtc = payDate
        };
        ManagerCashbook.AssignCashbookForNewEntry(expense, "SupplierPayment", supplierName, PrintFactoryConventions.ExpenseScope, "");
        db.ExpenseEntries.Add(expense);
        db.PrintFactoryCashEntries.Add(new PrintFactoryCashEntry
        {
            VoucherNo = expense.ExpenseNo,
            EntryType = "Payment",
            PurchaseId = purchase.Id,
            PartyName = supplierName,
            Amount = amount,
            Note = note,
            EntryDateUtc = payDate,
            ExpenseEntryId = expense.Id
        });
    }

    private static void AddSaleReceiptEntity(
        BongoTexDbContext db,
        PrintFactorySale sale,
        string buyerName,
        decimal amount,
        DateTime collectDate,
        string note)
    {
        db.PrintFactoryCashEntries.Add(new PrintFactoryCashEntry
        {
            VoucherNo = PrintFactoryConventions.NewReceiptVoucherNo(),
            EntryType = "Receipt",
            SaleId = sale.Id,
            PartyName = buyerName,
            Amount = amount,
            Note = note,
            EntryDateUtc = collectDate
        });
    }
}

public sealed class CreateRawMaterialRequest
{
    public string? Code { get; set; }
    /// <summary>With category, builds unique code e.g. FAB-03.</summary>
    public string? MemoNo { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Unit { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class UpdateRawMaterialRequest
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Unit { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class RawMaterialIssueRequest
{
    public Guid RawMaterialId { get; set; }
    public Guid SiteId { get; set; }
    public decimal Quantity { get; set; }
    public string? Note { get; set; }
    public DateTime? MovementDateUtc { get; set; }
    public string? CutLotCode { get; set; }
}

public sealed class RawMaterialAdjustRequest
{
    public Guid RawMaterialId { get; set; }
    public Guid SiteId { get; set; }
    /// <summary>Positive adds stock, negative removes.</summary>
    public decimal QuantityDelta { get; set; }
    public string? Note { get; set; }
    public DateTime? MovementDateUtc { get; set; }
}

public sealed class FixHyphenRawMaterialCodesRequest
{
    public string? InvoiceRef { get; set; }
}

public sealed class SplitMergedRawMaterialRequest
{
    public Guid SourceRawMaterialId { get; set; }
    public Guid SiteId { get; set; }
    public string? InvoiceRef { get; set; }
    public List<SplitMergedRawMaterialLineRequest>? Lines { get; set; }
}

public sealed class SplitMergedRawMaterialLineRequest
{
    public string? MemoNo { get; set; }
    public string? Name { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitCost { get; set; }
    public Guid? SourceMovementId { get; set; }
}

public sealed class CreateRawMaterialScrapSaleRequest
{
    public Guid SiteId { get; set; }
    public Guid? RawMaterialId { get; set; }
    public Guid? InventoryItemId { get; set; }
    public string? ScrapType { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitRate { get; set; }
    public string? BuyerName { get; set; }
    public bool IsCredit { get; set; }
    public decimal PaidAmount { get; set; }
    public string? Note { get; set; }
    public DateTime? SoldAtUtc { get; set; }
}

public sealed class CollectRawMaterialScrapSaleRequest
{
    public decimal Amount { get; set; }
}

internal static class RawMaterialRules
{
    public static readonly string[] Categories =
        ["Fabrics", "Yarn", "Collar", "Embroidery", SupplierConventions.PrintInHouse, SupplierConventions.PrintOutside, "Accessories", "Sewing Less Glue", "Dyeing", "Others"];

    public static readonly string[] Units = ["kg", "m", "pcs", "roll", "lot", "box"];

    public static string NormalizeCategory(string? category)
    {
        var c = (category ?? "Others").Trim();
        if (c.Equals(SupplierConventions.LegacyPrint, StringComparison.OrdinalIgnoreCase))
            return SupplierConventions.PrintOutside;
        return Categories.FirstOrDefault(x => x.Equals(c, StringComparison.OrdinalIgnoreCase)) ?? "Others";
    }

    public static string NormalizeUnit(string? unit)
    {
        var u = (unit ?? "kg").Trim().ToLowerInvariant();
        return Units.FirstOrDefault(x => x.Equals(u, StringComparison.OrdinalIgnoreCase)) ?? u;
    }

    public static string CodePrefixForCategory(string normalizedCategory) => normalizedCategory switch
    {
        "Fabrics" => "FAB",
        "Yarn" => "YRN",
        "Collar" => "CLR",
        "Embroidery" => "EMB",
        SupplierConventions.PrintInHouse => "PRI",
        SupplierConventions.PrintOutside => "PRO",
        "Accessories" => "ACC",
        "Sewing Less Glue" => "SLG",
        "Dyeing" => "DYE",
        _ => "OTH"
    };

    public static async Task<string> GenerateNextCodeAsync(BongoTexDbContext db, string? category)
    {
        var cat = NormalizeCategory(category);
        var prefix = CodePrefixForCategory(cat);
        var likePrefix = $"{prefix}-%";
        var existingCodes = await db.RawMaterials.AsNoTracking()
            .Where(x => EF.Functions.Like(x.Code, likePrefix))
            .Select(x => x.Code)
            .ToListAsync();

        var maxNumber = 0;
        foreach (var code in existingCodes)
        {
            var parts = code.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            if (!parts[0].Equals(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(parts[^1], out var n) && n > maxNumber)
                maxNumber = n;
        }

        return $"{prefix}-{(maxNumber + 1):D2}";
    }

    /// <summary>Builds material code from category prefix and user memo (e.g. Fabrics + 168-1 → FAB-168-1).</summary>
    public static string BuildCodeFromCategoryAndMemo(string? category, string? memoNo, out string? error)
    {
        error = null;
        var memo = (memoNo ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(memo))
        {
            error = "Memo number is required.";
            return string.Empty;
        }

        var safe = Regex.Replace(memo, @"[^a-zA-Z0-9\-]", "");
        safe = Regex.Replace(safe, @"\-+", "-").Trim('-');
        if (string.IsNullOrEmpty(safe))
        {
            error = "Memo number must contain letters or digits.";
            return string.Empty;
        }

        if (!safe.Contains('-') && int.TryParse(safe, out var n) && n >= 0)
            safe = n <= 99 ? n.ToString("D2", CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);

        var prefix = CodePrefixForCategory(NormalizeCategory(category));
        return $"{prefix}-{safe}";
    }

    /// <summary>Converts legacy concatenated codes (FAB-1681) to hyphenated (FAB-168-1) for a bill memo.</summary>
    public static string? TryLegacyCodeToHyphenated(string code, string? invoiceRef)
    {
        var inv = Regex.Replace((invoiceRef ?? string.Empty).Trim(), @"[^a-zA-Z0-9]", "");
        if (string.IsNullOrEmpty(inv))
            return null;
        var prefixes = new[]
        {
            "FAB", "YRN", "CLR", "EMB", "PRI", "PRO", "ACC", "SLG", "DYE", "OTH"
        };
        foreach (var prefix in prefixes)
        {
            var head = $"{prefix}-{inv}";
            if (!code.StartsWith(head, StringComparison.OrdinalIgnoreCase) || code.Length <= head.Length)
                continue;
            var tail = code[head.Length..];
            if (tail.Length > 0 && tail.All(char.IsDigit))
                return $"{prefix}-{inv}-{tail}";
        }
        return null;
    }

    /// <summary>Combines invoice memo with per-line memo (e.g. 168 + 1 → 168-1).</summary>
    public static string ResolveLineMemo(string? invoiceMemo, string? lineMemo)
    {
        var inv = (invoiceMemo ?? string.Empty).Trim();
        var line = (lineMemo ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(line))
            return inv;
        if (string.IsNullOrEmpty(inv))
            return line;
        var safeInv = Regex.Replace(inv, @"[^a-zA-Z0-9]", "");
        var safeLine = Regex.Replace(line, @"[^a-zA-Z0-9]", "");
        if (safeLine.StartsWith(safeInv, StringComparison.OrdinalIgnoreCase) && safeLine.Length > safeInv.Length)
            return line;
        return $"{inv}-{line}";
    }
}

internal static class RawMaterialOps
{
    public const string TypePurchase = "Purchase";
    public const string TypeIssue = "Issue";
    public const string TypeAdjust = "Adjust";
    public const string TypeSale = "Sale";

    public static bool CountsAsIssued(string movementType) =>
        movementType == TypeIssue || movementType == TypeSale;

    public static async Task EnsureFinishingEntryIdColumnAsync(BongoTexDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH('RawMaterialMovements', 'FinishingEntryId') IS NULL
                ALTER TABLE RawMaterialMovements ADD FinishingEntryId uniqueidentifier NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawMaterialMovements_FinishingEntryId' AND object_id = OBJECT_ID('RawMaterialMovements'))
                CREATE INDEX IX_RawMaterialMovements_FinishingEntryId ON RawMaterialMovements(FinishingEntryId);
            """);
    }

    public static async Task<RawMaterialStock> GetOrCreateStockAsync(BongoTexDbContext db, Guid materialId, Guid siteId)
    {
        var stock = await db.RawMaterialStocks.FirstOrDefaultAsync(x => x.RawMaterialId == materialId && x.SiteId == siteId);
        if (stock is not null) return stock;
        stock = new RawMaterialStock { RawMaterialId = materialId, SiteId = siteId, QuantityOnHand = 0 };
        db.RawMaterialStocks.Add(stock);
        return stock;
    }

    public static async Task<string?> ReceiveForPurchaseAsync(
        BongoTexDbContext db,
        Guid materialId,
        Guid siteId,
        decimal quantity,
        decimal unitCost,
        DateTime movementDateUtc,
        Guid purchaseId,
        string note)
    {
        if (quantity <= 0) return "Receipt quantity must be greater than zero.";
        // Include pending Added entities — new materials are not in the DB until SaveChanges.
        var material = db.RawMaterials.Local.FirstOrDefault(x => x.Id == materialId && x.IsActive)
            ?? await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == materialId && x.IsActive);
        if (material is null) return "Raw material not found or inactive.";
        return await ReceiveForPurchaseCoreAsync(db, material, siteId, quantity, unitCost, movementDateUtc, purchaseId, note);
    }

    static async Task<string?> ReceiveForPurchaseCoreAsync(
        BongoTexDbContext db,
        RawMaterial material,
        Guid siteId,
        decimal quantity,
        decimal unitCost,
        DateTime movementDateUtc,
        Guid? purchaseId,
        string note)
    {
        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == siteId);
        if (site is null) return "Site not found.";

        var stock = await GetOrCreateStockAsync(db, material.Id, siteId);
        stock.QuantityOnHand += quantity;
        var totalCost = decimal.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);
        db.RawMaterialMovements.Add(new RawMaterialMovement
        {
            MovementNo = $"RM-IN-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            RawMaterialId = material.Id,
            SiteId = siteId,
            MovementType = TypePurchase,
            Quantity = quantity,
            UnitCost = unitCost,
            TotalCost = totalCost,
            MovementDateUtc = movementDateUtc,
            Note = note.Length > 500 ? note[..500] : note,
            SupplierPurchaseId = purchaseId,
            CreatedAtUtc = DateTime.UtcNow
        });
        return null;
    }

    public static async Task<string?> IssueForCuttingAsync(
        BongoTexDbContext db,
        CuttingEntry cutting,
        Guid materialId,
        decimal quantity,
        string cutLotCode)
    {
        if (quantity <= 0) return "Issue quantity must be greater than zero.";
        var material = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == materialId && x.IsActive);
        if (material is null) return "Raw material not found or inactive.";
        var stock = await GetOrCreateStockAsync(db, materialId, cutting.FactorySiteId);
        if (stock.QuantityOnHand < quantity)
            return $"Insufficient {material.Code} stock at factory (on hand {stock.QuantityOnHand:0.####}, need {quantity:0.####} {material.Unit}).";

        stock.QuantityOnHand -= quantity;
        var unitCost = cutting.FabricPricePerKg;
        db.RawMaterialMovements.Add(new RawMaterialMovement
        {
            MovementNo = $"RM-OUT-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            RawMaterialId = materialId,
            SiteId = cutting.FactorySiteId,
            MovementType = TypeIssue,
            Quantity = quantity,
            UnitCost = unitCost,
            TotalCost = decimal.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero),
            MovementDateUtc = cutting.CutAtUtc,
            Note = $"Cutting {cutting.CuttingNo}",
            CuttingEntryId = cutting.Id,
            CutLotCode = cutLotCode ?? "",
            CreatedAtUtc = DateTime.UtcNow
        });
        return null;
    }

    public static async Task<string?> ReverseCuttingIssueAsync(BongoTexDbContext db, Guid cuttingEntryId)
    {
        var movements = await db.RawMaterialMovements
            .Where(x => x.CuttingEntryId == cuttingEntryId && x.MovementType == TypeIssue)
            .ToListAsync();
        foreach (var m in movements)
        {
            var stock = await GetOrCreateStockAsync(db, m.RawMaterialId, m.SiteId);
            stock.QuantityOnHand += m.Quantity;
            db.RawMaterialMovements.Remove(m);
        }
        return null;
    }

    public static async Task<string?> IssueForFinishingAsync(
        BongoTexDbContext db,
        FinishingEntry finishing,
        Guid materialId,
        decimal quantity,
        decimal? unitCost,
        string cutLotCode,
        string itemSku,
        int lineSequence = 1)
    {
        if (quantity <= 0)
            return "Material quantity must be greater than zero.";
        var material = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == materialId && x.IsActive);
        if (material is null)
            return "Raw material not found or inactive.";
        var stock = await GetOrCreateStockAsync(db, materialId, finishing.FactorySiteId);
        if (stock.QuantityOnHand < quantity)
            return $"Insufficient {material.Code} stock at factory (on hand {stock.QuantityOnHand:0.####}, need {quantity:0.####} {material.Unit}).";

        stock.QuantityOnHand -= quantity;
        var cost = unitCost is > 0 ? unitCost.Value : 0m;
        var lot = (cutLotCode ?? "").Trim();
        var sku = (itemSku ?? "").Trim();
        var note = $"Finishing {finishing.FinishingNo}";
        if (!string.IsNullOrEmpty(lot))
            note += $" / lot {lot}";
        if (!string.IsNullOrEmpty(sku))
            note += $" / {sku}";

        var movementNo = $"RM-FIN-{DateTime.UtcNow:yyyyMMddHHmmss}-{lineSequence:000}";
        if (movementNo.Length > 40)
            movementNo = movementNo[..40];

        db.RawMaterialMovements.Add(new RawMaterialMovement
        {
            MovementNo = movementNo,
            RawMaterialId = materialId,
            SiteId = finishing.FactorySiteId,
            MovementType = TypeIssue,
            Quantity = quantity,
            UnitCost = cost,
            TotalCost = decimal.Round(quantity * cost, 2, MidpointRounding.AwayFromZero),
            MovementDateUtc = finishing.FinishedAtUtc,
            Note = note,
            FinishingEntryId = finishing.Id,
            CutLotCode = lot,
            CreatedAtUtc = DateTime.UtcNow
        });
        return null;
    }

    public static async Task ReverseFinishingIssuesAsync(BongoTexDbContext db, Guid finishingEntryId)
    {
        var movements = await db.RawMaterialMovements
            .Where(x => x.FinishingEntryId == finishingEntryId && x.MovementType == TypeIssue)
            .ToListAsync();
        foreach (var m in movements)
        {
            var stock = await GetOrCreateStockAsync(db, m.RawMaterialId, m.SiteId);
            stock.QuantityOnHand += m.Quantity;
            db.RawMaterialMovements.Remove(m);
        }
    }

    public static async Task<string?> IssueManualAsync(BongoTexDbContext db, RawMaterialIssueRequest req)
    {
        if (req.RawMaterialId == Guid.Empty || req.SiteId == Guid.Empty)
            return "Material and site are required.";
        if (req.Quantity <= 0) return "Quantity must be greater than zero.";
        var material = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == req.RawMaterialId && x.IsActive);
        if (material is null) return "Raw material not found.";
        var stock = await GetOrCreateStockAsync(db, req.RawMaterialId, req.SiteId);
        if (stock.QuantityOnHand < req.Quantity)
            return $"Insufficient stock (on hand {stock.QuantityOnHand:0.####} {material.Unit}).";
        stock.QuantityOnHand -= req.Quantity;
        var at = req.MovementDateUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        db.RawMaterialMovements.Add(new RawMaterialMovement
        {
            MovementNo = $"RM-OUT-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            RawMaterialId = req.RawMaterialId,
            SiteId = req.SiteId,
            MovementType = TypeIssue,
            Quantity = req.Quantity,
            UnitCost = 0,
            TotalCost = 0,
            MovementDateUtc = at,
            Note = (req.Note ?? "Manual issue").Trim(),
            CutLotCode = (req.CutLotCode ?? "").Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
        return null;
    }

    public static async Task<string?> AdjustAsync(BongoTexDbContext db, RawMaterialAdjustRequest req)
    {
        if (req.RawMaterialId == Guid.Empty || req.SiteId == Guid.Empty)
            return "Material and site are required.";
        if (req.QuantityDelta == 0) return "Adjustment quantity cannot be zero.";
        if (!await db.RawMaterials.AnyAsync(x => x.Id == req.RawMaterialId))
            return "Raw material not found.";
        var stock = await GetOrCreateStockAsync(db, req.RawMaterialId, req.SiteId);
        var newQty = stock.QuantityOnHand + req.QuantityDelta;
        if (newQty < 0)
            return $"Adjustment would make stock negative (on hand {stock.QuantityOnHand:0.####}).";
        stock.QuantityOnHand = newQty;
        var at = req.MovementDateUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        db.RawMaterialMovements.Add(new RawMaterialMovement
        {
            MovementNo = $"RM-ADJ-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            RawMaterialId = req.RawMaterialId,
            SiteId = req.SiteId,
            MovementType = TypeAdjust,
            Quantity = req.QuantityDelta,
            UnitCost = 0,
            TotalCost = 0,
            MovementDateUtc = at,
            Note = (req.Note ?? "Stock adjustment").Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
        return null;
    }

    public static async Task<(object? Body, string? Error)> GetSplitPreviewAsync(
        BongoTexDbContext db,
        Guid materialId,
        Guid siteId,
        string? invoiceRef)
    {
        var material = await db.RawMaterials.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == materialId && x.IsActive);
        if (material is null)
            return (null, "Material not found or inactive.");

        var stock = await db.RawMaterialStocks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RawMaterialId == materialId && x.SiteId == siteId);
        var onHand = stock?.QuantityOnHand ?? 0m;

        var purchaseMovements = await db.RawMaterialMovements.AsNoTracking()
            .Where(x => x.RawMaterialId == materialId
                        && x.SiteId == siteId
                        && x.MovementType == TypePurchase
                        && x.Quantity > 0)
            .OrderBy(x => x.MovementDateUtc)
            .ThenBy(x => x.MovementNo)
            .ToListAsync();

        var invFilter = (invoiceRef ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(invFilter))
        {
            var purchaseIds = await db.SupplierPurchases.AsNoTracking()
                .Where(p => p.InvoiceRef == invFilter)
                .Select(p => p.Id)
                .ToListAsync();
            purchaseMovements = purchaseMovements
                .Where(m => m.SupplierPurchaseId is Guid pid && purchaseIds.Contains(pid))
                .ToList();
        }

        string resolvedInvoice = invFilter;
        if (string.IsNullOrEmpty(resolvedInvoice)
            && purchaseMovements.FirstOrDefault()?.SupplierPurchaseId is Guid firstPurchaseId)
        {
            var purchase = await db.SupplierPurchases.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == firstPurchaseId);
            resolvedInvoice = (purchase?.InvoiceRef ?? string.Empty).Trim();
        }

        var suggestedLines = new List<object>();
        for (var i = 0; i < purchaseMovements.Count; i++)
        {
            var movement = purchaseMovements[i];
            var lineMemo = RawMaterialRules.ResolveLineMemo(resolvedInvoice, (i + 1).ToString(CultureInfo.InvariantCulture));
            var code = RawMaterialRules.BuildCodeFromCategoryAndMemo(material.Category, lineMemo, out var codeErr);
            if (codeErr is not null)
                return (null, codeErr);
            suggestedLines.Add(new
            {
                LineNo = i + 1,
                MemoNo = lineMemo,
                SuggestedCode = code,
                movement.Quantity,
                movement.UnitCost,
                Name = string.Empty,
                SourceMovementId = movement.Id
            });
        }

        var movementQtyTotal = purchaseMovements.Sum(x => x.Quantity);
        var canAutoSplit = purchaseMovements.Count > 1;
        var helpText = purchaseMovements.Count switch
        {
            0 => "No purchase receipts found on this material at this factory. Use manual lines below if you still need to split on-hand stock.",
            1 => "Only one purchase receipt is stored on this item (quantities were combined). Enter each fabric line manually — qty + name + line memo — so they become separate stock rows.",
            _ => $"Found {purchaseMovements.Count} purchase lines combined under {material.Code}. Click Split to move each line to its own unique ID."
        };

        return (new
        {
            Source = new
            {
                material.Id,
                material.Code,
                material.Name,
                material.Category,
                material.Unit,
                QuantityOnHand = onHand,
                InvoiceRef = resolvedInvoice
            },
            CanAutoSplitByMovements = canAutoSplit,
            PurchaseMovementCount = purchaseMovements.Count,
            PurchaseMovementQtyTotal = movementQtyTotal,
            HelpText = helpText,
            PurchaseMovements = purchaseMovements.Select(m => new
            {
                m.Id,
                m.Quantity,
                m.UnitCost,
                m.TotalCost,
                m.MovementDateUtc,
                m.SupplierPurchaseId,
                m.Note
            }),
            SuggestedLines = suggestedLines
        }, null);
    }

    public static async Task<string?> SplitMergedStockAsync(BongoTexDbContext db, SplitMergedRawMaterialRequest req)
    {
        if (req.SourceRawMaterialId == Guid.Empty || req.SiteId == Guid.Empty)
            return "Material and factory are required.";

        var source = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == req.SourceRawMaterialId && x.IsActive);
        if (source is null)
            return "Material not found or inactive.";

        var lines = (req.Lines ?? [])
            .Where(l => l.Quantity > 0)
            .ToList();
        if (lines.Count == 0)
            return "Add at least one split line with quantity.";

        var sourceStock = await GetOrCreateStockAsync(db, source.Id, req.SiteId);
        var totalQty = lines.Sum(l => l.Quantity);
        if (totalQty > sourceStock.QuantityOnHand)
            return $"Split total {totalQty:0.####} exceeds on hand {sourceStock.QuantityOnHand:0.####} {source.Unit}.";

        var targetCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var memo = (line.MemoNo ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(memo))
                return "Each split line needs a line memo (e.g. 168-1).";
            var code = RawMaterialRules.BuildCodeFromCategoryAndMemo(source.Category, memo, out var codeErr);
            if (codeErr is not null)
                return codeErr;
            if (code.Equals(source.Code, StringComparison.OrdinalIgnoreCase))
                return $"Line memo must create a different ID than merged item {source.Code}.";
            if (!targetCodes.Add(code))
                return $"Duplicate target ID {code} on split lines.";
        }

        var manualLines = new List<SplitMergedRawMaterialLineRequest>();
        foreach (var line in lines)
        {
            var memo = (line.MemoNo ?? string.Empty).Trim();
            var code = RawMaterialRules.BuildCodeFromCategoryAndMemo(source.Category, memo, out _);
            var name = string.IsNullOrWhiteSpace(line.Name) ? source.Name : line.Name.Trim();

            if (line.SourceMovementId is Guid movementId && movementId != Guid.Empty)
            {
                var movement = await db.RawMaterialMovements.FirstOrDefaultAsync(x =>
                    x.Id == movementId && x.RawMaterialId == source.Id && x.SiteId == req.SiteId);
                if (movement is null)
                    return "Purchase movement not found on this material.";
                if (movement.MovementType != TypePurchase)
                    return "Only purchase movements can be reassigned.";
                if (movement.Quantity != line.Quantity)
                    return $"Line {memo}: quantity must match the stored purchase line ({movement.Quantity:0.####}).";

                var targetResult = await GetOrCreateSplitTargetAsync(db, code, name, source);
                if (targetResult.Error is not null)
                    return targetResult.Error;
                var target = targetResult.Material!;
                var targetStock = await GetOrCreateStockAsync(db, target.Id, req.SiteId);
                sourceStock.QuantityOnHand -= movement.Quantity;
                targetStock.QuantityOnHand += movement.Quantity;
                movement.RawMaterialId = target.Id;
                var splitNote = $"Split from {source.Code} → {target.Code}";
                movement.Note = string.IsNullOrWhiteSpace(movement.Note)
                    ? splitNote
                    : $"{splitNote}. {movement.Note}";
                if (movement.Note.Length > 500)
                    movement.Note = movement.Note[..500];
            }
            else
            {
                manualLines.Add(line);
            }
        }

        if (manualLines.Count > 0)
        {
            var manualTotal = manualLines.Sum(l => l.Quantity);
            var purchaseMovement = await db.RawMaterialMovements
                .Where(x => x.RawMaterialId == source.Id
                            && x.SiteId == req.SiteId
                            && x.MovementType == TypePurchase
                            && x.Quantity > 0)
                .OrderByDescending(x => x.MovementDateUtc)
                .ThenByDescending(x => x.MovementNo)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(req.InvoiceRef))
            {
                var purchaseIds = await db.SupplierPurchases
                    .Where(p => p.InvoiceRef == req.InvoiceRef.Trim())
                    .Select(p => p.Id)
                    .ToListAsync();
                purchaseMovement = await db.RawMaterialMovements
                    .Where(x => x.RawMaterialId == source.Id
                                && x.SiteId == req.SiteId
                                && x.MovementType == TypePurchase
                                && x.Quantity > 0
                                && x.SupplierPurchaseId != null
                                && purchaseIds.Contains(x.SupplierPurchaseId.Value))
                    .OrderByDescending(x => x.MovementDateUtc)
                    .ThenByDescending(x => x.MovementNo)
                    .FirstOrDefaultAsync();
            }

            if (purchaseMovement is null)
                return "No purchase receipt found to split. Post the supplier bill again with unique line memos, or use auto-split when multiple purchase lines exist.";

            if (purchaseMovement.Quantity < manualTotal)
                return $"Purchase receipt has {purchaseMovement.Quantity:0.####} {source.Unit}; manual split needs {manualTotal:0.####}.";

            var movementDate = purchaseMovement.MovementDateUtc;
            var purchaseId = purchaseMovement.SupplierPurchaseId;

            foreach (var line in manualLines)
            {
                var memo = (line.MemoNo ?? string.Empty).Trim();
                var code = RawMaterialRules.BuildCodeFromCategoryAndMemo(source.Category, memo, out _);
                var name = string.IsNullOrWhiteSpace(line.Name) ? source.Name : line.Name.Trim();
                var unitCost = line.UnitCost is > 0
                    ? line.UnitCost.Value
                    : (purchaseMovement.UnitCost > 0
                        ? purchaseMovement.UnitCost
                        : await GetLatestPurchaseUnitCostAsync(db, source.Id, req.SiteId));

                var targetResult = await GetOrCreateSplitTargetAsync(db, code, name, source);
                if (targetResult.Error is not null)
                    return targetResult.Error;
                var target = targetResult.Material!;
                sourceStock.QuantityOnHand -= line.Quantity;

                var recvErr = await ReceiveForPurchaseCoreAsync(
                    db, target, req.SiteId, line.Quantity, unitCost, movementDate, purchaseId,
                    $"Split from {source.Code} → {target.Code}");
                if (recvErr is not null)
                    return recvErr;
            }

            purchaseMovement.Quantity -= manualTotal;
            purchaseMovement.TotalCost = decimal.Round(
                purchaseMovement.Quantity * purchaseMovement.UnitCost, 2, MidpointRounding.AwayFromZero);
            if (purchaseMovement.Quantity <= 0)
                db.RawMaterialMovements.Remove(purchaseMovement);
        }

        if (sourceStock.QuantityOnHand < 0)
            return "Split would make source stock negative.";

        return null;
    }

    static async Task<(RawMaterial? Material, string? Error)> GetOrCreateSplitTargetAsync(
        BongoTexDbContext db,
        string code,
        string name,
        RawMaterial source)
    {
        var target = await db.RawMaterials.FirstOrDefaultAsync(x => x.Code.ToLower() == code.ToLower());
        if (target is null)
        {
            target = new RawMaterial
            {
                Code = code,
                Name = name,
                Category = source.Category,
                Unit = source.Unit,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.RawMaterials.Add(target);
            return (target, null);
        }

        if (!target.IsActive)
            return (null, $"Material {code} exists but is inactive.");
        if (!string.IsNullOrWhiteSpace(name) && !name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
            target.Name = name;
        return (target, null);
    }

    public static async Task<(int Renamed, string? Error)> FixHyphenCodesAsync(BongoTexDbContext db, string? invoiceRef)
    {
        var inv = (invoiceRef ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(inv))
            return (0, "Enter invoice memo (e.g. 168).");

        var materials = await db.RawMaterials.Where(x => x.IsActive).ToListAsync();
        var renamed = 0;
        foreach (var material in materials)
        {
            var newCode = RawMaterialRules.TryLegacyCodeToHyphenated(material.Code, inv);
            if (newCode is null || newCode.Equals(material.Code, StringComparison.OrdinalIgnoreCase))
                continue;
            if (await db.RawMaterials.AnyAsync(x => x.Code.ToLower() == newCode.ToLower() && x.Id != material.Id))
                return (renamed, $"Cannot rename {material.Code} → {newCode}: that ID already exists.");
            material.Code = newCode;
            renamed++;
        }

        return (renamed, null);
    }

    public static async Task<object> GetStockReportAsync(BongoTexDbContext db, Guid? siteId, Guid? materialId)
    {
        var q = from s in db.RawMaterialStocks.AsNoTracking()
                join m in db.RawMaterials.AsNoTracking() on s.RawMaterialId equals m.Id
                join site in db.Sites.AsNoTracking() on s.SiteId equals site.Id
                where m.IsActive
                select new { s, m, site };
        if (siteId is { } sid && sid != Guid.Empty)
            q = q.Where(x => x.s.SiteId == sid);
        if (materialId is { } mid && mid != Guid.Empty)
            q = q.Where(x => x.s.RawMaterialId == mid);
        var rows = await q.OrderBy(x => x.site.Code).ThenBy(x => x.m.Code).Select(x => new
        {
            x.s.RawMaterialId,
            MaterialCode = x.m.Code,
            MaterialName = x.m.Name,
            x.m.Category,
            x.m.Unit,
            x.s.SiteId,
            SiteCode = x.site.Code,
            SiteName = x.site.Name,
            x.s.QuantityOnHand
        }).ToListAsync();
        return new { Rows = rows, TotalLines = rows.Count };
    }

    public static async Task<object> GetReconciliationReportAsync(BongoTexDbContext db, Guid? siteId)
    {
        var movQ = db.RawMaterialMovements.AsNoTracking().AsQueryable();
        if (siteId is { } sid && sid != Guid.Empty)
            movQ = movQ.Where(x => x.SiteId == sid);

        var movementAgg = await movQ
            .GroupBy(x => new { x.RawMaterialId, x.SiteId })
            .Select(g => new
            {
                g.Key.RawMaterialId,
                g.Key.SiteId,
                TotalPurchased = g.Sum(x => x.MovementType == TypePurchase ? x.Quantity : 0m),
                TotalIssued = g.Sum(x => x.MovementType == TypeIssue || x.MovementType == TypeSale ? x.Quantity : 0m),
                TotalPurchasedAmount = g.Sum(x => x.MovementType == TypePurchase ? x.TotalCost : 0m),
                TotalIssuedAmount = g.Sum(x => x.MovementType == TypeIssue || x.MovementType == TypeSale ? x.TotalCost : 0m)
            })
            .ToListAsync();

        var stockQ = from s in db.RawMaterialStocks.AsNoTracking()
                     join m in db.RawMaterials.AsNoTracking() on s.RawMaterialId equals m.Id
                     join site in db.Sites.AsNoTracking() on s.SiteId equals site.Id
                     where m.IsActive
                     select new { s, m, site };
        if (siteId is { } sid2 && sid2 != Guid.Empty)
            stockQ = stockQ.Where(x => x.s.SiteId == sid2);

        var stockRows = await stockQ
            .OrderBy(x => x.site.Code)
            .ThenBy(x => x.m.Code)
            .Select(x => new
            {
                x.s.RawMaterialId,
                x.s.SiteId,
                MaterialCode = x.m.Code,
                MaterialName = x.m.Name,
                x.m.Category,
                x.m.Unit,
                SiteCode = x.site.Code,
                x.s.QuantityOnHand
            })
            .ToListAsync();

        var aggByKey = movementAgg.ToDictionary(x => (x.RawMaterialId, x.SiteId));
        var keys = new HashSet<(Guid RawMaterialId, Guid SiteId)>();
        foreach (var s in stockRows)
            keys.Add((s.RawMaterialId, s.SiteId));
        foreach (var a in movementAgg)
            keys.Add((a.RawMaterialId, a.SiteId));

        var matIds = keys.Select(k => k.RawMaterialId).Distinct().ToList();
        var siteIds = keys.Select(k => k.SiteId).Distinct().ToList();
        var materials = await db.RawMaterials.AsNoTracking()
            .Where(m => matIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id);
        var sites = await db.Sites.AsNoTracking()
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);
        var stockByKey = stockRows.ToDictionary(x => (x.RawMaterialId, x.SiteId));

        var rows = keys.Select(key =>
        {
            aggByKey.TryGetValue(key, out var agg);
            stockByKey.TryGetValue(key, out var stock);
            materials.TryGetValue(key.RawMaterialId, out var mat);
            sites.TryGetValue(key.SiteId, out var site);
            var purchased = agg?.TotalPurchased ?? 0m;
            var issued = agg?.TotalIssued ?? 0m;
            var purchasedAmount = agg?.TotalPurchasedAmount ?? 0m;
            var issuedAmount = agg?.TotalIssuedAmount ?? 0m;
            var onHand = stock?.QuantityOnHand ?? 0m;
            var onHandAmount = purchased > 0m
                ? decimal.Round(onHand * (purchasedAmount / purchased), 2, MidpointRounding.AwayFromZero)
                : 0m;
            return new
            {
                key.RawMaterialId,
                key.SiteId,
                MaterialCode = stock?.MaterialCode ?? mat?.Code ?? "",
                MaterialName = stock?.MaterialName ?? mat?.Name ?? "",
                Category = stock?.Category ?? mat?.Category ?? "",
                Unit = stock?.Unit ?? mat?.Unit ?? "",
                SiteCode = stock?.SiteCode ?? site?.Code ?? "",
                TotalPurchased = purchased,
                TotalPurchasedAmount = purchasedAmount,
                TotalIssued = issued,
                TotalIssuedAmount = issuedAmount,
                QuantityOnHand = onHand,
                OnHandAmount = onHandAmount
            };
        })
        .OrderBy(x => x.SiteCode)
        .ThenBy(x => x.MaterialCode)
        .ToList();

        return new { Rows = rows, TotalLines = rows.Count };
    }

    public static async Task<object> ListMovementsAsync(
        BongoTexDbContext db,
        DateTime? fromUtc,
        DateTime? toExclusive,
        Guid? siteId,
        Guid? materialId)
    {
        var q = db.RawMaterialMovements.AsNoTracking().AsQueryable();
        if (fromUtc.HasValue) q = q.Where(x => x.MovementDateUtc >= fromUtc.Value);
        if (toExclusive.HasValue) q = q.Where(x => x.MovementDateUtc < toExclusive.Value);
        if (siteId is { } sid && sid != Guid.Empty) q = q.Where(x => x.SiteId == sid);
        if (materialId is { } mid && mid != Guid.Empty) q = q.Where(x => x.RawMaterialId == mid);
        var movements = await q.OrderByDescending(x => x.MovementDateUtc).Take(500).ToListAsync();
        var matIds = movements.Select(x => x.RawMaterialId).Distinct().ToList();
        var siteIds = movements.Select(x => x.SiteId).Distinct().ToList();
        var materials = await db.RawMaterials.AsNoTracking().Where(m => matIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        var sites = await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var rows = movements.Select(m =>
        {
            materials.TryGetValue(m.RawMaterialId, out var mat);
            sites.TryGetValue(m.SiteId, out var site);
            return new
            {
                m.Id,
                m.MovementNo,
                m.MovementType,
                m.Quantity,
                m.UnitCost,
                m.TotalCost,
                m.MovementDateUtc,
                m.Note,
                m.CutLotCode,
                m.SupplierPurchaseId,
                m.CuttingEntryId,
                m.FinishingEntryId,
                MaterialCode = mat?.Code ?? "",
                MaterialName = mat?.Name ?? "",
                Unit = mat?.Unit ?? "",
                SiteCode = site?.Code ?? "",
                SiteName = site?.Name ?? ""
            };
        }).ToList();
        return new { Rows = rows };
    }

    public static async Task<decimal> GetLatestPurchaseUnitCostAsync(
        BongoTexDbContext db,
        Guid materialId,
        Guid siteId)
    {
        return await db.RawMaterialMovements.AsNoTracking()
            .Where(m => m.RawMaterialId == materialId
                        && m.SiteId == siteId
                        && m.MovementType == TypePurchase
                        && m.UnitCost > 0)
            .OrderByDescending(m => m.MovementDateUtc)
            .ThenByDescending(m => m.MovementNo)
            .Select(m => m.UnitCost)
            .FirstOrDefaultAsync();
    }

    public const string ScrapTypeScrapStock = "ScrapStock";
    public const string ScrapTypeCuttingWastage = "CuttingWastage";
    public const string ScrapTypeRejectGarment = "RejectGarment";

    public static bool ScrapTypeDeductsStock(string? scrapType)
    {
        var norm = NormalizeScrapType(scrapType);
        return norm == ScrapTypeScrapStock;
    }

    public static string NormalizeScrapType(string? scrapType)
    {
        var t = (scrapType ?? ScrapTypeScrapStock).Trim();
        if (t.Equals(ScrapTypeCuttingWastage, StringComparison.OrdinalIgnoreCase)
            || t.Equals("Cutting", StringComparison.OrdinalIgnoreCase))
            return ScrapTypeCuttingWastage;
        if (t.Equals(ScrapTypeRejectGarment, StringComparison.OrdinalIgnoreCase)
            || t.Equals("RejectGarment", StringComparison.OrdinalIgnoreCase))
            return ScrapTypeRejectGarment;
        if (t.Equals(ScrapTypeScrapStock, StringComparison.OrdinalIgnoreCase)
            || t.Equals("Wastage", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Reject", StringComparison.OrdinalIgnoreCase))
            return ScrapTypeScrapStock;
        return ScrapTypeScrapStock;
    }

    public static async Task<(RawMaterialScrapSale? Sale, string? Error)> CreateScrapSaleAsync(
        BongoTexDbContext db,
        CreateRawMaterialScrapSaleRequest req)
    {
        if (req.SiteId == Guid.Empty)
            return (null, "Factory is required.");
        if (req.Quantity <= 0)
            return (null, "Quantity must be greater than zero.");
        if (req.UnitRate <= 0)
            return (null, "Unit rate must be greater than zero.");

        var buyer = (req.BuyerName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(buyer))
            return (null, "Buyer name is required.");

        var site = await db.Sites.FirstOrDefaultAsync(x => x.Id == req.SiteId);
        if (site is null || !string.Equals(site.Type, "Factory", StringComparison.OrdinalIgnoreCase))
            return (null, "Scrap sales can only be posted from a factory site.");

        var scrapType = NormalizeScrapType(req.ScrapType);
        var deductStock = ScrapTypeDeductsStock(scrapType);
        var rawMaterialId = req.RawMaterialId is { } rm && rm != Guid.Empty ? rm : (Guid?)null;
        var inventoryItemId = req.InventoryItemId is { } inv && inv != Guid.Empty ? inv : (Guid?)null;

        RawMaterial? material = null;
        InventoryItem? item = null;
        string unit;

        if (scrapType == ScrapTypeCuttingWastage)
        {
            unit = "kg";
            if (rawMaterialId is { } fabricId)
            {
                material = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == fabricId && x.IsActive);
                if (material is null)
                    return (null, "Fabric material not found or inactive.");
            }
        }
        else if (scrapType == ScrapTypeRejectGarment)
        {
            rawMaterialId = null;
            unit = "pcs";
            if (inventoryItemId is { } itemId)
            {
                item = await db.InventoryItems.FirstOrDefaultAsync(x => x.Id == itemId);
                if (item is null)
                    return (null, "Inventory item not found.");
            }
        }
        else
        {
            if (rawMaterialId is null)
                return (null, "Material is required for scrap pile sales.");
            material = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == rawMaterialId && x.IsActive);
            if (material is null)
                return (null, "Raw material not found or inactive.");
            inventoryItemId = null;
            unit = material.Unit;
        }

        RawMaterialStock? stock = null;
        if (deductStock)
        {
            stock = await GetOrCreateStockAsync(db, rawMaterialId!.Value, req.SiteId);
            if (stock.QuantityOnHand < req.Quantity)
                return (null, $"Insufficient stock (on hand {stock.QuantityOnHand:0.####} {material!.Unit}, need {req.Quantity:0.####}).");
        }

        var totalAmount = decimal.Round(req.Quantity * req.UnitRate, 2, MidpointRounding.AwayFromZero);
        var paidAmount = req.IsCredit ? req.PaidAmount : totalAmount;
        if (paidAmount < 0)
            return (null, "Paid amount cannot be negative.");
        if (paidAmount > totalAmount)
            return (null, "Paid amount cannot exceed total amount.");

        var saleNo = $"SCR-{site.Code}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        while (await db.RawMaterialScrapSales.AnyAsync(x => x.SaleNo == saleNo))
            saleNo = $"SCR-{site.Code}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var soldAt = req.SoldAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var unitCost = rawMaterialId is { } matId
            ? await GetLatestPurchaseUnitCostAsync(db, matId, req.SiteId)
            : (item?.UnitPrice ?? 0m);
        var note = (req.Note ?? string.Empty).Trim();

        var row = new RawMaterialScrapSale
        {
            SaleNo = saleNo,
            SiteId = req.SiteId,
            RawMaterialId = rawMaterialId,
            InventoryItemId = inventoryItemId,
            ScrapType = scrapType,
            Quantity = req.Quantity,
            Unit = unit,
            UnitRate = req.UnitRate,
            TotalAmount = totalAmount,
            BuyerName = buyer,
            IsCredit = req.IsCredit,
            PaidAmount = paidAmount,
            DueAmount = totalAmount - paidAmount,
            Note = note,
            SoldAtUtc = soldAt,
            CreatedAtUtc = DateTime.UtcNow
        };

        await using var dbTx = await db.Database.BeginTransactionAsync();
        try
        {
            if (deductStock && stock is not null)
                stock.QuantityOnHand -= req.Quantity;
            db.RawMaterialScrapSales.Add(row);
            await db.SaveChangesAsync();

            if (deductStock && rawMaterialId is { } stockMatId)
            {
                db.RawMaterialMovements.Add(new RawMaterialMovement
                {
                    MovementNo = $"RM-SAL-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                    RawMaterialId = stockMatId,
                    SiteId = req.SiteId,
                    MovementType = TypeSale,
                    Quantity = req.Quantity,
                    UnitCost = unitCost,
                    TotalCost = decimal.Round(req.Quantity * unitCost, 2, MidpointRounding.AwayFromZero),
                    MovementDateUtc = soldAt,
                    Note = $"{scrapType} sale {saleNo} — {buyer}" + (string.IsNullOrEmpty(note) ? "" : $" — {note}"),
                    ScrapSaleId = row.Id,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            await dbTx.CommitAsync();
            return (row, null);
        }
        catch (Exception ex)
        {
            await dbTx.RollbackAsync();
            return (null, $"Could not post scrap sale: {ex.Message}");
        }
    }

    public static async Task<string?> CollectScrapSaleDueAsync(BongoTexDbContext db, Guid id, decimal amount)
    {
        if (amount <= 0)
            return "Collection amount must be greater than zero.";
        var sale = await db.RawMaterialScrapSales.FirstOrDefaultAsync(x => x.Id == id);
        if (sale is null)
            return "Scrap sale not found.";
        if (sale.DueAmount <= 0)
            return "No due remaining on this sale.";
        if (amount > sale.DueAmount)
            return $"Amount exceeds due ({sale.DueAmount:0.##}).";
        sale.PaidAmount += amount;
        sale.DueAmount -= amount;
        await db.SaveChangesAsync();
        return null;
    }
}

public static class PasswordHasher
{
    const int SaltSize = 16;
    const int KeySize = 32;
    const int Iterations = 100_000;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.', 3);
            if (parts.Length != 3) return false;
            int iterations = int.Parse(parts[0]);
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] key = Convert.FromBase64String(parts[2]);
            byte[] attempt = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, key.Length);
            return CryptographicOperations.FixedTimeEquals(attempt, key);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class AuthSession
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}

public static class SessionStore
{
    static readonly ConcurrentDictionary<string, AuthSession> Sessions = new();
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static string Create(AuthSession session)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        session.ExpiresUtc = DateTime.UtcNow.Add(Lifetime);
        Sessions[token] = session;
        return token;
    }

    public static AuthSession? Get(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!Sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresUtc < DateTime.UtcNow)
        {
            Sessions.TryRemove(token, out _);
            return null;
        }
        session.ExpiresUtc = DateTime.UtcNow.Add(Lifetime);
        return session;
    }

    public static void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token)) Sessions.TryRemove(token, out _);
    }

    public static void RemoveUser(Guid userId)
    {
        foreach (var kv in Sessions)
        {
            if (kv.Value.UserId == userId) Sessions.TryRemove(kv.Key, out _);
        }
    }

    public static string? ExtractToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth.Substring(7).Trim();
        var custom = ctx.Request.Headers["X-Auth-Token"].ToString();
        return string.IsNullOrEmpty(custom) ? null : custom.Trim();
    }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Accountant = "Accountant";

    public static bool IsValid(string? role) =>
        role == Admin || role == Manager || role == Accountant;
}
