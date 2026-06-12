using System.Net.Http;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>The shared guarded runner (<c>RunAsync</c>) maps transport-level failures to a clear "server
/// unreachable" message — so a tester knows it's the server, not their input — while leaving other errors intact.</summary>
public class ViewModelBaseTests
{
    private sealed class ProbeViewModel : ViewModelBase
    {
        public Task Run(Func<Task> action) => RunAsync(action);
        public Task Run(Func<Task> action, bool toastOnError) => RunAsync(action, toastOnError);
    }

    private const string Unreachable = "Server není dostupný — zkontroluj, že běží, a ověř adresu (⚙ vpravo nahoře).";

    [Fact]
    public void Connection_failure_maps_to_a_clear_message_and_clears_busy()
    {
        AsyncPump.Run(async () =>
        {
            var vm = new ProbeViewModel();
            await vm.Run(() => throw new HttpRequestException("Connection refused"));
            Assert.Equal(Unreachable, vm.Error);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void Wrapped_socket_failure_is_also_recognised()
    {
        AsyncPump.Run(async () =>
        {
            var vm = new ProbeViewModel();
            var wrapped = new InvalidOperationException("boom", new System.Net.Sockets.SocketException(10061));
            await vm.Run(() => throw wrapped);
            Assert.Equal(Unreachable, vm.Error);
        });
    }

    [Fact]
    public void Non_connection_errors_keep_their_own_message()
    {
        AsyncPump.Run(async () =>
        {
            var vm = new ProbeViewModel();
            await vm.Run(() => throw new InvalidOperationException("Vyber úlohu."));
            Assert.Equal("Vyber úlohu.", vm.Error);
        });
    }

    // Truly unexpected exceptions (not API, not connection, not a guard InvalidOperationException) keep their
    // technical detail but get a Czech framing so they don't read like the app's own text.
    [Fact]
    public void Unexpected_errors_are_prefixed_but_keep_the_detail()
    {
        AsyncPump.Run(async () =>
        {
            var vm = new ProbeViewModel();
            await vm.Run(() => throw new ArgumentException("boom"));
            Assert.Equal("Neočekávaná chyba: boom", vm.Error);
        });
    }

    // A periodic background refresh (toastOnError: false) still records the error for the page, but must NOT
    // raise a toast — otherwise a server outage spams one every few seconds.
    [Fact]
    public void Background_run_records_the_error_but_does_not_toast()
    {
        AsyncPump.Run(async () =>
        {
            var toasts = new ToastService();
            ViewModelBase.ToastSink = toasts;
            try
            {
                var vm = new ProbeViewModel();
                await vm.Run(() => throw new InvalidOperationException("boom"), toastOnError: false);
                Assert.Equal("boom", vm.Error);
                Assert.Empty(toasts.Items);
            }
            finally
            {
                ViewModelBase.ToastSink = null;
            }
        });
    }
}
