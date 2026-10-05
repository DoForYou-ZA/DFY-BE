# Paystack production setup for DFY

DFY uses Paystack as its single production payment and payout provider.

## Render environment

Set these variables on the DFY backend service:

- `PAYSTACK_SECRET_KEY` — Paystack secret key for the active environment.
- `BACKEND_URL` — public DFY API origin, for example `https://api.doforyou.co.za`.
- `FRONTEND_URL` — public DFY frontend origin.

Do not commit Paystack keys to Git.

## Paystack Dashboard

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
5. The runner can only be paid when the account is active and verified.

Existing Ozow-verified bank accounts do not automatically have a Paystack recipient reference. They must be re-verified through the DFY bank-account verification flow before a Paystack payout can be made.

## Payment lifecycle

Creator:

`Create task -> Paystack checkout -> charge.success -> DFY independently verifies transaction -> task Posted / EscrowHeld`

Runner:

`Complete task -> creator confirms OR 24h expiry -> DFY creates Pending payout -> Paystack transfer -> transfer.success -> RunnerPaid`

## Safety

DFY keeps the task, escrow, commission and payout state in its own database. Paystack is responsible for payment collection and bank transfers. The database must never be treated as a regulated escrow account.

Paystack transaction and transfer references are reused for reconciliation so an ambiguous network failure does not create a second payout.
