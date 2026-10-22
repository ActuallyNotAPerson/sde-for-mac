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
- **Edit your own layers**: draw points, lines and areas, move and delete corners with snapping, undo and redo, and
  type values in the attribute table. **Drag a structure's dot** to fix its location, with a question first.
- **Select by location** (touching, inside, containing or within a distance of other features) and **copy values
  onto structures** from a layer, such as the parcel number or FEMA flood zone, with a preview to tick.
- **Print the map** to a Letter-size PDF with a legend, scale bar, north arrow and sources.
- **Keep an area offline**: download the USGS map for an area before going where there is no connection.

## In the field
- **Field teams**: make teams, assign structures from Structures or the map, put each team's structures in a driving
  order, and print a route sheet and a worksheet per structure with a small map and a short code the phone form's
  search accepts. The Assignments page and a Dashboard chart show each team's progress, and the phone form shows each
  inspector their team's list.
- **Work packages**: take assessments to a Mac in the field on a file, work there without a connection, and check the
  work back in at the office with its history. While out, the assessments are read-only at the office.

## In the office
- **Review**: a queue of assessments waiting for review, quality checks that flag likely mistakes (set one aside with
  a reason, or bring it back), reviewers' comments with replies, and Mark reviewed. Before an export or a submission
  folder, SDE asks about open errors.
- **Compliance and ICC**: a case for each substantially damaged structure (or substantial improvement) with its path,
  permit, deadline, ICC claim, elevation certificate and final inspection, a timeline of notes, three starter letters,
  and PDF and CSV lists. The Dashboard shows open and overdue cases.

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
  read and explain assessments, run the quality checks, summarise an event, list compliance cases and map layers, and
  make reports, GeoJSON files and web maps (into an "AI outputs" folder beside the database).
- **Changes only when you allow them**, in Settings, AI assistants: fill in fields, set Draft, Reviewed or Appeal, add
  structures, events and review comments, and assign teams. Every change is recorded in History as the assistant's
  and can be reverted from Settings; Final and Letter sent assessments stay locked; nothing can be deleted.
- Settings, AI assistants has the setup to paste into Claude Code or Claude Desktop.

## Settings
- **Map & GIS**: the background map, whether FEMA's flood zones start on, the saved map pieces, and leaving owner names
  out of GIS files (on by default).
- **AI assistants**: whether assistants may change the database, the setup to connect one, and their recent changes.

## Unchanged on purpose
- The damage calculation is FEMA's engine, unedited.
- A 2.0 app can still open a 2.5 database. While assessments are out in a work package, edit them only in 2.5: a
  change 2.0 makes to one is kept, and the work package's copy of it isn't checked in.
- Export.json, SDE_n folders and the .mdb import are exactly what FEMA's SDE writes and reads. Events, layers, markup,
  certificates, assignments, review comments and compliance cases live in SDE for Mac's own tables and are never
  exported to FEMA.
