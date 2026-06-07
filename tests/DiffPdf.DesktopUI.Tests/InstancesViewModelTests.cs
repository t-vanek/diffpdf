using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Regression tests for the async re-entrancy bugs in <see cref="InstancesViewModel"/>'s detail loaders:
/// (1) overlapping instance loads must not duplicate rows (the "every instance listed twice" bug), and
/// (2) a selection change while a load is in flight must neither apply stale data nor throw a
/// NullReferenceException. Both are driven by snapshotting the selection and rebuilding atomically after the
/// fetch — which only works because Avalonia marshals async continuations onto the single UI thread. The
/// tests model that with a single-threaded <see cref="AsyncPump"/>; without it the loaders' post-await
/// mutations would race the ObservableCollection. A real <see cref="DiffPdfClient"/> runs over a fake,
/// gated <see cref="HttpMessageHandler"/> so request timing is fully controlled.
/// </summary>
public class InstancesViewModelTests
{
    private static readonly Guid AlfaId = Guid.NewGuid();
    private static readonly Guid BetaId = Guid.NewGuid();

    private static Branch Alfa => new() { Id = AlfaId, Key = "alfa", Name = "alfa", Enabled = true };
    private static Branch Beta => new() { Id = BetaId, Key = "beta", Name = "beta", Enabled = true };

    private static Instance Inst(string key) => new()
    {
        Id = Guid.NewGuid(), BranchId = AlfaId, Key = key, Name = key, BasePath = $@"D:\diffpdf\alfa\{key}", Enabled = true,
    };

    // The three instances from the screenshot, in the order the server returns them.
    private static readonly Instance[] AlfaInstances = [Inst("Centropol"), Inst("LamaEnergy"), Inst("Pragoplyn")];

    [Fact]
    public void Overlapping_loads_for_the_same_branch_do_not_duplicate_rows()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi { InstancesByBranch = _ => AlfaInstances, GatedSuffix = "/instances" };
            var vm = NewVm(api);
            SetField(vm, "_selectedBranch", Alfa); // set state without firing the auto-load

            // Two loads for the SAME branch, both suspended at the (gated) fetch — exactly what a branch
            // re-select / refresh / page re-activate triggers. Releasing then lets both finish.
            var first = InvokeLoadInstances(vm);
            var second = InvokeLoadInstances(vm);
            api.Release();
            await Task.WhenAll(first, second);

            // The buggy loader (Clear at top, Add after the await) left [C,L,P,C,L,P]; the fix rebuilds
            // atomically after the fetch, so each instance appears exactly once.
            Assert.Equal(new[] { "Centropol", "LamaEnergy", "Pragoplyn" },
                vm.Instances.Select(r => r.Instance.Key).ToArray());
        });
    }

    [Fact]
    public void Load_for_a_previous_branch_is_discarded_after_the_selection_changed()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                InstancesByBranch = key => key == "alfa" ? AlfaInstances : [],
                GatedSuffix = "/instances",
            };
            var vm = NewVm(api);
            SetField(vm, "_selectedBranch", Alfa);

            var load = InvokeLoadInstances(vm);     // alfa load, suspended at the fetch
            SetField(vm, "_selectedBranch", Beta);  // user switched branch while alfa was loading
            api.Release();
            await load;

            // alfa's rows must NOT be painted under the beta selection (the staleness guard discards them).
            Assert.Empty(vm.Instances);
        });
    }

    [Fact]
    public void Clearing_the_selection_during_a_detail_load_does_not_throw_and_discards_stale_results()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                Readiness = new InstanceReadiness(),
                Stats = new ScopeStatsResponse(),
                Triggers = Array.Empty<TriggerResponse>(),
                GatedSuffix = "/readiness",
            };
            var vm = NewVm(api);
            SetField(vm, "_selectedBranch", Alfa);
            SetField(vm, "_selectedRow", new InstanceRowViewModel(Inst("Centropol")));

            var load = InvokeLoadInstanceDetails(vm);  // suspended at the (gated) readiness fetch
            SetField(vm, "_selectedRow", null);        // user clicked away mid-load — the reported NRE trigger
            api.Release();

            await load;                 // must NOT throw NullReferenceException (the original bug)
            Assert.Null(vm.Readiness);  // stale result discarded — the selection is gone
            Assert.Empty(vm.Stats);
        });
    }

    // ---------------- harness ----------------

    private static InstancesViewModel NewVm(HttpMessageHandler handler)
    {
        // Only ServerSession needs to be real (it hands the VM the typed client over the fake transport).
        // The loaders never touch the dialog service, and reach the SignalR hub only inside a try/catch — so
        // null collaborators are safe here.
        var session = new ServerSession
        {
            Client = new DiffPdfClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }),
        };
        return new InstancesViewModel(session, null!, null!);
    }

    private static void SetField(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static Task InvokeLoadInstances(InstancesViewModel vm) => Invoke(vm, "LoadInstancesAsync");
    private static Task InvokeLoadInstanceDetails(InstancesViewModel vm) => Invoke(vm, "LoadInstanceDetailsAsync");

    private static Task Invoke(InstancesViewModel vm, string method) =>
        (Task)typeof(InstancesViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;

    /// <summary>A fake API transport: serves canned JSON per route and can gate one route on a manual release.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json =
            new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        public Func<string, object?>? InstancesByBranch { get; init; }
        public object? Readiness { get; init; }
        public object? Stats { get; init; }
        public object? Triggers { get; init; }

        /// <summary>Requests whose path ends with this suffix block until <see cref="Release"/> is called.</summary>
        public string? GatedSuffix { get; init; }

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (GatedSuffix is not null && path.EndsWith(GatedSuffix, StringComparison.Ordinal))
                await _gate.Task.ConfigureAwait(false);

            object? payload = path switch
            {
                _ when path.EndsWith("/triggers", StringComparison.Ordinal) => Triggers ?? Array.Empty<TriggerResponse>(),
                _ when path.EndsWith("/readiness", StringComparison.Ordinal) => Readiness,
                _ when path.EndsWith("/stats", StringComparison.Ordinal) => Stats,
                _ when path.EndsWith("/instances", StringComparison.Ordinal) => InstancesByBranch?.Invoke(BranchKey(path)),
                _ => null,
            };
            if (payload is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), Json), Encoding.UTF8, "application/json"),
            };
        }

        // /api/v1/branches/{key}/instances
        private static string BranchKey(string path)
        {
            var parts = path.Split('/');
            var i = Array.IndexOf(parts, "branches");
            return i >= 0 && i + 1 < parts.Length ? Uri.UnescapeDataString(parts[i + 1]) : "";
        }
    }

    /// <summary>
    /// Minimal single-threaded SynchronizationContext message pump (Stephen Toub's pattern). Runs the async
    /// delegate and every continuation it posts on one thread, modelling the Avalonia UI dispatcher so the
    /// loaders' post-await collection rebuilds are serialized (never racing each other).
    /// </summary>
    private sealed class AsyncPump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public static void Run(Func<Task> root)
        {
            var previous = Current;
            var pump = new AsyncPump();
            SetSynchronizationContext(pump);
            try
            {
                var task = root();
                task.ContinueWith(_ => pump._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (cb, state) in pump._queue.GetConsumingEnumerable())
                    cb(state);
                task.GetAwaiter().GetResult(); // surface assertion failures / exceptions
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
