using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DiffPdf.DesktopUI.Controls;

/// <summary>How the ECG should read — mapped from the server's health by the view-model.</summary>
public enum EcgRhythm
{
    /// <summary>Still resolving health: a slow, shallow "acquiring signal" wander.</summary>
    Searching,
    /// <summary>Fully operational: a calm ~75 bpm heartbeat.</summary>
    Normal,
    /// <summary>Degraded: a faster, sharper ~130 bpm "stressed" beat.</summary>
    Elevated,
    /// <summary>Down: a flat line — no pulse.</summary>
    Flatline,
}

/// <summary>
/// A vital-signs ECG, like a bedside patient monitor. The <see cref="Rhythm"/> drives the whole
/// character of the trace — waveform, on-screen rate, scroll speed and amplitude — so it visibly
/// reacts to the server's health: a calm green beat when healthy, a faster amber beat when degraded,
/// a slow muted wander while resolving, and a flat line when down. Colour is supplied by
/// <see cref="LiveBrush"/>/<see cref="FlatBrush"/> (the dashboard binds both to its health accent).
/// Drawn directly (no template) and animated by a render-priority timer that runs only while attached
/// and not flatlined.
/// </summary>
public sealed class EcgMonitor : Control
{
    public static readonly StyledProperty<EcgRhythm> RhythmProperty =
        AvaloniaProperty.Register<EcgMonitor, EcgRhythm>(nameof(Rhythm));

    public static readonly StyledProperty<IBrush?> LiveBrushProperty =
        AvaloniaProperty.Register<EcgMonitor, IBrush?>(nameof(LiveBrush), Brushes.LimeGreen);

    public static readonly StyledProperty<IBrush?> FlatBrushProperty =
        AvaloniaProperty.Register<EcgMonitor, IBrush?>(nameof(FlatBrush), Brushes.Gray);

    static EcgMonitor() =>
        AffectsRender<EcgMonitor>(RhythmProperty, LiveBrushProperty, FlatBrushProperty);

    /// <summary>The rhythm to display (maps from server health).</summary>
    public EcgRhythm Rhythm { get => GetValue(RhythmProperty); set => SetValue(RhythmProperty, value); }

    /// <summary>Trace colour for a live rhythm (beat / wander).</summary>
    public IBrush? LiveBrush { get => GetValue(LiveBrushProperty); set => SetValue(LiveBrushProperty, value); }

    /// <summary>Line colour when flatlined.</summary>
    public IBrush? FlatBrush { get => GetValue(FlatBrushProperty); set => SetValue(FlatBrushProperty, value); }

    private const double FrameSeconds = 1.0 / 30;

    private DispatcherTimer? _timer;
    private double _scroll;   // px scrolled so far (the strip moves right→left)

    // Per-rhythm character. On-screen rate (beats/sec) = speed / beatWidth:
    // Normal ≈ 80/64 ≈ 1.25 Hz (75 bpm); Elevated ≈ 100/46 ≈ 2.2 Hz (130 bpm).
    // Searching uses beatWidth 0 as a sentinel → a sine wander instead of PQRST beats.
    private (double BeatWidth, double Speed, double Amp) Shape => Rhythm switch
    {
        EcgRhythm.Normal => (64, 80, 1.00),
        EcgRhythm.Elevated => (46, 100, 1.12),
        EcgRhythm.Searching => (0, 34, 0.30),
        _ => (64, 0, 0),   // Flatline
    };

    private bool Animated => Rhythm != EcgRhythm.Flatline;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fill the space we're given; fall back to a sensible strip when a dimension is unconstrained.
        double w = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 24 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(FrameSeconds), DispatcherPriority.Render, OnTick);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RhythmProperty)
            UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (_timer is null) return;
        if (Animated)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            InvalidateVisual();   // settle to the flat line
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _scroll = (_scroll + Shape.Speed * FrameSeconds) % 100000;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        double mid = h / 2;

        if (Rhythm == EcgRhythm.Flatline)
        {
            context.DrawLine(new Pen(FlatBrush ?? Brushes.Gray, 1.5), new Point(0, mid), new Point(w, mid));
            return;
        }

        var brush = LiveBrush ?? Brushes.LimeGreen;
        var pen = new Pen(brush, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var (beatWidth, _, ampScale) = Shape;
        double amp = mid * 0.82 * ampScale;

        // Searching = a gentle compound wander (no beats); otherwise tile the PQRST heartbeat.
        Func<double, double> sample = Rhythm == EcgRhythm.Searching
            ? x => 0.6 * Math.Sin((x + _scroll) * 0.050) + 0.3 * Math.Sin((x + _scroll) * 0.017)
            : x => Beat(((x + _scroll) % beatWidth) / beatWidth);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool started = false;
            for (double x = 0; x <= w; x += 0.5)
            {
                var p = new Point(x, mid - sample(x) * amp);
                if (!started) { ctx.BeginFigure(p, false); started = true; }
                else ctx.LineTo(p);
            }
        }
        context.DrawGeometry(null, pen, geometry);

        // Glowing sweep tip at the leading (right) edge.
        context.DrawEllipse(brush, null, new Point(w, mid - sample(w) * amp), 1.9, 1.9);
    }

    /// <summary>One normalised heartbeat over t∈[0,1): small P wave, sharp QRS spike, rounded T wave; flat baseline elsewhere.</summary>
    private static double Beat(double t)
    {
        static double Gauss(double x, double mu, double sigma) => Math.Exp(-((x - mu) * (x - mu)) / (2 * sigma * sigma));
        double y = 0;
        y += 0.13 * Gauss(t, 0.18, 0.022);   // P wave
        y -= 0.12 * Gauss(t, 0.345, 0.010);  // Q
        y += 1.00 * Gauss(t, 0.380, 0.011);  // R spike
        y -= 0.26 * Gauss(t, 0.415, 0.012);  // S
        y += 0.24 * Gauss(t, 0.620, 0.034);  // T wave
        return y;
    }
}
