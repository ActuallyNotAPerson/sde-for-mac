# FEMA SDE 3.0: how it works (from the decompiled source)

Source: `SDE3_04062018.zip` → `SDE3-Installer.msi` (ProductVersion 3.0.0, FEMA, built 2018-04-06).
- Unpacked files: `extracted/`.
- Decompiled C#: `decomp/`, produced by ILSpy 11.1 (`ilspycmd -p`).
- Database dump: `decomp/database/`, produced by mdbtools 1.0.1.

`file:line` references are relative to `decomp/` unless noted.

---

## 1. The pieces

| Assembly | Size | What it is | Lines (decompiled) |
|---|---|---|---|
| `wpfSDE.exe` | 1.8 MB | WPF UI: 55 XAML screens + code-behind. **It also assembles the calculation inputs.** | 22,124 C# + 55 XAML |
| `BLL.dll` | 190 KB | View models, validation, report datasets, search, Excel enterprise import | 8,800 |
| `DAL.dll` | 206 KB | Dapper over Jet OLEDB, all SQL built by string concatenation | 6,480 |
| `CalculationEngine.dll` | 26 KB | The damage math (pure, no I/O except loading JSON) | 513 |

- **Runtime:** .NET Framework 4.5.2, x86, `Microsoft.Jet.OLEDB.4.0`.
- **Third-party (commercial, Windows-only):** DevExpress 15.2 (grids, editors, spell check, Excel export), Telerik (busy indicator, image editor), Xceed, Microsoft ReportViewer (RDLC), AForge (webcam).
- **Shipped but never referenced:** EntityFramework, DevExpress XPO, Z.Dapper.Plus, SharpKml, Ionic.Zip.
- **Open-source and used:** Dapper 1.50.2, Newtonsoft.Json 10.0.3, SharpZipLib 0.85.5, ServiceStack.Text 4.5.12.

**Data files:**
- **`Database/SDEDatabase.mdb`** (Jet 4): 29 tables. All transactional tables ship empty; only the reference tables have rows. Opened **in place** next to the exe.
- **`JSON/Residential.json`** (90.7 MB): 378,000 rows. 283,500 are non-blank, which is 23,625 building combinations × 12 elements, each combination summing to 100%.
- **`JSON/Commercial.json`**: 2,331 rows. 24 structure uses × story × sprinkler × conveyance → 7 elements.
- **`JSON/Lookup.json`**: 404 rows. Maps DB LookupIDs to the "OldID" keys the JSON tables use.
- **`Reports/*.rdlc`**: 6 layouts × (with/without photos).

---

## 2. Calculation (the part that must match FEMA exactly)

`CalculationEngine/CalculationEngine/Calculations.cs`. Entry point: `RunCalulations(DataInputOutputModel)` (sic). It mutates and returns the same object. Steps, in order:

| # | Step | Formula |
|---|---|---|
| 1 | CostCalculation | `Cost = BaseCostPerSqFt × SquareFeet × GeographicAdjustment` |
| 2 | TotalAdjustments | `Σ(CostAdj.Qty × UnitCost) + Σ(AdditionalAdj.Qty × UnitCost)`; also sets each row's TotalCost |
| 3 | TotalReplacementCost | `TRC = Cost + TotalAdjustments` |
| 4 | ReplacementCostPerSqFt | `TRC / SquareFeet` (only if SquareFeet > 0) |
| 5 | Element % | For each element: `ItemCost = Element% / 100 × TRC`; `DamagedValue = ItemCost × UserDamage% / 100`; `ComputedDamages = Σ DamagedValue` |
| 6 | Computed ACV | `ACV = TRC × (1 − Depreciation% / 100)` |
| 7 | Adj. tax value | `AdjTax = TaxAssessedValue × FactorAdjustment` |
| 8 | Repair/Recon % | `ComputedDamages / ACV` (only if ACV > 0) |
| 9 | **Percent Damaged** | Divides the chosen repair cost by the chosen building value (details below) |

**Step 9 in detail:**
- Building value comes from `ValueOfBuildingID`: 390 = Computed ACV, 391 = Professional Market Appraisal, 392 = Adjusted Tax Assessed.
- Repair cost comes from `CostOfRepairsID`: 393 = Computed Damages, 394 = Contractor Estimate, 395 = Community Estimate.
- If the building value is greater than 0, `PercentDamaged = repair / value`. Otherwise it is null and the result reads "Data Entry Incomplete".
- `≥ 0.50` → **"Substantially Damaged"**; `< 0.50` → "Not Substantially Damaged".
- *After* the determination is made, the percentage is capped at 1.0.
- If `DamageUndeterminedMessage` is set, the result text becomes that message and the percentage becomes 0.

**Element table lookup (`GetElementList`).**
- The UI passes DB LookupIDs. `GetOldLookUpID(id, type)` maps them through `Lookup.json` → `OldID`, and an ID of 0 maps to 1.
- Residential key: Residence (type 11), Foundation (7), Superstructure (16), Roof (12), Exterior (4), HVAC (8), Story (14).
- Non-residential key: Story (14), StructureUse (15), Sprinkler (13), Conveyance (1).

**Depreciation % is not computed by the engine.**
- The UI fetches it from the `DepreciationValue` table (`DAL/DAL.Services/LookupService.cs:141`) by rating (lookup type 24) × story × structure use.
- Residential uses story = use = 0.
- Rating 389 "Other" lets the user type a value.

**Quirks that must be preserved in the faithful port (and are covered by tests):**
- The decimal arithmetic order and nullable-decimal semantics. Null propagates; for example, a null SquareFeet makes Cost null.
- Empty `ElementPercentages` input → builds a default list and then discards it (`Calculations.cs:195-208`), so ComputedDamages = 0.
- The cap to 1.0 happens after the 50% test. Undetermined damage overrides everything.
- `GetOldLookUpID` throws a NullReferenceException when an ID is missing from `Lookup.json`.
- `PercentOfExistingImprovements` is accepted but never used.

**Things that break on macOS:**
- JSON paths are built as `BaseDirectory + "JSON\\REsidential.json"`, with backslashes and odd casing.
- The static caches are not thread-safe.

**Who calls it.** Not the BLL:
- **Interactive:** `wpfSDE/wpfSDE.Assessment/ucAssessment.xaml.cs:342`. Each tab pushes its inputs into a shared `_CalcModel` through `MainWindow.MainDock.Children[0]`:
  - `ucStructureDamage.xaml.cs:953`
  - `ucCost.xaml.cs:430`
  - `ucElementPercentages.xaml.cs:582`
  - `ucOutputSummary.xaml.cs:206`
  - Results are written to labels, then **parsed back out of formatted text** (`"$1,234.00"`) when saving.
- **Batch (on import):** `wpfSDE/wpfSDE.ImportExport/ucImportSDEdata.xaml.cs:1053-1148`, then `DAL UtilityService.BulkUpdateCalcs`. It stores `PercentDamage` and `RepairReconstructPercent` ×100.

---

## 3. Data model

Main entities (GUID keys stored as Jet "Replication ID"):

```
Property (PropertyID)                 ← one building / parcel; owner, address, NFIP, FIRM, building characteristics, SquareFootage
 ├─ Shapes (ShapeID autonum)          ← square-footage calculator shapes (L/T/U/rect/'c'=total)
 ├─ CustomField_Results (ObjectIdentifier=PropertyID)
 └─ Assessment (AssessmentID)         ← one inspection after one event; inspector, dates, cause, costs, results
     ├─ ElementPercentage (ElementID)       ← user % damaged per element + computed cost/value
     ├─ CostAdjustmentResult (ResultID)     ← standard cost adjustments (lookup type 23)
     ├─ AdditionalAdjustment (AdditionalAdjustmentID) ← up to 7 free-form adjustments
     ├─ Attachment (AttachmentID autonum)   ← photos (files in ../attachments/)
     └─ CustomField_Results (ObjectIdentifier=AssessmentID)
```

**Reference tables:**
- `Lookup` (414 rows, 30 types) + `LookupType`
- `State` (59), `County` (3,251), `StreetSuffixAbbreviation` (202)
- `DepreciationValue` (210)
- `CostAdjustment_ResidenceType` (20)
- `ValidationRule` (49), `LatLongValidation` (59 state bounding boxes)
- `CustomField` (3 fixed slots)
- `Default`, `UserDefault`, `Notes`, `EnterpriseImportSettings`, `DatabaseInfo`, `LastFiveAssesments`, `Manage`

**Legacy/unused:** `InspectionStatus`, `ResidenceType`, `RoofCoveringType`, `StructureShape`.

Schema: `decomp/database/schema.access.sql` (also `schema.postgres.sql`). Relationships: `decomp/database/relationships.csv`.

**Points that matter for SQLite:**
- GUIDs: store as lowercase `D`-format TEXT (the BLL compares `Guid.ToString()` strings), and add a Dapper type handler.
- mdb-export prints GUIDs with a missing hyphen (`{8-4-4-16}`), so the converter must repair them.
- Jet rounds on write; SQLite doesn't. Round in code: Currency → 4 dp, `PercentDamage` Numeric(4,1) → 1 dp, Numeric(18,0) → integer.
- Jet True = −1. One query depends on that: `ORDER BY DefaultImage ASC` puts the default photo first (`DAL/DAL.Services/ReportsService.cs:203`).
- Booleans: 0/1, with `DEFAULT 0`. Turn on `PRAGMA foreign_keys`.

---

## 4. Data access layer: what has to change

- 25 services, each `new OleDbConnection → Open → Dapper → Dispose`, about 179 queries in all. There are **no parameters and no transactions anywhere**; most string values are concatenated into SQL without escaping.
- `SQLFactory` builds INSERT and UPDATE statements from model properties by reflection:
  - GUIDs are written as `{guid}` literals and bools as `True`/`False`.
  - Everything else goes through culture-dependent `ToString()`.
- **Access/Jet-only SQL to translate:**
  - `Delete * from` → `DELETE FROM`
  - `{guid}` / `{guid {x}}` literals → parameters
  - `TOP n` → `LIMIT n`
  - `@@Identity` → `last_insert_rowid()`
  - `UPDATE T SET T.col=` → unqualified `SET`
  - `+` string concatenation → `||`
  - `#date#` literals → parameters
  - `ALike` → `LIKE`
  - nested `RIGHT JOIN` chains → flat `LEFT JOIN`s
  - `SELECT A.*, B.*` with duplicate column names → explicit column list
- The full inventory, with file:line for every statement, is in `docs/PORTING-NOTES.md` (written during Step 2).
- **Windows-only pieces in DAL/BLL:**
  - `System.Data.OleDb` (every DAL service)
  - `DevExpress.DataAccess.Excel` (`BLL/BLL.Service/EnterpriseImportService.cs` only)
  - backslash paths: `ConnectionStringService.cs:17`, `UtilityService.cs:179`, BLL `ReportsService.cs:239-305`
  - Otherwise, DataSet/DataTable and the view models are portable.

---

## 5. Screens and workflow

**Navigation.** One `MainWindow` swaps a single control into `MainDock` (`wpfSDE/wpfSDE/MainWindow.xaml.cs:325-433`). Almost everything else is a modal window. The XAML has **no data binding**: every screen copies values in and out in code-behind.

**Evaluating one structure:**
1. **Add New Residential / Non-Residential Assessment** → `PropertySelect`. Pick an existing property or create a new one; this has a hard-coded 10 s `Thread.Sleep`. Creates an assessment with ValueOfBuildingID=390 and CostOfRepairsID=393.
2. **ucAssessment** with 6 tabs, plus read-only tabs for the property's earlier assessments:
   - **Address:** subdivision/parcel/lot, lowest-floor elevation, datum, NFIP ID/name, lat/long, owner and structure address, mailing address, custom fields 1-3.
   - **Structure/Damage/NFIP:**
     - Building fields depend on type. Residential: residence type, foundation, superstructure, roof, exterior, HVAC. Non-residential: use, sprinkler, conveyance.
     - Common fields: stories, year, quality, inspector, inspection and damage dates, cause, flood duration and depth, "damage undetermined", FIRM panel/zone, BFE, floodway.
   - **Cost:**
     - square-footage calculator (L/T/U/rect shapes, stored per Property), base cost $/sf, geographic adjustment
     - standard cost adjustments (dynamic list, by residence type), 7 additional adjustments
     - cost data source and date, depreciation rating → %
   - **Element Percentages:** % damaged for 12 residential or 7 non-residential elements. "Skirting" replaces Foundation for manufactured homes.
   - **Output Summary:** choose the value basis (ACV, appraisal, adjusted tax) and the repair basis (computed, contractor, community). Shows % damaged and the determination.
   - **Photos:** upload (3 MB limit per file), webcam capture, image editor, default photo, descriptions.
3. Every edit re-runs the calculation, and the sidebar shows % damaged.
4. **Save:**
   - Validation (§6) runs first.
   - Then 5 separate per-tab writes, with no transaction.
   - Then a "saved successfully" message box.
5. **Print** Summary or Detailed, which reads from the DB, so you must save first.

**Other areas:**
- **View/Search:** card grid with filters by type, date range, field search, % damaged range, custom field.
- **Bulk Editor:** editable grid, saves per row, **does not recalculate**.
- **Enter Default Data:** pre-fills new records.
- **Custom Fields:** rename 3 slots.
- **Notes:** reusable text snippets.
- **Search & Replace** and **Delete All Data:** destructive, with no preview or undo.
- **Import/Export** (§7).
- **Reports** (§8).
- **Lat/Long:** validation against state bounding boxes; xlsx round-trip.
- **Generate GeoFile:** KMZ.
- **Help:** opens the manual PDF at specific pages.

---

## 6. Validation

**Table-driven:** `ValidationRule` (49 rows) checked by `BLL/BLL.Service/DataValidationViewService.cs:15`.
- Each rule's `ValidationTypeID` selects the logic:
  1. required
  2. dropdown > 0
  3. damage date ≤ inspection date
  4. not in the future
  5. latitude in the state box
  6. longitude in the state box
  7. threshold (year > 1600, BFE > −10)
  8. flood duration required for flood causes
  11. description required when cause is "Other"
  12. required for non-residential
  13. required for residential
  14. inspection date ≥ damage date
  15. depreciation required unless damage is undetermined
- Pin colors:
  - Red = required (NFIP ID, residence type, inspection date, depreciation, story, use)
  - Yellow = recommended
  - Green = suggested (owner and mailing fields)
- **Nothing blocks the save:** the dialog offers "Save" anyway.

**Hard-coded checks elsewhere:**
- On the property screen, only Structure Type blocks the save.
- Depreciation must be 0 < x ≤ 100.
- Element % must be ≤ 100.
- The bulk editor has its own cell validators.

---

## 7. Import / export formats

### SDE data exchange (the key one for FEMA/state submission)

**It's a plain folder, not a zip and not an Access file.**
```
SDE_<n>/
  Export.json      ← Newtonsoft, Formatting.Indented, UTF-8 no BOM, CRLF
  photos/<FileName> (attachment files, GUID-named)
```

**Root object** (key order): `Properties, Assessments, Attachments, ElementPercentages, CostAdjustments, AdditionalAdjustments, CustomFieldsResults, Shapes`.
- Each array holds full DB rows. The property names and order are the DAL model declaration order.
- Exact key lists: `DAL/DAL.Models/{PropertyModel,AssessmentModel,…}.cs`.

**Value formats:**
- GUIDs: lowercase, no braces.
- Dates: `yyyy-MM-ddTHH:mm:ss` with no zone.
- Decimals keep the DB scale (Currency `1234.5000`).
- `ElementPercentages.ItemCost` and `DamageValues` are **strings**.
- `Property.ImportDate` null → `"0001-01-01T00:00:00"`.

**Import:**
- Pick the folder; it must contain `Export.json`, or `SummaryData.xml` for legacy SDE 2.x.
- Per-row merge choice:
  - not existing → insert
  - existing + Select → insert a copy with new GUIDs
  - existing + Overwrite → delete the children and update
- Photos are copied into `attachments/`.
- Every imported assessment is **recalculated** with the engine.
- **Legacy SDE 2.x XML import** maps old lookup IDs via `GetSingleLookupByOldID`.
- Code: `wpfSDE/wpfSDE.ImportExport/ucExportSDEdata.xaml.cs:312-362`, `ucImportSDEdata.xaml.cs`.

**Original bugs we will *not* copy (but must stay compatible with):**
- `SDE_<n>` folder numbering uses `+=` instead of `=`.
- Filtered export drops property-level custom fields.
- Copy-import keeps the old `ElementID`/`ResultID`, which can collide with primary keys.
- Copy-import gives assessment custom fields an empty GUID.
- Multi-assessment properties get their Shapes duplicated.

### Other formats
- **Excel export:** DevExpress xlsx, 218 text columns, one row per assessment (column list in `wpfSDE/wpfSDE.ImportExport/ucExcelExport.xaml:38-687`). The 20 "Additional Cost Adjustment" columns, "Attachment Name 3" and "Other Cause of Damage" always export **blank** because of binding typos. Use ClosedXML.
- **Enterprise Import:** an Excel sheet of properties (owner, address, NFIP, FIRM, lat/long, etc.).
  - Column mapping plus name and address parsing modes (lookups 429-441).
  - Creates Property records only.
  - Mappings are saved in `EnterpriseImportSettings`.
- **User settings JSON:** `{ Notes, EnterpriseSettings, DefaultValues }`.
- **Lat/Long:** xlsx export with PropertyID, Address, Latitude, Longitude. Re-import is positional, matched by PropertyID.
- **KMZ:**
  - Hand-built KML: a legend ScreenOverlay plus one Placemark per assessment.
  - Icons: residential red/green, non-residential red/yellow/green by % damaged (≥50 / 40-50 / ≤40).
  - Zipped with 8 PNG icons.
- **Database location:** `<app>\Database\SDEDatabase.mdb`, optionally redirected to a shared `.mdb` path ("server mode"). Photos are in `<db parent>\attachments\`. There is no backup or restore feature.

---

## 8. Reports (RDLC → will be rebuilt as PDF)

Each report comes with and without photos.

| Report | Contents |
|---|---|
| Community (`rptCommunityRes`) | Landscape, grouped by NFIP community → property → assessment: owner, address, inspection date, type/use, ACV, value basis + value, repair basis + cost, **% damaged**, depreciation explanation, photo |
| Structure & Percent Damaged (`rptPercentDamaged`) | NFIP, owner, address, city/state, county, date, **% damaged**, photo |
| Residential / Non-Residential Summary | One page per structure: community, address, structure info, damage info, NFIP info, **% damaged + determination text**, damage summary (replacement cost, depreciation, ACV, computed damages, existing improvements, repair %), FEMA 213 ACV footnote, signature line |
| Residential / Non-Residential Detail | Everything: all fields, 7 cost + 7 additional adjustments, the element table, damage summary, custom fields, 2 photos, signature line |

- Datasets are built in `BLL/BLL.Service/ReportsService.cs` from the SQL in `DAL/DAL.Services/ReportsService.cs`.
- **Original bug:** an assessment that has photos but no *default* photo drops out of the community, percent and summary reports.

---

## 9. Pain points the friendlier app should fix

- Property data is edited in 3 places. Editing an assessment rewrites the shared Property row; square footage is per property.
- Modal stacking and about 50 message boxes. Fake 1–10 s delays.
- Validation appears only after Save, and is advisory. The determination is visible only on the last tab.
- You must save before printing. Individual reports always include photos.
- "Damage undetermined" silently deletes all of the property's shapes. The bulk editor leaves % damaged stale.
- Destructive tools (Search & Replace, Delete All) have no preview or undo.
- Lat/long sign forcing breaks American Samoa, Guam and CNMI.
- Mailing county loads the wrong list. Photo delete uses the wrong path. Default geographic adjustment truncates to an integer.
