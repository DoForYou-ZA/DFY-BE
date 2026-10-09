# DoForYou API (DFY-BE)

The .NET Web API for the DoForYou South African task marketplace. Creators post tasks; runners accept and complete them. The backend is the authority for permissions, task/payment states, escrow, disputes, bank verification and payouts. The active payment provider is **Paystack**.

## Current release state

DFY is **not yet production-ready**. The 24-hour creator-confirmation lifecycle and core staging E2E coverage have passed, but a controlled Paystack payment/transfer test, native mobile build/device validation, and final refund/dispute/reconciliation checks remain release gates.

See [DFY Release Readiness](DFY_RELEASE_READINESS_2026-10-09.md) for the current verified status and remaining gates. Treat older Ozow-oriented notes as historical unless revalidated against current code.

## Core task and payment lifecycle

1. Creator creates a task; the API initializes Paystack checkout and persists the payment attempt.
2. Paystack webhook signature verification and server-side transaction verification establish payment success; browser return/dismissal alone never proves payment.
3. The backend holds the funds in escrow and makes the task available to runners.
4. A runner accepts and completes the task.
5. The task enters `AwaitingConfirmation`. The creator has **24 hours** to confirm completion or raise a dispute.
6. A dispute keeps escrow from being automatically released while it is reviewed.
7. If the creator does neither within 24 hours, the escrow service releases the escrow state and queues the runner payout.
8. A queued payout is **not** a successful payout. The runner is shown as paid only after Paystack confirms the transfer.

Runners are paid directly to a verified bank account; the retired wallet/withdrawal flow must not be reintroduced.

## Requirements

- .NET SDK 9 (the CI workflow uses .NET 9)
- PostgreSQL
- A Paystack test-mode secret key for controlled integration tests

## Local setup

1. Configure a PostgreSQL connection string through .NET configuration, for example `ConnectionStrings__DefaultConnection`.
2. Set a development JWT key if needed. Outside Development, `JWT_KEY` (or `Jwt:Key`) is mandatory and must be at least 32 characters.
3. Configure `PAYSTACK_SECRET_KEY` for payment-provider calls. `PAYSTACK_BASE_URL` is optional and defaults to `https://api.paystack.co`.
4. Configure `BACKEND_URL`, `FRONTEND_URL`, and `AllowedOrigin` for the target environment.
5. Run:
   ```bash
   dotnet restore DoForYou.API.csproj
   dotnet run --project DoForYou.API.csproj
   ```

Do not commit secrets or put real payment credentials in source control. Production database migrations should be applied through the release process; `AUTO_MIGRATE=true` is intended for controlled staging use and should not be enabled in production without an explicit migration plan.

## Important configuration

| Setting | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection |
| `JWT_KEY` | JWT signing key outside Development |
| `PAYSTACK_SECRET_KEY` | Paystack API and webhook signature verification |
| `PAYSTACK_BASE_URL` | Optional Paystack API base URL; defaults to `https://api.paystack.co` |
| `BACKEND_URL` | Public API base URL used in Paystack callback URLs |
| `FRONTEND_URL` | Frontend base URL |
| `AllowedOrigin` | Comma-separated allowed frontend origins |
| `AUTO_MIGRATE` | Opt-in migration/schema initialization; use only in controlled environments |
| `DFY_E2E_TEST_MODE` | Enables test-only CORS origins and prevents the payout worker from submitting/reconciling external transfers during E2E |

**Safety:** do not enable `DFY_E2E_TEST_MODE` in production. E2E tests should assert that escrow release queues a payout without sending money to a provider.

## Payment endpoints

- `POST /api/v1/tasks` — create a task and receive the Paystack checkout URL in the response.
- `POST /api/v1/payment/webhook` — Paystack webhook receiver; verifies `x-paystack-signature` before handling events.
- `GET /api/v1/payment/return` — payment return/callback route.
- `GET /api/v1/banking/banks` and `/api/v1/banking/accounts` — bank list and runner bank-account workflows.

Legacy wallet endpoints return `410 Gone`; do not use `POST /api/v1/payment/initiate` as a payment workflow. Payment creation belongs to the task-creation workflow.

## Validation

Run the backend build and automated tests:

```bash
dotnet restore DoForYou.API.csproj
dotnet build DoForYou.API.csproj --no-restore --configuration Release
dotnet test DoForYou.Tests/DoForYou.Tests.csproj --configuration Release --verbosity normal
```

Before production release, complete the controlled Paystack checkout and transfer journey, payout failure/retry/reversal and duplicate-event checks, dispute/refund reconciliation, and the native mobile device smoke test listed in the release-readiness document.
