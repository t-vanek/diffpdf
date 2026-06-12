using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Validation + recipients handling of the "send diff PDFs by e-mail" dialog. Validation short-circuits
/// before any API call, so a not-connected <see cref="ServerSession"/> is never dereferenced.
/// </summary>
public class SendDiffsDialogViewModelTests
{
    private static readonly ServerSession NoSession = new();

    private static (SendDiffsDialogViewModel Vm, string Dir) Create(string? rememberedRecipients = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "diffpdf-send-" + Guid.NewGuid().ToString("N"));
        var store = new ClientSettingsStore(Path.Combine(dir, "client-settings.json"));
        if (rememberedRecipients is not null) store.SaveSendRecipients(rememberedRecipients);
        return (new SendDiffsDialogViewModel(NoSession, Guid.NewGuid(), "Celá dávka", files: null, store), dir);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("a@x.cz, b@x.cz", new[] { "a@x.cz", "b@x.cz" })]
    [InlineData("a@x.cz; b@x.cz", new[] { "a@x.cz", "b@x.cz" })]
    [InlineData("  a@x.cz   b@x.cz  ", new[] { "a@x.cz", "b@x.cz" })]
    [InlineData("", new string[0])]
    public void ParseRecipients_splits_on_commas_semicolons_and_whitespace(string input, string[] expected) =>
        Assert.Equal(expected, SendDiffsDialogViewModel.ParseRecipients(input));

    [Fact]
    public async Task Send_requires_at_least_one_recipient()
    {
        var (vm, dir) = Create();
        try
        {
            vm.Recipients = "   ";
            await vm.SendCommand.ExecuteAsync(null);

            Assert.Null(vm.Result);
            Assert.Equal("Zadej alespoň jednoho příjemce.", vm.ValidationError);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Send_rejects_an_address_without_at_sign()
    {
        var (vm, dir) = Create();
        try
        {
            vm.Recipients = "tiskar@firma.cz, vratnice";
            await vm.SendCommand.ExecuteAsync(null);

            Assert.Null(vm.Result);
            Assert.Contains("„vratnice“", vm.ValidationError);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Remembered_recipients_prefill_the_dialog()
    {
        var (vm, dir) = Create(rememberedRecipients: "tiskar@firma.cz");
        try { Assert.Equal("tiskar@firma.cz", vm.Recipients); }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void SuccessToast_reports_zip_recipients_and_skipped_files()
    {
        var plain = new SendJobDiffsResponse { AttachedFiles = 1, Recipients = 1, Zipped = false };
        Assert.Equal("E-mail odeslán: 1× diff PDF, 1 příjemce.", SendDiffsDialogViewModel.SuccessToast(plain));

        var zipped = new SendJobDiffsResponse
        {
            AttachedFiles = 12, Recipients = 3, Zipped = true, SkippedFiles = ["bez-diffu.pdf"],
        };
        Assert.Equal(
            "E-mail odeslán: 12× diff PDF v ZIP, 3 příjemci. Bez diff PDF (nepřiloženo): bez-diffu.pdf.",
            SendDiffsDialogViewModel.SuccessToast(zipped));
    }
}
