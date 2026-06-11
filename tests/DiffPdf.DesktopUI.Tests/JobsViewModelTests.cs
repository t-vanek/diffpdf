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

    private static FilePairTaskSummary FileTask(string path, FilePairStatus status, double similarity = 1, int differingPages = 0) => new()
    {
        Id = Guid.NewGuid(), RelativePath = path, Status = "Completed", ResultStatus = status.ToString(),
        Result = new FilePairResult { RelativePath = path, Status = status, Similarity = similarity, DifferingPages = differingPages },
    };

    [Fact]
    public void File_search_and_diff_filter_reload_from_the_server_and_report_the_count()
    {
        AsyncPump.Run(async () =>
        {
            var jobId = Guid.NewGuid();
            // Filtering is server-side now: GET /tasks applies the name search + "jen odlišné" and returns the
            // matching page plus the filtered total (X-Total-Count). FilesView is a plain view over the loaded page
            // (no client predicate), so each filter change reloads page 1 and the VM shows exactly what came back.
            var api = new FakeApi
            {
                Tasks =
                [
                    FileTask("alfa_invoice.pdf", FilePairStatus.Identical),
                    FileTask("beta_invoice.pdf", FilePairStatus.Differs, similarity: 0.8, differingPages: 1),
                    FileTask("gamma_report.pdf", FilePairStatus.Identical),
                ],
            };
            var vm = NewVm(api);
            // Select a job without going through the property setter (which would kick off a fire-and-forget detail
            // load that races these assertions) — we drive the file reload explicitly below.
            VmTest.SetField(vm, "_selectedJob", new JobRowViewModel(new JobSummary
            {
                Id = jobId, BranchKey = "alfa", InstanceKey = "LamaEnergy", Status = JobStatus.Completed, TotalCount = 3,
            }));

            // Reloads page 1 for the current search + "jen odlišné" as the filter-change paths do: fetch the
            // server-filtered page, then recompute the "shown of total" label + empty-state hint (in its settled
            // state — no IsLoadingFiles spinner toggle to model, which is a UI concern, not part of the filtering).
            async Task ReloadAsync()
            {
                await VmTest.InvokeAsync(vm, "LoadFilesPageAsync", jobId, 0, true);
                VmTest.Invoke(vm, "RefreshFilesView");
            }

            // Name search is case-insensitive + substring; with "jen odlišné" off both invoices come back (not the report).
            VmTest.SetField(vm, "_showOnlyDiffering", false);
            VmTest.SetField(vm, "_fileSearch", "INVOICE");
            await ReloadAsync();
            Assert.Equal(new[] { "alfa_invoice.pdf", "beta_invoice.pdf" },
                vm.FilesView.Cast<FilePairLine>().Select(f => f.Name).ToArray());
            Assert.Equal("Zobrazeno 2 z 2", vm.FileCountLabel); // count reflects the filtered total the server reported
            Assert.False(vm.HasNoMatchingFiles);

            // Combined with "jen odlišné" → only the differing invoice survives both server filters.
            VmTest.SetField(vm, "_showOnlyDiffering", true);
            await ReloadAsync();
            Assert.Equal("beta_invoice.pdf", vm.FilesView.Cast<FilePairLine>().Single().Name);

            // A search that matches nothing (files present) → an empty page + the empty-state hint.
            VmTest.SetField(vm, "_showOnlyDiffering", false);
            VmTest.SetField(vm, "_fileSearch", "does-not-exist");
            await ReloadAsync();
            Assert.Empty(vm.FilesView);
            Assert.True(vm.HasNoMatchingFiles);

            // Clearing the search restores the full list and clears the hint.
            VmTest.SetField(vm, "_fileSearch", "");
            await ReloadAsync();
            Assert.Equal(3, vm.FilesView.Count);
            Assert.False(vm.HasNoMatchingFiles);
        });
    }

    [Fact]
    public void Delete_selection_counts_only_deletable_rows_and_unticks_a_row_that_stops_being_deletable()
    {
        AsyncPump.Run(async () =>
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            var api = new FakeApi { Jobs = new[] { Job(a, JobStatus.Completed), Job(b, JobStatus.Running), Job(c, JobStatus.Cancelled) } };
            var vm = NewVm(api);
            await VmTest.InvokeAsync(vm, "LoadJobsAsync");

            var rowA = vm.Jobs.First(r => r.Id == a);
            var rowB = vm.Jobs.First(r => r.Id == b);
            var rowC = vm.Jobs.First(r => r.Id == c);
            Assert.True(rowA.CanDelete);   // Hotovo
            Assert.False(rowB.CanDelete);  // běží — nelze smazat
            Assert.True(rowC.CanDelete);   // Zrušeno

            // Header "select all" ticks only the deletable rows; the count follows.
            vm.AllSelected = true;
            Assert.True(rowA.IsSelected);
            Assert.False(rowB.IsSelected);
            Assert.True(rowC.IsSelected);
            Assert.Equal(2, vm.SelectedDeleteCount);
            Assert.True(vm.CanDeleteSelected);
            Assert.True(vm.AllSelected);

            // A ticked row that stops being deletable (e.g. retry reopened it) unticks itself and leaves the count.
            rowA.Apply(rowA.Job with { Status = JobStatus.Running });
            Assert.False(rowA.IsSelected);
            Assert.Equal(1, vm.SelectedDeleteCount);
        });
    }

    [Fact]
    public void Deleting_rows_calls_the_api_drops_them_from_the_list_and_closes_their_open_detail()
    {
        AsyncPump.Run(async () =>
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var deleted = new List<string>();
            var api = new FakeApi { Jobs = new[] { Job(a, JobStatus.Completed), Job(b, JobStatus.Completed) } };
            api.Custom = req =>
            {
                if (req.Method == HttpMethod.Delete && req.RequestUri!.AbsolutePath.StartsWith("/api/v1/jobs/", StringComparison.Ordinal))
                {
                    deleted.Add(req.RequestUri.AbsolutePath.Split('/').Last());
                    return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
                }
                return null;
            };
            var vm = NewVm(api);
            await VmTest.InvokeAsync(vm, "LoadJobsAsync");
            var rowA = vm.Jobs.First(r => r.Id == a);
            // Select without the property setter (its fire-and-forget detail load would race the assertions).
            VmTest.SetField(vm, "_selectedJob", rowA);

            await VmTest.InvokeAsync(vm, "DeleteRowsAsync", new List<JobRowViewModel> { rowA });

            Assert.Equal(a.ToString(), Assert.Single(deleted)); // DELETE /jobs/{id} went out for the right job
            Assert.DoesNotContain(vm.Jobs, r => r.Id == a);     // row dropped without a reload
            Assert.Contains(vm.Jobs, r => r.Id == b);
            Assert.Null(vm.SelectedJob);                        // the deleted job's open detail closed
        });
    }

    [Fact]
    public void Transient_null_branch_filter_does_not_hit_the_api_with_a_null_key()
    {
        AsyncPump.Run(() =>
        {
            var vm = NewVm(new FakeApi());
            // A ComboBox nulls its bound string while its ItemsSource is repopulated (Clear()). That must NOT
            // reach ListInstancesAsync(null) → Uri.EscapeDataString(null) ("Value cannot be null. stringToEscape").
            vm.FilterBranch = null!;
            vm.FilterInstance = null!;
            Assert.Null(vm.Error);
            return Task.CompletedTask;
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
