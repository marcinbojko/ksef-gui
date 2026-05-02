# Changelog

All notable changes to this project will be documented in this file.

## [0.6.2] — unreleased

### ⚠️ Breaking Changes

- **Download directory structure changed.** Invoices are now saved to a subject-type subfolder: `output-dir/[NIP/]sprzedawca|nabywca|podmiot3|uprawniony/`. Previously files landed directly in `output-dir/` or `output-dir/NIP/`. Existing downloaded files will not be detected as present (table icons will be missing) until moved to the new subfolder or re-downloaded.

### Added
- Save path now includes subject-type subfolder: `outputDir/[NIP/]sprzedawca|nabywca|podmiot3|uprawniony/`

---

## [0.6.1] — 2025-05-02

### Added
- NBP exchange rate integration: currency chips show live mid-rate from NBP Table A (1 h cache)
- PLN conversion in bar chart labels: `≈ netto: X PLN / brutto: Y PLN` per currency
- PLN summary row below chart, reactive to active currency filter; `~` prefix for approximate values
- Net+VAT stacked bars: coloured net segment + grey VAT segment, proportionally scaled against largest gross
- `Podmiot3` (third party) and `PodmiotUpowazniony` (authorised entity) rendered in PDF
- Dynamic VAT rate discovery: all `P_13_*` / `P_14_*` fields scanned automatically — no hardcoded rate list
- `P_14_*W` column in VAT table (foreign-currency VAT), shown only when present
- `P_KursWaluty` — invoice-level exchange rate in PDF Details section
- `P_16` (reverse charge), `P_17` (self-billing), `P_18` (margin scheme), `P_18A` (new transport) — highlighted annotations in PDF
- `P_19` / `P_19A` / `P_19B` — VAT exemption basis, legal reference, description in PDF
- `WarunkiTransakcji`: `NrZamowienia`, `NrPartiiDostawy`, `Incoterms` in PDF
- Invoice table columns: **Kwota netto** and **VAT** (`brutto − netto`) alongside Kwota brutto — all sortable
- NBP FX fetch state flags: separate `fxRatesFetchInProgress` / `fxRatesFetchFailed` — summary shows correct message per state

### Fixed
- Chart proportionality: VAT segment scales relative to rendered net bar, preserving ratio for clamped small currencies
- Correction invoices (negative net) render muted red bar; no fake net segment when net = 0
- VAT table brutto uses `P_14_*W` (invoice currency) instead of PLN VAT for foreign-currency invoices
- `SavePrefs` always stores base output directory, preventing path duplication on repeated downloads
- PLN summary no longer shows `0 PLN` when all selected non-PLN currencies lack exchange rates
- VAT sort column treats missing amounts as `null` (sorts separately) rather than `0`
- `silentRefresh` now triggers `refreshExchangeRates` so PLN conversions stay fresh in background

### Changed
- Chart title is dynamic: *Przychody* for Subject1, *Koszty* for Subject2, *Kwoty* for others
- Bars scale against max gross so the largest bar fills 100% without clipping its VAT segment
- VAT rows ordered numerically by suffix (`P_13_10` after `P_13_9`, not before `P_13_2`)
- P_13_7 correctly mapped to `np` (niepodlegający), not `zw` (zwolniony = P_13_6)
- Correction invoices now symmetrically reduce both `netTotals` and `vatTotals`
- VAT table PLN column header is `VAT (PLN)` when foreign-currency VAT column is also present

---

## [0.6.0] — 2025-04-09

### Added
- Horizontal bar chart showing net totals per currency (opt-out in Preferences)
- VAT segment in currency bars (grey, proportional)
- Dynamic chart title based on subject type

---

## [0.5.9] — 2025-04-07

### Changed
- Renamed `offset` variable to `pageNumber` to reflect correct KSeF API semantics

---

## [0.5.8] — 2025-04-06

### Fixed
- Handle KSeF error 21405 (page offset out of range) with user-friendly warning
- Improved error UX for API limit errors

---

## [0.5.7] — 2025-04-04

### Changed
- Bumped all GitHub Actions to latest versions

---

## [0.5.6] — 2025-03-31

### Added
- Toast notifications for search results and background events
- Income chart (net per currency) in the filter bar

---

## [0.5.5] — 2025-03-28

### Added
- KSeF API 10 000-result limit detection with truncation warning and UI message

---

## [0.5.4] — 2025-03-25

### Fixed
- Active profile cache now syncs correctly after background refresh

---

## [0.5.3] — 2025-03-22

### Fixed
- Prevent double notifications for the active profile during background refresh

---

## [0.5.2] — 2025-03-19

### Added
- Monthly invoice summary CSV export (UTF-8 BOM, `;` separator, per-currency totals)

---

## [0.5.1] — 2025-03-15

### Fixed
- Font rendering improvements in PDF output
- Better error handling for malformed XML invoices

---

## [0.5.0] — 2025-03-12

### Added
- KSeF reference number displayed in PDF (injected from API response, not XML)
- QR code (KOD I) in PDF: `https://qr.ksef.mf.gov.pl/invoice/{nip}/{date}/{hash}`

---

## [0.4.2] — 2025-03-08

### Added
- Retry mechanism for failed webhook/email notifications with persistent queue

---

## [0.4.1] — 2025-03-05

### Fixed
- Token handling improvements: correct refresh timing and expiry logic

---

## [0.4.0] — 2025-03-02

### Added
- Auto-refresh date range controls per profile (limit to current month option)

---

## [0.3.9] — 2025-02-26

### Fixed
- Escape special Markdown characters in Slack notification messages

---

## [0.3.8] — 2025-02-24

### Added
- SMTP e-mail notifications (STARTTLS, port 587) per profile
- Extended notifications with invoice details (date, NIP, seller name)

---

## [0.3.7] — 2025-02-20

### Changed
- Improved token management flow: proactive expiry, single refresh path

---

## [0.3.6] — 2025-02-18

### Fixed
- Preferences modal UX: data loss on cancel prevented, validation improved

---

## [0.3.5] — 2025-02-16

### Added
- Webhook notifications documentation

---

## [0.3.4] — 2025-02-13

### Fixed
- Path traversal vulnerability in file serving endpoint

---

## [0.3.3] — 2025-02-11

### Changed
- Improved profile deletion UX with confirmation dialog

---

## [0.3.2] — 2025-02-09

### Added
- Docker image published to GitHub Container Registry (GHCR)

---

## [0.3.1] — 2025-02-06

### Added
- Comprehensive invoice XML security validation (XSD, element limits, size cap)

---

## [0.3.0] — 2025-02-04

### Added
- SQLite invoice cache with background auto-refresh every N minutes
- Per-profile opt-in for background refresh
- OS desktop notifications, Slack and Teams webhook notifications

---

## [0.2.7] — 2025-01-30

### Changed
- Test infrastructure improvements, code quality fixes

---

## [0.2.6] — 2025-01-28

### Fixed
- 100 unit tests passing; exposed internals for testing; corrected routes, exception types, sanitization

---

## [0.2.5] — 2025-01-24

### Added
- Docker production stack with Traefik reverse proxy and Ofelia scheduler

---

## [0.2.4] — 2025-01-21

### Changed
- Improved logging, documentation, privacy handling (no PII in logs)

---

## [0.2.3] — 2025-01-18

### Changed
- Improved logging and localization for GUI interface

---

## [0.2.2] — 2025-01-15

### Changed
- Removed unused submodules (ksef-docs, ksef-pdf-generator)

---

## [0.2.1] — 2025-01-14

### Fixed
- Release workflow trigger: match non-`v`-prefixed tags

---

## [0.2.0] — 2025-01-12

### Changed
- Replaced external Node.js PDF generator with native QuestPDF renderer (no external dependencies)

---

## [0.1.4] — 2025-01-09

### Fixed
- Cache corruption prevention on profile switch failure
- Profile rollback on switch error

---

## [0.1.3] — 2025-01-07

### Fixed
- PDF generation error handling on Windows

---

## [0.1.2] — 2025-01-06

### Fixed
- Node.js path parsing on Windows

---

## [0.1.1] — 2025-01-05

### Fixed
- Resource leaks in scope recreation
- GUI config error handling improvements

---

## [0.1.0] — 2025-01-04

### Added
- Initial public release
- Browser-based GUI with embedded HTTP server
- Multi-profile support with YAML configuration
- Certificate-based authentication
- CI/CD workflows (GitHub Actions), build metadata injection
- Windows, Linux, macOS builds
