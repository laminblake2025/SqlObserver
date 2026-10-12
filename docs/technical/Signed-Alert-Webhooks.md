# Signed alert webhooks

HTTPS alert destinations can opt into HMAC-SHA256 authentication. Signing adds
headers to the existing bounded JSON outbox delivery. Authorization, destination
approval, address validation, TLS and retries remain in the normal delivery
path. This is not a Teams, PagerDuty, SMTP or SNMP adapter.

## Provisioning and configuration

Establish a separate, random 32-byte shared key with the receiver through the
deployment owner's approved secret channel. Do not put the key in application
settings, command-line arguments, URLs, source or logs. On each Windows host
that needs the key, protect it using the interactive helper:

```powershell
pwsh ./tools/lab/provision-webhook-signing-key.ps1 `
  -Path 'C:\ProgramData\SqlObserver\webhook-primary-2026\key.dpapi' `
  -ServiceIdentities 'DOMAIN\SqlObserverServer$', 'DOMAIN\SqlObserverCollector$'
```

The parent directory must exist; the dedicated key directory must be new.
Enter the shared key as Base64 at the hidden prompt. The helper uses machine
DPAPI and disables inherited access on the file and directory. Access is limited
to the named service identities, provisioning owner, SYSTEM and Administrators.
It refuses existing directories and reparse-point ancestors, and never prints
the key. Separate Server/Collector hosts require separately protected files with
the same shared key. Owner-approved identities/provisioning remain deployment
prerequisites under ADR-0017.

Configure the same reference on Server and Collector, using the existing layout:

```json
{
  "AlertDestinations": {
    "primary": "https://alerts.example.com/sqlobserver",
    "primary:Kind": "https-webhook",
    "primary:Revision": "7",
    "primary:Allowlist": "<receiver IP addresses or CIDRs>",
    "primary:Signing:KeyId": "primary-2026",
    "primary:Signing:ProtectedKeyPath": "C:\\ProgramData\\SqlObserver\\webhook-primary-2026\\key.dpapi"
  }
}
```

Every resolved address must pass the configured allowlist and normal network
policy, which rejects private/loopback addresses. The HTTPS URI cannot contain
credentials, a query or fragment. `KeyId` is 1–64 ASCII letters, digits, dots,
underscores or hyphens. Both signing settings must be supplied together. The
loader rejects missing/unavailable files, plaintext keys, keys of other lengths,
reparse points and broad directory/file permissions.

Approve the reference through the existing destination administration screen/API.
Approval and delivery compute the same configuration digest, binding the signing
protocol, key ID and SHA-256 fingerprint of the random key. No key bytes or key
file path enter the approval response, repository or outbox. Each send loads one
snapshot and erases its key on success, failure or cancellation. Signed work must
carry its approved revision and digest. Destinations with neither signing setting
retain their existing unsigned configuration digest and behavior.

## Receiver contract

Every signed POST carries these single-valued headers:

| Header | Value |
| --- | --- |
| `X-SqlObserver-Key-Id` | Configured key ID |
| `X-SqlObserver-Timestamp` | Decimal Unix seconds at send time |
| `X-SqlObserver-Delivery-Id` | Stable outbox UUID, lowercase `D` form |
| `X-SqlObserver-Signature` | `v1=` plus 64 lowercase hexadecimal characters |

Compute HMAC-SHA256 over this UTF-8 prefix, followed immediately by the exact
received body bytes. Each prefix line, including the UUID, ends with one LF.
There is no separator after the body.

```text
sqlobserver.webhook.v1
POST
<configured absolute destination URI>
<key ID>
<Unix timestamp>
<delivery UUID>
<exact body bytes>
```

The URI is .NET's canonical `Uri.AbsoluteUri`, including its encoded path and
non-default port. Configure that exact public URI at the receiver rather than
deriving it from untrusted Host/forwarding headers. Proxies must preserve the
body and headers. Do not parse/reformat JSON before verification. The sender
snapshots the payload before signing/sending; the body remains capped at 16 KiB.

Reject duplicate/missing headers, unknown key IDs, malformed values, oversized
bodies, timestamps more than five minutes from the receiver's clock, and invalid
signatures. Compare HMACs in constant time before acting on the alert. Store the
delivery UUID durably to avoid duplicate effects, and return success for an
already accepted delivery. Retain deduplication state for at least the sender's
maximum retry lifetime. Keep both systems' clocks synchronized.

Retries retain the UUID/body but sign with a fresh timestamp. Cancellation or a
lost response can happen after acceptance; signing does not provide exactly-once
delivery.

## Rotation

Provision a new key in a new directory, use a new key ID, increment the revision
on both hosts and approve the updated reference. Retain the receiver's former key
as required by its traffic/retry policy. The sender does not retain historical
configurations: queued deliveries bound to the former key fail validation before
sending. Coordinate queue handling with the deployment owner. Changing the key
under the same ID, removing signing or configuring it partially cannot silently
turn an approved signed delivery into an unsigned send.

## Verification evidence

Focused checks reconstruct the receiver's HMAC independently from request bytes
and cover tampering, Unicode, fresh retries/stable UUIDs, payload mutation, key
rotation/removal, malformed configuration, cancellation and key-buffer erasure.
Approval checks verify Server/Collector digest agreement. Native Windows checks
cover DPAPI, file/directory ACLs, plaintext/wrong-sized key rejection and helper
replacement refusal. Existing HTTPS checks cover payload bounds, DNS/address
pinning, response statuses and cancellation.

Local evidence: `artifacts/webhook-signing/webhook-signing-final.trx` (30 checks),
`webhook-approval.trx` (8 checks), `webhook-production-di.trx` (1 composition check),
and the native provisioning probe. No external
receiver was contacted. Production certificates, gMSA identities, throughput and
third-party integrations remain unqualified by these checks.
