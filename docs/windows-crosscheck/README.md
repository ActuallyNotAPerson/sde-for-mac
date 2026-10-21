# Windows cross-check kit

The Mac version has been checked against FEMA's decompiled code, the screenshots in FEMA's manual, and a populated
`.mdb` built from FEMA's empty database. It has **not** been checked against FEMA's SDE 3.0 actually running on Windows,
because none was available. This kit is for whoever has a Windows PC with SDE 3.0 installed. It takes about an hour.

## What's here

| File | What it is |
|---|---|
| `SDE_1/` | An SDE export folder written by the Mac app: the 9 test structures below |
| `expected.csv` | The results the Mac app computed for each structure |
| `WORKSHEET.md` | Every value to type into FEMA's SDE for each structure, and what the Output Summary should show |
| `cases/*.json` | The source of each case (also used by the automated tests) |

The cases cover each calculation path:

| Case | What it exercises | Mac result |
|---|---|---|
| 01 ranch flood | Residential, crawlspace, a cost adjustment and an additional adjustment | 75.0% Substantially Damaged |
| 02 two-story close | Just under the 50% threshold | 49.9% Not Substantially Damaged |
| 03 office minor | Non-residential, 1 story, with a sprinkler | 20.6% Not Substantially Damaged |
| 04 townhouse tax value | Town house, 2+ stories, adjusted tax assessed value × 1.25 | 56.4% Substantially Damaged |
| 05 appraisal + contractor | The manual's §8.4 procedure: base cost $1, depreciation "Other" = 1 | 53.6% Substantially Damaged |
| 06 manufactured home | Skirting / Forms Piers in place of Foundation; Expando and Skirting adjustments | 57.2% Substantially Damaged |
| 07 no physical damage | Damage Undetermined | 0.0% No Physical Damage Sustained |
| 08 hotel community estimate | Non-residential, 5+ stories, sprinkler and elevator, community estimate | 17.1% Not Substantially Damaged |
| 09 very poor, capped | Damage worth more than the depreciated value: capped at 100% | 100.0% Substantially Damaged |

## Check 1: Mac → Windows (import)

1. Copy `SDE_1` to the Windows PC.
2. In SDE: **Import/Export → Import SDE Data**, choose the `SDE_1` folder, and import all records.
3. Open each assessment and compare its **Output Summary** tab with `expected.csv`: Replacement Cost, Computed Damages,
   Computed Actual Cash Value, Percent Damaged and the determination.
4. Print one Summary report to check that the imported records print.

**Pass:** every record imports without an error, and every number matches to the cent and the percent to one decimal.

## Check 2: FEMA's screens (hand entry)

The Mac results come from FEMA's calculation engine, which was decompiled and proven identical on 41,868 configurations.
What hasn't been checked is how FEMA's *screens* feed that engine. Hand entry tests that.

1. In a new, empty SDE database, enter each case from `WORKSHEET.md`, tab by tab, and save.
2. Compare the Output Summary with the **Expected** table under each case.

**Pass:** the same numbers as Check 1. If one differs, note which tab's value differs first; that locates the cause.

## Check 3: Windows → Mac (export)

1. In SDE on Windows, after Check 2: **Import/Export → Export SDE Data**, all records.
2. Also copy `SDEDatabase.mdb` from SDE's `Database` folder, together with the `attachments` folder beside it.
3. On the Mac, in a new database (**Settings → Database → New empty database…**), use **Import & export** to import the export folder,
   then the `.mdb` in another new database. Importing `.mdb` files needs mdbtools: `brew install mdbtools`.
4. Compare with `expected.csv`.

**Pass:** both import without warnings and show the same results.

## Reporting back

For anything that differs, write down:
- the case;
- the tab and field;
- the Windows value and the Mac value.

Screenshots of the Windows Output Summary help. Adding the Windows exports to `tests/fixtures/` would make them a
permanent answer key.

## Keeping the kit current

`CrossCheckKitTests` fails if the code stops giving the results in `expected.csv`. After a deliberate change, regenerate
`SDE_1`, `expected.csv` and `WORKSHEET.md` with:

```bash
SDE_WRITE_CROSSCHECK=1 dotnet test tests/Sde.Tests --filter CrossCheckKit
```
