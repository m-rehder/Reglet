# Reglet MCP server

`reglet-mcp` lets AI agents (Claude, Copilot, Cursor and any other MCP client) create, convert, read, fill and validate
office documents: DOCX, ODT, PDF, HTML and Excel workbooks. It runs locally over stdio and needs neither Office nor
LibreOffice; it is built on [Reglet](https://github.com/m-rehder/Reglet).

## Install

```
dotnet tool install -g Reglet.Mcp
```

It needs the .NET 10 runtime.

## Configure

The server may read and write files only inside the directories given with `--root` (several allowed); without one
it uses the directory it is started in. Paths the agent passes are relative to the first root.

Claude Code:

```
claude mcp add reglet -- reglet-mcp --root ~/Documents/reports
```

Claude Desktop and other clients (`mcpServers` in their JSON configuration):

```json
{
  "mcpServers": {
    "reglet": { "command": "reglet-mcp", "args": ["--root", "/Users/me/Documents/reports"] }
  }
}
```

## Tools

| Tool | What it does |
| --- | --- |
| `create_document` | Writes DOCX, ODT, PDF or HTML from Markdown (headings, emphasis, lists, tables, page breaks). |
| `convert_document` | Converts between DOCX, ODT, PDF and HTML. |
| `read_document` | Returns a document's content as Markdown-like text. |
| `fill_template` | Fills the `{{Name}}` placeholders of a DOCX or ODT template; its styles stay. |
| `validate_file` | Checks DOCX, XLSX, ODT/ODS or PDF against the standards, with part and line of each finding. |
| `create_workbook` | Writes an Excel workbook from rows of values; `=` starts a formula. |
| `read_workbook` | Returns each sheet as rows of the values Excel shows. |
| `merge_pdfs` | Merges PDFs into one. |
| `extract_pdf_pages` | Writes chosen pages of a PDF into a new one. |

## Safety

- No path outside the roots is used, also not through `..` or a symbolic link.
- An existing file is replaced only when the agent passes `overwrite: true`, and only after the new file was written
  completely; a failed write leaves the old file as it was.
- In workbooks, values with leading zeros or a leading `+` (postcodes, phone numbers) stay text.
- The readers are hardened against hostile files (size, depth and decoding budgets); a broken file is a tool error,
  not a crash.
- Nothing is fetched from the network, and no other program is started.

MIT licensed. The assemblies the tool carries (the MCP C# SDK under Apache 2.0, Microsoft.Extensions under MIT) are
listed in `THIRD-PARTY-NOTICES.md` in the package.
