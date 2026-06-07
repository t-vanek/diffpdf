using DiffPdf.Client;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The instance detail's friendly readiness view: plain-language status, folder rows, pairing + bar.</summary>
public class ReadinessViewTests
{
    [Fact]
    public void Ready_instance_maps_to_friendly_status_folders_and_pairing()
    {
        var r = new InstanceReadiness
        {
            Ready = true, Reachable = true,
            OldPdfCount = 654, NewPdfCount = 536,
            Matched = 536, OnlyInOld = 118, OnlyInNew = 0,
            Structure = new InstanceStructureReport
            {
                Ok = true, Reachable = true, BasePath = @"D:\diffpdf\alfa\LamaEnergy",
                Items =
                [
                    new StructureItem { Name = "old", State = StructureItemState.Present, PdfCount = 654 },
                    new StructureItem { Name = "new", State = StructureItemState.Present, PdfCount = 536 },
                    new StructureItem { Name = "reports", State = StructureItemState.Present },
                ],
            },
        };

        var v = ReadinessView.From(r);

        Assert.Equal("✓", v.StatusIcon);
        Assert.Equal("Připraveno k porovnání", v.StatusText);

        // Folders read as counts / OK — no raw "Present (654 pdf)".
        Assert.Equal("654 PDF", v.Folders.Single(f => f.Name == "old").Value);
        Assert.Equal("OK", v.Folders.Single(f => f.Name == "reports").Value);

        // Pairing sentences bake the count in.
        Assert.Contains(v.Pairing, p => p.Text == "536 párů k porovnání");
        Assert.Contains(v.Pairing, p => p.Text == "118 jen ve staré verzi");

        Assert.True(v.HasPairing);
        Assert.Equal(2, v.Bar.Count); // matched + only-old; only-new is 0 so it is dropped
    }

    [Fact]
    public void Unreachable_instance_explains_why_it_is_not_ready()
    {
        var r = new InstanceReadiness
        {
            Ready = false, Reachable = false,
            Structure = new InstanceStructureReport { Ok = false, BasePath = @"\\share\x" },
        };

        var v = ReadinessView.From(r);
        Assert.Equal("✗", v.StatusIcon);
        Assert.Contains("nedostupná", v.StatusText);
        Assert.False(v.HasPairing);
    }
}
