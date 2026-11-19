# ADR-0006: Protection and signatures (phase AD)

- **Status:** Accepted (revised 2026-11-05: OF-E01, OF-E02 and OF-E03 implemented)
- **Date:** 2026-10-01
- **Context:** Phase AD

## Context

Phase AD evaluates whether Reglet can offer protection and signatures without external
packages:

- **DOCX encryption:** "agile encryption" (ECMA-376 Part 4) wraps the package in an
  encrypted OLE/CFB container using AES, SHA-512 and XML descriptors. Legacy encryption uses
  RC4/AES in a binary structure.
- **PDF encryption:** the standard security handler (ISO 32000-1, 7.6) computes an encryption
  key from user/owner passwords (RC4 or AES-CBC) and encrypts strings and streams; PDF 2.0
  adds AES-256.
- **PDF signatures (PAdES):** an ASN.1/CMS (PKCS#7) detached signature over a `/ByteRange`
  with `/Contents`, plus a signature dictionary and optional DSS.
- **DOCX write protection:** `w:documentProtection` in `settings.xml` restricts editing
  without encryption (with or without a password hash).

## Decision

1. **AD03 (DOCX write protection) is implemented.** `DocxWriteOptions.Protection`
   (`DocxProtection`) writes `w:documentProtection w:edit="…" w:enforcement="1"`. Without
   `ProtectionPassword` there is no password hash, so the user can remove the protection; with it
   the protection is secured by a salted SHA-512 hash (ISO/IEC 29500-1, 17.15.1.29). Either way it
   is protection without encryption.
2. **AD01 (evaluation) is documented here.** The BCL offers the primitives
   (`System.Security.Cryptography.Aes`, `Rfc2898DeriveBytes`, `SHA256/512`, `RandomNumberGenerator`)
   for both the PDF standard security handler and DOCX agile encryption.
3. **AD02 (PDF AES encryption) is implemented.** The standard security handler is implemented
   with the BCL (`System.Security.Cryptography` for MD5/AES, a small local RC4 for the older
   revisions):
   - writing: revision 6 with AES-256-CBC (`/V 5 /R 6 /Length 256`, `/CF << /StdCF <<
     /CFM /AESV3 /Length 32 >> >>`, `/StmF`/`/StrF /StdCF`, catalog extension `ADBE` level 8).
     The 256-bit file key is random; `/U`/`/UE` and `/O`/`/OE` come from the hardened SHA-2
     hash of ISO 32000-2 (Algorithm 2.B, passwords as UTF-8 after NFKC normalization), `/Perms`
     holds the encrypted permissions, and AES-256-CBC prepends a random IV to every string and
     stream.
   - *Revised 2026-09-25:* the first version wrote revision 4 (AES-128 with the MD5-based key
     derivation of Algorithm 2, 50 rounds). ISO 32000-2 deprecates it because weak passwords
     can be tested very quickly; revision 6 is supported by all current viewers.
   - `PdfWriteOptions.Encryption` (`PdfEncryptionOptions`: user/owner password and
     `PdfPermissions`) turns it on; `PdfTokenWriter`/`PdfFileWriter` encrypt at the
     serialisation boundary. The `/Encrypt` dictionary and the trailer `/ID` stay unencrypted.
   - reading: `PdfFile` decrypts strings and streams (RC4 for R2/R3, AES-128 for R4 and AES-256 for
     the standard handler revision 5/6, `/CFM /AESV3`).
     `PdfDocumentReader.Read`/`ReadPageTexts` accept `PdfReadOptions.Password`; the owner
     password recovers the user password from `/O`.
   Encryption and PDF/A are mutually exclusive (PDF/A requires unencrypted files).
4. **OF-E01 (DOCX agile encryption) is implemented.** The package is wrapped in a compound file
   (MS-CFB, written and read by `CompoundFile`: header, FAT, mini stream, DIFAT) with the
   `EncryptionInfo` and `EncryptedPackage` streams. The descriptor follows ECMA-376 Part 4 /
   MS-OFFCRYPTO 2.3.4.10 with the values Word writes: AES-256-CBC, SHA-512, 100 000 iterations,
   a verifier (encrypted hash input/value), an encrypted intermediate key, 4 KB segments with an
   IV per segment (`SHA-512(keyDataSalt + segment)`) and HMAC-SHA-512 integrity over the
   encrypted package. `DocxWriteOptions.Encryption` (`DocxEncryptionOptions`: password and spin
   count) turns it on, `DocxReadOptions.Password` opens it (`DocumentReader.Read` has the
   overload with a password). A wrong password and a modified payload raise
   `DocumentValidationException`. Only the AES-256/SHA-512 variant is accepted; a document with
   another agile algorithm, with a certificate key encryptor or with legacy standard/extensible
   encryption is rejected with a message. Verified against an independent implementation
   (`msoffcrypto-tool` decrypts our output; the check is optional via
   `REGLET_MSOFFCRYPTO`).
5. **OF-E02 (DOCX digital signature) is implemented.** `DocxWriteOptions.Signature`
   (`DocxSignatureOptions`) adds the OPC signature parts to the finished package: the origin part
   `_xmlsignatures/origin.sigs`, the signature part `_xmlsignatures/sig1.xml`, the relationship of
   the package to the origin and of the origin to the signature, and the two content-type
   overrides. The signature part carries an XML signature (XMLDSig): `SignedInfo` with the inclusive
   Canonical XML 1.0 method, RSA/SHA-256, one `Reference` per signed part (raw part bytes, therefore
   no transform) and one reference to the Office `Object` that holds the signature time; `KeyInfo`
   carries the certificate and the optional chain. The `SignedInfo` and the `Object` are written in
   the canonical form that was signed and digested, so a verifier that canonicalizes them again
   arrives at the same bytes (`XmlCanonicalizer`, a C14N 1.0 implementation for the subset that
   occurs here). `DocxSignatureReader.Read` verifies the signature value and every digest and
   reports each problem.
   - *Revised 2026-11-05 (additional signature and chain check):* signing an already signed package
     adds `sig2.xml` and keeps the first signature valid, because a signature no longer covers the
     package plumbing (`[Content_Types].xml`, `_rels/.rels`) that a later signature has to change.
     `DocxSignatureReader.Read(stream, DocxSignatureReadOptions)` builds the certificate chain with
     `X509Chain` against the caller's trusted roots (custom trust store, revocation off unless the
     caller enables it) and reports `IsTrusted` and the chain problems next to the cryptographic
     result.
   - *Deliberately outside:* Word's visible signature line (`w:sig`, `w:sdt`) is not written. A
     signed DOCX can also be encrypted: the signature is added to the finished package before the
     container is written, and the test `CombinesWithEncryption` covers the round trip. Only
     RSA/SHA-256 with inclusive C14N is supported (no ECDSA, no SHA-1, no exclusive C14N). The
     canonical form and the signature were checked
   against libxml2's C14N and the `cryptography` package (optional check
   `REGLET_XMLDSIG`).
   - *Revised 2026-11-13 (PDF verification):* `PdfSignatureReader` (`PdfDerReader`, `PdfCmsVerifier`)
     verifies PDF signatures without a CMS library: byte range and its gap, the message digest,
     the signature value (RSA PKCS#1, RSASSA-PSS with a salt as long as the digest, ECDSA; SHA-256,
     SHA-384, SHA-512, no SHA-1), the RFC 3161 token of a B-T signature and a document timestamp,
     and the chain with the caller's roots at the verified token time (a signing time that the
     signer claims by itself never decides). Only the first signer of a CMS is checked. Revocation
     and DSS evaluation stay with the caller.
6. **AD04 (PDF signatures) is implemented (phase BJ02, extended by OF-E03).** The BCL has no ASN.1/CMS
   writer, so the DER encoding and the CMS structure are written by hand
   (`PdfDer`, `PdfCms`):
   - `PdfWriteOptions.Signature` (`PdfSignatureOptions`) signs the document with the private key
     of a certificate (RSA). The signature is a detached CMS SignedData (RFC 5652, SHA-256,
     PKCS#1 v1.5) in the signature dictionary of the PDF (`/Type /Sig`,
     `/SubFilter /adbe.pkcs7.detached`); the document refers to it through a signature field
     in the AcroForm.
   - The signed data is the file without the signature value; the `/ByteRange` names the two
     ranges. The placeholders have a fixed width, so the values can be written into the
     finished file without changing its length. A signed document is therefore built in memory
     and cannot be encrypted.
   - *Revised 2026-11-05 (OF-E03):* the signature can be extended to the PAdES baselines B-T and
     B-LT, and a document timestamp can be appended:
     - **B-T:** `PdfSignatureOptions.Timestamp` (`PdfTimestampOptions`) sends an RFC 3161
       `TimeStampReq` over the signature value (a responder delegate or a TSA URL), reads the
       `TimeStampResp` (`PdfTimestamp`) and embeds the token as the unsigned attribute
       `id-aa-signatureTimeStampToken`.
     - **B-LT:** `PdfSignatureOptions.LongTermValidation` (`PdfValidationData`: certificates, OCSP
       responses, CRLs) writes a document security store with a VRI entry keyed by the SHA-1 of the
       signature. The store belongs after the signed revision, so it is appended as an
       **incremental update** (`PdfIncrementalUpdate`: new objects, a cross-reference section for
       those objects, a trailer with `/Prev`). The bytes of the signed revision stay unchanged,
       which is what a validator checks; when the store is new, the update also adds `/DSS` to the
       catalog.
     - **Document timestamp:** `PdfDocumentTimestamp.Add` appends a signature dictionary with
       `/Type /DocTimeStamp` and `/SubFilter /ETSI.RFC3161` whose `/Contents` is the RFC 3161 token
       over the byte ranges of the finished file, together with its hidden field, a revised page
       that carries the annotation and a revised AcroForm.
     - An encrypted PDF cannot be updated incrementally (the appended objects would need the file
       key), so both operations refuse it.
     - Verified with pyHanko (optional gate `REGLET_PYHANKO`, which also runs a local TSA):
       B-T is `INTACT:TRUSTED,TIMESTAMP_TOKEN<INTACT:TRUSTED>,UNTOUCHED`, B-LT reports
       `EXTENDED_WITH_LTA_UPDATES,ACCEPTABLE_MODIFICATIONS`, the document timestamp validates as
       `INTACT:TRUSTED`.
   - *Deliberately outside:* the signature covers the file, not a semantic subset; the caller
     supplies the TSA and the validation material, and decides about trust and revocation.
   - *Revised 2026-11-04:* the PAdES baseline B-B (ETSI EN 319 142-1) is available:
     `PdfSignatureOptions.Pades` adds the signed attribute "signing certificate v2"
     (RFC 5035) with the hash of the certificate, sets the subfilter to
     `/ETSI.CAdES.detached` and writes a document security store (DSS) with the certificate
     of the signer into the catalog. Without a time stamp authority the signature stays a
     B-B signature.
   - *Revised 2026-11-05 (OF-E02):* a DOCX signature is implemented as an OPC signature part with
     an XML signature over the parts (see item 5).

## Consequences

- `DocxProtection`, `DocxWriteOptions.Protection`, `PdfEncryptionOptions`, `PdfPermissions`,
  `PdfWriteOptions.Encryption`, `PdfReadOptions` and the PDF reader overloads are public; they
  add no dependencies.
- Encrypted PDFs are not deterministic (random document ID and AES IVs); this is inherent to
  the format.
- PDF signatures follow the PAdES baselines B-B, B-T and B-LT and support a document timestamp;
  the TSA and the validation material come from the caller, and trust and revocation are the
  caller's decision. DOCX signatures are written as an OPC/XMLDSig signature part.
- An incremental update appends a revision without touching the signed bytes; an encrypted PDF
  cannot be updated that way.
- A signed PDF is built in memory (the signature covers the written bytes) and cannot be
  combined with encryption.
