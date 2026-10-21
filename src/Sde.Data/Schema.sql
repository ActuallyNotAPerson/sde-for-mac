-- SQLite schema for the SDE Mac port.
-- A 1:1 translation of FEMA's SDEDatabase.mdb (decomp/database/schema.access.sql): same table and column
-- names so SDE Export.json, the original reports and the original code all map directly.
--
-- Type mapping (Jet → SQLite):
--   Replication ID (GUID)  → TEXT, lowercase "D" format (what Guid.ToString() gives, which the BLL compared against)
--   Text / Memo            → TEXT
--   Long Integer / Integer → INTEGER
--   Boolean                → INTEGER 0/1 (Jet stored True as -1; see PORTING-NOTES.md)
--   DateTime               → TEXT "yyyy-MM-dd HH:mm:ss"
--   Currency / Numeric(p,s)→ REAL  (the data layer rounds to the Jet scale on write: Currency 4 dp, Numeric(p,s) s dp)
--   Double                 → REAL
--   AutoNumber             → INTEGER PRIMARY KEY AUTOINCREMENT
--
-- Foreign keys: enforced for the Property → Assessment → child-rows tree (Jet enforced these too).
-- Lookup/State/County references are not enforced; the original stored 0 for "none".

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS SchemaInfo (
	Key   TEXT PRIMARY KEY,
	Value TEXT
);

-- ───────────────────────── Reference data (seeded from FEMA's database) ─────────────────────────

CREATE TABLE IF NOT EXISTS LookupType (
	LookupTypeID INTEGER PRIMARY KEY AUTOINCREMENT,
	LookupType   TEXT
);

CREATE TABLE IF NOT EXISTS Lookup (
	LookupID        INTEGER PRIMARY KEY AUTOINCREMENT,
	LookupTypeID    INTEGER REFERENCES LookupType(LookupTypeID),
	LookupFieldName TEXT,
	LookupValue1    TEXT,
	LookupValue2    TEXT,
	OldID           INTEGER,
	zOrder          INTEGER
);
CREATE INDEX IF NOT EXISTS IX_Lookup_Type ON Lookup(LookupTypeID);

CREATE TABLE IF NOT EXISTS State (
	StateID  INTEGER PRIMARY KEY AUTOINCREMENT,
	StateAbb TEXT,
	State    TEXT
);

CREATE TABLE IF NOT EXISTS County (
	CountyID INTEGER PRIMARY KEY AUTOINCREMENT,
	County   TEXT,
	StateID  INTEGER
);
CREATE INDEX IF NOT EXISTS IX_County_State ON County(StateID);

CREATE TABLE IF NOT EXISTS StreetSuffixAbbreviation (
	StreetSuffixAbbreviationID INTEGER PRIMARY KEY AUTOINCREMENT,
	StreetSuffixID             INTEGER,
	StreetSuffixAbbreviation   TEXT
);

CREATE TABLE IF NOT EXISTS CostAdjustment_ResidenceType (
	ID            INTEGER PRIMARY KEY AUTOINCREMENT,
	ResidenceType INTEGER,
	LookupID      INTEGER
);

CREATE TABLE IF NOT EXISTS DepreciationValue (
	ID                             INTEGER PRIMARY KEY AUTOINCREMENT,
	DepreciationLookupID           INTEGER,
	DepreciationPercentage         REAL,
	StoriesLookupID                INTEGER,
	StructureUseLookupID           INTEGER,
	OriginalDepreciationPercentage REAL
);

CREATE TABLE IF NOT EXISTS ValidationRule (
	ValidationID     INTEGER PRIMARY KEY AUTOINCREMENT,
	AssessmentType   TEXT,
	Tab              TEXT,
	FieldName        TEXT,
	ValidationRule   TEXT,
	PinColor         TEXT,
	PinMessage       TEXT,
	ControlName      TEXT,
	ValidationType   TEXT,
	ValidationTypeID INTEGER,
	ModelName        TEXT
);

CREATE TABLE IF NOT EXISTS LatLongValidation (
	StateID  INTEGER PRIMARY KEY,
	StateAbb TEXT,
	State    TEXT,
	LongMin  REAL,
	LongMax  REAL,
	LatMin   REAL,
	LatMax   REAL
);

-- Legacy tables carried over from SDE 2.x (unused by SDE 3.0's code; kept so imports are lossless).
CREATE TABLE IF NOT EXISTS InspectionStatus (InspectionStatusID INTEGER PRIMARY KEY, InspectionStatus TEXT);
CREATE TABLE IF NOT EXISTS ResidenceType (ResidenceTypeID INTEGER PRIMARY KEY AUTOINCREMENT, ResidenceType TEXT);
CREATE TABLE IF NOT EXISTS RoofCoveringType (RoofCoveringTypeID INTEGER PRIMARY KEY AUTOINCREMENT, RoofCoveringType TEXT);
CREATE TABLE IF NOT EXISTS StructureShape (StructureShapeID INTEGER PRIMARY KEY AUTOINCREMENT, StructureShape TEXT);

-- ───────────────────────── Structures and assessments ─────────────────────────

CREATE TABLE IF NOT EXISTS Property (
	PropertyID            TEXT PRIMARY KEY NOT NULL,
	TeamName              TEXT,
	OwnerFirstName        TEXT,
	OwnerLastName         TEXT,
	StreetNumber          TEXT,
	StreetName            TEXT,
	StreetSuffixID        INTEGER,
	City                  TEXT,
	CountyID              INTEGER,
	StateID               INTEGER,
	Latitude              REAL,   -- Numeric(18,6)
	Longitude             REAL,   -- Numeric(18,6)
	StructureTypeID       INTEGER,   -- 1 Residential, 2 Non-Residential
	FirstFloorElevation   REAL,   -- Numeric(18,2)
	YearOfConstruction    INTEGER,
	StructureQualityID    INTEGER,
	FoundationTypeID      INTEGER,
	SuperStructureTypeID  INTEGER,
	RoofCoveringTypeID    INTEGER,
	ExteriorFinishTypeID  INTEGER,
	HVACTypeID            INTEGER,
	StoryTypeID           INTEGER,
	StructureUseID        INTEGER,
	ResidenceTypeID       INTEGER,
	SprinklerSystem       INTEGER DEFAULT 0,
	Conveyance            INTEGER DEFAULT 0,
	StructureInformation  TEXT,
	SquareFootage         REAL,   -- Numeric(18,2)
	StructureShapeID      INTEGER,
	BaseCostPerSqFt       TEXT,
	Subdivision           TEXT,
	ParcelNumber          TEXT,
	LotNumber             TEXT,
	NFIPCommunityName     TEXT,
	FIRMPanelNumber       TEXT,
	PanelSuffix           TEXT,
	FIRMPanelDate         TEXT,
	FIRMZone              INTEGER DEFAULT 0,
	BFE                   REAL,   -- Numeric(18,2)
	RegulatoryFloodway    INTEGER DEFAULT 0,
	ImportDate            TEXT,
	Active                INTEGER NOT NULL DEFAULT 0,
	MailingOwnerFirstName TEXT,
	MailingOwnerLastName  TEXT,
	MailingStreetNumber   TEXT,
	MailingStreetName     TEXT,
	MailingStreetSuffixID INTEGER DEFAULT 0,
	MailingCity           TEXT,
	MailingStateID        INTEGER DEFAULT 0,
	MailingCountyID       INTEGER DEFAULT 0,
	MailingPhoneNumber    TEXT,
	MailingCareOf         TEXT,
	PhoneNumber           TEXT,
	CellPhone             TEXT,
	Datum                 TEXT,
	CommunityInformation  TEXT,
	LastUpdate            TEXT,
	ZipCode               TEXT,
	MailingZipCode        TEXT,
	NFIPCID               TEXT
);

CREATE TABLE IF NOT EXISTS Assessment (
	AssessmentID             TEXT PRIMARY KEY NOT NULL,
	PropertyID               TEXT REFERENCES Property(PropertyID),
	DateofInspection         TEXT,
	InspectorName            TEXT,
	InspectorPhoneNumber     TEXT,
	DateDamageOccurred       TEXT,
	CauseofDamageID          INTEGER DEFAULT 0,
	PhysicalDamage           INTEGER NOT NULL DEFAULT 0,   -- the "damage undetermined" checkbox
	DurationOfFlood          REAL DEFAULT 0,            -- Numeric(18,0)
	DurationOfFloodUnitsID   INTEGER DEFAULT 0,
	DepthOfFlood             REAL DEFAULT 0,
	DepthOfFloodAboveFFE     REAL DEFAULT 0,            -- Numeric(18,4)
	DepreciationRatingID     INTEGER DEFAULT 0,
	DepreciationValue        REAL DEFAULT 0,
	Active                   INTEGER NOT NULL DEFAULT 0,
	PercentDamage            REAL DEFAULT 0,            -- Numeric(4,1), stored as a percentage (×100)
	UndeterminedDamageTypeID INTEGER DEFAULT 0,
	BaseCost                 REAL DEFAULT 0,            -- Currency
	CostData                 TEXT,
	CostDataDate             TEXT,
	MarketAppraisal          REAL DEFAULT 0,            -- Currency
	TaxAssessedValue         REAL DEFAULT 0,            -- Currency
	FactorAdjustment         REAL DEFAULT 0,            -- Numeric(18,2)
	ContractorEstimate       REAL DEFAULT 0,            -- Currency
	CommunityEstimate        REAL DEFAULT 0,            -- Currency
	ExistingImprovementPercent REAL DEFAULT 0,          -- Numeric(18,2)
	ValueOfBuildingID        INTEGER DEFAULT 0,            -- 390 ACV / 391 appraisal / 392 adj. tax value
	CostOfRepairsID          INTEGER DEFAULT 0,            -- 393 computed / 394 contractor / 395 community
	CauseOfDamageDescription TEXT,
	DepreciationExplanation  TEXT,
	ReplacementCost          REAL DEFAULT 0,            -- Currency
	ComputedDamages          REAL DEFAULT 0,            -- Currency
	ComputedActualCashValue  REAL DEFAULT 0,            -- Currency
	RepairReconstructPercent REAL DEFAULT 0,
	DamageMessage            TEXT,
	TotalReplacementCost     REAL DEFAULT 0,            -- Currency
	TotalEstimatedDamages    REAL DEFAULT 0,            -- Currency
	LastUpdate               TEXT,
	TotalAdjustments         REAL DEFAULT 0,            -- Currency
	ReplacementCostSqFt      REAL DEFAULT 0,            -- Currency
	GeographicAdjustment     REAL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_Assessment_Property ON Assessment(PropertyID);

CREATE TABLE IF NOT EXISTS ElementPercentage (
	ElementID         TEXT PRIMARY KEY NOT NULL,
	AssessmentID      TEXT REFERENCES Assessment(AssessmentID),
	CalcID            INTEGER,
	ElementName       TEXT,
	UserDamage        REAL,
	ElementPercentage REAL,
	ItemCost          REAL,   -- Currency
	DamageValues      REAL    -- Currency
);
CREATE INDEX IF NOT EXISTS IX_ElementPercentage_Assessment ON ElementPercentage(AssessmentID);

CREATE TABLE IF NOT EXISTS CostAdjustmentResult (
	ResultID               TEXT PRIMARY KEY NOT NULL,
	CostAdjustmentLookupID INTEGER,
	AssessmentID           TEXT REFERENCES Assessment(AssessmentID),
	Quantity               REAL,
	UnitCost               REAL,   -- Currency
	TotalCost              REAL,   -- Currency
	Units                  TEXT
);
CREATE INDEX IF NOT EXISTS IX_CostAdjustmentResult_Assessment ON CostAdjustmentResult(AssessmentID);

CREATE TABLE IF NOT EXISTS AdditionalAdjustment (
	AdditionalAdjustmentID TEXT PRIMARY KEY NOT NULL,
	AssessmentID           TEXT REFERENCES Assessment(AssessmentID),
	AdjustmentName         TEXT,
	Quantity               REAL,
	UnitCost               REAL,   -- Currency
	TotalCost              REAL,   -- Currency
	zOrder                 INTEGER
);
CREATE INDEX IF NOT EXISTS IX_AdditionalAdjustment_Assessment ON AdditionalAdjustment(AssessmentID);

CREATE TABLE IF NOT EXISTS Attachment (
	AttachmentID   INTEGER PRIMARY KEY AUTOINCREMENT,
	AssessmentID   TEXT,
	AttachmentType TEXT,
	FileName       TEXT,
	Description    TEXT,
	DefaultImage   INTEGER NOT NULL DEFAULT 0,
	Active         INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_Attachment_Assessment ON Attachment(AssessmentID);

CREATE TABLE IF NOT EXISTS Shapes (
	ShapeID      INTEGER PRIMARY KEY AUTOINCREMENT,
	PropertyID   TEXT,
	ShapeType    TEXT,              -- l / t / r / u / c (c = total entered directly)
	TotalSqFt    REAL DEFAULT 0, -- Numeric(18,0)
	LengthA      REAL DEFAULT 0,
	LengthB      REAL DEFAULT 0,
	LengthC      REAL DEFAULT 0,
	LengthD      REAL DEFAULT 0,
	WidthA       REAL DEFAULT 0,
	WidthB       REAL DEFAULT 0,
	WidthC       REAL DEFAULT 0,
	WidthD       REAL DEFAULT 0,
	StoriesA     INTEGER DEFAULT 0,
	StoriesB     INTEGER DEFAULT 0,
	StoriesC     INTEGER DEFAULT 0,
	DescriptionA TEXT,
	DescriptionB TEXT,
	DescriptionC TEXT
);
CREATE INDEX IF NOT EXISTS IX_Shapes_Property ON Shapes(PropertyID);

CREATE TABLE IF NOT EXISTS CustomField (
	CustomFieldID      TEXT PRIMARY KEY NOT NULL,
	CustomField        TEXT,
	OriginalDatabaseID TEXT NOT NULL DEFAULT '1',
	IsDefault          INTEGER NOT NULL DEFAULT 0,
	isUnique           INTEGER NOT NULL DEFAULT 0,
	DateOfLastMod      TEXT,
	zOrder             INTEGER
);

CREATE TABLE IF NOT EXISTS CustomField_Results (
	ObjectIdentifier TEXT NOT NULL,                -- a PropertyID or an AssessmentID
	CustomFieldID    TEXT NOT NULL REFERENCES CustomField(CustomFieldID) ON DELETE CASCADE,
	Result           TEXT,
	ResultType       TEXT,                         -- "property" or "assessment"
	zOrder           INTEGER,
	PRIMARY KEY (ObjectIdentifier, CustomFieldID)
);

CREATE TABLE IF NOT EXISTS LastFiveAssesments (
	AssessmentID  TEXT PRIMARY KEY NOT NULL,
	PropertyID    TEXT,
	StreetAddress TEXT,
	StructureType TEXT,
	LastUpdate    TEXT
);

-- ───────────────────────── User settings ─────────────────────────

CREATE TABLE IF NOT EXISTS "Default" (
	DefaultValueID      INTEGER PRIMARY KEY AUTOINCREMENT,
	City                TEXT,
	State               INTEGER,
	County              INTEGER,
	Zip                 TEXT,
	NFIPCommunityID     TEXT,
	NFIPCommunityName   TEXT,
	Datum               TEXT,
	YearOfConstruction  TEXT,
	DateDamagedOccurred TEXT,
	CauseOfDamage       TEXT,
	DurationOfFlood     TEXT,
	DurationOfFloodUnit TEXT,
	RegulatoryFloodway  INTEGER,
	InspectedBy         TEXT,
	InspectedPhone      TEXT,
	DateOfInspection    TEXT,
	FirmPanelNumber     TEXT,
	Suffix              TEXT,
	DateOfFirmPanel     TEXT,
	FirmZone            INTEGER,
	BFE                 TEXT,
	CommunityUse        TEXT,
	LocalMultiplier     TEXT,
	CostDataDate        TEXT,
	CostDataRef         TEXT,
	BaseCost            REAL
);

CREATE TABLE IF NOT EXISTS UserDefault (
	UserDefaultID  INTEGER PRIMARY KEY AUTOINCREMENT,
	LatLong        INTEGER NOT NULL DEFAULT 0,
	AutoSpellCheck INTEGER NOT NULL DEFAULT 0,
	UseDefaults    INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS Notes (
	NoteID   TEXT PRIMARY KEY NOT NULL,
	NoteName TEXT,
	Notes    TEXT,
	Active   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS EnterpriseImportSettings (
	EIS_ID                 INTEGER PRIMARY KEY AUTOINCREMENT,
	EISGuid                TEXT,
	EISName                TEXT,
	OwnersFirstName        TEXT,
	OwnersLastName         TEXT,
	PhoneNumber            TEXT,
	AddressLine1           TEXT,
	AddressLine2           TEXT,
	PostGeoDir             TEXT,
	ApartmentUnit          TEXT,
	StreetSuffix           TEXT,
	City                   TEXT,
	State                  TEXT,
	County                 TEXT,
	Zip                    TEXT,
	NFIPID                 TEXT,
	NFIPName               TEXT,
	Latitude               TEXT,
	Longitude              TEXT,
	Subdivision            TEXT,
	ParcelNum              TEXT,
	LotNum                 TEXT,
	Datum                  TEXT,
	FFE                    TEXT,
	StructureType          TEXT,
	YrOfCon                TEXT,
	FirmPanel              TEXT,
	TotalSqFt              TEXT,
	DateOfFirmPanel        TEXT,
	FirmZone               TEXT,
	BFE                    TEXT,
	NFIPSuffix             TEXT,
	RegFloodway            TEXT,
	OwnersFirstNameParsing TEXT,
	OwnersLastNameParsing  TEXT,
	AddressLine1Parsing    TEXT,
	AddressLine2Parsing    TEXT,
	StreetSuffixParsing    TEXT
);

CREATE TABLE IF NOT EXISTS DatabaseInfo (
	DataBaseID             TEXT PRIMARY KEY NOT NULL,
	DataBaseType           TEXT,
	DataBaseName           TEXT,
	ServerConnectionString TEXT
);

CREATE TABLE IF NOT EXISTS Manage (
	ID          INTEGER PRIMARY KEY AUTOINCREMENT,
	MUADateTime TEXT,
	MUA         INTEGER
);

-- ───────────────────────── Mac-only; never exported ─────────────────────────
-- Tables FEMA's SDE doesn't have. ExchangeService reads only FEMA's tables, so nothing here reaches Export.json,
-- and SdeDatabase.Open adds them to databases made by an earlier version (SchemaVersion stays 1, so those versions
-- can still open the file).

-- Links an assessment to the record it was imported from (an inspection in a permit system). One row per assessment.
CREATE TABLE IF NOT EXISTS ExternalLink (
	AssessmentID TEXT PRIMARY KEY NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	Source       TEXT NOT NULL,                           -- the import profile's source name, e.g. 'Accela'
	RecordId     TEXT NOT NULL COLLATE NOCASE,            -- e.g. BLD-2026-00123
	InspectionId TEXT NOT NULL DEFAULT '' COLLATE NOCASE, -- '' = one assessment per record
	SourceValues TEXT,                                    -- JSON {fieldKey: source text} as last imported
	SourceFile   TEXT,
	ImportedOn   TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ExternalLink_Key ON ExternalLink(Source, RecordId, InspectionId);

-- ───────────────────────── Mac-only, added in v2.0 (MacSchemaVersion 2) ─────────────────────────
-- Every table below refers to FEMA's rows with ON DELETE CASCADE (or not at all), so older versions of this app, which
-- don't know these tables, can still delete structures. None of them is ever exported.

-- Per-database settings as JSON values: list filters and saved views state, cumulative damage and SI settings, the
-- depth table in use.
CREATE TABLE IF NOT EXISTS MacSetting (
	Key   TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
	Value TEXT
);

-- "Recently deleted". The rows stay in FEMA's tables with Active = 0 (FEMA's own SDE deleted that way), so every
-- version of this app and every export already leaves them out. No foreign keys: rows whose target is gone are dropped
-- when the database is opened.
CREATE TABLE IF NOT EXISTS DeletedItem (
	ItemKey      TEXT PRIMARY KEY NOT NULL,  -- 'S:<PropertyID>', 'A:<AssessmentID>' or 'P:<FileName>'
	Kind         TEXT NOT NULL,              -- Structure, Assessment, Photo
	PropertyID   TEXT,
	AssessmentID TEXT,
	FileName     TEXT,
	Label        TEXT,
	Detail       TEXT,                       -- JSON list of the assessment IDs deleted with a structure
	DeletedOn    TEXT NOT NULL,
	DeletedBy    TEXT,
	PurgeAfter   TEXT NOT NULL
);

-- Where an assessment is in the review workflow. No row = Draft. Final and LetterSent are locked.
CREATE TABLE IF NOT EXISTS AssessmentStatus (
	AssessmentID     TEXT PRIMARY KEY NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	Status           TEXT NOT NULL,          -- Draft, Reviewed, Final, LetterSent, Appeal
	ChangedOn        TEXT,
	ChangedBy        TEXT,
	Reason           TEXT,
	LockedLastUpdate TEXT                    -- Assessment.LastUpdate when it was locked (an older version may edit it)
);

-- Who changed what, and when.
CREATE TABLE IF NOT EXISTS EditHistory (
	HistoryID    INTEGER PRIMARY KEY AUTOINCREMENT,
	PropertyID   TEXT REFERENCES Property(PropertyID) ON DELETE CASCADE,
	AssessmentID TEXT REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	ChangedOn    TEXT NOT NULL,
	ChangedBy    TEXT,
	Via          TEXT,                       -- App, Bulk edit, Import, Phone, Restore, ...
	BatchId      TEXT,                       -- one bulk edit or import
	Field        TEXT,                       -- field key (FieldCatalog), or NULL for an event such as "Created"
	Label        TEXT,
	OldValue     TEXT,
	NewValue     TEXT
);
CREATE INDEX IF NOT EXISTS IX_EditHistory_Assessment ON EditHistory(AssessmentID, ChangedOn);
CREATE INDEX IF NOT EXISTS IX_EditHistory_Property ON EditHistory(PropertyID, ChangedOn);
CREATE INDEX IF NOT EXISTS IX_EditHistory_Batch ON EditHistory(BatchId);

-- Named filters for the Structures list.
CREATE TABLE IF NOT EXISTS SavedView (
	Name      TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
	State     TEXT NOT NULL,
	UpdatedOn TEXT
);

-- What a photo's own metadata says (keyed by the stored file name, which never changes).
CREATE TABLE IF NOT EXISTS PhotoInfo (
	FileName     TEXT PRIMARY KEY NOT NULL,
	AssessmentID TEXT REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	TakenOn      TEXT,
	Latitude     REAL,
	Longitude    REAL,
	OriginalName TEXT
);

-- Element percentages filled in from the depth table that nobody has accepted or changed yet.
CREATE TABLE IF NOT EXISTS DamageSuggestion (
	AssessmentID     TEXT NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	ElementName      TEXT NOT NULL,
	SuggestedPercent REAL NOT NULL,
	DepthFt          REAL,
	TableName        TEXT,
	SuggestedOn      TEXT,
	PRIMARY KEY (AssessmentID, ElementName)
);

-- Contractor bids an owner submitted, and the appeal of a determination.
CREATE TABLE IF NOT EXISTS ContractorBid (
	BidID        TEXT PRIMARY KEY NOT NULL,
	AssessmentID TEXT NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	Contractor   TEXT,
	Amount       REAL,
	BidDate      TEXT,
	Notes        TEXT
);

CREATE TABLE IF NOT EXISTS Appeal (
	AssessmentID TEXT PRIMARY KEY NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	ReceivedOn   TEXT,
	Basis        TEXT,
	Outcome      TEXT,                       -- Pending, Upheld, Revised, Withdrawn
	DecidedOn    TEXT,
	DecidedBy    TEXT,
	Notes        TEXT
);

-- Letters written to owners.
CREATE TABLE IF NOT EXISTS LetterLog (
	LetterID     TEXT PRIMARY KEY NOT NULL,
	AssessmentID TEXT NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	Kind         TEXT,
	Template     TEXT,
	GeneratedOn  TEXT,
	SentOn       TEXT,
	FileName     TEXT
);

-- Substantial improvement: improvement projects (permits) and the determinations made from them.
CREATE TABLE IF NOT EXISTS ImprovementProject (
	ProjectID       TEXT PRIMARY KEY NOT NULL,
	PropertyID      TEXT NOT NULL REFERENCES Property(PropertyID) ON DELETE CASCADE,
	PermitNumber    TEXT,
	Description     TEXT,
	ProjectDate     TEXT,
	Cost            REAL,
	CostBasis       TEXT,
	Included        INTEGER NOT NULL DEFAULT 1,
	ExclusionReason TEXT,
	CreatedOn       TEXT
);
CREATE INDEX IF NOT EXISTS IX_ImprovementProject_Property ON ImprovementProject(PropertyID);

CREATE TABLE IF NOT EXISTS SIDetermination (
	DeterminationID  TEXT PRIMARY KEY NOT NULL,
	PropertyID       TEXT NOT NULL REFERENCES Property(PropertyID) ON DELETE CASCADE,
	DeterminedOn     TEXT NOT NULL,
	MarketValue      REAL,
	MarketValueBasis TEXT,
	LookbackYears    INTEGER,
	ThresholdPercent REAL NOT NULL,
	CumulativeCost   REAL NOT NULL,
	Percent          REAL,
	Result           TEXT,
	DeterminedBy     TEXT,
	Notes            TEXT
);

-- Links a structure to a record in another system (an assessor's parcel, a permit), for imports that update structures.
CREATE TABLE IF NOT EXISTS StructureLink (
	Source       TEXT NOT NULL,
	RecordId     TEXT NOT NULL COLLATE NOCASE,
	PropertyID   TEXT NOT NULL REFERENCES Property(PropertyID) ON DELETE CASCADE,
	SourceValues TEXT,
	SourceFile   TEXT,
	ImportedOn   TEXT,
	PRIMARY KEY (Source, RecordId)
);
CREATE INDEX IF NOT EXISTS IX_StructureLink_Property ON StructureLink(PropertyID);

-- ───────────────────────── Mac-only, added in v2.5 (MacSchemaVersion 3) ─────────────────────────
-- Same rules as v2.0's tables: foreign keys to FEMA's rows use ON DELETE CASCADE (or there are none, and rows whose
-- target is gone are dropped when the database is opened), keys are GUIDs or stored file names (never ElementID or
-- AttachmentID, which change on every save), and none of it is ever exported. Every v2.5 table is created here at
-- once, so later work never has to change this file.

-- The version of each structure and assessment, so two programs using one database (this app, the AI assistant
-- server, the app on another Mac via a shared folder) notice each other's saves. No row means version 0: never saved
-- by v2.5. Seq orders every save in this database; WriterId names the running copy of the app that saved.
CREATE TABLE IF NOT EXISTS RowVersion (
	ObjectID  TEXT PRIMARY KEY NOT NULL,   -- a PropertyID or an AssessmentID
	Kind      TEXT NOT NULL,               -- 'P' structure, 'A' assessment
	Version   INTEGER NOT NULL,
	Seq       INTEGER NOT NULL,
	WriterId  TEXT,
	ChangedOn TEXT
);
CREATE INDEX IF NOT EXISTS IX_RowVersion_Seq ON RowVersion(Seq);

-- A disaster (a flood, a storm) and the assessments made after it. An assessment belongs to at most one event.
CREATE TABLE IF NOT EXISTS Event (
	EventID       TEXT PRIMARY KEY NOT NULL,
	Name          TEXT NOT NULL,
	Kind          TEXT,                    -- Flood, Hurricane, Tornado, Fire, Other
	DRNumber      TEXT,                    -- a disaster declaration number, e.g. DR-4999
	StartDate     TEXT,
	EndDate       TEXT,
	Notes         TEXT,
	DatumOffsetFt REAL,                    -- added to high-water mark elevations from another datum, or NULL
	CreatedOn     TEXT,
	CreatedBy     TEXT
);

CREATE TABLE IF NOT EXISTS EventAssessment (
	AssessmentID TEXT PRIMARY KEY NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	EventID      TEXT NOT NULL REFERENCES Event(EventID) ON DELETE CASCADE,
	AddedOn      TEXT
);
CREATE INDEX IF NOT EXISTS IX_EventAssessment_Event ON EventAssessment(EventID);

-- Field teams and the structures assigned to them.
CREATE TABLE IF NOT EXISTS Team (
	TeamID    TEXT PRIMARY KEY NOT NULL,
	Name      TEXT NOT NULL,
	Members   TEXT,                        -- free text: names, phone numbers
	Color     TEXT,                        -- '#RRGGBB', for the map
	Active    INTEGER NOT NULL DEFAULT 1,
	CreatedOn TEXT
);

CREATE TABLE IF NOT EXISTS Assignment (
	AssignmentID TEXT PRIMARY KEY NOT NULL,
	PropertyID   TEXT NOT NULL REFERENCES Property(PropertyID) ON DELETE CASCADE,
	TeamID       TEXT NOT NULL REFERENCES Team(TeamID) ON DELETE CASCADE,
	EventID      TEXT REFERENCES Event(EventID) ON DELETE SET NULL,
	AssignedOn   TEXT,
	AssignedBy   TEXT,
	DueDate      TEXT,
	RouteOrder   INTEGER,                  -- the stop's place in the team's route
	Status       TEXT NOT NULL DEFAULT 'Assigned',   -- Assigned, Visited, Done, Skipped
	ShortCode    TEXT,                     -- the code printed (and in the QR code) on the field worksheet
	Notes        TEXT
);
CREATE INDEX IF NOT EXISTS IX_Assignment_Team ON Assignment(TeamID);
CREATE INDEX IF NOT EXISTS IX_Assignment_Property ON Assignment(PropertyID);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Assignment_ShortCode ON Assignment(ShortCode) WHERE ShortCode IS NOT NULL;

-- Review: comments on an assessment (threaded, resolvable) and quality checks someone looked at and set aside.
CREATE TABLE IF NOT EXISTS ReviewComment (
	CommentID    TEXT PRIMARY KEY NOT NULL,
	AssessmentID TEXT NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	ParentID     TEXT REFERENCES ReviewComment(CommentID) ON DELETE CASCADE,   -- set on a reply
	FieldKey     TEXT,                     -- the field it's about (FieldCatalog key), or NULL for the whole assessment
	Body         TEXT NOT NULL,
	Author       TEXT,
	Via          TEXT,                     -- App, AI assistant
	CreatedOn    TEXT NOT NULL,
	ResolvedOn   TEXT,
	ResolvedBy   TEXT
);
CREATE INDEX IF NOT EXISTS IX_ReviewComment_Assessment ON ReviewComment(AssessmentID);

-- No foreign key: ObjectID can be a structure, an assessment or an event.
CREATE TABLE IF NOT EXISTS QaDismissal (
	RuleId      TEXT NOT NULL,             -- the check, e.g. DuplicateParcel
	ObjectID    TEXT NOT NULL,             -- what it was about
	Detail      TEXT NOT NULL DEFAULT '',  -- which finding exactly (e.g. the other structure), so a new one still shows
	Reason      TEXT NOT NULL,
	DismissedOn TEXT,
	DismissedBy TEXT,
	PRIMARY KEY (RuleId, ObjectID, Detail)
);

-- Compliance after a substantial damage (SD) or substantial improvement (SI) determination, and its timeline.
CREATE TABLE IF NOT EXISTS ComplianceCase (
	CaseID            TEXT PRIMARY KEY NOT NULL,
	PropertyID        TEXT NOT NULL REFERENCES Property(PropertyID) ON DELETE CASCADE,
	AssessmentID      TEXT REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,          -- an SD determination
	DeterminationID   TEXT REFERENCES SIDetermination(DeterminationID) ON DELETE CASCADE,  -- an SI determination
	Kind              TEXT NOT NULL,       -- SD, SI
	Path              TEXT,                -- Elevate, Relocate, Demolish, Floodproof, RepairBelowThreshold
	PermitNumber      TEXT,
	PermitIssuedOn    TEXT,
	Deadline          TEXT,
	IccClaimNumber    TEXT,
	IccStatus         TEXT,                -- NotFiled, Filed, Approved, Paid, Denied
	IccAmount         REAL,
	EcReceivedOn      TEXT,                -- the elevation certificate after the work
	FinalInspectionOn TEXT,
	ComplianceCertOn  TEXT,                -- certificate of compliance or occupancy
	Status            TEXT NOT NULL DEFAULT 'Open',   -- Open, Closed
	ClosedOn          TEXT,
	ClosedReason      TEXT,
	Notes             TEXT,
	CreatedOn         TEXT,
	CreatedBy         TEXT,
	UpdatedOn         TEXT
);
CREATE INDEX IF NOT EXISTS IX_ComplianceCase_Property ON ComplianceCase(PropertyID);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ComplianceCase_Assessment ON ComplianceCase(AssessmentID) WHERE AssessmentID IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_ComplianceCase_Determination ON ComplianceCase(DeterminationID) WHERE DeterminationID IS NOT NULL;

CREATE TABLE IF NOT EXISTS ComplianceEntry (
	EntryID   TEXT PRIMARY KEY NOT NULL,
	CaseID    TEXT NOT NULL REFERENCES ComplianceCase(CaseID) ON DELETE CASCADE,
	EntryOn   TEXT NOT NULL,
	Kind      TEXT,                        -- Note, Letter, Call, Permit, Inspection, ICC, Status
	Text      TEXT,
	LetterID  TEXT,                        -- the LetterLog row, for a letter
	CreatedBy TEXT
);
CREATE INDEX IF NOT EXISTS IX_ComplianceEntry_Case ON ComplianceEntry(CaseID, EntryOn);

-- Assessments checked out to another Mac in a work package (.sdework). Read-only here until checked back in.
CREATE TABLE IF NOT EXISTS CheckOut (
	AssessmentID    TEXT PRIMARY KEY NOT NULL REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	PackageID       TEXT NOT NULL,
	CheckedOutOn    TEXT NOT NULL,
	CheckedOutBy    TEXT,
	CheckedOutTo    TEXT,                  -- who, or which Mac, took it
	Version         INTEGER,               -- the assessment's RowVersion when it left
	PropertyVersion INTEGER                -- its structure's RowVersion when it left
);
CREATE INDEX IF NOT EXISTS IX_CheckOut_Package ON CheckOut(PackageID);

-- Drawings on a photo (a water line, arrows, boxes, text), as JSON. The photo file itself is never changed.
CREATE TABLE IF NOT EXISTS PhotoMarkup (
	FileName  TEXT PRIMARY KEY NOT NULL,   -- Attachment.FileName
	Markup    TEXT NOT NULL,
	UpdatedOn TEXT,
	UpdatedBy TEXT
);

-- Documents kept with a structure (an elevation certificate, a permit), stored in the "documents" folder.
CREATE TABLE IF NOT EXISTS Document (
	DocumentID   TEXT PRIMARY KEY NOT NULL,
	PropertyID   TEXT NOT NULL REFERENCES Property(PropertyID) ON DELETE CASCADE,
	AssessmentID TEXT REFERENCES Assessment(AssessmentID) ON DELETE CASCADE,
	Kind         TEXT NOT NULL,            -- ElevationCertificate, Permit, Other
	FileName     TEXT NOT NULL,            -- the stored name in the documents folder
	OriginalName TEXT,
	Title        TEXT,
	Fields       TEXT,                     -- JSON: values read from it (an elevation certificate's A5, C2a, ...)
	AddedOn      TEXT,
	AddedBy      TEXT
);
CREATE INDEX IF NOT EXISTS IX_Document_Property ON Document(PropertyID);

-- The map's layers and bookmarks. Feature data lives in "<database>.layers.gpkg" beside the database.
CREATE TABLE IF NOT EXISTS MapLayer (
	LayerID      TEXT PRIMARY KEY NOT NULL,
	Name         TEXT NOT NULL,
	SourceKind   TEXT NOT NULL,            -- Structures, File, ArcGisRest, Nfhl, Basemap
	Source       TEXT,                     -- the original file path or URL
	TableName    TEXT,                     -- its feature table in the layers GeoPackage, when copied in
	GeometryType TEXT,                     -- Point, Line, Polygon
	SortOrder    INTEGER NOT NULL DEFAULT 0,
	Visible      INTEGER NOT NULL DEFAULT 1,
	Opacity      REAL NOT NULL DEFAULT 1,
	MinScale     REAL,
	MaxScale     REAL,
	Style        TEXT,                     -- JSON
	Labels       TEXT,                     -- JSON
	Editable     INTEGER NOT NULL DEFAULT 0,
	AddedOn      TEXT,
	UpdatedOn    TEXT
);

CREATE TABLE IF NOT EXISTS MapBookmark (
	BookmarkID TEXT PRIMARY KEY NOT NULL,
	Name       TEXT NOT NULL,
	MinX       REAL NOT NULL,              -- longitude and latitude (WGS 84)
	MinY       REAL NOT NULL,
	MaxX       REAL NOT NULL,
	MaxY       REAL NOT NULL,
	SortOrder  INTEGER NOT NULL DEFAULT 0,
	CreatedOn  TEXT
);
