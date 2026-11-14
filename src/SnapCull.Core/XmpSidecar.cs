using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SnapCull.Core;

public enum CullFlag { None, Pick, Reject }

public enum ColorLabel { None, Red, Yellow, Green, Blue, Purple }

/// <summary>Everything SnapCull records about one photo.</summary>
public readonly record struct CullState(int Stars, CullFlag Flag, ColorLabel Label);

/// <summary>
/// Reads and writes ratings in .xmp sidecar files next to the photo (IMG_0001.CR3 → IMG_0001.xmp),
/// the convention Lightroom, Capture One, Bridge and darktable understand.
/// Existing sidecars are updated in place; anything else in them (edits, keywords) is preserved.
/// </summary>
public static class XmpSidecar
{
    static readonly XNamespace X = "adobe:ns:meta/";
    static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    /// <summary>Our own namespace for the pick flag, which has no standard XMP field.</summary>
    public static readonly XNamespace SnapCullNs = "http://ns.snapcull.app/1.0/";

    public static CullState Read(string xmpPath)
    {
        if (!File.Exists(xmpPath)) return default;
        try
        {
            return Read(XDocument.Load(xmpPath));
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static CullState Read(XDocument doc)
    {
        double rating = 0;
        if (GetValue(doc, Xmp + "Rating") is { } r)
            double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out rating);

        var flag = rating < 0
            ? CullFlag.Reject // Lightroom & darktable store "rejected" as rating -1
            : string.Equals(GetValue(doc, SnapCullNs + "Flag"), "pick", StringComparison.OrdinalIgnoreCase)
                ? CullFlag.Pick
                : CullFlag.None;

        int stars = rating < 0 ? 0 : (int)Math.Clamp(Math.Round(rating), 0, 5);
        var label = Enum.TryParse<ColorLabel>(GetValue(doc, Xmp + "Label"), ignoreCase: true, out var l) ? l : ColorLabel.None;
        return new CullState(stars, flag, label);
    }

    /// <summary>Writes <paramref name="state"/> to the sidecar, creating it if needed. The write is atomic.</summary>
    public static void Write(string xmpPath, CullState state)
    {
        bool existed = File.Exists(xmpPath);
        var doc = existed ? XDocument.Load(xmpPath, LoadOptions.PreserveWhitespace) : CreateEmpty();
        Apply(doc, state);

        var temp = xmpPath + ".tmp";
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = !existed,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using (var writer = XmlWriter.Create(temp, settings))
            doc.Save(writer);
        File.Move(temp, xmpPath, overwrite: true);
    }

    public static void Apply(XDocument doc, CullState state)
    {
        int rating = state.Flag == CullFlag.Reject ? -1 : Math.Clamp(state.Stars, 0, 5);
        SetValue(doc, Xmp + "Rating", rating.ToString(CultureInfo.InvariantCulture), "xmp");
        if (state.Label == ColorLabel.None) RemoveValue(doc, Xmp + "Label");
        else SetValue(doc, Xmp + "Label", state.Label.ToString(), "xmp");
        SetValue(doc, SnapCullNs + "Flag", state.Flag.ToString().ToLowerInvariant(), "snapcull");
    }

    static XDocument CreateEmpty() => new(
        new XElement(X + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", X),
            new XAttribute(X + "xmptk", "SnapCull"),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                new XElement(Rdf + "Description",
                    new XAttribute(Rdf + "about", ""),
                    new XAttribute(XNamespace.Xmlns + "xmp", Xmp),
                    new XAttribute(XNamespace.Xmlns + "snapcull", SnapCullNs)))));

    // XMP allows a property either as an attribute (xmp:Rating="3") or as a child element (<xmp:Rating>3</xmp:Rating>).
    static string? GetValue(XDocument doc, XName name)
    {
        foreach (var d in doc.Descendants(Rdf + "Description"))
        {
            if (d.Attribute(name) is { } a) return a.Value;
            if (d.Element(name) is { } e) return e.Value;
        }
        return null;
    }

    static void SetValue(XDocument doc, XName name, string value, string preferredPrefix)
    {
        foreach (var d in doc.Descendants(Rdf + "Description"))
        {
            if (d.Attribute(name) is { } a) { a.Value = value; return; }
            if (d.Element(name) is { } e) { e.Value = value; return; }
        }

        var description = doc.Descendants(Rdf + "Description").FirstOrDefault();
        if (description is null)
        {
            var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault()
                ?? throw new InvalidDataException("The .xmp file has no rdf:RDF element.");
            description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(description);
        }

        if (description.GetPrefixOfNamespace(name.Namespace) is null)
        {
            var prefix = preferredPrefix;
            for (int i = 1; description.GetNamespaceOfPrefix(prefix) is not null; i++) prefix = preferredPrefix + i;
            description.SetAttributeValue(XNamespace.Xmlns + prefix, name.NamespaceName);
        }
        description.SetAttributeValue(name, value);
    }

    static void RemoveValue(XDocument doc, XName name)
    {
        foreach (var d in doc.Descendants(Rdf + "Description"))
        {
            d.Attribute(name)?.Remove();
            d.Element(name)?.Remove();
        }
    }
}
