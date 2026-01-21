using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Invoices.Common;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("SzukajFaktur", HelpText = "Query invoice metadata")]
public class SzukajFakturCommand : GlobalCommand
{
    [Option('s', "subjectType", Default = "Subject1", HelpText = """
    Typ podmiotu, którego dotyczą kryteria filtrowania metadanych faktur. Określa kontekst, w jakim przeszukiwane są dane.
    Wartość           | Opis
    ------------------|-------------------------
    Subject1          | Podmiot 1 - sprzedawca
    Subject2          | Podmiot 2 - nabywca
    Subject3          | Podmiot 3
    SubjectAuthorized | Podmiot upoważniony
    """)]
    public required string SubjectType { get; set; }

    [Option("from", Required = true, HelpText = "Start date in ISO-8601 format")]
    public DateTime From { get; set; }

    [Option("to", HelpText = "End date in ISO-8601 format")]
    public DateTime? To { get; set; }

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

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        SzukajFakturCommand settings = this;

        if (!Enum.TryParse(settings.SubjectType, true, out InvoiceSubjectType subjectType))
        {
            Console.Error.WriteLine($"Invalid SubjectType: {settings.SubjectType}");
            return 1;
        }

        if (!Enum.TryParse(settings.DateType, true, out DateType dateType))
        {
            Console.Error.WriteLine($"Invalid DateType: {settings.DateType}");
            return 1;
        }

        InvoiceQueryFilters invoiceQueryFilters = new InvoiceQueryFilters
        {
            SubjectType = subjectType,
            DateRange = new DateRange
            {
                From = settings.From,
                To = settings.To,
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
                    Console.Error.WriteLine($"Invalid CurrencyCode: {currencyCodeStr}");
                    return 1;
                }
            }
        }

        if (settings.AmountType is not null)
        {
            if (!Enum.TryParse(settings.AmountType, true, out AmountType amountType))
            {
                Console.Error.WriteLine($"Invalid AmountType: {settings.AmountType}");
                return 1;
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
                Console.Error.WriteLine($"Invalid BuyerIdentifierType: {settings.BuyerIdentifierType}");
                return 1;
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
                Console.Error.WriteLine($"Invalid InvoicingMode: {settings.InvoicingMode}");
                return 1;
            }
            invoiceQueryFilters.InvoicingMode = invoicingMode;
        }

        if (settings.FormType is not null)
        {
            if (!Enum.TryParse(settings.FormType, true, out FormType formType))
            {
                Console.Error.WriteLine($"Invalid FormType: {settings.FormType}");
                return 1;
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
                    Console.Error.WriteLine($"Invalid InvoiceType: {invoiceTypeStr}");
                    return 1;
                }
            }
        }

        string accessToken = await GetAccessToken(cancellationToken).ConfigureAwait(false);
        PagedInvoiceResponse pagedInvoicesResponse = await ksefClient.QueryInvoiceMetadataAsync(
            invoiceQueryFilters,
            accessToken,
            pageOffset: settings.PageOffset,
            pageSize: settings.PageSize,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(pagedInvoicesResponse));
        return 0;
    }
}
