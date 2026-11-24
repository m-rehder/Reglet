# ADR-0045: An MCP server for Reglet

Status: Accepted (2026-11-24)

## Context

AI agents create, convert and check documents all the time, and today mostly do it by starting LibreOffice or
Pandoc or by writing Python. The Model Context Protocol (MCP) lets an agent call tools of a local server. Reglet can
do the document work without any other program, validate the result and read workbooks, which makes it a natural
backend for such a server. The library itself must stay free of dependencies.

## Decision

- **A separate application, `apps/Reglet.Mcp`**, packed as the .NET tool `Reglet.Mcp` (command `reglet-mcp`), not a
  library under `src/`. It uses the official MCP C# SDK (ModelContextProtocol, Apache 2.0) and Microsoft.Extensions
  hosting (MIT). The zero-dependency rule keeps covering every project under `src/`; `apps/` holds shipped
  applications that may depend on packages. Their licences ship in the tool package (`THIRD-PARTY-NOTICES.md`,
  `MICROSOFT-THIRD-PARTY-NOTICES.TXT`).
- **stdio transport only**: the server runs on the user's machine, started by the agent's client. No network
  transport, so there is no remote attack surface and no authentication to get wrong.
- **Nine tools**: `create_document` (DOCX, ODT, PDF or HTML from a Markdown subset: headings, emphasis, bullet and
  numbered lists, pipe tables, page breaks), `convert_document`, `read_document` (Markdown-like text, cut at a limit),
  `fill_template` (`{{Name}}` placeholders of a DOCX or ODT template), `validate_file` (ADR-0044), `create_workbook`
  (rows of values; numbers parsed in the invariant culture, `=` starts a formula, optional bold header that repeats
  when printed), `read_workbook` (values as Excel shows them, formulas without a cached value as `=…`), `merge_pdfs`
  and `extract_pdf_pages`. Reading tools are marked read-only.
- **Files only inside roots**: `--root` (repeatable) or the working directory. Every path is made absolute against the
  first root and must lie inside a root after following symbolic links of the file and its folders; `..` and links do
  not lead out, a link whose target does not exist yet included. Writing needs an existing folder, and an existing file
  is replaced only with `overwrite: true`; every file is written to a temporary file beside it and moved into place
  only when the write succeeded, so a failed write neither leaves half a file nor destroys the one it was to replace.
- **No silent data change in workbooks**: a cell becomes a number only when it is a plain number; values with leading
  zeros or a leading `+` (postcodes, part numbers, phone numbers) stay text.
- **Bounded output and input**: `read_document` cuts at a limit, `read_workbook` shows at most 200 columns, a template
  is read up to 512 MB, at most 200 PDFs are merged; the regular expressions of the Markdown subset have time limits.
- **No configuration from the start directory**: the host reads no `appsettings.json`, environment variables or user
  secrets.
- **Errors are tool errors**: Reglet's errors about the input (invalid or hostile file, unsupported content, a refused
  sheet name, …) become MCP tool errors with their message, so the agent can react; the readers' limits against hostile
  files apply unchanged. stdout carries only the protocol; logging goes to stderr.
- **Markdown in the server, not the library**: Markdown import stays out of the library's scope (feature matrix, out of
  scope); the server has its own small subset for agents.

## Consequences

- A tenth package with a different dependency policy; it is versioned and released with the library.
- Tested by `tests/Reglet.Mcp.Tests`: the tools in-process, the path policy (relative and absolute escapes, folder and
  file links, overwrite) and one session over stdio (initialize, tools/list, tools/call, a refused path).
- HTTP transport, MCP resources and prompts, and roots negotiated with the client are not offered; they can follow when
  a client needs them.
