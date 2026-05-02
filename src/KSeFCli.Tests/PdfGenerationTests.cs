using System.Reflection;

using Xunit;

namespace KSeFCli.Tests;

public class PdfGenerationTests
{
    private static string LoadSampleXml()
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string path = Path.Combine(dir, "TestData", "sample-invoice.xml");
        return File.ReadAllText(path);
    }

    private static bool IsPdfHeader(byte[] pdf) =>
        pdf.Length >= 4 &&
        pdf[0] == (byte)'%' && pdf[1] == (byte)'P' &&
        pdf[2] == (byte)'D' && pdf[3] == (byte)'F';

    // ── PDF byte sanity ───────────────────────────────────────────────────────

    [Fact]
    public void GeneratePdf_DefaultScheme_ReturnsPdfBytes()
    {
        byte[] pdf = KSeFInvoicePdf.FromXml(LoadSampleXml());
        Assert.True(IsPdfHeader(pdf), "Expected %PDF header");
        Assert.True(pdf.Length > 1024, "PDF should be larger than 1 KB");
    }

    [Theory]
    [InlineData("navy")]
    [InlineData("forest")]
    [InlineData("slate")]
    public void GeneratePdf_NamedScheme_ReturnsPdfBytes(string scheme)
    {
        byte[] pdf = KSeFInvoicePdf.FromXml(LoadSampleXml(), scheme);
        Assert.True(IsPdfHeader(pdf), $"Expected %PDF header for scheme '{scheme}'");
        Assert.True(pdf.Length > 1024);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-scheme")]
    public void GeneratePdf_FallbackScheme_ReturnsPdfBytes(string? scheme)
    {
        byte[] pdf = KSeFInvoicePdf.FromXml(LoadSampleXml(), scheme);
        Assert.True(IsPdfHeader(pdf));
        Assert.True(pdf.Length > 1024);
    }

    [Fact]
    public void GeneratePdf_WithKsefReferenceNumber_ReturnsPdfBytes()
    {
        byte[] pdf = KSeFInvoicePdf.FromXml(
            LoadSampleXml(), null, "9999999999-20240315-ABCDEF123456-01");
        Assert.True(IsPdfHeader(pdf), "Expected %PDF header when KSeF number is provided");
        Assert.True(pdf.Length > 1024);
    }

    [Fact]
    public void GeneratePdf_WithoutKsefReferenceNumber_ReturnsPdfBytes()
    {
        byte[] pdf = KSeFInvoicePdf.FromXml(LoadSampleXml(), null, null);
        Assert.True(IsPdfHeader(pdf), "Expected %PDF header when KSeF number is omitted");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void FromXml_WhitespaceOnlyKsefReferenceNumber_TreatedAsNull(string whitespace)
    {
        // Whitespace-only inputs must be normalized to null — the PDF must be identical to passing null.
        byte[] pdfWithWhitespace = KSeFInvoicePdf.FromXml(LoadSampleXml(), null, whitespace);
        byte[] pdfWithNull = KSeFInvoicePdf.FromXml(LoadSampleXml(), null, null);
        Assert.True(IsPdfHeader(pdfWithWhitespace));
        // Byte-equality is fragile because the PDF embeds a creation timestamp.
        // Length equality is sufficient: a PDF with a KSeF reference number is strictly
        // longer, so equal length confirms whitespace was treated as null.
        Assert.Equal(pdfWithNull.Length, pdfWithWhitespace.Length);
    }

    [Fact]
    public void FromXml_OversizedKsefReferenceNumber_TruncatesAndReturnsPdf()
    {
        // A string longer than 256 chars must be truncated — the result must still be a valid PDF.
        string oversize = new('X', 300);
        string expectedTruncated = oversize.Substring(0, 256);
        byte[] pdfFromOversize = KSeFInvoicePdf.FromXml(LoadSampleXml(), null, oversize);
        byte[] pdfFromTruncated = KSeFInvoicePdf.FromXml(LoadSampleXml(), null, expectedTruncated);
        Assert.True(IsPdfHeader(pdfFromOversize));
        Assert.True(pdfFromOversize.Length > 1024);
        // Byte-equality is fragile due to embedded PDF creation timestamps; length equality
        // is sufficient because both inputs truncate to the same 256-char string.
        Assert.Equal(pdfFromTruncated.Length, pdfFromOversize.Length);
    }

    [Fact]
    public void GeneratePdf_WithKsefVerificationUrl_IsLargerThanWithout()
    {
        // A QR code image is embedded when a KSeF verification URL is provided;
        // the resulting PDF must be strictly larger than one without it.
        const string VerificationUrl = "https://qr.ksef.mf.gov.pl/invoice/9999999999/15-03-2024/AAABBBCCC";
        byte[] pdfWithQr = KSeFInvoicePdf.FromXml(
            LoadSampleXml(), null, "9999999999-20240315-ABCDEF123456-01", VerificationUrl);
        byte[] pdfWithout = KSeFInvoicePdf.FromXml(LoadSampleXml(), null, null, null);
        Assert.True(
            pdfWithQr.Length > pdfWithout.Length,
            $"Expected PDF with QR ({pdfWithQr.Length} B) to be larger than without ({pdfWithout.Length} B)");
    }

    // ── Parser field coverage ─────────────────────────────────────────────────

    [Fact]
    public void ParseInvoice_HeaderFields_AreCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal("VAT", d.RodzajFaktury);
        Assert.Equal("FV/2024/03/001", d.Numer);
        Assert.Equal("2024-03-15", d.DataFaktury);
        Assert.Equal("Warszawa", d.MiejsceWystawienia);
        Assert.Equal("2024-03-15", d.DataDostawy);
        Assert.Equal("PLN", d.Waluta);
        Assert.Equal("TestSystem v1.0", d.SystemInfo);
    }

    [Fact]
    public void ParseInvoice_KsefReferenceNumber_IsNullFromXml()
    {
        // KSeF number is not in the XML — it comes from API metadata.
        // Parser must always return null; FromXml injects it afterwards.
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Null(d.KsefReferenceNumber);
    }

    [Fact]
    public void ParseInvoice_Seller_IsCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal("1234567890", d.Sprzedawca.Nip);
        Assert.Equal("Testowa Firma Sp. z o.o.", d.Sprzedawca.Nazwa);
        Assert.Equal("PL", d.Sprzedawca.KodKraju);
        Assert.Equal("ul. Przykładowa 1", d.Sprzedawca.AdresL1);
        Assert.Equal("00-001 Warszawa", d.Sprzedawca.AdresL2);
        Assert.Equal("kontakt@testowafirma.example", d.Sprzedawca.Email);
        Assert.Equal("+48 123 456 789", d.Sprzedawca.Telefon);
    }

    [Fact]
    public void ParseInvoice_Buyer_IsCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal("0987654321", d.Nabywca.Nip);
        Assert.Equal("Nabywca Testowy S.A.", d.Nabywca.Nazwa);
        Assert.Equal("ul. Fikcyjna 42", d.Nabywca.AdresL1);
        Assert.Equal("31-001 Kraków", d.Nabywca.AdresL2);
        Assert.Equal("faktury@nabywca.example", d.Nabywca.Email);
        Assert.Equal("KLIENT-001", d.Nabywca.NrKlienta);
    }

    [Fact]
    public void ParseInvoice_LineItems_AreCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal(3, d.Wiersze.Count);

        InvoiceLine line1 = d.Wiersze[0];
        Assert.Equal(1, line1.Nr);
        Assert.Equal("Usługa programistyczna — moduł A", line1.Name);
        Assert.Equal("godz", line1.Unit);
        Assert.Equal("10", line1.Qty);
        Assert.Equal("150.00", line1.UnitNetPrice);
        Assert.Equal("1500.00", line1.NetTotal);
        Assert.Equal("23", line1.VatRate);

        InvoiceLine line3 = d.Wiersze[2];
        Assert.Equal(3, line3.Nr);
        Assert.Equal("zw", line3.VatRate);
    }

    [Fact]
    public void ParseInvoice_VatRows_AreCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.NotEmpty(d.VatRows);

        VatRow vat23 = Assert.Single(d.VatRows, v => v.Rate == "23%");
        Assert.Equal("3900.00", vat23.Net);
        Assert.Equal("897.00", vat23.Vat);

        // P_13_7 = "niepodlegający opodatkowaniu" (np), not "zwolniony" (zw=P_13_6)
        VatRow vatNp = Assert.Single(d.VatRows, v => v.Rate == "np");
        Assert.Equal("1000.00", vatNp.Net);
        Assert.Null(vatNp.Vat);
    }

    [Fact]
    public void ParseInvoice_Totals_AreCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal("5897.00", d.RazemBrutto);
    }

    [Fact]
    public void ParseInvoice_Payment_IsCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal("Przelew", d.FormaPlatnosci);
        Assert.Single(d.TerminyPlatnosci, "2024-03-29");
        Assert.NotNull(d.RachunekBankowy);
        Assert.Equal("12345678901234567890123456", d.RachunekBankowy!.NrRB);
        Assert.Equal("Bank Testowy S.A.", d.RachunekBankowy.NazwaBanku);
    }

    [Fact]
    public void ParseInvoice_AdditionalDescriptions_AreCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Single(d.DodatkowyOpis);
        Assert.Equal("Zamówienie", d.DodatkowyOpis[0].Klucz);
        Assert.Equal("ZAM/2024/02/099", d.DodatkowyOpis[0].Wartosc);
    }

    [Fact]
    public void ParseInvoice_Registry_IsCorrect()
    {
        InvoiceData d = KSeFInvoiceParser.Parse(LoadSampleXml());
        Assert.Equal(
            "Testowa Firma Spółka z ograniczoną odpowiedzialnością",
            d.PelnaNazwa);
        Assert.Equal("123456789", d.Regon);
        Assert.Equal("000012345", d.Bdo);
    }

    // ── Render scenarios ──────────────────────────────────────────────────────

    public static IEnumerable<object[]> RenderScenarios() =>
    [
        ["sample-invoice.xml",               "PLN invoice — baseline"],
        ["invoice-foreign-currency-eur.xml", "EUR invoice with VatW column"],
        ["invoice-podmiot3.xml",             "Invoice with Podmiot3 (third party)"],
        ["invoice-reverse-charge.xml",       "Reverse charge + self-billing flags (P_16/P_17)"],
        ["invoice-multi-vat-rates.xml",      "Multiple VAT rates (23%, 8%, 5%, np)"],
        ["invoice-correction.xml",           "Correction invoice (KOR) with negative amounts"],
    ];

    [Theory]
    [MemberData(nameof(RenderScenarios))]
    public void GeneratePdf_Scenario_ReturnsPdfBytes(string xmlFile, string description)
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string xml = File.ReadAllText(Path.Combine(dir, "TestData", xmlFile));
        byte[] pdf = KSeFInvoicePdf.FromXml(xml);
        Assert.True(IsPdfHeader(pdf), $"Scenario '{description}': expected %PDF header");
        Assert.True(pdf.Length > 1024, $"Scenario '{description}': PDF too small ({pdf.Length} bytes)");
    }

    // ── Currency rendering ────────────────────────────────────────────────────

    public static IEnumerable<object[]> ForeignCurrencies() =>
    [
        // Common European / Western
        ["USD"], ["GBP"], ["CHF"], ["CZK"], ["HUF"],
        ["DKK"], ["SEK"], ["NOK"], ["JPY"], ["CAD"],
        // Exotic — long display names, non-Latin regions, unusual exchange rates
        ["BHD"],  // Bahraini dinar — 3 decimal places
        ["KWD"],  // Kuwaiti dinar — highest-valued currency
        ["IQD"],  // Iraqi dinar — very large nominal values
        ["IDR"],  // Indonesian rupiah — 5-digit amounts common
        ["VND"],  // Vietnamese dong — 6-digit amounts common
        ["XAU"],  // Gold (troy ounce) — commodity pseudo-currency
        ["XDR"],  // IMF Special Drawing Rights
        ["XXX"],  // No currency / unspecified
    ];

    private static string BuildForeignCurrencyXml(string currency) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Faktura xmlns="http://crd.gov.pl/wzor/2025/06/25/13775/">
          <Naglowek>
            <KodFormularza kodSystemowy="FA (2)" wersjaSchemy="1-0E">FA</KodFormularza>
            <WariantFormularza>2</WariantFormularza>
            <DataWytworzeniaFa>2024-05-01T10:00:00</DataWytworzeniaFa>
            <SystemInfo>CurrencyTest</SystemInfo>
          </Naglowek>
          <Podmiot1>
            <DaneIdentyfikacyjne><NIP>1234567890</NIP><Nazwa>Sprzedawca Sp. z o.o.</Nazwa></DaneIdentyfikacyjne>
            <Adres><KodKraju>PL</KodKraju><AdresL1>ul. Testowa 1</AdresL1><AdresL2>00-001 Warszawa</AdresL2></Adres>
          </Podmiot1>
          <Podmiot2>
            <DaneIdentyfikacyjne><NIP>9876543210</NIP><Nazwa>Buyer Corp.</Nazwa></DaneIdentyfikacyjne>
            <Adres><KodKraju>PL</KodKraju><AdresL1>ul. Nabywców 2</AdresL1><AdresL2>31-001 Kraków</AdresL2></Adres>
          </Podmiot2>
          <Fa>
            <KodWaluty>{currency}</KodWaluty>
            <P_KursWaluty>4.2500</P_KursWaluty>
            <P_1>2024-05-01</P_1>
            <P_2>FV/TEST/{currency}/001</P_2>
            <RodzajFaktury>VAT</RodzajFaktury>
            <FaWiersz>
              <NrWierszaFa>1</NrWierszaFa>
              <P_7>Test service — {currency}</P_7>
              <P_8A>szt</P_8A><P_8B>1</P_8B>
              <P_9A>1000.00</P_9A>
              <P_11>1000.00</P_11>
              <P_12>23</P_12>
            </FaWiersz>
            <P_13_1>1000.00</P_13_1>
            <P_14_1>4255.00</P_14_1>
            <P_14_1W>230.00</P_14_1W>
            <P_15>1230.00</P_15>
            <Platnosc><FormaPlatnosci>6</FormaPlatnosci></Platnosc>
          </Fa>
        </Faktura>
        """;

    [Theory]
    [MemberData(nameof(ForeignCurrencies))]
    public void GeneratePdf_ForeignCurrency_ReturnsPdfBytes(string currency)
    {
        byte[] pdf = KSeFInvoicePdf.FromXml(BuildForeignCurrencyXml(currency));
        Assert.True(IsPdfHeader(pdf), $"Currency {currency}: expected %PDF header");
        Assert.True(pdf.Length > 1024, $"Currency {currency}: PDF too small ({pdf.Length} bytes)");
    }

    [Fact]
    public void ParserAndSanitizer_PreserveKsefReferenceNumber()
    {
        // Verify the number survives the Sanitize step and arrives at the generator.
        // We test via the parser+sanitize pipeline rather than byte-scanning the PDF.
        string ksefNumber = "1234567890-20240315-TESTTEST-01";
        InvoiceData parsed = KSeFInvoiceParser.Parse(LoadSampleXml());
        InvoiceData sanitized = KSeFInvoiceSanitizer.Sanitize(
            LoadSampleXml(), parsed with { KsefReferenceNumber = ksefNumber });
        // SanitizeData uses `d with { ... }` and does not touch KsefReferenceNumber,
        // so it must be preserved unchanged.
        Assert.Equal(ksefNumber, sanitized.KsefReferenceNumber);
    }
}
