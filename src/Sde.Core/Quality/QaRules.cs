using System.Globalization;
using Sde.Core.Addresses;
using Sde.Core.Assessments;
using Sde.Core.Geo;
using Sde.Core.Records;

namespace Sde.Core.Quality;

/// <summary>
/// The quality checks: likely mistakes and gaps in a set of assessments, each with a plain-English message and the
/// field to look at. Pure functions over a <see cref="QaInput"/>, so the Review page, the check before an SDE export
/// and the AI assistant's qa_check tool all find the same things. Nothing here changes an assessment.
/// </summary>
public static class QaRules
{
	/// <summary>Every check, in the order findings are listed.</summary>
	public static readonly IReadOnlyList<QaRuleInfo> Catalog = new[]
	{
		new QaRuleInfo(QaRuleIds.UnacceptedSuggestions, "Suggestions not accepted", QaSeverity.Error, false,
			"Damage percentages filled in from the depth table that nobody has accepted. They block making the assessment Final."),
		new QaRuleInfo(QaRuleIds.InspectedBeforeDamage, "Inspected before the damage", QaSeverity.Error, true,
			"The inspection date is before the date the damage occurred."),
		new QaRuleInfo(QaRuleIds.DuplicateParcel, "Same parcel number", QaSeverity.Warning, true,
			"Two or more structures have the same parcel number.") { Pairwise = true },
		new QaRuleInfo(QaRuleIds.DuplicateAddress, "Same address", QaSeverity.Warning, true,
			"Two or more structures have the same address, written the same way or not (case, spacing and street suffix are ignored).") { Pairwise = true },
		new QaRuleInfo(QaRuleIds.DuplicateLocation, "Same location", QaSeverity.Warning, true,
			"Two or more structures are within a few metres of each other.") { Pairwise = true },
		new QaRuleInfo(QaRuleIds.MissingCoordinates, "No location", QaSeverity.Warning, true,
			"The structure has no latitude and longitude, so it isn't on the map."),
		new QaRuleInfo(QaRuleIds.MissingPhotos, "No photos", QaSeverity.Warning, true,
			"The assessment has no photos."),
		new QaRuleInfo(QaRuleIds.MissingDamageDate, "No damage date", QaSeverity.Warning, true,
			"The date the damage occurred is blank."),
		new QaRuleInfo(QaRuleIds.ValuePerSqFt, "Unusual value per sq ft", QaSeverity.Warning, true,
			"The building value per square foot is far from the other assessments' (more than 3 median absolute deviations from the median)."),
		new QaRuleInfo(QaRuleIds.DepthVsNeighbours, "Flood elevation unlike neighbours'", QaSeverity.Warning, true,
			"The flood elevation is far from the median of nearby assessments'."),
		new QaRuleInfo(QaRuleIds.DepthVsHighWaterMarks, "Flood elevation unlike high-water marks", QaSeverity.Warning, true,
			"The flood elevation is far from what nearby high-water marks suggest."),
		new QaRuleInfo(QaRuleIds.ZoneDiffersFromNfhl, "Zone differs from the flood map", QaSeverity.Warning, true,
			"The FIRM zone isn't the zone FEMA's flood map (NFHL) shows at the structure."),
		new QaRuleInfo(QaRuleIds.SeveralDamageDates, "Several damage dates", QaSeverity.Warning, true,
			"The assessments in one event have more than one damage date."),
		new QaRuleInfo(QaRuleIds.NearThreshold, "Close to the threshold", QaSeverity.Info, true,
			"The percent damaged is between 45% and 55%: close to the threshold, so check it twice."),
		new QaRuleInfo(QaRuleIds.MissingMailingAddress, "No mailing address", QaSeverity.Info, true,
			"The structure has no mailing address, so letters go to the structure's own address."),
	};

	private static readonly Dictionary<string, QaRuleInfo> ById = Catalog.ToDictionary(r => r.Id, StringComparer.Ordinal);

	/// <summary>A check by its ID, or null.</summary>
	public static QaRuleInfo Rule(string id) => id != null && ById.TryGetValue(id, out QaRuleInfo r) ? r : null;

	/// <summary>
	/// Runs every check that its inputs allow and returns the findings in <see cref="Catalog"/> order (within a check,
	/// in the order of the assessments given). Findings that were set aside come back with their dismissal attached.
	/// </summary>
	public static List<QaFinding> Check(QaInput input)
	{
		if (input?.Reference == null)
		{
			throw new ArgumentException("The quality checks need the reference data.", nameof(input));
		}
		Context ctx = new Context(input);
		List<QaFinding> found = new List<QaFinding>();
		found.AddRange(UnacceptedSuggestions(ctx));
		found.AddRange(InspectedBeforeDamage(ctx));
		found.AddRange(DuplicateParcels(ctx));
		found.AddRange(DuplicateAddresses(ctx));
		found.AddRange(DuplicateLocations(ctx));
		found.AddRange(MissingCoordinates(ctx));
		found.AddRange(MissingPhotos(ctx));
		found.AddRange(MissingDamageDates(ctx));
		found.AddRange(ValuePerSqFt(ctx));
		found.AddRange(DepthVsNeighbours(ctx));
		found.AddRange(DepthVsHighWaterMarks(ctx));
		found.AddRange(ZoneDiffersFromNfhl(ctx));
		found.AddRange(SeveralDamageDates(ctx));
		found.AddRange(NearThreshold(ctx));
		found.AddRange(MissingMailingAddresses(ctx));
		return ApplyDismissals(found, input.Dismissals);
	}

	/// <summary>
	/// The findings with any matching dismissal attached. A dismissal matches its rule, object and detail exactly; for a
	/// check that compares structures it also matches the same group seen from another of its structures. Dismissals of
	/// checks that can't be set aside are ignored.
	/// </summary>
	public static List<QaFinding> ApplyDismissals(IEnumerable<QaFinding> findings, IEnumerable<QaDismissalRecord> dismissals)
	{
		List<QaDismissalRecord> list = dismissals?.Where(d => d != null).ToList() ?? new List<QaDismissalRecord>();
		return findings.Select(f =>
		{
			QaRuleInfo rule = Rule(f.RuleId);
			if (rule == null || !rule.CanDismiss)
			{
				return f;
			}
			QaDismissalRecord match = list.FirstOrDefault(d => d.RuleId == f.RuleId && d.ObjectID == f.ObjectId && (d.Detail ?? "") == f.Detail)
				?? (rule.Pairwise ? list.FirstOrDefault(d => d.RuleId == f.RuleId && SameGroup(d.ObjectID, d.Detail, f.ObjectId, f.Detail)) : null);
			return match == null ? f : f with { Dismissal = match };
		}).ToList();
	}

	/// <summary>A structure's address for messages, e.g. "123 River Road"; "a structure with no address" when it has none.</summary>
	public static string Address(PropertyRecord p, ReferenceData reference)
	{
		string text = string.Join(" ", new[] { p?.StreetNumber, p?.StreetName, reference?.LookupName(p?.StreetSuffixID) }
			.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
		return text.Length == 0 ? "a structure with no address" : text;
	}

	// ───────────────────────── Assessment checks ─────────────────────────

	private static IEnumerable<QaFinding> UnacceptedSuggestions(Context ctx)
	{
		if (ctx.Input.SuggestionCounts == null)
		{
			yield break;
		}
		foreach (AssessmentFile f in ctx.Files)
		{
			if (ctx.Input.SuggestionCounts.TryGetValue(f.Assessment.AssessmentID, out int n) && n > 0)
			{
				string what = n == 1 ? "1 damage percentage came" : n.ToString(CultureInfo.InvariantCulture) + " damage percentages came";
				yield return ForAssessment(QaRuleIds.UnacceptedSuggestions, f, "",
					$"{what} from the depth table and {(n == 1 ? "hasn't" : "haven't")} been accepted. Accept or change {(n == 1 ? "it" : "them")} before making this assessment Final.");
			}
		}
	}

	private static IEnumerable<QaFinding> InspectedBeforeDamage(Context ctx)
	{
		foreach (AssessmentFile f in ctx.Files)
		{
			AssessmentRecord a = f.Assessment;
			if (a.DateofInspection is DateTime inspected && a.DateDamageOccurred is DateTime damaged && inspected.Date < damaged.Date)
			{
				yield return ForAssessment(QaRuleIds.InspectedBeforeDamage, f, Iso(inspected) + "|" + Iso(damaged),
					$"Inspected on {Day(inspected)}, before the damage on {Day(damaged)}.", nameof(AssessmentRecord.DateofInspection));
			}
		}
	}

	private static IEnumerable<QaFinding> MissingPhotos(Context ctx)
	{
		foreach (AssessmentFile f in ctx.Files)
		{
			if (!(f.Attachments ?? new List<AttachmentRecord>()).Any(x => x.Active))
			{
				yield return ForAssessment(QaRuleIds.MissingPhotos, f, "", "There are no photos.");
			}
		}
	}

	private static IEnumerable<QaFinding> MissingDamageDates(Context ctx)
	{
		foreach (AssessmentFile f in ctx.Files)
		{
			if (f.Assessment.DateDamageOccurred == null)
			{
				yield return ForAssessment(QaRuleIds.MissingDamageDate, f, "", "The date the damage occurred is blank.", nameof(AssessmentRecord.DateDamageOccurred));
			}
		}
	}

	private static IEnumerable<QaFinding> NearThreshold(Context ctx)
	{
		QaOptions o = ctx.Options;
		foreach (AssessmentFile f in ctx.Files)
		{
			AssessmentRecord a = f.Assessment;
			bool determined = a.DamageMessage is Determinations.SubstantialMessage or Determinations.NotSubstantialMessage;
			if (determined && a.PercentDamage is decimal pct && pct >= o.NearThresholdLow && pct <= o.NearThresholdHigh)
			{
				yield return ForAssessment(QaRuleIds.NearThreshold, f, FieldCatalog.Canonical(pct),
					$"{Percent(pct)} damaged is close to the {Percent(Determinations.DefaultSubstantialPercent)} threshold. Check it twice.");
			}
		}
	}

	/// <summary>
	/// Building value per square foot against the other assessments of the same event and structure type: flagged when
	/// it is more than <see cref="QaOptions.MadLimit"/> scaled median absolute deviations (1.4826 x MAD, which matches
	/// a standard deviation for normal data) from the median. When most values are equal the spread is taken as at
	/// least 5% of the median, so a group of identical values doesn't flag every small difference.
	/// </summary>
	private static IEnumerable<QaFinding> ValuePerSqFt(Context ctx)
	{
		QaOptions o = ctx.Options;
		List<(AssessmentFile File, double PerSqFt)> values = ctx.Files
			.Select(f => (File: f, Value: BasisValue(f.Assessment), SqFt: f.Property.SquareFootage))
			.Where(x => x.Value is > 0m && x.SqFt is > 0m)
			.Select(x => (x.File, (double)(x.Value.Value / x.SqFt.Value)))
			.ToList();
		foreach (IGrouping<(Guid? Event, int? Type), (AssessmentFile File, double PerSqFt)> group in values.GroupBy(x => (ctx.EventOf(x.File), x.File.Property.StructureTypeID)))
		{
			if (group.Count() < Math.Max(2, o.MinimumForValueCheck))
			{
				continue;
			}
			double median = Median(group.Select(x => x.PerSqFt));
			double mad = Median(group.Select(x => Math.Abs(x.PerSqFt - median)));
			double spread = Math.Max(1.4826 * mad, 0.05 * Math.Abs(median));
			double low = median - o.MadLimit * spread;
			double high = median + o.MadLimit * spread;
			string among = group.Key.Event != null ? "this event's" : "these";
			string kind = group.Key.Type == 2 ? "non-residential" : "residential";
			foreach ((AssessmentFile f, double perSqFt) in group)
			{
				if (perSqFt < low || perSqFt > high)
				{
					yield return ForAssessment(QaRuleIds.ValuePerSqFt, f, Math.Round(perSqFt).ToString("0", CultureInfo.InvariantCulture),
						$"The building value is {Dollars(perSqFt)} per sq ft. For {among} {kind} assessments it is usually {Dollars(median)} ({Dollars(Math.Max(0, low))} to {Dollars(high)}).",
						BasisField(f.Assessment));
				}
			}
		}
	}

	/// <summary>
	/// A flood elevation far from its neighbours': the median flood elevation of other structures' assessments in the
	/// same event within <see cref="QaOptions.NeighbourRadiusMeters"/>, when there are at least
	/// <see cref="QaOptions.MinimumNeighbours"/>. A flood elevation of 0 counts as blank, as in FEMA's lists.
	/// </summary>
	private static IEnumerable<QaFinding> DepthVsNeighbours(Context ctx)
	{
		QaOptions o = ctx.Options;
		List<(AssessmentFile File, decimal Depth, double Lat, double Lon)> withDepth = ctx.Files
			.Where(f => FieldCatalog.IsFlood(f) && FloodElevation(f) != null && GeoMath.IsPosition(f.Property.Latitude, f.Property.Longitude))
			.Select(f => (File: f, Depth: FloodElevation(f).Value, Lat: (double)f.Property.Latitude.Value, Lon: (double)f.Property.Longitude.Value))
			.ToList();
		foreach (IGrouping<Guid?, (AssessmentFile File, decimal Depth, double Lat, double Lon)> group in withDepth.GroupBy(x => ctx.EventOf(x.File)))
		{
			PointIndex<(AssessmentFile File, decimal Depth)> index = new PointIndex<(AssessmentFile, decimal)>(
				group.Select(x => ((x.File, x.Depth), x.Lat, x.Lon)), o.NeighbourRadiusMeters);
			foreach ((AssessmentFile f, decimal depth, double lat, double lon) in group)
			{
				List<decimal> neighbours = index.Within(lat, lon, o.NeighbourRadiusMeters)
					.Where(n => n.Item.File.Property.PropertyID != f.Property.PropertyID)
					.Select(n => n.Item.Depth)
					.ToList();
				if (neighbours.Count < Math.Max(1, o.MinimumNeighbours))
				{
					continue;
				}
				decimal median = Median(neighbours);
				if (Math.Abs(depth - median) > o.NeighbourToleranceFt)
				{
					yield return ForAssessment(QaRuleIds.DepthVsNeighbours, f, FieldCatalog.Canonical(depth),
						$"The flood elevation is {Feet(depth)}, but {neighbours.Count} neighbours within {Meters(o.NeighbourRadiusMeters)} have about {Feet(median)}.",
						nameof(AssessmentRecord.DepthOfFlood));
				}
			}
		}
	}

	private static IEnumerable<QaFinding> DepthVsHighWaterMarks(Context ctx)
	{
		if (ctx.Input.HighWaterMarkEstimates == null)
		{
			yield break;
		}
		foreach (AssessmentFile f in ctx.Files)
		{
			if (FloodElevation(f) is decimal depth && ctx.Input.HighWaterMarkEstimates.TryGetValue(f.Assessment.AssessmentID, out decimal estimate)
				&& Math.Abs(depth - estimate) > ctx.Options.HighWaterMarkToleranceFt)
			{
				decimal shown = Math.Round(estimate, 1, MidpointRounding.AwayFromZero);
				yield return ForAssessment(QaRuleIds.DepthVsHighWaterMarks, f, FieldCatalog.Canonical(depth) + "|" + FieldCatalog.Canonical(shown),
					$"The flood elevation is {Feet(depth)}, but the high-water marks suggest about {Feet(shown)}.", nameof(AssessmentRecord.DepthOfFlood));
			}
		}
	}

	// ───────────────────────── Structure checks ─────────────────────────

	private static IEnumerable<QaFinding> DuplicateParcels(Context ctx)
	{
		Dictionary<string, List<PropertyRecord>> byParcel = new Dictionary<string, List<PropertyRecord>>(StringComparer.Ordinal);
		foreach (PropertyRecord p in ctx.Structures)
		{
			string parcel = ParcelNumbers.Normalize(p.ParcelNumber);
			if (parcel == null)
			{
				continue;
			}
			if (!byParcel.TryGetValue(parcel, out List<PropertyRecord> list))
			{
				list = new List<PropertyRecord>();
				byParcel[parcel] = list;
			}
			list.Add(p);
		}
		foreach (PropertyRecord p in ctx.Structures)
		{
			string parcel = ParcelNumbers.Normalize(p.ParcelNumber);
			if (parcel == null || byParcel[parcel].Count < 2)
			{
				continue;
			}
			List<PropertyRecord> others = byParcel[parcel].Where(x => x.PropertyID != p.PropertyID).ToList();
			yield return ForStructure(ctx, QaRuleIds.DuplicateParcel, p, GroupDetail(others),
				$"{Others(ctx, others)} {(others.Count == 1 ? "has" : "have")} the same parcel number ({p.ParcelNumber.Trim()}).",
				nameof(PropertyRecord.ParcelNumber));
		}
	}

	private static IEnumerable<QaFinding> DuplicateAddresses(Context ctx)
	{
		AddressMatcher matcher = new AddressMatcher(ctx.Input.Reference, matchUnit: true);
		AddressIndex<PropertyRecord> index = matcher.Index(ctx.Structures, matcher.KeyFor);
		foreach (PropertyRecord p in ctx.Structures)
		{
			List<PropertyRecord> others = index.Find(matcher.KeyFor(p))
				.Where(x => x.PropertyID != p.PropertyID && !DifferentCities(p, x))
				.ToList();
			if (others.Count == 0)
			{
				continue;
			}
			yield return ForStructure(ctx, QaRuleIds.DuplicateAddress, p, GroupDetail(others),
				$"{Others(ctx, others, withAddress: false)} {(others.Count == 1 ? "has" : "have")} the same address.",
				nameof(PropertyRecord.StreetName));
		}
	}

	private static IEnumerable<QaFinding> DuplicateLocations(Context ctx)
	{
		double radius = ctx.Options.DuplicateDistanceMeters;
		List<PropertyRecord> placed = ctx.Structures.Where(p => GeoMath.IsPosition(p.Latitude, p.Longitude)).ToList();
		PointIndex<PropertyRecord> index = new PointIndex<PropertyRecord>(placed.Select(p => (p, (double)p.Latitude.Value, (double)p.Longitude.Value)), Math.Max(radius, 1));
		foreach (PropertyRecord p in placed)
		{
			List<(PropertyRecord Item, double Meters)> near = index.Within((double)p.Latitude.Value, (double)p.Longitude.Value, radius)
				.Where(x => x.Item.PropertyID != p.PropertyID).ToList();
			if (near.Count == 0)
			{
				continue;
			}
			string message = near.Count == 1
				? $"{Capital(Address(near[0].Item, ctx.Input.Reference))} is {Meters(near[0].Meters)} away. Is it the same building?"
				: $"{near.Count} other structures are within {Meters(radius)}: {List(ctx, near.Select(x => x.Item).ToList())}. Is one of them the same building?";
			yield return ForStructure(ctx, QaRuleIds.DuplicateLocation, p, GroupDetail(near.Select(x => x.Item)), message, nameof(PropertyRecord.Latitude));
		}
	}

	private static IEnumerable<QaFinding> MissingCoordinates(Context ctx)
	{
		foreach (PropertyRecord p in ctx.Structures)
		{
			if (!GeoMath.IsPosition(p.Latitude, p.Longitude))
			{
				yield return ForStructure(ctx, QaRuleIds.MissingCoordinates, p, "", "There's no latitude and longitude, so the structure isn't on the map.", nameof(PropertyRecord.Latitude));
			}
		}
	}

	private static IEnumerable<QaFinding> MissingMailingAddresses(Context ctx)
	{
		foreach (PropertyRecord p in ctx.Structures)
		{
			// The letters' own rule: a mailing address is a street number or name (LetterFields).
			if (string.IsNullOrWhiteSpace(p.MailingStreetName) && string.IsNullOrWhiteSpace(p.MailingStreetNumber))
			{
				yield return ForStructure(ctx, QaRuleIds.MissingMailingAddress, p, "", "There's no mailing address, so letters will go to the structure's own address.",
					nameof(PropertyRecord.MailingStreetName));
			}
		}
	}

	private static IEnumerable<QaFinding> ZoneDiffersFromNfhl(Context ctx)
	{
		if (ctx.Input.NfhlZones == null)
		{
			yield break;
		}
		foreach (PropertyRecord p in ctx.Structures)
		{
			if (!ctx.Input.NfhlZones.TryGetValue(p.PropertyID, out string zone) || FirmZones.ChoiceName(zone) is not string mapped)
			{
				continue;
			}
			string current = ctx.Input.Reference.LookupName(p.FIRMZone);
			if (string.Equals(current, mapped, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string shown = zone.Trim().ToUpperInvariant();
			yield return ForStructure(ctx, QaRuleIds.ZoneDiffersFromNfhl, p, (current ?? "") + "|" + mapped,
				$"The FIRM zone is {(current == null ? "blank" : current)}, but FEMA's flood map shows {shown} here.", nameof(PropertyRecord.FIRMZone));
		}
	}

	// ───────────────────────── Event checks ─────────────────────────

	private static IEnumerable<QaFinding> SeveralDamageDates(Context ctx)
	{
		if (ctx.Input.EventOf == null)
		{
			yield break;
		}
		foreach (IGrouping<Guid, AssessmentFile> group in ctx.Files.Where(f => ctx.EventOf(f) != null).GroupBy(f => ctx.EventOf(f).Value))
		{
			List<(DateTime Date, int Count)> dates = group.Where(f => f.Assessment.DateDamageOccurred != null)
				.GroupBy(f => f.Assessment.DateDamageOccurred.Value.Date)
				.Select(g => (g.Key, g.Count()))
				.OrderBy(x => x.Key)
				.ToList();
			if (dates.Count < 2)
			{
				continue;
			}
			string name = ctx.Input.Events != null && ctx.Input.Events.TryGetValue(group.Key, out QaEvent e) && !string.IsNullOrWhiteSpace(e.Name)
				? "\"" + e.Name.Trim() + "\""
				: "This event";
			string list = string.Join(", ", dates.Select(d => $"{Day(d.Date)} ({d.Count})"));
			yield return new QaFinding
			{
				RuleId = QaRuleIds.SeveralDamageDates,
				Severity = Rule(QaRuleIds.SeveralDamageDates).Severity,
				ObjectId = group.Key,
				Detail = string.Join(",", dates.Select(d => Iso(d.Date))),
				Message = $"{(name == "This event" ? name : "Event " + name)} has {dates.Count} damage dates: {list}. Cumulative damage counts each damage date as its own event, so check they are right.",
				FieldKey = FieldCatalog.Find(FieldModel.Assessment, nameof(AssessmentRecord.DateDamageOccurred)).Key,
			};
		}
	}

	// ───────────────────────── Helpers ─────────────────────────

	/// <summary>The checked inputs, with the structures among the assessments and the newest assessment of each.</summary>
	private sealed class Context
	{
		public Context(QaInput input)
		{
			Input = input;
			Options = input.Options ?? new QaOptions();
			Files = (input.Assessments ?? Array.Empty<AssessmentFile>()).Where(f => f?.Assessment != null && f.Property != null).ToList();
			Dictionary<Guid, AssessmentFile> newest = new Dictionary<Guid, AssessmentFile>();
			List<PropertyRecord> structures = new List<PropertyRecord>();
			foreach (AssessmentFile f in Files)
			{
				Guid pid = f.Property.PropertyID;
				if (!newest.TryGetValue(pid, out AssessmentFile current))
				{
					newest[pid] = f;
					structures.Add(f.Property);
				}
				else if (Later(f.Assessment, current.Assessment))
				{
					newest[pid] = f;
				}
			}
			Newest = newest;
			Structures = structures;
		}

		public QaInput Input { get; }
		public QaOptions Options { get; }
		public List<AssessmentFile> Files { get; }
		public List<PropertyRecord> Structures { get; }
		public Dictionary<Guid, AssessmentFile> Newest { get; }

		public Guid? EventOf(AssessmentFile f) =>
			Input.EventOf != null && Input.EventOf.TryGetValue(f.Assessment.AssessmentID, out Guid e) ? e : null;

		private static bool Later(AssessmentRecord a, AssessmentRecord b) =>
			(a.DateofInspection ?? DateTime.MinValue, a.LastUpdate ?? DateTime.MinValue).CompareTo((b.DateofInspection ?? DateTime.MinValue, b.LastUpdate ?? DateTime.MinValue)) > 0;
	}

	private static QaFinding ForAssessment(string ruleId, AssessmentFile f, string detail, string message, string assessmentMember = null) => new QaFinding
	{
		RuleId = ruleId,
		Severity = Rule(ruleId).Severity,
		ObjectId = f.Assessment.AssessmentID,
		AssessmentId = f.Assessment.AssessmentID,
		PropertyId = f.Property.PropertyID,
		Detail = detail ?? "",
		Message = message,
		FieldKey = assessmentMember == null ? null : FieldCatalog.Find(FieldModel.Assessment, assessmentMember)?.Key,
	};

	private static QaFinding ForStructure(Context ctx, string ruleId, PropertyRecord p, string detail, string message, string propertyMember = null) => new QaFinding
	{
		RuleId = ruleId,
		Severity = Rule(ruleId).Severity,
		ObjectId = p.PropertyID,
		AssessmentId = ctx.Newest[p.PropertyID].Assessment.AssessmentID,
		PropertyId = p.PropertyID,
		Detail = detail ?? "",
		Message = message,
		FieldKey = propertyMember == null ? null : FieldCatalog.Find(FieldModel.Property, propertyMember)?.Key,
	};

	/// <summary>The detail of a finding about a group of structures: the other structures' IDs, sorted, so it is the same however they were found.</summary>
	private static string GroupDetail(IEnumerable<PropertyRecord> others) =>
		string.Join(",", others.Select(o => o.PropertyID.ToString("N")).Distinct().OrderBy(s => s, StringComparer.Ordinal));

	/// <summary>True when a dismissal of (object, detail) is about the same group of structures as a finding (object, detail).</summary>
	private static bool SameGroup(Guid dismissedObject, string dismissedDetail, Guid findingObject, string findingDetail)
	{
		HashSet<string> a = Members(dismissedObject, dismissedDetail);
		HashSet<string> b = Members(findingObject, findingDetail);
		return a.Count > 1 && a.SetEquals(b);
	}

	private static HashSet<string> Members(Guid objectId, string detail)
	{
		HashSet<string> set = new HashSet<string>(StringComparer.Ordinal) { objectId.ToString("N") };
		foreach (string part in (detail ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			set.Add(Guid.TryParse(part, out Guid g) ? g.ToString("N") : part);
		}
		return set;
	}

	private static bool DifferentCities(PropertyRecord a, PropertyRecord b)
	{
		string ca = Squash(a.City);
		string cb = Squash(b.City);
		return ca.Length > 0 && cb.Length > 0 && ca != cb;
	}

	private static string Squash(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

	/// <summary>"Another structure, 125 River Road," or "2 other structures (125 River Road and 127 River Road)".</summary>
	private static string Others(Context ctx, List<PropertyRecord> others, bool withAddress = true)
	{
		if (others.Count == 1)
		{
			return withAddress ? "Another structure, " + Address(others[0], ctx.Input.Reference) + "," : "Another structure";
		}
		return others.Count.ToString(CultureInfo.InvariantCulture) + " other structures" + (withAddress ? " (" + List(ctx, others) + ")" : "");
	}

	/// <summary>Up to three addresses, then "and n more".</summary>
	private static string List(Context ctx, List<PropertyRecord> structures)
	{
		List<string> names = structures.Take(3).Select(s => Address(s, ctx.Input.Reference)).ToList();
		if (structures.Count > 3)
		{
			names.Add((structures.Count - 3).ToString(CultureInfo.InvariantCulture) + " more");
		}
		return names.Count switch
		{
			1 => names[0],
			2 => names[0] + " and " + names[1],
			_ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
		};
	}

	private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

	/// <summary>The building value the percentage is computed against, by the assessment's chosen basis (no matter the percentage).</summary>
	private static decimal? BasisValue(AssessmentRecord a) => a.ValueOfBuildingID switch
	{
		LookupIds.ValueAdjustedTax => a.TaxAssessedValue is decimal t ? t * (a.FactorAdjustment ?? 0m) : null,
		LookupIds.ValueComputedAcv => a.ComputedActualCashValue,
		LookupIds.ValueMarketAppraisal => a.MarketAppraisal,
		_ => null,
	};

	/// <summary>The field behind the building value: the base cost for a computed value, otherwise the amount entered.</summary>
	private static string BasisField(AssessmentRecord a) => a.ValueOfBuildingID switch
	{
		LookupIds.ValueAdjustedTax => nameof(AssessmentRecord.TaxAssessedValue),
		LookupIds.ValueMarketAppraisal => nameof(AssessmentRecord.MarketAppraisal),
		_ => nameof(AssessmentRecord.BaseCost),
	};

	/// <summary>An assessment's flood elevation (Assessment.DepthOfFlood); 0 counts as blank, as in FEMA's lists.</summary>
	private static decimal? FloodElevation(AssessmentFile f) => f.Assessment.DepthOfFlood is decimal d && d != 0m ? d : null;

	private static double Median(IEnumerable<double> values)
	{
		List<double> sorted = values.OrderBy(v => v).ToList();
		int n = sorted.Count;
		return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
	}

	private static decimal Median(IEnumerable<decimal> values)
	{
		List<decimal> sorted = values.OrderBy(v => v).ToList();
		int n = sorted.Count;
		return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
	}

	private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	private static string Day(DateTime d) => d.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));

	private static string Percent(decimal p) => p.ToString("0.#", CultureInfo.InvariantCulture) + "%";

	private static string Feet(decimal ft) => ft.ToString("#,##0.0#", CultureInfo.GetCultureInfo("en-US")) + " ft";

	private static string Dollars(double d) => "$" + Math.Round(d).ToString("#,##0", CultureInfo.GetCultureInfo("en-US"));

	private static string Meters(double m) => Math.Round(m).ToString("#,##0", CultureInfo.GetCultureInfo("en-US")) + " m";
}
