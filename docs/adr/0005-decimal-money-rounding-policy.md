# ADR 0005 — Decimal money with a single round-once, away-from-zero policy

**Status**: accepted

## Context

Payroll is the heart of the product: gross salaries split into employer
costs, deductions, net pay, benefit shares, reimbursements, management fees,
and FX conversions. Rounding drift — a cent appearing or disappearing when
components are re-added — is the classic failure mode.

## Decision

- All money is `decimal` with an ISO 4217 currency code alongside; no
  floats, no currency-less amounts.
- One rounding function, `PayrollCalculator.RoundMoney`: 2 decimal places,
  `MidpointRounding.AwayFromZero` (mirroring how statutory amounts are
  stated).
- **Round once per component, then derive the rest by arithmetic**:
  - payroll: employer cost and deductions are rounded products; net is
    `gross - deductions`, total cost is `gross + employerCost` — so
    `net + deductions == gross` holds *exactly*;
  - benefit split: employer share is the rounded product, employee share is
    the **remainder** (`MonthlyCost - EmployerShare`), never independently
    rounded — the split re-adds to the premium to the cent;
  - FX: convert the already-summed invoice total once
    (`RoundMoney(total * rate)`), never per line;
  - final pay: the daily rate is rounded once, then multiplied by days.

## Consequences

- Cent conservation is a testable invariant, and `MoneyInvariantTests`
  sweeps grids of amounts and rates to prove it.
- Sums of per-item roundings can legitimately differ from rounding a sum;
  the code always picks *which* quantity is authoritative (the component,
  the share, the invoice total) and rounds only there.
- `AwayFromZero` rather than banker's rounding: predictable for people
  checking payslips by hand, and consistent with statutory tables.
