# Porting notes: FEMA SDE 3.0 → macOS (.NET 10)

This file is the record of what changed between FEMA's code (decompiled into `decomp/`) and the Mac port in `src/`, and why. Anything not listed here behaves as the original did.

## Layout

| FEMA assembly | Mac project | Notes |
|---|---|---|
| `CalculationEngine.dll` | `src/Sde.Calculation` | Faithful port. The arithmetic is copied verbatim. |
| (answer key) | `reference/OriginalCalc` | Compiles `decomp/CalculationEngine/**.cs` **in place, unedited**. Used only by tests. |
| `BLL.dll` + calculation wiring from `wpfSDE.exe` | `src/Sde.Core` | Records, the calculation bridge, validation, defaults, and the Export.json format. No UI and no database code. |
| `DAL.dll` | `src/Sde.Data` | SQLite through Microsoft.Data.Sqlite and Dapper 2. Rewritten rather than translated. |
| `wpfSDE.exe` (UI) | `src/Sde.Cli` now; Avalonia app in Step 3 | |

**Not ported (Windows-only, commercial).** DevExpress 15.2, Telerik, Xceed, Microsoft ReportViewer, Z.Dapper.Plus (shipped but never referenced), AForge (webcam), ACE/Jet OLEDB.

**Open-source libraries in use:** Newtonsoft.Json 13 (the same family SDE used), Dapper 2, and Microsoft.Data.Sqlite.

---

## Calculation (`Sde.Calculation`)

**Verbatim.** Every method of `Calculations.cs` keeps the original statements, including the decompiler's nullable-decimal forms, so decimal results match to the last digit *and* scale.

**Changes:**
1. **Data loading.**
   - The original read `AppDomain.BaseDirectory + "JSON\\Lookup.json"` (and `"JSON\\REsidential.json"`) on every cold start: Windows backslashes, odd casing, and a 90 MB file.
   - The port embeds compact tables built by `tools/build_calc_tables.py`:
     - `residential.tsv.gz` (849 KB)
     - `commercial.tsv.gz`
     - FEMA's `Lookup.json`
   - Rows are kept exactly when `Element != ""` and stay in file order. Percentages are kept as FEMA's original strings, so the decimal scale is preserved (`13.0` stays `13.0`).
   - `ElementTables.LoadFemaJson(dir)` can still load FEMA's original JSON files (for example if FEMA updates them).
2. **Lookup index.** The original used a linear LINQ `Where` over 283,500 rows on every calculation (about 1 ms). The port uses a dictionary keyed on the same 7 or 4 IDs, with rows kept in file order. The result lists are identical.
3. **Missing lookup ID.** When a lookup ID is not in `Lookup.json`, the original threw `NullReferenceException`; the port throws `InvalidOperationException` with a message saying which ID it was.
4. **Thread safety.** The static caches are now created once in a thread-safe way (`Lazy<T>`).

**Original quirks kept on purpose (the app's results must match FEMA's):**
- `≥ 50%` is "Substantially Damaged". The second check (`< 50`) overwrites the first, so moving only the first threshold would change nothing.
- PercentDamaged is capped at 1.0 *after* the determination is made. Undetermined damage then overrides the result to 0% plus the message.
- An empty `ElementPercentages` list builds default rows and then discards them (so computed damages = 0). `Sde.Core` always passes the visible element rows, just as the original UI did, so the app never hits this.
- `PercentOfExistingImprovements` is accepted but never used in any formula.
- Element names are matched case-insensitively: the UI sends "HVAC" and the residential table says "Hvac".

**Verification.** `tests/Sde.Tests/Calculation/CalculationParityTests.cs` runs the port and the untouched original side by side and requires byte-identical JSON output:
- 31,500 residential selections: every combination of residence × foundation × superstructure × roof × exterior × HVAC × stories, covering all 23,625 table rows, each with a random scenario.
- 10,368 non-residential scenarios.
- 25 edge cases, including the 50% threshold from both sides.
- A coverage guard that every result type is actually reached.
- Planted bugs were caught (ACV sign; the threshold check that has an effect).

## Where calculation inputs come from (`Sde.Core.Assessments.AssessmentCalculator`)

In the original, the calculation inputs were assembled inside four WPF tabs that reached into each other through `MainWindow.MainDock.Children[0]`. `BuildInput` reproduces them field by field:
- Empty combo boxes become `0`.
- Empty number boxes become `0`.
- The 7 additional-adjustment rows are always sent; an empty quantity or cost stays `null`.
- Element rows are the visible ones. Residential shows 12 (Skirting replaces Foundation for manufactured houses); non-residential shows 7.
- Cost-adjustment rows follow `CostAdjustment_ResidenceType` for residential and lookup type 23 with `LookupValue2='2'` otherwise. They are ordered by zOrder with **NULLs first, as Jet sorts**.
- The depreciation % comes from `DepreciationValue` (rating × story × use; residential uses story = use = 0). A table value of 0 shows as blank (null), as in the original.

**How results are stored.** The original showed results in labels (`{0:C}` → "$1,234.57", `{0:0.0} %`) and **parsed the labels back** on Save. `ApplyResults` therefore stores:
- money rounded to 2 decimals and percentages to 1 decimal, both rounding half away from zero (.NET formatting);
- the tabs' values in the original save order (Cost → Output Summary → Elements), so `ComputedDamages` = `TotalEstimatedDamages`, as it ended up in FEMA's database.

**Differences from the original's save:**
- **One transaction instead of five separate per-tab writes.**
- **Negative adjustments.** The original's en-US currency format wrote them as "($1.00)", which then crashed the parse, so a save with a negative adjustment failed. The Mac version stores them.
- **Import recalculation.** The original re-ran the engine on import but stored full-precision values through `BulkUpdateCalcs`, with a stray leading space in `DamageMessage`. The port uses the same rounding as an interactive save, so imported and hand-entered assessments are stored the same way.

## Validation (`AssessmentValidator`)

This is a port of `DataValidationViewService` driven by FEMA's `ValidationRule` table (49 rules, 15 rule types).
- Pin colours become severities: red → Required, yellow → Recommended, green → Suggested.
- Latitude/longitude-in-state checks keep the original's special cases for Alaska (crosses 180°) and American Samoa (southern hemisphere), and still run only when the "Lat/Long Validation" preference is on.
- As in the original, issues never block a save.

## Database (`Sde.Data`)

`Schema.sql` is a one-to-one translation with the same table and column names.

| Jet | SQLite | Why |
|---|---|---|
| Replication ID (GUID) | TEXT, lowercase `D` format | The BLL compared `Guid.ToString()` strings; Dapper type handlers convert both ways |
| Boolean (True = −1) | INTEGER 0/1 | One original report query relied on −1 to sort the default photo first (`ORDER BY DefaultImage ASC`); the reports now pick the default photo explicitly |
| DateTime | TEXT `yyyy-MM-dd HH:mm:ss` | Read back as `Unspecified`, as OleDb returned it |
| Currency / Numeric(p,s) | REAL, plus `[JetScale]` on the record | Jet rounded on write and OleDb returned values at the column scale (`1234.5000`). `JetScaling` does both, so values look the same as in FEMA's SDE. Not NUMERIC: SQLite then stores `75` as INTEGER and `75.5` as REAL in the same column, and Dapper (which caches one conversion per query) crashed reading a mix. `DecimalHandler` reads REAL back as `decimal`; every stored value has ≤ 4 decimals, well inside a double's precision |
| Double | REAL | |
| AutoNumber | INTEGER PRIMARY KEY AUTOINCREMENT | |

**What changed in the data layer:**
- **All SQL is parameterized.** The original concatenated user text into SQL with no escaping (`SQLFactory`, search, reports).
- **Real transactions.** Saves, deletes and imports are each one transaction; the original had none.
- **Foreign keys:** Property → Assessment → child rows are enforced. Lookup, state and county references are not, because the original stored 0 to mean "none".
- **Jet SQL constructs** (`Delete *`, `{guid}` literals, `TOP`, `@@Identity`, qualified `SET T.col`, `+` concatenation, `#date#`, `ALike`, nested RIGHT JOINs) were not translated one by one. The queries were rewritten against the same tables.
- **Location.** The database lives at `~/Library/Application Support/SDE/sde.sqlite`, with photos in the `attachments/` folder beside it. FEMA kept `SDEDatabase.mdb` beside the .exe, which on macOS would be inside a read-only app bundle.
- **No "server mode".** The original's server mode (a shared `.mdb` path) is not carried over, because SQLite on network shares is unsafe. Share data through Export/Import instead.
- **Seed data.** A new database is seeded from `src/Sde.Data/Seed/*.csv`: FEMA's reference tables, exported by `tools/build_seed.sh`. `DatabaseSeedTests` checks every value against the `.mdb`.
- **Mac-only tables (v1.5).** `ExternalLink` links an assessment to the record and inspection it was imported from (v1.5: always an Accela record; v2.0: any source, named in the import profile), with the values last imported; v2.0's `StructureLink` does the same for structures imported from a parcel list. It isn't part of FEMA's schema and never reaches Export.json (`ExchangeService` reads only FEMA's tables; a test checks the bytes are identical with and without links). `SdeDatabase.Open` adds it to databases made by 1.0 by re-running `Schema.sql`, whose statements are all `CREATE … IF NOT EXISTS`. The schema version stays 1, so 1.0 still opens a 1.5 database, and `ON DELETE CASCADE` lets 1.0 delete linked assessments. See `docs/IMPORTING.md`.

## Import and export (`Sde.Core.Exchange`, `Sde.Data.ExchangeService`)

**Format.** SDE 3's exchange format is a plain folder, `SDE_<n>/Export.json` plus `photos/`. It is written by Newtonsoft with Indented formatting, UTF-8 without BOM, CRLF line breaks, and keys in FEMA's model declaration order. Values look like this:
- GUIDs in lowercase;
- dates as `yyyy-MM-ddTHH:mm:ss`;
- Currency with 4 decimals;
- `ItemCost`/`DamageValues` as strings.

`ExchangeTests` checks the key order against `decomp/DAL/DAL.Models/*.cs`, and also checks the byte format and an exact round trip. A folder written here should import into FEMA's SDE on Windows, and vice versa. That direction is not yet tested with a real Windows SDE, because there is no Windows access.

**Fixed original bugs** (each change keeps the files compatible):

| Original | Mac port |
|---|---|
| `SDE_<n>` numbering added the numbers of existing folders instead of taking the highest | n = highest + 1 |
| A filtered export dropped property-level custom fields | Kept |
| Import reused `ElementID`/`ResultID`, which could break primary keys | Child-row IDs are always regenerated |
| "Copy" import gave assessment custom fields `Guid.Empty` | Remapped to the new assessment |
| Shapes of a property with N assessments were inserted N times | De-duplicated |
| Copies shared photo files, so deleting one deleted the other's photo | Copies get their own files |
| No transaction: an import could stop half-way | One transaction; photos are copied after it commits |

**Handling existing records.**
- **Skip:** leave existing records alone; new assessments of an existing structure are still added.
- **Overwrite:** replace the existing records.
- **Copy:** import as a separate copy with new IDs.
- In the original these were per-row checkboxes. The app shows every structure in the file with an include checkbox, and one choice for structures already in the database.

**FEMA `.mdb` databases** are read through mdbtools (`mdb-tables`, `mdb-export`) into a scratch SQLite file, then imported like an exchange folder. Photos are expected in SDE's layout: `<install>/attachments`. Writing `.mdb` files isn't possible on macOS, and isn't needed, because SDE itself exchanges data as Export.json.
mdbtools reads an empty string as NULL (Text and Memo alike), so `""` and NULL can't be told apart in an `.mdb` import;
FEMA's code treats them the same. `MdbImportTests` reads a populated FEMA database back field for field.

**SDE 2.x folders** (`SummaryData.xml` + per-structure `PropertyData.xml` / `assessmentdata.xml`) are read by `Sde.Interop.LegacySde2Import` into the same dataset, then previewed and imported like an SDE 3 folder. See "Spreadsheets, maps, settings and SDE 2.x" below.

## Reports (`Sde.Reports`)

FEMA's 12 RDLC layouts (Microsoft ReportViewer, Windows-only) are rebuilt with MigraDoc/PDFsharp (MIT) as 4 reports, each with and without photos: **Summary** (one page per assessment), **Detail** (one section per assessment, page numbers restart), **Structure and Percent Damaged list** and **Community report** (landscape, grouped by NFIP community → structure → assessment). The residential or non-residential variant is chosen per assessment. Font: Public Sans (USWDS, OFL 1.1), embedded. Samples: `docs/sample-reports/`.

The app saves the PDF through the macOS save dialog and opens it in Preview, where it can be printed. Unsaved edits are saved first, so the report matches the screen.

| Original | Mac port |
|---|---|
| An assessment with photos but none marked default vanished from the summary, percent-damaged and community reports (`DefaultImage OR DefaultImage IS NULL` join) | The default photo, otherwise the first photo |
| A 4th custom field crashed the report (3 fixed boxes) | All custom fields are listed |
| At most 7 cost adjustments, in no set order | All rows, in the lookup's display order |
| A missing value-of-building amount threw an error | "Not Provided" |
| Photos embedded at full size; phone photos printed sideways (EXIF orientation ignored) | Turned upright and resized to 1,600 px on the long side at render time (`PhotoPrep`, SkiaSharp); the stored photo is untouched |
| GIF/TIFF or a missing file crashed or printed a broken image | GIF and WebP print; anything unreadable gets a "Photo format not supported" / "Photo file not found" box |
| Factor Adjustment printed as a whole number (1.15 → "1") on the summary | Decimals kept |

Smaller differences: blank optional amounts print blank, not "$0.00"; percentages print "75.0%" not "75.0 %"; additional adjustments show only filled-in rows and drop the hard-coded "Sq Ft" unit (the entry screen has no unit); the detail report's "Additional Image" is the next photo in order (the original took the newest non-default one); the list reports add the determination under the % and a title/count line.

**Submission folder (v1.5).** `SubmissionFolder.Write` puts a report per assessment (named after its Accela record
when it has one), the percent damaged list, the community report, an `SDE_1` package and `results.csv` into one new
folder, with progress and cancellation. See `docs/IMPORTING.md`.

## Spreadsheets, maps, settings and SDE 2.x (`Sde.Interop`)

| Feature | Original | Mac port |
|---|---|---|
| **Excel export** (`ucExcelExport`) | DevExpress grid export, 218 columns | ClosedXML (MIT), the same 218 headers (checked against the XAML), values as SDE displayed them. From Import & Export (everything) or the Structures list (checked rows) |
| **Google Earth map** (`ucGeoFileCreator`) | KMZ via SharpZipLib, legend overlay, pins by type and % damaged | Same KML layout, icons and thresholds; `System.IO.Compression` |
| **Lat/Long spreadsheet** (`Tools/LatLong`) | Export 4 columns; re-import read cells by position and silently skipped bad rows | Columns found by header; every row checked and shown before anything is saved; only checked rows saved, in one transaction |
| **Enterprise Import** (`ucEnterpriseImport`) | DevExpress Excel data source; map every column by hand | ClosedXML (.xlsx/.xlsm) or .csv. Columns named like a field (or common assessor names such as "Year Built", "Parcel ID", "Site Address") are mapped automatically; a live preview shows each row's address, owner and problems before import; saved mappings are interchangeable with FEMA's (`EnterpriseImportSettings` rows) |
| **User-settings file** | JSON of notes, Enterprise mappings, default values | Same keys, key order and byte format; import says what it will add or replace and asks first |
| **SDE 2.x import** | XmlSerializer classes, rewrote `PropertyData.xml` in place | Same classes; the folder is read without being changed; photos are staged under unique names |

**Original bugs fixed:**
- **Excel:** Additional Cost Adjustments 1–5, Attachment Name 3 and Other Cause of Damage were always blank (misnamed or mistyped bindings); Replacement Cost per Square Foot showed the base cost; with more than 7 cost / 5 additional adjustments, filled-in rows could fall off the end.
- **Map:** names went into the XML unescaped (an "&" broke the file); structures without coordinates were written with empty coordinates (now skipped and listed); a missing street number or name crashed it; coordinates were written "lon, lat" with a space KML doesn't allow.
- **Enterprise Import:**
  - "123 Martin Luther King Blvd" became number 123, name "Martin", suffix "Luther". The number is now the first word, the suffix the last word when it is one, and the name everything between.
  - Suffix modes looked up the whole cell, not the split-off suffix.
  - Rows with too few words were silently dropped; now they import with a warning.
  - Mapping a direction without an apartment column failed every row.
  - "Make Selection..." as the street-number mode imported no number.
  - A saved mapping naming a missing column failed every row; now it's reported up front.
  - The original required three address columns; one is enough.
- **SDE 2.x:** cost adjustments pointed at the old lookup type 10 (now type 23); residences got the non-residential story count (duplicate OldIDs in lookup type 14); missing elements, structures without assessments and sub-folders without `PropertyData.xml` crashed or stopped the import; empty dates became 1 Jan 0001 and empty BFE/flood values 0; undetermined-damage codes were stored as the depreciation %; "52.3%" was dropped; same-named photos overwrote each other; an unknown value/repair basis became 0 (now 390/393).

**Not supported:** old `.xls` workbooks (ClosedXML reads .xlsx only); the app asks to re-save as .xlsx or .csv.

**Accela import (v1.5, not in FEMA's SDE).** `Sde.Interop.Accela` turns an Accela export into assessments: a field
catalog grouped like the editor, a JSON profile per agency, a checklist (guidesheet) pivot, Accela text → FEMA choices
with translations, and a preview with a status per inspection. Enterprise Import's row parser is now
`Internal/PropertyRowParser.cs`, shared by both; Enterprise Import behaves exactly as before (its tests are unchanged).
v2.0 generalizes it into `Sde.Interop`'s SourceImport (any source, inspections or structures, matching by record ID,
parcel or address, the building-value confirmation, Final assessments left alone); Accela is one example profile.
`docs/IMPORTING.md` has the details.

## The app (`Sde.App`, Avalonia 12)

FEMA's WPF screens used DevExpress, Telerik and Xceed controls (commercial, Windows-only). The app replaces them with Avalonia (MIT) and reorganises the work:

- **Structures list** with search, a type filter, status tiles (substantially damaged / close to the threshold / not substantially damaged / incomplete), and checkboxes for reports and exports.
- **One assessment, eight steps** (the original's large tabbed forms): location & owner, building, inspection, flood zone, cost & depreciation, damage by element (sliders), result, photos & notes. Each step shows how many required and recommended items are missing; clicking an issue jumps to its field.
- **Live result.** The % damaged and determination update on every change, using the same `Sde.Calculation` engine. Saves happen automatically 0.7 s after the last edit; FEMA's SDE had to be saved by hand, and reports printed the last saved version.
- **Same rules as FEMA.** The validator's 49 rules, the field dependencies (story types reset sprinkler/conveyance, non-flood causes clear the flood fields, depreciation rating fills the %), and the element lists per building type are FEMA's.
- **Photos** by drag and drop; one is marked for the reports. iPhone HEIC photos are converted to JPEG as they're added (macOS `sips`), because FEMA's SDE, the reports and the thumbnails can't read HEIC.
- **Import & Export** page for everything in the two sections above, each with a preview before anything is written. Drop a folder or `.mdb` on the page to import it.
- **Settings:** default values for new structures, custom field names, preferences, and the database (show in Finder, open another, new, back up).
- **Importing inspections or parcels (v1.5 for Accela, v2.0 for any source):** an import page under Import & Export (also File → Import Inspections or Parcels…, ⌘⇧I); the Structures list shows each assessment's or structure's source and record ID, finds it by search, filters imported vs entered in SDE, and narrows to the last import with a chip; Export ▾ → Submission folder.
- **macOS menus:** File (new structure ⌘N, import ⌘I, export ⌘E, inspections or parcels ⌘⇧I), View (⌘1–3).
- **Light and dark mode.**

A bulk change (import, coordinates) first saves and closes the open assessment, so its in-memory copy can't overwrite the change when it next saves.

**Accessibility.** Every input has an accessible name: its label, or for row controls something like "Roof Covering
percent damaged". VoiceOver reads it, and assistive and automation tools find the control by it. For number and date
fields the name is passed to the text box inside, which is what macOS exposes. `AccessibilityTests` fails on any
visible input without a name.

## Other original bugs fixed so far
- **Default geographic adjustment** was truncated to a whole number (`Convert.ToInt32`). It is now parsed as a decimal.
- **Unparseable default values** crashed the original when creating a record. They are now skipped.

## v2.0 additions
Nothing here changes the calculation or FEMA's formats; everything new is in Mac-only tables (`Schema.sql`, "added in
v2.0"), which are never exported. `SdeDatabase.MacSchemaVersion` is 2 and FEMA's `SchemaVersion` stays 1, so v1.5
still opens a 2.0 database.

- **Deleting** sets FEMA's own `Active` flag (FEMA's DAL deleted that way too), and `DeletedItem` remembers it for 30
  days. Every query already filtered on `Active`, so lists, reports, exports and v1.5 leave deleted rows out.
- **Saves** go through `SdeStore.SaveTracked`: the stored copy is compared field by field (`AssessmentDiff`, using
  `FieldCatalog`), Final and Letter sent assessments refuse changes, and `EditHistory` records the rest.
- **Photo order**: a reorder gives the photos new AttachmentIDs in display order, since FEMA's SDE, the reports and
  Export.json all list photos by AttachmentID. The order therefore reaches FEMA's SDE too.
- **Search and replace** uses FEMA's field list (Lookup type 28). FEMA's version lowercased the whole value
  (`SearchAndReplaceViewService.SearchAndReplace`); this one changes only the matching text.
- **Drafts**: FEMA saved a new structure as soon as it was created; here nothing is written until the first edit.
- **Field rules** (what changing one field resets) moved from the editor into `FieldRules`, so bulk edit, copy-from,
  imports and the phone form apply exactly the same rules.

## Tests
`dotnet test Sde.sln` runs 611 tests (577 pass, 34 need FEMA's installer); the list below describes v1.5's. See `docs/TEST-REPORT.md` for what they establish.

- **`Sde.Tests` (79)**
  - Answer-key parity: 31,500 residential and 10,368 non-residential configurations, plus edge cases.
  - FEMA's manual as answer key (`ManualExamplesTests`): Figs. 3-34, 3-37, 3-38, 3-43, and the manual's stated rules.
  - Seed data compared with the `.mdb`; the store; the exchange format; the validator.
  - A populated FEMA `.mdb` read back field for field (`MdbImportTests`; fixture built by `tools/mdb-fixture`).
  - The Windows cross-check kit kept in step with the code (`CrossCheckKitTests`).
  - `--filter "Category!=Slow"` skips the residential sweep.
  - The `ExternalLink` table: migration of a 1.0 database, saving, cascading deletes, Export.json unchanged.
- **`Sde.Reports.Tests` (53)**
  - Every report × photos on/off × residential/non-residential, checked through `pdftotext` and `pdfimages`.
  - Photo choice and placeholders; photo orientation and resizing.
  - The submission folder: names, contents, `SDE_1` into a fresh database, `results.csv`, cancelling.
- **`Sde.Interop.Tests` (76)**
  - Excel headers compared with the original XAML; KMZ; Lat/Long.
  - Enterprise Import modes; SDE 2.x; user-settings files.
  - Importing inspections or parcels: guessing, the checklist pivot, value matching, profiles (v1.5's still load),
    every preview status, RanchFlood imported from an Accela-style export, re-imports that keep SDE edits, every FEMA
    choice the import can set, structures through StructureLink, matching by parcel and address with ambiguity, the
    building-value confirmation, Final assessments left alone, one history batch per import.
- **`Sde.App.Tests` (29)**
  - The real window rendered off-screen (Avalonia.Headless): every page in light and dark mode, failing on binding errors.
  - Editing, and every report, export and import, run through the app's own commands.
  - Accessible names on every input, including the import page and the submission panel.
  - The import (inspections, and parcels as structures), review and submission folder through the app's own view models.
  - 1,000-structure scale.
  - Screenshots go to `docs/screenshots/`.
