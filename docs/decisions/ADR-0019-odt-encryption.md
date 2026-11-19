# ADR-0019: ODT package encryption (OF-E05)

- **Status:** Implemented
- **Date:** 2026-11-09
- **Context:** Follow-up scope, closes the ODT half of the protection gap

## Context

DOCX (OF-E01) and PDF (AD02) could already be encrypted, but an ODT package was always plain:
`OdtWriteOptions` had no encryption and `OdtReadOptions` did not exist. A document that had to be
protected could therefore not be written as ODT.

ODF 1.3 part 2, section 3.4 defines package encryption: each file entry is compressed, encrypted
and stored, and the manifest carries the parameters. The legacy algorithm is Blowfish-CFB; modern
producers use AES-256-CBC with a password-derived key. LibreOffice writes
`http://www.w3.org/2001/04/xmlenc#aes256-cbc`, a SHA-256 start key and a SHA-256/1K checksum.

## Decision

### 1. Public options

- `OdtEncryptionOptions(password)` with an optional `IterationCount` (default 100000) is added to
  `OdtWriteOptions.Encryption`.
- `OdtReadOptions.Password` is added; `OdtDocumentReader.Read(Stream, OdtReadOptions, …)` and
  `DocumentReader.Read(stream, DocumentFormat.Odt, password, …)` open an encrypted package.
- An unencrypted ODT ignores the password, as DOCX and PDF already do.

### 2. Format

The writer follows ODF 1.3 part 2:

- a SHA-256 start key from the UTF-8 password;
- per file entry a random 16-byte salt, PBKDF2-HMAC-SHA1 with 100000 iterations and a 32-byte key;
- a random 16-byte IV and AES-256-CBC with PKCS#7 padding;
- deflate before encryption and a SHA-256/1K checksum over the compressed plaintext;
- `manifest:encryption-data` with `manifest:algorithm`, `manifest:key-derivation` and
  `manifest:start-key-generation`; `manifest:size` is the uncompressed plaintext size;
- the `mimetype` entry and `META-INF/manifest.xml` stay unencrypted, and encrypted entries are
  stored (`CompressionMethod.Stored`) as the spec requires.

The output is not deterministic (random salts and IVs). Encryption and an ODT template are
mutually exclusive in this slice and are rejected with `UnsupportedFeatureException`.

### 3. Reading

The reader decrypts the package to an in-memory **plain** ODT before parsing: it reads the
manifest, derives the key per entry, verifies the checksum, inflates the plaintext and rewrites
the manifest without the encryption data. The preserved source (`PortableDocument.SourcePackage`)
is therefore a plain package; an unmodified read/write cycle writes a plain ODT unless the caller
asks for encryption again.

### 4. Limits

- The external interoperability check with LibreOffice is not automated yet: LibreOffice's
  command line has no password option for headless conversion. The round-trip is covered by the
  compatibility suite and the format follows ODF 1.3; a manual LibreOffice open remains for the
  verification checklist.
- Only AES-256-CBC, PBKDF2-HMAC-SHA1 and SHA-256/SHA-1 start keys and checksums are accepted;
  Blowfish-CFB and PGP encryption are reported as unsupported.
- ODT signatures are not part of this record; they remain the next protection slice.

## Consequences

- `OdtWriteOptions.Encryption` produces a password-protected ODT; `OdtReadOptions.Password` opens
  it. Missing or wrong passwords raise `DocumentValidationException`.
- An encrypted source read without a password fails clearly instead of returning a damaged model.
- The manifest no longer has to be regenerated for a plain read/write cycle: the decrypted source
  is a valid plain ODT.

## Alternatives rejected

- **Blowfish-CFB only (the legacy mandatory algorithm).** Conforming consumers must support it,
  but modern producers use AES; writing Blowfish would be weaker and less interoperable.
- **Encrypt the whole ZIP instead of the entries.** ODF encryption is entry-based; a whole-ZIP
  password would not open in LibreOffice/Word.
- **Keep the encrypted source package for preservation.** The reader would need to decrypt every
  part again on every write; a plain in-memory package keeps the existing source-preservation
  paths working.
