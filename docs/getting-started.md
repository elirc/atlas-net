# Getting started

## Prerequisites

- **.NET 10 SDK** — `dotnet --version` should report a 10.x SDK.
- Nothing else: the database is SQLite (a local `atlas.db` file created on
  first run) and there is no frontend, message broker, or external service.

## Build and test

```bash
dotnet build
dotnet test
```

The test suite (domain unit tests + `WebApplicationFactory` integration tests
against in-memory SQLite) needs no setup; see [testing.md](testing.md).

## Run the API

```bash
dotnet run --project src/Atlas.Api
```

In the **Development** environment the app creates `atlas.db` (connection
string `ConnectionStrings:Atlas`, default `Data Source=atlas.db`) and seeds
sample data on first start:

- 6 countries (US, GB, DE, PH, BR, IN) with statutory rates, notice periods,
  and leave policies
- 2 clients (Acme Robotics / USD, Nordwind Analytics / EUR)
- 4 workers, 3 contracts (2 active), onboarding checklists, a pending leave
  request, benefit plans and one enrollment, dated FX rates, and compliance
  documents (one expiring soon, one already expired)
- 3 API keys:

| Key | Role |
| --- | --- |
| `dev-admin-key` | PlatformAdmin |
| `dev-acme-admin-key` | ClientAdmin (Acme Robotics) |
| `dev-acme-viewer-key` | ClientViewer (Acme Robotics) |

Seeding is idempotent — delete `atlas.db` to reset.

Every request except `GET /health` needs the `X-Api-Key` header. Money is
`decimal` + ISO 4217 code, dates are `yyyy-MM-dd`, errors are RFC 7807
ProblemDetails. Full endpoint documentation: [api-reference.md](api-reference.md).

## Walkthrough: country → client → worker → contract → payroll → invoice

The commands below assume the API on `http://localhost:5000` (adjust the port
to what `dotnet run` prints) and `jq` for readability (optional). Capture the
`id` fields from each response for the next step.

```bash
BASE=http://localhost:5000
AUTH='X-Api-Key: dev-admin-key'
```

### 1. Health (no key needed)

```bash
curl $BASE/health
```

### 2. Create a country

```bash
curl -s -X POST $BASE/api/countries -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "code": "NL", "name": "Netherlands", "currencyCode": "EUR",
  "employerCostRate": 0.18, "employeeDeductionRate": 0.27, "minimumNoticeDays": 30
}'
```

### 3. Create a client company

```bash
curl -s -X POST $BASE/api/clients -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "name": "Tulip Trading", "billingEmail": "billing@tulip.example",
  "headquartersCountryCode": "NL", "managementFeeRate": 0.10
}'
# => { "id": "<CLIENT_ID>", ... "billingCurrencyCode": "EUR" }
```

### 4. Create a worker

```bash
curl -s -X POST $BASE/api/workers -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "fullName": "Anna de Vries", "email": "anna@example.com", "countryCode": "NL"
}'
# => { "id": "<WORKER_ID>", ... }
```

### 5. Create the contract (starts as Draft)

```bash
curl -s -X POST $BASE/api/contracts -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "clientId": "<CLIENT_ID>", "workerId": "<WORKER_ID>",
  "jobTitle": "Product Engineer", "monthlySalary": 6000, "startDate": "2026-07-01"
}'
# => { "id": "<CONTRACT_ID>", "status": "Draft", "currencyCode": "EUR", ... }
```

### 6. Complete onboarding, then activate

Activation is blocked (409) until every required checklist item is done:

```bash
curl -s $BASE/api/contracts/<CONTRACT_ID>/onboarding -H "$AUTH"
# For each item with "isRequired": true and "isCompleted": false:
curl -s -X POST $BASE/api/contracts/<CONTRACT_ID>/onboarding/<ITEM_ID>/complete \
  -H "$AUTH" -H 'Content-Type: application/json' -d '{ "notes": "Verified" }'

curl -s -X POST $BASE/api/contracts/<CONTRACT_ID>/activate -H "$AUTH"
# => { "status": "Active", ... }
```

### 7. Run payroll for the month

```bash
curl -s -X POST $BASE/api/payroll-runs -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "countryCode": "NL", "year": 2026, "month": 7
}'
# => draft run with one payslip:
#    gross 6000.00, employerCost 1080.00 (18%),
#    employeeDeductions 1620.00 (27%), netPay 4380.00, totalCost 7080.00
```

### 8. Complete the run — invoices are issued

```bash
curl -s -X POST $BASE/api/payroll-runs/<RUN_ID>/complete -H "$AUTH"
# => { "run": { "status": "Completed", ... },
#      "invoices": [ { "invoiceNumber": "INV-202607-NL-001",
#                      "payrollSubtotal": 7080.00,
#                      "managementFee": 600.00,
#                      "total": 7680.00,
#                      "billingCurrencyCode": "EUR", "fxRateApplied": 1, ... } ] }
```

The client bills in EUR and payroll ran in EUR, so no FX rate was needed. If
the billing currency differed, completion would 409 until a rate covering the
period exists (`POST /api/fx-rates`, platform admin).

### 9. Inspect the results

```bash
curl -s "$BASE/api/invoices?clientId=<CLIENT_ID>" -H "$AUTH"
curl -s "$BASE/api/reports/headcount?asOf=2026-07-15" -H "$AUTH"
curl -s "$BASE/api/reports/payroll-costs?countryCode=NL" -H "$AUTH"
```

### 10. See the authorization model in action

```bash
# Issue a viewer key scoped to the client:
curl -s -X POST $BASE/api/api-users -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "name": "Tulip viewer", "role": "ClientViewer", "clientId": "<CLIENT_ID>"
}'
# => the "apiKey" value is shown only once

# The viewer can read its own client but cannot write (403) and
# cannot see other clients (404):
curl -s $BASE/api/contracts -H 'X-Api-Key: <VIEWER_KEY>'
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  $BASE/api/contracts/<CONTRACT_ID>/terminate \
  -H 'X-Api-Key: <VIEWER_KEY>' -H 'Content-Type: application/json' \
  -d '{ "endDate": "2026-12-31", "reason": "no" }'   # 403
```

## Where to go next

- [api-reference.md](api-reference.md) — every endpoint, shape, and error code.
- [architecture.md](architecture.md) — layering and domain invariants.
- [adr/](adr/) — why the code is the way it is.
- [testing.md](testing.md) — how the suite is organized and run.
