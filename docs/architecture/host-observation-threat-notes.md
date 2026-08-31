# Host observation threat notes

This note scopes M10 host metrics. It supplements the repository-wide threat model and is not a
deployment permission grant.

| Threat | Boundary / mitigation |
| --- | --- |
| WMI or performance providers return machine names, paths, labels, or messages | Fixed query kinds, bounded rows/bytes, and domain-separated opaque fingerprints; provider text is discarded. |
| A target reads another target's host binding | Binding and profile are keyed by the exact monitored-instance id and target revision; mismatches fail closed. |
| A provider hangs during collection | Five-second command deadline, cancellation acknowledgement grace, and `TerminationUnproven` with no output when termination cannot be proven. |
| Provider output is malformed or oversized | Numeric/range validation, unique volume fingerprints, 256-row and 256 KiB limits, and no partial unsafe payload. |
| Host identity changes while a target is observed | The returned fingerprint must equal the revisioned binding; identity mismatch yields no metrics. |
| Collection leaks network or credentials | Host collection is local Windows observation under the service identity; no caller-supplied host/path/query/credential string is accepted by the host port. |

Review gates include hostile provider strings, two-target binding isolation, cancellation with a
reader that honors and one that ignores cancellation, and serialized-output scans for raw identity
text.
