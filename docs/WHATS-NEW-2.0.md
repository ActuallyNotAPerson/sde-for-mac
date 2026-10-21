# What's new in SDE for Mac 2.0

SDE for Mac 2.0 keeps FEMA's calculation and file formats exactly as they were and adds the tools a floodplain office
needs around them. Everything below works on databases from 1.0 and 1.5, and a 1.5 app can still open a 2.0 database.

## Safer to use
- **Recently deleted.** Deleting a structure, an assessment or a photo moves it to Recently deleted for 30 days.
- **Undo and redo** in an assessment (Cmd+Z, Shift+Cmd+Z).
- **Automatic backups** beside the database, once a day and on quitting; File, then Restore from Backup.
- **Statuses**: Draft, Reviewed, Final, Letter sent, Appeal. Final and Letter sent lock an assessment; reopening it asks
  for a reason.
- **Edit history**: who changed what and when, for every assessment, including bulk changes, imports and the phone form.
- **Errors in plain English**, with the technical details folded away and a log to send when reporting a problem.
- **Nothing is saved until you start**: a new structure you open and close without typing leaves nothing behind.

## Faster with many structures
- **A sortable list** with the columns you choose, filters (inspection dates, percent damaged, street, ZIP, parcel,
  status) that are remembered, and saved views. The order you sort is the order reports print in.
- **Set a field on many structures at once**, with an undo, skipping Final assessments.
- **Search and replace** over the same fields as FEMA's SDE, with a preview.
- **Copy from another structure** for rows of similar buildings.
- **Note snippets** for text you type often.
- **The keyboard**: Edit, Window and Help menus; Cmd+S, Cmd+F, Cmd+O, Cmd+[ and Cmd+] everywhere; 0 to 4 on a damage
  slider for 0, 25, 50, 75 and 100 percent.

## Field data in
- **Import inspections or parcels** from any permit system, assessor roll or spreadsheet (.csv, .xlsx, .xls, or a
  shapefile's .dbf), matched by record ID, parcel or address. Example profiles are included.
- **FEMA `.mdb` databases open with nothing else installed**: the two mdbtools programs that read them are now inside
  the app.
- **Photos**: reorder them, use a photo's GPS position and date, and match a whole folder of photos to structures.
- **Look up flood data online**: coordinates from the Census Bureau's geocoder and the flood zone, panel and base flood
  elevation from FEMA's National Flood Hazard Layer, shown for you to confirm before anything changes.
- **Depth tables**: your community's own depth-damage table can suggest element damage from the flood depth; the
  inspector accepts or changes each suggestion.
- **A phone form**: inspectors can look up or add structures, fill in fields and take photos from a phone on the same
  Wi-Fi, after pairing with a QR code or PIN.

## Results out
- **Map** of every structure, coloured by result, with a basemap.
- **Event dashboard** with totals by result, street, community and inspector, an event summary PDF, and GeoPackage and
  GeoJSON exports for GIS (with a QGIS style).
- **Owner letters** from editable Word templates (starter templates included; review them with your counsel).
- **Substantial improvement** tracking with an SI determination PDF.
- **Appeals**: contractor bids compared with the computed damage.
- **Cumulative damage** across events (off unless your community's ordinance needs it).

## Help
- A "?" beside every field, with the page of FEMA's manual it comes from.
- A sample database to practise on, and a short walkthrough the first time SDE opens.

## Unchanged on purpose
- The damage calculation is FEMA's engine, compiled unedited for the tests and ported line for line for the app.
- Export.json, SDE_n folders and the .mdb import are exactly what FEMA's SDE writes and reads. Nothing added in 2.0 is
  ever exported to FEMA.
- Cumulative damage, substantial improvement and appeals never change FEMA's percent damaged or its determination.
