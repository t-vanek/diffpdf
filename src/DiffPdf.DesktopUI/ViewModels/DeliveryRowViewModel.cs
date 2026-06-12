using Avalonia.Media;
using DiffPdf.Client;

namespace DiffPdf.DesktopUI.ViewModels;

/// <summary>
/// Jeden řádek historie doručení notifikací (outbox): česká podoba události a stavu, příjemci a poslední
/// chyba. „Poslat znovu" je nabídnuto jen pro řádky, které selhaly nebo skončily jako nedoručené.
/// </summary>
public sealed class DeliveryRowViewModel(NotificationDeliveryResponse delivery)
{
    public NotificationDeliveryResponse Delivery { get; } = delivery;

    public Guid Id => Delivery.Id;

    public string EventLabel => Delivery.Event switch
    {
        "Completed" => "Porovnání dokončeno",
        "CompletedWithErrors" => "Dokončeno s chybami",
        "GateViolated" => "Porušená brána",
        "Failed" => "Porovnání selhalo",
        "ReadinessFailed" => "Připravenost selhala",
        "HealthDegraded" => "Zhoršené zdraví serveru",
        "StructureDrift" => "Odchylka struktury",
        "AutomationRecovered" => "Automatizace obnovena",
        "AutomationFailing" => "Automatizace selhává",
        "JobStalled" => "Úloha se zasekla",
        _ => Delivery.Event,
    };

    public string Scope => (Delivery.BranchKey, Delivery.InstanceKey) switch
    {
        (null or "", null or "") => "",
        ({ } b, null or "") => b,
        (null or "", { } i) => i,
        var (b, i) => $"{b}/{i}",
    };

    public string RecipientsText => string.Join(", ", Delivery.Recipients);

    public string StatusText => Delivery.Status switch
    {
        "Pending" => "Čeká na odeslání",
        "Sent" => "Odesláno",
        "Failed" => $"Selhalo ({Format.Plural(Delivery.AttemptCount, "pokus", "pokusy", "pokusů")}) — zkusí se znovu",
        "DeadLetter" => $"Nedoručeno ({Format.Plural(Delivery.AttemptCount, "pokus", "pokusy", "pokusů")})",
        _ => Delivery.Status,
    };

    public IBrush StatusBrush => Delivery.Status switch
    {
        "Sent" => Palette.Good,
        "Failed" => Palette.Warning,
        "DeadLetter" => Palette.Bad,
        _ => Palette.Muted,
    };

    public bool HasError => !string.IsNullOrWhiteSpace(Delivery.LastError);
    public string? ErrorText => Delivery.LastError;

    /// <summary>Znovu poslat dává smysl jen pro selhané/nedoručené řádky (Sent server odmítne, Pending už čeká).</summary>
    public bool CanResend => Delivery.Status is "Failed" or "DeadLetter";
}
