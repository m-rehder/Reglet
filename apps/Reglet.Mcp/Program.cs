using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reglet.Mcp;

// The Reglet MCP server (ADR-0045), over stdio:
//   reglet-mcp [--root <directory>]...
// Tools may read and write files only inside the root directories; without --root the working directory is the root.
var roots = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--root" && index + 1 < args.Length)
    {
        roots.Add(args[++index]);
    }
    else if (args[index] is "--help" or "-h")
    {
        Console.Error.WriteLine("reglet-mcp [--root <directory>]...  MCP server over stdio; tools use files inside the roots (default: the working directory).");
        return 0;
    }
    else
    {
        Console.Error.WriteLine($"Unknown argument '{args[index]}'. Use --root <directory>.");
        return 2;
    }
}

PathPolicy policy;
try
{
    policy = new PathPolicy(roots.Count > 0 ? roots : [Environment.CurrentDirectory]);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

// An empty builder: no appsettings.json, environment variables or user secrets are read from wherever the tool starts.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

// stdout carries the protocol; anything logged goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(policy);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "reglet", Version = typeof(PathPolicy).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" };
        options.ServerInstructions = "Reglet creates, converts, reads, fills and validates DOCX, ODT, PDF, HTML and XLSX files without Office or LibreOffice. "
            + "Paths are relative to, and must stay inside: " + string.Join(", ", policy.Roots) + ".";
    })
    .WithStdioServerTransport()
    .WithTools<DocumentTools>();

await builder.Build().RunAsync();
return 0;
