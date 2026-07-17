# atlas-net

An Employer-of-Record (EOR) platform backend, in the style of Velocity Global / Deel, built with C# and .NET 10.

Atlas lets client companies hire workers in countries where they have no legal entity. The platform owns the
employment relationship: contracts, onboarding, compliance documents, monthly payroll, payslips, and client invoicing.

## Stack

- .NET 10 / ASP.NET Core Web API (no frontend)
- EF Core with SQLite
- xUnit (unit + `WebApplicationFactory` integration tests)

## Solution layout

| Project | Purpose |
| --- | --- |
| `Atlas.Domain` | Entities, enums, domain logic — no infrastructure dependencies |
| `Atlas.Infrastructure` | EF Core `DbContext`, persistence configuration, seeding |
| `Atlas.Api` | ASP.NET Core Web API host, endpoints, request validation |
| `Atlas.Tests` | Unit and integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Atlas.Api
```

The API seeds a SQLite database (`atlas.db`) with sample countries, clients, and workers on first run in Development.

## Domain scope

- **Clients** — companies that hire through Atlas
- **Countries** — supported hiring countries with currency and employer-cost rate
- **Workers** — individuals employed via Atlas on behalf of clients
- **Employment contracts** — draft → active → terminated lifecycle with salary, currency, start date
- **Onboarding checklists** — per-contract items (documents, right-to-work, banking)
- **Compliance documents** — with expiry tracking
- **Payroll runs** — per country/month; gross → employer costs → net for active contracts
- **Payslips** — one per contract per payroll run
- **Invoices** — client invoices aggregating payroll costs + management fees
