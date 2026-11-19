# ADR-0020: ODT package signatures (OF-E05)

- **Status:** Implemented
- **Date:** 2026-11-09
- **Context:** Follow-up scope, closes the ODT half of the signature gap

## Context

DOCX (OF-E02) and PDF (BJ02) could be signed, but an ODT package had no signature. The ODF 1.3
package specification, section 5, defines XML signatures stored in
`META-INF/documentsignatures.xml`; every file of the package is referenced, and XML parts may be
canonicalized before they are digested.

## Decision

### 1. Public options

- `OdtSignatureOptions(certificate)` with optional `AdditionalCertificates` is added to
  `OdtWriteOptions.Signature`.
- `OdtSignatureReader.Read(stream|byte[])` returns `OdtSignatureInfo` per signature: certificate,
  covered parts, validity and concrete problems.
- The writer requires an RSA private key; the reader verifies the RSA/SHA-256 signature. Certificate
  chain and trust are the caller's responsibility, as with the DOCX reader before its trust
  options were added.

### 2. Format

- The signature file is `META-INF/documentsignatures.xml` with a
  `dsig:document-signatures` root (`dsig:version="1.2"`) and one `ds:Signature` per signature.
- Every file of the package except the signature file is referenced by a `ds:Reference` with its
  package path as `URI`. Plain XML parts use a C14N transform and a SHA-256 digest of the
  canonicalized root; binary parts and encrypted parts are digested as raw octets.
- `ds:SignedInfo` uses C14N 1.0 and RSA/SHA-256; `ds:KeyInfo` carries the X.509 certificate(s).
- The signature is added **after** encryption. Encrypted parts are therefore signed in their
  encrypted form and the signature file stays unencrypted, as ODF 1.3 requires.
- A second signature is appended to the existing signature file; the signature file itself is not
  referenced, so earlier signatures stay valid.
- The XML canonicalizer moved from the DOCX project to Core so both writers use the same
  implementation.

### 3. Limits

- No XAdES `SignedProperties`; the signature is a basic XMLDSig. The ODF specification permits
  XAdES but does not require it.
- No certificate chain or revocation evaluation in the reader.
- LibreOffice signature verification is a manual check (its command line has no signature or
  password option); the optional office smoke only proves that a signed ODT opens without a repair
  prompt.

## Consequences

- `OdtWriteOptions.Signature` produces a signed ODT; `OdtSignatureReader` verifies the
  cryptographic signature and every digest.
- A changed part invalidates the signature and the reader names the part.
- A signed encrypted ODT opens with the password and the signature covers the ciphertext.
- The signature file does not change the plain ODT reader or source preservation.

## Alternatives rejected

- **XAdES with `SignedProperties`.** More metadata, but not required for a valid ODF signature and
  a much larger implementation for no version-1 benefit.
- **Sign the whole ZIP as one octet stream.** ODF signatures reference individual package files;
  a single stream would not be understood by ODF consumers.
- **Reuse the DOCX OPC signature part.** The part layout, reference names and signature-file
  namespace differ; a separate ODF writer is simpler than translating between the two.
