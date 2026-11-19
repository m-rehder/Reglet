using Reglet;

/// <summary>
/// AF04: the round-trip CI gate. Runs every corpus case and fails on an unexpected package
/// loss (not covered by the manifest allowlist) and on a stale allowlist entry (a listed loss
/// that no longer occurs). The gate is green today; the allowlist documents the known
/// deviations until AF-B/AF-C preserve unknown parts.
/// </summary>
internal static class CorpusChecks
{
    internal static int Run()
    {
        try
        {
            var cases = RoundTripCorpus.Load();
            foreach (var item in cases)
            {
                switch (item.Check)
                {
                    case CorpusCheck.Parts:
                        CheckParts(item);
                        break;
                    case CorpusCheck.Text:
                        CheckText(item);
                        break;
                    case CorpusCheck.Verbatim:
                        CheckVerbatim(item);
                        break;
                    case CorpusCheck.PreserveBlock:
                        CheckPreserveBlock(item);
                        break;
                    case CorpusCheck.PreserveAfterInsert:
                        CheckPreserveAfterInsert(item);
                        break;
                }
            }

            Console.WriteLine($"Round-trip corpus (AF-A): {cases.Count} cases passed against the manifest.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Round-trip corpus (AF-A): " + exception);
            return 1;
        }
    }

    private static void CheckParts(CorpusCase item)
    {
        var rewritten = TestPackages.Write(TestPackages.Read(item.Original, item.Format), item.Format);
        ValidatePackage(item, rewritten);
        var differences = PreservationDiff.Compare(item.Original, rewritten);
        var losses = PreservationDiff.Losses(differences)
            .Select(difference => difference.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unexpected = losses
            .Where(name => !item.AllowedLosses.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (unexpected.Count > 0)
        {
            throw new InvalidOperationException(
                $"{item.Id}: unexpected package loss: {string.Join(", ", unexpected)}");
        }

        var stale = item.AllowedLosses
            .Where(name => !losses.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (stale.Count > 0)
        {
            throw new InvalidOperationException(
                $"{item.Id}: the allowlist lists parts that are no longer lost (remove them): {string.Join(", ", stale)}");
        }
    }

    private static void CheckText(CorpusCase item)
    {
        var reader = new PdfDocumentReader();
        var before = reader.ReadPageTexts(new MemoryStream(item.Original, writable: false));
        var rewritten = TestPackages.Write(TestPackages.Read(item.Original, item.Format), item.Format);
        var after = reader.ReadPageTexts(new MemoryStream(rewritten, writable: false));

        if (!before.SequenceEqual(after, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"{item.Id}: extracted text changed: '{string.Join("\\n", before)}' -> '{string.Join("\\n", after)}'");
        }
    }

    private static void CheckVerbatim(CorpusCase item)
    {
        var rewritten = TestPackages.Write(TestPackages.Read(item.Original, item.Format), item.Format);
        ValidatePackage(item, rewritten);
        if (!item.Original.AsSpan().SequenceEqual(rewritten))
        {
            throw new InvalidOperationException(
                $"{item.Id}: an unchanged document was not written back byte-identically ({item.Original.Length} -> {rewritten.Length} bytes).");
        }
    }

    /// <summary>Replaces one block and checks that the unmodelled content of the others survives (AF06/AF12).</summary>
    private static void CheckPreserveBlock(CorpusCase item)
    {
        var document = TestPackages.Read(item.Original, item.Format);
        var sections = document.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[^1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = document with { Sections = sections };

        var rewritten = TestPackages.Write(modified, item.Format);
        ValidatePackage(item, rewritten);

        var (part, marker) = item.Format == DocumentFormat.Docx
            ? ("word/document.xml", "commentRangeStart")
            : ("content.xml", "office:annotation");
        var xml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, part));
        if (!xml.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{item.Id}: the unmodelled block content was lost ({marker}).");
        }
    }

    /// <summary>Inserts one block and checks that the unmodelled content of the other blocks survives.</summary>
    private static void CheckPreserveAfterInsert(CorpusCase item)
    {
        var document = TestPackages.Read(item.Original, item.Format);
        var sections = document.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks.Insert(Math.Min(1, blocks.Count), new Paragraph(new TextRun("inserted")));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = document with { Sections = sections };

        var rewritten = TestPackages.Write(modified, item.Format);
        ValidatePackage(item, rewritten);

        var (part, marker) = item.Format == DocumentFormat.Docx
            ? ("word/document.xml", "commentRangeStart")
            : ("content.xml", "office:annotation");
        var xml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, part));
        if (!xml.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{item.Id}: the unmodelled block content was lost after an insertion ({marker}).");
        }

        if (!xml.Contains("inserted", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{item.Id}: the inserted block was not written.");
        }
    }

    private static void ValidatePackage(CorpusCase item, byte[] package)
    {
        // Only DOCX is validated here: the injected ODT test part is not listed in the ODF
        // manifest, which the ODT validator (correctly) rejects. Generated ODT is validated by
        // the ODT writer/template suites.
        if (item.Format != DocumentFormat.Docx)
        {
            return;
        }

        var errors = DocxPackageValidator.Validate(package);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{item.Id}: invalid DOCX package: {string.Join("; ", errors)}");
        }
    }
}