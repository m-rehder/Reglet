using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Reglet;

/// <summary>Form fields (ADR-0024): model rules, the AcroForm in the PDF and the warnings of DOCX and ODT.</summary>
internal static class PdfFormChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidFields();
            WritesAcroForm();
            ReportsProblems();
            WorksWithSignature();
            RoundTripsDocx();
            RoundTripsOdt();
            ReadsLibreOfficeMultiline();
            OdtBandsReport();
            Console.WriteLine("Form fields: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Form fields: " + exception);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static Paragraph Text(string text) => new(new Inline[] { new TextRun(text) });

    private static PortableDocument Document(params Block[] blocks) => new(new DocumentMetadata(), new Section(blocks));

    private static (string Pdf, IReadOnlyList<WriteWarning> Warnings) Write(PortableDocument document, PdfWriteOptions? options = null)
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer, options ?? new PdfWriteOptions()).GetAwaiter().GetResult();
        return (Encoding.Latin1.GetString(buffer.ToArray()), result.Warnings);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void RejectsInvalidFields()
    {
        foreach (var make in new Func<FormField>[]
        {
            () => new FormField(FormFieldKind.Text, ""),
            () => new FormField(FormFieldKind.Text, "a.b"),
            () => new FormField(FormFieldKind.DropDown, "d"),
            () => new FormField(FormFieldKind.DropDown, "d", options: Array.Empty<string>()),
            () => new FormField(FormFieldKind.DropDown, "d", options: new[] { "a", "" }),
            () => new FormField(FormFieldKind.Text, "t", options: new[] { "a" }),
            () => new FormField(FormFieldKind.Text, "t", width: Length.Zero),
            () => new FormField(FormFieldKind.DropDown, "d", options: new[] { "a" }) { Value = "b" },
            () => new FormField(FormFieldKind.Text, "t") { MaxLength = 0 },
            () => new FormField((FormFieldKind)9, "t"),
        })
        {
            var threw = false;
            try
            {
                make();
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Require(threw, "An invalid form field is rejected.");
        }

        var box = new FormField(FormFieldKind.CheckBox, "c");
        Require(box.Width.Points == 12 && box.Height.Points == 12, "A check box is 12 pt by default.");
        var text = new FormField(FormFieldKind.Text, "t");
        Require(text.Width.Points == 150 && text.Height.Points == 20, "A text field is 150 by 20 pt by default.");
    }

    private static void WritesAcroForm()
    {
        var (pdf, warnings) = Write(Document(
            Text("Form"),
            new FormField(FormFieldKind.Text, "name") { Value = "Maik", MaxLength = 20, Required = true, ToolTip = "Your name" },
            new FormField(FormFieldKind.Text, "note") { Multiline = true, ReadOnly = true },
            new FormField(FormFieldKind.DropDown, "country", options: new[] { "DE", "AT" }) { Value = "AT" },
            new FormField(FormFieldKind.CheckBox, "agree") { Checked = true },
            new FormField(FormFieldKind.RadioButton, "pay") { ExportValue = "card" },
            new FormField(FormFieldKind.RadioButton, "pay") { ExportValue = "bank", Checked = true },
            new TableBlock(new[] { new TableRow(new TableCell(new Block[] { new FormField(FormFieldKind.Text, "cell") })) })));
        Require(warnings.Count == 0, "No warning: " + string.Join("|", warnings.Select(w => w.Message)));
        Require(Count(pdf, "/Subtype /Widget") == 7, "Seven widgets: " + Count(pdf, "/Subtype /Widget"));
        Require(pdf.Contains("/AcroForm") && pdf.Contains("/DA (/Helv 0 Tf 0 g)") && pdf.Contains("/DA (0 g /Helv 10 Tf)"), "AcroForm and fields name the same default font (Helvetica).");
        Require(Count(pdf, "/FT /Tx") == 3 && Count(pdf, "/FT /Ch") == 1, "Three text fields and one choice.");
        Require(Count(pdf, "/FT /Btn") == 2, "A check box and the radio group (one parent).");
        Require(pdf.Contains("/T (name)") && pdf.Contains("/T (pay)") && pdf.Contains("/Kids ["), "Names and the kids of the radio group.");
        Require(pdf.Contains("/V /bank") && pdf.Contains("/AS /bank") && pdf.Contains("/AS /Off"), "The radio group holds the chosen value.");
        Require(pdf.Contains("/Opt [(DE) (AT) ]") && pdf.Contains("/MaxLen 20") && pdf.Contains("/TU (Your name)"), "Options, maximum length and tool tip.");
        Require(pdf.Contains("/Ff 2 "), "Required flag is set.");
    }

    private static void ReportsProblems()
    {
        var (pdf, warnings) = Write(Document(
            new FormField(FormFieldKind.Text, "a"),
            new FormField(FormFieldKind.CheckBox, "a"),
            new FormField(FormFieldKind.RadioButton, "r"),
            new FormField(FormFieldKind.RadioButton, "g") { ExportValue = "x" },
            new FormField(FormFieldKind.RadioButton, "g") { ExportValue = "x" }));
        var messages = string.Join("|", warnings.Select(w => w.Message));
        Require(messages.Contains("'a' is used twice"), "Duplicate name reported: " + messages);
        Require(messages.Contains("'r' has no export value"), "Radio button without export value reported: " + messages);
        Require(messages.Contains("value 'x' of 'g' is used twice"), "Duplicate radio value reported: " + messages);
        Require(Count(pdf, "/Subtype /Widget") == 2, "Only the first field of a name and the first radio button are written.");
    }

    private static void WorksWithSignature()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Forms", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            Document(Text("x"), new FormField(FormFieldKind.Text, "name")),
            buffer,
            new PdfWriteOptions { Signature = new PdfSignatureOptions(certificate) }).GetAwaiter().GetResult();
        var bytes = buffer.ToArray();
        var pdf = Encoding.Latin1.GetString(bytes);
        Require(pdf.Contains("/SigFlags 3") && pdf.Contains("/FT /Tx"), "A signed document keeps the signature field and the form field.");
        Require(PdfSignatureReader.Read(bytes).Single().IsValid, "The signature of a document with a form field is valid.");
    }

    private static void RoundTripsDocx()
    {
        var document = Document(
            Text("before"),
            new FormField(FormFieldKind.Text, "name") { Value = "Maik & <Co>", ToolTip = "Your name", FontSize = Length.FromPoints(12) },
            new FormField(FormFieldKind.Text, "note") { Multiline = true, Value = "one\ntwo", ReadOnly = true },
            new FormField(FormFieldKind.Text, "empty"),
            new FormField(FormFieldKind.DropDown, "country", options: new[] { "DE", "AT" }) { Value = "AT" },
            new FormField(FormFieldKind.CheckBox, "agree") { Checked = true },
            new FormField(FormFieldKind.CheckBox, "later"),
            new FormField(FormFieldKind.RadioButton, "pay") { ExportValue = "card" },
            new FormField(FormFieldKind.RadioButton, "pay") { ExportValue = "bank", Checked = true },
            new TableBlock(new[] { new TableRow(new TableCell(new Block[] { new FormField(FormFieldKind.CheckBox, "cell") { Checked = true } })) }),
            Text("after"));
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Docx).GetAwaiter().GetResult();
        Require(result.Warnings.Count(w => w.Message.Contains("radio button")) == 2, "Each radio button is reported: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var bytes = buffer.ToArray();
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            System.Xml.Linq.XDocument.Parse(xml);
            Require(Count(xml, "<w:sdt>") == 9, "Nine content controls: " + Count(xml, "<w:sdt>"));
            Require(xml.Contains("<w:dropDownList>") && xml.Contains("w14:checkbox") && xml.Contains("<w:text w:multiLine=\"1\"") && xml.Contains("sdtContentLocked") && xml.Contains("<w:showingPlcHdr"), "Control types, lock and placeholder.");
        }

        var read = DocumentReader.Read(new MemoryStream(bytes), DocumentFormat.Docx).Document.Sections[0].Blocks;
        var fields = read.OfType<FormField>().ToList();
        Require(fields.Count == 8, "Eight fields at the top level: " + fields.Count);
        var name = fields.Single(f => f.Name == "name");
        Require(name.Kind == FormFieldKind.Text && name.Value == "Maik & <Co>" && name.ToolTip == "Your name" && name.FontSize?.Points == 12, "Text field with value, tool tip and size.");
        var note = fields.Single(f => f.Name == "note");
        Require(note.Multiline && note.ReadOnly && note.Value == "one\ntwo", "Multi-line, read-only text: " + note.Value);
        Require(fields.Single(f => f.Name == "empty").Value is null, "A placeholder is not read as the value.");
        var country = fields.Single(f => f.Name == "country");
        Require(country.Kind == FormFieldKind.DropDown && country.Options!.SequenceEqual(new[] { "DE", "AT" }) && country.Value == "AT", "Drop-down with options and value.");
        Require(fields.Single(f => f.Name == "agree").Checked && !fields.Single(f => f.Name == "later").Checked, "Check boxes.");
        var radios = fields.Where(f => f.Name == "pay").ToList();
        Require(radios.Count == 2 && radios.All(f => f.Kind == FormFieldKind.RadioButton)
            && radios[0].ExportValue == "card" && !radios[0].Checked && radios[1].ExportValue == "bank" && radios[1].Checked, "The radio group is restored.");
        Require(read.OfType<Paragraph>().Count() == 2, "Only the two real paragraphs remain: " + read.OfType<Paragraph>().Count());
        Require(read.OfType<TableBlock>().Single().Rows[0].Cells[0].Blocks.Single() is FormField { Name: "cell", Checked: true }, "A control in a table cell.");

        using var again = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(read)), again, DocumentFormat.Docx).GetAwaiter().GetResult();
        again.Position = 0;
        Require(DocumentReader.Read(again, DocumentFormat.Docx).Document.Sections[0].Blocks.OfType<FormField>().Count() == 8, "The second round trip keeps the fields.");

        // A date control and a control without type stay text.
        var date = System.Xml.Linq.XElement.Parse(
            "<w:sdt xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:sdtPr><w:date/></w:sdtPr>"
            + "<w:sdtContent><w:r><w:t>2026-11-17</w:t></w:r></w:sdtContent></w:sdt>");
        Require(DocxFormControls.TryRead(date) is null, "A date control is not a form field (it stays text).");
    }

    private static void RoundTripsOdt()
    {
        var document = Document(
            Text("before"),
            new FormField(FormFieldKind.Text, "name", Length.FromCentimeters(5), Length.FromCentimeters(0.8)) { Value = "Maik & <Co>", ToolTip = "Your name", MaxLength = 30 },
            new FormField(FormFieldKind.Text, "note") { Multiline = true, Value = "one\ntwo", ReadOnly = true },
            new FormField(FormFieldKind.Text, "empty"),
            new FormField(FormFieldKind.DropDown, "country", options: new[] { "DE", "AT" }) { Value = "AT" },
            new FormField(FormFieldKind.CheckBox, "agree") { Checked = true },
            new FormField(FormFieldKind.CheckBox, "later"),
            new FormField(FormFieldKind.RadioButton, "pay") { ExportValue = "card" },
            new FormField(FormFieldKind.RadioButton, "pay") { ExportValue = "bank", Checked = true },
            new TableBlock(new[] { new TableRow(new TableCell(new Block[] { new FormField(FormFieldKind.CheckBox, "cell") { Checked = true } })) }),
            Text("after"));
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Odt).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "ODT writes every kind without a warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var bytes = buffer.ToArray();
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            using var reader = new StreamReader(zip.GetEntry("content.xml")!.Open());
            var xml = reader.ReadToEnd();
            System.Xml.Linq.XDocument.Parse(xml);
            Require(xml.Contains("<office:forms") && Count(xml, "<draw:control ") == 9, "Forms and nine placements: " + Count(xml, "<draw:control "));
            Require(xml.Contains("<form:textarea") && xml.Contains("<form:listbox") && xml.Contains("<form:checkbox") && xml.Contains("<form:radio"), "Control kinds.");
        }

        var read = DocumentReader.Read(new MemoryStream(bytes), DocumentFormat.Odt).Document.Sections[0].Blocks;
        var fields = read.OfType<FormField>().ToList();
        Require(fields.Count == 8, "Eight fields at the top level: " + fields.Count);
        var name = fields.Single(f => f.Name == "name");
        Require(name.Value == "Maik & <Co>" && name.ToolTip == "Your name" && name.MaxLength == 30, "Text field with value, tool tip and maximum length.");
        Require(Math.Abs(name.Width.Points - Length.FromCentimeters(5).Points) < 0.5 && Math.Abs(name.Height.Points - Length.FromCentimeters(0.8).Points) < 0.5, "The size comes back.");
        var note = fields.Single(f => f.Name == "note");
        Require(note.Multiline && note.ReadOnly && note.Value == "one\ntwo", "Multi-line, read-only text: " + note.Value);
        Require(fields.Single(f => f.Name == "empty").Value is null, "An empty text field has no value.");
        var country = fields.Single(f => f.Name == "country");
        Require(country.Kind == FormFieldKind.DropDown && country.Options!.SequenceEqual(new[] { "DE", "AT" }) && country.Value == "AT", "List box with options and value.");
        Require(fields.Single(f => f.Name == "agree").Checked && !fields.Single(f => f.Name == "later").Checked, "Check boxes.");
        var radios = fields.Where(f => f.Name == "pay").ToList();
        Require(radios.Count == 2 && radios[0].ExportValue == "card" && !radios[0].Checked && radios[1].ExportValue == "bank" && radios[1].Checked, "The radio group.");
        Require(read.OfType<Paragraph>().Count() == 2, "Only the two real paragraphs remain: " + read.OfType<Paragraph>().Count());
        Require(read.OfType<TableBlock>().Single().Rows[0].Cells[0].Blocks.Single() is FormField { Name: "cell", Checked: true }, "A control in a table cell.");

        using var again = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(read)), again, DocumentFormat.Odt).GetAwaiter().GetResult();
        again.Position = 0;
        Require(DocumentReader.Read(again, DocumentFormat.Odt).Document.Sections[0].Blocks.OfType<FormField>().Count() == 8, "The second round trip keeps the fields.");
    }

    // LibreOffice writes a multi-line text as form:text with the line breaks in the value (checked 2026-11-17).
    private static void ReadsLibreOfficeMultiline()
    {
        using var source = new MemoryStream();
        DocumentWriter.WriteAsync(
            Document(new FormField(FormFieldKind.Text, "note") { Multiline = true, Value = "a\nb" }), source, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var patched = new MemoryStream();
        using (var input = new System.IO.Compression.ZipArchive(new MemoryStream(source.ToArray())))
        using (var output = new System.IO.Compression.ZipArchive(patched, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var data = reader.ReadToEnd();
                if (entry.FullName == "content.xml")
                {
                    var start = data.IndexOf("<form:textarea", StringComparison.Ordinal);
                    var end = data.IndexOf("</form:textarea>", StringComparison.Ordinal) + "</form:textarea>".Length;
                    var element = data[start..end];
                    var attributes = element[.."<form:textarea".Length] + element["<form:textarea".Length..element.IndexOf('>', StringComparison.Ordinal)];
                    data = data[..start] + attributes.Replace("<form:textarea", "<form:text", StringComparison.Ordinal) + " form:current-value=\"a&#10;b\"/>" + data[end..];
                }

                var created = output.CreateEntry(entry.FullName);
                using var writer = new StreamWriter(created.Open());
                writer.Write(data);
            }
        }

        patched.Position = 0;
        var field = DocumentReader.Read(patched, DocumentFormat.Odt).Document.Sections[0].Blocks.OfType<FormField>().Single();
        Require(field.Multiline && field.Value == "a\nb", "A form:text with a line break in the value is a multi-line field.");
    }

    private static void OdtBandsReport()
    {
        using var plain = new MemoryStream();
        var reported = DocumentWriter.WriteAsync(
            Document(new FormField(FormFieldKind.Text, "r") { Required = true }), plain, DocumentFormat.Odt).GetAwaiter().GetResult();
        Require(reported.Warnings.Any(w => w.Message.Contains("'Required' and 'FontSize' of the form field 'r'")), "ODT: Required is reported.");

        var document = new PortableDocument(new DocumentMetadata(), new Section(Text("body")) { Header = new HeaderFooter(new Block[] { new FormField(FormFieldKind.Text, "h") }) });
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Odt).GetAwaiter().GetResult();
        Require(result.Warnings.Any(w => w.Message.Contains("form fields in the header")), "ODT: a form field in a header is reported.");
    }
}
