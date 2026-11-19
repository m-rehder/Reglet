using Gallery;

// The Reglet example gallery: from "Hello World" to complete business documents.
//   dotnet run --project examples/Gallery -- --list                 list all examples
//   dotnet run --project examples/Gallery                           run all
//   dotnet run --project examples/Gallery -- tables --out ./out     one category (or one example by name)
IReadOnlyList<Example> all =
[
    .. Basics.All(), .. Text.All(), .. Layout.All(), .. Tables.All(), .. Graphics.All(),
    .. Collaboration.All(), .. Automation.All(), .. Pdf.All(), .. Showcase.All(),
];
return await ExampleRunner.RunAsync(args, all);
