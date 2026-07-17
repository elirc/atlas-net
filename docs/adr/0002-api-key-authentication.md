# ADR 0002 — API-key header auth with client-scoped roles

**Status**: accepted

## Context

Atlas is a machine-to-machine API: callers are the platform's own operators
and client companies' backoffice systems, not browser users. There is no
login UI, no third-party IdP, and no need for delegated user consent.

## Decision

Authenticate with a static secret in the **`X-Api-Key`** header, resolved by
a custom `AuthenticationHandler` against `ApiUser` rows in the database.
Each key carries a role (`PlatformAdmin`, `ClientAdmin`, `ClientViewer`) and,
for client roles, exactly one `ClientId`, both issued as claims. Keys are
generated server-side (`atlas_<32 hex>`), returned **once** at creation,
listed masked afterwards, and revoked by deactivation (not deletion, so the
audit trail keeps the row).

## Consequences

- No token infrastructure (JWT signing, refresh, clock skew) to operate;
  revocation is immediate because every request hits the `ApiUsers` table.
- That lookup is one indexed query per request — fine at this scale, and it
  doubles as the revocation check.
- Scope is coarse: a key is a role + optional client, nothing finer. The
  role/scope pairing is validated at issue time (`PlatformAdmin` must not
  carry a client id; client roles must).
- Keys are stored in plaintext; hashing them would be the first hardening
  step before real production use.
