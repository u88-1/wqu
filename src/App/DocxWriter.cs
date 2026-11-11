using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GeminiMenu;

/// <summary>
/// Writes a small, clean Word document with no extra libraries (a .docx is a zip of XML files).
/// Right-to-left Hebrew paragraphs, "#"/"##"/"###" lines as real Word headings (so the navigation pane works),
/// review markers such as [?] highlighted, and speaker labels in bold.
/// </summary>
public static class DocxWriter
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    static readonly Regex Control = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]", RegexOptions.Compiled);
    static readonly Regex Speaker = new(@"^(\s*(?:\[\d{1,3}:\d{2}(?::\d{2})?\]\s*)?(?:דובר|Speaker)\s*\d+\s*:)(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Save(string path, string? title, string text, string? note = null)
    {
        var bytes = Build(title, text, note);
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] Build(string? title, string text, string? note = null)
    {
        var body = new XElement(W + "body");
        if (!string.IsNullOrWhiteSpace(title)) body.Add(Paragraph("Title", Runs(title!.Trim(), false)));

        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (BookStyle.IsHeading(line, out var level, out var headingText))
            {
                body.Add(Paragraph("Heading" + Math.Clamp(level, 1, 3), Runs(headingText, false)));
                continue;
            }
            var sp = Speaker.Match(line);
            var runs = new List<XElement>();
            if (sp.Success)
            {
                runs.AddRange(Runs(sp.Groups[1].Value, true));
                runs.AddRange(Runs(sp.Groups[2].Value, false));
            }
            else runs.AddRange(Runs(line, false));
            body.Add(Paragraph(null, runs));
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            body.Add(Paragraph("Note", Runs("הערות עיבוד: " + note!.Trim(), false)));
        }

        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", 11906), new XAttribute(W + "h", 16838)),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", 1440), new XAttribute(W + "right", 1440), new XAttribute(W + "bottom", 1440),
                new XAttribute(W + "left", 1440), new XAttribute(W + "header", 708), new XAttribute(W + "footer", 708), new XAttribute(W + "gutter", 0)),
            new XElement(W + "bidi")));

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), body));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypes());
            AddEntry(zip, "_rels/.rels", RootRels());
            AddEntry(zip, "word/document.xml", document);
            AddEntry(zip, "word/_rels/document.xml.rels", DocumentRels());
            AddEntry(zip, "word/styles.xml", Styles());
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ paragraphs and runs

    static XElement Paragraph(string? style, IEnumerable<XElement> runs)
    {
        var pPr = new XElement(W + "pPr");
        if (style != null) pPr.Add(new XElement(W + "pStyle", new XAttribute(W + "val", style)));
        pPr.Add(new XElement(W + "bidi"));
        var p = new XElement(W + "p", pPr);
        foreach (var r in runs) p.Add(r);
        return p;
    }

    /// <summary>Splits a line at review markers so they can be highlighted.</summary>
    static List<XElement> Runs(string text, bool bold)
    {
        text = Control.Replace(text ?? "", "");
        var list = new List<XElement>();
        int pos = 0;
        foreach (Match m in BookStyle.FlagRegex.Matches(text))
        {
            if (m.Index > pos) list.Add(Run(text[pos..m.Index], bold, false));
            list.Add(Run(m.Value, bold, true));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length || list.Count == 0) list.Add(Run(text[pos..], bold, false));
        return list;
    }

    static XElement Run(string text, bool bold, bool highlight)
    {
        var rPr = new XElement(W + "rPr");
        if (bold) { rPr.Add(new XElement(W + "b")); rPr.Add(new XElement(W + "bCs")); }
        if (highlight) rPr.Add(new XElement(W + "highlight", new XAttribute(W + "val", "yellow")));
        rPr.Add(new XElement(W + "rtl"));
        return new XElement(W + "r", rPr,
            new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
    }

    // ------------------------------------------------------------------ package parts

    static void AddEntry(ZipArchive zip, string name, XDocument doc)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write(doc.Declaration + Environment.NewLine + doc.Root);
    }

    static XDocument ContentTypes() => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(Ct + "Types",
            new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(Ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(Ct + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
            new XElement(Ct + "Override", new XAttribute("PartName", "/word/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"))));

    static XDocument RootRels() => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship", new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "word/document.xml"))));

    static XDocument DocumentRels() => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship", new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                new XAttribute("Target", "styles.xml"))));

    static XElement Fonts(string name) => new(W + "rFonts",
        new XAttribute(W + "ascii", name), new XAttribute(W + "hAnsi", name), new XAttribute(W + "cs", name));

    static XElement HeadingStyle(string id, string name, int level, int halfPoints) =>
        new(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", id),
            new XElement(W + "name", new XAttribute(W + "val", name)),
            new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
            new XElement(W + "next", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"),
            new XElement(W + "pPr",
                new XElement(W + "keepNext"),
                new XElement(W + "spacing", new XAttribute(W + "before", 360), new XAttribute(W + "after", 120)),
                new XElement(W + "outlineLvl", new XAttribute(W + "val", level))),
            new XElement(W + "rPr",
                new XElement(W + "b"), new XElement(W + "bCs"),
                new XElement(W + "sz", new XAttribute(W + "val", halfPoints)),
                new XElement(W + "szCs", new XAttribute(W + "val", halfPoints))));

    static XDocument Styles() => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(W + "styles", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "docDefaults",
                new XElement(W + "rPrDefault", new XElement(W + "rPr",
                    Fonts("David"),
                    new XElement(W + "sz", new XAttribute(W + "val", 26)),
                    new XElement(W + "szCs", new XAttribute(W + "val", 26)),
                    new XElement(W + "lang", new XAttribute(W + "val", "he-IL"), new XAttribute(W + "bidi", "he-IL")))),
                new XElement(W + "pPrDefault", new XElement(W + "pPr",
                    new XElement(W + "spacing", new XAttribute(W + "after", 140), new XAttribute(W + "line", 320), new XAttribute(W + "lineRule", "auto"))))),
            new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "default", "1"), new XAttribute(W + "styleId", "Normal"),
                new XElement(W + "name", new XAttribute(W + "val", "Normal")), new XElement(W + "qFormat")),
            new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", "Title"),
                new XElement(W + "name", new XAttribute(W + "val", "Title")),
                new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
                new XElement(W + "next", new XAttribute(W + "val", "Normal")),
                new XElement(W + "qFormat"),
                new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "after", 280)), new XElement(W + "jc", new XAttribute(W + "val", "center"))),
                new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "bCs"),
                    new XElement(W + "sz", new XAttribute(W + "val", 44)), new XElement(W + "szCs", new XAttribute(W + "val", 44)))),
            HeadingStyle("Heading1", "heading 1", 0, 36),
            HeadingStyle("Heading2", "heading 2", 1, 30),
            HeadingStyle("Heading3", "heading 3", 2, 27),
            new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", "Note"),
                new XElement(W + "name", new XAttribute(W + "val", "Processing note")),
                new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
                new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "before", 360))),
                new XElement(W + "rPr", new XElement(W + "i"), new XElement(W + "iCs"),
                    new XElement(W + "sz", new XAttribute(W + "val", 20)), new XElement(W + "szCs", new XAttribute(W + "val", 20))))));
}
