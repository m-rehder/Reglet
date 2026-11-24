// Writes a compact, grep-friendly Markdown reference of the public API: one line per member,
// with its C# signature and the first paragraph of its XML summary.
//   dotnet run --project tools/ApiReference -- docs/api-reference.md          (write)
//   dotnet run --project tools/ApiReference -- docs/api-reference.md --check  (fail if stale)
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ApiReference <output.md> [--check]");
    return 2;
}

string[] assemblyNames = ["Reglet", "Reglet.Core", "Reglet.Docx", "Reglet.Odt", "Reglet.Pdf", "Reglet.Html", "Reglet.Fonts", "Reglet.Hyphenation", "Reglet.Xlsx"];
var docs = new Dictionary<string, string>();
var output = new StringBuilder();
output.Append("""
    # Reglet API reference

    Generated from the public API and its XML comments; do not edit by hand. Regenerate with
    `dotnet run --project tools/ApiReference -- docs/api-reference.md`.

    Layout, so that it can be searched line by line: `## <package>`, `### <namespace>`,
    `#### <type>` with its declaration, then one line per member: `- <signature> — <summary>`.
    Members inherited from a base type are listed under that base type. Start with
    `DocumentWriter` (write a `PortableDocument` as DOCX/ODT/PDF/HTML) and `DocumentReader` (read one);
    build documents with `PortableDocument.Create()` (a `PortableDocumentBuilder`). Lengths are `Length` values
    (see docs/units.md). Examples: docs/examples.md; guide: docs/guide.md.

    """);

int count = 0;
foreach (var name in assemblyNames)
{
    var assembly = Assembly.Load(name);
    LoadDocs(Path.ChangeExtension(assembly.Location, ".xml"));
    output.Append($"\n## {name}\n");
    var types = assembly.GetExportedTypes().Where(IsVisible).OrderBy(t => t.Namespace).ThenBy(DisplayName, StringComparer.Ordinal);
    foreach (var group in types.GroupBy(t => t.Namespace))
    {
        output.Append($"\n### {group.Key}\n");
        foreach (var type in group)
        {
            WriteType(type);
        }
    }
}

var text = output.ToString().Replace("\r\n", "\n");
var target = args[0];
if (args.Contains("--check"))
{
    if (!File.Exists(target) || File.ReadAllText(target).Replace("\r\n", "\n") != text)
    {
        Console.Error.WriteLine($"{target} is out of date; regenerate it with: dotnet run --project tools/ApiReference -- {target}");
        return 1;
    }
    Console.WriteLine($"{target} is up to date ({count} entries).");
    return 0;
}
File.WriteAllText(target, text);
Console.WriteLine($"Wrote {target} ({count} entries).");
return 0;

void WriteType(Type type)
{
    count++;
    output.Append($"\n#### {DisplayName(type)}\n\n`{TypeDeclaration(type)}`");
    var summary = Summary("T:" + XmlTypeName(type));
    output.Append(summary.Length > 0 ? $" — {summary}\n\n" : "\n\n");
    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    if (type.IsEnum)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Line($"{field.Name} = {Convert.ToInt64(field.GetRawConstantValue())}", "F:" + XmlTypeName(type) + "." + field.Name);
        }
        return;
    }
    if (typeof(Delegate).IsAssignableFrom(type))
    {
        return;
    }
    foreach (var ctor in type.GetConstructors(flags).Where(IsVisible))
    {
        Line($"{(ctor.IsStatic ? "static " : "")}new {CleanName(type)}({Parameters(ctor)})", MethodId(ctor));
    }
    foreach (var field in type.GetFields(flags).Where(IsVisible))
    {
        var modifier = field.IsLiteral ? "const " : field.IsStatic ? (field.IsInitOnly ? "static readonly " : "static ") : field.IsInitOnly ? "readonly " : "";
        Line($"{modifier}{TypeName(field.FieldType, new NullabilityInfoContext().Create(field))} {field.Name}", "F:" + XmlTypeName(type) + "." + field.Name);
    }
    foreach (var property in type.GetProperties(flags).Where(IsVisible))
    {
        var getter = property.GetMethod is { IsPublic: true } ? "get; " : "";
        var setter = property.SetMethod is { IsPublic: true } set
            ? set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? "init; " : "set; "
            : "";
        var indexParameters = property.GetIndexParameters();
        var name = indexParameters.Length > 0 ? $"this[{string.Join(", ", indexParameters.Select(Parameter))}]" : property.Name;
        var id = "P:" + XmlTypeName(type) + "." + property.Name + (indexParameters.Length > 0 ? $"({string.Join(",", indexParameters.Select(p => XmlTypeName(p.ParameterType)))})" : "");
        var accessor = property.GetMethod ?? property.SetMethod!;
        var required = property.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : "";
        Line($"{(accessor.IsStatic ? "static " : "")}{required}{TypeName(property.PropertyType, new NullabilityInfoContext().Create(property))} {name} {{ {getter}{setter}}}", id);
    }
    foreach (var e in type.GetEvents(flags).Where(IsVisible))
    {
        Line($"event {TypeName(e.EventHandlerType!)} {e.Name}", "E:" + XmlTypeName(type) + "." + e.Name);
    }
    foreach (var method in type.GetMethods(flags).Where(m => (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)) && IsVisible(m)).OrderBy(m => m.Name, StringComparer.Ordinal))
    {
        var modifier = method.IsStatic ? "static " : method.IsAbstract && !type.IsInterface ? "abstract " : "";
        var generic = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>" : "";
        var returnType = TypeName(method.ReturnType, new NullabilityInfoContext().Create(method.ReturnParameter));
        Line($"{modifier}{returnType} {method.Name}{generic}({Parameters(method)})", MethodId(method));
    }
}

void Line(string signature, string id)
{
    count++;
    var summary = Summary(id);
    output.Append($"- `{signature}`{(summary.Length > 0 ? " — " + summary : "")}\n");
}

static bool IsVisible(MemberInfo member)
    => !member.IsDefined(typeof(CompilerGeneratedAttribute))
       && member.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never
       && member is not MethodBase { IsPublic: false } and not FieldInfo { IsPublic: false };

static string DisplayName(Type type) => type.IsNested ? DisplayName(type.DeclaringType!) + "." + CleanName(type) : CleanName(type);

static string CleanName(Type type)
{
    var name = type.Name;
    var tick = name.IndexOf('`');
    if (tick >= 0)
    {
        name = name[..tick];
    }
    var own = type.GetGenericArguments().Skip(type.DeclaringType?.GetGenericArguments().Length ?? 0).ToArray();
    return own.Length > 0 ? $"{name}<{string.Join(", ", own.Select(a => a.Name))}>" : name;
}

static string TypeDeclaration(Type type)
{
    string kind;
    if (type.IsEnum)
    {
        kind = "enum";
    }
    else if (typeof(Delegate).IsAssignableFrom(type))
    {
        var invoke = type.GetMethod("Invoke")!;
        return $"delegate {TypeName(invoke.ReturnType)} {CleanName(type)}({Parameters(invoke)})";
    }
    else if (type.IsInterface)
    {
        kind = "interface";
    }
    else if (type.IsValueType)
    {
        kind = IsRecord(type) ? "readonly record struct" : type.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly struct" : "struct";
    }
    else
    {
        kind = (type.IsAbstract && type.IsSealed ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "") + (IsRecord(type) ? "record" : "class");
    }
    var bases = new List<string>();
    if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
    {
        bases.Add(TypeName(baseType));
    }
    var inherited = type.BaseType?.GetInterfaces() ?? [];
    bases.AddRange(type.GetInterfaces().Where(i => !inherited.Contains(i) && !i.Name.StartsWith("IEquatable", StringComparison.Ordinal)).Select(i => TypeName(i)).Order(StringComparer.Ordinal));
    return $"{kind} {CleanName(type)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}";
}

static bool IsRecord(Type type)
    => type.GetMethod("<Clone>$") is not null
       || type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null
       || (type.IsValueType && type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null);

static string Parameters(MethodBase method)
{
    var parameters = method.GetParameters().Select(Parameter);
    var prefix = method.IsDefined(typeof(ExtensionAttribute)) ? "this " : "";
    return prefix + string.Join(", ", parameters);
}

static string Parameter(ParameterInfo parameter)
{
    var type = parameter.ParameterType;
    var modifier = "";
    if (type.IsByRef)
    {
        modifier = parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
        type = type.GetElementType()!;
    }
    if (parameter.IsDefined(typeof(ParamArrayAttribute)))
    {
        modifier = "params ";
    }
    var text = $"{modifier}{TypeName(type, new NullabilityInfoContext().Create(parameter))} {parameter.Name}";
    if (parameter.HasDefaultValue)
    {
        text += " = " + DefaultValue(parameter.RawDefaultValue, type);
    }
    return text;
}

static string DefaultValue(object? value, Type type) => value switch
{
    null => type.IsValueType && Nullable.GetUnderlyingType(type) is null ? "default" : "null",
    string s => $"\"{s}\"",
    bool b => b ? "true" : "false",
    _ when type.IsEnum => $"{type.Name}.{Enum.ToObject(type, value)}",
    _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
};

static string TypeName(Type type, NullabilityInfo? nullability = null)
{
    if (Nullable.GetUnderlyingType(type) is { } underlying)
    {
        return TypeName(underlying) + "?";
    }
    var suffix = nullability?.ReadState == NullabilityState.Nullable && !type.IsValueType ? "?" : "";
    if (type.IsArray)
    {
        return TypeName(type.GetElementType()!, nullability?.ElementType) + "[]" + suffix;
    }
    if (type.IsByRef)
    {
        return TypeName(type.GetElementType()!, nullability);
    }
    if (type.IsGenericParameter)
    {
        return type.Name + suffix;
    }
    var keyword = Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => "bool", TypeCode.Byte => "byte", TypeCode.SByte => "sbyte", TypeCode.Char => "char",
        TypeCode.Int16 => "short", TypeCode.UInt16 => "ushort", TypeCode.Int32 => "int", TypeCode.UInt32 => "uint",
        TypeCode.Int64 => "long", TypeCode.UInt64 => "ulong", TypeCode.Single => "float", TypeCode.Double => "double",
        TypeCode.Decimal => "decimal", TypeCode.String => "string",
        _ => type == typeof(object) ? "object" : type == typeof(void) ? "void" : null,
    };
    if (keyword is not null && !type.IsEnum)
    {
        return keyword + suffix;
    }
    var name = type.IsNested && !type.IsGenericParameter ? DisplayName(type.DeclaringType!) + "." + type.Name : type.Name;
    var tick = name.LastIndexOf('`');
    if (tick >= 0)
    {
        name = name[..tick];
    }
    if (type.IsGenericType)
    {
        var arguments = type.GetGenericArguments();
        var argumentNullability = nullability?.GenericTypeArguments;
        if (type.IsGenericType && type.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true)
        {
            return $"({string.Join(", ", arguments.Select((a, i) => TypeName(a, argumentNullability?.ElementAtOrDefault(i))))}){suffix}";
        }
        name += $"<{string.Join(", ", arguments.Select((a, i) => TypeName(a, argumentNullability?.ElementAtOrDefault(i))))}>";
    }
    return name + suffix;
}

// XML documentation IDs (ECMA-334 annex D).
static string XmlTypeName(Type type)
{
    if (type.IsGenericParameter)
    {
        return (type.DeclaringMethod is not null ? "``" : "`") + type.GenericParameterPosition;
    }
    if (type.IsArray)
    {
        return XmlTypeName(type.GetElementType()!) + "[]";
    }
    if (type.IsByRef)
    {
        return XmlTypeName(type.GetElementType()!) + "@";
    }
    if (type.IsGenericType && !type.IsGenericTypeDefinition)
    {
        var definition = XmlTypeName(type.GetGenericTypeDefinition());
        return definition[..definition.LastIndexOf('`')] + "{" + string.Join(",", type.GetGenericArguments().Select(XmlTypeName)) + "}";
    }
    return (type.IsNested ? XmlTypeName(type.DeclaringType!) + "." + type.Name : type.Namespace + "." + type.Name);
}

static string MethodId(MethodBase method)
{
    var name = method.IsConstructor ? (method.IsStatic ? "#cctor" : "#ctor") : method.Name;
    if (method.IsGenericMethod)
    {
        name += "``" + method.GetGenericArguments().Length;
    }
    var parameters = method.GetParameters();
    var id = $"M:{XmlTypeName(method.DeclaringType!)}.{name}";
    if (parameters.Length > 0)
    {
        id += $"({string.Join(",", parameters.Select(p => XmlTypeName(p.ParameterType)))})";
    }
    if (method is MethodInfo { Name: "op_Implicit" or "op_Explicit" } conversion)
    {
        id += "~" + XmlTypeName(conversion.ReturnType);
    }
    return id;
}

void LoadDocs(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("XML documentation file missing.", path);
    }
    foreach (var member in XDocument.Load(path).Descendants("member"))
    {
        docs[(string)member.Attribute("name")!] = member.Element("summary") is { } summary ? Flatten(summary) : "";
    }
}

string Summary(string id) => docs.TryGetValue(id, out var summary) ? summary : "";

static string Flatten(XElement element)
{
    var text = new StringBuilder();
    foreach (var node in element.Nodes())
    {
        text.Append(node switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" or "seealso" } e when e.Attribute("langword") is { } word => $"`{word.Value}`",
            XElement { Name.LocalName: "see" or "seealso" } e when e.Attribute("cref") is { } cref => $"`{ShortCref(cref.Value)}`",
            XElement { Name.LocalName: "see" or "seealso" } e when e.Attribute("href") is { } href => e.Value.Length > 0 ? e.Value : href.Value,
            XElement { Name.LocalName: "paramref" or "typeparamref" } e => $"`{e.Attribute("name")?.Value}`",
            XElement { Name.LocalName: "c" } e => $"`{e.Value}`",
            XElement { Name.LocalName: "para" } e => " " + Flatten(e) + " ",
            XElement e => Flatten(e),
            _ => "",
        });
    }
    return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
}

// "M:Reglet.Core.Paragraph.Create(System.String)" -> "Paragraph.Create"
static string ShortCref(string cref)
{
    var kind = cref.Length > 2 && cref[1] == ':' ? cref[0] : 'T';
    var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
    var paren = name.IndexOf('(');
    if (paren >= 0)
    {
        name = name[..paren];
    }
    name = Regex.Replace(name, @"`+\d+", "");
    var parts = name.Split('.');
    return kind == 'T' || parts.Length < 2 ? parts[^1] : $"{parts[^2]}.{parts[^1]}";
}
