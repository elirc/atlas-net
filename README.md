# atlas-net

An Employer-of-Record (EOR) platform backend, in the style of Velocity Global / Deel, built with C# and .NET 10.

Atlas lets client companies hire workers in countries where they have no legal entity. The platform owns the
employment relationship end to end: contracts, onboarding, compliance documents, monthly payroll, payslips,
and client invoicing.

## Stack

- .NET 10 / ASP.NET Core Web API (minimal APIs, no frontend)
- EF Core with SQLite
- xUnit — unit tests plus `WebApplicationFactory` integration tests against real in-memory SQLite

## Solution layout

| Project | Purpose |
| --- | --- |
| `src/Atlas.Domain` | Entities, enums, lifecycle rules, pure payroll math — no infrastructure dependencies |
| `src/Atlas.Infrastructure` | EF Core `AtlasDbContext`, payroll orchestration, dev seeding |
| `src/Atlas.Api` | ASP.NET Core host, endpoints, validation, ProblemDetails |
| `tests/Atlas.Tests` | Unit + integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Atlas.Api
```

In Development the API creates and seeds a SQLite database (`atlas.db`) with six countries, two clients,
four workers, three contracts (two active), onboarding checklists, and compliance documents — including one
document that shows up in the expiring report and one that is already expired.

## Domain model

```
Country ──< Worker ──< ComplianceDocument
   │           │
Client ──< EmploymentContract ──< OnboardingItem
   │           │
   │       Payslip >── PayrollRun (one per country + month)
   │           │
Invoice >──────┘  (one per client per completed run)
```

- **Countries** carry the currency plus the statutory rates payroll needs: an employer-cost rate
  (social contributions on top of gross) and a flat employee-deduction rate (withholding).
- **Employment contracts** follow a one-way `Draft -> Active -> Terminated` lifecycle. Activation requires
  every required onboarding item to be completed; termination requires a reason and an end date on or after
  the start date. A worker can hold only one non-terminated contract at a time.
- **Onboarding checklists** are created automatically with each contract: identity document, right-to-work
  check, bank details, and signed contract (required) plus local tax forms (optional).
- **Compliance documents** track expiry: `Valid`, `ExpiringSoon` (within a configurable window, default
  30 days), or `Expired`. `GET /api/compliance/expiring` is the ops report.
- **Payroll runs** are unique per country + calendar month. Creating one computes a payslip for every
  contract whose employment period covers that month (full-month pay, no day proration):
  `gross -> employer cost -> employee deductions -> net`, all `decimal`, rounded to 2 dp away from zero
  per component, so `net + deductions == gross` always holds.
- **Completing a run** is one-way and issues one invoice per client: payroll subtotal
  (gross + employer costs) plus the client's management fee on gross, numbered `INV-yyyymm-CC-nnn`,
  in the payroll country's currency.

## API surface

| Method & path | Purpose |
| --- | --- |
| `GET /health` | Liveness probe |
| `GET/POST /api/countries`, `GET /api/countries/{code}` | Supported hiring countries |
| `GET/POST /api/clients`, `GET /api/clients/{id}` | Client companies |
| `GET/POST /api/workers`, `GET /api/workers/{id}` | Workers (filter: `?countryCode=`) |
| `GET/POST /api/contracts`, `GET /api/contracts/{id}` | Contracts (filters: `?clientId=`, `?status=`) |
| `POST /api/contracts/{id}/activate` | Draft -> Active (blocked until onboarding is complete) |
| `POST /api/contracts/{id}/terminate` | Active -> Terminated (end date + reason) |
| `GET /api/contracts/{id}/onboarding` | Checklist with completion rollup |
| `POST /api/contracts/{id}/onboarding/{itemId}/complete` | Complete a checklist item |
| `GET/POST /api/workers/{id}/documents` | Compliance documents with computed status |
| `GET /api/compliance/expiring?withinDays=N` | Expired / expiring-soon report |
| `GET/POST /api/payroll-runs`, `GET /api/payroll-runs/{id}` | Payroll runs with money totals and payslips |
| `POST /api/payroll-runs/{id}/complete` | Complete a run and issue client invoices |
| `GET /api/invoices`, `GET /api/invoices/{id}` | Invoices (filter: `?clientId=`) |

Errors are RFC 7807 ProblemDetails throughout: validation failures return 400 with per-field errors,
domain-rule violations (double activation, duplicate payroll run, incomplete onboarding, ...) return 409,
and a global `IExceptionHandler` catches any `DomainException` that escapes an endpoint.

## Implementation notes

- **SQLite and `DateTimeOffset`**: SQLite cannot order or compare `DateTimeOffset` natively, so
  `AtlasDbContext` registers a `ValueConverter` that stores every `DateTimeOffset` as UTC ticks (`long`).
  Ordering, filtering, and instant-preserving round-trips are covered by tests. `DateOnly` is used for
  calendar dates (start/end/expiry) and money is `decimal` + ISO 4217 currency code everywhere.
- **Testing**: integration tests boot the real host via `WebApplicationFactory` with each fixture bound to
  its own in-memory SQLite connection; payroll tests provision a private country each so run uniqueness
  never leaks between tests. A full-journey test walks country -> client -> worker -> contract ->
  onboarding -> compliance -> payroll -> invoice through the public API only.
