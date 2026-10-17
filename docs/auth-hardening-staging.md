# DoForYou staging authentication and marketplace rollout

This document describes the current **staging PR branch**, not a statement that the feature is deployed or production-ready. Changes must be coordinated across DFY-BE, DFY-FE and dfy-mobile. Do not merge or deploy until the preflight items marked **BLOCKED** below are resolved.

## Recorded policy for this staging slice

- Account signup uses email/password or Google sign-in. The role is a preference (creator, runner, or both), not proof of eligibility.
- Signup requires names, email, password, phone contact, role preference and required Terms/Privacy acknowledgements. Phone is contact-only; it is not verified and no SMS OTP is sent. SA ID, date of birth and address are not signup requirements.
- Email verification is required for password-based signup before a non-admin user can sign in. Google sign-in is accepted only after server-side ID-token signature/issuer/expiry/audience validation and Google reports the email as verified.
- Creator capability is gated independently: verified email + creator/both. Profile completion, runner identity, authenticator 2FA and payout-recipient setup do not gate task posting.
- Runner capability is independent: verified email + runner/both + a backend identity status of passed + authenticator 2FA enabled. This is deliberately fail-closed. A South African ID format/checksum, legacy IsVerified, or Paystack bank-recipient verification is not identity verification.
- Payout setup is separate from accepting work. DFY's active task payout processor is Paystack; legacy wallet withdrawal endpoints return the documented retired response. The backend does not display a queued/processing payout as paid.
- A runner marks work complete; the creator has 24 hours to confirm or dispute. The escrow release service handles the no-action deadline; a qualifying dispute stops the normal release path. Do not modify these marketplace/financial rules as an authentication side effect.
- Google sign-in and authenticator/TOTP are included. Apple sign-in is deferred. Email-change, user-to-user report/block, self-service account deletion, formal identity-provider integration, and provider-backed appeals/manual review are not implemented in this staging slice.
- Browser access tokens remain in localStorage for this release. This is an explicit residual XSS exposure; migrating to HttpOnly cookies would require coordinated CSRF, CORS, session and SignalR changes.
- Required consent versions are terms-v1 and privacy-v1; the optional marketing choice is marketing-v1. The append-only consent event table records each new consent decision. **BLOCKED:** the published legal pages still need an owner to confirm that the actual text matches these version identifiers and approve the retention/access policy.

This is an engineering record, not legal advice. Obtain qualified advice on applicable South African privacy, identity, financial, consumer and payment-provider obligations.

## Required staging configuration

Set secrets only in Render's **staging API** service. Never commit keys or put them in frontend/mobile source:

- DFY_PUBLIC_URL=https://dfy-fe-staging.onrender.com
- SMTP_HOST, SMTP_PORT, SMTP_USERNAME, SMTP_PASSWORD, SMTP_FROM, SMTP_ENABLE_SSL
- GOOGLE_CLIENT_IDS: comma- or semicolon-separated IDs for the actual configured web, iOS and Android Google OAuth clients. Do not invent client IDs; each client ID must be created in the correct Google Cloud OAuth client type and approved origins/package/bundle identities must match the clients.
- DFY_TOTP_ENCRYPTION_KEY: base64 encoding of a random 32-byte (256-bit) key used to encrypt TOTP secrets. Store and back it up in the secret manager; do not rotate it without a tested re-encryption/recovery procedure for existing authenticator secrets.
- DFY_TRUST_MANAGED_INGRESS=true only if the service is reachable publicly through the managed Render ingress, which supplies trusted forwarded headers. The API uses one forwarded hop for client IP and scheme. Do not enable this behind a client-reachable or untrusted proxy.
- AUTO_MIGRATE=true only for a reviewed staging rollout. This applies pending EF migrations at startup. Recheck schema/history and disable it after the reviewed rollout.

SMTP sender authorization, link delivery, link host and spam placement must be tested with a dedicated staging inbox. Auth email send failures do not roll back signup; operators must observe logs/alerts without exposing tokens or email bodies.

## Authentication endpoints in the current branch

- POST /api/v1/auth/register: records required and optional consent; creates an unverified password account and sends a 24-hour email-verification link without issuing a session.
- POST /api/v1/auth/verify-email: one-time verification token, 24-hour expiry.
- POST /api/v1/auth/resend-verification: generic response; initial send plus resends are capped at five sends within a rolling 24-hour window and at least 60 seconds apart.
- POST /api/v1/auth/forgot-password and POST /api/v1/auth/reset-password: generic response, persisted send limits, 30-minute hashed single-use token and security-stamp rotation.
- POST /api/v1/auth/login: password check, verified-email gate, failed-login lockout and required TOTP/recovery-code check when enabled.
- POST /api/v1/auth/google and POST /api/v1/auth/google/register: server validates Google ID tokens against configured audiences. New Google accounts still require a selected role, contact phone and required legal consent.
- GET /api/v1/auth/two-factor/status, POST /api/v1/auth/two-factor/setup, confirm, cancel-setup, and disable: AES-GCM encrypted TOTP secret, 10-minute expiring setup, hashed one-time recovery codes, atomic time-step replay protection and session-stamp rotation. Enrollment returns HTTP 503 until the TOTP key is configured.
- POST /api/v1/auth/change-password: current password required and existing sessions revoked.
- POST /api/v1/auth/logout: rotates the shared security stamp and therefore revokes all sessions, not just the current device. Clients must clear local credentials even if this best-effort request fails.
- PUT /api/v1/user/marketing-consent: changes optional marketing preference and appends an auditable consent event.

Verification/reset tokens are hashed at rest and consumed with conditional database updates. Email verification is not proof of phone ownership or identity. The per-IP limiter currently uses an in-process fixed window; the account lockout/send-window controls are persisted. **Residual risk:** IP rate-limit counters are not distributed across scaled-out API instances.

## Database and sensitive-data safeguards

- Normalized email has a database unique index. The Google/auth migration backfills normalized values and deliberately aborts if duplicate normalized emails exist. Do not automatically merge or delete duplicate users; resolve them with a reviewed staging-only procedure.
- Profile endpoints expose only whether an ID is on file and its last four digits; general profile APIs reject new ID/DOB updates. Existing ID values may still be plaintext in the staging database. Field-level encryption/HMAC-based uniqueness and a managed-key data migration have **not** been implemented. Do not claim ID-at-rest encryption.
- Consent-history migration backfills only evidence that was already recorded in user columns; it does not infer missing historical acceptance.
- Task/business, bank-account, payment, payout and dispute state remain backend-authoritative. Payment-return URLs are navigation only; provider-confirmed callbacks/reconciliation determine payment status.
- Existing admin payment/dispute controls and legacy account archive controls still require a separate permission/audit review. No new identity-review or independent financial-override approval workflow is provided by this change.

## Preflight checklist before any staging deploy

1. Inspect staging data without modifying it: count normalized email duplicates, confirm migration history versus actual schema, determine whether the deployment database contains production personal data, and verify the migration's abort behavior in a safe copy. **BLOCKED until verified.**
2. Confirm terms-v1, privacy-v1, and marketing-v1 correspond to approved text. Confirm consent retention/access rules.
3. Configure SMTP, Google client IDs and the TOTP encryption key in the Render staging API secret configuration. Never paste secret values into GitHub, this PR, build logs or client bundles.
4. Set proxy-aware forwarding only after verifying the service is behind the managed Render ingress. Test that spoofed X-Forwarded-For values do not control the IP limiter; test the actual forwarded client IP.
5. Deploy the API/migrations first in staging, then smoke test /health, schema/migration state, email links, Google login and TOTP setup. Verify TOTP time-step replay is rejected. If a required migration fails, roll back the deploy and do not start the clients.
6. Register with a dedicated test inbox. Exercise existing/unknown-account neutral responses, resend cooldown/quota, used/expired/tampered tokens, multibyte password byte boundaries, lockouts and revocation.
7. Exercise Creator, Runner and Both direct-API capability tests. Confirm Both can post while runner identity is incomplete, but cannot accept paid work. **BLOCKED:** no identity provider has been selected or configured; consequently the legitimate user journey to reach IdentityVerificationStatus=passed is not available yet, and Runner acceptance must remain disabled.
8. Verify task posting, payment attempt/idempotency, claim concurrency, completion/24-hour timeout, dispute freeze, refund events and Paystack payout outcomes in test mode only. Never trigger a real charge or payout as an auth smoke test.
9. Run backend tests, website build/E2E, and mobile typecheck/bundle/Android release build from the final aligned PR heads. Record the SHA of each build.
10. Run both clients against the same deployed staging API and database, capture request/visible/persisted state and cross-client refresh for each case, and only then consider marking the PRs ready.

## Remaining release blockers

- **Identity/KYC policy and provider:** no provider or qualifying statuses have been approved/configured. Needs a product decision, provider sandbox credentials, retention/evidence design, appeal/manual-review path and qualified compliance review. Do not fake a passed identity result for acceptance testing in a shared staging database.
- **Payout recent re-authentication:** the PR now implements a five-minute, one-operation step-up token for bank-account add/verify/default/remove, validated by the backend and surfaced in both clients. Hosted negative tests (missing, expired, reused token; Google/password + TOTP reauth) remain release evidence to capture.
- **At-rest protection for legacy identity data:** current schema can still contain plaintext ID values; provider-backed data collection, encryption/HMAC key management, migration, retention/deletion and access controls are outstanding.
- **Cross-instance rate limiting and incident runbooks:** per-IP limiter counters are in-process; monitoring/alerts and distributed coordination remain necessary before scaling out.
- **Lifecycle scope:** report/block, email change, account deletion/export/correction, suspended-account handling for active tasks/funds, and support/admin permission review are not implemented by these auth PRs.
- **Staging evidence:** real SMTP delivery, Google OAuth audience/origin/package configuration, TOTP encryption key operation, deployed schema, proxy IP validation and cross-client E2E must be proven on the hosted staging services, not inferred from CI.

## Do not announce completion based only on green CI

CI proves that the code built and the automated tests included in those jobs passed. It does not prove that Render secrets exist, migrations will apply to the actual staging data, Google OAuth clients are configured, SMTP delivers, an identity provider is integrated, or real Paystack callbacks and payouts reconcile.
