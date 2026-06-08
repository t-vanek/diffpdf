using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace DiffPdf.DesktopUI.Controls;

/// <summary>
/// Hosts a WebView2 (Edge) control inside Avalonia via <see cref="NativeControlHost"/>. Windows-only — on other
/// platforms, or when the WebView2 runtime is missing, the host stays blank. Set <see cref="Source"/> to a
/// <c>file://</c> URI to render a local PDF in Edge's built-in viewer (zoom / search / print / paging for free).
/// </summary>
public sealed class WebView2Host : NativeControlHost
{
    public static readonly DirectProperty<WebView2Host, string?> SourceProperty =
        AvaloniaProperty.RegisterDirect<WebView2Host, string?>(nameof(Source), o => o.Source, (o, v) => o.Source = v);

    private string? _source;
    private CoreWebView2Controller? _controller;

    /// <summary>The URI the webview navigates to (a <c>file://</c> path to the temp PDF); re-navigates when changed.</summary>
    public string? Source
    {
        get => _source;
        set { if (SetAndRaise(SourceProperty, ref _source, value)) Navigate(); }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (OperatingSystem.IsWindows())
            _ = InitializeAsync(handle.Handle);
        return handle;
    }

    [SupportedOSPlatform("windows")]
    private async Task InitializeAsync(nint hwnd)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffPdf", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            ApplyBounds(Bounds.Width, Bounds.Height);
            Navigate();
        }
        catch
        {
            // WebView2 runtime unavailable / init failed → leave the host blank (best-effort preview).
        }
    }

    private void Navigate()
    {
        if (_controller?.CoreWebView2 is { } core && !string.IsNullOrWhiteSpace(_source))
            core.Navigate(_source);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        ApplyBounds(finalSize.Width, finalSize.Height);
        return arranged;
    }

    // The WebView2 controller does not track the host's size; forward it (DPI-scaled to physical pixels).
    private void ApplyBounds(double width, double height)
    {
        if (_controller is null) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)(width * scale), (int)(height * scale));
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _controller?.Close();
        _controller = null;
        base.DestroyNativeControlCore(control);
    }
}
