using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// File manager view-model logic on the <see cref="AsyncPump"/> UI-thread model with a real
/// <see cref="DiffPdfClient"/> over <see cref="FakeApi"/>: panel listing + reconcile + local filter,
/// the sequential upload queue (conflict → resolver → overwrite retry), the dialog's name validation
/// and the active-panel switching contract.
/// </summary>
public class FileManagerViewModelTests
{
    private static FileItemDto Folder(string path) => new()
    {
        Name = path.Split('/')[^1],
        Path = path,
        Kind = FileItemKind.Folder,
        LastModified = DateTimeOffset.UtcNow,
    };

    private static FileItemDto Pdf(string path, long size = 1024) => new()
    {
        Name = path.Split('/')[^1],
        Path = path,
        Kind = FileItemKind.Pdf,
        SizeBytes = size,
        LastModified = DateTimeOffset.UtcNow,
    };

    private static FileListResponse Listing(string current, string? parent, params FileItemDto[] items) =>
        new() { CurrentPath = current, ParentPath = parent, Items = items };

    private static ServerSession Session(FakeApi api) =>
        new() { Client = new DiffPdfClient(new HttpClient(api) { BaseAddress = new Uri("http://localhost") }) };

    private static ServerFileBackend ServerBackend(FakeApi api) => new(Session(api));

    private static FilePanelViewModel ServerPanel(FakeApi api) => new(ServerBackend(api));

    private static FileManagerViewModel NewManager(FakeApi api, ClientSettingsStore? settings = null) =>
        new(Session(api), null!, settings ?? new ClientSettingsStore(Path.Combine(TempDir(), "client-settings.json")));

    private static FakeApi FilesApi(Func<FileListResponse> listing) => new()
    {
        Custom = request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/files"
                ? listing()
                : null,
    };

    /// <summary>Serves GET /files per requested ?path= (null → 404) and GET /files/search with a canned result.</summary>
    private static FakeApi FilesApiByPath(Func<string, FileListResponse?> listingByPath, FileSearchResponse? search = null) => new()
    {
        Custom = request =>
        {
            if (request.Method != HttpMethod.Get) return null;
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/files/search") return search;
            if (path != "/api/v1/files") return null;
            string query = request.RequestUri.Query;
            string requested = "";
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (pair.StartsWith("path=", StringComparison.Ordinal))
                    requested = Uri.UnescapeDataString(pair["path=".Length..]);
            return listingByPath(requested);
        },
    };

    // ---------------- panel ----------------

    [Fact]
    public void Panel_navigation_clears_filter_but_refresh_keeps_it()
    {
        AsyncPump.Run(async () =>
        {
            var panel = ServerPanel(FilesApiByPath(path => path == ""
                ? Listing("", null, Folder("Alfa"))
                : Listing("Alfa", "", Folder("Alfa/Instance"))));
            await panel.LoadAsync("");
            panel.FilterText = "Alfa";
            await panel.RefreshAsync();
            Assert.Equal("Alfa", panel.FilterText);
            await panel.LoadAsync("Alfa");
            Assert.Equal("", panel.FilterText);
            Assert.Single(panel.ItemsView.Cast<object>());
            panel.FilterText = "no-match";
            Assert.False(panel.IsEmpty);
            Assert.True(panel.IsFilteredEmpty);
        });
    }

    [Fact]
    public void Panel_failed_backend_switch_clears_old_rows_path_and_selection()
    {
        AsyncPump.Run(async () =>
        {
            var panel = ServerPanel(FilesApi(() => Listing("Alfa", "", Folder("Alfa/Instance"))));
            await panel.LoadAsync("Alfa");
            panel.SelectedItem = panel.Items.Single();
            panel.SetSelection([panel.SelectedItem]);
            await panel.SetBackendAsync(new LocalFileBackend(false), Path.Combine(TempDir(), "missing"));
            Assert.False(panel.IsServer);
            Assert.False(panel.HasLoaded);
            Assert.Empty(panel.Items);
            Assert.Empty(panel.SelectedItems);
            Assert.Null(panel.SelectedItem);
            Assert.Equal("", panel.CurrentPath);
            Assert.NotNull(panel.Error);
            Assert.False(panel.IsEmpty);
        });
    }

    [Fact]
    public void Manager_disconnect_invalidates_server_rows_and_reconnect_loads_new_root()
    {
        AsyncPump.Run(async () =>
        {
            var session = Session(FilesApi(() => Listing("", null, Folder("serverA"))));
            var manager = new FileManagerViewModel(session, null!, new ClientSettingsStore(Path.Combine(TempDir(), "settings.json")));
            await manager.ActivateAsync();
            Assert.Equal("serverA", manager.RightPanel.Items.Single().Name);
            session.Disconnect();
            Assert.Empty(manager.RightPanel.Items);
            Assert.False(manager.RightPanel.HasLoaded);
            session.Client = Session(FilesApi(() => Listing("", null, Folder("serverB")))).Client;
            await manager.ActivateAsync();
            Assert.Equal("serverB", manager.RightPanel.Items.Single().Name);
        });
    }

    [Fact]
    public void Queue_server_change_during_move_never_deletes_on_new_server()
    {
        AsyncPump.Run(async () =>
        {
            int newServerCalls = 0, originalDeletes = 0;
            var other = Session(new FakeApi { Custom = _ => { newServerCalls++; return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent); } }).Client;
            var session = new ServerSession();
            session.Client = Session(new FakeApi { Custom = request =>
            {
                if (request.Method == HttpMethod.Delete) originalDeletes++;
                session.Client = other; // switch while the original download is completing
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(PdfContent) };
            } }).Client;
            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            string destination = TempDir();
            queue.Enqueue([new TransferRequest(new ServerFileBackend(session), "doc.pdf", "doc.pdf", null,
                new LocalFileBackend(false), destination, Move: true)]);
            var item = queue.Items.Single();
            while (item.IsRunning) await Task.Delay(10);
            Assert.Equal(TransferState.Failed, item.State);
            Assert.Contains("Připojení", item.Error);
            Assert.Equal(0, newServerCalls);
            Assert.Equal(0, originalDeletes);
            Assert.True(File.Exists(Path.Combine(destination, "doc.pdf"))); // original must be kept after session change
        });
    }

    [Fact]
    public void Queue_waiting_uploads_do_not_follow_a_changed_session()
    {
        AsyncPump.Run(async () =>
        {
            int calls = 0, otherCalls = 0;
            var session = new ServerSession();
            var other = Session(new FakeApi { Custom = _ => { otherCalls++; return UploadOk("doc.pdf"); } }).Client;
            session.Client = Session(new FakeApi { Custom = _ => { calls++; session.Client = other; return UploadOk("first.pdf"); } }).Client;
            var server = new ServerFileBackend(session);
            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            queue.Enqueue([LocalToServer(server, TempPdf("first.pdf"), ""), LocalToServer(server, TempPdf("second.pdf"), "")]);
            var items = queue.Items.ToList();
            while (items.Any(i => i.IsRunning)) await Task.Delay(10);
            Assert.Equal(1, calls);
            Assert.Equal(0, otherCalls);
            Assert.Equal(TransferState.Failed, items[1].State);
        });
    }

    [Fact]
    public void Queue_previous_cleanup_does_not_erase_a_new_failed_batch()
    {
        AsyncPump.Run(async () =>
        {
            var cleanup = new TaskCompletionSource();
            var queue = new TransferQueueViewModel { WaitForCleanupAsync = _ => cleanup.Task };
            queue.Enqueue([LocalToLocal(TempPdf("first.pdf"), TempDir())]);
            var first = queue.Items.Single();
            while (first.IsRunning) await Task.Delay(10);
            queue.Enqueue([LocalToServer(ServerBackend(new FakeApi()), TempPdf("failed.pdf"), "")]);
            var failed = queue.Items.Single();
            while (failed.IsRunning) await Task.Delay(10);
            Assert.Equal(TransferState.Failed, failed.State);
            cleanup.SetResult();
            await Task.Yield();
            Assert.Same(failed, Assert.Single(queue.Items));
            Assert.True(queue.HasItems);
        });
    }

    [Fact]
    public void Panel_load_populates_items_parent_and_status()
    {
        AsyncPump.Run(async () =>
        {
            var api = FilesApi(() => Listing("faktury", "", Folder("faktury/2026"), Pdf("faktury/a.pdf"), Pdf("faktury/b.pdf")));
            var panel = ServerPanel(api);

            await panel.LoadAsync("faktury");

            Assert.Equal("faktury", panel.CurrentPath);
            Assert.Equal("", panel.ParentPath);
            Assert.Equal("faktury", panel.PathInput);
            Assert.Equal(3, panel.Items.Count);
            Assert.True(panel.Items[0].IsFolder); // folders come first from the server; order preserved
            Assert.Contains("1 složka", panel.StatusText);
            Assert.Contains("2 PDF", panel.StatusText);
            Assert.False(panel.IsEmpty);
        });
    }

    [Fact]
    public void Panel_reload_reconciles_in_place_preserving_row_identity()
    {
        AsyncPump.Run(async () =>
        {
            var items = new[] { Pdf("a.pdf"), Pdf("b.pdf") };
            var api = FilesApi(() => Listing("", null, items));
            var panel = ServerPanel(api);

            await panel.LoadAsync("");
            var first = panel.Items[0];

            items = [Pdf("a.pdf", size: 4096), Pdf("c.pdf")]; // a changed, b gone, c new
            await panel.RefreshAsync();

            Assert.Equal(2, panel.Items.Count);
            Assert.Same(first, panel.Items[0]);                  // same row object → selection/scroll survive
            Assert.Equal("4 kB", panel.Items[0].SizeText);       // …but the data refreshed
            Assert.DoesNotContain(panel.Items, i => i.Name == "b.pdf");
        });
    }

    [Fact]
    public void Panel_local_filter_narrows_view_and_status()
    {
        AsyncPump.Run(async () =>
        {
            var api = FilesApi(() => Listing("", null, Pdf("smlouva.pdf"), Pdf("faktura.pdf")));
            var panel = ServerPanel(api);
            await panel.LoadAsync("");

            panel.FilterText = "SMLOU"; // case-insensitive
            Assert.Single(panel.ItemsView.Cast<FileListItemViewModel>());
            Assert.Contains("1 PDF", panel.StatusText);

            panel.FilterText = "";
            Assert.Equal(2, panel.ItemsView.Cast<FileListItemViewModel>().Count());
        });
    }

    [Fact]
    public void Panel_empty_folder_flags_empty_state()
    {
        AsyncPump.Run(async () =>
        {
            var panel = ServerPanel(FilesApi(() => Listing("prazdna", "", [])));
            await panel.LoadAsync("prazdna");
            Assert.True(panel.IsEmpty);
        });
    }

    [Fact]
    public void Panel_unconfigured_server_storage_shows_onboarding_instead_of_error()
    {
        AsyncPump.Run(async () =>
        {
            bool configured = false;
            var api = new FakeApi
            {
                Custom = req => req.RequestUri!.AbsolutePath == "/api/v1/files"
                    ? configured
                        ? Listing("", null, Pdf("a.pdf"))
                        : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("{\"detail\":\"File manager root is not configured\"}",
                                System.Text.Encoding.UTF8, "application/json"),
                        }
                    : null,
            };
            var panel = ServerPanel(api);

            await panel.LoadAsync("");

            Assert.NotNull(panel.BlockedMessage);                  // friendly onboarding…
            Assert.Contains("appsettings.json", panel.BlockedMessage);
            Assert.Null(panel.Error);                              // …not a red error
            Assert.False(panel.HasLoaded);
            Assert.Empty(panel.Items);

            // The admin configures the server, the user hits "Zkusit znovu" — the panel recovers.
            configured = true;
            await panel.RefreshAsync();

            Assert.Null(panel.BlockedMessage);
            Assert.True(panel.HasLoaded);
            Assert.Single(panel.Items);
        });
    }

    // ---------------- storage diagnostics card ----------------

    private static FileManagerStatusResponse Status(
        bool configured = true, string? resolvedFrom = "FileManager:RootPath", string? root = @"D:\diffpdf",
        bool exists = true, bool writable = true, string? error = null) => new()
    {
        Configured = configured,
        ResolvedFrom = configured ? resolvedFrom : null,
        RootPath = configured ? root : null,
        RootExists = configured && exists,
        Writable = configured && exists && writable,
        FreeSpaceBytes = configured ? 100L * 1024 * 1024 * 1024 : null,
        TotalSpaceBytes = configured ? 500L * 1024 * 1024 * 1024 : null,
        MaxUploadSizeMB = 256,
        ValidatePdfMagicBytes = true,
        MaxSearchResults = 500,
        Error = error,
    };

    [Fact]
    public void StorageStatus_maps_states_to_readable_card()
    {
        var vm = new FileStorageStatusViewModel(null!); // Apply() never touches the session

        vm.Apply(Status());
        Assert.Contains("připravené", vm.StateText);
        Assert.Contains(@"D:\diffpdf", vm.RootText);
        Assert.Contains("FileManager:RootPath", vm.RootText);
        Assert.Contains("100 GB", vm.SpaceText);
        Assert.Contains("256 MB", vm.LimitsText);
        Assert.Null(vm.DetailText);

        vm.Apply(Status(configured: false));
        Assert.Contains("není nakonfigurováno", vm.StateText);
        Assert.Null(vm.RootText);
        Assert.Null(vm.SpaceText);

        vm.Apply(Status(exists: false, error: "Root folder is not reachable: ..."));
        Assert.Contains("není dostupná", vm.StateText);
        Assert.NotNull(vm.DetailText);

        vm.Apply(Status(writable: false, error: "Root folder is not writable: ..."));
        Assert.Contains("nelze do ní zapisovat", vm.StateText);
    }

    [Fact]
    public void StorageStatus_UnreadableRoot_ShowsListingError()
    {
        var vm = new FileStorageStatusViewModel(null!);
        vm.Apply(new FileManagerStatusResponse
        {
            Configured = true, RootExists = true, Readable = false, Writable = false,
            Error = "Root folder cannot be listed: access denied",
        });
        Assert.Contains("nelze zobrazit její obsah", vm.StateText);
        Assert.Contains("access denied", vm.DetailText);
        // Existing Status() fixtures omit Readable, as responses from older servers do.
        vm.Apply(Status());
        Assert.Contains("připravené", vm.StateText);
    }

    // ---------------- manager (active panel) ----------------

    [Fact]
    public void Manager_activates_both_panels_and_tab_switches_the_active_one()
    {
        AsyncPump.Run(async () =>
        {
            var api = FilesApi(() => Listing("", null, Pdf("a.pdf")));
            var vm = NewManager(api);

            // Default layout per the spec: the local computer on the left, the server on the right.
            Assert.Equal(BackendKind.Local, vm.LeftPanel.Backend.Kind);
            Assert.Equal(BackendKind.Server, vm.RightPanel.Backend.Kind);

            await vm.ActivateAsync();

            Assert.True(vm.LeftPanel.HasLoaded);
            Assert.True(vm.RightPanel.HasLoaded);
            Assert.NotEmpty(vm.LeftPanel.Items); // the drive list of this machine
            Assert.All(vm.LeftPanel.Items, i => Assert.True(i.IsFolder));
            Assert.Same(vm.LeftPanel, vm.ActivePanel);
            Assert.True(vm.LeftPanel.IsActive);
            Assert.False(vm.RightPanel.IsActive);

            FilePanelViewModel? switchedTo = null;
            vm.PanelSwitchRequested += p => switchedTo = p;
            vm.SwitchPanelCommand.Execute(null);

            Assert.Same(vm.RightPanel, vm.ActivePanel);
            Assert.True(vm.RightPanel.IsActive);
            Assert.False(vm.LeftPanel.IsActive);
            Assert.Same(vm.RightPanel, switchedTo);

            // Click-activation (panel event) also moves the toolbar target…
            vm.LeftPanel.MakeActive();
            Assert.Same(vm.LeftPanel, vm.ActivePanel);
        });
    }

    [Fact]
    public void Manager_restores_saved_panel_layout_and_persists_navigation()
    {
        AsyncPump.Run(async () =>
        {
            string localDir = TempDir(); // an existing local folder the left panel should reopen
            var api = FilesApiByPath(path => path switch
            {
                "slozka" => Listing("slozka", "", Pdf("slozka/a.pdf")),
                _ => Listing(path, null),
            });
            var settings = new ClientSettingsStore(Path.Combine(TempDir(), "client-settings.json"));
            settings.SaveFileManagerPanels(new FileManagerPanelsState(
                new FilePanelState("Local", localDir),
                new FilePanelState("Server", "slozka")));

            var vm = NewManager(api, settings);
            await vm.ActivateAsync();

            Assert.Equal(localDir, vm.LeftPanel.CurrentPath);
            Assert.Equal(BackendKind.Local, vm.LeftPanel.Backend.Kind);
            Assert.Equal("slozka", vm.RightPanel.CurrentPath);
            Assert.Equal(BackendKind.Server, vm.RightPanel.Backend.Kind);

            // Navigating writes the new layout back, so the next start reopens it.
            await vm.RightPanel.LoadAsync("");
            var persisted = settings.LoadFileManagerPanels();
            Assert.NotNull(persisted);
            Assert.Equal("", persisted!.Right!.Path);
            Assert.Equal("Server", persisted.Right.Backend);
            Assert.Equal(localDir, persisted.Left!.Path);
            Assert.Equal("Local", persisted.Left.Backend);
        });
    }

    [Fact]
    public void Manager_restore_falls_back_to_defaults_when_saved_locations_are_gone()
    {
        AsyncPump.Run(async () =>
        {
            string vanished = Path.Combine(Path.GetTempPath(), "diffpdf-tests", "gone-" + Guid.NewGuid().ToString("N"));
            var api = FilesApiByPath(path => path.Length == 0 ? Listing("", null, Pdf("a.pdf")) : null); // saved server folder 404s
            var settings = new ClientSettingsStore(Path.Combine(TempDir(), "client-settings.json"));
            settings.SaveFileManagerPanels(new FileManagerPanelsState(
                new FilePanelState("Local", vanished),
                new FilePanelState("Server", "smazana-slozka")));

            var vm = NewManager(api, settings);
            await vm.ActivateAsync();

            Assert.Equal("", vm.LeftPanel.CurrentPath);  // the drive list — local default
            Assert.True(vm.LeftPanel.HasLoaded);
            Assert.Equal("", vm.RightPanel.CurrentPath); // the server root — server default
            Assert.True(vm.RightPanel.HasLoaded);
            Assert.Null(vm.LeftPanel.Error);             // the fallback is quiet, no error greeting
            Assert.Null(vm.RightPanel.Error);
        });
    }

    // ---------------- transfer queue ----------------

    private const string PdfContent = "%PDF-1.4 test";

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "diffpdf-tests", "ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempPdf(string name, string? dir = null)
    {
        string path = Path.Combine(dir ?? TempDir(), name);
        File.WriteAllText(path, PdfContent);
        return path;
    }

    private static UploadFilesResponse UploadOk(string name) => new()
    {
        Files = [new UploadFileResponse { FileName = name, Uploaded = true, Path = name, SizeBytes = 13 }],
    };

    private static UploadFilesResponse UploadExists(string name) => new()
    {
        Files = [new UploadFileResponse { FileName = name, Uploaded = false, ErrorCode = FileUploadErrorCodes.Exists }],
    };

    /// <summary>A queue item heading from a local file into the fake server's folder (recycle bin off in tests).</summary>
    private static TransferRequest LocalToServer(IFileBackend server, string localPath, string targetDir, bool move = false) =>
        new(new LocalFileBackend(useRecycleBin: false), localPath, Path.GetFileName(localPath), new FileInfo(localPath).Length, server, targetDir, move);

    private static TransferRequest LocalToLocal(string localPath, string targetDir, bool move = false)
    {
        var local = new LocalFileBackend(useRecycleBin: false);
        return new TransferRequest(local, localPath, Path.GetFileName(localPath), new FileInfo(localPath).Length, local, targetDir, move);
    }

    // NOTE: every queue test zeroes CleanupDelay and drains HasItems before returning — a pending
    // self-clear Task.Delay must never outlive the AsyncPump (a late Post would hit a completed pump).

    [Fact]
    public void Queue_uploads_local_files_to_server_and_reports_changed_folders()
    {
        AsyncPump.Run(async () =>
        {
            int uploads = 0;
            var api = new FakeApi
            {
                Custom = req => req.RequestUri!.AbsolutePath == "/api/v1/files/upload"
                    ? UploadOk($"f{++uploads}.pdf")
                    : null,
            };
            var server = ServerBackend(api);
            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            IReadOnlyCollection<(IFileBackend Backend, string Directory)>? changed = null;
            queue.BatchCompleted += c => changed = c;

            queue.Enqueue([LocalToServer(server, TempPdf("one.pdf"), "cil"), LocalToServer(server, TempPdf("two.pdf"), "cil")]);
            var items = queue.Items.ToList(); // capture before the clean batch self-clears
            Assert.Equal(2, items.Count);

            while (items.Any(i => i.IsRunning))
                await Task.Delay(10);

            Assert.Equal(2, uploads);
            Assert.All(items, i => Assert.Equal(TransferState.Done, i.State));
            Assert.NotNull(changed);
            var touched = Assert.Single(changed!);
            Assert.Same(server, touched.Backend);
            Assert.Equal("cil", touched.Directory);

            while (queue.HasItems) // fully successful batch self-clears
                await Task.Delay(10);
        });
    }

    [Fact]
    public void Queue_conflict_asks_resolver_and_retries_with_overwrite()
    {
        AsyncPump.Run(async () =>
        {
            int calls = 0;
            var api = new FakeApi
            {
                Custom = req => req.RequestUri!.AbsolutePath == "/api/v1/files/upload"
                    ? (++calls == 1 ? UploadExists("doc.pdf") : UploadOk("doc.pdf"))
                    : null,
            };
            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            int asked = 0;
            queue.OverwriteResolver = _ => { asked++; return Task.FromResult(OverwriteDecision.Overwrite); };

            queue.Enqueue([LocalToServer(ServerBackend(api), TempPdf("doc.pdf"), "")]);
            var item = queue.Items.Single();

            while (item.IsRunning)
                await Task.Delay(10);

            Assert.Equal(1, asked);
            Assert.Equal(2, calls); // initial attempt + overwrite retry
            Assert.Equal(TransferState.Done, item.State);
            Assert.True(item.Overwrite);

            while (queue.HasItems)
                await Task.Delay(10);
        });
    }

    [Fact]
    public void Queue_conflict_skips_when_resolver_declines()
    {
        AsyncPump.Run(async () =>
        {
            var api = new FakeApi
            {
                Custom = req => req.RequestUri!.AbsolutePath == "/api/v1/files/upload"
                    ? UploadExists("doc.pdf")
                    : null,
            };
            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            queue.OverwriteResolver = _ => Task.FromResult(OverwriteDecision.Skip);

            queue.Enqueue([LocalToServer(ServerBackend(api), TempPdf("doc.pdf"), "")]);
            var item = queue.Items.Single();

            while (item.IsRunning)
                await Task.Delay(10);

            Assert.Equal(TransferState.Skipped, item.State);

            while (queue.HasItems) // skipped counts as clean → the batch self-clears too
                await Task.Delay(10);
        });
    }

    [Fact]
    public void Queue_overwriteAll_settles_remaining_conflicts_without_asking_again()
    {
        AsyncPump.Run(async () =>
        {
            int calls = 0;
            var api = new FakeApi
            {
                // Every file conflicts on its first attempt and succeeds on the overwrite retry:
                // odd call = exists, even call = uploaded.
                Custom = req => req.RequestUri!.AbsolutePath == "/api/v1/files/upload"
                    ? (++calls % 2 == 1 ? UploadExists("doc.pdf") : UploadOk("doc.pdf"))
                    : null,
            };
            var server = ServerBackend(api);
            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            int asked = 0;
            queue.OverwriteResolver = _ => { asked++; return Task.FromResult(OverwriteDecision.OverwriteAll); };

            queue.Enqueue([
                LocalToServer(server, TempPdf("a.pdf"), ""),
                LocalToServer(server, TempPdf("b.pdf"), ""),
                LocalToServer(server, TempPdf("c.pdf"), ""),
            ]);
            var items = queue.Items.ToList();

            while (items.Any(i => i.IsRunning))
                await Task.Delay(10);

            Assert.Equal(1, asked); // one answer settled the whole batch
            Assert.Equal(6, calls); // 3 × (attempt + overwrite retry)
            Assert.All(items, i => Assert.Equal(TransferState.Done, i.State));

            while (queue.HasItems)
                await Task.Delay(10);
        });
    }

    [Fact]
    public void Queue_copies_local_to_local()
    {
        AsyncPump.Run(async () =>
        {
            string sourceDir = TempDir();
            string targetDir = TempDir();
            string source = TempPdf("doc.pdf", sourceDir);

            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            queue.Enqueue([LocalToLocal(source, targetDir)]);
            var item = queue.Items.Single();

            while (item.IsRunning)
                await Task.Delay(10);

            Assert.Equal(TransferState.Done, item.State);
            Assert.True(File.Exists(source)); // copy keeps the original
            Assert.Equal(PdfContent, File.ReadAllText(Path.Combine(targetDir, "doc.pdf")));

            while (queue.HasItems)
                await Task.Delay(10);
        });
    }

    [Fact]
    public void Queue_move_deletes_the_source_and_reports_both_folders()
    {
        AsyncPump.Run(async () =>
        {
            string sourceDir = TempDir();
            string targetDir = TempDir();
            string source = TempPdf("doc.pdf", sourceDir);

            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            IReadOnlyCollection<(IFileBackend Backend, string Directory)>? changed = null;
            queue.BatchCompleted += c => changed = c;

            queue.Enqueue([LocalToLocal(source, targetDir, move: true)]);
            var item = queue.Items.Single();

            while (item.IsRunning)
                await Task.Delay(10);

            Assert.Equal(TransferState.Done, item.State);
            Assert.False(File.Exists(source)); // a move removes the original
            Assert.True(File.Exists(Path.Combine(targetDir, "doc.pdf")));
            Assert.NotNull(changed);
            Assert.Contains(changed!, c => c.Directory.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(changed!, c => c.Directory.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));

            while (queue.HasItems)
                await Task.Delay(10);
        });
    }

    [Fact]
    public void Queue_downloads_server_file_to_local_folder()
    {
        AsyncPump.Run(async () =>
        {
            byte[] payload = System.Text.Encoding.ASCII.GetBytes(PdfContent);
            var api = new FakeApi
            {
                Custom = req => req.RequestUri!.AbsolutePath == "/api/v1/files/download"
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                    : null,
            };
            var server = ServerBackend(api);
            string targetDir = TempDir();

            var queue = new TransferQueueViewModel { CleanupDelay = TimeSpan.Zero };
            queue.Enqueue([new TransferRequest(server, "slozka/doc.pdf", "doc.pdf", payload.Length, new LocalFileBackend(), targetDir, Move: false)]);
            var item = queue.Items.Single();

            while (item.IsRunning)
                await Task.Delay(10);

            Assert.Equal(TransferState.Done, item.State);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(targetDir, "doc.pdf")));

            while (queue.HasItems)
                await Task.Delay(10);
        });
    }

    // ---------------- subtree search ----------------

    [Fact]
    public void Panel_subtree_search_shows_results_and_open_jumps_to_the_file()
    {
        AsyncPump.Run(async () =>
        {
            var api = FilesApiByPath(
                listingByPath: path => path switch
                {
                    "faktury" => Listing("faktury", "", Folder("faktury/2026"), Pdf("faktury/a.pdf")),
                    "faktury/2026" => Listing("faktury/2026", "faktury", Pdf("faktury/2026/smlouva.pdf")),
                    _ => Listing(path, null),
                },
                search: new FileSearchResponse
                {
                    Query = "smlou",
                    SearchPath = "faktury",
                    Items = [Pdf("faktury/2026/smlouva.pdf")],
                    Truncated = false,
                });
            var panel = ServerPanel(api);
            await panel.LoadAsync("faktury");

            panel.FilterText = "smlou";
            await panel.SearchSubtreeCommand.ExecuteAsync(null);

            Assert.True(panel.IsSearchMode);
            Assert.Contains("smlou", panel.SearchInfo);
            var hit = Assert.Single(panel.Items);
            Assert.True(hit.ShowFullPath);
            Assert.Equal("faktury/2026/smlouva.pdf", hit.DisplayText); // results show where they live

            // Enter on a result navigates to its folder, selects it and leaves search mode.
            panel.SelectedItem = hit;
            await panel.OpenSelectedCommand.ExecuteAsync(null);

            Assert.False(panel.IsSearchMode);
            Assert.Null(panel.SearchInfo);
            Assert.Equal("faktury/2026", panel.CurrentPath);
            Assert.Equal("faktury/2026/smlouva.pdf", panel.SelectedItem?.Path);
            Assert.False(Assert.Single(panel.Items).ShowFullPath); // back to plain names
        });
    }

    [Fact]
    public void Panel_exit_search_restores_the_folder_listing_and_clears_filter()
    {
        AsyncPump.Run(async () =>
        {
            var api = FilesApiByPath(
                listingByPath: _ => Listing("slozka", "", Pdf("slozka/a.pdf"), Pdf("slozka/b.pdf")),
                search: new FileSearchResponse { Query = "a", SearchPath = "slozka", Items = [Pdf("slozka/a.pdf")] });
            var panel = ServerPanel(api);
            await panel.LoadAsync("slozka");

            panel.FilterText = "a";
            await panel.SearchSubtreeCommand.ExecuteAsync(null);
            Assert.True(panel.IsSearchMode);

            await panel.ExitSearchCommand.ExecuteAsync(null);

            Assert.False(panel.IsSearchMode);
            Assert.Equal("", panel.FilterText);
            Assert.Equal(2, panel.Items.Count);
        });
    }

    [Fact]
    public void Panel_search_with_empty_query_is_a_noop()
    {
        AsyncPump.Run(async () =>
        {
            var panel = ServerPanel(FilesApi(() => Listing("", null, Pdf("a.pdf"))));
            await panel.LoadAsync("");

            panel.FilterText = "   ";
            await panel.SearchSubtreeCommand.ExecuteAsync(null);

            Assert.False(panel.IsSearchMode);
            Assert.Single(panel.Items);
        });
    }

    // ---------------- dialog validation ----------------

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a:b", false)]
    [InlineData("name.", false)]
    [InlineData("CON", false)]
    [InlineData("Faktury 2026", true)]
    [InlineData("příloha", true)]
    public void CreateFolderDialog_validates_names(string name, bool expectedConfirmed)
    {
        var dialog = FileOperationDialogViewModel.ForCreateFolder();
        dialog.Name = name;
        if (dialog.ConfirmCommand.CanExecute(null))
            dialog.ConfirmCommand.Execute(null);
        Assert.Equal(expectedConfirmed, dialog.Confirmed);
        if (!expectedConfirmed && !string.IsNullOrWhiteSpace(name))
            Assert.NotNull(dialog.ValidationError);
    }

    [Theory]
    [InlineData("novy.pdf", true)]
    [InlineData("novy.PDF", true)]
    [InlineData("novy.txt", false)]
    [InlineData("novy", false)]
    public void RenameDialog_enforces_pdf_extension_for_files(string newName, bool expectedConfirmed)
    {
        var dialog = FileOperationDialogViewModel.ForRename("stary.pdf", isFile: true);
        dialog.Name = newName;
        dialog.ConfirmCommand.Execute(null);
        Assert.Equal(expectedConfirmed, dialog.Confirmed);
    }

    [Fact]
    public void RenameDialog_folder_has_no_extension_rule()
    {
        var dialog = FileOperationDialogViewModel.ForRename("slozka", isFile: false);
        dialog.Name = "jina slozka";
        dialog.ConfirmCommand.Execute(null);
        Assert.True(dialog.Confirmed);
    }

    [Fact]
    public void OverwriteDialog_has_no_name_input_and_confirms()
    {
        var dialog = FileOperationDialogViewModel.ForOverwrite("doc.pdf");
        Assert.False(dialog.ShowNameInput);
        Assert.False(dialog.ShowApplyToAll);
        dialog.ConfirmCommand.Execute(null);
        Assert.True(dialog.Confirmed);
    }

    [Theory]
    [InlineData(true, false, OverwriteDecision.Overwrite)]
    [InlineData(true, true, OverwriteDecision.OverwriteAll)]
    [InlineData(false, false, OverwriteDecision.Skip)]
    [InlineData(false, true, OverwriteDecision.SkipAll)]
    public void OverwriteDialog_maps_answer_and_applyToAll_to_batch_decision(bool confirm, bool applyToAll, OverwriteDecision expected)
    {
        var dialog = FileOperationDialogViewModel.ForOverwrite("doc.pdf", showApplyToAll: true);
        Assert.True(dialog.ShowApplyToAll);
        dialog.ApplyToAll = applyToAll;
        if (confirm) dialog.ConfirmCommand.Execute(null);
        else dialog.CancelCommand.Execute(null);
        Assert.Equal(expected, dialog.Decision);
    }

    // ---------------- formatting ----------------

    [Fact]
    public void Format_bytes_picks_sensible_units()
    {
        Assert.Equal("—", Format.Bytes(null));
        Assert.Equal("500 B", Format.Bytes(500));
        Assert.EndsWith("kB", Format.Bytes(1536));
        Assert.EndsWith("MB", Format.Bytes(3 * 1024 * 1024));
        Assert.EndsWith("GB", Format.Bytes(5L * 1024 * 1024 * 1024));
    }
}
