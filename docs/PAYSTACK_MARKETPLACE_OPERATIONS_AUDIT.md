# Paystack Marketplace Operations and Release Audit

**Audit date:** 2026-10-14  
**Repository:** `DoForYou-ZA/DFY-BE`  
**Scope:** Paystack collection, commission accounting, settlement, refunds, runner transfers, disputes, and release readiness.

## Executive decision

**Do not treat the Paystack marketplace payment flow as production-approved solely because the API integration exists.** The repository contains meaningful Paystack collection, verification, refund, bank-recipient, transfer, webhook, and payout-reconciliation code. Live merchant configuration, actual bank settlement, and controlled end-to-end financial evidence still need verification.

DFY's canonical task lifecycle must remain authoritative:

1. Creator pays through Paystack Checkout.
2. Backend verifies the transaction independently and records the paid task.
3. Runner accepts and completes the task.
4. Task enters `AwaitingConfirmation`; creator has 24 hours to confirm or dispute.
5. A dispute blocks normal payout release.
6. If no confirmation or dispute is recorded by expiry, the backend queues the runner payout.
7. The runner is marked paid only after Paystack reports a successful transfer.

**Important:** the application's `EscrowHeld` / `EscrowReleased` statuses describe DFY's workflow state. They do not, by themselves, prove that Paystack provides a regulated escrow or safeguarded client-money account. DFY must obtain written confirmation from Paystack and appropriate South African compliance advice for the proposed marketplace model.

## Findings verified in source

| Area | Current code evidence | Assessment |
|---|---|---|
| Checkout | `Services/PaystackProvider.cs` initializes a Paystack transaction in ZAR for the task budget and returns a checkout reference/URL. | Implemented; validate with controlled Test Mode checkout. |
| Payment verification | Provider verifies a transaction with Paystack; `Controllers/PaymentController.cs` independently checks successful status, reference, amount and task/payment-attempt relationship before activating the task. | Implemented; retain server-side verification as the source of truth. |
| Webhook security | Payment controller validates `x-paystack-signature`; webhook paths process charge, refund and transfer events. | Implemented; run duplicate, invalid-signature, retry and out-of-order event tests. |
| Refunds | Provider exposes refund creation and refund reconciliation; webhook handlers update refund/task/dispute states. | Implemented; end-to-end outcomes and accounting still require verification. |
| Runner bank setup | Banking controller/provider supports bank listing, South African account validation and Paystack transfer-recipient references. | Implemented; verify eligibility, account ownership, failure handling and live/test environment separation. |
| Runner transfers | `PaystackProvider.CreatePayoutAsync` submits ZAR transfers from Paystack balance; payout processor reconciles status and handles retryable failures, failures and reversals. | Implemented; Paystack requires sufficient balance for payout plus applicable transfer fee. |
| 24-hour release | `Services/Background/EscrowReleaseService.cs` polls expired `AwaitingConfirmation` tasks and calls the race-safe escrow release operation. | Implemented in source; verify deployed service is running and test a real expiry through the controlled staging path. |
| Commission | `EscrowService.CalculateCommission` calculates 15%, with a minimum commission of R5; task payout uses persisted `PayoutAmount`. | Verify all task creation/update paths persist matching commission and payout values and FE/mobile display the same runner net amount. |
| Split settlement | The inspected `PaystackProvider` checkout payload initializes the full task budget and does not pass a Paystack `subaccount` or `split_code`. | No automatic Paystack Split Payments allocation was found in the inspected checkout code. Do not tell users the commission is already split to a separate bank account. |
| Settlement reconciliation | Current inspected code focuses on transaction verification and transfers. A full Paystack Settlements API-to-DFY ledger reconciliation was not confirmed. | Release gate: reconcile settlement totals, fees, deductions, refunds, chargebacks and transaction references. |
| Production evidence | `DFY_RELEASE_READINESS_2026-10-09.md` explicitly leaves controlled Paystack payment/transfer validation and final refund/dispute reconciliation as release gates. | Production sign-off remains pending until evidence is attached. |

## Commission and bank settlement: expected treatment

For a R200 creator payment, the intended task allocation is:

- Creator payment: R200.00
- DFY platform commission at 15%: R30.00
- Runner earnings at 85%: R170.00

These are **gross allocation figures before any processing/transfer fees or other permitted adjustments**. The backend's minimum commission rule must also be reflected in product copy and tests for low-value tasks.

The current inspected integration does not automatically route R30 into a separate DFY commission bank account via a Paystack split. Paystack's main business settlement is paid to the payout bank account configured in the Paystack Dashboard's **Settings → Accounts** area. Runner payouts are separate transfers from the Paystack balance to the verified runner recipient.

Before enabling any split/subaccount configuration, Paystack must confirm in writing that the proposed marketplace model, fee allocation, and creator confirmation/dispute hold are supported for this South African account. Standard split settlement should not send the runner's share to the runner before DFY's eligibility rules authorize release. Do not create per-runner subaccounts or enable split settlement as a shortcut without this approval.

## Paystack merchant configuration to verify

These steps require an authorised DFY operator in the Paystack Dashboard; they cannot be safely completed from repository access alone.

- [ ] Confirm the registered business profile and settlement/payout bank account belong to DoForYou.
- [ ] Confirm the exact settlement bank name and masked account details with the authorised business owner; never commit bank details to the repository.
- [ ] Configure Test Mode webhook at `/api/v1/payment/webhook` and verify the test secret matches the deployed staging environment.
- [ ] Confirm required event subscriptions: `charge.success`, `refund.pending`, `refund.processing`, `refund.processed`, `refund.failed`, `transfer.success`, `transfer.failed`, `transfer.reversed`.
- [ ] Confirm Transfers is enabled for the registered South African business and review any transfer approval/OTP settings with Paystack.
- [ ] Confirm available-balance requirements, transfer limits, fees and whether the payout account has any holds or restrictions.
- [ ] Keep Test Mode and Live Mode keys/webhook settings isolated. Never run a test script with live credentials.
- [ ] Obtain Paystack's written guidance on marketplace payment allocation, timing of settlement, runner payouts after task confirmation, refunds/chargebacks, and whether any escrow/safeguarding approval is needed.

Official references:
- [Paystack Split Payments](https://paystack.com/docs/payments/split-payments/)
- [Paystack Multi-split Payments](https://paystack.com/docs/payments/multi-split-payments/)
- [Paystack Transfers](https://paystack.com/docs/transfers/)
- [How Transfers Work](https://paystack.com/docs/transfers/how-transfers-work/)
- [Paystack Settlements API](https://paystack.com/docs/api/settlement/)
- [Getting your money / settlement bank account](https://support.paystack.com/en/articles/2125314)

Paystack's published documentation states that transfers are available to registered businesses in South Africa, that the Paystack balance must cover transfer amount plus fees, and that a `success` transfer status does not necessarily mean the beneficiary's bank has instantly credited the account. DFY UI and notifications must not promise instant payouts.

## Required automated and manual test matrix

Run only against a controlled staging deployment and Paystack Test Mode credentials.

### Payment collection

- [ ] Successful checkout: valid reference and exact amount; task activates once.
- [ ] Invalid signature: webhook rejected without changing payment/task state.
- [ ] Amount/reference/task mismatch: no activation; auditable failure.
- [ ] Duplicate successful webhook: no duplicate payment, task activation or commission.
- [ ] Delayed/out-of-order webhook and temporary provider outage: safe retry/reconciliation.
- [ ] Checkout cancelled/abandoned: task remains unpaid and can retry with a distinct payment-attempt reference.

### Commission and runner amount

- [ ] R200 task: commission R30, runner payout R170 before transfer fees.
- [ ] Minimum commission boundary: verify the R5 minimum rule and displayed net amount.
- [ ] Decimal/rounding cases and edits/retries: persisted commission + payout equal the values shown to creator and runner.
- [ ] Verify FE and mobile cards show runner's net earnings, not the creator's gross task budget.
- [ ] Confirm refunds and disputes reverse/adjust the correct ledger entries and do not double-count commission.

### Confirmation, dispute and payout

- [ ] Completion enters `AwaitingConfirmation` and establishes a 24-hour deadline.
- [ ] Creator confirmation queues one payout.
- [ ] Creator dispute prevents ordinary payout release.
- [ ] No action by the deadline triggers one payout queue entry.
- [ ] Concurrent confirmation and expiry worker cannot create duplicate payouts.
- [ ] Missing/unverified recipient blocks payout without marking the runner paid.
- [ ] Insufficient Paystack balance waits and retries without duplicate transfers.
- [ ] Ambiguous network timeout reuses the same merchant reference and reconciles before resubmitting.
- [ ] `transfer.success` marks the runner paid exactly once.
- [ ] `transfer.failed` / `transfer.reversed` records the failure and allows a safe, audited retry only after the issue is addressed.
- [ ] Duplicate/out-of-order transfer webhooks do not regress a terminal payout state.
- [ ] Creator and runner notifications describe “queued/processing” versus “paid” accurately.

### Refunds, disputes and settlement accounting

- [ ] Refund pending/processing/processed/failed updates are idempotent.
- [ ] A successful refund prevents any queued or subsequent payout for the refunded task.
- [ ] Refund failure keeps the task/dispute in a reviewable state and alerts the operator.
- [ ] Paystack chargeback notification and evidence deadlines are tracked independently of DFY task disputes.
- [ ] Reconcile Paystack transaction references and settlement records to DFY payment attempts, task budget, commission, provider fees, refunds, chargebacks, transfer fees and runner payouts.
- [ ] Investigate any mismatch; never “fix” money records by directly editing production database state without an auditable corrective operation.

## Recommended implementation sequence

1. **P0 — Complete financial tests:** run the controlled Test Mode checkout and transfer journey, then capture CI and provider evidence.
2. **P0 — Add/verify a financial reconciliation report:** use Paystack transaction, settlement and transfer references to identify missing/mismatched entries. Reconciliation must be read-only initially and must not auto-adjust balances.
3. **P0 — Validate commission invariants:** one authoritative backend calculation, persisted gross/commission/net values, and contract tests for FE/mobile presentation.
4. **P1 — Chargeback operations:** operational inbox/deadline monitoring and an evidence checklist tied to task messages, completion evidence and receipts.
5. **P1 — Customer/runner statements:** payment receipt, commission breakdown, runner payout statement, refund status and clear transfer-pending/failed wording.
6. **P1 — Admin financial view:** pending/failed/reversed payouts, refunds, unsettled payments, reconciliation differences and audit log. Restrict access to authorised admin roles.
7. **P2 — Optional bulk transfers:** consider only after single transfers, balance monitoring, approvals and reconciliation are reliable.
8. **P2 — Split payments/subaccounts:** only after Paystack confirms the use case and timing is compatible with DFY's hold/dispute workflow. Keep disabled until then.

## Production sign-off checklist

A release owner should attach evidence for all of the following before approving live payments:

- [ ] Paystack merchant/settlement configuration confirmed by an authorised DFY operator.
- [ ] Paystack written response on marketplace, settlement, delayed runner payout and safeguarding/escrow implications.
- [ ] Successful Test Mode payment and transfer evidence.
- [ ] Negative tests for duplicate, invalid, failed, reversed, insufficient-balance, refund and dispute scenarios.
- [ ] No task or runner is marked paid before verified provider success.
- [ ] Reconciliation report reviewed against Paystack Dashboard records.
- [ ] FE and mobile display the backend-calculated runner earnings consistently.
- [ ] Native mobile build/device smoke test and production API configuration verified.
- [ ] Monitoring, alerts, support/disputes mailbox ownership and incident runbook confirmed.
- [ ] Legal/compliance sign-off on the intended custody/settlement model.

**Current conclusion:** this audit is a release plan and source inspection, not proof of live-account configuration or successful real-money testing. Keep live payouts disabled for tests and do not declare production readiness until the P0 evidence is collected and reviewed.


## Cross-repository client audit

The payment-provider and financial rules remain in DFY-BE. The clients should display backend values and status only; neither client should calculate authoritative commission or infer payment success from a browser return.

### DFY-FE

- `src/pages/PaymentPages.tsx` explicitly tells users that a checkout return is not proof of payment and that DFY is confirming with Paystack.
- `src/pages/user/BankAccounts.tsx` describes direct Paystack runner payouts, verification, default payout account selection, and keeping existing payout destinations unchanged.
- `src/pages/tasks/BrowseErrands.tsx` uses the backend's `payoutAmount` field as the source of truth for runner earnings, including browse cards, detail modal and claim confirmation. It does not derive the payout from the creator budget.
- `src/pages/tasks/MyActiveTasks.tsx` displays `payoutAmount` for runner tasks.
- `e2e/paystack-manual-checkout.spec.ts` contains a deliberately gated manual Test Mode checkout-to-`RunnerPaid` test. It requires explicit Test Mode confirmation and verified runner bank details.

### dfy-mobile

- `README.md` states that DFY-BE owns payment verification, escrow, disputes, bank verification and payout rules; checkout dismissal is not payment confirmation; queued payouts must not be displayed as paid.
- `src/screens/tasks/BrowseErrandsScreen.tsx` uses backend `payoutAmount` for runner-facing task cards and details.
- `src/screens/tasks/MyActiveTasksScreen.tsx` displays the backend `payoutAmount`.
- `src/screens/user/BankAccountsScreen.tsx` supports verification, setting a default payout account, and removing accounts while retaining the backend's payout-in-progress safeguards.
- `src/api/index.ts` calls backend payment history, payment URL and bank-account APIs; it does not embed Paystack secret keys or own payment logic.

### Client conclusion

The inspected FE and mobile task-card paths already use the backend's runner payout field rather than recomputing 85% in the client. This is correct. Keep regression tests for both clients, especially low-budget tasks affected by DFY's minimum R5 commission. The remaining financial risk is not the card calculation itself; it is verifying persisted backend amounts, actual provider settlement/fees, and payout/refund outcomes against Paystack records.
