using System.Reflection;
using Reglet;

/// <summary>
/// ensures that the public API of Core and Facade exposes no backend or
/// serialization types (OOXML, ODF, PDF, ZIP).
/// </summary>
internal static class ApiSurfaceChecks
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Xml",
        "System.IO.Packaging",
        "System.IO.Compression",
        "System.Text.Json",
    ];

    internal static int Run()
    {
        try
        {
            var assemblies = new[] { typeof(PortableDocument).Assembly, typeof(DocumentFormat).Assembly };
            var violations = new List<string>();

            foreach (var assembly in assemblies.Distinct())
            {
                foreach (var type in assembly.GetExportedTypes())
                {
                    CheckType(type, violations);
                }
            }

            if (violations.Count > 0)
            {
                foreach (var violation in violations)
                {
                    Console.Error.WriteLine("API violation: " + violation);
                }

                return 1;
            }

            Console.WriteLine("API surface (E09): no backend types in public signatures.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("API surface (E09): " + exception);
            return 1;
        }
    }

    private static void CheckType(Type type, List<string> violations)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                Check(parameter.ParameterType, $"{type.FullName}..ctor({parameter.Name})", violations);
            }
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName)
            {
                continue;
            }

            Check(method.ReturnType, $"{type.FullName}.{method.Name} (return)", violations);
            foreach (var parameter in method.GetParameters())
            {
                Check(parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})", violations);
            }
        }

        foreach (var property in type.GetProperties(flags))
        {
            Check(property.PropertyType, $"{type.FullName}.{property.Name}", violations);
        }

        foreach (var field in type.GetFields(flags))
        {
            Check(field.FieldType, $"{type.FullName}.{field.Name}", violations);
        }
    }

    private static void Check(Type? type, string context, List<string> violations)
    {
        switch (type)
        {
            case null:
            case { IsGenericParameter: true }:
                return;
            case { IsByRef: true } or { IsPointer: true } or { IsArray: true }:
                Check(type.GetElementType(), context, violations);
                return;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Check(argument, context, violations);
            }
        }

        var ns = type.Namespace ?? string.Empty;
        if (ns.StartsWith("Reglet", StringComparison.Ordinal))
        {
            return;
        }

        if (ns.StartsWith("System", StringComparison.Ordinal))
        {
            if (ForbiddenNamespacePrefixes.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
            {
                violations.Add($"{context} uses {type.FullName}");
            }

            return;
        }

        violations.Add($"{context} uses {type.FullName}");
    }
}
