# Paystack production setup for DFY

DFY uses Paystack as its single production payment and payout provider.

## Render environment

Set these variables on the DFY backend service:

- `PAYSTACK_SECRET_KEY` — Paystack secret key for the active environment.
- `BACKEND_URL` — public DFY API origin, for example `https://api.doforyou.co.za`.
- `FRONTEND_URL` — public DFY frontend origin.

Do not commit Paystack keys to Git.

## Paystack Dashboard

For staging, switch the Paystack Dashboard to **Test Mode**. Test and live webhook configuration are separate, so configure the webhook while Test Mode is selected.

Configure the webhook URL for the same Paystack environment as the secret key:

`https://<BACKEND_URL_HOST>/api/v1/payment/webhook`

The backend validates the `x-paystack-signature` HMAC-SHA512 signature before processing events.

Enable/confirm these event families:

- `charge.success`
- `refund.pending`
- `refund.processing`
- `refund.processed`
- `refund.failed`
- `transfer.success`
- `transfer.failed`
- `transfer.reversed`

## Transfers

DFY runner payouts use Paystack Transfers from the Paystack balance.

Paystack must have Transfers enabled for the DFY business. Paystack requires a registered business for Transfers, and the balance must contain enough funds for the payout plus the transfer fee.

For fully automated runner payouts, configure Paystack transfer approval so DFY transfers do not require manual OTP approval.

## Runner bank accounts

When a runner adds a bank account:

1. DFY gets the Paystack South African bank list.
2. DFY validates the account with Paystack's South African Account Validation API.
3. DFY creates a Paystack South African (`basa`) transfer recipient.
4. DFY stores the recipient reference on the runner's bank account.
5. The runner can only be paid when the account is active, verified, and has a Paystack recipient reference.
6. The runner can choose a default payout account from their verified, payout-ready accounts. This preference is used when DFY creates future payouts.

The authenticated `PUT /api/v1/banking/bank-accounts/{id}/default` endpoint selects the runner's default. The account must belong to the caller and be active, verified, and recipient-backed. The account list reports `isDefaultPayout`. If the runner has not selected a default, DFY uses the oldest eligible account as a deterministic fallback.

Payout records store their bank-account ID when created. Changing the default does not change the destination of existing payouts. Removing an account deactivates it and clears its default flag; removal is blocked while a payout is in progress.

Existing Ozow-verified bank accounts do not automatically have a Paystack recipient reference. They must be re-verified through the DFY bank-account verification flow before a Paystack payout can be made.

An account marked verified without a Paystack recipient is shown as requiring verification in the runner's Payout Settings. After Paystack verification creates the recipient, payouts that failed specifically because the recipient was missing are automatically queued again with their original merchant reference. Do not manually edit payout status in the database.

Failed account verification remains visible in Payout Settings with Paystack's verification feedback, so the runner can correct the details or retry without refreshing. Accounts can be removed from the runner's active payout settings; removal deactivates the local account rather than deleting payout history or the Paystack recipient. Removal is blocked while a payout is in progress.

## Payment lifecycle

Creator:

`Create task -> Paystack checkout -> charge.success -> DFY independently verifies transaction -> task Posted / EscrowHeld`

Runner:

`Complete task -> creator confirms OR 24h expiry -> DFY creates Pending payout -> Paystack transfer -> transfer.success -> RunnerPaid`

## Test checkout

Use Paystack's documented test payment details in Test Mode. The successful reusable test card is:
- Number: 4084 0840 8408 4081
- Expiry: any future date
- CVV: 408

Paystack documents that test transfers return success because they are simulated, so staging can exercise the DFY payout lifecycle without moving real money.

The automated escrow/dispute workflow uses admin verification to simulate the creator-payment state. It is not evidence of Paystack collection or transfer completion. It only runs its payout workflow when the runner already has an active bank account verified through Paystack with a stored recipient reference; it skips rather than provisioning a fake recipient.

For the end-to-end provider test, first verify the staging runner's bank account in **Paystack Test Mode** and ensure the staging backend uses the matching test secret and webhook. Then run the manual checkout test with staging credentials:

```sh
PAYSTACK_MANUAL_CHECKOUT=1 \
PAYSTACK_TEST_MODE_CONFIRMED=1 \
DFY_CREATOR_EMAIL=... \
DFY_CREATOR_PASSWORD=... \
DFY_RUNNER_EMAIL=... \
DFY_RUNNER_PASSWORD=... \
npx playwright test e2e/paystack-manual-checkout.spec.ts --headed
```

The explicit Test Mode confirmation is required to avoid accidentally creating a real payment or transfer. The test pauses for the operator to complete Paystack Checkout, then verifies webhook activation, runner claim/completion, creator confirmation, and the final `RunnerPaid` / `Completed` payout state. Do not use production credentials or a live-mode Paystack account for this test.

The provider-test sequence is:

1. The test creates a task and opens its Paystack-hosted checkout.
2. Complete the test payment using Paystack's documented Test Mode details.
3. Paystack sends `charge.success` to `/api/v1/payment/webhook`.
4. DFY independently verifies the transaction and moves the task to `Posted / EscrowHeld`.
5. A runner with a verified Paystack recipient accepts and completes the task.
6. The creator confirms completion.
7. DFY creates the Paystack transfer.
8. Paystack confirms the test transfer and DFY moves the task to `RunnerPaid`.

Every Paystack payment attempt uses a unique transaction reference. Pending tasks generate a new reference whenever the creator starts checkout again; webhook reconciliation uses the payment-attempt record instead of relying only on the task's latest reference.

## Safety

DFY keeps the task, escrow, commission and payout state in its own database. Paystack is responsible for payment collection and bank transfers. The database must never be treated as a regulated escrow account.

Paystack transaction and transfer references are reused for reconciliation so an ambiguous network failure does not create a second payout.
