# DFY Backend Work History

This document records backend work completed during the October 2026 task-workspace and Paystack payout work across DFY-BE and DFY-FE. It describes code pushed to `main`; a GitHub push does not by itself confirm that staging or production has deployed or that a migration has been applied.

## Task workspaces, lifecycle, and notifications

- Added server-side pagination for posted, active, and completed task workspaces, preserving task-specific deep links and returning paging metadata.
- Added status filtering and counts for posted and completed tasks, including payout-related states and awaiting creator confirmation.
- Added a prerequisite check when a runner claims a task: the runner must have an active, verified bank account with a non-empty Paystack recipient reference.
- Corrected task-related notification links so notifications can carry the related task association needed to navigate to the correct workflow.
- Added and adjusted scenario coverage for pagination, task status filtering, task acceptance, and escrow/payout lifecycle scenarios.

## Paystack staging and financial lifecycle

- Moved creator checkout and runner transfers to Paystack and documented the environment variables, webhook configuration, test-mode constraints, and transfer prerequisites in `PAYSTACK_SETUP.md`.
- Added/maintained payment-attempt and Paystack provider fields, schema-repair migrations, webhook/transfer lifecycle handling, and staging test support.
- Distinguished automated lifecycle simulations from a manual provider test: admin verification can simulate payment state but does not prove Paystack checkout or transfer completion.
- Documented a manual, explicitly gated Test Mode checkout scenario. Synthetic DFY staging login accounts were provisioned during setup, but they do not represent Paystack credentials or a verified recipient.
- Removed fake E2E bank-account provisioning from the payout harness. Test scenarios now require a genuine active, verified Paystack recipient and must not fabricate recipient details.
- Required Paystack bank verification and a recipient reference before treating a bank account as payout-ready. Verification results and provider feedback are persisted, and missing-recipient payout failures can be retried after successful recipient creation.
- Added safe account deactivation rather than deleting account history. Deactivation is rejected while the account is tied to an in-flight payout.
- Changed escrow payout selection to ignore active/verified-looking legacy records without a Paystack recipient and to choose only active, verified, recipient-backed accounts.

## Runner-selected default payout account

- Added `BankAccount.IsDefaultPayout` and migration `20261007040258_AddDefaultPayoutBankAccount`.
- Added migration `20261007032129_AddBankAccountVerificationMessage` to retain verification feedback for the runner settings flow.
- Added authenticated `PUT /api/v1/banking/bank-accounts/{id}/default`. It only accepts an account owned by the caller that is active, verified, and has a Paystack recipient reference.
- Bank-account listing returns `isDefaultPayout`. When no eligible account is explicitly marked as default, the API presents the oldest eligible account as the effective default.
- New payout creation prefers the selected eligible account and falls back deterministically to the oldest eligible account if no selection is available.
- Removing an account clears its default flag. Existing `Payout.BankAccountId` values are unchanged, so changing the preference does not reroute payouts already created.

## Key API and persistence surfaces

| Purpose | Endpoint or model |
|---|---|
| List active bank accounts and selected state | `GET /api/v1/banking/accounts` |
| Select default payout destination | `PUT /api/v1/banking/bank-accounts/{id}/default` |
| Verify/retry a bank account | `POST /api/v1/banking/bank-accounts/{id}/verify` |
| Remove an active bank account | `DELETE /api/v1/banking/bank-accounts/{id}` |
| Select a destination for a new payout | `EscrowService` |
| Preserve destination on a created payout | `Payout.BankAccountId` |

## Validation recorded

- Backend test suite passed with 26 tests after the preferred-account implementation.
- Frontend production build passed for the corresponding UI changes.
- EF migration SQL generation was checked for the default-account migration.
- Staging logs and earlier tests showed provider transfer verification/retry activity; that is not proof that the latest code or migration has deployed or that any transfer completed.

## Deployment and security notes

- Preferred payout-account support was pushed to BE `main` in commit `076d41c`; the migration must be applied by deployment before the API can use the new column.
- See `PAYSTACK_SETUP.md` for the provider environment, webhook, manual Test Mode run, and payout destination setup details.
- Do not update existing payout records to a different bank account as part of changing a runner's preference. Verify payout status through the provider and preserve payout audit history.
- Never store Paystack secrets, bearer tokens, staging passwords, or real bank details in source control or this document. Use environment secrets and redact logs before sharing.
- For a real provider test, use a staging backend configured with the matching Paystack Test Mode secret and webhook, and a runner account that Paystack has actually verified and assigned a recipient reference.
