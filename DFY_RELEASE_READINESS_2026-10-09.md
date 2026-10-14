# DFY Release Readiness — 2026-10-14

## Executive decision

**Staging integration is deployed for the core task, payout-display, and mobile Paystack-return fixes. DFY is not yet approved for production money movement.** The backend and frontend changes are merged; the Android release artifact has been built successfully. Remaining gates are the latest frontend main-branch CI completion, real-device APK smoke testing, controlled Paystack Test Mode validation, merchant settlement/commission-routing confirmation, and reconciliation/refund/dispute evidence.

This report supersedes the stale status in the 2026-10-09 report and should be updated as each release gate is evidenced.

## Current repository and deployment state

| Area | Current state | Evidence |
|---|---|---|
| DFY-BE | Task search, currency-precision commission/payout calculation, mobile Paystack return, and marketplace Paystack audit merged | Main includes commits `3cf34d3`, `1489791`, `c980eff`, and audit documentation merge `ed25356`; backend CI passed on runtime commit `c980eff` |
| DFY-FE | Exact backend payout display and task-description validation parity merged | Main commit `f09d1fc`; CI run [38581076848](https://github.com/DoForYou-ZA/DFY-FE/actions/runs/38581076848) was still running at report time |
| dfy-mobile | Browse/dashboard/idle-session changes, backend payout display, task-creation parity, auth styling, and bank-account layout fixes merged | Main commit `c055cb0`; [Android release workflow](https://github.com/DoForYou-ZA/dfy-mobile/actions/runs/38579429541) passed |
| FE staging | Newest confirmed live deploy before the latest task-description merge was commit `1069dc5`; the `f09d1fc` merge triggered a new main workflow and should be checked for deployment completion | [Render service](https://dashboard.render.com/web/srv-dab4fic9v7es73c0id9g) · [staging site](https://dfy-fe-staging.onrender.com) |
| BE staging | Commit `c980eff` is live; Render reported a successful build and deploy | [Render service](https://dashboard.render.com/web/srv-dab4b6id0e5s73da8uk0) · [staging API](https://dfy-be-staging.onrender.com/api/v1) |
| Android artifact | `dfy-mobile-android-release` artifact was created successfully for main SHA `c055cb0`, size approximately 20.6 MB, not expired when checked | [Actions run and artifact download](https://github.com/DoForYou-ZA/dfy-mobile/actions/runs/38579429541) |

## Changes merged in this integration pass

### Backend
- Available-task search now covers task name, description, area, category, and notes, while preserving availability/payment/escrow filters.
- Commission and runner payout are normalized to cents in the backend; the existing minimum commission is retained.
- Mobile Paystack checkout return handling is included. Payment success must still be verified independently by the backend.
- The Paystack marketplace operations audit was merged at `docs/PAYSTACK_MARKETPLACE_OPERATIONS_AUDIT.md`.

### Frontend
- Browse, active, and completed task views use the backend's stored runner payout instead of recomputing a commission formula in the browser.
- Missing or invalid payout data is presented as unavailable rather than inventing a value.
- Task description input validation now matches backend constraints.

### Mobile
- Browse search/filtering and load-error retry UX improved; API categories are fetched when available.
- Dashboard profile completion and activity metrics layout improved.
- Inactivity logout after 15 minutes of inactivity added.
- Browse, active, and completed task screens display backend payout amounts rather than locally estimating 85% of task budget.
- Task creation validation, authentication theme, and bank-account action layout aligned with web/backend.

## Canonical lifecycle and financial safety

1. A creator posts a task and completes Paystack checkout.
2. The backend verifies the provider transaction independently before treating the task as paid.
3. A runner accepts and completes the task.
4. The task enters `AwaitingConfirmation`; the creator has **24 hours** to confirm completion or raise a dispute.
5. A dispute blocks normal payout release.
6. If no confirmation or dispute is recorded by expiry, the backend queues the runner payout.
7. The runner is marked paid only after Paystack confirms a successful transfer. An escrow-release state or queued payout is not proof of a successful bank transfer.

Client apps must never calculate commission or treat browser dismissal/client-side state as proof of payment. For a R200 budget, the example calculation is R30 commission and R170 runner payout; that calculation is not evidence that a real payment or transfer occurred.

## Staging evidence and limitations

- Render reports the BE deployment for `c980eff` as live and successful, and the FE deployment for `1069dc5` as live and successful.
- FE staging logs show successful HTTP 200 responses for the homepage and static assets.
- BE staging logs show `DFY_E2E_TEST_MODE` enabled and the payout processor explicitly skipping Paystack payout processing. This is the intended safety guard for E2E tests; **no real payout was tested by those logs**.
- The latest Android CI run passed typecheck/bundle and Android release build. This is a CI artifact, not proof of installation or a full real-device workflow.
- The frontend PR #63 mobile-responsive E2E retry passed; PR #63 was then merged. The post-merge main workflow for `f09d1fc` was still running when this report was refreshed.
- A direct automated page-open check was unavailable from the web inspection tool; Render deploy records and service logs were used as deployment evidence instead.

## Paystack marketplace operations — unresolved P0 items

The repository contains checkout initialization, transaction verification, webhook signature checks, refund/transfer handling, and payout reconciliation code. Implementation alone does not prove the merchant account is configured correctly or that the marketplace money flow is approved.

- Confirm in the Paystack Dashboard and with Paystack support which merchant settlement account receives collected task funds and how platform commission is allocated.
- The inspected checkout implementation does **not** establish that Paystack Split Payments/subaccounts are configured. Do not tell users that commission automatically routes to a separate DFY account until verified.
- Confirm in writing the supported marketplace/escrow model and any South African compliance requirements. DFY's `EscrowHeld`/`EscrowReleased` labels describe application workflow; they do not prove regulated escrow or safeguarded client-money status.
- Run controlled **Test Mode** checkout and server-side verification, invalid/duplicate/out-of-order webhook, refund, dispute, failed transfer, retry, reversal, idempotency, and settlement-reconciliation scenarios.
- Verify runner bank-recipient creation/verification and user notifications. Keep live money movement disabled during tests; do not disable the staging E2E payout guard unless test credentials and isolation are explicitly confirmed.
- Reconcile the platform commission ledger, Paystack fees/VAT, task budget, refund records, transfer references, and eventual bank settlement against provider evidence.
- Do not promise instant payouts.

## P1 — release validation still required

- [ ] Confirm the FE main workflow for `f09d1fc` finishes green and the Render FE deploy for that commit is live.
- [ ] Download/install the Android artifact on a device or emulator; verify login, task browse/search, create-task validation, checkout return, runner accept/complete, creator confirm/dispute, bank setup, and payout status.
- [ ] Confirm the release build uses the intended production API configuration, not staging endpoints.
- [ ] Run controlled Paystack Test Mode end-to-end checks and capture evidence for payment verification, webhook idempotency, refunds, disputes, failed transfers/retries, and reconciliation.
- [ ] Audit any pre-existing tasks with missing/stale `PayoutAmount`; clients now correctly avoid guessing, so stale database values must be reconciled rather than hidden by client fallbacks.
- [ ] Complete a final source audit for retired Ozow/wallet paths and verify no active references before removing anything.
- [ ] Review production secrets, webhook URLs/signatures, payout recipient requirements, settlement setup, support/operations inboxes, and incident handling before enabling live money movement.

## Open pull requests that must not be blindly merged

- **DFY-FE #20** remains a draft reusable task-card refactor. Its branch contains stale client-side `budget * 0.85` payout fallbacks and whole-rand formatting in active/completed task paths. Keep it unmerged until rebased and corrected to preserve the backend payout source of truth, then rerun CI.
- **DFY-BE #36** is a release-readiness document update based on the older 2026-10-09 status. It should be superseded by a current report rather than merged unchanged.
- The Paystack operations audit in `docs/PAYSTACK_MARKETPLACE_OPERATIONS_AUDIT.md` is the detailed companion checklist for merchant configuration and Test Mode evidence.

## Release decision

**Staging integration: deployed for the latest confirmed backend and prior frontend commit. Android build: successful. Production readiness: not yet approved.** Do not enable live payments/payouts or announce full launch readiness until the P0 Paystack, settlement, compliance, and reconciliation evidence is collected and the P1 device/configuration checks pass.
