using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Reglet;
using Reglet.Mcp;

// Checks of the Reglet MCP server (ADR-0045): the tools in-process, the path policy, and one session over stdio.
var root = Directory.CreateTempSubdirectory("reglet-mcp-");
var outside = Directory.CreateTempSubdirectory("reglet-mcp-outside-");
var failures = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        failures++;
        Console.Error.WriteLine("FAIL: " + message);
    }
}

void Throws(Action action, string expected, string message)
{
    try
    {
        action();
        Check(false, message + " (no error)");
    }
    catch (McpException exception)
    {
        Check(exception.Message.Contains(expected, StringComparison.Ordinal), $"{message}: {exception.Message}");
    }
}

try
{
    var tools = new DocumentTools(new PathPolicy([root.FullName]));

    // Markdown in, every format out; the text comes back with its structure from DOCX and ODT.
    const string Markdown = "# Status\n\nAll **good**, *on time* and `ok`.\n\n- one\n- two\n\n1. first\n2. second\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n---\n\nNext page.";
    foreach (var extension in new[] { "docx", "odt", "pdf", "html" })
    {
        var result = await tools.CreateDocumentAsync($"report.{extension}", Markdown, "Report");
        Check(result.StartsWith("Wrote ", StringComparison.Ordinal) && File.Exists(Path.Combine(root.FullName, $"report.{extension}")), $"create_document writes {extension}: {result}");
    }

    foreach (var extension in new[] { "docx", "odt" })
    {
        var text = tools.ReadDocument($"report.{extension}");
        Check(text.Contains("# Status", StringComparison.Ordinal) && text.Contains("All good, on time and ok.", StringComparison.Ordinal)
            && text.Contains("- one", StringComparison.Ordinal) && text.Contains("| A | B |", StringComparison.Ordinal) && text.Contains("| 1 | 2 |", StringComparison.Ordinal)
            && text.Contains("Next page.", StringComparison.Ordinal), $"read_document of {extension} keeps the structure:\n{text}");
        var report = tools.ValidateFile($"report.{extension}");
        Check(report.Contains(": valid,", StringComparison.Ordinal), $"Created {extension} is valid: {report}");
    }

    var cut = tools.ReadDocument("report.docx", max_characters: 100);
    Check(cut.Contains("[… cut at 100", StringComparison.Ordinal), "read_document cuts long text with a note.");

    // Conversion, template, workbook, PDF pages.
    Check((await tools.ConvertDocumentAsync("report.docx", "converted.odt")).StartsWith("Wrote ", StringComparison.Ordinal), "convert_document DOCX → ODT.");
    await tools.CreateDocumentAsync("template.docx", "Dear {{Name}},\n\nyour order {{Order}} ships today.");
    await tools.FillTemplateAsync("template.docx", "letter.docx", new() { ["Name"] = "Ms Jansen", ["Order"] = "A-1001" });
    Check(tools.ReadDocument("letter.docx").Contains("Dear Ms Jansen,", StringComparison.Ordinal) && tools.ReadDocument("letter.docx").Contains("A-1001", StringComparison.Ordinal), "fill_template fills the placeholders.");
    Throws(() => tools.FillTemplateAsync("template.docx", "letter.pdf", new() { ["Name"] = "x" }).GetAwaiter().GetResult(), "same format", "fill_template keeps the template's format.");

    await tools.CreateWorkbookAsync("sums.xlsx", [new SheetInput { Name = "Items", Rows = [["Item", "Qty", "Price", "Total"], ["Arm", "4", "89.9", "=B2*C2"], ["Note", "n/a", null, ""]] }]);
    var sheet = tools.ReadWorkbook("sums.xlsx");
    Check(sheet.Contains("## Items", StringComparison.Ordinal) && sheet.Contains("2\tArm\t4\t89.9\t=B2*C2", StringComparison.Ordinal) && sheet.Contains("3\tNote\tn/a", StringComparison.Ordinal), $"create_workbook and read_workbook: numbers, formulas, text:\n{sheet}");
    Check(tools.ValidateFile("sums.xlsx").Contains("XLSX: valid", StringComparison.Ordinal), "The workbook is valid.");
    Throws(() => tools.CreateWorkbookAsync("bad.xlsx", [new SheetInput { Name = "a/b", Rows = [] }]).GetAwaiter().GetResult(), "not a valid sheet name", "create_workbook rejects a sheet name Excel refuses.");
    Throws(() => tools.CreateWorkbookAsync("bad.docx", [new SheetInput { Name = "S" }]).GetAwaiter().GetResult(), ".xlsx", "create_workbook writes .xlsx only.");

    tools.MergePdfs(["report.pdf", "report.pdf"], "both.pdf");
    var pages = DocumentReader.ReadPdfPages(File.OpenRead(Path.Combine(root.FullName, "both.pdf"))).Count;
    tools.ExtractPdfPages("both.pdf", $"2-{pages}", "rest.pdf");
    Check(DocumentReader.ReadPdfPages(File.OpenRead(Path.Combine(root.FullName, "rest.pdf"))).Count == pages - 1, "merge_pdfs and extract_pdf_pages.");
    Check(DocumentTools.PageList("1-3,7,5").SequenceEqual([1, 2, 3, 7, 5]), "Page lists keep their order.");
    foreach (var bad in new[] { "0", "3-1", "x", "1-20000", "" })
    {
        Throws(() => DocumentTools.PageList(bad), bad.Length == 0 ? "No pages" : "not a page", $"'{bad}' is not a page list.");
    }

    // The path policy: nothing outside the roots, also not through .. or a symbolic link; no silent overwrite.
    File.WriteAllText(Path.Combine(outside.FullName, "secret.docx"), "secret");
    Throws(() => tools.ReadDocument(Path.Combine(outside.FullName, "secret.docx")), "outside", "An absolute path outside the root is refused.");
    Throws(() => tools.ReadDocument("../" + outside.Name + "/secret.docx"), "outside", "A relative path out of the root is refused.");
    try
    {
        Directory.CreateSymbolicLink(Path.Combine(root.FullName, "link"), outside.FullName);
        File.CreateSymbolicLink(Path.Combine(root.FullName, "file-link.docx"), Path.Combine(outside.FullName, "secret.docx"));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // Windows without the right to create links: the link cases cannot be set up here.
        Console.WriteLine("note: symbolic links cannot be created here; the link checks are skipped.");
    }

    if (Directory.Exists(Path.Combine(root.FullName, "link")))
    {
        Throws(() => tools.ReadDocument("link/secret.docx"), "outside", "A symbolic link out of the root is refused.");
        Throws(() => tools.CreateDocumentAsync("link/new.docx", "x").GetAwaiter().GetResult(), "outside", "Writing through a link out of the root is refused.");
        Throws(() => tools.ReadDocument("file-link.docx"), "outside", "A file link out of the root is refused.");
    }
    Throws(() => tools.CreateDocumentAsync("report.docx", "x").GetAwaiter().GetResult(), "overwrite", "An existing file is not replaced without overwrite.");
    Check((await tools.CreateDocumentAsync("report.docx", "x", overwrite: true)).StartsWith("Wrote ", StringComparison.Ordinal), "overwrite: true replaces it.");
    Throws(() => tools.ReadDocument("missing.docx"), "does not exist", "A missing file is reported.");
    Throws(() => tools.ReadDocument("sums.xlsx"), "", "A workbook is not a document.");
    File.WriteAllBytes(Path.Combine(root.FullName, "broken.docx"), [1, 2, 3]);
    Throws(() => tools.ReadDocument("broken.docx"), "", "A broken file is a tool error, not a crash.");
    Check(tools.ValidateFile("broken.docx").Contains("not valid", StringComparison.Ordinal), "validate_file reports a broken file.");
    try
    {
        _ = new PathPolicy([Path.Combine(root.FullName, "no-such-folder")]);
        Check(false, "A missing root is refused at start.");
    }
    catch (ArgumentException exception)
    {
        Check(exception.Message.Contains("does not exist", StringComparison.Ordinal), "A missing root is refused at start: " + exception.Message);
    }

    // Audit 2026-11-24: a failed overwrite keeps the old file; a dangling link out of the root is refused; codes stay
    // text; a far-right column does not blow up the text.
    var before = File.ReadAllBytes(Path.Combine(root.FullName, "rest.pdf"));
    Throws(() => tools.ExtractPdfPages("both.pdf", "900", "rest.pdf", overwrite: true), "", "Extracting a page that does not exist fails.");
    Check(File.ReadAllBytes(Path.Combine(root.FullName, "rest.pdf")).SequenceEqual(before), "A failed overwrite leaves the old file as it was.");
    Check(!Directory.GetFiles(root.FullName, "*.tmp").Any(), "A failed write leaves no temporary file.");
    try
    {
        File.CreateSymbolicLink(Path.Combine(root.FullName, "dangling.docx"), Path.Combine(outside.FullName, "created-through-link.docx"));
        Throws(() => tools.CreateDocumentAsync("dangling.docx", "x", overwrite: true).GetAwaiter().GetResult(), "outside", "A dangling link out of the root is refused.");
        Check(!File.Exists(Path.Combine(outside.FullName, "created-through-link.docx")), "Nothing was written outside the root.");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine("note: symbolic links cannot be created here; the dangling link check is skipped.");
    }

    foreach (var (value, number) in new[] { ("01234", false), ("+4930123", false), ("-012", false), ("0", true), ("0.5", true), ("-0.5", true), ("1e3", true), (" 5 ", true), ("12,5", false), ("NaN", false) })
    {
        Check(DocumentTools.IsNumber(value, out _) == number, $"'{value}' is {(number ? "a number" : "text")}.");
    }

    await tools.CreateWorkbookAsync("codes.xlsx", [new SheetInput { Name = "S", Rows = [["Postcode", "Phone"], ["01067", "+49 351 1234"]] }]);
    Check(tools.ReadWorkbook("codes.xlsx").Contains("2\t01067\t+49 351 1234", StringComparison.Ordinal), "Codes and phone numbers stay text in the workbook.");
    var wide = new Workbook();
    wide.AddWorksheet("W").Cell(1, 500).Value = "far";
    await using (var file = File.Create(Path.Combine(root.FullName, "wide.xlsx")))
    {
        await new XlsxWorkbookWriter().WriteAsync(wide, file);
    }

    var wideText = tools.ReadWorkbook("wide.xlsx");
    Check(wideText.Contains("[columns after 200 are not shown]", StringComparison.Ordinal) && wideText.Length < 2000, "A far-right column is cut, not padded out.");

    // One session over stdio, as a client runs the server.
    failures += await StdioSession(root.FullName);
}
catch (Exception exception)
{
    failures++;
    Console.Error.WriteLine("FAIL: " + exception);
}
finally
{
    root.Delete(recursive: true);
    outside.Delete(recursive: true);
}

Console.WriteLine(failures == 0 ? "Reglet MCP server: all checks passed." : $"Reglet MCP server: {failures} check(s) failed.");
return failures == 0 ? 0 : 1;

static async Task<int> StdioSession(string root)
{
    var server = Path.Combine(AppContext.BaseDirectory, "Reglet.Mcp.dll");
    using var process = Process.Start(new ProcessStartInfo("dotnet", [server, "--root", root])
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    var id = 0;
    async Task<JsonNode> Send(string method, object? parameters)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = JsonSerializer.SerializeToNode(parameters);
        }

        await process.StandardInput.WriteLineAsync(request.ToJsonString());
        await process.StandardInput.FlushAsync();
        while (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
        {
            if (JsonNode.Parse(line) is { } response && response["id"]?.GetValue<int>() == id)
            {
                return response;
            }
        }

        throw new InvalidOperationException("The server closed the connection: " + await process.StandardError.ReadToEndAsync());
    }

    var failed = 0;
    var initialized = await Send("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "tests", version = "1" } });
    if (initialized["result"]?["serverInfo"]?["name"]?.GetValue<string>() != "reglet")
    {
        failed++;
        Console.Error.WriteLine("FAIL: initialize: " + initialized);
    }

    await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    var list = await Send("tools/list", null);
    var names = list["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>()).ToList() ?? [];
    string[] expected = ["create_document", "convert_document", "read_document", "fill_template", "validate_file", "read_workbook", "create_workbook", "merge_pdfs", "extract_pdf_pages"];
    if (!expected.All(names.Contains))
    {
        failed++;
        Console.Error.WriteLine("FAIL: tools/list: " + string.Join(", ", names));
    }

    var call = await Send("tools/call", new { name = "read_document", arguments = new { path = "letter.docx" } });
    if (call["result"]?["content"]?[0]?["text"]?.GetValue<string>()?.Contains("Ms Jansen", StringComparison.Ordinal) != true)
    {
        failed++;
        Console.Error.WriteLine("FAIL: tools/call read_document: " + call);
    }

    var refused = await Send("tools/call", new { name = "read_document", arguments = new { path = "/etc/hosts" } });
    if (refused["result"]?["isError"]?.GetValue<bool>() != true)
    {
        failed++;
        Console.Error.WriteLine("FAIL: a path outside the root is a tool error over stdio: " + refused);
    }

    process.StandardInput.Close();
    if (!process.WaitForExit(10_000))
    {
        process.Kill();
        failed++;
        Console.Error.WriteLine("FAIL: the server does not end when stdin closes.");
    }

    return failed;
}
