## What changed and why
<!-- What this changes, and the problem it solves. Link the issue if there is one ("Fixes #12"). -->

## How it was tested
<!-- Commands run, and anything checked by hand. Screenshots help for UI changes. -->

## Checklist
- [ ] `dotnet build Sde.sln` and `dotnet test Sde.sln` pass.
- [ ] If this touches `src/Sde.Calculation`, `src/Sde.Data/Seed` or the export formats, I ran the tests with FEMA's
      answer key (`tools/answer-key/setup.sh`) and they pass.
- [ ] Export.json, the `.mdb` import and SDE_n folders are unchanged, or this PR explains why FEMA's SDE still reads them.
- [ ] A database written by this version still opens in the previous release.
- [ ] Nothing here is specific to one city, county, state agency or software vendor.
- [ ] No real addresses, owner names or photos, and nothing from `decomp/`, `reference/OriginalCalc/` or `extracted/`.
- [ ] `docs/screenshots/` changed only if the UI was meant to change.
- [ ] User-facing text is plain English, with no em dashes.
