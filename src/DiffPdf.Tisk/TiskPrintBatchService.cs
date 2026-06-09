using System.Data;
using System.IO.Compression;
using DiffPdf.Application.Abstractions;
using D3Soft.Tisk.Application.Proxy;
using D3Soft.Tisk.Application.Shared.DataContracts;
using D3Soft.Tisk.Application.Shared.DataContracts.PDFValidation;
using D3Soft.Tisk.Application.Shared.DataContracts.PDFValidation.PDFBlankPage;
using D3Soft.Tisk.Application.Shared.DataContracts.PDFValidation.PDFTextPattern;

namespace DiffPdf.Tisk;

/// <summary>
/// <see cref="IPrintBatchService"/> over the D3Soft Tisk print SOA. The orchestration logic is
/// ported from HromadneVzory (<c>HromadneVzoryBL.PrintCustomer</c> / <c>GetCisloPredchoziDavky</c>
/// and <c>TransferFilesBL.TransferFiles</c>); the only differences are that the destination folder
/// is supplied by the caller (DiffPdf decides old/new) and System.IO replaces AlphaFS.
/// <para>
/// Every SOA call runs inside <c>scope.RunIsolated(...)</c> (the proxies are thread-affine) and is
/// offloaded via <see cref="Task.Run(Action)"/> so the async message handler thread isn't blocked
/// for the (minutes-long) duration of a print/fetch.
/// </para>
/// </summary>
public sealed class TiskPrintBatchService : IPrintBatchService
{
    // The "print all templates" source label that marks a comparable full batch, CZ + SK variants.
    private const string ZdrojTiskuCz = "Tisk všech vzorů";
    private const string ZdrojTiskuSk = "Tlač všetkých vzorov";

    private const int PageSize = 100;

    public Task<PrintSampleResult> PrintSampleAsync(PrintSoaConnection conn, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scope = WcfProxyScopeProvider.GetOrCreate(conn);
        return Task.Run(() => scope.RunIsolated(() =>
        {
            using var proxy = new TiskPrintingClientInitialized(conn.Login);

            var batchNumber = proxy.GetCisloDavkyVzor();
            var printed = proxy.PrintSampleAsync(batchNumber, null, null, BuildValidationSettings(conn.TextPattern));

            var blankPage = MapBlankPage(printed.PDFValidationResult.BlankPageValidationResult);
            var textPattern = MapTextPattern(printed.PDFValidationResult.TextPatternValidationResult);
            return new PrintSampleResult(batchNumber, blankPage, textPattern);
        }), ct);
    }

    public Task<string?> FindPreviousBatchAsync(PrintSoaConnection conn, string currentBatch, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scope = WcfProxyScopeProvider.GetOrCreate(conn);
        return Task.Run<string?>(() => scope.RunIsolated(() =>
        {
            var filter = new HistorieVzoruFilterDC { CountItemsOnPage = 10 };
            using var proxy = new AdministraceVzoruClientInitialized(conn.Login);
            var historie = proxy.GetHistorieTiskuVzoruByFilter(filter);

            // The history grid is a DataTable with CisloDavky/ZdrojTisku columns (the same shape
            // HromadneVzory's TiskDokumentVzorZakazkaDC.FromTable reads). Take the most recent
            // "print all templates" batch that isn't the one we just printed.
            DataTable table = historie.Data.GridDataTable;
            foreach (DataRow row in table.Rows)
            {
                var cislo = row["CisloDavky"] as string;
                var zdroj = row["ZdrojTisku"] as string;
                if (string.IsNullOrEmpty(cislo) || cislo == currentBatch)
                    continue;
                if (string.Equals(zdroj, ZdrojTiskuCz, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(zdroj, ZdrojTiskuSk, StringComparison.OrdinalIgnoreCase))
                    return cislo;
            }
            return null;
        }), ct);
    }

    public Task FetchBatchToFolderAsync(PrintSoaConnection conn, string batchNumber, string destinationFolder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scope = WcfProxyScopeProvider.GetOrCreate(conn);
        return Task.Run(() => scope.RunIsolated(() =>
        {
            Directory.CreateDirectory(destinationFolder);

            using (var docProxy = new DokumentEntitaDavkaClientInitialized(conn.Login))
            {
                var idDocData = docProxy.GetIdDocDataByCisloDavky(batchNumber);
                if (idDocData == null || idDocData.Count == 0)
                    throw new InvalidOperationException($"Dávka '{batchNumber}' neobsahuje žádné dokumenty ke stažení.");

                var zipFileName = Path.Combine(destinationFolder, "instance.zip");
                int min = 0;
                int count = PageSize;
                while (min < idDocData.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    if (min + count > idDocData.Count)
                        count = idDocData.Count - min;

                    Stream zippedFileStream = null!;
                    try
                    {
                        var ids = idDocData.GetRange(min, count).ToArray();
                        using (var streamProxy = new TiskDocumentClientInitialized(conn.Login))
                        {
                            streamProxy.GetDocumentByCisloDavkyStream(batchNumber, min, ids, out zippedFileStream);
                        }

                        if (zippedFileStream != null)
                        {
                            using var writeStream = new FileStream(zipFileName, FileMode.Create);
                            zippedFileStream.CopyTo(writeStream);
                        }

                        ZipFile.ExtractToDirectory(zipFileName, destinationFolder, overwriteFiles: true);
                        File.Delete(zipFileName);
                    }
                    finally
                    {
                        zippedFileStream?.Dispose();
                    }

                    min += count;
                }
            }
        }), ct);
    }

    private static PdfValidationSettingsDC BuildValidationSettings(string textPattern) => new()
    {
        BlankPageValidationSettings = new BlankPageValidationSettingsDC
        {
            HeaderSizeInMillimeters = 0,
            FooterSizeInMillimeters = 0,
            UseValuesFromClient = true,
            SkipValidation = false,
        },
        TextPatternValidationSettings = new TextPatternValidationSettingsDC
        {
            TextPattern = textPattern,
            SkipValidation = false,
        },
    };

    private static PrintValidation MapBlankPage(BlankPageValidationResultDC result)
    {
        if (result == null || !result.IsError)
            return new PrintValidation(false, "Validace na prázdné stránky proběhla úspěšně.", []);

        var files = ExtractInvalidFileNames(result.InvalidResults);
        var summary = "Validace na prázdné stránky proběhla NEÚSPĚŠNĚ. Chyba u dokladů: " + string.Join(", ", files);
        return new PrintValidation(true, summary, files);
    }

    private static PrintValidation MapTextPattern(TextPatternValidationResultDC result)
    {
        if (result == null || !result.IsError)
            return new PrintValidation(false, $"Validace na výskyt slova '{result?.TextPattern}' proběhla úspěšně.", []);

        var files = ExtractInvalidFileNames(result.InvalidResults);
        var summary = $"Validace na výskyt slova '{result.TextPattern}' proběhla NEÚSPĚŠNĚ. Chyba u dokladů: " + string.Join(", ", files);
        return new PrintValidation(true, summary, files);
    }

    // The result DCs nest InvalidResults[] -> Results[] -> PrintFile.FileName for both validators.
    private static IReadOnlyList<string> ExtractInvalidFileNames(IEnumerable<dynamic>? invalidResults)
    {
        var files = new List<string>();
        if (invalidResults == null)
            return files;

        foreach (var group in invalidResults)
        {
            if (group?.Results == null)
                continue;
            foreach (var item in group.Results)
            {
                string? fileName = item?.PrintFile?.FileName;
                if (!string.IsNullOrEmpty(fileName))
                    files.Add(fileName);
            }
        }
        return files;
    }
}
