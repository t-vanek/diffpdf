namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Sdílené texty UI, které se opakují napříč stránkami — jediné místo pravdy, aby stejná situace
/// nikdy nemluvila dvěma různými hláškami. Stránkově specifické texty zůstávají u svých view-modelů.
/// </summary>
public static class UiText
{
    /// <summary>Akce vyžaduje nejdřív zvolit větev v horním výběru.</summary>
    public const string SelectBranchFirst = "Nejdřív vyber větev.";

    /// <summary>Hromadná akce bez zaškrtnutého řádku. <paramref name="itemAccusative"/> je 4. pád („úlohu“, „větev“, „instanci“).</summary>
    public static string NothingSelected(string itemAccusative) => $"Nevybral jsi žádnou {itemAccusative}.";

    /// <summary>Výsledek hromadné akce, kde část položek selhala (např. 409 ze serveru) — jednotný formát se seznamem důvodů.</summary>
    public static string PartialFailure(string doneVerb, int done, IReadOnlyList<string> failed) =>
        $"{doneVerb} {done}, nešlo dokončit {failed.Count}: {string.Join("; ", failed)}";

    /// <summary>Obalí hodnotu českými uvozovkami — jednotně pro názvy a klíče v hláškách.</summary>
    public static string Quote(string value) => $"„{value}“";
}
