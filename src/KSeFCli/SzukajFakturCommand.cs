using System.ComponentModel;
using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Invoices.Common;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("SzukajFaktur", HelpText = "Query invoice metadata")]
public class SzukajFakturCommand : IWithConfigCommand
{
    [Option('s', "subjectType", Default = "Subject1", HelpText = """
    Typ podmiotu, którego dotyczą kryteria filtrowania metadanych faktur. Określa kontekst, w jakim przeszukiwane są dane.
    Wartość                 | Opis
    ------------------------|-------------------------
    Subject1, 1, sprzedawca | Podmiot 1 - sprzedawca
    Subject2, 2, nabywca    | Podmiot 2 - nabywca
    Subject3, 3             | Podmiot 3
    SubjectAuthorized, 4    | Podmiot upoważniony
    """)]
    public required string SubjectType { get; set; }

    [Option("from", Required = true, HelpText = "Start date. Can be a specific date (e.g., 2023-01-01) or a relative date (e.g., -2days, 'last monday').")]
    public required string From { get; set; }

    [Option("to", HelpText = "End date. Can be a specific date (e.g., 2023-01-31) or a relative date (e.g., today, -1day).")]
    public string? To { get; set; }

    [Option("dateType", Default = "Issue", HelpText = """
    Typ daty, według której ma być zastosowany zakres.
    Wartość           | Opis
    ------------------|-----------------------------------------------------------------
    Issue             | Data wystawienia faktury.
    Invoicing         | Data przyjęcia faktury w systemie KSeF (do dalszego przetwarzania).
    PermanentStorage  | Data trwałego zapisu faktury w repozytorium systemu KSeF.
    """)]
    public required string DateType { get; set; }

    [Option("pageOffset", Default = 0, HelpText = "Page offset for pagination")]
    public int PageOffset { get; set; }

    [Option("pageSize", Default = 10, HelpText = "Page size for pagination")]
    public int PageSize { get; set; }

    [Option("restrictToPermanentStorageHwmDate", HelpText = "Określa, czy system ma ograniczyć filtrowanie (zakres dateRange.to) do wartości PermanentStorageHwmDate. Dotyczy wyłącznie zapytań z dateType = PermanentStorage.")]
    public bool? RestrictToPermanentStorageHwmDate { get; set; }

    [Option("ksefNumber", HelpText = "Numer KSeF faktury (dokładne dopasowanie).")]
    public string? KsefNumber { get; set; }

    [Option("invoiceNumber", HelpText = "Numer faktury nadany przez wystawcę (dokładne dopasowanie).")]
    public string? InvoiceNumber { get; set; }

    [Option("amountType", HelpText = """
    Filtr kwotowy. Należy podać typ, oraz opcjonalnie zakres od/do.
    Wartość | Opis
    --------|------------
    Brutto  | Kwota brutto
    Netto   | Kwota netto
    Vat     | Kwota VAT
    """)]
    public string? AmountType { get; set; }

    [Option("amountFrom", HelpText = "Minimalna wartość kwoty.")]
    public double? AmountFrom { get; set; }

    [Option("amountTo", HelpText = "Maksymalna wartość kwoty.")]
    public double? AmountTo { get; set; }

    [Option("sellerNip", HelpText = "NIP sprzedawcy (dokładne dopasowanie).")]
    public string? SellerNip { get; set; }

    [Option("buyerIdentifierType", HelpText = """
    Typ identyfikatora nabywcy.
    Wartość | Opis
    --------|---------------------------------------
    Nip     | 10 cyfrowy numer NIP
    VatUe   | Identyfikator VAT UE podmiotu unijnego
    Other   | Inny identyfikator
    None    | Brak identyfikatora nabywcy
    """)]
    public string? BuyerIdentifierType { get; set; }

    [Option("buyerIdValue", HelpText = "Wartość identyfikatora nabywcy (dokładne dopasowanie).")]
    public string? BuyerIdValue { get; set; }

    [Option("currencyCodes", Separator = ',', HelpText = """
    Kody walut. Można podać wiele wartości oddzielonych przecinkami, np. `--currencyCodes PLN,EUR`.
    Dostępne kody: AED, AFN, ALL, AMD, ANG, AOA, ARS, AUD, AWG, AZN, BAM, BBD, BDT, BGN, BHD, BIF, BMD, BND, BOB, BOV, BRL, BSD, BTN, BWP, BYN, BZD, CAD, CDF, CHE, CHF, CHW, CLF, CLP, CNY, COP, COU, CRC, CUC, CUP, CVE, CZK, DJF, DKK, DOP, DZD, EGP, ERN, ETB, EUR, FJD, FKP, GBP, GEL, GGP, GHS, GIP, GMD, GNF, GTQ, GYD, HKD, HNL, HRK, HTG, HUF, IDR, ILS, IMP, INR, IQD, IRR, ISK, JEP, JMD, JOD, JPY, KES, KGS, KHR, KMF, KPW, KRW, KWD, KYD, KZT, LAK, LBP, LKR, LRD, LSL, LYD, MAD, MDL, MGA, MKD, MMK, MNT, MOP, MRU, MUR, MVR, MWK, MXN, MXV, MYR, MZN, NAD, NGN, NIO, NOK, NPR, NZD, OMR, PAB, PEN, PGK, PHP, PKR, PLN, PYG, QAR, RON, RSD, RUB, RWF, SAR, SBD, SCR, SDG, SEK, SGD, SHP, SLL, SOS, SRD, SSP, STN, SVC, SYP, SZL, THB, TJS, TMT, TND, TOP, TRY, TTD, TWD, TZS, UAH, UGX, USD, USN, UYI, UYU, UYW, UZS, VES, VND, VUV, WST, XAF, XAG, XAU, XBA, XBB, XBC, XBD, XCD, XCG, XDR, XOF, XPD, XPF, XPT, XSU, XUA, XXX, YER, ZAR, ZMW, ZWL.
    """)]
    public IEnumerable<string>? CurrencyCodes { get; set; }

    [Option("invoicingMode", HelpText = "Tryb wystawienia faktury: Online lub Offline.")]
    public string? InvoicingMode { get; set; }

    [Option("isSelfInvoicing", HelpText = "Czy faktura została wystawiona w trybie samofakturowania.")]
    public bool? IsSelfInvoicing { get; set; }

    [Option("formType", HelpText = """
    Typ dokumentu.
    Wartość | Opis
    --------|----------------
    FA      | Faktura VAT
    PEF     | Faktura PEF
    RR      | Faktura RR
    """)]
    public string? FormType { get; set; }

    [Option("invoiceTypes", Separator = ',', HelpText = """
    Rodzaje faktur. Można podać wiele wartości oddzielonych przecinkami.
    Wartość  | Opis
    ---------|-------------------------------------------
    Vat      | (FA) Podstawowa
    Zal      | (FA) Zaliczkowa
    Kor      | (FA) Korygująca
    Roz      | (FA) Rozliczeniowa
    Upr      | (FA) Uproszczona
    KorZal   | (FA) Korygująca fakturę zaliczkową
    KorRoz   | (FA) Korygująca fakturę rozliczeniową
    VatPef   | (PEF) Podstawowa
    VatPefSp | (PEF) Specjalizowana
    KorPef   | (PEF) Korygująca
    VatRr    | (RR) Podstawowa
    KorVatRr | (RR) Korygująca
    """)]
    public IEnumerable<string>? InvoiceTypes { get; set; }

    [Option("hasAttachment", HelpText = "Czy faktura ma załącznik.")]
    public bool? HasAttachment { get; set; }

    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        Log.LogInformation("Szukanie faktur...");
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();

        List<InvoiceSummary> invoices = await SzukajFaktury(
            scope,
            ksefClient,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(invoices));
        return 0;
    }

    protected async Task<List<InvoiceSummary>> SzukajFaktury(
        IServiceScope scope,
        IKSeFClient ksefClient,
        CancellationToken cancellationToken)
    {
        SzukajFakturCommand settings = this;

        if (!Enum.TryParse(settings.SubjectType, true, out InvoiceSubjectType subjectType))
        {
            subjectType = settings.SubjectType.ToLowerInvariant() switch
            {
                "1" or "sprzedawca" => InvoiceSubjectType.Subject1,
                "2" or "nabywca" => InvoiceSubjectType.Subject2,
                "3" => InvoiceSubjectType.Subject3,
                "4" => InvoiceSubjectType.SubjectAuthorized,
                _ => throw new FormatException($"Invalid SubjectType: {settings.SubjectType}")
            };
        }

        if (!Enum.TryParse(settings.DateType, true, out DateType dateType))
        {
            throw new InvalidEnumArgumentException($"Invalid DateType: {settings.DateType}");
        }

        DateTime parsedFromDate = await ParseDate.Parse(settings.From).ConfigureAwait(false);
        DateTime? parsedToDate = null;
        if (settings.To is not null)
        {
            parsedToDate = await ParseDate.Parse(settings.To).ConfigureAwait(false);
        }

        InvoiceQueryFilters invoiceQueryFilters = new InvoiceQueryFilters
        {
            SubjectType = subjectType,
            DateRange = new DateRange
            {
                From = parsedFromDate,
                To = parsedToDate,
                DateType = dateType,
                RestrictToPermanentStorageHwmDate = settings.RestrictToPermanentStorageHwmDate
            },
            KsefNumber = settings.KsefNumber,
            InvoiceNumber = settings.InvoiceNumber,
            SellerNip = settings.SellerNip,
            IsSelfInvoicing = settings.IsSelfInvoicing,
            HasAttachment = settings.HasAttachment
        };

        if (settings.CurrencyCodes is not null)
        {
            invoiceQueryFilters.CurrencyCodes = new List<CurrencyCode>();
            foreach (string currencyCodeStr in settings.CurrencyCodes)
            {
                if (Enum.TryParse(currencyCodeStr, true, out CurrencyCode currencyCode))
                {
                    invoiceQueryFilters.CurrencyCodes.Add(currencyCode);
                }
                else
                {
                    throw new InvalidEnumArgumentException($"Invalid CurrencyCode: {currencyCodeStr}");
                }
            }
        }

        if (settings.AmountType is not null)
        {
            if (!Enum.TryParse(settings.AmountType, true, out AmountType amountType))
            {
                throw new InvalidEnumArgumentException($"Invalid AmountType: {settings.AmountType}");
            }
            AmountFilter amountFilter = new AmountFilter
            {
                Type = amountType
            };
            if (settings.AmountFrom.HasValue)
            {
                amountFilter.From = (decimal)settings.AmountFrom.Value;
            }
            if (settings.AmountTo.HasValue)
            {
                amountFilter.To = (decimal)settings.AmountTo.Value;
            }
            invoiceQueryFilters.Amount = amountFilter;
        }

        if (settings.BuyerIdentifierType is not null)
        {
            if (!Enum.TryParse(settings.BuyerIdentifierType, true, out BuyerIdentifierType buyerIdentifierType))
            {
                throw new InvalidEnumArgumentException($"Invalid BuyerIdentifierType: {settings.BuyerIdentifierType}");
            }
            invoiceQueryFilters.BuyerIdentifier = new BuyerIdentifier
            {
                Type = buyerIdentifierType,
                Value = settings.BuyerIdValue
            };
        }

        if (settings.InvoicingMode is not null)
        {
            if (!Enum.TryParse(settings.InvoicingMode, true, out InvoicingMode invoicingMode))
            {
                throw new InvalidEnumArgumentException($"Invalid InvoicingMode: {settings.InvoicingMode}");
            }
            invoiceQueryFilters.InvoicingMode = invoicingMode;
        }

        if (settings.FormType is not null)
        {
            if (!Enum.TryParse(settings.FormType, true, out FormType formType))
            {
                throw new InvalidEnumArgumentException($"Invalid FormType: {settings.FormType}");
            }
            invoiceQueryFilters.FormType = formType;
        }

        if (settings.InvoiceTypes is not null)
        {
            invoiceQueryFilters.InvoiceTypes = new List<InvoiceType>();
            foreach (string invoiceTypeStr in settings.InvoiceTypes)
            {
                if (Enum.TryParse(invoiceTypeStr, true, out InvoiceType invoiceType))
                {
                    invoiceQueryFilters.InvoiceTypes.Add(invoiceType);
                }
                else
                {
                    throw new InvalidEnumArgumentException($"Invalid InvoiceType: {invoiceTypeStr}");
                }
            }
        }

        string accessToken = await GetAccessToken(scope, cancellationToken).ConfigureAwait(false);

        List<InvoiceSummary> allInvoices = new List<InvoiceSummary>();
        PagedInvoiceResponse pagedInvoicesResponse;
        int currentPageOffset = settings.PageOffset;

        do
        {
            Log.LogInformation($"Fetching page with offset {currentPageOffset} and size {settings.PageSize}");
            pagedInvoicesResponse = await ksefClient.QueryInvoiceMetadataAsync(
                invoiceQueryFilters,
                accessToken,
                pageOffset: currentPageOffset,
                pageSize: settings.PageSize,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (pagedInvoicesResponse.Invoices != null)
            {
                allInvoices.AddRange(pagedInvoicesResponse.Invoices);
            }

            currentPageOffset += settings.PageSize;
        } while (pagedInvoicesResponse.HasMore == true);

        Log.LogInformation($"Found {allInvoices.Count} invoices.");

        return allInvoices;
    }
}
