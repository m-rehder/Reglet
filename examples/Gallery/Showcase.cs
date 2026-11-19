using Reglet;

namespace Gallery;

/// <summary>Complete documents that combine many features: an invoice, a report and a business letter.</summary>
public static class Showcase
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("showcase", "invoice", "An invoice with letterhead, address, line items, totals, payment QR code and footer.")
        {
            Document = InvoiceDocument,
        };
        yield return new Example("showcase", "report", "A report with title page, contents, chapters, chart, table, formula, footnotes and page numbers.")
        {
            Document = ReportDocument,
        };
        yield return new Example("showcase", "business-letter", "A letter with sender line, address window, date, subject and closing.")
        {
            Document = BusinessLetter,
        };
    }

    private static readonly System.Globalization.CultureInfo En = System.Globalization.CultureInfo.GetCultureInfo("en-GB");

    /// <summary>The invoice; also used by the PDF examples.</summary>
    public static PortableDocument InvoiceDocument()
    {
        var items = new (string Text, int Quantity, decimal Price)[]
        {
            ("Layout of the annual report (40 pages)", 1, 2400m),
            ("Proofreading, per page", 40, 12.5m),
            ("Print-ready PDF/A files", 1, 180m),
            ("Express delivery", 1, 45m),
        };
        var net = items.Sum(item => item.Quantity * item.Price);
        var vat = Math.Round(net * 0.19m, 2);
        string Money(decimal value) => value.ToString("€ #,##0.00", En);

        TableCell Head(string text, TextAlignment alignment = TextAlignment.Left)
            => new(new Paragraph(new TextRun(text) { Bold = true, Color = Color.White })) { Background = Assets.Petrol, Alignment = alignment };
        TableCell Right(string text, bool bold = false)
            => new(new Paragraph(new TextRun(text) { Bold = bold })) { Alignment = TextAlignment.Right };

        var rows = new List<TableRow> { new(Head("Item"), Head("Qty", TextAlignment.Right), Head("Unit price", TextAlignment.Right), Head("Amount", TextAlignment.Right)) { IsHeader = true } };
        rows.AddRange(items.Select(item => new TableRow(new TableCell(item.Text), Right(item.Quantity.ToString(En)), Right(Money(item.Price)), Right(Money(item.Quantity * item.Price)))));
        rows.Add(new TableRow(new TableCell("Net") { ColumnSpan = 3, Alignment = TextAlignment.Right }, Right(Money(net))));
        rows.Add(new TableRow(new TableCell("VAT 19 %") { ColumnSpan = 3, Alignment = TextAlignment.Right }, Right(Money(vat))));
        rows.Add(new TableRow(new TableCell(new Paragraph(new TextRun("Total") { Bold = true })) { ColumnSpan = 3, Alignment = TextAlignment.Right, Background = Assets.Tint }, new TableCell(new Paragraph(new TextRun(Money(net + vat)) { Bold = true })) { Alignment = TextAlignment.Right, Background = Assets.Tint }));

        var small = Length.FromPoints(8);
        var blocks = new Block[]
        {
            new ImageBlock(Assets.GradientPng(240, 60), ImageMediaType.Png) { Width = Length.FromCentimeters(4), AltText = "Logo of Harbour Design" },
            new Paragraph(new TextRun("Harbour Design · Am Hafen 7 · 28217 Bremen") { FontSize = small, Color = Assets.Petrol }),
            new Paragraph(new TextRun("Northline Publishing Ltd")) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromPoints(18), SpaceAfter = Length.Zero } },
            new Paragraph(new TextRun("Attn. Ms Clara Holm")) { Style = ParagraphStyle.Default with { SpaceAfter = Length.Zero } },
            new Paragraph(new TextRun("14 Mill Lane")) { Style = ParagraphStyle.Default with { SpaceAfter = Length.Zero } },
            new Paragraph(new TextRun("Leeds LS1 4AP")),
            new Heading(HeadingLevel.Level1, new TextRun("Invoice 2026-0315")),
            new Paragraph(new TextRun("Invoice date: 15 March 2026 · Customer no. 10482 · Due: 14 April 2026")),
            new TableBlock(rows)
            {
                ColumnWidths = [Length.FromCentimeters(9), Length.FromCentimeters(1.6), Length.FromCentimeters(3), Length.FromCentimeters(3)],
            },
            new Paragraph(new TextRun("Please transfer the total to the account below, quoting the invoice number. Scan the code with your banking app:")) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromPoints(12) } },
            Barcode.Create(BarcodeSymbology.QrCode, "BCD\n002\n1\nSCT\nBREMDE33\nHarbour Design\nDE02290500000001234567\nEUR" + (net + vat).ToString("0.00", En) + "\n\n\nInvoice 2026-0315", moduleWidth: Length.FromMillimeters(0.6)),
        };

        var footer = new HeaderFooter(new Paragraph(new TextRun("Harbour Design · VAT ID DE123456789 · IBAN DE02 2905 0000 0001 2345 67 · BIC BREMDE33") { FontSize = small })
        {
            Style = ParagraphStyle.Default with { Alignment = TextAlignment.Center },
        });
        return new PortableDocument(new DocumentMetadata { Title = "Invoice 2026-0315", Author = "Harbour Design" }, new Section(blocks) { Footer = footer })
        {
            Defaults = DocumentDefaults.Default with { FontFamily = "Arial", FontSize = Length.FromPoints(10) },
        };
    }

    /// <summary>The report; also used by the conversion and PDF examples.</summary>
    public static PortableDocument ReportDocument()
    {
        var blocks = new List<Block>
        {
            new Paragraph(new TextRun("Visitor survey 2026") { FontSize = Length.FromPoints(30), Color = Assets.Petrol, Bold = true }) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromCentimeters(6) } },
            new Paragraph(new TextRun("City library · Results of the spring survey") { FontSize = Length.FromPoints(14) }),
            new PageBreak(),
            new TableOfContents { Title = "Contents", MaxLevel = 2 },
            new PageBreak(),
            new Heading(HeadingLevel.Level1, new TextRun("Summary")),
            new Paragraph(new Inline[]
            {
                new TextRun("Visits rose by 17 per cent"), new Footnote(new Paragraph(new TextRun("Compared with spring 2025; counted at the entrance gates."))),
                new TextRun(". Most visitors come for the reading rooms; the new opening hours on Saturday are used well."),
            }),
            new Heading(HeadingLevel.Level1, new TextRun("Results")),
            new Heading(HeadingLevel.Level2, new TextRun("Visits per month")),
            new Chart(ChartKind.Column,
                [new ChartSeries("2025", [12, 15, 14, 18, 21, 24]) { Color = Assets.Tint }, new ChartSeries("2026", [14, 17, 19, 22, 26, 29]) { Color = Assets.Petrol }],
                ["Jan", "Feb", "Mar", "Apr", "May", "Jun"])
            { Title = "Visits (thousands)", AltText = "Visits per month in 2025 and 2026" },
            new Heading(HeadingLevel.Level2, new TextRun("Reasons for the visit")),
            new TableBlock([
                new TableRow(new TableCell("Reason"), new TableCell("Share")) { IsHeader = true },
                new TableRow(new TableCell("Reading rooms"), new TableCell("41 %") { Alignment = TextAlignment.Right }),
                new TableRow(new TableCell("Borrowing"), new TableCell("33 %") { Alignment = TextAlignment.Right }),
                new TableRow(new TableCell("Events"), new TableCell("15 %") { Alignment = TextAlignment.Right }),
                new TableRow(new TableCell("Other"), new TableCell("11 %") { Alignment = TextAlignment.Right }),
            ]) { ColumnWidths = [Length.FromCentimeters(8), Length.FromCentimeters(3)] },
            new Heading(HeadingLevel.Level1, new TextRun("Method")),
            new Paragraph(new TextRun("The margin of error of a share p from n answers is about")),
            new Equation(new MathRow([new MathText("e"), new MathText("="), new MathText("1.96"), new MathRadical(new MathFraction(new MathRow([new MathText("p"), new MathDelimiter('(', ')', [new MathRow([new MathText("1"), new MathText("−"), new MathText("p")])])]), new MathText("n")))])) { AltText = "Margin of error formula" },
            new Paragraph(new TextRun(Words.Paragraph(21, 120))),
        };

        var section = new Section(blocks)
        {
            // The title page carries neither header nor footer.
            FirstHeader = new HeaderFooter(new Paragraph(new TextRun(""))),
            FirstFooter = new HeaderFooter(new Paragraph(new TextRun(""))),
            Header = new HeaderFooter(new Paragraph(new TextRun("Visitor survey 2026") { Color = Assets.Petrol, FontSize = Length.FromPoints(9) })),
            Footer = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() }) { Style = ParagraphStyle.Default with { Alignment = TextAlignment.Right } }),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Visitor survey 2026", Author = "City library" }, section);
    }

    private static PortableDocument BusinessLetter()
    {
        static Paragraph Line(string text, bool tight = true)
            => new(new TextRun(text)) { Style = ParagraphStyle.Default with { SpaceAfter = tight ? Length.Zero : null } };

        var blocks = new Block[]
        {
            new Paragraph(new TextRun("Harbour Design · Am Hafen 7 · 28217 Bremen") { FontSize = Length.FromPoints(7), Underline = true }),
            Line("Ms Clara Holm"), Line("Northline Publishing Ltd"), Line("14 Mill Lane"), Line("Leeds LS1 4AP", tight: false),
            new Paragraph(new Inline[] { new TextRun("Bremen, "), new DateField(new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero)) })
            {
                Style = ParagraphStyle.Default with { Alignment = TextAlignment.Right, SpaceBefore = Length.FromPoints(24) },
            },
            new Paragraph(new TextRun("Your annual report: proofs ready") { Bold = true }) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromPoints(24) } },
            new Paragraph(new TextRun("Dear Ms Holm,")),
            new Paragraph(new TextRun("the proofs of your annual report are ready. We have set all 40 pages in the new house style and checked every table against your figures. You will find the PDF in your project folder.")),
            new Paragraph(new TextRun("If you send us your corrections by Friday, the printer can start on Monday and deliver within a week.")),
            new Paragraph(new TextRun("Kind regards")) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromPoints(12), SpaceAfter = Length.FromPoints(30) } },
            Line("Jonas Meyer"), Line("Harbour Design"),
        };
        var section = new Section(blocks) { Margins = new PageMargins(Length.FromCentimeters(2.5), Length.FromCentimeters(2), Length.FromCentimeters(4.5), Length.FromCentimeters(2)) };
        return new PortableDocument(new DocumentMetadata { Title = "Letter to Northline Publishing", Author = "Harbour Design" }, section)
        {
            Defaults = DocumentDefaults.Default with { FontFamily = "Arial", FontSize = Length.FromPoints(11) },
        };
    }
}
