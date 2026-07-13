# BongoTex ERP (Starter)

Starter ERP project for BongoTex, built with modern .NET.

## What is included

- `src/BongoTex.Api`: ASP.NET Core minimal API endpoints
- `src/BongoTex.Core`: Core ERP domain models
- `src/BongoTex.Application`: Application layer
- `src/BongoTex.Infrastructure`: Entity Framework Core + SQL Server persistence

## Prerequisites

- .NET SDK 10 installed

## Database setup (SQL Server)

Update connection string in:

- `src/BongoTex.Api/appsettings.json`

Then create and apply migration:

```powershell
cd BongoTex
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project .\src\BongoTex.Infrastructure\BongoTex.Infrastructure.csproj --startup-project .\src\BongoTex.Api\BongoTex.Api.csproj
dotnet ef database update --project .\src\BongoTex.Infrastructure\BongoTex.Infrastructure.csproj --startup-project .\src\BongoTex.Api\BongoTex.Api.csproj
```

## Run locally

```powershell
cd BongoTex
dotnet restore
dotnet run --project .\src\BongoTex.Api\BongoTex.Api.csproj
```

Default URL:

- `http://localhost:5080`

## Current ERP modules (DB-backed starter)

- Inventory items
- Customers
- Sales orders
- Production orders
- Site-level stock (Factory/Sales Center)
- Stock transfer between sites

## Your structure (1 factory + 3 sales centers)

After running the API and migrations, initialize your company locations:

```powershell
curl -X POST http://localhost:5080/api/setup/sites/default
```

This creates:

- 1 Factory (`FACTORY-01`)
- 3 Sales centers (`SC-01`, `SC-02`, `SC-03`)

Use these APIs for production flow:

- `POST /api/production-orders` to add produced stock into factory
- `POST /api/stock-transfers` to send stock from factory to sales centers
- `GET /api/stocks` to see quantity by site

## Next recommended steps

1. Add authentication/roles (Admin, Sales, Inventory)
2. Build React or Blazor frontend
3. Add reports and invoicing workflows
4. Add purchasing and supplier modules
