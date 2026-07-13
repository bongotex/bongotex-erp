# Rewrites CuttingEntry, SewingEntry, FinishingEntry as UTF-8 no BOM. Run if you see CS1056.
$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$utf8 = New-Object System.Text.UTF8Encoding $false
$entities = Join-Path $root "src\BongoTex.Core\Entities"
if (-not (Test-Path $entities)) {
    Write-Error "Entities folder not found: $entities"
    exit 1
}

$cuttingLines = @(
    "namespace BongoTex.Core.Entities;",
    "",
    "/// <summary>Cutting section: fabric used and pieces cut for an inventory style; feeds sewing WIP.</summary>",
    "public class CuttingEntry",
    "{",
    "    public Guid Id { get; set; } = Guid.NewGuid();",
    "    public string CuttingNo { get; set; } = string.Empty;",
    "    public Guid FactorySiteId { get; set; }",
    "    public Guid InventoryItemId { get; set; }",
    "    /// <summary>Pieces cut for this item (same unit as sewing / finishing).</summary>",
    "    public int QuantityCut { get; set; }",
    "    public decimal FabricKg { get; set; }",
    "    public decimal FabricPricePerKg { get; set; }",
    "    /// <summary>FabricKg times FabricPricePerKg at entry time.</summary>",
    "    public decimal FabricAmount { get; set; }",
    "    public DateTime CutAtUtc { get; set; } = DateTime.UtcNow;",
    "}"
)
$cutting = ($cuttingLines -join [Environment]::NewLine) + [Environment]::NewLine

$sewingLines = @(
    "namespace BongoTex.Core.Entities;",
    "",
    "/// <summary>Sewing section: pieces sewn per day per item.</summary>",
    "public class SewingEntry",
    "{",
    "    public Guid Id { get; set; } = Guid.NewGuid();",
    "    public string SewingNo { get; set; } = string.Empty;",
    "    public Guid FactorySiteId { get; set; }",
    "    public Guid InventoryItemId { get; set; }",
    "    public int QuantitySewn { get; set; }",
    "    public DateTime SewnAtUtc { get; set; } = DateTime.UtcNow;",
    "}"
)
$sewing = ($sewingLines -join [Environment]::NewLine) + [Environment]::NewLine

$finishingLines = @(
    "namespace BongoTex.Core.Entities;",
    "",
    "/// <summary>Finishing section: pieces finished per day per item.</summary>",
    "public class FinishingEntry",
    "{",
    "    public Guid Id { get; set; } = Guid.NewGuid();",
    "    public string FinishingNo { get; set; } = string.Empty;",
    "    public Guid FactorySiteId { get; set; }",
    "    public Guid InventoryItemId { get; set; }",
    "    public int QuantityFinished { get; set; }",
    "    public DateTime FinishedAtUtc { get; set; } = DateTime.UtcNow;",
    "}"
)
$finishing = ($finishingLines -join [Environment]::NewLine) + [Environment]::NewLine

$siteRentLines = @(
    "namespace BongoTex.Core.Entities;",
    "",
    "/// <summary>Registered monthly rent for a factory or sales centre site (used for daily P/L allocation).</summary>",
    "public class SiteMonthlyRent",
    "{",
    "    public Guid Id { get; set; } = Guid.NewGuid();",
    "    public Guid SiteId { get; set; }",
    "    public decimal MonthlyRent { get; set; }",
    "    public string LandlordName { get; set; } = string.Empty;",
    "    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;",
    "}"
)
$siteRent = ($siteRentLines -join [Environment]::NewLine) + [Environment]::NewLine

$productStyleLines = @(
    "namespace BongoTex.Core.Entities;",
    "",
    "/// <summary>Master garment style by SKU prefix (e.g. SP = Polo Shirt). One production cost applies to all SKUs with that prefix.</summary>",
    "public class ProductStyle",
    "{",
    "    public string Prefix { get; set; } = string.Empty;",
    "    public string Name { get; set; } = string.Empty;",
    "    public decimal ProductionCost { get; set; }",
    "    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;",
    "    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;",
    "}"
)
$productStyle = ($productStyleLines -join [Environment]::NewLine) + [Environment]::NewLine

foreach ($pair in @(
        @{ Name = "CuttingEntry.cs"; Text = $cutting },
        @{ Name = "SewingEntry.cs"; Text = $sewing },
        @{ Name = "FinishingEntry.cs"; Text = $finishing },
        @{ Name = "SiteMonthlyRent.cs"; Text = $siteRent },
        @{ Name = "ProductStyle.cs"; Text = $productStyle }
    )) {
    $path = Join-Path $entities $pair.Name
    [System.IO.File]::WriteAllText($path, $pair.Text, $utf8)
}

Write-Host "fix-pipeline-entities-encoding: OK"
exit 0
