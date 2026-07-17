# atlas-net

An Employer-of-Record (EOR) platform backend, in the style of Velocity Global / Deel, built with C# and .NET 10.

Atlas lets client companies hire workers in countries where they have no legal entity. The platform owns the
employment relationship end to end: contracts, onboarding, compliance documents, monthly payroll, payslips,
and client invoicing — plus (v2) API-key auth with client-scoped roles, leave, expense claims, contract
amendments with salary history, multi-currency invoicing, benefits, notice-checked terminations with final
pay, operational reports, and production hardening.

## Documentation

| Document | Contents |
| --- | --- |
| [docs/getting-started.md](docs/getting-started.md) | Prerequisites, run & seed, curl walkthrough from country setup to invoice |
| [docs/architecture.md](docs/architecture.md) | Layering, project responsibilities, domain invariants, the SQLite `DateTimeOffset` converter |
| [docs/api-reference.md](docs/api-reference.md) | Every endpoint: method, route, required role, shapes, error codes |
| [docs/testing.md](docs/testing.md) | Test taxonomy, how to run, what the integration harness does |
| [docs/adr/](docs/adr/README.md) | Architecture decision records (auth, 404-vs-403, money rounding, concurrency, …) |

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
- **Leave** is governed by one policy per country (annual + sick working-day allowances per
  calendar year). Requests consume working days (Mon-Fri, holidays out of scope), must fit in a
  single calendar year, may not overlap another pending/approved request, and must fit the
  remaining balance — pending requests reserve days; rejection/cancellation releases them.
  Lifecycle: `Pending -> Approved | Rejected` (client decision, note required to reject) or
  `Pending -> Cancelled` (withdrawn).
- **Expense claims** carry line items (positive amounts, receipts as http(s) URLs) in the
  contract's local currency and follow `Pending -> Approved | Rejected -> Reimbursed`. Creating a
  payroll run pays out every approved, not-yet-reimbursed claim of the contracts in the run:
  reimbursements are added untaxed to net pay, billed to the client at cost on the invoice
  (management fee applies to gross salary only), and the claim records the reimbursing run.
- **Contract amendments** change an active contract's salary and/or job title from an effective
  date (`Pending -> Approved | Rejected | Cancelled`, one pending per contract). Approval updates
  the contract's current terms and appends to an immutable, append-only salary history that starts
  with the hiring terms. Payroll pays the terms effective for each period — the latest record
  effective on or before the month's end — so future-dated raises never leak into earlier months
  (mid-month changes apply to the whole month; no proration).
- **Multi-currency invoicing**: payroll always runs in the worker's local currency, while each
  client is billed in its own billing currency (defaults to the HQ country's currency). Invoices
  keep the local amounts and add the applied FX rate plus the converted total. Rates live in an
  append-only dated table (`FxRate`, unique per base/quote/effective-date) and conversion uses the
  rate effective for the payroll period — same selection rule as salary history. Completing a run
  without a needed rate fails with 409 and leaves the run draft; all conversions round to 2 dp
  away from zero.
- **Benefits** are per-country plans priced as a monthly premium with an employer contribution
  rate in [0, 1]: the employer share (rounded) is billed to the client, the employee share (exact
  remainder, so no cent is lost) is withheld from net pay. Enrollments tie a plan to an active
  contract in the same country (one active enrollment per plan per contract; deactivated plans
  accept no new enrollments) and payroll charges every enrollment covering any part of the month.
- **Terminations & final pay**: the standard flow is a termination request whose proposed end date
  must satisfy the country's minimum notice period counted from the request date; approving it
  terminates the contract (the direct `/api/contracts/{id}/terminate` endpoint remains for
  immediate, for-cause dismissals). The final month's payroll prorates salary by calendar days
  worked and pays out unused annual leave (allowance minus reserved days, at a daily rate of
  12 salaries / 260 working days) as part of taxable gross; later months pay nothing.
- **Reports** (platform-admin only, under `/api/reports`): headcount as of a date (activated
  contracts covering the date, including notice-serving terminations) broken down by country and
  client; payroll cost totals per month and currency with country/client/period filters;
  compliance-document expiries within a window with days-until-expiry; and invoice aging in
  0-30 / 31-60 / 61-90 / 90+ day buckets per billing currency (no payment tracking, so every
  invoice counts as outstanding).

## Authentication & authorization

Every `/api/*` endpoint requires an API key in the `X-Api-Key` header (`/health` stays open).
Keys belong to `ApiUser` rows with one of three roles:

- **PlatformAdmin** — operates Atlas itself: full access, plus admin-only endpoints
  (countries, clients, workers, compliance, payroll runs, API keys).
- **ClientAdmin** — scoped to one client: sees that client's data and can create/activate/terminate
  its contracts and complete onboarding items.
- **ClientViewer** — read-only access scoped to one client.

Client-scoped callers only ever see their own client, workers (via contracts), contracts, and
invoices — cross-client reads return 404 (existence is not revealed) and insufficient-role writes
return 403. Keys are issued via `POST /api/api-users` (full secret returned once, masked afterwards)
and revoked via `POST /api/api-users/{id}/deactivate`. Development seeding creates `dev-admin-key`,
`dev-acme-admin-key`, and `dev-acme-viewer-key`.

## API surface

| Method & path | Purpose |
| --- | --- |
| `GET /health` | Liveness probe with database check (anonymous) |
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
| `GET/POST /api/api-users`, `POST /api/api-users/{id}/deactivate` | API keys (platform admin only) |
| `GET/POST /api/leave-policies`, `GET /api/leave-policies/{code}` | Per-country leave allowances |
| `GET/POST /api/leave-requests`, `GET /api/leave-requests/{id}` | Leave requests (filters: `?contractId=`, `?status=`) |
| `POST /api/leave-requests/{id}/approve\|reject\|cancel` | Leave approval flow |
| `GET /api/contracts/{id}/leave-balances?year=N` | Per-type allowance/used/pending/remaining |
| `GET/POST /api/expense-claims`, `GET /api/expense-claims/{id}` | Expense claims (filters: `?contractId=`, `?status=`) |
| `POST /api/expense-claims/{id}/approve\|reject` | Expense approval flow |
| `GET/POST /api/contract-amendments`, `GET /api/contract-amendments/{id}` | Amendments (filters: `?contractId=`, `?status=`) |
| `POST /api/contract-amendments/{id}/approve\|reject\|cancel` | Amendment approval flow |
| `GET /api/contracts/{id}/salary-history` | Immutable salary/title history |
| `GET/POST /api/fx-rates` | Dated FX rates (filters: `?baseCurrency=`, `?quoteCurrency=`) |
| `GET/POST /api/benefit-plans`, `POST /api/benefit-plans/{id}/deactivate` | Per-country benefit packages |
| `GET/POST /api/benefit-enrollments`, `POST /api/benefit-enrollments/{id}/end` | Contract benefit enrollments |
| `GET/POST /api/termination-requests`, `GET /api/termination-requests/{id}` | Notice-checked termination flow |
| `POST /api/termination-requests/{id}/approve\|reject\|cancel` | Termination approval flow |
| `GET /api/reports/headcount?asOf=` | Headcount by country and client |
| `GET /api/reports/payroll-costs?countryCode=&clientId=&fromYear=...` | Payroll cost totals per month/currency |
| `GET /api/reports/compliance-expiries?withinDays=N` | Expired + upcoming document expiries |
| `GET /api/reports/invoice-aging?clientId=` | Outstanding invoices bucketed by age |

Errors are RFC 7807 ProblemDetails throughout: validation failures return 400 with per-field errors,
domain-rule violations (double activation, duplicate payroll run, incomplete onboarding, duplicate
countries/workers/policies/rates/plans, ...) return 409, and global `IExceptionHandler`s catch any
`DomainException` (409) or `DbUpdateConcurrencyException` (409) that escapes an endpoint.

## Production readiness

- **Request logging**: one structured log line per request (method, path, final status, elapsed ms)
  emitted after the exception handler so the logged status matches what the client received.
- **Health**: `GET /health` runs ASP.NET Core health checks including a database connectivity
  probe and reports `{status, service, timestampUtc, checks[]}` (503 when unhealthy).
- **Pagination**: every flat list endpoint accepts `?page=` (1-based) and `?pageSize=` (default 50,
  max 200; out-of-range values 400). Bodies stay plain JSON arrays; page metadata travels in
  `X-Total-Count`, `X-Page`, and `X-Page-Size` response headers. Bounded sub-resource lists
  (onboarding checklist, salary history, balances, worker documents) are not paged.
- **Optimistic concurrency**: entities whose updates race (contracts, payroll runs, leave requests,
  expense claims, amendments, termination requests, benefit enrollments) carry an integer `Version`
  concurrency token bumped on every update; a stale write fails and surfaces as a 409 ProblemDetails
  telling the caller to reload and retry.

## Implementation notes

- **SQLite and `DateTimeOffset`**: SQLite cannot order or compare `DateTimeOffset` natively, so
  `AtlasDbContext` registers a `ValueConverter` that stores every `DateTimeOffset` as UTC ticks (`long`).
  Ordering, filtering, and instant-preserving round-trips are covered by tests. `DateOnly` is used for
  calendar dates (start/end/expiry) and money is `decimal` + ISO 4217 currency code everywhere.
- **Testing**: integration tests boot the real host via `WebApplicationFactory` with each fixture bound to
  its own in-memory SQLite connection and a seeded platform-admin API key attached to every test client;
  payroll tests provision a private country each so run uniqueness never leaks between tests. A
  full-journey test walks country -> client -> worker -> contract -> onboarding -> compliance ->
  payroll -> invoice through the public API only. 438 tests across domain unit tests and endpoint
  integration tests — see [docs/testing.md](docs/testing.md).
