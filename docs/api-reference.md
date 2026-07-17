# API reference

Every endpoint under `/api` requires an API key in the **`X-Api-Key`** header;
only `GET /health` is anonymous. Errors are RFC 7807 ProblemDetails
(`application/problem+json`) throughout.

**Roles** (see [architecture](architecture.md#authorization-model)):

- **Platform** — `PlatformAdmin` keys only; client-scoped keys get **403**.
- **Any** — any authenticated key; results are silently scoped to the
  caller's client for client roles, and cross-client resources return **404**.
- **Manage** — `PlatformAdmin`, or the `ClientAdmin` of the client that owns
  the resource. Viewers get **403**; another client's admin gets **404**.

**Common status codes**

| Code | Meaning |
| --- | --- |
| 400 | Validation failure — `errors` maps field name → messages |
| 401 | Missing, unknown, or deactivated API key |
| 403 | Authenticated but insufficient role for this action |
| 404 | Not found — including resources owned by another client |
| 409 | Domain-rule conflict (`detail` explains) or stale concurrency token |

**Pagination** — every flat list endpoint accepts `?page=` (1-based, default 1)
and `?pageSize=` (default 50, max 200); out-of-range values return 400. Bodies
are plain JSON arrays; metadata travels in the `X-Total-Count`, `X-Page`, and
`X-Page-Size` response headers. Bounded sub-resource lists (onboarding
checklist, salary history, leave balances, worker documents) are not paged.

---

## Health

### `GET /health` — anonymous

Liveness/readiness with a database connectivity probe.
Returns `{ status, service: "atlas-net", timestampUtc, checks: [{ name, status, description, durationMs }] }`.
`200` when healthy, `503` otherwise.

---

## Countries

### `GET /api/countries` — Any
Paged list of supported hiring countries.

### `GET /api/countries/{code}` — Any
`404` for unknown codes. Response:
`{ code, name, currencyCode, employerCostRate, employeeDeductionRate, minimumNoticeDays, isActive }`.

### `POST /api/countries` — Platform
```json
{ "code": "NL", "name": "Netherlands", "currencyCode": "EUR",
  "employerCostRate": 0.18, "employeeDeductionRate": 0.27, "minimumNoticeDays": 30 }
```
`code` must be 2 letters, `currencyCode` 3 letters, both rates in `[0, 1)`,
`minimumNoticeDays` optional (default 30, max 365). Codes are upper-cased.
`201` with the country; `400` on validation; `409` if the code already exists.

---

## Clients

### `GET /api/clients` — Any
Paged. Client-scoped callers see only their own client.

### `GET /api/clients/{id}` — Any
`404` when unknown *or* belonging to another caller. Response:
`{ id, name, legalName, billingEmail, headquartersCountryCode, managementFeeRate, billingCurrencyCode, createdAtUtc }`.

### `POST /api/clients` — Platform
```json
{ "name": "Acme Robotics", "legalName": "Acme Robotics, Inc.",
  "billingEmail": "billing@acme.example", "headquartersCountryCode": "US",
  "managementFeeRate": 0.10, "billingCurrencyCode": "USD" }
```
`legalName` defaults to `name`; `managementFeeRate` defaults to 0.10 and must
be in `[0, 1)`; `billingCurrencyCode` defaults to the HQ country's currency.
`201`; `400` on validation / unsupported HQ country.

---

## Workers

### `GET /api/workers` — Any
Paged; filter `?countryCode=`. Client-scoped callers see only workers who have
a contract with their client.

### `GET /api/workers/{id}` — Any
`404` unless the worker is contracted to the caller's client (or caller is
platform admin). Response:
`{ id, fullName, email, countryCode, dateOfBirth, createdAtUtc }`.

### `POST /api/workers` — Platform
```json
{ "fullName": "Maria Santos", "email": "maria@example.com",
  "countryCode": "PH", "dateOfBirth": "1993-04-12" }
```
Email is lower-cased and unique. `201`; `400` on validation / unsupported
country; `409` on duplicate email.

---

## Employment contracts

### `GET /api/contracts` — Any
Paged; filters `?clientId=`, `?status=Draft|Active|Terminated` (unknown status
→ 400). Client-scoped callers always get their own contracts, whatever filter
they pass.

### `GET /api/contracts/{id}` — Any
`404` cross-client. Response:
`{ id, clientId, workerId, countryCode, jobTitle, monthlySalary, currencyCode, startDate, endDate, status, createdAtUtc, activatedAtUtc, terminatedAtUtc, terminationReason }`.

### `POST /api/contracts` — Manage (of the target client)
```json
{ "clientId": "…", "workerId": "…", "jobTitle": "Engineer",
  "monthlySalary": 100000, "startDate": "2026-01-01" }
```
Country and currency come from the worker's country; an initial `SalaryRecord`
and the default onboarding checklist are created with the contract.
`201`; `400` on validation / unknown client or worker / inactive hiring
country; `403` when a client admin targets another client; `409` when the
worker already has a non-terminated contract.

### `POST /api/contracts/{id}/activate` — Manage
No body. Draft → Active. `409` (listing the items) while required onboarding
items are pending, or when not draft.

### `POST /api/contracts/{id}/terminate` — Manage
```json
{ "endDate": "2026-12-31", "reason": "Position eliminated" }
```
Immediate, for-cause termination (the notice-checked flow is
`/api/termination-requests`). Active → Terminated. `400` when `endDate`
missing; `409` when not active, reason blank, or `endDate` before the start
date.

---

## Onboarding

### `GET /api/contracts/{contractId}/onboarding` — Any
`{ contractId, isComplete, items: [{ id, contractId, type, title, isRequired, isCompleted, completedAtUtc, notes }] }`.
`isComplete` means every *required* item is done. `404` cross-client.

### `POST /api/contracts/{contractId}/onboarding/{itemId}/complete` — Manage
Optional body `{ "notes": "Verified" }`. `409` when already completed.

---

## Compliance documents

### `GET /api/workers/{workerId}/documents` — Any
`404` unless the worker is contracted to the caller's client. Each document:
`{ id, workerId, type, name, issuedDate, expiryDate, status, createdAtUtc }`
where `status` is computed as `Valid`, `ExpiringSoon`, or `Expired`.

### `POST /api/workers/{workerId}/documents` — Platform
```json
{ "type": "Passport", "name": "PH passport",
  "issuedDate": "2022-01-01", "expiryDate": "2032-01-01" }
```
Types: `Passport`, `Visa`, `WorkPermit`, `ProfessionalCertification`, `Other`
(per `ComplianceDocumentType`).
`400` when expiry precedes issue; `404` unknown worker.

### `GET /api/compliance/expiring?withinDays=N` — Platform
Documents already expired or expiring within the window (default 30).
`400` when `withinDays` is negative.

---

## Payroll runs

All payroll endpoints are **Platform** (403 for client roles).

### `GET /api/payroll-runs` — Platform
Paged; filter `?countryCode=`. Summaries with money totals:
`{ id, countryCode, year, month, status, payslipCount, totalGross, totalEmployerCost, totalReimbursements, totalBenefitsEmployerCost, totalBenefitsEmployeeDeductions, totalNet, totalCost, createdAtUtc, completedAtUtc }`.

### `GET /api/payroll-runs/{id}` — Platform
`{ run: <summary>, payslips: [{ id, payrollRunId, contractId, workerId, clientId, currencyCode, grossSalary, employerCost, employeeDeductions, reimbursements, benefitsEmployerCost, benefitsEmployeeDeduction, unusedLeavePayout, netPay, totalCost }] }`.

### `POST /api/payroll-runs` — Platform
```json
{ "countryCode": "PH", "year": 2026, "month": 7 }
```
Creates a draft run and computes payslips (see
[architecture — payroll composition](architecture.md#payroll-composition)).
Year must be 2000–2100, month 1–12. `201`; `400` on validation; `409` when
the country is unknown, a run for that country+month exists, or no contract
covers the month.

### `POST /api/payroll-runs/{id}/complete` — Platform
No body. One-way; issues one invoice per client and returns
`{ run, invoices }`. `404` unknown run; `409` when already completed or when
an FX rate needed for a client's billing currency is missing for the period
(the run stays draft).

---

## Invoices

### `GET /api/invoices` — Any
Paged; filter `?clientId=`. Client-scoped callers see only their own.

### `GET /api/invoices/{id}` — Any
`404` cross-client. Response:
`{ id, invoiceNumber, clientId, payrollRunId, currencyCode, payrollSubtotal, managementFee, total, billingCurrencyCode, fxRateApplied, totalInBillingCurrency, issuedAtUtc }`.
`invoiceNumber` format: `INV-yyyymm-CC-nnn`. `fxRateApplied` is `1` when the
billing currency equals the payroll currency.

---

## API users (keys)

All **Platform** only.

### `GET /api/api-users` — Platform
Paged; keys are masked (`****abcd`).

### `POST /api/api-users` — Platform
```json
{ "name": "Acme admin", "role": "ClientAdmin", "clientId": "…" }
```
Roles: `PlatformAdmin` (must *not* carry `clientId`), `ClientAdmin` /
`ClientViewer` (must carry one). The generated key (`atlas_<32 hex>`) is
returned **once**, on creation. `400` on role/clientId mismatch or unknown
client.

### `POST /api/api-users/{id}/deactivate` — Platform
Revocation: the key immediately fails authentication (401). `404` unknown id.

---

## Leave

### `GET /api/leave-policies`, `GET /api/leave-policies/{countryCode}` — Any
One policy per country:
`{ id, countryCode, annualLeaveDays, sickLeaveDays, createdAtUtc }`.

### `POST /api/leave-policies` — Platform
```json
{ "countryCode": "PH", "annualLeaveDays": 15, "sickLeaveDays": 15 }
```
Allowances 0–366. `400` validation / unsupported country; `409` when the
country already has a policy.

### `GET /api/leave-requests` — Any
Paged; filters `?contractId=`, `?status=Pending|Approved|Rejected|Cancelled`.

### `GET /api/leave-requests/{id}` — Any
`404` cross-client. Response:
`{ id, contractId, type, startDate, endDate, days, reason, status, requestedAtUtc, decidedAtUtc, decisionNote }`.

### `POST /api/leave-requests` — Manage
```json
{ "contractId": "…", "type": "Annual", "startDate": "2026-08-03",
  "endDate": "2026-08-07", "reason": "Family holiday" }
```
Types: `Annual`, `Sick`. `days` is the working-day (Mon–Fri) count, computed
at submission. `400` validation / foreign or unknown contract; `409` when the
contract is not active, the range spans calendar years, starts before the
contract, contains no working day, overlaps a pending/approved request, no
policy exists for the country, or the balance is insufficient (pending +
approved requests reserve days).

### `POST /api/leave-requests/{id}/approve` — Manage
Optional body `{ "note": "…" }`. `409` unless pending.

### `POST /api/leave-requests/{id}/reject` — Manage
Body `{ "note": "…" }` — the note is **required** (409 without it).

### `POST /api/leave-requests/{id}/cancel` — Manage
No body. `409` unless pending.

### `GET /api/contracts/{contractId}/leave-balances?year=N` — Any
Defaults to the current year:
`{ contractId, year, balances: [{ type, allowanceDays, approvedDays, pendingDays, remainingDays }] }`.
`409` when the country has no leave policy.

---

## Expense claims

### `GET /api/expense-claims` — Any
Paged; filters `?contractId=`, `?status=Pending|Approved|Rejected|Reimbursed`.

### `GET /api/expense-claims/{id}` — Any
`404` cross-client. Response:
`{ id, contractId, currencyCode, description, status, totalAmount, submittedAtUtc, decidedAtUtc, decisionNote, reimbursedInPayrollRunId, reimbursedAtUtc, items: [{ id, description, amount, incurredDate, receiptUrl }] }`.

### `POST /api/expense-claims` — Manage
```json
{ "contractId": "…", "description": "Client visit",
  "items": [{ "description": "Taxi", "amount": 25.00,
               "incurredDate": "2026-07-01",
               "receiptUrl": "https://…" }] }
```
At least one item; amounts positive; `receiptUrl` optional but must be an
absolute http(s) URL. Currency is always the contract's local currency.
`400` validation; `409` when the contract is not active.

### `POST /api/expense-claims/{id}/approve|reject` — Manage
Approve takes an optional note; reject requires one. `409` unless pending.
There is no reimburse endpoint: creating a payroll run reimburses every
approved claim of the contracts in the run and stamps
`reimbursedInPayrollRunId`.

---

## Contract amendments & salary history

### `GET /api/contract-amendments` — Any
Paged; filters `?contractId=`, `?status=Pending|Approved|Rejected|Cancelled`.

### `GET /api/contract-amendments/{id}` — Any
`404` cross-client. Response:
`{ id, contractId, newMonthlySalary, newJobTitle, effectiveDate, reason, status, requestedAtUtc, decidedAtUtc, decisionNote }`.

### `POST /api/contract-amendments` — Manage
```json
{ "contractId": "…", "newMonthlySalary": 120000,
  "newJobTitle": "Staff Engineer", "effectiveDate": "2026-09-01",
  "reason": "Promotion" }
```
Must change the salary, the title, or both; salary positive. `400`
validation; `409` when the contract is not active, the effective date
precedes the contract start, or another amendment is already pending.

### `POST /api/contract-amendments/{id}/approve` — Manage
Optional note. Applies the change to the contract's current terms **and**
appends an immutable `SalaryRecord`. `409` unless pending, or when the
contract is no longer active (the amendment then stays pending).

### `POST /api/contract-amendments/{id}/reject|cancel` — Manage
Reject requires a note. `409` unless pending.

### `GET /api/contracts/{contractId}/salary-history` — Any
Append-only, ordered by effective date then creation:
`[{ id, contractId, monthlySalary, jobTitle, effectiveDate, source: "Initial"|"Amendment", amendmentId, createdAtUtc }]`.

---

## FX rates

### `GET /api/fx-rates` — Any
Paged; filters `?baseCurrency=`, `?quoteCurrency=`.

### `POST /api/fx-rates` — Platform
```json
{ "baseCurrencyCode": "PHP", "quoteCurrencyCode": "USD",
  "rate": 0.0175, "effectiveDate": "2026-07-01" }
```
3-letter codes, base ≠ quote, rate positive. Rates are append-only and unique
per base/quote/effective-date (`409` on duplicates). Invoicing picks the rate
effective for the payroll period (latest effective date on or before the
month's end).

---

## Benefits

### `GET /api/benefit-plans`, `GET /api/benefit-plans/{id}` — Any
Paged; filter `?countryCode=`. Response includes the computed split:
`{ id, countryCode, name, description, monthlyCost, employerContributionRate, employerShare, employeeShare, isActive, createdAtUtc }`.

### `POST /api/benefit-plans` — Platform
```json
{ "countryCode": "PH", "name": "HealthGuard Plus",
  "description": "Private health insurance", "monthlyCost": 4500,
  "employerContributionRate": 0.80 }
```
Cost positive, rate in `[0, 1]`, name unique per country (`409`).

### `POST /api/benefit-plans/{id}/deactivate` — Platform
Deactivated plans accept no new enrollments; existing ones keep running.

### `GET /api/benefit-enrollments` — Any
Paged; filters `?contractId=`, `?status=Active|Ended`.

### `GET /api/benefit-enrollments/{id}` — Any
`404` cross-client. Response:
`{ id, contractId, benefitPlanId, benefitPlanName, startDate, endDate, status, createdAtUtc, endedAtUtc }`.

### `POST /api/benefit-enrollments` — Manage
```json
{ "contractId": "…", "benefitPlanId": "…", "startDate": "2026-02-01" }
```
`400` validation / unknown plan; `409` when the contract is not active, the
plan is inactive or in a different country, coverage would start before the
contract, or an active enrollment in the same plan already exists.

### `POST /api/benefit-enrollments/{id}/end` — Manage
```json
{ "endDate": "2026-12-31" }
```
One-way. `409` when already ended or the end date precedes the start.
Payroll charges the premium for every month the enrollment covers any part of.

---

## Termination requests & final pay

### `GET /api/termination-requests` — Any
Paged; filters `?contractId=`, `?status=Pending|Approved|Rejected|Cancelled`.

### `GET /api/termination-requests/{id}` — Any
`404` cross-client. Response:
`{ id, contractId, reason, noticeDate, proposedEndDate, status, requestedAtUtc, decidedAtUtc, decisionNote }`.

### `POST /api/termination-requests` — Manage
```json
{ "contractId": "…", "reason": "Role eliminated",
  "proposedEndDate": "2026-12-31" }
```
`noticeDate` is stamped as today (UTC); the proposed end date must be at
least the country's `minimumNoticeDays` after it, and not before the contract
start. `400` validation; `409` when the contract is not active, notice is too
short, or another request is already pending.

### `POST /api/termination-requests/{id}/approve` — Manage
Optional note. Terminates the contract effective the proposed end date. If
the contract is no longer active, the approve fails with 409 and the request
**stays pending**. The contract's final payroll month prorates salary by
calendar days and pays out unused annual leave.

### `POST /api/termination-requests/{id}/reject|cancel` — Manage
Reject requires a note. `409` unless pending.

---

## Reports

All **Platform** only (403 for client roles).

### `GET /api/reports/headcount?asOf=2026-07-01`
Activated contracts covering the date (terminated contracts still serving
notice count until their end date):
`{ asOf, total, byCountry: [{ countryCode, count }], byClient: [{ clientId, clientName, count }] }`.

### `GET /api/reports/payroll-costs?countryCode=&clientId=&fromYear=&fromMonth=&toYear=&toMonth=`
Payslip totals grouped per year+month+currency (inclusive period range,
months 1–12 or 400):
`[{ year, month, currencyCode, payslipCount, totalGross, totalEmployerCost, totalReimbursements, totalBenefitsEmployerCost, totalUnusedLeavePayout, totalCost }]`.

### `GET /api/reports/compliance-expiries?withinDays=N`
Documents expired or expiring within the window (default 60; negative → 400),
with `daysUntilExpiry` (negative when already expired) and computed status.

### `GET /api/reports/invoice-aging?clientId=`
Outstanding totals per age bucket (`0-30`, `31-60`, `61-90`, `90+` days since
issue) and billing currency — no payment tracking exists, so every invoice
counts as outstanding:
`{ asOf, rows: [{ bucket, currencyCode, invoiceCount, total }] }`.
