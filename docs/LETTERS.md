# Owner letters

SDE for Mac can write a letter to each owner from a Word (.docx) template: a substantial damage notice, a "not
substantially damaged" notice, or a substantial improvement determination. You edit the templates in Word (or in
Pages, saving as .docx) to suit your ordinance and letterhead; SDE fills in the details for each structure.

## Before you send anything

The starter templates are a plain-English starting point written in the spirit of the sample substantial damage
notice in FEMA P-758 (*Substantial Improvement/Substantial Damage Desk Reference*), Appendix D. They are not legal
advice. **Review and adapt them with your community's counsel before you use them.** In particular, check:

- the threshold (the starter damage letters say 50 percent, the NFIP minimum; your ordinance may be stricter),
- what your ordinance requires of a substantially damaged or substantially improved building,
- how an owner can appeal, and any deadline (add it with an extra field, for example «AppealDeadline»),
- whether your community counts damage or improvements cumulatively.

## The starter templates

| File | Kind (used in file names) | When |
|---|---|---|
| Substantial damage notice.docx | Substantial damage | FEMA's result is "Substantially Damaged" |
| Not substantially damaged notice.docx | Not substantially damaged | FEMA's result is "Not Substantially Damaged" |
| Substantial improvement determination.docx | Substantial improvement | After an SI determination for a project |

The Compliance page has three more starter letters for a substantially damaged building's compliance case:

| File | Kind (used in file names) | When |
|---|---|---|
| Substantial damage options and deadline.docx | Compliance options | The ways to comply (elevate, relocate, demolish, floodproof for non-residential buildings) and the deadline |
| ICC support letter.docx | ICC support | For the owner's flood insurance adjuster, confirming the determination for an ICC claim |
| Compliance deadline reminder.docx | Deadline reminder | When the deadline is near or has passed and the case is still open |

A compliance letter is recorded on the case's timeline and, for a substantial damage case, in the assessment's
letter log.

SDE writes the starter templates only when they are missing, so your edits are never overwritten. Each starter
letter has the community and office in the page header, contact details in the footer, and a table of the facts.

## Placeholders

A placeholder is a field name between guillemets: «OwnerName». On a Mac, type « with Option-\ and » with
Option-Shift-\. Placeholders work anywhere: body text, tables, headers, footers, footnotes and endnotes. Field
names are not case-sensitive. Type them as plain text (not as Word mail-merge fields).

The value takes the formatting of the place where the placeholder starts, so make the whole placeholder bold if
you want the value bold. Word sometimes splits what you type into pieces behind the scenes; SDE handles that.

A placeholder that is not a field is left in the letter as it is, and SDE lists it so you can fix the template
or add the field.

## Fields

Every field is always available. A field with nothing to put in it is left blank.

**Letter and sender** (Settings)

| Field | Example |
|---|---|
| Today | October 20, 2026 |
| CommunityName | The community name from Settings, or the structure's NFIP community name |
| NFIPCommunityID | 999999 |
| OfficeName | Floodplain Management Office |
| OfficeAddress | The office's address (several lines) |
| OfficePhone, OfficeEmail | How owners reach you |
| OfficialName, OfficialTitle | Who signs the letter |

**Owner and mailing address**

| Field | Example |
|---|---|
| OwnerName | Pat Sample |
| OwnerFirstName, OwnerLastName | Pat / Sample |
| MailingName | The mailing name, or the owner's name |
| MailingCareOf | c/o Agent |
| MailingStreet | PO Box 1 |
| MailingCityStateZip | Sample City, OH 00000-1234 |
| MailingAddress | Care-of, street, and city, state and ZIP on separate lines |

When no mailing address is entered, the mailing fields use the structure's address.

**Structure**

| Field | Example |
|---|---|
| PropertyAddress | 12 Example Street |
| PropertyCity, PropertyState, PropertyZip | Sample City / Ohio / 00000 |
| PropertyCityStateZip | Sample City, OH 00000 |
| ParcelNumber | 000-111-222 |
| FloodZone | AE |
| FIRMPanel | 99999C0000E |
| BFE | 101.50 |

**Damage assessment**

| Field | Example |
|---|---|
| DamageDate, InspectionDate | September 14, 2026 |
| InspectorName | A. Inspector |
| CauseOfDamage | Flood |
| PercentDamaged | 52.3% |
| DamageDetermination | Substantially Damaged (FEMA's result, as stored) |
| DeterminationWording | "is substantially damaged", "is not substantially damaged", or "has not yet been determined to be substantially damaged or not" |
| BuildingValue, BuildingValueBasis | $110,000.00 / Computed Actual Cash Value |
| RepairCost, RepairCostBasis | $57,530.00 / Computed Damages |
| Status | The review status, e.g. Final |

The letters use FEMA's stored result. Cumulative damage and substantial improvement are separate findings and
never change an assessment's percent damaged or its determination.

**Substantial improvement**

| Field | Example |
|---|---|
| ProjectDescription | Second-story addition |
| PermitNumber | BP-0002 |
| ProjectDate | June 1, 2026 |
| ImprovementCost | $60,000.00 (the cost counted, including earlier projects in the look-back period) |
| MarketValue | $110,000.00 |
| ImprovementPercent | 54.5% |
| SIThreshold | 50% |
| LookbackPeriod | "this project only", or "5 years" |
| SIDetermination | Substantial Improvement, Not Substantial Improvement, or Cannot be determined |
| SIDeterminationWording | "is a substantial improvement", "is not a substantial improvement", or "cannot be determined until the market value of the structure is known" |

**Compliance case** (compliance letters, from the case on the Compliance page)

| Field | Example |
|---|---|
| CompliancePath | Elevate, Relocate, Demolish, "Floodproof (non-residential)", or blank until chosen |
| ComplianceDeadline | March 1, 2027 |
| CompliancePermitNumber | P-0001 |
| IccClaimNumber | The owner's Increased Cost of Compliance claim number |
| IccStatus | Not filed, Filed, Approved, Paid or Denied |
| IccAmount | $30,000.00 |
| EcReceivedDate, FinalInspectionDate | February 20, 2027 |

Your own templates can use extra fields that the app is given (for example «AppealDeadline»).

## Writing a batch

SDE writes a new folder named "Letters yyyy-MM-dd HHmm" (with " (2)" added if that name is taken) holding one file
per letter, named "<address> (<kind>).docx", for example "12 Example Street (Substantial damage).docx". Two letters
with the same name get " (2)", " (3)" and so on. If you stop the batch, or something goes wrong, the partly written
folder is removed.
