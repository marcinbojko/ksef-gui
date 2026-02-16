using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KSeFCli;

// ── Data model ───────────────────────────────────────────────────────────────

internal record InvoiceParty(
    string? Nip,
    string? Nazwa,
    string? KodKraju,
    string? AdresL1,
    string? AdresL2,
    string? Email,
    string? Telefon,
    string? NrEORI,
    string? NrKlienta
);

internal record InvoiceLine(
    int Nr,
    string? UuId,
    string? Name,
    string? Indeks,
    string? Gtin,
    string? Unit,
    string? Qty,
    string? UnitNetPrice,
    string? UnitGrossPrice,
    string? NetTotal,
    string? GrossTotal,
    string? VatRate,
    string? ExchangeRate
);

internal record VatRow(string Rate, string? Net, string? Vat);

internal record BankAccount(string? NrRB, string? NazwaBanku, string? OpisRachunku);

internal record InvoiceData(
    string? RodzajFaktury,
    string? Numer,
    string? DataFaktury,
    string? MiejsceWystawienia,
    string? DataDostawy,      // P_6
    string? Waluta,
    string? OkresOd,
    string? OkresDo,
    InvoiceParty Sprzedawca,
    InvoiceParty Nabywca,
    List<InvoiceLine> Wiersze,
    List<VatRow> VatRows,
    string? RazemBrutto,
    List<(string Klucz, string Wartosc)> DodatkowyOpis,
    bool Zaplacono,
    string? DataZaplaty,
    string? FormaPlatnosci,
    List<string> TerminyPlatnosci,
    BankAccount? RachunekBankowy,
    string? WZ,
    List<string> NrUmowy,
    string? NrFaZaliczkowej,
    string? PelnaNazwa,
    string? Regon,
    string? Bdo,
    string? SystemInfo
);

// ── Parser ───────────────────────────────────────────────────────────────────

internal static class KSeFInvoiceParser
{
    private static readonly XNamespace FallbackNs = "http://crd.gov.pl/wzor/2025/06/25/13775/";

    // 10 MB ceiling — a real KSeF invoice is well under 1 MB
    private const long MaxXmlBytes = 10 * 1024 * 1024;

    public static InvoiceData Parse(string xmlContent)
    {
        if (xmlContent.Length > MaxXmlBytes)
        {
            throw new InvalidOperationException($"XML content exceeds maximum allowed size ({MaxXmlBytes / 1024 / 1024} MB).");
        }

        XmlReaderSettings readerSettings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,   // block XXE / DTD injection
            XmlResolver = null,                        // disable external entity resolution
            MaxCharactersInDocument = MaxXmlBytes,
        };

        XDocument doc;
        using (XmlReader reader = XmlReader.Create(new System.IO.StringReader(xmlContent), readerSettings))
        {
            doc = XDocument.Load(reader);
        }

        XElement root = doc.Root!;
        XNamespace ns = root.GetDefaultNamespace() == XNamespace.None ? FallbackNs : root.GetDefaultNamespace();

        XElement? El(XElement parent, string name) =>
            parent.Element(ns + name) ?? parent.Element(name);

        string? Val(XElement? parent, string name) =>
            parent is null ? null : El(parent, name)?.Value?.Trim();

        IEnumerable<XElement> Els(XElement parent, string name) =>
            parent.Elements(ns + name).Any()
                ? parent.Elements(ns + name)
                : parent.Elements(name);

        XElement naglowek = El(root, "Naglowek")!;
        XElement podmiot1 = El(root, "Podmiot1")!;
        XElement podmiot2 = El(root, "Podmiot2")!;
        XElement fa = El(root, "Fa")!;
        XElement? stopka = El(root, "Stopka");

        InvoiceParty ParseParty(XElement p)
        {
            XElement? dane = El(p, "DaneIdentyfikacyjne");
            XElement? adres = El(p, "Adres");
            XElement? kontakt = El(p, "DaneKontaktowe");
            return new InvoiceParty(
                Nip: Val(dane, "NIP"),
                Nazwa: Val(dane, "Nazwa"),
                KodKraju: Val(adres, "KodKraju"),
                AdresL1: Val(adres, "AdresL1"),
                AdresL2: Val(adres, "AdresL2"),
                Email: Val(kontakt, "Email"),
                Telefon: Val(kontakt, "Telefon"),
                NrEORI: Val(p, "NrEORI"),
                NrKlienta: Val(p, "NrKlienta")
            );
        }

        List<InvoiceLine> wiersze = Els(fa, "FaWiersz")
            .Select(w => new InvoiceLine(
                Nr: int.TryParse(Val(w, "NrWierszaFa"), out int nr) ? nr : 0,
                UuId: Val(w, "UU_ID"),
                Name: Val(w, "P_7"),
                Indeks: Val(w, "Indeks"),
                Gtin: Val(w, "GTIN"),
                Unit: Val(w, "P_8A"),
                Qty: Val(w, "P_8B"),
                UnitNetPrice: Val(w, "P_9A"),
                UnitGrossPrice: Val(w, "P_9B"),
                NetTotal: Val(w, "P_11"),
                GrossTotal: Val(w, "P_11A"),
                VatRate: Val(w, "P_12"),
                ExchangeRate: Val(w, "KursWaluty") ?? Val(w, "P_KursWaluty")
            ))
            .OrderBy(w => w.Nr)
            .ToList();

        // VAT summary
        List<VatRow> vatRows = new List<VatRow>();
        (string Rate, string NetField, string? VatField)[] vatMap = new (string Rate, string NetField, string? VatField)[]
        {
            ("23%", "P_13_1", "P_14_1"),
            ("8%",  "P_13_2", "P_14_2"),
            ("5%",  "P_13_3", "P_14_3"),
            ("0%",  "P_13_6", null),
            ("zw",  "P_13_7", null),
            ("np",  "P_13_8", null),
        };
        foreach ((string? rate, string? netF, string? vatF) in vatMap)
        {
            string? net = Val(fa, netF);
            if (!string.IsNullOrEmpty(net))
            {
                vatRows.Add(new VatRow(rate, net, vatF is null ? null : Val(fa, vatF)));
            }
        }

        List<(string Klucz, string Wartosc)> dodOpis = Els(fa, "DodatkowyOpis")
            .Select(d => (Klucz: Val(d, "Klucz") ?? "", Wartosc: Val(d, "Wartosc") ?? ""))
            .ToList();

        XElement? platnosc = El(fa, "Platnosc");
        bool zaplacono = platnosc is not null && Val(platnosc, "Zaplacono") == "1";
        string? dataZaplaty = platnosc is null ? null : Val(platnosc, "DataZaplaty");
        string? formaPlatnosci = platnosc is null ? null : FormatPlatnosc(Val(platnosc, "FormaPlatnosci"));

        // TerminPlatnosci can be a string or contain nested <Termin> elements
        List<string> terminy = new List<string>();
        if (platnosc is not null)
        {
            XElement? tpEl = El(platnosc, "TerminPlatnosci");
            if (tpEl is not null)
            {
                List<string> terminEls = Els(tpEl, "Termin").Select(t => t.Value.Trim()).ToList();
                if (terminEls.Count > 0)
                {
                    terminy.AddRange(terminEls);
                }
                else if (!string.IsNullOrWhiteSpace(tpEl.Value))
                {
                    terminy.Add(tpEl.Value.Trim());
                }
            }
        }

        BankAccount? bank = null;
        if (platnosc is not null)
        {
            XElement? rbEl = El(platnosc, "RachunekBankowy");
            if (rbEl is not null)
            {
                bank = new BankAccount(Val(rbEl, "NrRB"), Val(rbEl, "NazwaBanku"), Val(rbEl, "OpisRachunku"));
            }
        }

        XElement? warunki = El(fa, "WarunkiTransakcji");
        List<string> umowy = new List<string>();
        if (warunki is not null)
        {
            XElement? umowyEl = El(warunki, "Umowy");
            if (umowyEl is not null)
            {
                umowy.AddRange(Els(umowyEl, "NrUmowy").Select(u => u.Value.Trim()));
            }
        }

        XElement? faZal = El(fa, "FakturaZaliczkowa");
        string? nrFaZal = faZal is null ? null : Val(faZal, "NrFaZaliczkowej");

        XElement? rejestry = stopka is null ? null : El(stopka, "Rejestry");
        XElement? okresEl = El(fa, "OkresFa");

        return new InvoiceData(
            RodzajFaktury: Val(fa, "RodzajFaktury"),
            Numer: Val(fa, "P_2"),
            DataFaktury: Val(fa, "P_1"),
            MiejsceWystawienia: Val(fa, "P_1M"),
            DataDostawy: Val(fa, "P_6"),
            Waluta: Val(fa, "KodWaluty") ?? "PLN",
            OkresOd: okresEl is null ? null : Val(okresEl, "P_6_Od"),
            OkresDo: okresEl is null ? null : Val(okresEl, "P_6_Do"),
            Sprzedawca: ParseParty(podmiot1),
            Nabywca: ParseParty(podmiot2),
            Wiersze: wiersze,
            VatRows: vatRows,
            RazemBrutto: Val(fa, "P_15"),
            DodatkowyOpis: dodOpis,
            Zaplacono: zaplacono,
            DataZaplaty: dataZaplaty,
            FormaPlatnosci: formaPlatnosci,
            TerminyPlatnosci: terminy,
            RachunekBankowy: bank,
            WZ: Val(fa, "WZ"),
            NrUmowy: umowy,
            NrFaZaliczkowej: nrFaZal,
            PelnaNazwa: rejestry is null ? null : Val(rejestry, "PelnaNazwa"),
            Regon: rejestry is null ? null : Val(rejestry, "REGON"),
            Bdo: rejestry is null ? null : Val(rejestry, "BDO"),
            SystemInfo: Val(naglowek, "SystemInfo")
        );
    }

    private static string? FormatPlatnosc(string? code) => code switch
    {
        "1" => "Gotówka",
        "2" => "Karta",
        "3" => "Bon",
        "4" => "Czek",
        "5" => "Kredyt",
        "6" => "Przelew",
        "7" => "Mobilna",
        null => null,
        _ => code
    };
}

// ── Sanitizer ────────────────────────────────────────────────────────────────

/// <summary>
/// Two-phase defence before data reaches the PDF renderer:
/// <list type="number">
///   <item><description>
///     <b>Schema validation</b> — validates the raw XML against the official FA(3) XSD
///     (http://crd.gov.pl/wzor/2025/06/25/13775/).  All violations are logged as warnings
///     but never block rendering (partial data is better than no PDF).
///   </description></item>
///   <item><description>
///     <b>Field sanitization</b> — every string field extracted by the parser is:
///     stripped of C0 control characters, truncated to the schema-specified maximum length,
///     and validated against the schema-derived pattern/enumeration.  Fields that fail are
///     replaced with <c>null</c> so the renderer never receives structurally invalid text.
///   </description></item>
/// </list>
/// All constants and patterns come directly from FA3.xsd / ElementarneTypy.xsd —
/// not from guesswork.
/// </summary>
internal static class KSeFInvoiceSanitizer
{
    // ── Compiled FA(3) schema (loaded once from embedded resources) ──────────
    private static readonly Lazy<XmlSchemaSet?> _schema = new(LoadSchemaSet);

    // ── Length caps — from FA3.xsd simpleType maxLength values ───────────────
    // TZnakowy: maxLength 256  (most text fields: Numer, P_1M, Klucz, Wartosc, …)
    private const int LenZnakowy = 256;
    // TZnakowy512: maxLength 512 (Nazwa party, P_7 item name, AdresL1/L2)
    private const int LenZnakowy512 = 512;
    // TAdresEmail: maxLength 255
    private const int LenEmail = 255;
    // TNrRB: maxLength 34 (bank account / IBAN)
    private const int LenNrRB = 34;

    // ── Whitelists — exact enumeration values from FA3.xsd ───────────────────

    // TRodzajFaktury (7 values)
    private static readonly HashSet<string> ValidRodzajFaktury = new(StringComparer.Ordinal)
        { "VAT", "KOR", "ZAL", "ROZ", "UPR", "KOR_ZAL", "KOR_ROZ" };

    // TStawkaPodatku / P_12 (14 values) — exactly as enumerated in FA3.xsd
    private static readonly HashSet<string> ValidStawkaPodatku = new(StringComparer.Ordinal)
        { "23", "22", "8", "7", "5", "4", "3", "0 KR", "0 WDT", "0 EX", "zw", "oo", "np I", "np II" };

    // TKodWaluty — all 182 ISO 4217 codes from FA3.xsd
    private static readonly HashSet<string> ValidKodWaluty = new(StringComparer.Ordinal)
    {
        "AED","AFN","ALL","AMD","ANG","AOA","ARS","AUD","AWG","AZN","BAM","BBD","BDT","BGN","BHD",
        "BIF","BMD","BND","BOB","BOV","BRL","BSD","BTN","BWP","BYN","BZD","CAD","CDF","CHE","CHF",
        "CHW","CLF","CLP","CNY","COP","COU","CRC","CUC","CUP","CVE","CZK","DJF","DKK","DOP","DZD",
        "EGP","ERN","ETB","EUR","FJD","FKP","GBP","GEL","GHS","GIP","GMD","GNF","GTQ","GYD","HKD",
        "HNL","HRK","HTG","HUF","IDR","ILS","INR","IQD","IRR","ISK","JMD","JOD","JPY","KES","KGS",
        "KHR","KMF","KPW","KRW","KWD","KYD","KZT","LAK","LBP","LKR","LRD","LSL","LYD","MAD","MDL",
        "MGA","MKD","MMK","MNT","MOP","MRU","MUR","MVR","MWK","MXN","MXV","MYR","MZN","NAD","NGN",
        "NIO","NOK","NPR","NZD","OMR","PAB","PEN","PGK","PHP","PKR","PLN","PYG","QAR","RON","RSD",
        "RUB","RWF","SAR","SBD","SCR","SDG","SEK","SGD","SHP","SLE","SLL","SOS","SRD","SSP","STN",
        "SVC","SYP","SZL","THB","TJS","TMT","TND","TOP","TRY","TTD","TWD","TZS","UAH","UGX","USD",
        "USN","UYI","UYU","UYW","UZS","VED","VES","VND","VUV","WST","XAF","XAG","XAU","XBA","XBB",
        "XBC","XBD","XCD","XDR","XOF","XPD","XPF","XPT","XSU","XTS","XUA","XXX","YER","ZAR","ZMW","ZWL",
    };

    // ── Patterns — from ElementarneTypy.xsd / FA3.xsd ────────────────────────

    // TNrNIP: [1-9]((\d[1-9])|([1-9]\d))\d{7}   (as defined in ElementarneTypy.xsd)
    private static readonly Regex ReNip = new(
        @"^[1-9]((\d[1-9])|([1-9]\d))\d{7}$", RegexOptions.Compiled);

    // TNrREGON: \d{9} | \d{14}   (as defined in ElementarneTypy.xsd — union of 9 and 14 digits)
    private static readonly Regex ReRegon = new(@"^(\d{9}|\d{14})$", RegexOptions.Compiled);

    // TKodKraju: [A-Z]{2}   (2-letter ISO country code)
    private static readonly Regex ReKodKraju = new(@"^[A-Z]{2}$", RegexOptions.Compiled);

    // TKwotowy: monetary amounts (P_11, P_13, P_14, P_15, VAT row amounts)
    //   pattern from FA3.xsd: -?([1-9]\d{0,15}|0)(\.\d{1,2})?
    private static readonly Regex ReKwotowy = new(
        @"^-?([1-9]\d{0,15}|0)(\.\d{1,2})?$", RegexOptions.Compiled);

    // TKwotowy2: exchange rates (KursWaluty)
    //   pattern from FA3.xsd: -?([1-9]\d{0,13}|0)(\.\d{1,8})?
    private static readonly Regex ReKwotowy2 = new(
        @"^-?([1-9]\d{0,13}|0)(\.\d{1,8})?$", RegexOptions.Compiled);

    // TIlosci: quantities (P_8B)
    //   pattern from FA3.xsd: -?([1-9]\d{0,15}|0)(\.\d{1,6})?
    private static readonly Regex ReIlosci = new(
        @"^-?([1-9]\d{0,15}|0)(\.\d{1,6})?$", RegexOptions.Compiled);

    // TData/TDataT/TDataU: date format (YYYY-MM-DD range 2006-01-01 – 2050-01-01)
    private static readonly DateTime DateMin = new(2006, 1, 1);
    private static readonly DateTime DateMax = new(2050, 1, 1);

    // TAdresEmail: (.)+@(.)+  min 3 chars  (ElementarneTypy.xsd)
    private static readonly Regex ReEmail = new(@"^.+@.+$", RegexOptions.Compiled);

    // ── Entry point ──────────────────────────────────────────────────────────

    /// <summary>
    /// Validates <paramref name="xmlContent"/> against the FA(3) XSD (logging any violations),
    /// then returns a sanitized copy of <paramref name="data"/>.
    /// </summary>
    public static InvoiceData Sanitize(string xmlContent, InvoiceData data)
    {
        ValidateSchema(xmlContent);
        return SanitizeData(data);
    }

    // ── Phase 1 — XSD schema validation ──────────────────────────────────────

    private static void ValidateSchema(string xmlContent)
    {
        XmlSchemaSet? schemas = _schema.Value;
        if (schemas is null)
        {
            return;
        }

        List<string> issues = new();
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
        };
        settings.ValidationEventHandler += (_, args) =>
        {
            string sev = args.Severity == XmlSeverityType.Error ? "ERR" : "WARN";
            issues.Add($"[{sev}] {args.Message} (line {args.Exception?.LineNumber})");
        };

        try
        {
            using XmlReader reader = XmlReader.Create(new System.IO.StringReader(xmlContent), settings);
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            issues.Add($"[FATAL] {ex.Message}");
        }

        if (issues.Count > 0)
        {
            Log.LogWarning($"[fa3-validator] {issues.Count} schema violation(s) in invoice XML:");
            foreach (string issue in issues)
            {
                Log.LogWarning($"  {issue}");
            }
        }
    }

    private static XmlSchemaSet? LoadSchemaSet()
    {
        try
        {
            System.Reflection.Assembly asm = typeof(KSeFInvoiceSanitizer).Assembly;
            using System.IO.Stream? fa3Stream = asm.GetManifestResourceStream("ksefcli.Resources.FA3.xsd");
            if (fa3Stream is null)
            {
                Log.LogWarning("[fa3-validator] FA3.xsd not found in resources; schema validation disabled");
                return null;
            }

            XmlSchemaSet schemas = new() { XmlResolver = new EmbeddedSchemaResolver(asm) };
            using XmlReader reader = XmlReader.Create(fa3Stream);
            schemas.Add(null, reader);
            schemas.Compile();
            return schemas;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[fa3-validator] Failed to compile XSD schema: {ex.Message}; validation disabled");
            return null;
        }
    }

    /// <summary>Resolves XSD imports from embedded resources instead of making HTTP requests.</summary>
    private sealed class EmbeddedSchemaResolver : XmlResolver
    {
        private static readonly Dictionary<string, string> UrlToResource = new(StringComparer.Ordinal)
        {
            ["http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/StrukturyDanych_v10-0E.xsd"]
                = "ksefcli.Resources.StrukturyDanych.xsd",
            ["http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/ElementarneTypyDanych_v10-0E.xsd"]
                = "ksefcli.Resources.ElementarneTypy.xsd",
            ["http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/KodyKrajow_v10-0E.xsd"]
                = "ksefcli.Resources.KodyKrajow.xsd",
        };

        private readonly System.Reflection.Assembly _asm;

        internal EmbeddedSchemaResolver(System.Reflection.Assembly asm) => _asm = asm;

        public override System.Net.ICredentials? Credentials { set { } }

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            if (UrlToResource.TryGetValue(absoluteUri.AbsoluteUri, out string? resName))
            {
                return _asm.GetManifestResourceStream(resName);
            }

            return null; // block any unexpected external requests
        }
    }

    // ── Phase 2 — field-level sanitization ───────────────────────────────────

    private static InvoiceData SanitizeData(InvoiceData d) => d with
    {
        RodzajFaktury = SanitizeRodzaj(d.RodzajFaktury),
        Numer = Text(d.Numer),
        DataFaktury = SanitizeDate(d.DataFaktury),
        MiejsceWystawienia = Text(d.MiejsceWystawienia),
        DataDostawy = SanitizeDate(d.DataDostawy),
        Waluta = SanitizeCurrency(d.Waluta),
        OkresOd = SanitizeDate(d.OkresOd),
        OkresDo = SanitizeDate(d.OkresDo),
        Sprzedawca = SanitizeParty(d.Sprzedawca),
        Nabywca = SanitizeParty(d.Nabywca),
        Wiersze = d.Wiersze.Select(SanitizeLine).ToList(),
        VatRows = d.VatRows.Select(SanitizeVatRow).ToList(),
        RazemBrutto = SanitizeKwotowy(d.RazemBrutto),
        DodatkowyOpis = d.DodatkowyOpis
                               .Select(x => (Text(x.Klucz) ?? "", Text(x.Wartosc) ?? ""))
                               .ToList(),
        DataZaplaty = SanitizeDate(d.DataZaplaty),
        TerminyPlatnosci = d.TerminyPlatnosci
                               .Select(t => SanitizeDate(t) ?? Text(t) ?? "")
                               .ToList(),
        RachunekBankowy = d.RachunekBankowy is null ? null : SanitizeBank(d.RachunekBankowy),
        WZ = Text(d.WZ),
        NrUmowy = d.NrUmowy.Select(u => Text(u) ?? "").ToList(),
        NrFaZaliczkowej = Text(d.NrFaZaliczkowej),
        PelnaNazwa = Name(d.PelnaNazwa),
        Regon = SanitizeRegon(d.Regon),
        Bdo = Text(d.Bdo),
        SystemInfo = Text(d.SystemInfo),
    };

    private static InvoiceParty SanitizeParty(InvoiceParty p) => p with
    {
        Nip = SanitizeNip(p.Nip),
        Nazwa = Name(p.Nazwa),
        KodKraju = SanitizeKodKraju(p.KodKraju),
        AdresL1 = Name(p.AdresL1),
        AdresL2 = Name(p.AdresL2),
        Email = SanitizeEmail(p.Email),
        Telefon = Text(p.Telefon),
        NrEORI = Text(p.NrEORI),
        NrKlienta = Text(p.NrKlienta),
    };

    private static InvoiceLine SanitizeLine(InvoiceLine l) => l with
    {
        UuId = Text(l.UuId),
        Name = Name(l.Name),          // TZnakowy512
        Indeks = Text(l.Indeks),
        Gtin = SanitizeGtin(l.Gtin),
        Unit = Text(l.Unit),
        Qty = SanitizeIlosci(l.Qty),           // TIlosci
        UnitNetPrice = SanitizeKwotowy(l.UnitNetPrice),  // TKwotowy
        UnitGrossPrice = SanitizeKwotowy(l.UnitGrossPrice),
        NetTotal = SanitizeKwotowy(l.NetTotal),
        GrossTotal = SanitizeKwotowy(l.GrossTotal),
        VatRate = SanitizeStawka(l.VatRate),        // TStawkaPodatku
        ExchangeRate = SanitizeKwotowy2(l.ExchangeRate), // TKwotowy2
    };

    private static VatRow SanitizeVatRow(VatRow r) => r with
    {
        Net = SanitizeKwotowy(r.Net),
        Vat = SanitizeKwotowy(r.Vat),
    };

    private static BankAccount SanitizeBank(BankAccount b) => b with
    {
        NrRB = SanitizeNrRB(b.NrRB),
        NazwaBanku = Text(b.NazwaBanku),
        OpisRachunku = Text(b.OpisRachunku),
    };

    // ── Strip helpers ─────────────────────────────────────────────────────────

    /// <summary>Removes C0 control chars (except HT/LF/CR) and truncates to <paramref name="max"/>.</summary>
    private static string? Strip(string? s, int max)
    {
        if (s is null)
        {
            return null;
        }

        System.Text.StringBuilder sb = new(s.Length);
        foreach (char c in s)
        {
            if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r')
            {
                sb.Append(c);
            }
        }

        string r = sb.ToString().Trim();
        return r.Length == 0 ? null : r.Length > max ? r[..max] : r;
    }

    // TZnakowy (256) — most text fields
    private static string? Text(string? s) => Strip(s, LenZnakowy);
    // TZnakowy512 (512) — party names, item names, addresses
    private static string? Name(string? s) => Strip(s, LenZnakowy512);

    // ── Field rules ───────────────────────────────────────────────────────────

    /// <summary>
    /// TData/TDataT: YYYY-MM-DD, calendar date, range 2006-01-01 – 2050-01-01.
    /// Dates within the period field (OkresOd/Do) use month-only YYYY-MM format — also accepted.
    /// </summary>
    private static string? SanitizeDate(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string t = s.Trim();

        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        {
            return d >= DateMin && d <= DateMax ? t : null;
        }

        // OkresFa uses YYYY-MM month format — accept as-is if it parses
        if (DateTime.TryParseExact(t + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dm))
        {
            return dm >= DateMin && dm <= DateMax ? t : null;
        }

        return null;
    }

    /// <summary>TKodWaluty — must be one of the 182 enumerated codes from FA3.xsd; falls back to PLN.</summary>
    private static string SanitizeCurrency(string? s)
    {
        if (s is null)
        {
            return "PLN";
        }

        string upper = s.Trim().ToUpperInvariant();
        return ValidKodWaluty.Contains(upper) ? upper : "PLN";
    }

    /// <summary>TKodKraju — 2 uppercase letters ([A-Z]{2} per ElementarneTypy.xsd).</summary>
    private static string? SanitizeKodKraju(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string upper = s.Trim().ToUpperInvariant();
        return ReKodKraju.IsMatch(upper) ? upper : null;
    }

    /// <summary>
    /// TNrNIP — pattern [1-9]((\d[1-9])|([1-9]\d))\d{7} as defined in ElementarneTypy.xsd.
    /// The XSD does not define a checksum; format match is sufficient.
    /// </summary>
    private static string? SanitizeNip(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string digits = s.Trim().Replace("-", "").Replace(" ", "");
        return ReNip.IsMatch(digits) ? digits : null;
    }

    /// <summary>TNrREGON — exactly 9 digits (\d{9}) per ElementarneTypy.xsd.</summary>
    private static string? SanitizeRegon(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string digits = s.Trim().Replace(" ", "");
        return ReRegon.IsMatch(digits) ? digits : null;
    }

    /// <summary>TRodzajFaktury — 7 enumerated values from FA3.xsd.</summary>
    private static string? SanitizeRodzaj(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string t = s.Trim();
        return ValidRodzajFaktury.Contains(t) ? t : null;
    }

    /// <summary>TStawkaPodatku (P_12) — 14 enumerated values from FA3.xsd.</summary>
    private static string? SanitizeStawka(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string t = s.Trim();
        return ValidStawkaPodatku.Contains(t) ? t : null;
    }

    /// <summary>TKwotowy — monetary amount, max 2 decimal places (FA3.xsd pattern).</summary>
    private static string? SanitizeKwotowy(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        string norm = s.Trim().Replace(",", ".");
        return ReKwotowy.IsMatch(norm) ? norm : null;
    }

    /// <summary>TKwotowy2 — exchange rate, max 8 decimal places (FA3.xsd pattern).</summary>
    private static string? SanitizeKwotowy2(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        string norm = s.Trim().Replace(",", ".");
        return ReKwotowy2.IsMatch(norm) ? norm : null;
    }

    /// <summary>TIlosci — quantity, max 6 decimal places (FA3.xsd pattern).</summary>
    private static string? SanitizeIlosci(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        string norm = s.Trim().Replace(",", ".");
        return ReIlosci.IsMatch(norm) ? norm : null;
    }

    /// <summary>TAdresEmail — pattern (.)+@(.)+, 3–255 chars (ElementarneTypy.xsd).</summary>
    private static string? SanitizeEmail(string? s)
    {
        string? clean = Strip(s, LenEmail);
        if (clean is null || clean.Length < 3)
        {
            return null;
        }

        return ReEmail.IsMatch(clean) ? clean : null;
    }

    /// <summary>GTIN (GS1) — 8, 12, 13 or 14 digits. Not defined in KSeF XSD; standard GS1 rule.</summary>
    private static string? SanitizeGtin(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string digits = s.Trim().Replace(" ", "");
        return digits.Length is 8 or 12 or 13 or 14 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>TNrRB — 10–34 chars (FA3.xsd). Strips spaces; accepts Polish 26-digit or IBAN format.</summary>
    private static string? SanitizeNrRB(string? s)
    {
        if (s is null)
        {
            return null;
        }

        string clean = s.Trim().Replace(" ", "").ToUpperInvariant();
        if (clean.Length < 10 || clean.Length > LenNrRB)
        {
            return null;
        }

        // Polish account: 26 digits
        if (clean.Length == 26 && clean.All(char.IsAsciiDigit))
        {
            return clean;
        }

        // IBAN: CC + 2 check digits + alphanumeric
        if (clean.Length >= 4
            && char.IsAsciiLetter(clean[0]) && char.IsAsciiLetter(clean[1])
            && char.IsAsciiDigit(clean[2]) && char.IsAsciiDigit(clean[3])
            && clean.All(char.IsAsciiLetterOrDigit))
        {
            return clean;
        }

        return null;
    }
}

// ── Color scheme ─────────────────────────────────────────────────────────────

internal record PdfColorScheme(
    string Primary,      // header/table-header background, primary borders
    string PrimaryLight, // totals row background, subtle tints
    string PrimaryText,  // text on Primary background (always white or near-white)
    string Accent,       // "e-Faktur" brand red — kept per scheme for flexibility
    string TextMuted,    // secondary/label text
    string RowAlt,       // alternating table row background
    string Border        // separator lines
)
{
    public static readonly PdfColorScheme Navy = new(
        Primary: "#1a3a6b",
        PrimaryLight: "#e8eef7",
        PrimaryText: Colors.White,
        Accent: "#cc0000",
        TextMuted: "#666666",
        RowAlt: "#f5f5f5",
        Border: "#dddddd"
    );

    public static readonly PdfColorScheme Forest = new(
        Primary: "#1a4a2e",
        PrimaryLight: "#e8f4ec",
        PrimaryText: Colors.White,
        Accent: "#cc0000",
        TextMuted: "#556655",
        RowAlt: "#f4f9f5",
        Border: "#c8ddd0"
    );

    public static readonly PdfColorScheme Slate = new(
        Primary: "#2d3748",
        PrimaryLight: "#edf2f7",
        PrimaryText: Colors.White,
        Accent: "#cc0000",
        TextMuted: "#718096",
        RowAlt: "#f7fafc",
        Border: "#e2e8f0"
    );

    public static PdfColorScheme FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "forest" or "green" => Forest,
        "slate" or "grey" or "gray" => Slate,
        _ => Navy
    };
}

// ── PDF Generator ────────────────────────────────────────────────────────────

internal sealed class KSeFInvoicePdfGenerator(PdfColorScheme scheme)
{
    private string Blue => scheme.Primary;
    private string Gray => scheme.TextMuted;
    private string LightGray => scheme.RowAlt;
    private string BorderGray => scheme.Border;
    private string RedAccent => scheme.Accent;

    public static byte[] Generate(InvoiceData d, PdfColorScheme? colorScheme = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        KSeFInvoicePdfGenerator gen = new KSeFInvoicePdfGenerator(colorScheme ?? PdfColorScheme.Navy);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(1.2f, Unit.Centimetre);
                page.MarginBottom(1.2f, Unit.Centimetre);
                page.MarginLeft(1.5f, Unit.Centimetre);
                page.MarginRight(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(8.5f));

                page.Header().Element(c => gen.ComposeHeader(c, d));
                page.Content().PaddingTop(6).Element(c => gen.ComposeContent(c, d));
                page.Footer().Element(c => gen.ComposeFooter(c, d));
            });
        }).GeneratePdf();
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private void ComposeHeader(IContainer c, InvoiceData d)
    {
        c.Column(col =>
        {
            // Top bar: KSeF branding + invoice number
            col.Item().BorderBottom(1.5f).BorderColor(Blue).PaddingBottom(6).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(t =>
                    {
                        t.Span("Krajowy System ").FontSize(18).FontColor(Colors.Black);
                        t.Span("e-Faktur").FontSize(18).Bold().FontColor(RedAccent);
                    });
                    left.Item().PaddingTop(1).Text(InvoiceSubtitle(d.RodzajFaktury))
                        .FontSize(9).FontColor(Gray);
                });
                row.ConstantItem(210).AlignRight().Column(right =>
                {
                    right.Item().Text("Numer Faktury:").FontSize(7).FontColor(Gray);
                    right.Item().Text(d.Numer ?? "—").FontSize(14).Bold().FontColor(Colors.Black);
                    if (d.RodzajFaktury is not null)
                    {
                        right.Item().Text(InvoiceSubtitle(d.RodzajFaktury)).FontSize(7.5f).FontColor(Gray);
                    }
                });
            });

            // Party row
            col.Item().PaddingTop(8).Row(row =>
            {
                row.RelativeItem().Element(c2 => PartyBox(c2, "Sprzedawca", d.Sprzedawca));
                row.ConstantItem(10);
                row.RelativeItem().Element(c2 => PartyBox(c2, "Nabywca", d.Nabywca));
            });

            // Details bar
            col.Item().PaddingTop(8).BorderTop(0.5f).BorderColor(BorderGray)
                .PaddingTop(6).BorderBottom(0.5f).BorderColor(BorderGray).PaddingBottom(6)
                .Column(det =>
                {
                    det.Item().Text("Szczegóły").FontSize(8).Bold().FontColor(Colors.Black);
                    det.Item().PaddingTop(3).Row(r =>
                    {
                        r.RelativeItem().Text(t =>
                        {
                            t.Span("Data wystawienia, z zastrzeżeniem art. 106na ust. 1 ustawy: ")
                                .FontSize(7.5f).FontColor(Gray);
                            t.Span(d.DataFaktury ?? "—").FontSize(7.5f).Bold();
                        });
                        if (!string.IsNullOrEmpty(d.MiejsceWystawienia))
                        {
                            r.ConstantItem(180).Text(t =>
                            {
                                t.Span("Miejsce wystawienia: ").FontSize(7.5f).FontColor(Gray);
                                t.Span(d.MiejsceWystawienia).FontSize(7.5f).Bold();
                            });
                        }
                    });
                    if (!string.IsNullOrEmpty(d.DataDostawy))
                    {
                        det.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span("Data dostawy/wykonania usługi: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.DataDostawy).FontSize(7.5f).Bold();
                        });
                    }

                    if (!string.IsNullOrEmpty(d.OkresOd))
                    {
                        det.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span("Okres: ").FontSize(7.5f).FontColor(Gray);
                            t.Span($"{d.OkresOd} – {d.OkresDo}").FontSize(7.5f).Bold();
                        });
                    }

                    if (!string.IsNullOrEmpty(d.NrFaZaliczkowej))
                    {
                        det.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span("Faktura zaliczkowa: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.NrFaZaliczkowej).FontSize(7.5f);
                        });
                    }
                });
        });
    }

    private static string InvoiceSubtitle(string? rodzaj) => rodzaj switch
    {
        "VAT" => "Faktura podstawowa",
        "ROZ" => "Faktura rozliczeniowa",
        "ZAL" => "Faktura zaliczkowa",
        "KOR" => "Faktura korygująca",
        "KOR_ZAL" => "Faktura korygująca zaliczkową",
        "UPR" => "Faktura uproszczona",
        "RR" => "Faktura RR",
        null => "Faktura",
        _ => $"Faktura {rodzaj}"
    };

    private void PartyBox(IContainer c, string label, InvoiceParty p)
    {
        c.BorderBottom(2).BorderColor(Blue).PaddingBottom(6).Column(col =>
        {
            col.Item().Text(label).FontSize(8).Bold().FontColor(Colors.Black);
            if (!string.IsNullOrEmpty(p.NrEORI))
            {
                col.Item().PaddingTop(3).Text(t =>
                {
                    t.Span("Numer EORI: ").FontSize(7.5f).Bold();
                    t.Span(p.NrEORI).FontSize(7.5f);
                });
            }
            if (!string.IsNullOrEmpty(p.Nip))
            {
                col.Item().PaddingTop(p.NrEORI is null ? 3 : 1).Text(t =>
                {
                    t.Span("NIP: ").FontSize(7.5f).Bold();
                    t.Span(p.Nip).FontSize(7.5f);
                });
            }

            if (!string.IsNullOrEmpty(p.Nazwa))
            {
                col.Item().PaddingTop(1).Text(t =>
                {
                    t.Span("Nazwa: ").FontSize(7.5f).Bold();
                    t.Span(p.Nazwa).FontSize(7.5f);
                });
            }

            if (!string.IsNullOrEmpty(p.AdresL1) || !string.IsNullOrEmpty(p.AdresL2))
            {
                col.Item().PaddingTop(4).Text("Adres").FontSize(7.5f).Bold().FontColor(Gray);
                if (!string.IsNullOrEmpty(p.AdresL1))
                {
                    col.Item().Text(p.AdresL1).FontSize(7.5f);
                }

                if (!string.IsNullOrEmpty(p.AdresL2))
                {
                    col.Item().Text(p.AdresL2).FontSize(7.5f);
                }

                if (!string.IsNullOrEmpty(p.KodKraju))
                {
                    col.Item().Text(CountryName(p.KodKraju)).FontSize(7.5f);
                }
            }

            if (!string.IsNullOrEmpty(p.Email) || !string.IsNullOrEmpty(p.Telefon))
            {
                col.Item().PaddingTop(4).Text("Dane kontaktowe").FontSize(7.5f).Bold().FontColor(Gray);
                if (!string.IsNullOrEmpty(p.Email))
                {
                    col.Item().Text($"E-mail: {p.Email}").FontSize(7.5f);
                }

                if (!string.IsNullOrEmpty(p.Telefon))
                {
                    col.Item().Text($"Tel.: {p.Telefon}").FontSize(7.5f);
                }
            }

            if (!string.IsNullOrEmpty(p.NrKlienta))
            {
                col.Item().PaddingTop(2).Text($"Nr klienta: {p.NrKlienta}").FontSize(7f).FontColor(Gray);
            }
        });
    }

    // ── Content ──────────────────────────────────────────────────────────────

    private void ComposeContent(IContainer c, InvoiceData d)
    {
        c.Column(col =>
        {
            // Line items
            if (d.Wiersze.Count > 0)
            {
                col.Item().PaddingTop(8).Element(c2 => LinesSection(c2, d));
            }

            // VAT summary + total
            col.Item().PaddingTop(10).Row(row =>
            {
                if (d.VatRows.Count > 0)
                {
                    row.RelativeItem().Element(c2 => VatTable(c2, d));
                }
                else
                {
                    row.RelativeItem();
                }

                if (!string.IsNullOrEmpty(d.RazemBrutto))
                {
                    row.ConstantItem(200).AlignBottom().AlignRight().Column(tot =>
                    {
                        tot.Item().Background("#f5f5f5").Border(0.5f).BorderColor(Blue)
                            .Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text("Kwota należności ogółem:").FontSize(9).Bold();
                            r.ConstantItem(80).AlignRight()
                                .Text($"{d.RazemBrutto} {d.Waluta}").FontSize(9).Bold().FontColor(Colors.Black);
                        });
                    });
                }
            });

            // Payment
            bool hasPayment = d.Zaplacono || !string.IsNullOrEmpty(d.DataZaplaty)
                || !string.IsNullOrEmpty(d.FormaPlatnosci) || d.TerminyPlatnosci.Count > 0
                || d.RachunekBankowy is not null;
            if (hasPayment)
            {
                col.Item().PaddingTop(10).Element(c2 => PaymentSection(c2, d));
            }

            // Additional notes
            if (d.DodatkowyOpis.Count > 0)
            {
                col.Item().PaddingTop(10).Element(c2 => DodatkowyOpisSection(c2, d));
            }

            // WZ documents
            if (!string.IsNullOrEmpty(d.WZ))
            {
                col.Item().PaddingTop(10).Element(c2 => WzSection(c2, d));
            }

            // Contracts
            if (d.NrUmowy.Count > 0)
            {
                col.Item().PaddingTop(10).Element(c2 => UmowySection(c2, d));
            }

            // Registries footer
            bool hasRej = !string.IsNullOrEmpty(d.PelnaNazwa) || !string.IsNullOrEmpty(d.Regon)
                || !string.IsNullOrEmpty(d.Bdo);
            if (hasRej)
            {
                col.Item().PaddingTop(10).Element(c2 => RejestryTable(c2, d));
            }
        });
    }

    // ── Line items ───────────────────────────────────────────────────────────

    private void LinesSection(IContainer c, InvoiceData d)
    {
        bool hasUuId = d.Wiersze.Any(w => !string.IsNullOrEmpty(w.UuId));
        bool hasGross = d.Wiersze.Any(w => !string.IsNullOrEmpty(w.UnitGrossPrice) || !string.IsNullOrEmpty(w.GrossTotal));
        bool hasFx = d.Waluta != "PLN" && d.Wiersze.Any(w => !string.IsNullOrEmpty(w.ExchangeRate));
        bool hasGtin = d.Wiersze.Any(w => !string.IsNullOrEmpty(w.Gtin));

        c.Column(col =>
        {
            col.Item().Text("Pozycje").FontSize(9).Bold().FontColor(Colors.Black);
            col.Item().PaddingTop(2).Text(
                $"Faktura wystawiona w cenach netto w walucie {d.Waluta}")
                .FontSize(7.5f).FontColor(Gray).Italic();

            col.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(18);  // Lp
                    if (hasUuId)
                    {
                        cols.ConstantColumn(72); // UU_ID
                    }

                    cols.RelativeColumn(3);   // Nazwa
                    cols.ConstantColumn(26);  // Cena netto
                    if (hasGross)
                    {
                        cols.ConstantColumn(28); // Cena brutto
                    }

                    cols.ConstantColumn(22);  // Ilość
                    cols.ConstantColumn(24);  // Miara
                    cols.ConstantColumn(22);  // Stawka
                    cols.ConstantColumn(32);  // Wart. netto
                    if (hasGross)
                    {
                        cols.ConstantColumn(32); // Wart. brutto
                    }

                    if (hasFx)
                    {
                        cols.ConstantColumn(32);    // Kurs
                    }
                });

                table.Header(h =>
                {
                    void Th(string text) =>
                        h.Cell().Background(Blue).Padding(3)
                            .Text(text).FontSize(6.5f).Bold().FontColor(Colors.White);

                    Th("Lp.");
                    if (hasUuId)
                    {
                        Th("Unikalny numer wiersza");
                    }

                    Th("Nazwa towaru\nlub usługi");
                    Th("Cena\njedn.\nnetto");
                    if (hasGross)
                    {
                        Th("Cena jedn.\nbrutto");
                    }

                    Th("Ilość");
                    Th("Miara");
                    Th("Stawka\npodatku");
                    Th("Wartość\nsprzedaży netto");
                    if (hasGross)
                    {
                        Th("Wartość\nsprzedaży\nbrutto");
                    }

                    if (hasFx)
                    {
                        Th("Kurs\nwaluty");
                    }
                });

                foreach ((InvoiceLine? w, int i) in d.Wiersze.Select((w, i) => (w, i)))
                {
                    string bg = i % 2 == 0 ? Colors.White : LightGray;
                    void Td(string? text, bool right = false, bool center = false)
                    {
                        IContainer cell = table.Cell().Background(bg)
                            .BorderBottom(0.5f).BorderColor(BorderGray).Padding(3);
                        TextBlockDescriptor txt = cell.Text(text ?? "").FontSize(7.5f);
                        if (right)
                        {
                            txt.AlignRight();
                        }
                        else if (center)
                        {
                            txt.AlignCenter();
                        }
                    }

                    Td(w.Nr.ToString(), center: true);
                    if (hasUuId)
                    {
                        Td(w.UuId);
                    }

                    Td(w.Name);
                    Td(w.UnitNetPrice, right: true);
                    if (hasGross)
                    {
                        Td(w.UnitGrossPrice, right: true);
                    }

                    Td(w.Qty, right: true);
                    Td(w.Unit, center: true);
                    Td(FormatVatRate(w.VatRate), center: true);
                    Td(w.NetTotal, right: true);
                    if (hasGross)
                    {
                        Td(w.GrossTotal, right: true);
                    }

                    if (hasFx)
                    {
                        Td(w.ExchangeRate, right: true);
                    }
                }
            });

            // GTIN sub-table
            if (hasGtin)
            {
                col.Item().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.ConstantColumn(18);
                        cols.ConstantColumn(80);
                    });
                    table.Header(h =>
                    {
                        h.Cell().Background(Blue).Padding(3)
                            .Text("Lp.").FontSize(6.5f).Bold().FontColor(Colors.White);
                        h.Cell().Background(Blue).Padding(3)
                            .Text("GTIN").FontSize(6.5f).Bold().FontColor(Colors.White);
                    });
                    foreach ((InvoiceLine? w, int i) in d.Wiersze.Select((w, i) => (w, i)))
                    {
                        if (string.IsNullOrEmpty(w.Gtin))
                        {
                            continue;
                        }

                        string bg = i % 2 == 0 ? Colors.White : LightGray;
                        table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray)
                            .Padding(3).Text(w.Nr.ToString()).FontSize(7.5f).AlignCenter();
                        table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray)
                            .Padding(3).Text(w.Gtin).FontSize(7.5f);
                    }
                });
            }
        });
    }

    // ── VAT summary ──────────────────────────────────────────────────────────

    private void VatTable(IContainer c, InvoiceData d)
    {
        c.Column(col =>
        {
            col.Item().Text("Podsumowanie stawek podatku").FontSize(9).Bold().FontColor(Colors.Black);
            col.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.ConstantColumn(18);  // Lp
                    cols.ConstantColumn(55);  // Stawka
                    cols.ConstantColumn(70);  // Netto
                    cols.ConstantColumn(60);  // VAT
                    cols.ConstantColumn(70);  // Brutto
                });
                table.Header(h =>
                {
                    void Th(string t) =>
                        h.Cell().Background(Blue).Padding(3)
                            .Text(t).FontSize(6.5f).Bold().FontColor(Colors.White);
                    Th("Lp."); Th("Stawka podatku"); Th("Kwota netto"); Th("Kwota podatku"); Th("Kwota brutto");
                });

                foreach ((VatRow? row, int i) in d.VatRows.Select((r, i) => (r, i)))
                {
                    string bg = i % 2 == 0 ? Colors.White : LightGray;
                    decimal net = Parse(row.Net);
                    decimal vat = Parse(row.Vat);
                    string brutto = (net + vat).ToString("F2", CultureInfo.InvariantCulture);

                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                        .Text((i + 1).ToString()).FontSize(7.5f).AlignCenter();
                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                        .Text(row.Rate).FontSize(7.5f);
                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                        .Text(row.Net ?? "0.00").FontSize(7.5f).AlignRight();
                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                        .Text(row.Vat ?? "0.00").FontSize(7.5f).AlignRight();
                    table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                        .Text(brutto).FontSize(7.5f).AlignRight();
                }
            });
        });
    }

    // ── Payment ──────────────────────────────────────────────────────────────

    private void PaymentSection(IContainer c, InvoiceData d)
    {
        c.BorderTop(0.5f).BorderColor(BorderGray).PaddingTop(6).Column(col =>
        {
            col.Item().Text("Płatność").FontSize(9).Bold().FontColor(Colors.Black);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    if (d.Zaplacono)
                    {
                        left.Item().Text(t =>
                        {
                            t.Span("Informacja o płatności: ").FontSize(7.5f).FontColor(Gray);
                            t.Span("Zapłacono").FontSize(7.5f).Bold().FontColor("#2a7a2a");
                        });
                    }

                    if (!string.IsNullOrEmpty(d.DataZaplaty))
                    {
                        left.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("Data zapłaty: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.DataZaplaty).FontSize(7.5f);
                        });
                    }

                    if (!string.IsNullOrEmpty(d.FormaPlatnosci))
                    {
                        left.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("Forma płatności: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.FormaPlatnosci).FontSize(7.5f);
                        });
                    }

                    foreach (string termin in d.TerminyPlatnosci)
                    {
                        left.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("Termin płatności: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(termin).FontSize(7.5f);
                        });
                    }
                });

                if (d.RachunekBankowy is not null)
                {
                    row.ConstantItem(10);
                    row.RelativeItem().Column(right =>
                    {
                        right.Item().Text("Rachunek bankowy").FontSize(7.5f).Bold().FontColor(Gray);
                        if (!string.IsNullOrEmpty(d.RachunekBankowy.NrRB))
                        {
                            right.Item().PaddingTop(1)
                                .Text(FormatIban(d.RachunekBankowy.NrRB)).FontSize(7.5f);
                        }

                        if (!string.IsNullOrEmpty(d.RachunekBankowy.NazwaBanku))
                        {
                            right.Item().Text(d.RachunekBankowy.NazwaBanku).FontSize(7.5f).FontColor(Gray);
                        }

                        if (!string.IsNullOrEmpty(d.RachunekBankowy.OpisRachunku))
                        {
                            right.Item().Text(d.RachunekBankowy.OpisRachunku).FontSize(7.5f).FontColor(Gray);
                        }
                    });
                }
            });
        });
    }

    // ── DodatkowyOpis ────────────────────────────────────────────────────────

    private void DodatkowyOpisSection(IContainer c, InvoiceData d)
    {
        c.BorderTop(0.5f).BorderColor(BorderGray).PaddingTop(6).Column(col =>
        {
            col.Item().Text("Informacje dodatkowe").FontSize(9).Bold().FontColor(Colors.Black);
            foreach ((string? klucz, string? wartosc) in d.DodatkowyOpis)
            {
                col.Item().PaddingTop(3).Row(row =>
                {
                    row.ConstantItem(140).Text(klucz).FontSize(7.5f).Bold().FontColor(Gray);
                    row.RelativeItem().Text(wartosc.Trim()).FontSize(7.5f);
                });
            }
        });
    }

    // ── WZ ───────────────────────────────────────────────────────────────────

    private void WzSection(IContainer c, InvoiceData d)
    {
        c.BorderTop(0.5f).BorderColor(BorderGray).PaddingTop(6).Column(col =>
        {
            col.Item().Text("Numery dokumentów magazynowych WZ").FontSize(9).Bold().FontColor(Colors.Black);
            col.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(cols => cols.ConstantColumn(120));
                table.Header(h =>
                    h.Cell().Background(Blue).Padding(3)
                        .Text("Numer WZ").FontSize(6.5f).Bold().FontColor(Colors.White));
                table.Cell().BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                    .Text(d.WZ).FontSize(7.5f);
            });
        });
    }

    // ── Umowy ────────────────────────────────────────────────────────────────

    private void UmowySection(IContainer c, InvoiceData d)
    {
        c.BorderTop(0.5f).BorderColor(BorderGray).PaddingTop(6).Column(col =>
        {
            col.Item().Text("Umowy").FontSize(9).Bold().FontColor(Colors.Black);
            foreach (string nr in d.NrUmowy)
            {
                col.Item().PaddingTop(2).Text(nr).FontSize(7.5f);
            }
        });
    }

    // ── Rejestry ─────────────────────────────────────────────────────────────

    private void RejestryTable(IContainer c, InvoiceData d)
    {
        c.BorderTop(0.5f).BorderColor(BorderGray).PaddingTop(6).Column(col =>
        {
            col.Item().Text("Rejestry").FontSize(9).Bold().FontColor(Colors.Black);
            col.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(3);
                    cols.ConstantColumn(70);
                    cols.ConstantColumn(70);
                });
                table.Header(h =>
                {
                    void Th(string t) =>
                        h.Cell().Background(Blue).Padding(3)
                            .Text(t).FontSize(6.5f).Bold().FontColor(Colors.White);
                    Th("Pełna nazwa"); Th("REGON"); Th("BDO");
                });
                table.Cell().BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                    .Text(d.PelnaNazwa ?? d.Sprzedawca.Nazwa ?? "").FontSize(7.5f);
                table.Cell().BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                    .Text(d.Regon ?? "").FontSize(7.5f);
                table.Cell().BorderBottom(0.5f).BorderColor(BorderGray).Padding(3)
                    .Text(d.Bdo ?? "").FontSize(7.5f);
            });
        });
    }

    // ── Footer ───────────────────────────────────────────────────────────────

    private void ComposeFooter(IContainer c, InvoiceData d)
    {
        c.BorderTop(0.5f).BorderColor(BorderGray).PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(7).FontColor(Gray));
                if (!string.IsNullOrEmpty(d.SystemInfo))
                {
                    t.Span($"Wytworzona w: {d.SystemInfo}");
                }
            });
            row.ConstantItem(80).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(7).FontColor(Gray));
                t.Span("Strona ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string FormatVatRate(string? rate)
    {
        if (rate is null) return "—";
        string t = rate.Trim();
        // Append % only for purely numeric tokens (e.g. "23", "8", "5", "0");
        // non-numeric codes ("0 KR", "0 WDT", "zw", "oo", "np I") are returned as-is.
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+$") ? t + "%" : t;
    }

    private static string FormatIban(string nr)
    {
        // Group Polish IBAN in blocks of 4 for readability
        nr = nr.Replace(" ", "");
        if (nr.Length == 26 && nr.All(char.IsDigit))
        {
            // Polish account: 2-digit check + 24 digit number
            return nr[..2] + " " + string.Join(" ", Enumerable.Range(0, 6).Select(i => nr.Substring(2 + i * 4, 4)));
        }
        return nr;
    }

    private static string CountryName(string? code) => code switch
    {
        "PL" => "Polska",
        "DE" => "Niemcy",
        "FR" => "Francja",
        "GB" => "Wielka Brytania",
        "CZ" => "Czechy",
        "SK" => "Słowacja",
        null => "",
        _ => code
    };

    private static decimal Parse(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v) ? v : 0;
}

// ── Public entry point ───────────────────────────────────────────────────────

public static class KSeFInvoicePdf
{
    public static byte[] FromXml(string xmlContent, string? colorScheme = null)
    {
        InvoiceData data = KSeFInvoiceSanitizer.Sanitize(xmlContent, KSeFInvoiceParser.Parse(xmlContent));
        return KSeFInvoicePdfGenerator.Generate(data, PdfColorScheme.FromName(colorScheme));
    }
}
