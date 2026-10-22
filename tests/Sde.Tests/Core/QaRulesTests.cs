using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Core.Records;

namespace Sde.Tests.Core;

/// <summary>The quality checks: every rule, its edge cases, and how dismissals match findings.</summary>
public class QaRulesTests
{
	private static readonly Lazy<ReferenceData> Reference = new Lazy<ReferenceData>(() =>
	{
		using TempDatabase t = new TempDatabase();
		return t.Store.Reference;
	});

	private static int _next;

	/// <summary>
	/// A complete, unremarkable flood assessment: an address, a parcel, a mailing address, a position of its own, a
	/// photo, sensible dates, $100 per sq ft and 30% damaged. Each test changes what it checks.
	/// </summary>
	private static AssessmentFile Clean(Action<PropertyRecord> property = null, Action<AssessmentRecord> assessment = null)
	{
		int n = Interlocked.Increment(ref _next);
		PropertyRecord p = new PropertyRecord
		{
			PropertyID = Guid.NewGuid(),
			StreetNumber = (100 + n).ToString(), StreetName = "Example", StreetSuffixID = 172, City = "Exampleville", ZipCode = "43702",
			ParcelNumber = "P-" + n, StructureTypeID = 1, SquareFootage = 1000m,
			// Each structure about 500 m from the last, so none are near each other unless a test says so.
			Latitude = 39.0m + n * 0.0045m, Longitude = -84.0m,
			MailingStreetNumber = "1", MailingStreetName = "Post Office Box",
			FIRMZone = 30,
		};
		AssessmentRecord a = new AssessmentRecord
		{
			AssessmentID = Guid.NewGuid(), PropertyID = p.PropertyID, Active = true,
			DateDamageOccurred = new DateTime(2026, 9, 14), DateofInspection = new DateTime(2026, 9, 20),
			CauseofDamageID = LookupIds.CauseFlood,
			ValueOfBuildingID = LookupIds.ValueComputedAcv, ComputedActualCashValue = 100000m,
			PercentDamage = 30m, DamageMessage = Determinations.NotSubstantialMessage,
		};
		property?.Invoke(p);
		assessment?.Invoke(a);
		return new AssessmentFile
		{
			Property = p,
			Assessment = a,
			Attachments = new List<AttachmentRecord> { new AttachmentRecord { FileName = "photo.jpg", Active = true, AssessmentID = a.AssessmentID } },
		};
	}

	private static List<QaFinding> Check(IEnumerable<AssessmentFile> files, Func<QaInput, QaInput> extra = null)
	{
		QaInput input = new QaInput { Assessments = files.ToList(), Reference = Reference.Value };
		return QaRules.Check(extra == null ? input : extra(input));
	}

	private static List<QaFinding> Of(IEnumerable<QaFinding> findings, string rule) => findings.Where(f => f.RuleId == rule).ToList();

	[Fact]
	public void CleanAssessments_HaveNoFindings()
	{
		Assert.Empty(Check(Enumerable.Range(0, 10).Select(_ => Clean())));
	}

	[Fact]
	public void Catalog_HasEveryRule_AndMessagesAreSafe()
	{
		IEnumerable<string> ids = typeof(QaRuleIds).GetFields().Select(f => (string)f.GetRawConstantValue());
		Assert.Equal(ids.OrderBy(x => x), QaRules.Catalog.Select(r => r.Id).OrderBy(x => x));
		Assert.All(QaRules.Catalog, r => Assert.DoesNotContain("—", r.Description + r.Title));
		Assert.Throws<ArgumentException>(() => QaRules.Check(new QaInput { Assessments = new[] { Clean() } }));
	}

	[Fact]
	public void DuplicateParcels_AreFoundOnBothSides_IgnoringDashesAndCase()
	{
		AssessmentFile a = Clean(p => p.ParcelNumber = "12-345.6a");
		AssessmentFile b = Clean(p => p.ParcelNumber = "12 3456A");
		AssessmentFile c = Clean(p => p.ParcelNumber = "999");
		List<QaFinding> found = Of(Check(new[] { a, b, c }), QaRuleIds.DuplicateParcel);
		Assert.Equal(new[] { a.Property.PropertyID, b.Property.PropertyID }, found.Select(f => f.ObjectId));
		Assert.Equal(b.Property.PropertyID.ToString("N"), found[0].Detail);
		Assert.Contains(QaRules.Address(b.Property, Reference.Value), found[0].Message);
		Assert.Equal(QaSeverity.Warning, found[0].Severity);
		Assert.Equal("Property.ParcelNumber", found[0].FieldKey);
		Assert.Equal(a.Assessment.AssessmentID, found[0].AssessmentId);
	}

	[Fact]
	public void DismissingADuplicate_SetsBothSidesAside_ButNotWhenANewStructureJoins()
	{
		AssessmentFile a = Clean(p => p.ParcelNumber = "77");
		AssessmentFile b = Clean(p => p.ParcelNumber = "77");
		QaFinding first = Of(Check(new[] { a, b }), QaRuleIds.DuplicateParcel)[0];
		QaDismissalRecord dismissal = new QaDismissalRecord { RuleId = first.RuleId, ObjectID = first.ObjectId, Detail = first.Detail, Reason = "Two buildings on one parcel" };

		List<QaFinding> again = Of(Check(new[] { a, b }, i => i with { Dismissals = new[] { dismissal } }), QaRuleIds.DuplicateParcel);
		Assert.All(again, f => Assert.Same(dismissal, f.Dismissal));

		AssessmentFile c = Clean(p => p.ParcelNumber = "77");
		List<QaFinding> withC = Of(Check(new[] { a, b, c }, i => i with { Dismissals = new[] { dismissal } }), QaRuleIds.DuplicateParcel);
		Assert.Equal(3, withC.Count);
		Assert.All(withC, f => Assert.False(f.IsDismissed));
		Assert.Contains("2 other structures", withC[0].Message);
	}

	[Fact]
	public void DuplicateAddresses_IgnoreCaseSpacingAndSuffixSpelling_ButNotUnitsCitiesOrZips()
	{
		int street = Reference.Value.OfType(LookupTypes.StreetSuffix).Single(l => l.LookupFieldName == "Street").LookupID;
		AssessmentFile a = Clean(p => { p.StreetNumber = "123"; p.StreetName = "N. Main St."; p.StreetSuffixID = null; });
		AssessmentFile b = Clean(p => { p.StreetNumber = "123"; p.StreetName = "north  MAIN"; p.StreetSuffixID = street; });
		AssessmentFile otherZip = Clean(p => { p.StreetNumber = "123"; p.StreetName = "N Main Street"; p.StreetSuffixID = null; p.ZipCode = "99999"; });
		AssessmentFile otherCity = Clean(p => { p.StreetNumber = "123"; p.StreetName = "N Main Street"; p.StreetSuffixID = null; p.City = "Elsewhere"; });
		AssessmentFile unit = Clean(p => { p.StreetNumber = "123 Apt 2"; p.StreetName = "N Main Street"; p.StreetSuffixID = null; });
		List<QaFinding> found = Of(Check(new[] { a, b, otherZip, otherCity, unit }), QaRuleIds.DuplicateAddress);
		Assert.Equal(new[] { a.Property.PropertyID, b.Property.PropertyID }, found.Select(f => f.ObjectId));
		Assert.Equal("Another structure has the same address.", found[0].Message);
	}

	[Fact]
	public void DuplicateLocations_WithinFiveMetres()
	{
		AssessmentFile a = Clean(p => { p.Latitude = 39.7m; p.Longitude = -84.2m; });
		AssessmentFile near = Clean(p => { p.Latitude = 39.700027m; p.Longitude = -84.2m; });   // about 3 m north
		AssessmentFile far = Clean(p => { p.Latitude = 39.7m; p.Longitude = -84.200093m; });   // about 8 m east
		List<QaFinding> found = Of(Check(new[] { a, near, far }), QaRuleIds.DuplicateLocation);
		Assert.Equal(new[] { a.Property.PropertyID, near.Property.PropertyID }, found.Select(f => f.ObjectId));
		Assert.Contains("is 3 m away", found[0].Message);
		Assert.Equal("Property.Latitude", found[0].FieldKey);
	}

	[Fact]
	public void MissingCoordinates_IncludesTheZeroPlaceholder()
	{
		AssessmentFile none = Clean(p => { p.Latitude = null; p.Longitude = null; });
		AssessmentFile zero = Clean(p => { p.Latitude = 0m; p.Longitude = 0m; });
		AssessmentFile half = Clean(p => p.Longitude = null);
		List<QaFinding> found = Of(Check(new[] { none, zero, half, Clean() }), QaRuleIds.MissingCoordinates);
		Assert.Equal(3, found.Count);
	}

	[Fact]
	public void MissingPhotos_CountsOnlyActivePhotos()
	{
		AssessmentFile none = Clean();
		none.Attachments.Clear();
		AssessmentFile deleted = Clean();
		deleted.Attachments[0].Active = false;
		List<QaFinding> found = Of(Check(new[] { none, deleted, Clean() }), QaRuleIds.MissingPhotos);
		Assert.Equal(new[] { none.Assessment.AssessmentID, deleted.Assessment.AssessmentID }, found.Select(f => f.ObjectId));
	}

	[Fact]
	public void MissingDamageDate_AndMailingAddress()
	{
		AssessmentFile noDate = Clean(assessment: a => a.DateDamageOccurred = null);
		AssessmentFile noMail = Clean(p => { p.MailingStreetName = " "; p.MailingStreetNumber = null; });
		AssessmentFile numberOnly = Clean(p => { p.MailingStreetName = null; p.MailingStreetNumber = "12"; });
		List<QaFinding> found = Check(new[] { noDate, noMail, numberOnly });
		Assert.Equal(noDate.Assessment.AssessmentID, Assert.Single(Of(found, QaRuleIds.MissingDamageDate)).ObjectId);
		QaFinding mail = Assert.Single(Of(found, QaRuleIds.MissingMailingAddress));
		Assert.Equal(noMail.Property.PropertyID, mail.ObjectId);
		Assert.Equal(QaSeverity.Info, mail.Severity);
	}

	[Fact]
	public void InspectedBeforeDamage_ComparesDatesOnly()
	{
		AssessmentFile before = Clean(assessment: a => a.DateofInspection = new DateTime(2026, 9, 13));
		AssessmentFile sameDay = Clean(assessment: a => a.DateofInspection = new DateTime(2026, 9, 14, 8, 0, 0));
		QaFinding f = Assert.Single(Of(Check(new[] { before, sameDay }), QaRuleIds.InspectedBeforeDamage));
		Assert.Equal(before.Assessment.AssessmentID, f.ObjectId);
		Assert.Equal("2026-09-13|2026-09-14", f.Detail);
		Assert.Equal("Inspected on Sep 13, 2026, before the damage on Sep 14, 2026.", f.Message);
		Assert.Equal(QaSeverity.Error, f.Severity);
	}

	[Theory]
	[InlineData(44.9, false)]
	[InlineData(45, true)]
	[InlineData(50, true)]
	[InlineData(55, true)]
	[InlineData(55.1, false)]
	public void NearThreshold_IsFortyFiveToFiftyFiveInclusive(double percent, bool flagged)
	{
		AssessmentFile f = Clean(assessment: a =>
		{
			a.PercentDamage = (decimal)percent;
			a.DamageMessage = percent >= 50 ? Determinations.SubstantialMessage : Determinations.NotSubstantialMessage;
		});
		Assert.Equal(flagged, Of(Check(new[] { f }), QaRuleIds.NearThreshold).Count == 1);
	}

	[Fact]
	public void NearThreshold_SkipsIncompleteAssessments_AndADismissalHoldsOnlyForThatPercent()
	{
		AssessmentFile incomplete = Clean(assessment: a => { a.PercentDamage = 50m; a.DamageMessage = Determinations.IncompleteMessage; });
		Assert.Empty(Of(Check(new[] { incomplete }), QaRuleIds.NearThreshold));

		AssessmentFile f = Clean(assessment: a => { a.PercentDamage = 52.3m; a.DamageMessage = Determinations.SubstantialMessage; });
		QaFinding found = Assert.Single(Of(Check(new[] { f }), QaRuleIds.NearThreshold));
		Assert.Equal("52.3", found.Detail);
		Assert.Equal("52.3% damaged is close to the 50% threshold. Check it twice.", found.Message);
		QaDismissalRecord d = new QaDismissalRecord { RuleId = found.RuleId, ObjectID = found.ObjectId, Detail = found.Detail, Reason = "Checked twice" };
		Assert.True(Assert.Single(Of(Check(new[] { f }, i => i with { Dismissals = new[] { d } }), QaRuleIds.NearThreshold)).IsDismissed);
		f.Assessment.PercentDamage = 53m;
		Assert.False(Assert.Single(Of(Check(new[] { f }, i => i with { Dismissals = new[] { d } }), QaRuleIds.NearThreshold)).IsDismissed);
	}

	[Fact]
	public void UnacceptedSuggestions_AreErrors_ThatCantBeSetAside()
	{
		AssessmentFile f = Clean();
		AssessmentFile none = Clean();
		Dictionary<Guid, int> counts = new() { [f.Assessment.AssessmentID] = 3, [none.Assessment.AssessmentID] = 0 };
		QaDismissalRecord d = new QaDismissalRecord { RuleId = QaRuleIds.UnacceptedSuggestions, ObjectID = f.Assessment.AssessmentID, Detail = "", Reason = "x" };
		QaFinding found = Assert.Single(Of(Check(new[] { f, none }, i => i with { SuggestionCounts = counts, Dismissals = new[] { d } }), QaRuleIds.UnacceptedSuggestions));
		Assert.Equal(QaSeverity.Error, found.Severity);
		Assert.False(found.IsDismissed);
		Assert.StartsWith("3 damage percentages came from the depth table", found.Message);
		Assert.False(QaRules.Rule(QaRuleIds.UnacceptedSuggestions).CanDismiss);
	}

	[Fact]
	public void ValuePerSqFt_FlagsOutliers_WhenThereAreEnoughToCompare()
	{
		List<AssessmentFile> files = new double[] { 95, 98, 100, 101, 103, 105, 99, 102 }
			.Select(v => Clean(assessment: a => a.ComputedActualCashValue = (decimal)v * 1000m)).ToList();
		AssessmentFile high = Clean(assessment: a => a.ComputedActualCashValue = 400000m);
		files.Add(high);
		QaFinding f = Assert.Single(Of(Check(files), QaRuleIds.ValuePerSqFt));
		Assert.Equal(high.Assessment.AssessmentID, f.ObjectId);
		Assert.Equal("400", f.Detail);
		Assert.StartsWith("The building value is $400 per sq ft. For these residential assessments it is usually $101", f.Message);
		Assert.Equal("Assessment.BaseCost", f.FieldKey);

		// Seven to compare with is too few.
		Assert.Empty(Of(Check(files.Skip(2)), QaRuleIds.ValuePerSqFt));
	}

	[Fact]
	public void ValuePerSqFt_SameValues_UseAFivePercentFloor_AndTypesAreComparedSeparately()
	{
		List<AssessmentFile> files = Enumerable.Range(0, 8).Select(_ => Clean()).ToList();
		AssessmentFile slightly = Clean(assessment: a => a.ComputedActualCashValue = 104000m);
		AssessmentFile clearly = Clean(assessment: a => a.ComputedActualCashValue = 130000m);
		AssessmentFile nonResidential = Clean(p => p.StructureTypeID = 2, a => a.ComputedActualCashValue = 400000m);
		AssessmentFile noSqFt = Clean(p => p.SquareFootage = 0m, a => a.ComputedActualCashValue = 999999m);
		AssessmentFile appraisal = Clean(assessment: a => { a.ValueOfBuildingID = LookupIds.ValueMarketAppraisal; a.MarketAppraisal = 300000m; });
		files.AddRange(new[] { slightly, clearly, nonResidential, noSqFt, appraisal });
		List<QaFinding> found = Of(Check(files), QaRuleIds.ValuePerSqFt);
		Assert.Equal(new[] { clearly.Assessment.AssessmentID, appraisal.Assessment.AssessmentID }, found.Select(f => f.ObjectId));
		Assert.Equal("Assessment.MarketAppraisal", found[1].FieldKey);
	}

	[Fact]
	public void ValuePerSqFt_ComparesWithinEachEvent()
	{
		Guid flood = Guid.NewGuid();
		Guid storm = Guid.NewGuid();
		List<AssessmentFile> inFlood = Enumerable.Range(0, 9).Select(_ => Clean()).ToList();
		// A storm with only two assessments: too few to check on their own, but outliers if mixed in with the flood.
		List<AssessmentFile> inStorm = Enumerable.Range(0, 2).Select(_ => Clean(assessment: a => a.ComputedActualCashValue = 300000m)).ToList();
		Dictionary<Guid, Guid> eventOf = inFlood.ToDictionary(f => f.Assessment.AssessmentID, _ => flood);
		foreach (AssessmentFile f in inStorm)
		{
			eventOf[f.Assessment.AssessmentID] = storm;
		}
		Assert.Empty(Of(Check(inFlood.Concat(inStorm), i => i with { EventOf = eventOf }), QaRuleIds.ValuePerSqFt));
		Assert.Equal(2, Of(Check(inFlood.Concat(inStorm)), QaRuleIds.ValuePerSqFt).Count);
	}

	/// <summary>A flood assessment at a position near 39.7, -84.2, with a flood elevation.</summary>
	private static AssessmentFile At(double northMetres, decimal? floodElevation, Action<AssessmentRecord> change = null) =>
		Clean(p => { p.Latitude = 39.7m + (decimal)(northMetres / 111195.0); p.Longitude = -84.2m; }, a => { a.DepthOfFlood = floodElevation; change?.Invoke(a); });

	[Fact]
	public void DepthVsNeighbours_FlagsAFloodElevationFarFromTheNeighboursMedian()
	{
		AssessmentFile odd = At(0, 748.0m);
		AssessmentFile close = At(10, 743.5m);
		List<AssessmentFile> files = new() { odd, close, At(40, 742.4m), At(80, 742.5m), At(120, 742.6m), At(160, 742.5m) };
		QaFinding f = Assert.Single(Of(Check(files), QaRuleIds.DepthVsNeighbours));
		Assert.Equal(odd.Assessment.AssessmentID, f.ObjectId);
		Assert.Equal("748", f.Detail);
		Assert.Equal("The flood elevation is 748.0 ft, but 5 neighbours within 300 m have about 742.5 ft.", f.Message);
		Assert.Equal("Assessment.DepthOfFlood", f.FieldKey);
	}

	[Fact]
	public void DepthVsNeighbours_NeedsThreeNeighbours_IgnoresZeroFarOtherCausesAndOtherEvents()
	{
		Assert.Empty(Of(Check(new[] { At(0, 748m), At(10, 742.5m), At(20, 742.5m) }), QaRuleIds.DepthVsNeighbours));
		Assert.Empty(Of(Check(new[] { At(0, 748m), At(10, 742.5m), At(20, 742.5m), At(30, 0m) }), QaRuleIds.DepthVsNeighbours));
		Assert.Empty(Of(Check(new[] { At(0, 748m), At(10, 742.5m), At(20, 742.5m), At(400, 742.5m) }), QaRuleIds.DepthVsNeighbours));
		Assert.Empty(Of(Check(new[] { At(0, 748m, a => a.CauseofDamageID = LookupIds.CauseOther), At(10, 742.5m), At(20, 742.5m), At(30, 742.5m) }), QaRuleIds.DepthVsNeighbours));

		AssessmentFile odd = At(0, 748m);
		List<AssessmentFile> others = new() { At(10, 742.5m), At(20, 742.5m), At(30, 742.5m) };
		Dictionary<Guid, Guid> eventOf = others.ToDictionary(f => f.Assessment.AssessmentID, _ => Guid.NewGuid());
		eventOf[odd.Assessment.AssessmentID] = Guid.NewGuid();
		Assert.Empty(Of(Check(others.Append(odd), i => i with { EventOf = eventOf }), QaRuleIds.DepthVsNeighbours));
	}

	[Fact]
	public void DepthVsHighWaterMarks_ComparesWithTheEstimate()
	{
		AssessmentFile far = At(0, 745.0m);
		AssessmentFile near = At(1000, 743.0m);
		AssessmentFile blank = At(2000, null);
		Dictionary<Guid, decimal> estimates = new()
		{
			[far.Assessment.AssessmentID] = 742.64m, [near.Assessment.AssessmentID] = 742.6m, [blank.Assessment.AssessmentID] = 742.6m,
		};
		Assert.Empty(Of(Check(new[] { far, near, blank }), QaRuleIds.DepthVsHighWaterMarks));
		QaFinding f = Assert.Single(Of(Check(new[] { far, near, blank }, i => i with { HighWaterMarkEstimates = estimates }), QaRuleIds.DepthVsHighWaterMarks));
		Assert.Equal(far.Assessment.AssessmentID, f.ObjectId);
		Assert.Equal("745|742.6", f.Detail);
		Assert.Equal("The flood elevation is 745.0 ft, but the high-water marks suggest about 742.6 ft.", f.Message);
	}

	[Fact]
	public void ZoneDiffersFromNfhl_ComparesFemasZoneChoices()
	{
		int x = Reference.Value.OfType(LookupTypes.FirmZone).Single(l => l.LookupFieldName == "X").LookupID;
		int numbered = Reference.Value.OfType(LookupTypes.FirmZone).Single(l => l.LookupFieldName == "A1 thru A99").LookupID;
		AssessmentFile same = Clean();                                        // AE
		AssessmentFile differs = Clean(p => p.FIRMZone = x);
		AssessmentFile blank = Clean(p => p.FIRMZone = null);
		AssessmentFile oldMap = Clean(p => p.FIRMZone = numbered);
		AssessmentFile unknown = Clean(p => p.FIRMZone = x);
		AssessmentFile notLookedUp = Clean(p => p.FIRMZone = x);
		Dictionary<Guid, string> nfhl = new()
		{
			[same.Property.PropertyID] = "ae", [differs.Property.PropertyID] = "AE", [blank.Property.PropertyID] = "VE",
			[oldMap.Property.PropertyID] = "A12", [unknown.Property.PropertyID] = "AREA NOT INCLUDED",
		};
		List<QaFinding> found = Of(Check(new[] { same, differs, blank, oldMap, unknown, notLookedUp }, i => i with { NfhlZones = nfhl }), QaRuleIds.ZoneDiffersFromNfhl);
		Assert.Equal(new[] { differs.Property.PropertyID, blank.Property.PropertyID }, found.Select(f => f.ObjectId));
		Assert.Equal("The FIRM zone is X, but FEMA's flood map shows AE here.", found[0].Message);
		Assert.Equal("X|AE", found[0].Detail);
		Assert.Equal("The FIRM zone is blank, but FEMA's flood map shows VE here.", found[1].Message);
		Assert.Equal("Property.FIRMZone", found[0].FieldKey);
	}

	[Fact]
	public void SeveralDamageDates_IsAnEventFinding()
	{
		Guid eventId = Guid.NewGuid();
		Guid single = Guid.NewGuid();
		List<AssessmentFile> files = new()
		{
			Clean(), Clean(), Clean(assessment: a => a.DateDamageOccurred = new DateTime(2026, 9, 15, 13, 0, 0)), Clean(assessment: a => a.DateDamageOccurred = null),
		};
		AssessmentFile other = Clean();
		Dictionary<Guid, Guid> eventOf = files.ToDictionary(f => f.Assessment.AssessmentID, _ => eventId);
		eventOf[other.Assessment.AssessmentID] = single;
		Dictionary<Guid, QaEvent> events = new() { [eventId] = new QaEvent(eventId, "September flood") };
		QaFinding f = Assert.Single(Of(Check(files.Append(other), i => i with { EventOf = eventOf, Events = events }), QaRuleIds.SeveralDamageDates));
		Assert.Equal(eventId, f.ObjectId);
		Assert.Null(f.AssessmentId);
		Assert.Null(f.PropertyId);
		Assert.Equal("2026-09-14,2026-09-15", f.Detail);
		Assert.StartsWith("Event \"September flood\" has 2 damage dates: Sep 14, 2026 (2), Sep 15, 2026 (1).", f.Message);
		Assert.Empty(Of(Check(files), QaRuleIds.SeveralDamageDates));
	}

	[Fact]
	public void StructureFindings_UseTheNewestAssessment_AndEveryFieldKeyIsACatalogField()
	{
		AssessmentFile older = Clean(p => { p.Latitude = null; p.Longitude = null; });
		AssessmentFile newer = new AssessmentFile
		{
			Property = older.Property,
			Assessment = new AssessmentRecord
			{
				AssessmentID = Guid.NewGuid(), PropertyID = older.Property.PropertyID, Active = true, DateofInspection = new DateTime(2027, 1, 5),
				DateDamageOccurred = new DateTime(2027, 1, 1), CauseofDamageID = LookupIds.CauseFlood,
			},
		};
		List<QaFinding> found = Check(new[] { older, newer, Clean(p => p.ParcelNumber = "1"), Clean(p => p.ParcelNumber = "1") });
		QaFinding missing = Assert.Single(Of(found, QaRuleIds.MissingCoordinates));
		Assert.Equal(newer.Assessment.AssessmentID, missing.AssessmentId);
		Assert.All(found.Where(f => f.FieldKey != null), f => Assert.NotNull(FieldCatalog.Get(f.FieldKey)));
		Assert.All(found, f => Assert.DoesNotContain("—", f.Message));
		// Listed in catalog order.
		List<int> order = found.Select(f => QaRules.Catalog.ToList().FindIndex(r => r.Id == f.RuleId)).ToList();
		Assert.Equal(order.OrderBy(i => i), order);
	}
}
