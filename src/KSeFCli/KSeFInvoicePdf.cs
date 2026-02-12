using System.Globalization;
using System.Xml.Linq;
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
    private static readonly XNamespace Ns = "http://crd.gov.pl/wzor/2025/06/25/13775/";

    public static InvoiceData Parse(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var root = doc.Root!;

        XElement? El(XElement parent, string name) =>
            parent.Element(Ns + name) ?? parent.Element(name);

        string? Val(XElement? parent, string name) =>
            parent is null ? null : El(parent, name)?.Value?.Trim();

        IEnumerable<XElement> Els(XElement parent, string name) =>
            parent.Elements(Ns + name).Any()
                ? parent.Elements(Ns + name)
                : parent.Elements(name);

        var naglowek = El(root, "Naglowek")!;
        var podmiot1 = El(root, "Podmiot1")!;
        var podmiot2 = El(root, "Podmiot2")!;
        var fa = El(root, "Fa")!;
        var stopka = El(root, "Stopka");

        InvoiceParty ParseParty(XElement p)
        {
            var dane = El(p, "DaneIdentyfikacyjne");
            var adres = El(p, "Adres");
            var kontakt = El(p, "DaneKontaktowe");
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

        var wiersze = Els(fa, "FaWiersz")
            .Select(w => new InvoiceLine(
                Nr: int.TryParse(Val(w, "NrWierszaFa"), out var nr) ? nr : 0,
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
        var vatRows = new List<VatRow>();
        var vatMap = new (string Rate, string NetField, string? VatField)[]
        {
            ("23%", "P_13_1", "P_14_1"),
            ("8%",  "P_13_2", "P_14_2"),
            ("5%",  "P_13_3", "P_14_3"),
            ("0%",  "P_13_6", null),
            ("zw",  "P_13_7", null),
            ("np",  "P_13_8", null),
        };
        foreach (var (rate, netF, vatF) in vatMap)
        {
            var net = Val(fa, netF);
            if (!string.IsNullOrEmpty(net))
                vatRows.Add(new VatRow(rate, net, vatF is null ? null : Val(fa, vatF)));
        }

        var dodOpis = Els(fa, "DodatkowyOpis")
            .Select(d => (Klucz: Val(d, "Klucz") ?? "", Wartosc: Val(d, "Wartosc") ?? ""))
            .ToList();

        var platnosc = El(fa, "Platnosc");
        bool zaplacono = platnosc is not null && Val(platnosc, "Zaplacono") == "1";
        string? dataZaplaty = platnosc is null ? null : Val(platnosc, "DataZaplaty");
        string? formaPlatnosci = platnosc is null ? null : FormatPlatnosc(Val(platnosc, "FormaPlatnosci"));

        // TerminPlatnosci can be a string or contain nested <Termin> elements
        var terminy = new List<string>();
        if (platnosc is not null)
        {
            var tpEl = El(platnosc, "TerminPlatnosci");
            if (tpEl is not null)
            {
                var terminEls = Els(tpEl, "Termin").Select(t => t.Value.Trim()).ToList();
                if (terminEls.Count > 0)
                    terminy.AddRange(terminEls);
                else if (!string.IsNullOrWhiteSpace(tpEl.Value))
                    terminy.Add(tpEl.Value.Trim());
            }
        }

        BankAccount? bank = null;
        if (platnosc is not null)
        {
            var rbEl = El(platnosc, "RachunekBankowy");
            if (rbEl is not null)
                bank = new BankAccount(Val(rbEl, "NrRB"), Val(rbEl, "NazwaBanku"), Val(rbEl, "OpisRachunku"));
        }

        var warunki = El(fa, "WarunkiTransakcji");
        var umowy = new List<string>();
        if (warunki is not null)
        {
            var umowyEl = El(warunki, "Umowy");
            if (umowyEl is not null)
                umowy.AddRange(Els(umowyEl, "NrUmowy").Select(u => u.Value.Trim()));
        }

        var faZal = El(fa, "FakturaZaliczkowa");
        string? nrFaZal = faZal is null ? null : Val(faZal, "NrFaZaliczkowej");

        var rejestry = stopka is null ? null : El(stopka, "Rejestry");
        var okresEl = El(fa, "OkresFa");

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
        Primary:      "#1a3a6b",
        PrimaryLight: "#e8eef7",
        PrimaryText:  Colors.White,
        Accent:       "#cc0000",
        TextMuted:    "#666666",
        RowAlt:       "#f5f5f5",
        Border:       "#dddddd"
    );

    public static readonly PdfColorScheme Forest = new(
        Primary:      "#1a4a2e",
        PrimaryLight: "#e8f4ec",
        PrimaryText:  Colors.White,
        Accent:       "#cc0000",
        TextMuted:    "#556655",
        RowAlt:       "#f4f9f5",
        Border:       "#c8ddd0"
    );

    public static readonly PdfColorScheme Slate = new(
        Primary:      "#2d3748",
        PrimaryLight: "#edf2f7",
        PrimaryText:  Colors.White,
        Accent:       "#cc0000",
        TextMuted:    "#718096",
        RowAlt:       "#f7fafc",
        Border:       "#e2e8f0"
    );

    public static PdfColorScheme FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "forest" or "green" => Forest,
        "slate"  or "grey" or "gray" => Slate,
        _ => Navy
    };
}

// ── PDF Generator ────────────────────────────────────────────────────────────

internal sealed class KSeFInvoicePdfGenerator(PdfColorScheme scheme)
{
    private string Blue       => scheme.Primary;
    private string Gray       => scheme.TextMuted;
    private string LightGray  => scheme.RowAlt;
    private string BorderGray => scheme.Border;
    private string RedAccent  => scheme.Accent;

    public static byte[] Generate(InvoiceData d, PdfColorScheme? colorScheme = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var gen = new KSeFInvoicePdfGenerator(colorScheme ?? PdfColorScheme.Navy);

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
                page.DefaultTextStyle(x => x.FontSize(8.5f).FontFamily("DejaVu Sans"));

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
                        right.Item().Text(InvoiceSubtitle(d.RodzajFaktury)).FontSize(7.5f).FontColor(Gray);
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
                            r.ConstantItem(180).Text(t =>
                            {
                                t.Span("Miejsce wystawienia: ").FontSize(7.5f).FontColor(Gray);
                                t.Span(d.MiejsceWystawienia).FontSize(7.5f).Bold();
                            });
                    });
                    if (!string.IsNullOrEmpty(d.DataDostawy))
                        det.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span("Data dostawy/wykonania usługi: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.DataDostawy).FontSize(7.5f).Bold();
                        });
                    if (!string.IsNullOrEmpty(d.OkresOd))
                        det.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span("Okres: ").FontSize(7.5f).FontColor(Gray);
                            t.Span($"{d.OkresOd} – {d.OkresDo}").FontSize(7.5f).Bold();
                        });
                    if (!string.IsNullOrEmpty(d.NrFaZaliczkowej))
                        det.Item().PaddingTop(2).Text(t =>
                        {
                            t.Span("Faktura zaliczkowa: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.NrFaZaliczkowej).FontSize(7.5f);
                        });
                });
        });
    }

    private static string InvoiceSubtitle(string? rodzaj) => rodzaj switch
    {
        "VAT"     => "Faktura podstawowa",
        "ROZ"     => "Faktura rozliczeniowa",
        "ZAL"     => "Faktura zaliczkowa",
        "KOR"     => "Faktura korygująca",
        "KOR_ZAL" => "Faktura korygująca zaliczkową",
        "UPR"     => "Faktura uproszczona",
        "RR"      => "Faktura RR",
        null      => "Faktura",
        _         => $"Faktura {rodzaj}"
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
                col.Item().PaddingTop(p.NrEORI is null ? 3 : 1).Text(t =>
                {
                    t.Span("NIP: ").FontSize(7.5f).Bold();
                    t.Span(p.Nip).FontSize(7.5f);
                });
            if (!string.IsNullOrEmpty(p.Nazwa))
                col.Item().PaddingTop(1).Text(t =>
                {
                    t.Span("Nazwa: ").FontSize(7.5f).Bold();
                    t.Span(p.Nazwa).FontSize(7.5f);
                });

            if (!string.IsNullOrEmpty(p.AdresL1) || !string.IsNullOrEmpty(p.AdresL2))
            {
                col.Item().PaddingTop(4).Text("Adres").FontSize(7.5f).Bold().FontColor(Gray);
                if (!string.IsNullOrEmpty(p.AdresL1))
                    col.Item().Text(p.AdresL1).FontSize(7.5f);
                if (!string.IsNullOrEmpty(p.AdresL2))
                    col.Item().Text(p.AdresL2).FontSize(7.5f);
                if (!string.IsNullOrEmpty(p.KodKraju))
                    col.Item().Text(CountryName(p.KodKraju)).FontSize(7.5f);
            }

            if (!string.IsNullOrEmpty(p.Email) || !string.IsNullOrEmpty(p.Telefon))
            {
                col.Item().PaddingTop(4).Text("Dane kontaktowe").FontSize(7.5f).Bold().FontColor(Gray);
                if (!string.IsNullOrEmpty(p.Email))
                    col.Item().Text($"E-mail: {p.Email}").FontSize(7.5f);
                if (!string.IsNullOrEmpty(p.Telefon))
                    col.Item().Text($"Tel.: {p.Telefon}").FontSize(7.5f);
            }

            if (!string.IsNullOrEmpty(p.NrKlienta))
                col.Item().PaddingTop(2).Text($"Nr klienta: {p.NrKlienta}").FontSize(7f).FontColor(Gray);
        });
    }

    // ── Content ──────────────────────────────────────────────────────────────

    private void ComposeContent(IContainer c, InvoiceData d)
    {
        c.Column(col =>
        {
            // Line items
            if (d.Wiersze.Count > 0)
                col.Item().PaddingTop(8).Element(c2 => LinesSection(c2, d));

            // VAT summary + total
            col.Item().PaddingTop(10).Row(row =>
            {
                if (d.VatRows.Count > 0)
                    row.RelativeItem().Element(c2 => VatTable(c2, d));
                else
                    row.RelativeItem();

                if (!string.IsNullOrEmpty(d.RazemBrutto))
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
            });

            // Payment
            bool hasPayment = d.Zaplacono || !string.IsNullOrEmpty(d.DataZaplaty)
                || !string.IsNullOrEmpty(d.FormaPlatnosci) || d.TerminyPlatnosci.Count > 0
                || d.RachunekBankowy is not null;
            if (hasPayment)
                col.Item().PaddingTop(10).Element(c2 => PaymentSection(c2, d));

            // Additional notes
            if (d.DodatkowyOpis.Count > 0)
                col.Item().PaddingTop(10).Element(c2 => DodatkowyOpisSection(c2, d));

            // WZ documents
            if (!string.IsNullOrEmpty(d.WZ))
                col.Item().PaddingTop(10).Element(c2 => WzSection(c2, d));

            // Contracts
            if (d.NrUmowy.Count > 0)
                col.Item().PaddingTop(10).Element(c2 => UmowySection(c2, d));

            // Registries footer
            bool hasRej = !string.IsNullOrEmpty(d.PelnaNazwa) || !string.IsNullOrEmpty(d.Regon)
                || !string.IsNullOrEmpty(d.Bdo);
            if (hasRej)
                col.Item().PaddingTop(10).Element(c2 => RejestryTable(c2, d));
        });
    }

    // ── Line items ───────────────────────────────────────────────────────────

    private void LinesSection(IContainer c, InvoiceData d)
    {
        bool hasUuId  = d.Wiersze.Any(w => !string.IsNullOrEmpty(w.UuId));
        bool hasGross = d.Wiersze.Any(w => !string.IsNullOrEmpty(w.UnitGrossPrice) || !string.IsNullOrEmpty(w.GrossTotal));
        bool hasFx    = d.Waluta != "PLN" && d.Wiersze.Any(w => !string.IsNullOrEmpty(w.ExchangeRate));
        bool hasGtin  = d.Wiersze.Any(w => !string.IsNullOrEmpty(w.Gtin));

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
                    if (hasUuId) cols.ConstantColumn(72); // UU_ID
                    cols.RelativeColumn(3);   // Nazwa
                    cols.ConstantColumn(26);  // Cena netto
                    if (hasGross) cols.ConstantColumn(28); // Cena brutto
                    cols.ConstantColumn(22);  // Ilość
                    cols.ConstantColumn(24);  // Miara
                    cols.ConstantColumn(22);  // Stawka
                    cols.ConstantColumn(32);  // Wart. netto
                    if (hasGross) cols.ConstantColumn(32); // Wart. brutto
                    if (hasFx) cols.ConstantColumn(32);    // Kurs
                });

                table.Header(h =>
                {
                    void Th(string text) =>
                        h.Cell().Background(Blue).Padding(3)
                            .Text(text).FontSize(6.5f).Bold().FontColor(Colors.White);

                    Th("Lp.");
                    if (hasUuId) Th("Unikalny numer wiersza");
                    Th("Nazwa towaru\nlub usługi");
                    Th("Cena\njedn.\nnetto");
                    if (hasGross) Th("Cena jedn.\nbrutto");
                    Th("Ilość");
                    Th("Miara");
                    Th("Stawka\npodatku");
                    Th("Wartość\nsprzedaży netto");
                    if (hasGross) Th("Wartość\nsprzedaży\nbrutto");
                    if (hasFx) Th("Kurs\nwaluty");
                });

                foreach (var (w, i) in d.Wiersze.Select((w, i) => (w, i)))
                {
                    string bg = i % 2 == 0 ? Colors.White : LightGray;
                    void Td(string? text, bool right = false, bool center = false)
                    {
                        var cell = table.Cell().Background(bg)
                            .BorderBottom(0.5f).BorderColor(BorderGray).Padding(3);
                        var txt = cell.Text(text ?? "").FontSize(7.5f);
                        if (right) txt.AlignRight();
                        else if (center) txt.AlignCenter();
                    }

                    Td(w.Nr.ToString(), center: true);
                    if (hasUuId) Td(w.UuId);
                    Td(w.Name);
                    Td(w.UnitNetPrice, right: true);
                    if (hasGross) Td(w.UnitGrossPrice, right: true);
                    Td(w.Qty, right: true);
                    Td(w.Unit, center: true);
                    Td(FormatVatRate(w.VatRate), center: true);
                    Td(w.NetTotal, right: true);
                    if (hasGross) Td(w.GrossTotal, right: true);
                    if (hasFx) Td(w.ExchangeRate, right: true);
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
                    foreach (var (w, i) in d.Wiersze.Select((w, i) => (w, i)))
                    {
                        if (string.IsNullOrEmpty(w.Gtin)) continue;
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

                foreach (var (row, i) in d.VatRows.Select((r, i) => (r, i)))
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
                        left.Item().Text(t =>
                        {
                            t.Span("Informacja o płatności: ").FontSize(7.5f).FontColor(Gray);
                            t.Span("Zapłacono").FontSize(7.5f).Bold().FontColor("#2a7a2a");
                        });
                    if (!string.IsNullOrEmpty(d.DataZaplaty))
                        left.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("Data zapłaty: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.DataZaplaty).FontSize(7.5f);
                        });
                    if (!string.IsNullOrEmpty(d.FormaPlatnosci))
                        left.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("Forma płatności: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(d.FormaPlatnosci).FontSize(7.5f);
                        });
                    foreach (var termin in d.TerminyPlatnosci)
                        left.Item().PaddingTop(1).Text(t =>
                        {
                            t.Span("Termin płatności: ").FontSize(7.5f).FontColor(Gray);
                            t.Span(termin).FontSize(7.5f);
                        });
                });

                if (d.RachunekBankowy is not null)
                {
                    row.ConstantItem(10);
                    row.RelativeItem().Column(right =>
                    {
                        right.Item().Text("Rachunek bankowy").FontSize(7.5f).Bold().FontColor(Gray);
                        if (!string.IsNullOrEmpty(d.RachunekBankowy.NrRB))
                            right.Item().PaddingTop(1)
                                .Text(FormatIban(d.RachunekBankowy.NrRB)).FontSize(7.5f);
                        if (!string.IsNullOrEmpty(d.RachunekBankowy.NazwaBanku))
                            right.Item().Text(d.RachunekBankowy.NazwaBanku).FontSize(7.5f).FontColor(Gray);
                        if (!string.IsNullOrEmpty(d.RachunekBankowy.OpisRachunku))
                            right.Item().Text(d.RachunekBankowy.OpisRachunku).FontSize(7.5f).FontColor(Gray);
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
            foreach (var (klucz, wartosc) in d.DodatkowyOpis)
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
            foreach (var nr in d.NrUmowy)
                col.Item().PaddingTop(2).Text(nr).FontSize(7.5f);
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
                    t.Span($"Wytworzona w: {d.SystemInfo}");
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

    private static string FormatVatRate(string? rate) => rate switch
    {
        "23" => "23%",
        "8"  => "8%",
        "5"  => "5%",
        "0"  => "0%",
        "zw" => "zw",
        "np" => "np",
        null => "—",
        _    => rate + "%"
    };

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
        _    => code
    };

    private static decimal Parse(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

// ── Public entry point ───────────────────────────────────────────────────────

public static class KSeFInvoicePdf
{
    public static byte[] FromXml(string xmlContent, string? colorScheme = null)
    {
        var data = KSeFInvoiceParser.Parse(xmlContent);
        return KSeFInvoicePdfGenerator.Generate(data, PdfColorScheme.FromName(colorScheme));
    }
}
