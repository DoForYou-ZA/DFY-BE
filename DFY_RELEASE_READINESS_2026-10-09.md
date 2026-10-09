# DFY Release Readiness — 2026-10-09

## Executive status

**Staging integration has materially improved, but DFY is not yet production-ready.** The canonical 24-hour confirmation lifecycle passed the latest FE staging E2E run, the mobile web-responsiveness suite passed, and the backend scenario suite plus staging-only CORS test support are merged. Actual Paystack payment and runner-transfer validation, a native mobile build/device smoke test, and a final refund/dispute reconciliation review remain release gates.

## Repository status

| Area | Current state | Evidence |
|---|---|---|
| DFY-BE | Paystack/escrow hardening merged; CI scenario suite and staging-only E2E CORS support merged | PRs #27, #28, #29, #24 and #30 merged; CI passed on PR #24 and #30 heads |
| DFY-BE documentation | Current release readiness published; root README refresh in progress | Release-readiness PR #31 merged; README cleanup branch docs/paystack-current-readme |
| DFY-FE | Bank-code contract and lifecycle E2E fixes merged | PR #46 merged; its CI workflow passed |
| DFY mobile | Expo/React Native app aligned with current API contract | PR #1 merged; TypeScript CI passed |
| Backend staging | Deployed safety commit | Render deployment for commit c21b928 succeeded; JWT signing key was rotated afterward |
| FE staging | Latest FE commit deployed | Render deployment for commit 4130f05 succeeded |

## Canonical task and payment lifecycle

1. Creator posts a task and completes Paystack checkout.
2. The backend verifies payment and holds the funds in escrow.
3. A runner accepts and completes the task.
4. The task enters AwaitingConfirmation; the creator has **24 hours** to confirm completion or raise a dispute.
5. A dispute keeps escrow frozen for review.
6. If the creator does neither within 24 hours, the escrow release path queues a runner payout.
7. The runner is shown as paid only after Paystack confirms the transfer; an escrow release or queued payout is not a successful transfer.

## Completed in this pass

- Merged backend task/payment/payout hardening and deployed it to staging (deployment record last verified for c21b928).
- Fixed the staging expiry-test endpoint to invoke the real escrow release operation and treat a concurrent background-worker release as success when final state confirms the transition.
- Prevented Paystack payout processing while DFY_E2E_TEST_MODE=true, so automated tests can assert EscrowReleased / PayoutPending without submitting external transfers.
- Added a targeted ASP.NET logging filter to stop framework request URL logs from persisting SignalR access_token query parameters.
- Rotated the staging JWT signing key after identifying access tokens in earlier request logs.
- Updated payout notification wording to Paystack.
- Fixed the FE bank selector to use bankCode as the primary field and use valid creator fixtures for E2E payment initialization.
- Removed mobile client-side payment-success calls; checkout browser dismissal is not treated as proof of payment.
- Added creator confirm/dispute actions to mobile, aligned the mobile task type with AwaitingConfirmation, and added default payout-account selection.
- Added a mobile TypeScript CI check and removed wallet navigation from the current mobile flow.
- Verified the latest FE PR workflow: **16 staging E2E tests passed**, and the mobile responsiveness job passed. The post-merge FE main workflow also passed: build, 16 staging E2E tests, and mobile responsiveness are green.
- Merged BE PR #24 (backend scenario suite) and PR #30 (local Vite CORS origins allowed only under explicit E2E mode); both PR-head CI workflows passed.
- Merged the current release-readiness documentation (BE PR #31).
- Closed obsolete BE PR #19 because its older expiry-harness implementation was superseded by the race-safe endpoint now on main.

## Financial safety observations

- During the pre-guard staging window, two Paystack transfer attempts were rejected with an insufficient-balance response. No successful transfer was observed in the inspected log entries.
- After the payout-worker guard deployment, logs showed the worker skipping payout processing in E2E mode and no transfer POSTs in the inspected post-deploy window.
- Framework request logs no longer showed SignalR access_token URLs in the inspected post-deploy window.
- The staging JWT key was rotated to invalidate previously logged tokens.

## Remaining release gates

### P0 — Financial verification

- Run a **controlled Paystack test-mode** checkout/verification test with a newly registered creator.
- Validate runner bank recipient creation/verification, payout queueing, Paystack transfer success webhook/reconciliation, idempotency, and correct user notifications. Do not disable the E2E payout guard unless credentials are confirmed non-live and the test is deliberately isolated.
- Verify failed/insufficient-funds, retry, returned-transfer, duplicate-webhook, dispute, and refund/reconciliation paths end-to-end.
- Confirm no runner is marked paid until the provider confirms the transfer.
- Verify persisted provider transaction references are sufficient to reconcile payment, transfer, and refund records across retries and duplicate events.

### P1 — Mobile release

- Run a native Android build from dfy-mobile (and iOS if supported); current mobile CI verifies TypeScript, not a distributable native binary.
- Install on a real/emulated device and smoke-test login, task creation/checkout handoff, runner task acceptance/completion, creator confirmation/dispute, bank setup, and payout status.
- Verify API base URL and production build configuration; do not ship staging endpoints in the release build.
- Confirm the app never treats browser dismissal or a client-side response as payment confirmation.

### P1 — Contract and legacy cleanup

- BE PR #30 is merged and deployed; it allows the local Vite origin only when E2E mode is enabled. The post-deploy log check found no CORS rejection for that origin.
- Complete a focused source audit of remaining retired Ozow/wallet configuration, classes and client methods. Remove or explicitly quarantine unused paths only after confirming there are no active references.
- Refresh the September 18 audit line-by-line; its historical table still contains outdated Ozow/refund/authorization claims.
- The repository root README is being updated separately because it still describes the old Ozow/.NET 8 integration and retired payment endpoints.

## Release decision

**Do not declare production-ready yet.** Core staging task lifecycle and mobile responsiveness checks are green, the E2E payout safety guard is active, and CI scenario coverage has been merged. Production sign-off still depends on controlled Paystack payment/transfer verification, native mobile build/device validation, and final refund/dispute, reconciliation, and legacy-code review.
