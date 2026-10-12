# Dashboard redesign verification

The retained October 9, 2026 verification describes the local Fleet, Summary,
and Activity redesign. The screenshots contain illustrative or disposable-lab
evidence and are not production acceptance records.

- [Activity verification receipt](../../.artifacts/activity-redesign-20261009/verification-receipt.json)
  records 1,089 frontend model/contract cases and 83 component, browser,
  accessibility, retained-native-capture, and connected-lab checks. Its
  [input inventory](../../.artifacts/activity-redesign-20261009/final-inputs.json)
  binds 841 frontend source and test files. Those hashes were checked again
  before publishing this branch.
- [Shared redesign verification](../../.artifacts/ui-redesign-20261009/verification.json)
  records the Fleet, Summary, and blocking layouts and their scoped checks.
  Its native core journey used a direct passive collector test seam; production
  collector admission and least-privilege operation remain unqualified.
- [Activity visual comparison](../../.artifacts/activity-redesign-20261009/visual-comparison.json)
  includes 1600×1000, 1440×900, and 390×844 layouts, along with the generated
  reference images. Captured counts and signals come from evidence rather than
  the illustrative reference data.
- The Activity native blocking workload was bounded to 150 seconds. Its
  [workload receipt](../../.artifacts/activity-redesign-20261009/native/workload-result.json)
  confirms rollback and no remaining owned sessions. The final UI replay used
  that same retained capture; no protected SQL text is stored in the receipt.

Initial Fleet JavaScript was 154.04 KiB gzip against the 250 KiB ceiling.
The build and asset checks continue to enforce that budget.

The [local lab connection guide](../development-lab-dashboard.md) describes
the deployed API/collector compatibility limits. These receipts do not qualify
a full deployment or migration of the newer backend. Release certification
lanes retain their existing pending status.

Temporary deployment packages, process state, intermediate builds, and local
backups are excluded from version control. The dated screenshots, bounded
qualification harnesses, and verification receipts are retained explicitly.
