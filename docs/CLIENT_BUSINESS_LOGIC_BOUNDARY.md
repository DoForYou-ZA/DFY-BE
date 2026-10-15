# Client business-logic boundary

## Purpose

DFY-BE is the authoritative implementation of DoForYou marketplace rules. DFY-FE and dfy-mobile are clients of the same versioned HTTP API; they must not independently decide whether a marketplace action is allowed or what money is owed.

## Rules that belong in DFY-BE

- Authorization and creator/runner role permissions.
- Task lifecycle transitions and allowed actions.
- Task budget validation and persisted monetary amounts.
- Platform fee, runner net earnings, and payment totals.
- Paystack checkout initialization and server-side payment verification.
- Escrow state, dispute blocking, the 24-hour creator confirmation deadline, and payout eligibility.
- Bank-account verification and payout status.
- Idempotency, webhook handling, and financial reconciliation.

The backend must validate every mutation even if a client hides or disables the corresponding button. A client response, URL return, local state, or queued payout is never proof of payment or payout completion.

## Client responsibilities

Both clients may validate fields for immediate user feedback and format server-provided values for display. Such client-side checks are usability aids only; the backend repeats authoritative validation.

- Display the budget, fee, and runner net amount returned by the API. Do not calculate an authoritative payout from a hard-coded percentage.
- Display the canonical task status returned by the API. Do not invent a status transition locally.
- Use backend action responses to refresh task/payment state.
- Treat queued/pending payout as not paid. Display paid only after the API reports provider-confirmed success.
- Never put Paystack secret credentials or other server secrets in a frontend/mobile bundle.

## Change propagation policy

1. Implement rule changes in DFY-BE first and add/update backend unit and integration tests.
2. Keep request/response schemas and enum values backward compatible where possible.
3. Update the generated/canonical API contract when endpoints, DTOs, statuses, or response fields change.
4. Run DFY-BE tests and the DFY-FE and dfy-mobile compatibility/integration suites before promoting the change.
5. Deploy the backend before clients that require a new contract. For breaking changes, use an additive migration period or versioned endpoint; do not assume old installed APKs update themselves.
6. Existing payments/tasks must follow explicit migration and grandfathering rules. A new fee setting must not retroactively recalculate settled or in-flight transactions unless a reviewed migration specifically requires it.

## Important distinction

A shared API contract synchronizes types and shapes, not the implementation of server-side rules. The source of truth is the executable code and tests in DFY-BE. The web and mobile clients should consume the resulting API decisions rather than maintain parallel copies of those rules.

## Current canonical marketplace lifecycle

Paystack checkout and server-side verification -> escrow -> runner completion -> `AwaitingConfirmation` -> creator confirms or disputes within 24 hours -> on timeout, escrow release queues payout -> payout is shown as paid only after Paystack confirms the transfer. Disputes block automatic release while under review.
