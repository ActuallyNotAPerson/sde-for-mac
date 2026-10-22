# Online service fixtures

These JSON files are hand-written to the documented response formats of the U.S. Census Bureau geocoder and the
ArcGIS REST API behind FEMA's National Flood Hazard Layer (NFHL). They are not recordings of live responses: the
values are plausible but invented, and the only real-world fact in them is the position of a public landmark
(the U.S. Capitol). They exist so the parsing code can be tested without a network.

The USGS Short-Term Network (STN) fixtures are hand-written too, with invented values and placeholder text ("Team
member 1") where the service puts staff names. The high-water mark records copy the field names and value types of
one live answer from `https://api.waterdata.usgs.gov/stnservices/hwms/filteredhwms/?states=VT` (2026-10-21); two
records send their numbers as text, as the service sometimes does. The events file follows the same naming but was not
checked live.

Verify against the live services on a machine with internet access; see docs/ONLINE-SERVICES.md.

| File | Shape |
| --- | --- |
| census-capitol-match.json | Geocoder answer with two address matches |
| census-no-match.json | Geocoder answer with no matches |
| nfhl-zone-ae.json | Flood hazard zone layer: zone AE with a base flood elevation |
| nfhl-zone-floodway.json | Zone AE, subtype FLOODWAY, BFE -9999 (none) |
| nfhl-zone-x-no-bfe.json | Zone X, minimal flood hazard, BFE -9999 |
| nfhl-zone-several.json | A point on a boundary: zones X, D, shaded X, AE and VE, in that order |
| nfhl-empty.json | A layer query with no features |
| nfhl-panel.json | FIRM panel layer, effective date as epoch milliseconds |
| nfhl-community.json | Political jurisdiction layer |
| nfhl-error.json | ArcGIS error object returned with HTTP 200 |
| stn-hwms.json | STN filtered high-water marks: numbers as numbers and as text, a mark with no datum, one not surveyed yet (no elevation), one at 0, 0 |
| stn-events.json | STN events, including one with numbers as text and one with no name |
