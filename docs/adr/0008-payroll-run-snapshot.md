# ADR 0008 — Payroll runs are immutable snapshots with one-way completion

**Status**: accepted

## Context

Payroll must be reproducible: what a worker was paid and what a client was
billed for a month cannot drift when contracts, amendments, expenses, or
enrollments change afterwards. Payroll also spans every client in a country,
so it cannot be a client-scoped operation.

## Decision

- A `PayrollRun` is unique per **country + calendar month** (unique index)
  and is a platform-admin operation.
- **Creating** the run computes and stores every `Payslip` immediately —
  effective salary from history, final-month proration and leave payout for
  ending contracts, expense reimbursements (marked `Reimbursed` in the same
  unit of work so they can never pay twice), and benefit premium splits.
  From that point the run is a snapshot: later amendments do not rewrite it.
- **Completing** the run is one-way (`Draft → Completed`) and issues exactly
  one invoice per client (unique index on run+client), with the management
  fee applied to gross only and FX conversion at the period's effective
  rate. A missing FX rate fails the completion with 409 and leaves the run
  draft — invoicing is all-or-nothing.
- Coverage is deliberately coarse: a contract covering *any* part of the
  month is paid the full month, except the final month of an ending
  contract, which is prorated by calendar days.

## Consequences

- Payslips and invoices are stable historical records; reports aggregate
  them without recomputation.
- Ordering races become well-defined: an amendment approved after run
  creation affects the *next* run (pinned by tests), and the draft run pays
  the snapshot.
- There is no "recalculate draft" or "amend completed run" — corrections
  would be a new feature (e.g. off-cycle adjustment runs), not a mutation.
