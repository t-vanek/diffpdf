using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>One folder row (old/new/reports): a coloured state dot + a friendly value ("654 PDF" / "chybí").</summary>
public sealed record FolderLine(string Name, string Value, IBrush Dot, IBrush ValueBrush);

/// <summary>One pairing line ("536 párů k porovnání") — icon + sentence with the count baked in.</summary>
public sealed record PairingLine(string Icon, string Text, IBrush Brush);

/// <summary>One coloured segment of the pairing proportion bar (pixel width against a fixed track).</summary>
public sealed record BarSegment(double Width, IBrush Brush);

/// <summary>
/// Friendly, merged view over <see cref="InstanceReadiness"/> for the instance detail: a plain-language ready
/// status (no raw True/False), the old/new/reports folders with state dots + counts, and the old↔new pairing
/// with a proportion bar. Replaces the "Připraveno: True / Spárováno: 536 / old Present (654 pdf)" dump.
/// Colours come from the shared <see cref="Palette"/>.
/// </summary>
public sealed class ReadinessView
{
    private const double BarWidth = 300;

    public string StatusIcon { get; }
    public string StatusText { get; }
    public IBrush StatusBrush { get; }

    public string BasePath { get; }
    public IReadOnlyList<FolderLine> Folders { get; }

    public bool HasPairing { get; }
    public IReadOnlyList<PairingLine> Pairing { get; }
    public IReadOnlyList<BarSegment> Bar { get; }

    private ReadinessView(InstanceReadiness r)
    {
        BasePath = r.Structure.BasePath;

        if (r.Ready)
        {
            StatusIcon = "✓";
            StatusText = "Připraveno k porovnání";
            StatusBrush = Palette.Good;
        }
        else
        {
            StatusIcon = "✗";
            if (!r.Reachable) { StatusText = "Nepřipraveno — cesta nedostupná"; StatusBrush = Palette.Bad; }
            else if (!r.Structure.Ok) { StatusText = "Nepřipraveno — chybí složky"; StatusBrush = Palette.Bad; }
            else if (r.Matched == 0) { StatusText = "Nepřipraveno — žádné páry k porovnání"; StatusBrush = Palette.Warning; }
            else if (!string.IsNullOrWhiteSpace(r.Error)) { StatusText = $"Nepřipraveno — {r.Error}"; StatusBrush = Palette.Warning; }
            else { StatusText = "Nepřipraveno"; StatusBrush = Palette.Warning; }
        }

        Folders = r.Structure.Items.Select(ToFolderLine).ToList();

        HasPairing = r.OldPdfCount > 0 || r.NewPdfCount > 0;
        Pairing =
        [
            new("✓", $"{r.Matched} párů k porovnání", r.Matched > 0 ? Palette.Good : Palette.Skipped),
            new("⚠", $"{r.OnlyInOld} jen ve staré verzi", r.OnlyInOld > 0 ? Palette.Warning : Palette.Skipped),
            new("•", $"{r.OnlyInNew} jen v nové verzi", Palette.Skipped),
        ];

        int total = r.Matched + r.OnlyInOld + r.OnlyInNew;
        Bar = total == 0 ? [] :
        [
            .. Seg(r.Matched, total, Palette.Good),
            .. Seg(r.OnlyInOld, total, Palette.Warning),
            .. Seg(r.OnlyInNew, total, Palette.Skipped),
        ];
    }

    public static ReadinessView From(InstanceReadiness r) => new(r);

    // A non-empty bucket gets at least 2px so it stays visible even as a sliver.
    private static IEnumerable<BarSegment> Seg(int count, int total, IBrush brush) =>
        count <= 0 ? [] : [new BarSegment(Math.Max(2, count / (double)total * BarWidth), brush)];

    private static FolderLine ToFolderLine(StructureItem i)
    {
        bool present = i.State is not (StructureItemState.Missing or StructureItemState.WrongType);
        IBrush dot = i.State switch
        {
            StructureItemState.Missing => Palette.Bad,
            StructureItemState.WrongType => Palette.Warning,
            _ => Palette.Good,
        };
        string value = i.State switch
        {
            StructureItemState.Missing => "chybí",
            StructureItemState.WrongType => "není složka",
            _ => i.PdfCount is { } n ? $"{n} PDF" : "V pořádku",
        };
        return new FolderLine(i.Name, value, dot, present ? Palette.Text : dot);
    }
}
