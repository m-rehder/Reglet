using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reglet;

/// <summary>
/// builds a PDF 1.7 from a <see cref="PortableDocument"/>: page tree, resources,
/// fonts and (in H19 ff.) the block layout.
/// </summary>
internal static class PdfDocumentBuilder
{
    public static int Build(
        PortableDocument document,
        Stream output,
        PdfWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        cancellationToken.ThrowIfCancellationRequested();
        DocumentLimits.Enforce(document, options.Limits);

        if (options.PdfA == PdfAConformance.PdfA2b && options.Attachments is { Count: > 0 })
        {
            throw new ArgumentException(
                "Attachments are not allowed in PDF/A-2; use PDF/A-3 instead.", nameof(options));
        }

        if (options.PdfA != PdfAConformance.None && options.Encryption is not null)
        {
            throw new ArgumentException(
                "PDF/A requires an unencrypted document; encryption and PDF/A cannot be combined.", nameof(options));
        }

        if (options.PdfA != PdfAConformance.None && options.IccProfile is { } profile && !PdfAWriter.IsRgbOutputProfile(profile))
        {
            throw new DocumentValidationException(
                "PDF/A needs an ICC printer or monitor profile in RGB as PdfWriteOptions.IccProfile.", nameof(options));
        }

        if (options.Signature is not null && options.Encryption is not null)
        {
            throw new ArgumentException(
                "A signed document cannot be encrypted; the signature covers the plain bytes.", nameof(options));
        }

        // The key is checked before the layout, not after the whole document was written.
        if (options.Signature is { } signing)
        {
            using var key = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(signing.Certificate);
            if (key is null)
            {
                throw new ArgumentException("The signature certificate has no RSA private key.", nameof(options));
            }
        }

        // OF-C03: an internal link whose bookmark is absent or sits in a header/footer is drawn
        // without a working destination; the reader of the PDF cannot jump, so say so. A page
        // reference is reported by the layout, which knows the pages of the bookmarks.
        foreach (var reference in ReferenceScan.Find(document, bandBookmarksAreTargets: true))
        {
            if (reference.Kind is ReferenceKind.Link or ReferenceKind.FieldReference)
            {
                handler.Report($"PDF: {reference.Message}");
            }
        }

        // OF-C03: a bookmark inside a note is recorded at the note's first line, so links and page
        // references to it reach the note. (DOCX and ODT already write a real bookmark there.)

        // The table of contents, the total page count (NUMPAGES) and page references (PAGEREF)
        // need numbers that only a layout pass knows (phase AA, Z04). The scratch passes write
        // into a throwaway stream; the iteration stabilises the layout (placeholder and real
        // numbers differ in width).
        var bands = Bands(document, options).ToList();
        var needsToc = HasTableOfContents(document);
        var needsPageCount = bands.Any(band => ContainsInline<PageCountField>(band.Blocks))
            || document.Sections.Any(section => ContainsInline<PageCountField>(section.Blocks));
        var needsPageRefs = bands.Any(band => ContainsInline<PageRefField>(band.Blocks))
            || document.Sections.Any(section => ContainsInline<PageRefField>(section.Blocks))
            || FigureIndexScan.HasListBlock(document);
        if (needsToc || needsPageCount || needsPageRefs)
        {
            IReadOnlyList<int>? pages = null;
            int? totalPages = null;
            IReadOnlyDictionary<string, int>? bookmarkPages = null;
            for (var pass = 0; pass < 3; pass++)
            {
                var collected = new List<int>();
                var collectedBookmarks = new Dictionary<string, int>(StringComparer.Ordinal);
                int count;
                using (var scratch = new MemoryStream())
                {
                    count = BuildCore(
                        document,
                        scratch,
                        options,
                        new UnsupportedFeatureHandler(handler.Behavior, new List<WriteWarning>()),
                        cancellationToken,
                        new LayoutPass(
                            needsToc ? pages : null,
                            needsToc ? collected : null,
                            needsPageCount ? totalPages ?? 0 : null,
                            needsPageRefs ? bookmarkPages : null,
                            needsPageRefs ? collectedBookmarks : null),
                        final: false);
                }

                var stable = (!needsToc || (pages is not null && pages.SequenceEqual(collected)))
                    && (!needsPageCount || totalPages == count)
                    && (!needsPageRefs || (bookmarkPages is not null && SameBookmarkPages(bookmarkPages, collectedBookmarks)));
                pages = needsToc ? collected : null;
                totalPages = needsPageCount ? count : null;
                bookmarkPages = needsPageRefs ? collectedBookmarks : null;
                if (stable)
                {
                    break;
                }
            }

            return BuildCore(
                document, output, options, handler, cancellationToken,
                new LayoutPass(pages, null, totalPages, bookmarkPages, null),
                final: true);
        }

        return BuildCore(document, output, options, handler, cancellationToken, LayoutPass.None, final: true);
    }

    /// <summary>
    /// Page numbers a previous layout pass determined (table of contents, total page count,
    /// bookmark pages) and the collectors the current pass fills.
    /// </summary>
    private sealed record LayoutPass(
        IReadOnlyList<int>? TocPages,
        List<int>? CollectedTocPages,
        int? TotalPages,
        IReadOnlyDictionary<string, int>? BookmarkPages,
        Dictionary<string, int>? CollectedBookmarkPages)
    {
        public static LayoutPass None { get; } = new(null, null, null, null, null);
    }

    private static bool SameBookmarkPages(IReadOnlyDictionary<string, int> previous, Dictionary<string, int> current)
        => previous.Count == current.Count
            && current.All(entry => previous.TryGetValue(entry.Key, out var page) && page == entry.Value);

    /// <summary>All headers and footers of the sections and the page template.</summary>
    private static IEnumerable<HeaderFooter> Bands(PortableDocument document, PdfWriteOptions options)
        => new[] { options.PageTemplate?.Header, options.PageTemplate?.Footer }
            .Concat(document.Sections.SelectMany(section => new[]
            {
                section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter,
            }))
            .OfType<HeaderFooter>();

    /// <summary>Whether an inline of type <typeparamref name="T"/> occurs in the blocks (recursively).</summary>
    private static bool ContainsInline<T>(IEnumerable<Block> blocks)
        where T : Inline
    {
        foreach (var block in blocks)
        {
            var found = block switch
            {
                Paragraph paragraph => ContainsInline<T>(paragraph.Inlines),
                Heading heading => ContainsInline<T>(heading.Inlines),
                ListBlock list => list.Items.Any(item => ContainsInline<T>(item.Blocks)),
                TableBlock table => table.Rows.Any(row => row.Cells.Any(cell => ContainsInline<T>(cell.Blocks))),
                _ => false,
            };
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsInline<T>(IEnumerable<Inline> inlines)
        where T : Inline
        => inlines.Any(inline => inline switch
        {
            T => true,
            Hyperlink hyperlink => ContainsInline<T>(hyperlink.Inlines),
            _ => false,
        });

    private static bool HasTableOfContents(PortableDocument document)
    {
        foreach (var section in document.Sections)
        {
            if (ContainsTableOfContents(section.Blocks))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTableOfContents(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TableOfContents:
                    return true;
                case ListBlock list when list.Items.Any(item => ContainsTableOfContents(item.Blocks)):
                    return true;
                case TableBlock table when table.Rows.Any(row => row.Cells.Any(cell => ContainsTableOfContents(cell.Blocks))):
                    return true;
            }
        }

        return false;
    }

    private static int BuildCore(
        PortableDocument document,
        Stream output,
        PdfWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken,
        LayoutPass pass,
        bool final)
    {
        var file = new PdfFileWriter(output);

        // BJ02: the signature field and the signature dictionary (with the placeholders).
        PdfSignatureWriter? signature = null;
        if (final && options.Signature is { } signatureOptions)
        {
            signature = new PdfSignatureWriter(file, output, signatureOptions);
        }

        var catalogObject = file.ReserveObject();
        var pagesObject = file.ReserveObject();
        var infoObject = file.ReserveObject();
        var resourcesObject = file.ReserveObject();
        var metadataObject = file.ReserveObject();

        // Phase AD (AD02): encryption with AES-128 (revision 4). The document ID takes part
        // in the key derivation and is written into the trailer; the /Encrypt dictionary
        // itself stays unencrypted.
        PdfStandardSecurity? security = null;
        var encryptObject = -1;
        byte[]? idOriginal = null;
        byte[]? idChanging = null;
        if (options.Encryption is { } encryption)
        {
            idOriginal = RandomNumberGenerator.GetBytes(16);
            idChanging = RandomNumberGenerator.GetBytes(16);
            encryptObject = file.ReserveObject();
            security = PdfStandardSecurity.Create(
                encryption.UserPassword, encryption.OwnerPassword, encryption.Permissions);
            file.EnableEncryption(security, encryptObject);
        }

        var resources = new PdfResourceManager();
        var images = new PdfImageManager(file, resources, options.Limits);

        // provide watermark transparency (S04) as a graphics state (ca/CA).
        string? watermarkExtGState = null;
        if (options.PageTemplate?.Watermark is { } watermark)
        {
            watermarkExtGState = AddWatermarkExtGState(file, resources, "Watermark", watermark.Opacity);
        }

        // OF-B09: a picture watermark is embedded like a normal image and drawn behind the content.
        string? pictureWatermarkExtGState = null;
        PdfPictureWatermark? pictureWatermark = null;
        if (options.PageTemplate?.PictureWatermark is { } picture)
        {
            pictureWatermarkExtGState = AddWatermarkExtGState(file, resources, "PictureWatermark", picture.Opacity);
            pictureWatermark = RegisterPictureWatermark(picture, images, handler);
        }

        var standardFonts = new PdfStandardFontManager(file, resources);
        var fonts = ResolveFonts(options, handler);
        var embeddedFonts = new PdfTrueTypeFontManager(file, resources);
        var fontCatalog = new PdfFontCatalog(
            standardFonts,
            embeddedFonts,
            fonts,
            requireEmbeddedFonts: options.PdfA != PdfAConformance.None,
            defaultFamily: document.Defaults.FontFamily);

        // Headers, footers and page decoration are drawn on every page (a footer twice: measured,
        // then drawn); their warnings are collected here and reported once each.
        var pageWarnings = new List<WriteWarning>();
        var pageHandler = new UnsupportedFeatureHandler(handler.Behavior, pageWarnings);

        var structure = new PdfStructureTree(file);
        var outline = new PdfOutline(file);
        var renderer = new PdfPageRenderer(
            file, resources, fontCatalog, structure, pagesObject, resourcesObject,
            new PdfPageDecoration(options.PageTemplate, watermarkExtGState, pictureWatermarkExtGState, pictureWatermark, pageHandler))
        {
            TotalPages = pass.TotalPages,
            SignatureWidget = signature?.FieldObject,
            Annotations = options.Annotations ?? [],
            WriteAnnotationAppearances = options.PdfA != PdfAConformance.None,
            WriteComments = options.Comments == PdfCommentMode.Annotations,
            Forms = new PdfFormWriter(file, fontCatalog, resourcesObject, handler, options.PdfA != PdfAConformance.None),
        };

        // OF-B07: a raster image embedded in an SVG image and a transparency group of one.
        renderer.RegisterImage = (bytes, mediaType) =>
        {
            var handle = images.GetImage(new ImageBlock(bytes, mediaType));
            return (handle.ResourceName, handle.Width, handle.Height);
        };
        renderer.RegisterForm = renderer.RegisterSvgForm;
        renderer.RegisterMask = renderer.RegisterSvgMask;
        renderer.RegisterAlphaMask = renderer.RegisterSvgAlphaMask;

        var fields = new PdfFieldContext(
            document.Metadata,
            pass.BookmarkPages,
            pass.CollectedBookmarkPages,
            ReferenceScan.BandOnlyBookmarks(document),
            BookmarkScan.Collect(document).Texts)
        {
            Lists = FigureIndexScan.HasListBlock(document) ? PdfListMarks.Collect(document) : null,
        };
        var numbering = new NumberingLookup(document.Numbering);
        var blocks = new PdfBlockRenderer(
            renderer, images, handler, outline, HeadingScan.Collect(document, 6), numbering,
            pass.TocPages, pass.CollectedTocPages, fields, options.Hyphenation,
            applyDefaults: document.SourcePackage is null);

        // Headers and footers are drawn with the full block layout (images, tables, headings,
        // character formatting) by a renderer of their own, so that their state (floating
        // images, list spacing) does not mix with the body.
        var bandBlocks = new PdfBlockRenderer(renderer, images, pageHandler, outline, [], numbering, fields: fields, hyphenation: options.Hyphenation, band: true, applyDefaults: document.SourcePackage is null);
        renderer.BandRenderer = bandContent => bandBlocks.RenderBand(
            bandContent, document.Defaults, document.Styles, document.SourceStyles ?? []);

        for (var sectionIndex = 0; sectionIndex < document.Sections.Count; sectionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = document.Sections[sectionIndex];
            renderer.BeginSection(section);

            // Like Word: a page break that ends a section is absorbed when the next section starts
            // a new page anyway, instead of leaving a blank page.
            var sectionBlocks = section.Blocks;
            if (sectionIndex + 1 < document.Sections.Count
                && sectionBlocks.Count > 0
                && sectionBlocks[^1] is PageBreak
                && renderer.StartsNewPage(document.Sections[sectionIndex + 1]))
            {
                sectionBlocks = sectionBlocks.Take(sectionBlocks.Count - 1).ToList();
            }

            blocks.Render(
                sectionBlocks,
                document.Defaults,
                document.CharacterStyles,
                document.Styles,
                document.SourceStyles ?? [],
                cancellationToken);
            options.Progress?.Report(new WriteProgress("section", sectionIndex + 1, document.Sections.Count));
        }

        blocks.RenderEndnotes();
        blocks.WriteOutlineEntries();

        var pageObjects = renderer.Finish();
        var forms = renderer.Forms!;
        forms.Complete();
        foreach (var message in pageWarnings.Select(warning => warning.Message).Distinct(StringComparer.Ordinal))
        {
            handler.Report(message);
        }

        structure.Write();
        outline.Write();
        int? destinationNamesObject = outline.HasNamedDestinations ? outline.WriteNames() : null;

        int? outputIntentObject = null;
        IReadOnlyList<int> attachmentFiles = [];
        int? attachmentNamesObject = null;
        if (options.PdfA != PdfAConformance.None)
        {
            outputIntentObject = PdfAWriter.WriteOutputIntent(file, options);
        }

        if (options.Attachments is { Count: > 0 } attachments)
        {
            (attachmentFiles, attachmentNamesObject) = PdfAWriter.WriteAttachments(file, attachments);
        }

        file.WriteObject(pagesObject, tokens => tokens.WriteDictionary(pages =>
        {
            pages.WriteName("Type");
            pages.WriteName("Pages");
            pages.WriteName("Kids");
            pages.WriteArray(kids =>
            {
                foreach (var pageObject in pageObjects)
                {
                    kids.WriteReference(pageObject);
                }
            });
            pages.WriteName("Count");
            pages.WriteInteger(pageObjects.Count);
        }));

        file.WriteObject(catalogObject, tokens => tokens.WriteDictionary(catalog =>
        {
            catalog.WriteName("Type");
            catalog.WriteName("Catalog");
            catalog.WriteName("Pages");
            catalog.WriteReference(pagesObject);
            if (security is not null)
            {
                // AES-256 (AESV3) in a PDF 1.7 file: Adobe extension level 8.
                catalog.WriteName("Extensions");
                catalog.WriteDictionary(extensions =>
                {
                    extensions.WriteName("ADBE");
                    extensions.WriteDictionary(adbe =>
                    {
                        adbe.WriteName("BaseVersion");
                        adbe.WriteName("1.7");
                        adbe.WriteName("ExtensionLevel");
                        adbe.WriteInteger(8);
                    });
                });
            }

            catalog.WriteName("StructTreeRoot");
            catalog.WriteReference(structure.RootObject);
            catalog.WriteName("MarkInfo");
            catalog.WriteDictionary(markInfo =>
            {
                markInfo.WriteName("Marked");
                markInfo.WriteBoolean(true);
            });
            catalog.WriteName("ViewerPreferences");
            catalog.WriteDictionary(preferences =>
            {
                preferences.WriteName("DisplayDocTitle");
                preferences.WriteBoolean(true);

                // OF-F02: the window flags of the viewer.
                WriteViewerFlag(preferences, "HideToolbar", options.ViewerPreferences, PdfViewerPreferences.HideToolbar);
                WriteViewerFlag(preferences, "HideMenubar", options.ViewerPreferences, PdfViewerPreferences.HideMenuBar);
                WriteViewerFlag(preferences, "HideWindowUI", options.ViewerPreferences, PdfViewerPreferences.HideWindowUi);
                WriteViewerFlag(preferences, "FitWindow", options.ViewerPreferences, PdfViewerPreferences.FitWindow);
                WriteViewerFlag(preferences, "CenterWindow", options.ViewerPreferences, PdfViewerPreferences.CenterWindow);
                WriteViewerFlag(preferences, "FullScreen", options.ViewerPreferences, PdfViewerPreferences.FullScreen);
            });

            // OF-F02: the page layout and the mode the viewer opens the document in.
            WritePageLayout(catalog, options.PageLayout);
            WritePageMode(catalog, options.PageMode);
            catalog.WriteName("Metadata");
            catalog.WriteReference(metadataObject);

            if (signature is not null || forms.HasFields)
            {
                // BJ02: the signature field of the document; ADR-0024: the form fields.
                catalog.WriteName("AcroForm");
                catalog.WriteDictionary(acroForm =>
                {
                    acroForm.WriteName("Fields");
                    acroForm.WriteArray(fieldObjects =>
                    {
                        if (signature is not null)
                        {
                            fieldObjects.WriteReference(signature.FieldObject);
                        }

                        foreach (var field in forms.Fields)
                        {
                            fieldObjects.WriteReference(field);
                        }
                    });

                    if (forms.HasFields)
                    {
                        // PDF/A needs an embedded font here, so the font of the document is used;
                        // otherwise a viewer that edits a value falls back to Helvetica.
                        acroForm.WriteName("DA");
                        acroForm.WriteLiteralString($"/{forms.DefaultFontResource} 0 Tf 0 g");
                        acroForm.WriteName("DR");
                        if (options.PdfA != PdfAConformance.None)
                        {
                            acroForm.WriteReference(resourcesObject);
                        }
                        else
                        {
                            acroForm.WriteDictionary(resources =>
                            {
                                resources.WriteName("Font");
                                resources.WriteDictionary(fontResources =>
                                {
                                    fontResources.WriteName("Helv");
                                    fontResources.WriteDictionary(helvetica =>
                                    {
                                        helvetica.WriteName("Type");
                                        helvetica.WriteName("Font");
                                        helvetica.WriteName("Subtype");
                                        helvetica.WriteName("Type1");
                                        helvetica.WriteName("BaseFont");
                                        helvetica.WriteName("Helvetica");
                                        helvetica.WriteName("Encoding");
                                        helvetica.WriteName("WinAnsiEncoding");
                                    });
                                });
                            });
                        }
                    }

                    if (signature is not null)
                    {
                        // SignaturesExist | AppendOnly (ISO 32000-1, 12.7.2): viewers keep later
                        // changes as an incremental update instead of rewriting the signed bytes.
                        acroForm.WriteName("SigFlags");
                        acroForm.WriteInteger(3);
                    }
                });

                if (signature is not null && signature.DssObject >= 0)
                {
                    // The PAdES baseline: the document security store with the certificate.
                    catalog.WriteName("DSS");
                    catalog.WriteReference(signature.DssObject);
                }
            }

            if (outputIntentObject is { } outputIntent)
            {
                catalog.WriteName("OutputIntents");
                catalog.WriteArray(intents => intents.WriteReference(outputIntent));
            }

            if (attachmentFiles.Count > 0)
            {
                catalog.WriteName("AF");
                catalog.WriteArray(files =>
                {
                    foreach (var filespec in attachmentFiles)
                    {
                        files.WriteReference(filespec);
                    }
                });
            }

            if (outline.HasOutline)
            {
                catalog.WriteName("Outlines");
                catalog.WriteReference(outline.RootObject);
            }

            if (attachmentNamesObject is not null || destinationNamesObject is not null)
            {
                catalog.WriteName("Names");
                catalog.WriteDictionary(names =>
                {
                    if (destinationNamesObject is { } dests)
                    {
                        names.WriteName("Dests");
                        names.WriteReference(dests);
                    }

                    if (attachmentNamesObject is { } embedded)
                    {
                        names.WriteName("EmbeddedFiles");
                        names.WriteReference(embedded);
                    }
                });
            }

            if (!string.IsNullOrEmpty(document.Metadata.Language))
            {
                catalog.WriteName("Lang");
                catalog.WriteLiteralString(document.Metadata.Language);
            }
        }));

        file.WriteObject(infoObject, tokens => WriteInfo(tokens, document.Metadata));

        WriteXmpMetadata(file, metadataObject, document.Metadata, options.PdfA);

        // The embedded fonts are written last: only now the drawn glyphs are known.
        embeddedFonts.Finish();
        file.WriteObject(resourcesObject, resources.Write);

        if (security is not null)
        {
            file.WriteObject(encryptObject, tokens => WriteEncryptDictionary(tokens, security));
        }

        if (options.PdfA != PdfAConformance.None)
        {
            (idOriginal, idChanging) = PdfAWriter.DocumentId(document.Metadata);
        }

        if (options.Comments == PdfCommentMode.Annotations && CommentScan.Collect(document).Elsewhere > 0)
        {
            handler.Report("PDF: a comment in a header, footer or note is not written.");
        }

        // OF-F01: an annotation without a page would be written nowhere, so say so.
        if (options.Annotations is { Count: > 0 } annotations)
        {
            foreach (var annotation in annotations)
            {
                if (annotation.Page > pageObjects.Count)
                {
                    handler.Report(
                        $"PDF: the annotation on page {annotation.Page} is skipped; the document has {pageObjects.Count} page(s).");
                }
            }
        }

        // BJ02: the signature objects belong to the cross-reference table, so they are
        // written before it.
        signature?.WriteObjects(pageObjects.Count > 0 ? pageObjects[0] : null);

        file.Complete(
            catalogObject,
            infoObject,
            idOriginal,
            idChanging,
            security is null ? null : encryptObject);

        // The signature covers the finished file (without the signature value itself).
        signature?.Sign();
        return pageObjects.Count;
    }

    /// <summary>Writes the <c>/Encrypt</c> dictionary (ISO 32000-2, Table 20/21): R6 with AESV3.</summary>
    private static void WriteEncryptDictionary(PdfTokenWriter tokens, PdfStandardSecurity security)
    {
        tokens.WriteDictionary(encrypt =>
        {
            encrypt.WriteName("Filter");
            encrypt.WriteName("Standard");
            encrypt.WriteName("V");
            encrypt.WriteInteger(5);
            encrypt.WriteName("R");
            encrypt.WriteInteger(6);
            encrypt.WriteName("Length");
            encrypt.WriteInteger(256);
            encrypt.WriteName("CF");
            encrypt.WriteDictionary(cf =>
            {
                cf.WriteName("StdCF");
                cf.WriteDictionary(standard =>
                {
                    standard.WriteName("CFM");
                    standard.WriteName("AESV3");
                    standard.WriteName("Length");
                    standard.WriteInteger(32);
                    standard.WriteName("AuthEvent");
                    standard.WriteName("DocOpen");
                });
            });
            encrypt.WriteName("StmF");
            encrypt.WriteName("StdCF");
            encrypt.WriteName("StrF");
            encrypt.WriteName("StdCF");
            encrypt.WriteName("O");
            encrypt.WriteHexString(security.O);
            encrypt.WriteName("U");
            encrypt.WriteHexString(security.U);
            encrypt.WriteName("OE");
            encrypt.WriteHexString(security.OE);
            encrypt.WriteName("UE");
            encrypt.WriteHexString(security.UE);
            encrypt.WriteName("Perms");
            encrypt.WriteHexString(security.Perms);
            encrypt.WriteName("P");
            encrypt.WriteInteger(security.Permissions);
        });
    }

    internal static void WriteInfo(PdfTokenWriter tokens, DocumentMetadata metadata)
    {
        tokens.WriteDictionary(info =>
        {
            // Same text as the XMP copy, which PDF/A requires to match.
            WriteStringIfPresent(info, "Title", XmpText(metadata.Title));
            WriteStringIfPresent(info, "Author", XmpText(metadata.Author));
            WriteStringIfPresent(info, "Subject", metadata.Subject);

            if (metadata.Keywords is { Count: > 0 } keywords)
            {
                info.WriteName("Keywords");
                info.WriteLiteralString(string.Join(", ", keywords));
            }

            info.WriteName("Producer");
            info.WriteLiteralString("Reglet");

            if (metadata.Created is { } created)
            {
                info.WriteName("CreationDate");
                info.WriteLiteralString(FormatPdfDate(created));
            }

            if (metadata.CustomProperties is { Count: > 0 } customProperties)
            {
                foreach (var (name, value) in customProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (StandardInfoKeys.Contains(name))
                    {
                        throw new DocumentValidationException(
                            $"PDF: the custom property '{name}' uses a reserved Info key.");
                    }

                    info.WriteName(name);
                    info.WriteLiteralString(value);
                }
            }
        });
    }

    /// <summary>The standard keys of the PDF Info dictionary; a custom property must not reuse one.</summary>
    private static readonly HashSet<string> StandardInfoKeys = new(StringComparer.Ordinal)
    {
        "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped",
    };

    /// <summary>PDF date format per ISO 32000-1, 7.9.4: D:YYYYMMDDHHmmSS±HH'mm'.</summary>
    private static string FormatPdfDate(DateTimeOffset value)
    {
        var builder = new StringBuilder("D:");
        builder.Append(value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        var offset = value.Offset;
        if (offset == TimeSpan.Zero)
        {
            builder.Append('Z');
            return builder.ToString();
        }

        builder.Append(offset > TimeSpan.Zero ? '+' : '-');
        var absolute = offset < TimeSpan.Zero ? -offset : offset;
        builder.Append(absolute.Hours.ToString("00", CultureInfo.InvariantCulture));
        builder.Append("'");
        builder.Append(absolute.Minutes.ToString("00", CultureInfo.InvariantCulture));
        builder.Append("'");
        return builder.ToString();
    }

    /// <summary>Writes an XMP metadata package (title, language, author, producer) for PDF/UA (Q02) and PDF/A (R04).</summary>
    private static void WriteXmpMetadata(PdfFileWriter file, int objectNumber, DocumentMetadata metadata, PdfAConformance pdfA)
        => file.WriteStreamObject(objectNumber, BuildXmp(metadata, pdfA), "/Type /Metadata /Subtype /XML");

    /// <summary>Builds an XMP metadata package (phase AF16 reuses it for a metadata edit).</summary>
    internal static byte[] BuildXmp(DocumentMetadata metadata, PdfAConformance pdfA)
    {
        var builder = new StringBuilder();
        builder.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        builder.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        builder.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        builder.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        if (pdfA != PdfAConformance.None)
        {
            builder.Append(" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"");
        }

        builder.Append(">\n");
        builder.Append("<pdf:Producer>Reglet</pdf:Producer>\n");

        if (pdfA != PdfAConformance.None)
        {
            builder.Append("<pdfaid:part>").Append(pdfA == PdfAConformance.PdfA3b ? "3" : "2").Append("</pdfaid:part>\n");
            builder.Append("<pdfaid:conformance>B</pdfaid:conformance>\n");
        }

        if (!string.IsNullOrEmpty(metadata.Title))
        {
            builder.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">")
                .Append(EscapeXml(metadata.Title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        }

        if (!string.IsNullOrEmpty(metadata.Author))
        {
            builder.Append("<dc:creator><rdf:Seq><rdf:li>")
                .Append(EscapeXml(metadata.Author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        }

        if (!string.IsNullOrEmpty(metadata.Language))
        {
            builder.Append("<dc:language><rdf:Bag><rdf:li>")
                .Append(EscapeXml(metadata.Language)).Append("</rdf:li></rdf:Bag></dc:language>\n");
        }

        builder.Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n");
        builder.Append("<?xpacket end=\"w\"?>");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// XMP is XML 1.0, which cannot carry control characters even as references, so they become
    /// U+FFFD; the Info entries mirroring XMP get the same text.
    /// </summary>
    private static string? XmpText(string? value)
        => value is null ? null : XmlText.Clean(value, InvalidXmlCharacterBehavior.Replace);

    private static string EscapeXml(string value)
        => XmpText(value)!.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static void WriteStringIfPresent(PdfTokenWriter tokens, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            tokens.WriteName(name);
            tokens.WriteLiteralString(value);
        }
    }

    /// <summary>
    /// adds a graphics state with the given opacity for a watermark (S04, OF-B09).
    /// </summary>
    private static string AddWatermarkExtGState(
        PdfFileWriter file,
        PdfResourceManager resources,
        string name,
        double opacity)
    {
        var extGStateObject = file.ReserveObject();
        file.WriteObject(extGStateObject, tokens => tokens.WriteDictionary(state =>
        {
            state.WriteName("Type");
            state.WriteName("ExtGState");
            state.WriteName("ca");
            state.WriteNumber(opacity);
            state.WriteName("CA");
            state.WriteNumber(opacity);
        }));
        return resources.AddExtGState(name, extGStateObject);
    }

    /// <summary>
    /// embeds the image of a picture watermark (OF-B09). Only PNG and JPEG are supported,
    /// as in the other places of the writer.
    /// </summary>
    private static PdfPictureWatermark? RegisterPictureWatermark(
        PictureWatermark picture,
        PdfImageManager images,
        UnsupportedFeatureHandler handler)
    {
        if (picture.MediaType == ImageMediaType.Svg)
        {
            handler.Report("PDF: an SVG picture watermark is not supported; use PNG or JPEG.");
            return null;
        }

        var handle = images.GetImage(new ImageBlock(picture.RawData, picture.MediaType));
        return new PdfPictureWatermark(
            handle.ResourceName,
            handle.Width,
            handle.Height,
            picture.Width,
            picture.Height,
            picture.OffsetX?.Points ?? 0,
            picture.OffsetY?.Points ?? 0);
    }

    /// <summary>
    /// writes a flag of the <c>/ViewerPreferences</c> dictionary (OF-F02).
    /// </summary>
    private static void WriteViewerFlag(
        PdfTokenWriter tokens,
        string name,
        PdfViewerPreferences preferences,
        PdfViewerPreferences flag)
    {
        if ((preferences & flag) == flag)
        {
            tokens.WriteName(name);
            tokens.WriteBoolean(true);
        }
    }

    /// <summary>
    /// writes the <c>/PageLayout</c> entry of the catalog (OF-F02).
    /// </summary>
    private static void WritePageLayout(PdfTokenWriter tokens, PdfPageLayout layout)
    {
        var name = layout switch
        {
            PdfPageLayout.SinglePage => "SinglePage",
            PdfPageLayout.OneColumn => "OneColumn",
            PdfPageLayout.TwoColumnLeft => "TwoColumnLeft",
            PdfPageLayout.TwoColumnRight => "TwoColumnRight",
            PdfPageLayout.TwoPageLeft => "TwoPageLeft",
            PdfPageLayout.TwoPageRight => "TwoPageRight",
            _ => null,
        };

        if (name is not null)
        {
            tokens.WriteName("PageLayout");
            tokens.WriteName(name);
        }
    }

    /// <summary>
    /// writes the <c>/PageMode</c> entry of the catalog (OF-F02).
    /// </summary>
    private static void WritePageMode(PdfTokenWriter tokens, PdfPageMode mode)
    {
        var name = mode switch
        {
            PdfPageMode.UseOutlines => "UseOutlines",
            PdfPageMode.UseThumbnails => "UseThumbs",
            PdfPageMode.FullScreen => "FullScreen",
            PdfPageMode.UseOptionalContent => "UseOC",
            PdfPageMode.UseAttachments => "UseAttachments",
            _ => null,
        };

        if (name is not null)
        {
            tokens.WriteName("PageMode");
            tokens.WriteName(name);
        }
    }

    /// <summary>
    /// merges <see cref="PdfWriteOptions.FontCollection"/> and <see cref="PdfWriteOptions.Fonts"/>
    /// together and checks the embedding rights. A font that per OS/2 must not be embedded
    /// is allowed is reported (warning or error depending on <c>UnsupportedContent</c>).
    /// </summary>
    private static FontCollection? ResolveFonts(PdfWriteOptions options, UnsupportedFeatureHandler handler)
    {
        var fonts = options.FontCollection;
        if (options.Fonts is { Count: > 0 } legacy)
        {
            fonts ??= FontCollection.Empty;
            foreach (var (family, data) in legacy.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                ArgumentNullException.ThrowIfNull(data, nameof(options));
                if (fonts.Contains(family))
                {
                    throw new DocumentValidationException(
                        $"The font family '{family}' is specified in both Fonts and FontCollection.", nameof(options));
                }

                // One file per family serves all styles (bold/italic synthetic).
                fonts = fonts.Add(family, FontFace.Load(data), bold: false, italic: false);
            }
        }

        if (fonts is null)
        {
            return null;
        }

        foreach (var family in fonts.Families.Order(StringComparer.Ordinal))
        {
            foreach (var face in fonts.GetFaces(family))
            {
                if (!face.CanEmbed)
                {
                    handler.Report($"PDF: the font '{face.PostScriptName}' does not permit embedding according to its license (OS/2 fsType) and is not used.");
                }
            }
        }

        // A font whose license forbids embedding is not embedded; its text falls back to the
        // standard fonts (or, for PDF/A, is rejected as text without an embedded font).
        return fonts.Where(face => face.CanEmbed);
    }
}
