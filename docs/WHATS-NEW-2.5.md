<!-- Draft written during v2.5 development. Check every line against what shipped before the release (M10). -->
# What's new in SDE for Mac 2.5

SDE for Mac 2.5 has a new look and adds the work around an event: a map you can edit, events, field assignments,
review and compliance tracking, and AI assistants. FEMA's calculation and file formats are unchanged. Everything below
works on databases from 1.0, 1.5 and 2.0, and a 2.0 app can still open a 2.5 database.

## A new look
- **Clean and modern**, in light and dark: a sidebar with counts, a breadcrumb at the top, and the same cards, buttons
  and type on every page.
- **The editor** has a step rail with icons, two-column fields with help beside each label, and a result bar that marks
  the 50 percent line.
- **Settings** looks like the Mac's System Settings, with a list of categories on the left.
- **Command palette** (Cmd+K) finds structures, pages, events and commands.

## Events
- Keep each disaster as an **event** (name, kind, DR number, dates and a datum offset).
- **Choose an event in the sidebar** and Structures, the map, the Dashboard, exports, letters and web maps show only
  that event. SDE remembers the choice for each database.
- A new assessment started while an event is chosen joins it, and the editor offers an event from the damage date.

## The map
- **Rebuilt on a real map engine**: background maps (USGS topographic and imagery, OpenStreetMap), FEMA's flood zones
  as an overlay, and structures coloured by result, status, team, event or any field.
- **Add layers** from Shapefiles, GeoJSON, GeoPackages, CSV files and ArcGIS web addresses; style them, label them and
  look through their attribute table.
- **Select** by clicking, dragging a box, drawing an area or by attribute, then show the selection in Structures, set a
  field, assign it to a team, print reports or make a web map.
- **Measure** distances and areas, and save **bookmarks**.

## Field data in
- **Flood depth from high-water marks**: from a file of marks, USGS's marks for an event, or a water-surface grid,
  with a confidence and a reason for every structure, for you to review.
- **Elevation certificates**: SDE reads FEMA's fillable certificate and lists its values beside the assessment's to
  tick; for a scan, it shows an entry sheet. The PDF is kept with the structure.
- **Photo markup**: draw a water line, arrows, boxes, ellipses and text on a photo. The photo itself never changes;
  reports and the submission folder draw the marks on copies.

## Sharing
- **Web maps**: a folder with a web page of structures on a map, for the office (a dot for each structure, with the
  fields you choose) or the public (counts in grid squares, no owners, addresses hidden or cut to the block), with the
  event's summary beside the map.

## AI assistants
- **Claude and other AI assistants can work with an SDE database** through SDE's built-in MCP server: find structures,
  read and explain assessments, check them, and make reports and maps.
- **Changes only when you allow them**, in Settings, AI assistants. Every change is recorded in History as the
  assistant's and can be reverted there; Final and Letter sent assessments stay locked; nothing can be deleted.
- Settings, AI assistants has the setup to paste into Claude Code or Claude Desktop.

## Unchanged on purpose
- The damage calculation is FEMA's engine, unedited.
- Export.json, SDE_n folders and the .mdb import are exactly what FEMA's SDE writes and reads. Events, layers, markup,
  certificates, assignments, review comments and compliance cases live in SDE for Mac's own tables and are never
  exported to FEMA.
