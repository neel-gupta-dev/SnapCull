namespace SnapCull.Core.Tests;

public class XmpSidecarTests
{
    public static TheoryData<int, CullFlag, ColorLabel> States => new()
    {
        { 0, CullFlag.None, ColorLabel.None },
        { 3, CullFlag.Pick, ColorLabel.Green },
        { 5, CullFlag.None, ColorLabel.Red },
        { 0, CullFlag.Reject, ColorLabel.Blue },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Round_trips_through_a_new_sidecar(int stars, CullFlag flag, ColorLabel label)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "IMG_0001.xmp");
        var state = new CullState(stars, flag, label);

        XmpSidecar.Write(path, state);

        Assert.Equal(state, XmpSidecar.Read(path));
    }

    [Fact]
    public void Rejects_are_stored_as_rating_minus_one_like_lightroom()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "IMG_0001.xmp");

        XmpSidecar.Write(path, new CullState(0, CullFlag.Reject, ColorLabel.None));

        Assert.Contains("xmp:Rating=\"-1\"", File.ReadAllText(path));
    }

    [Fact]
    public void Updates_an_existing_lightroom_sidecar_without_losing_its_edits()
    {
        using var dir = new TempDir();
        var path = dir.File("IMG_0001.xmp", """
            <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 7.0">
             <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about=""
                xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
                xmp:Rating="2"
                crs:Exposure2012="+0.50">
               <dc:subject xmlns:dc="http://purl.org/dc/elements/1.1/"><rdf:Bag><rdf:li>sunset</rdf:li></rdf:Bag></dc:subject>
              </rdf:Description>
             </rdf:RDF>
            </x:xmpmeta>
            """u8.ToArray());

        Assert.Equal(new CullState(2, CullFlag.None, ColorLabel.None), XmpSidecar.Read(path));

        var state = new CullState(4, CullFlag.Pick, ColorLabel.Green);
        XmpSidecar.Write(path, state);

        var text = File.ReadAllText(path);
        Assert.Equal(state, XmpSidecar.Read(path));
        Assert.Contains("crs:Exposure2012=\"+0.50\"", text);
        Assert.Contains("<rdf:li>sunset</rdf:li>", text);
        Assert.Contains("xmp:Rating=\"4\"", text);
        Assert.Contains("xmp:Label=\"Green\"", text);
    }

    [Fact]
    public void Handles_properties_written_as_elements()
    {
        using var dir = new TempDir();
        var path = dir.File("IMG_0001.xmp", """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
             <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
               <xmp:Rating>3</xmp:Rating>
               <xmp:Label>Yellow</xmp:Label>
              </rdf:Description>
             </rdf:RDF>
            </x:xmpmeta>
            """u8.ToArray());

        Assert.Equal(new CullState(3, CullFlag.None, ColorLabel.Yellow), XmpSidecar.Read(path));

        XmpSidecar.Write(path, new CullState(5, CullFlag.None, ColorLabel.None));

        var text = File.ReadAllText(path);
        Assert.Contains("<xmp:Rating>5</xmp:Rating>", text);
        Assert.DoesNotContain("xmp:Rating=", text);
        Assert.DoesNotContain("Yellow", text);
    }

    [Fact]
    public void Missing_or_broken_sidecars_read_as_unrated()
    {
        using var dir = new TempDir();
        Assert.Equal(default, XmpSidecar.Read(Path.Combine(dir.Path, "missing.xmp")));
        Assert.Equal(default, XmpSidecar.Read(dir.File("broken.xmp", "<not xml"u8.ToArray())));
    }
}
