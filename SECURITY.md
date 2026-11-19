# Security policy

Reglet reads existing DOCX/ODT/PDF files (subset) and creates new documents from an
own model. The relevant attack surfaces are ZIP/XML parsing (DOCX/ODT), PDF parsing and
font handling.

## Supported versions

Security updates are provided for the current minor line of the respectively supported
major version (SemVer, see [docs/versioning.md](docs/versioning.md)).

| Version | Supported |
| --- | --- |
| 1.9.x (current) | yes |
| < 1.9 | no |

## Reporting

Please **do not** report via public issues. Use

- **GitHub Private Vulnerability Reporting** (repository → *Security* → *Report a
  vulnerability*) or
- a confidential email to the maintainer (contact in the repository profile).

Please include: affected version, format (DOCX/ODT/PDF), write or read path, reproduction
and possible impact.

## Process

- Acknowledgement within **5 business days**.
- We assess the report, coordinate disclosure and publish a corrected version (SemVer PATCH
  or MINOR depending on impact).
- Please make the bug public only after coordinated disclosure.

## Hardening

Resource limits (`ResourceLimits`) and the reader hardening protect against oversized, deeply
nested or hostile input; details are in [docs/hardening.md](docs/hardening.md). The shipped
projects are free of reflection and native libraries (see
[docs/aot-trimming.md](docs/aot-trimming.md)).
