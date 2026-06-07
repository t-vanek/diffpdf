using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// The realtime jobs list reconciles in place (the part driven by SignalR <c>jobProgress</c>): a refresh must
/// update rows by id, insert new jobs, drop gone ones, and keep the user's selected row — not rebuild + lose it.
/// </summary>
public class JobsViewModelTests
{
    private static JobSummary Job(Guid id, JobStatus status) => new()
    {
        Id = id, BranchKey = "alfa", InstanceKey = "LamaEnergy",
        Status = status, Progress = status == JobStatus.Running ? 0.5 : 1.0,
    };

    [Fact]
    public void Reloading_reconciles_jobs_in_place_preserving_selection()
    {
        AsyncPump.Run(async () =>
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            var api = new FakeApi { Jobs = new[] { Job(a, JobStatus.Running), Job(b, JobStatus.Completed), Job(c, JobStatus.Queued) } };
            var vm = NewVm(api);

            await VmTest.InvokeAsync(vm, "LoadJobsAsync");
            Assert.Equal(3, vm.Jobs.Count);

            var rowA = vm.Jobs.First(r => r.Id == a);
            vm.SelectedJob = rowA;

            // Server state changes: a finishes, c disappears, new job d shows up.
            Guid d = Guid.NewGuid();
            api.Jobs = new[] { Job(a, JobStatus.Completed), Job(b, JobStatus.Completed), Job(d, JobStatus.Running) };
            await VmTest.InvokeAsync(vm, "LoadJobsAsync");

            Assert.Equal(3, vm.Jobs.Count);
            Assert.Same(rowA, vm.SelectedJob);                          // same row object → selection preserved
            Assert.Equal(JobStatus.Completed, rowA.Job.Status);         // updated in place (not rebuilt)
            Assert.Contains(vm.Jobs, r => r.Id == d);                   // new job inserted
            Assert.DoesNotContain(vm.Jobs, r => r.Id == c);             // gone job dropped
        });
    }

    [Fact]
    public void OnProgress_updates_only_the_selected_job_on_running_ticks()
    {
        AsyncPump.Run(async () =>
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var api = new FakeApi { Jobs = new[] { Job(a, JobStatus.Running), Job(b, JobStatus.Running) } };
            var vm = NewVm(api);
            await VmTest.InvokeAsync(vm, "LoadJobsAsync");

            var rowA = vm.Jobs.First(r => r.Id == a);
            var rowB = vm.Jobs.First(r => r.Id == b);
            vm.SelectedJob = rowA;

            // jobProgress is broadcast to all clients; a running tick for the NON-selected job must not churn its row.
            VmTest.Invoke(vm, "OnProgress", new JobProgress { JobId = b, Status = "Running", Progress = 0.9, ProcessedCount = 9, TotalCount = 10 });
            Assert.Equal(0.5, rowB.Job.Progress, 3);

            // A tick for the selected job updates the row + the live header.
            VmTest.Invoke(vm, "OnProgress", new JobProgress { JobId = a, Status = "Running", Progress = 0.8, ProcessedCount = 8, TotalCount = 10 });
            Assert.Equal(0.8, rowA.Job.Progress, 3);
            Assert.Equal(0.8, vm.LiveProgress, 3);
        });
    }

    // Only ServerSession is real; the hub is constructed but never connected, and the dialog service is unused
    // by LoadJobsAsync / reconcile — so it is safe to leave null here.
    private static JobsViewModel NewVm(HttpMessageHandler handler)
    {
        var session = new ServerSession { Client = new DiffPdfClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }) };
        return new JobsViewModel(session, new JobProgressHubClient(session, new TokenSource(session)), null!);
    }
}
