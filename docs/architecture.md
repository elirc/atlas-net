# Architecture

Atlas is an Employer-of-Record (EOR) platform backend: client companies hire
workers in countries where they have no legal entity, and Atlas owns the
employment relationship — contracts, onboarding, compliance, payroll, and
invoicing. This document explains how the code is layered, what each project
owns, and the invariants the domain enforces.

## Layering

```
┌──────────────────────────────────────────────────────────┐
│ Atlas.Api          ASP.NET Core minimal-API host          │
│   endpoints, request validation, authn/authz,             │
│   ProblemDetails mapping, pagination, request logging     │
├──────────────────────────────────────────────────────────┤
│ Atlas.Infrastructure   EF Core + orchestration            │
│   AtlasDbContext (SQLite), PayrollService, LeaveService,  │
│   DataSeeder                                              │
├──────────────────────────────────────────────────────────┤
│ Atlas.Domain       Pure domain model                      │
│   entities, enums, lifecycle transitions,                 │
│   PayrollCalculator / FinalPayCalculator / LeaveCalculator│
│   — no framework or database dependencies                 │
└──────────────────────────────────────────────────────────┘
```

Dependencies point strictly downward: `Api → Infrastructure → Domain`.

### Atlas.Domain

- **Entities** (`Country`, `Client`, `Worker`, `EmploymentContract`,
  `OnboardingItem`, `ComplianceDocument`, `PayrollRun`, `Payslip`, `Invoice`,
  `ApiUser`, `LeavePolicy`, `LeaveRequest`, `ExpenseClaim`/`ExpenseItem`,
  `ContractAmendment`, `SalaryRecord`, `FxRate`, `BenefitPlan`,
  `BenefitEnrollment`, `TerminationRequest`) carry both data and behavior:
  state transitions live on the entity (`contract.Activate(...)`,
  `leaveRequest.Reject(note, now)`), never in endpoint code.
- **Pure calculators** are static and side-effect free:
  - `PayrollCalculator` — gross → employer cost / deductions / net / total
    cost; `RoundMoney` is the single rounding policy (2 dp, away from zero).
  - `FinalPayCalculator` — calendar-day proration of the final month and
    unused-leave payout at a daily rate of 12 salaries / 260 working days.
  - `LeaveCalculator` — working-day (Mon–Fri) counting, endpoints inclusive.
- **`DomainException`** is the domain's only failure signal. The API maps it
  to a 409 ProblemDetails; nothing in the domain knows about HTTP.
- **`IVersioned`** marks entities that participate in optimistic concurrency
  (an `int Version` token).

### Atlas.Infrastructure

- **`AtlasDbContext`** configures the schema: string-converted enums, max
  lengths, unique indexes that back domain invariants (one payroll run per
  country+month, unique worker email, one payslip per run+contract, one
  invoice per run+client, unique FX rate per base/quote/effective-date,
  unique plan name per country, unique API key), and delete behaviors.
  It also owns two cross-cutting mechanics:
  - the `DateTimeOffset` → UTC-ticks value converter (see below), and
  - `Version` bumping: every `SaveChanges` increments the token on modified
    `IVersioned` entities, and the token is registered as an EF concurrency
    token so a stale write throws `DbUpdateConcurrencyException`.
- **`PayrollService`** orchestrates the payroll unit of work (see
  "Payroll composition" below).
- **`LeaveService`** validates leave submissions against contract state, the
  country policy, overlapping requests, and the remaining balance, and
  computes per-type balances.
- **`DataSeeder`** populates a fresh Development database (six countries, two
  clients, four workers, three contracts, policies, plans, FX rates,
  compliance documents, and three dev API keys). It is idempotent — it exits
  if any country exists.

### Atlas.Api

- Minimal-API endpoint classes, one per aggregate, each following the same
  pipeline: validate input (400) → load + ownership check (404) → role check
  (403) → domain transition inside try/catch (409 on `DomainException`) →
  save → map to a response record. Response DTOs are records defined next to
  the endpoints; entities never serialize directly.
- **Auth**: `ApiKeyAuthenticationHandler` resolves the `X-Api-Key` header to
  an active `ApiUser` and issues role + client-id claims.
  `AuthorizationExtensions` centralizes the policy questions
  (`IsPlatformAdmin`, `CanViewClient`, `CanManageClient`).
- **Error surface**: `DomainExceptionHandler` and
  `ConcurrencyExceptionHandler` (global `IExceptionHandler`s) convert any
  escaping `DomainException` or `DbUpdateConcurrencyException` into 409
  ProblemDetails; `AddProblemDetails` + `UseStatusCodePages` make every
  error body RFC 7807.
- **`Pagination`** implements header-based paging (`X-Total-Count`,
  `X-Page`, `X-Page-Size`; default 50, max 200) so list bodies stay plain
  JSON arrays.
- **`RequestLoggingMiddleware`** logs one structured line per request after
  the exception handler, so the logged status is the status the client saw.

## Key domain invariants

### Lifecycles are one-way

| Aggregate | Lifecycle |
| --- | --- |
| `EmploymentContract` | `Draft → Active → Terminated` |
| `PayrollRun` | `Draft → Completed` |
| `LeaveRequest`, `ContractAmendment`, `TerminationRequest` | `Pending → Approved \| Rejected \| Cancelled` |
| `ExpenseClaim` | `Pending → Approved → Reimbursed`, or `Pending → Rejected` |
| `BenefitEnrollment` | `Active → Ended` |

Every transition validates the current state and throws `DomainException`
with a message naming the offending state; the API surfaces that message
verbatim as the 409 `detail`. Rejections always require a note. A contract
can hold only one *pending* amendment and one *pending* termination request
at a time; a worker can hold only one non-terminated contract.

Activation has one extra gate: every **required onboarding item** must be
completed first (the checklist is created automatically with the contract).

### Money handling

- All money is `decimal` plus an ISO 4217 currency code; there is no float
  anywhere and no currency-less amount.
- `PayrollCalculator.RoundMoney` (2 dp, `MidpointRounding.AwayFromZero`) is
  the single rounding function. Each payroll component is rounded once, so
  `net + deductions == gross` and `gross + employerCost == totalCost` hold
  exactly.
- Benefit premiums split without losing a cent: the employer share is the
  rounded product, the employee share is defined as the *remainder*
  (`MonthlyCost - EmployerShare`), never independently rounded.
- FX conversion rounds once on the invoice total
  (`RoundMoney(total * rate)`), never per line.
- Payroll always runs in the worker's local (country) currency; invoices
  keep the local amounts and add the applied rate plus the converted total
  in the client's billing currency.

### Salary history is append-only

`SalaryRecord` rows are only ever inserted: one `Initial` record at contract
creation, one `Amendment` record per approved amendment. Nothing updates or
deletes them, so a contract's full compensation history is auditable.
Payroll pays the terms **effective for each period** — the latest record
whose `EffectiveDate` is on or before the month's end (ties broken by
creation time). Consequences:

- future-dated raises never leak into earlier months, and
- a change effective mid-month applies to that whole month (documented
  simplification: no intra-month proration).

`FxRate` follows exactly the same append-only + effective-for-month rule, so
salary and FX period selection behave identically.

### Payroll composition

`PayrollService.CreateRunAsync` builds a run for one country + calendar
month (unique, enforced by index):

1. Select contracts that were activated and whose employment period overlaps
   the month (`CoversMonth`; full-month pay, no day proration).
2. For each contract, pick the salary effective for the period from salary
   history (falling back to the contract's current salary).
3. If the contract *ends* inside this month: prorate the final month by
   calendar days worked and add an unused-annual-leave payout (allowance
   minus reserved pending/approved days in the end year, at the daily rate)
   — both are part of taxable gross.
4. Apply `PayrollCalculator` for the statutory split.
5. Add approved, not-yet-reimbursed expense claims untaxed to net pay, mark
   them `Reimbursed`, and carry them onto the client's bill at cost.
6. Charge benefit premiums for every enrollment covering the month: employer
   share onto client cost, employee share out of net pay.

The run is a **snapshot**: amendments approved after the run is created do
not rewrite its payslips. `CompleteRunAsync` is one-way and issues one
invoice per client (subtotal + management fee on gross, numbered
`INV-yyyymm-CC-nnn`), converting into the client's billing currency at the
rate effective for the payroll period — and fails with 409 (leaving the run
draft) if a needed rate is missing.

### Authorization model

Three roles: `PlatformAdmin` (unscoped), `ClientAdmin` and `ClientViewer`
(both bound to exactly one client). The policy, enforced uniformly:

- Platform operations (countries, workers, clients, compliance writes,
  payroll, FX rates, leave policies, benefit plans, API keys, reports) are
  platform-admin only → **403** for client roles.
- Writes on a client's own data require `ClientAdmin` → viewers get **403**.
- Anything belonging to *another* client returns **404**, on reads and
  writes alike — cross-client existence is never revealed. List endpoints
  are silently scoped to the caller's client regardless of filters.

### Optimistic concurrency

Aggregates whose updates can race (`EmploymentContract`, `PayrollRun`,
`LeaveRequest`, `ExpenseClaim`, `ContractAmendment`, `TerminationRequest`,
`BenefitEnrollment`) implement `IVersioned`. EF registers `Version` as a
concurrency token; `AtlasDbContext.SaveChanges` bumps it on every modified
entity. A stale write throws `DbUpdateConcurrencyException`, which the
global handler converts to a 409 telling the caller to reload and retry.
Double decisions that arrive sequentially fail earlier, on the state check,
with a 409 naming the winning state.

## The SQLite `DateTimeOffset` converter

SQLite has no native date-time type; EF's default stores `DateTimeOffset` as
`TEXT`, which cannot be ordered or compared correctly in SQL. `AtlasDbContext`
therefore registers a model-wide `ValueConverter` that persists every
`DateTimeOffset` as its **UTC ticks** (`long`):

```csharp
configurationBuilder
    .Properties<DateTimeOffset>()
    .HaveConversion<DateTimeOffsetToTicksConverter>();
// value => value.UtcTicks
// ticks => new DateTimeOffset(ticks, TimeSpan.Zero)
```

Trade-offs, deliberately accepted:

- `ORDER BY` / `WHERE` on timestamps work in SQL (integers compare
  correctly), which `tests/Atlas.Tests/Infrastructure/DateTimeOffsetConverterTests.cs`
  proves.
- The original offset is *not* round-tripped — a value written at `+08:00`
  reads back at `+00:00` — but it is the **same instant**. All timestamps in
  Atlas are semantically UTC (`*AtUtc` naming), so only the instant matters.
- Calendar dates (start/end/effective/expiry dates) use `DateOnly`, which
  avoids the timezone question entirely for date arithmetic like proration
  and notice periods.

## Related documents

- [API reference](api-reference.md) — every endpoint with auth, shapes, and errors.
- [Getting started](getting-started.md) — run, seed, and walk the happy path.
- [Testing](testing.md) — test taxonomy and the integration harness.
- [ADRs](adr/) — the reasoning behind the individual decisions above.
