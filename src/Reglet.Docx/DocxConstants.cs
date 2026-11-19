namespace Reglet;

/// <summary>Fixed names, namespaces, content types and relationship types of the DOCX format.</summary>
internal static class DocxConstants
{
    public const string WordprocessingML =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public const string Relationships =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const string CorePropertiesNamespace =
        "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    public const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    public const string DublinCoreTermsNamespace = "http://purl.org/dc/terms/";
    public const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    public const string ExtendedPropertiesNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

    public const string DrawingWordprocessingNamespace =
        "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public const string DrawingMainNamespace =
        "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string WordprocessingShapeNamespace =
        "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    public const string DrawingPictureNamespace =
        "http://schemas.openxmlformats.org/drawingml/2006/picture";

    /// <summary>The namespace of the SVG extension of a picture (Word 2016 and newer).</summary>
    public const string SvgDrawingNamespace =
        "http://schemas.microsoft.com/office/drawing/2016/SVG/main";

    /// <summary>The URI of the extension list entry that carries an SVG picture.</summary>
    public const string SvgDrawingExtensionUri =
        "{96DAC541-7B7A-43D3-8B79-37D633B846F1}";

    public const string DocumentPart = "word/document.xml";
    public const string CorePropertiesPart = "docProps/core.xml";
    public const string AppPropertiesPart = "docProps/app.xml";
    public const string CustomPropertiesPart = "docProps/custom.xml";
    public const string StylesPart = "word/styles.xml";
    public const string NumberingPart = "word/numbering.xml";
    public const string SettingsPart = "word/settings.xml";
    public const string FootnotesPart = "word/footnotes.xml";
    public const string CommentsPart = "word/comments.xml";
    public const string CommentsExtendedPart = "word/commentsExtended.xml";
    public const string EndnotesPart = "word/endnotes.xml";

    /// <summary>Namespace for <c>w:compatSetting</c> names.</summary>
    public const string WordCompatibilityUri = "http://schemas.microsoft.com/office/word";

    /// <summary>
    /// compatibility level 15 (Word 2013 and newer). Without it, Word opens the document in
    /// compatibility mode and computes layout (e.g. paragraph spacing) by old rules.
    /// </summary>
    public const string CompatibilityMode = "15";

    public const string DocumentContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    public const string StylesContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml";
    public const string NumberingContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml";
    public const string SettingsContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml";
    public const string CorePropertiesContentType =
        "application/vnd.openxmlformats-package.core-properties+xml";
    public const string HeaderContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml";
    public const string FooterContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml";
    public const string AppPropertiesContentType =
        "application/vnd.openxmlformats-officedocument.extended-properties+xml";
    public const string CustomPropertiesContentType =
        "application/vnd.openxmlformats-officedocument.custom-properties+xml";
    public const string CommentsContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml";
    public const string CommentsExtendedContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.commentsExtended+xml";
    public const string FootnotesContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml";
    public const string EndnotesContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml";

    public const string CustomPropertiesNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
    public const string VariantTypesNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

    public const string OfficeDocumentRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string CorePropertiesRelationship =
        "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    public const string AppPropertiesRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties";
    public const string CustomPropertiesRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties";
    public const string StylesRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
    public const string NumberingRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering";
    public const string SettingsRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings";
    public const string ImageRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    public const string HyperlinkRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    public const string HeaderRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/header";
    public const string FooterRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer";
    public const string CommentsRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments";
    public const string CommentsExtendedRelationship =
        "http://schemas.microsoft.com/office/2011/relationships/commentsExtended";
    public const string FootnotesRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes";
    public const string EndnotesRelationship =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/endnotes";

    public const string PngContentType = "image/png";
    public const string JpegContentType = "image/jpeg";
    public const string SvgContentType = "image/svg+xml";

    public const string VbaProjectPart = "word/vbaProject.bin";
    public const string VbaProjectRelationship = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";
    public const string VbaProjectContentType = "application/vnd.ms-office.vbaProject";
    public const string MacroEnabledDocumentContentType = "application/vnd.ms-word.document.macroEnabled.main+xml";

    public const string CustomXmlRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml";
    public const string CustomXmlPropertiesRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXmlProps";
    public const string CustomXmlPropertiesContentType = "application/vnd.openxmlformats-officedocument.customXmlProperties+xml";
    public const string CustomXmlPropertiesNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/customXml";
}
