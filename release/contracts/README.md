# Release identity assessment

`release-identity-assessment.v1.schema.json` defines the bounded, decision-neutral
M12 release identity assessment. The read-only assessor reports the current Git
commit, the versioned certification policy identity, matrix/profile counts, and
an ordered catalog of exactly 29 checks. It is evidence about repository state,
not release evidence.

The assessment is intentionally closed and fail-closed: `status` is always
`not_ready`, and both `releaseEvidence` and `readyToRelease` are always `false`.
No product version, publisher, license, signing identity, key, host, environment
value, path, or raw command error is emitted. Owner and external certification
gates remain blocked until their decisions and evidence exist.

The policy version is the certification policy version from the matrix; it is not
a product version. The matrix inventory is 20 lanes and 35 cases, comprising 8
implemented lanes/cases and 12 lanes with 27 pending cases at this milestone.
The schema hash is pinned in `checksums.sha256`.
