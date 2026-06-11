using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Validation rules of the create/edit instance form. These short-circuit before any API call, so a
/// not-connected <see cref="ServerSession"/> is never dereferenced.
/// </summary>
public class InstanceFormViewModelTests
{
    private static readonly ServerSession NoSession = new();

    [Fact]
    public async Task Create_rejects_a_duplicate_key()
    {
        var form = InstanceFormViewModel.ForCreate(NoSession, "alfa", scopeRoot: @"D:\diffpdf",
            existingKeys: ["LamaEnergy"], existingNames: ["LamaEnergy"]);
        form.Key = "LamaEnergy";
        form.Name = "Nová";

        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(form.Saved);
        Assert.Contains("klíčem 'LamaEnergy'", form.ValidationError);
    }

    [Fact]
    public async Task Create_rejects_a_key_with_invalid_characters()
    {
        var form = InstanceFormViewModel.ForCreate(NoSession, "alfa", scopeRoot: @"D:\diffpdf", existingKeys: [], existingNames: []);
        form.Key = "bad key!";
        form.Name = "Platný";

        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(form.Saved);
        Assert.Equal(ScopeKey.Rule, form.ValidationError);
    }

    [Fact]
    public async Task Create_without_a_server_root_requires_a_manual_root()
    {
        // No scope root configured ⇒ the manual-root box is shown and required.
        var form = InstanceFormViewModel.ForCreate(NoSession, "alfa", scopeRoot: null, existingKeys: [], existingNames: []);
        Assert.True(form.RootInputVisible);

        form.Key = "LamaEnergy";
        form.Name = "Lama";
        // Root left empty.
        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(form.Saved);
        Assert.Equal("Zadej kořenovou složku.", form.ValidationError);
    }

    [Fact]
    public void Create_previews_the_derived_base_path_from_root_branch_and_key()
    {
        // A UNC root keeps backslash separators on every OS (the VM joins UNC paths explicitly),
        // so the derived preview is deterministic regardless of the host platform.
        var form = InstanceFormViewModel.ForCreate(NoSession, "alfa", scopeRoot: @"\\fileserver\diffpdf", existingKeys: [], existingNames: []);
        Assert.False(form.RootInputVisible);   // server root present → no manual root box
        form.Key = "LamaEnergy";

        Assert.Equal(@"\\fileserver\diffpdf\alfa\LamaEnergy", form.BasePathPreview);
    }

    [Fact]
    public async Task Edit_is_in_edit_mode_rejects_another_instances_name_and_requires_a_base_path()
    {
        var inst = new Instance { Id = Guid.NewGuid(), Key = "LamaEnergy", Name = "Lama", BasePath = @"D:\diffpdf\alfa\LamaEnergy", Enabled = true };
        var form = InstanceFormViewModel.ForEdit(NoSession, "alfa", inst, otherNames: ["Centropol"]);

        Assert.True(form.IsEditMode);
        Assert.Equal("LamaEnergy", form.Key); // prefilled, kept read-only by the view

        form.Name = "Centropol"; // collides with a different instance
        await form.SaveCommand.ExecuteAsync(null);
        Assert.False(form.Saved);
        Assert.Contains("názvem 'Centropol'", form.ValidationError);

        form.Name = "Lama";
        form.BasePath = "";
        await form.SaveCommand.ExecuteAsync(null);
        Assert.False(form.Saved);
        Assert.Equal("Zadej základní cestu.", form.ValidationError);
    }
}
