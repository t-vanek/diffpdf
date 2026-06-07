using DiffPdf.Client;
using DiffPdf.DesktopUI.Services;
using DiffPdf.DesktopUI.ViewModels;

namespace DiffPdf.DesktopUI.Tests;

/// <summary>
/// Validation rules of the create/edit branch form. These short-circuit before any API call, so a
/// not-connected <see cref="ServerSession"/> is never dereferenced.
/// </summary>
public class BranchFormViewModelTests
{
    private static readonly ServerSession NoSession = new(); // validation fails before any Require()/API call

    [Fact]
    public async Task Create_rejects_a_duplicate_key()
    {
        var form = BranchFormViewModel.ForCreate(NoSession, scopeRoot: null, existingKeys: ["alfa"], existingNames: ["Alfa"]);
        form.Key = "alfa";
        form.Name = "Nová";

        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(form.Saved);
        Assert.Contains("klíčem 'alfa'", form.ValidationError);
    }

    [Fact]
    public async Task Create_rejects_a_key_with_invalid_characters()
    {
        var form = BranchFormViewModel.ForCreate(NoSession, scopeRoot: null, existingKeys: [], existingNames: []);
        form.Key = "má mezeru/lomítko";
        form.Name = "Platný název";

        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(form.Saved);
        Assert.Equal(ScopeKey.Rule, form.ValidationError);
    }

    [Fact]
    public async Task Create_requires_both_key_and_name()
    {
        var form = BranchFormViewModel.ForCreate(NoSession, null, [], []);

        await form.SaveCommand.ExecuteAsync(null);
        Assert.False(form.Saved);
        Assert.Equal("Zadej klíč.", form.ValidationError);

        form.Key = "beta";
        await form.SaveCommand.ExecuteAsync(null);
        Assert.False(form.Saved);
        Assert.Equal("Zadej název.", form.ValidationError);
    }

    [Fact]
    public async Task Edit_is_in_edit_mode_and_rejects_another_branchs_name()
    {
        var branch = new Branch { Id = Guid.NewGuid(), Key = "alfa", Name = "Alfa", Enabled = true };
        var form = BranchFormViewModel.ForEdit(NoSession, branch, otherNames: ["Beta"]);

        Assert.True(form.IsEditMode);
        Assert.Equal("alfa", form.Key); // prefilled, and the view keeps it read-only

        form.Name = "Beta"; // collides with a different branch
        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(form.Saved);
        Assert.Contains("názvem 'Beta'", form.ValidationError);
    }
}
