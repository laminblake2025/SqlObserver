# Clean-room product boundary

## Purpose

SqlObserver is an independent implementation of generally understood SQL Server monitoring outcomes. Comparable outcomes do not justify copying another product's expression or internals. This boundary applies to employees, contributors, contractors, generated content, tests, examples, documentation, and assets.

## Acceptable inputs

Design may be informed by:

- the goals and requirements written for this repository;
- official, public Microsoft SQL Server, Windows, .NET, PostgreSQL, browser, protocol, and standards documentation;
- public descriptions of user problems and generic monitoring capabilities;
- independently run experiments against systems the contributor is authorized to use;
- permissively licensed libraries and assets used in accordance with their licenses;
- original interviews and feedback that do not disclose a third party's confidential implementation.

Record citations or provenance when an important design choice depends on a source. Public availability alone does not grant a license to copy expressive content.

## Forbidden inputs and copying

Do not copy, adapt, translate, trace, transcribe, extract, decompile, or closely imitate proprietary:

- source code, scripts, queries, migrations, schemas, stored procedures, binaries, or configuration;
- API routes, request/response contracts, tool names as a set, wire formats, or internal identifiers;
- database/table/column layout or undocumented behavior;
- UI page structure, dashboards, navigation, interaction sequences, screenshots, visual hierarchy, colors as a distinctive system, icons, illustrations, or other assets;
- product text, help content, alert wording, report wording, rule names, feature taxonomy, or branded terminology;
- test cases, fixture data, deployment layouts, or reverse-engineered performance thresholds;
- materials obtained under NDA, from leaked sources, private support exchanges, packet captures, trial-installation inspection, or unauthorized access.

Do not use another product as a pixel, behavior, schema, or API reference while implementing SqlObserver. Do not obscure copying through renaming, reformatting, machine translation, code generation, or an AI tool.

## Independent design practice

Start from a user problem and a supported platform contract. Define an original domain term, threat boundary, data model, interface, and test oracle. Prefer official engine semantics over inferred competitor behavior. Keep notes of relevant public/first-party sources and the reasoning that connects them to the design.

If a contributor has seen non-public competitor internals related to a proposed feature, disclose the conflict privately to the maintainer and do not contribute that portion until an independent design path is established. If provenance is uncertain, quarantine the material and redesign; do not attempt to make it look different.

Third-party dependencies need explicit provenance, compatible licenses, and review. Generated output is treated as submitted by the contributor: it must be checked for recognizable copied expression, secrets, and incompatible material.

## Product identity

`SqlObserver` is the working codename in this repository. Its vocabulary is defined in [terminology.md](terminology.md). Names describe this product's own concepts and do not assert compatibility with a competitor's private APIs or data model.

Public product descriptions may establish that users need outcomes such as historical telemetry, wait analysis, blocking and deadlock evidence, alerts, reports, or incident correlation. SqlObserver's implementation, schemas, user experience, alert logic, interfaces, prose, and artwork for those outcomes must remain original.

## Review gate

Every feature review asks:

1. What user outcome and supported platform facts justify the feature?
2. What sources informed it, and are they public/authorized and license-compatible?
3. Are the terminology, schema, API, UI, text, tests, and assets independently expressed?
4. Could a reviewer reasonably mistake any submitted artifact for a copied proprietary artifact?
5. Does the change introduce compatibility claims that were not independently tested?

A negative or uncertain answer blocks acceptance until the design is independently reworked. Suspected contamination should be reported privately, removed from active use, and replaced from clean inputs. Git history and released artifacts may require coordinated remediation.

This policy is recorded as an accepted architectural constraint in [ADR-0009](../adr/ADR-0009-clean-room-boundary.md).
