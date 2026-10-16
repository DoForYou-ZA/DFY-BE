# Staging authentication hardening

This change targets the `staging` branch only. Do not merge or deploy until the backend and both clients pass CI and the staging environment is configured.

## Required staging environment

Configure these in the Render **staging API** service environment; never commit their values:

- `DFY_PUBLIC_URL=https://dfy-fe-staging.onrender.com`
- `SMTP_HOST`
- `SMTP_PORT`
- `SMTP_USERNAME`
- `SMTP_PASSWORD`
- `SMTP_FROM`
- `SMTP_ENABLE_SSL=true` unless the provider explicitly requires another setting
- `AUTO_MIGRATE=true` only for the reviewed staging rollout that applies pending EF migrations; reassess after migration succeeds

The SMTP account must be authorized to send from `SMTP_FROM`. Verify delivery, spam placement, and the link host before inviting real users. Missing SMTP settings are intentionally not replaced with a production URL; delivery errors are logged without message bodies or tokens.

## Public auth endpoints

- `POST /api/v1/auth/register`: stores the account with email unverified, hashes the verification token at rest, and sends a 24-hour verification link. It does not issue a JWT.
- `POST /api/v1/auth/verify-email`: accepts a one-time token and marks the mailbox verified.
- `POST /api/v1/auth/resend-verification`: generic response to reduce account enumeration.
- `POST /api/v1/auth/forgot-password`: generic response; reset token is hashed at rest and expires after 30 minutes.
- `POST /api/v1/auth/reset-password`: consumes the token once, changes the password, verifies control of the email mailbox, and rotates the security stamp.
- `POST /api/v1/auth/login`: denies non-admin users whose email is not verified.
- `POST /api/v1/auth/change-password`: requires the current password and rotates the security stamp.

A successful password change or reset invalidates existing JWTs. Users must sign in again. Email verification is not proof of phone ownership or identity; phone OTP and KYC are separate follow-up work.

## Staging rollout checklist

1. Review legal content at `/terms` and `/privacy`; confirm that `terms-v1` and `privacy-v1` identify the exact published versions before enabling registration.
2. Audit duplicate emails and ID numbers in staging before adding database-level uniqueness constraints. Do not run an unreviewed cleanup against production.
3. Configure the environment above, then deploy the backend staging branch and confirm the recovery migration applies.
4. Register a dedicated test account with an email inbox you control; verify the link, sign in, and confirm an unverified account cannot sign in or call protected endpoints.
5. Test resend with an unknown email, expired/used verification tokens, forgot-password for both existing and unknown addresses, expired/used reset tokens, password policy limits, and rate limiting.
6. Change/reset a password and confirm old tokens fail; verify the user can sign in with the new password.
7. Verify profile responses contain only `hasIdNumber` and `idNumberLastFour`, not the full ID. Check browser localStorage and mobile AsyncStorage after profile refresh.
8. Run website and mobile CI and perform creator/runner smoke tests. Do not create paid tasks or trigger real payouts during auth tests.

## Known follow-up security work

- The current database still stores existing ID numbers as plaintext. Profile API responses are minimized, but field-level encryption/HMAC-based uniqueness needs a reviewed data migration and a managed key before claiming at-rest encryption.
- Case-insensitive email and ID uniqueness are currently checked in application code; database unique constraints must follow a staging duplicate audit.
- SMS OTP, phone verification enforcement, identity/KYC verification, a revocable refresh-token family, and distributed rate limiting are not implemented by this change.
- SMTP delivery is not operational until the staging service is configured with a provider's credentials.
