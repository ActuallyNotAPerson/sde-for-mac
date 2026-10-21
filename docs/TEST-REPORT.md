# Step 4 test report: does the Mac SDE work?

As of 2026-10-20 (v2.0), 577 automated tests pass and 34 are skipped, run with `dotnet test Sde.sln` (about two
minutes). The skipped ones compare against FEMA's own SDE 3.0 and need its installer (`tools/answer-key/setup.sh`);
they last ran, all passing, on macOS with v1.5, and the 2.0 run was on Linux (Ubuntu 24.04) without the installer, so
**run the full suite with the answer key on a Mac before releasing**. v1.0 had 175 tests and v1.5 237 (section "v1.5"
below); what 2.0 adds is in "v2.0: the rest" below.
FEMA's own software running on Windows has not been checked, because none was available; `docs/windows-crosscheck/`
is ready for whoever has it.

## What was checked, against what

| Question | Checked against | Result |
|---|---|---|
| Is the damage math FEMA's? | FEMA's decompiled engine, compiled unchanged, on 31,500 residential and 10,368 non-residential configurations, plus edge cases | Identical: every output, to the last decimal |
| Does it match what FEMA's program displayed? | Screenshots of SDE 3.0 in FEMA's manual (P-784, Aug 2017) | Matches all three worked examples (below) |
| Do the manual's stated rules hold? | Manual §3.11, §3.11.2, §8.4 | Yes, with one clarification (below) |
| Can it read a real FEMA database? | FEMA's empty `SDEDatabase.mdb`, filled with Jackcess | Reads back field for field, then imports with results and photos |
| Are files exchanged with FEMA's SDE in its format? | FEMA's export models (key order, types), byte format | Yes (Step 2 tests); actual import into Windows SDE still to do |
| Do the reports print? | Text extracted from every report PDF, with and without photos | Yes; photos now print upright and print-sized |
| Do spreadsheets, maps and the other import/export tools work? | The original screens' behaviour; FEMA's Excel headers | Yes, with the original's bugs fixed (see PORTING-NOTES) |
| Does the app work as a user would use it? | The real window rendered off-screen, every page and step; the packaged `SDE.app` driven through macOS accessibility | Yes |
| Is it usable with VoiceOver? | Every visible input checked for an accessible name | Yes, after fixing (below) |
| Is it fast enough for a community-wide event? | 1,000 assessments | Yes (below) |

## FEMA's manual as answer key (`tests/Sde.Tests/Calculation/ManualExamplesTests.cs`)

The manual has no numbered worked examples, but its screenshots of SDE 3.0 show real inputs and outputs:

| Example | FEMA's screen | Mac |
|---|---|---|
| Fig. 3-37, residential Element Percentages tab: 12 element shares; replacement cost $324,000; damage of 100 / 5 / 10 / 20 % | 24 element costs and damage values; Computed Damages **$26,179.20** | Every value to the cent |
| Figs. 3-34 and 3-38, Cost and Output Summary tabs: $135.00 × 2,000 sq ft × 1.00; adjusted tax value $137,268 | Cost $270,000.00; Computed Damages $580.50; **0.4 %, Not Substantially Damaged**; Repair/Reconstruction 0.2 % | Identical |
| Fig. 3-43, non-residential Element Percentages tab: 1-story Mini-Warehouse | Shares 13 / 29 / 8 / 11 / 6 / 23 / 10 % | Identical, from the shipped tables |

The two residential screenshots predate the tables shipped in SDE 3.0.0 (April 2018):
- Fig. 3-37's twelve shares are not a row of the shipped `Residential.json`.
- Fig. 3-34 shows "4 - Average Condition" at 0.0 %, but the shipped table gives 24.2 % for every residence. FEMA's own
  `LookupService.GetDepreciationValues` reads the same row.

FEMA's screen only fills the percentage when the rating is changed and otherwise shows the stored value, so the
screenshots show older stored data, not a different calculation. Those two examples are therefore replayed with the
values the screenshots display.

**Rules the manual states, as tested:**
- **"Data Entry Incomplete … most likely the square footage, base cost, or the depreciation rate."**
  - FEMA's Cost tab sends a blank as 0, so missing square footage or base cost gives Data Entry Incomplete, as stated.
    This holds unless cost adjustments alone give the building a value; that is FEMA's behaviour, and the port keeps it.
  - A blank depreciation counts as 0 %. It still gives a percentage, on the undepreciated value, and the validator
    reports it as required data.
- **Damage Undetermined:** 0 % in reports and exports, and fewer required fields.
- **Tax factor 1.43 raises the value by 43 %:** yes.
- **50 % or more is Substantially Damaged:** exactly 50.0 % is. A value of 49.999 % displays as 50.0 % but is not,
  because the determination uses the unrounded ratio, as in FEMA's engine.
- **Field Workbook §8.4, appraisal plus contractor estimate** (base cost $1, depreciation "Other" = 1): a valid
  assessment with no required data missing.

## A populated FEMA database (`tests/Sde.Tests/Data/MdbImportTests.cs`)

FEMA ships `SDEDatabase.mdb` empty. `tools/mdb-fixture` writes 4 structures into a copy of it with Jackcess, the
standard Java library for Access files (Apache-2.0, checksums verified, test tooling only):
- photos, a shape, and custom fields;
- a re-inspection, a non-residential building, a damage-undetermined case, and a structure with no assessment;
- text with quotes, commas, line breaks and accents.

Reading it back through mdbtools gives the source data exactly, across every column type, including mdbtools' odd GUID
format. Importing it recalculates to the same results and copies the photos.

Two things this turned up:
1. **Column defaults.** FEMA's schema gives several columns a default of 0. Jet keeps an explicit NULL, but Jackcess
   substituted the default. Fixed in the fixture writer; the reader was right.
2. **mdbtools reads an empty string as NULL**, in Text and Memo columns alike (mdb-export and mdb-json). An `.mdb` import
   cannot tell `""` from NULL. FEMA's code treats the two the same and Export.json allows null, so this has no
   practical effect; it is documented in the test.

## The app

**Off-screen UI tests (`tests/Sde.App.Tests`, 26 tests).** They render the real window and drive it through the
app's commands:
- every page and every assessment step, in light and dark mode, with no binding errors;
- editing, autosave, switching building type, photos (including iPhone HEIC → JPEG), shapes;
- every report;
- Excel, map, coordinates, Enterprise Import, SDE 2.x import, and settings files.

**Accessibility.** Driving the packaged `SDE.app` through macOS accessibility found that inputs had **no accessible
names**:
- the sidebar read "Avalonia.Controls.StackPanel";
- the fields were unlabelled, so VoiceOver could not say what a box was for.

Fixed for every field, row and button, including the text box inside number and date fields, which is the element
macOS exposes. `AccessibilityTests` now fails if any visible input lacks a name; a mutation check confirmed it catches
the gap.

**Packaged app run-through** (`out/osx-arm64/SDE.app` on a scratch database, controlled in the background):
- launched and opened a new non-residential structure;
- typed the address and year;
- moved by Tab and chose drop-down values by keyboard;
- watched the title, step status and "Needs attention" list update live;
- the values were in the database through autosave.

The **Intel build** (`out/osx-x64/SDE.app`) was launched under Rosetta ("X86-64 (translated)") on a copy of the sample
database and showed the three structures with their results. An Intel Mac itself has not been tried.

The run stopped early, once macOS reported SDE as the frontmost app, to avoid fighting the user's input. Report
saving through the macOS save panel and the **File/View menus** were therefore not exercised. The menus only appear
once the window is activated, which a background run never does: **check them by hand.**

**Scale** (1,000 assessments, M-series Mac):

| Operation | Time |
|---|---|
| Structures list load / search / open an assessment | 42 ms / 9 ms / 102 ms |
| One edit, including recalculation | 1 ms |
| Export to SDE folder (10.7 MB Export.json) / import into a new database | 0.2 s / 0.9 s |
| Excel export | 0.9 s |
| Summary report, 200 pages without photos (810 KB) | 1.0 s |
| Percent-damaged list, 1,000 rows | 0.25 s |

## v1.5: Accela import and the submission folder

There is no real Accela export yet, so these checks use synthetic exports (`tests/fixtures/accela/`): one with a row
per inspection, and the same inspections as a checklist with a row per item, including RanchFlood and a checklist
item answered twice.

| Question | Checked against | Result |
|---|---|---|
| Does an inspection entered through Accela calculate like one typed in? | RanchFlood (`tests/cases/ranch-flood.json`) built by hand vs. the same building as an Accela row (`SourceImportTests`) | Same structure fields, element costs, ACV, damages, 75.0 %, Substantially Damaged. RanchFlood's garage and shed aren't in Accela, so both sides leave them out |
| Do Accela's own value and repair figures work? | Market value $200,000 and contractor's estimate $110,000 with bases 391/394 | Exactly 55.0 %, Substantially Damaged |
| Can the import set a value FEMA's engine rejects? | Every residence × foundation, every superstructure, roof, exterior, HVAC, condition, cause and reason, and every non-residential stories × use × condition FEMA allows | All calculate; none is a problem row |
| Is a re-import safe? | Import, edit in SDE, change one cell and blank another in Accela, import again | Only the changed field updates; the SDE edit stays; the blank clears nothing; the same file twice writes nothing (LastUpdate unchanged) |
| Do checklist exports give the same assessments? | The checklist fixture vs. the wide one | Same statuses and results; the later of two answers is used, with a warning |
| Do 1.0 and 1.5 share a database? | The released 1.0 `sde` tool (built from tag `v1.0`) on a 1.5 database with links, and 1.5 opening a 1.0 database | 1.0 opens, lists and exports; 1.5 adds the table and nothing else changes; deleting a linked assessment the way 1.0 does works |
| Is the FEMA package unchanged by links? | Export.json bytes with and without a link | Identical |
| Is the submission folder complete and usable? | `SubmissionFolderTests`; `SDE_1` imported into a fresh database | Reports named by record, both lists, `SDE_1` (same results after import), `results.csv` agreeing with the reports; incomplete left out; cancelling leaves nothing |
| Does the app do it all? | `SourceImportUiTests`, `SubmissionUiTests`, `AccessibilityTests` | Map → match → preview → import → review → submission folder, with every new input named |

**End to end outside the tests** (a script against the projects): import the wide fixture into a new database (3 new
structures at 75.0 / 59.3 / 41.6 %, 1 row without a record ID refused), import again (all unchanged), write a
submission folder, and import its `SDE_1` with the **1.0** `sde` tool into another new database: the same three
results.

**Packaged 1.5.0 apps**, launched in the background on a copy of that database: the arm64 build and the Intel build
under Rosetta both open and list the three Accela structures with their record IDs and the source filter.

## v2.0: importing inspections or parcels from any source

v1.5's Accela import became one vendor-neutral importer (`docs/IMPORTING.md`). The checks use synthetic files in
`tests/fixtures/import/` (a spreadsheet of inspections, an assessor roll and an Accela-style report, all in a made-up
"Exampleville") and the v1.5 fixtures.

| Question | Checked against | Result |
|---|---|---|
| Do v1.5 profiles still work? | A v1.5-shaped profile JSON (`SourceMatchingTests`); v1.5's "Accela Profiles" folder (`SourceImportUiTests`) | Loads with no warnings as Inspections from "Accela", matching by record ID; the folder is copied once to "Import Profiles" and left in place |
| Do the examples fit their files? | "Generic inspections", "County parcels", "Accela (example)" on their fixtures | No mapping problems (the generic one asks about the building value first), every row importable |
| Can a parcel list create and update structures? | The assessor roll imported, re-imported unchanged, then with one cell changed (`SourceImportModesTests`) | 4 structures linked in StructureLink; the same file writes nothing; only the changed field updates and an edit made in SDE stays |
| Are new records matched without guessing? | Parcel numbers written differently, two structures with one parcel, an address shared by two parcels, leading zeros, units, suffix spellings | One match links or attaches; several are shown as Ambiguous with the structures listed and nothing imported |
| Is the building value the building only (Flag 4)? | A value column used with Adj. Tax Assessed Value | Refused with a plain-English mapping problem until confirmed; the page asks, and the answer is kept in the profile |
| Are Final assessments left alone? | An update to a Final assessment, in the preview and made Final between preview and import | "Final, won't change" in the preview; skipped by the store and reported in the result |
| Is each import one history batch? | Edit history after importing inspections and structures | One batch, an "Imported from <file>" event per assessment or structure, field changes on updates |
| Does the app do it all? | `SourceImportUiTests`, `AccessibilityTests` | Parcels as structures, then inspections by address with the building-value question and an ambiguous row; every input named in both modes |

## v2.0: the rest

| Area | Tests | What they establish |
|---|---|---|
| Compatibility | `CompatibilityTests`, `ExternalLinkTests`, `HistoryTests.MacTables_NeverReachTheExport`, `FieldDataTests` | With every Mac-only table filled in, Export.json is byte-identical; v1.5's hard delete still works in a 2.0 database (the Mac-only rows cascade); reopening a 2.0 database changes nothing; photo reordering keeps the export's shape |
| Edit history and locks | `HistoryTests` | Who changed what, merged edits within 10 minutes, batches never merged; Final refuses changes but allows unchanged saves; reopening needs a reason; edits by an older version are detected; imports are one batch and skip Final |
| Recently deleted and backups | `TrashAndBackupTests`, `DataSafetyUiTests` | Soft delete with FEMA's Active flag hides rows everywhere; restore brings back what was deleted together; purge after 30 days with a test clock removes rows and photo files; imports replace trashed rows with the same IDs; backups keep 14, restore backs up first |
| Changing many at once | `BulkTests`, `ListPowerUiTests` | Bulk edit follows the editor's rules, skips Final, reverts except values changed since; search and replace changes only the match (FEMA lowercased the rest); copy-from never copies location, owner or damage |
| The list | `ListPowerUiTests`, `KeyboardTests` | Sorting is the report order; filters and saved views are remembered per database; the keyboard shortcuts work through the window's key bindings |
| Field data | `FieldDataTests`, `FieldDataUiTests`, `OnlineClientTests`, `PhotoTests`, `XlsAndDbfTests` | Photo order is report order; EXIF date and GPS offered, never applied silently; online look-ups from recorded responses (no network in tests); depth suggestions block Final until accepted; .xls and .dbf read like .xlsx |
| Outputs | `MapDashboardUiTests`, `GisExportTests` (with `ogrinfo`), `EventSummaryReportTests`, `LetterTests`, `SiDeterminationReportTests`, `ImprovementCalculatorTests`, `CumulativeDamageTests`, `OutputsUiTests` | GeoPackage opens in GDAL; letters merge placeholders split across runs; SI and cumulative arithmetic at the thresholds; appeal what-ifs equal the engine; FEMA's percent and determination never change |
| Phone form | `Sde.Field.Tests` (real Kestrel on loopback) | Pairing by one-time token and PIN with lockout, cookie and CSRF header required, wrong Host refused, only private addresses served, uploads capped and type-checked, changes saved through the editor with Via Phone |
| Help | `HelpUiTests` | Every field has help with a manual page; the excerpt PDF is written; the sample database is marked and warns before exporting |
| Accessibility | `AccessibilityTests` and a check in each new UI test | Every input on every new page and sheet has an accessible name |

## Not covered
- **Real exports.** The column guesses, header names and the example profiles need real (anonymised) files; see `docs/IMPORTING.md`.
- **File → Import Inspections or Parcels…** in the menu bar (like the other menus, check by hand).
- **FEMA's SDE on Windows**: importing our files, entering the same cases, and exporting back. Use
  `docs/windows-crosscheck/README.md`, which takes about an hour.
- **The File/View menus and the macOS save panel**, by hand.
- **Importing `.mdb` files on a Mac that has never had Homebrew.** The packaged app now carries its own mdbtools
  (`tools/packaging/build_mdbtools.sh`). On the build Mac, both architectures' copies give byte-identical output to
  Homebrew's mdbtools on every table of the test database and of FEMA's shipped database, and SDE's `.mdb` import ran
  through them with Homebrew left out of the search.
- **Live online services** (Census geocoder, FEMA NFHL): the tests use recorded responses. See `docs/ONLINE-SERVICES.md`.
- **The phone form on a real phone**, the map with live tiles, drag-and-drop with a real mouse, and real camera photos
  (EXIF). See the "check on a Mac" lists in `docs/PHONE-FORM.md` and `docs/ONLINE-SERVICES.md`.
