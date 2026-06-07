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
/// </summary>
public sealed class ReadinessView
{
    private const double BarWidth = 300;

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#6FCF73"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#D8A657"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E06C75"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#CCCCCC"));

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
            StatusBrush = Green;
        }
        else
        {
            StatusIcon = "✗";
            if (!r.Reachable) { StatusText = "Nepřipraveno — cesta nedostupná"; StatusBrush = Red; }
            else if (!r.Structure.Ok) { StatusText = "Nepřipraveno — chybí složky"; StatusBrush = Red; }
            else if (r.Matched == 0) { StatusText = "Nepřipraveno — žádné páry k porovnání"; StatusBrush = Amber; }
            else if (!string.IsNullOrWhiteSpace(r.Error)) { StatusText = $"Nepřipraveno — {r.Error}"; StatusBrush = Amber; }
            else { StatusText = "Nepřipraveno"; StatusBrush = Amber; }
        }

        Folders = r.Structure.Items.Select(ToFolderLine).ToList();

        HasPairing = r.OldPdfCount > 0 || r.NewPdfCount > 0;
        Pairing =
        [
            new("✓", $"{r.Matched} párů k porovnání", r.Matched > 0 ? Green : Gray),
            new("⚠", $"{r.OnlyInOld} jen ve staré verzi", r.OnlyInOld > 0 ? Amber : Gray),
            new("•", $"{r.OnlyInNew} jen v nové verzi", r.OnlyInNew > 0 ? Gray : Gray),
        ];

        int total = r.Matched + r.OnlyInOld + r.OnlyInNew;
        Bar = total == 0 ? [] :
        [
            .. Seg(r.Matched, total, Green),
            .. Seg(r.OnlyInOld, total, Amber),
            .. Seg(r.OnlyInNew, total, Gray),
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
            StructureItemState.Missing => Red,
            StructureItemState.WrongType => Amber,
            _ => Green,
        };
        string value = i.State switch
        {
            StructureItemState.Missing => "chybí",
            StructureItemState.WrongType => "není složka",
            _ => i.PdfCount is { } n ? $"{n} PDF" : "OK",
        };
        return new FolderLine(i.Name, value, dot, present ? Normal : dot);
    }
}
