using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using QRCoder;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Field;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports.Internal;
using SkiaSharp;
using Column = Sde.Reports.Internal.Column;

namespace Sde.Reports;

/// <summary>One stop on a team's route: the assignment, its structure, and the structure's latest assessment if any.</summary>
public sealed class FieldKitStop
{
	public AssignmentRecord Assignment { get; init; }

	public PropertyRecord Property { get; init; }

	/// <summary>The latest assessment of the structure, or null when it has none yet.</summary>
	public StructureSummary Latest { get; init; }

	/// <summary>The stop's number on the route (1 first).</summary>
	public int Number { get; init; }
}

/// <summary>What to print on a route sheet or field worksheets besides the stops.</summary>
public sealed class FieldKitOptions
{
	public string CommunityName { get; init; }

	public string EventName { get; init; }

	/// <summary>Date printed in the footer; default DateTime.Now.</summary>
	public DateTime? GeneratedOn { get; init; }

	/// <summary>
	/// A small map of the structure for its worksheet (PNG or JPEG bytes), drawn by the map engine; null leaves the
	/// space out.
	/// </summary>
	public Func<FieldKitStop, byte[]> MapSnippet { get; init; }
}

/// <summary>
/// The field kit for a team (C5): a route sheet listing its stops in order, each with an "Open in Maps" link, and a
/// pre-filled worksheet per structure with the assignment's code as a QR code (the phone form's search accepts it).
/// </summary>
public static class FieldKit
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The tallest a worksheet's map may be, in inches.</summary>
	public const double MapHeight = 2.4;

	/// <summary>A team's stops in route order (stops without a place come last, in the order assigned).</summary>
	public static List<FieldKitStop> Stops(SdeStore store, Guid teamId, Guid? eventId = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		Dictionary<Guid, StructureSummary> latest = new();
		foreach (StructureSummary s in store.ListStructures())
		{
			if (s.AssessmentID == null)
			{
				continue;
			}
			if (!latest.TryGetValue(s.PropertyID, out StructureSummary seen) || (s.DateofInspection ?? DateTime.MinValue) > (seen.DateofInspection ?? DateTime.MinValue))
			{
				latest[s.PropertyID] = s;
			}
		}
		List<FieldKitStop> stops = new();
		foreach (AssignmentRecord a in store.GetAssignments(teamId, eventId))
		{
			PropertyRecord p = store.GetProperty(a.PropertyID);
			if (p == null)
			{
				continue;
			}
			latest.TryGetValue(a.PropertyID, out StructureSummary last);
			stops.Add(new FieldKitStop { Assignment = a, Property = p, Latest = last, Number = stops.Count + 1 });
		}
		return stops;
	}

	/// <summary>
	/// An Apple Maps link with directions to the structure: to its position, or to its address when it has no
	/// coordinates (see <see cref="RouteOrder.MapsLink"/>); null when it has neither.
	/// </summary>
	public static string MapsLink(PropertyRecord p, ReferenceData reference)
	{
		if (p == null)
		{
			return null;
		}
		AssessmentView v = View(p, reference);
		string address = Fmt.Join(", ", v.StreetAddress, v.City?.Trim(), Fmt.Join(" ", reference.StateAbbreviation(p.StateID), v.Zip));
		return RouteOrder.MapsLink(new RouteStop(p.PropertyID, (double?)p.Latitude, (double?)p.Longitude, address));
	}

	/// <summary>The route sheet: one line per stop. Writes the PDF only once it rendered.</summary>
	public static void WriteRouteSheet(TeamRecord team, IReadOnlyList<FieldKitStop> stops, ReferenceData reference, FieldKitOptions options, string path)
	{
		ArgumentNullException.ThrowIfNull(team);
		ArgumentNullException.ThrowIfNull(stops);
		options ??= new FieldKitOptions();
		DateTime printed = options.GeneratedOn ?? DateTime.Now;
		Document doc = Theme.NewDocument("Route sheet: " + team.Name, $"{stops.Count} stop(s)");
		Section sec = Kit.NewSection(doc, landscape: false, "Route sheet", printed, Clean(options.CommunityName), restartPageNumbers: false);
		Heading(sec, "Route sheet: " + team.Name, options, printed);
		if (!string.IsNullOrWhiteSpace(team.Members))
		{
			Kit.Note(sec, team.Members.Trim());
		}
		Kit.Gap(sec, 8);

		Column[] columns =
		{
			new Column("#", 0.35, ParagraphAlignment.Right), new Column("Address", 2.3), new Column("Owner", 1.4), new Column("Code", 0.85),
			new Column("Due", 0.8), new Column("Directions", 0.9), new Column("Done", 0.7),
		};
		Table t = Kit.Data(sec, $"{stops.Count} stop(s)", columns, stops.Select(s => new[]
		{
			s.Number.ToString(Us),
			Address(s, reference),
			Fmt.Join(" ", s.Property.OwnerFirstName, s.Property.OwnerLastName),
			s.Assignment.ShortCode ?? "",
			s.Assignment.DueDate?.ToString("MM/dd", Us) ?? "",
			"",
			"",
		}));
		// The directions column holds a link; Kit.Data writes text, so the link goes into each row's cell here.
		int first = t.Rows.Count - stops.Count;
		for (int i = 0; i < stops.Count; i++)
		{
			if (MapsLink(stops[i].Property, reference) is string link)
			{
				Cell cell = t.Rows[first + i].Cells[5];
				Paragraph p = cell.Elements.LastObject as Paragraph ?? cell.AddParagraph();
				FormattedText text = p.AddHyperlink(link, HyperlinkType.Url).AddFormattedText("Open in Maps");
				text.Color = Theme.Accent;
				text.Underline = Underline.Single;
			}
		}
		Kit.Note(sec, "Code: type it into the phone form's search, or scan the QR code on the structure's worksheet. Directions open Apple Maps.");
		Render(doc, path);
	}

	/// <summary>One pre-filled worksheet page per stop. Writes the PDF only once it rendered.</summary>
	public static void WriteWorksheets(TeamRecord team, IReadOnlyList<FieldKitStop> stops, ReferenceData reference, FieldKitOptions options, string path)
	{
		ArgumentNullException.ThrowIfNull(team);
		ArgumentNullException.ThrowIfNull(stops);
		if (stops.Count == 0)
		{
			throw new InvalidOperationException("This team has nothing assigned.");
		}
		options ??= new FieldKitOptions();
		DateTime printed = options.GeneratedOn ?? DateTime.Now;
		Document doc = Theme.NewDocument("Field worksheets: " + team.Name, $"{stops.Count} structure(s)");
		foreach (FieldKitStop s in stops)
		{
			Worksheet(doc, team, s, reference, options, printed);
		}
		Render(doc, path);
	}

	private static void Worksheet(Document doc, TeamRecord team, FieldKitStop s, ReferenceData reference, FieldKitOptions o, DateTime printed)
	{
		Section sec = Kit.NewSection(doc, landscape: false, "Field worksheet", printed, Clean(o.CommunityName), restartPageNumbers: false);
		AssessmentView v = View(s.Property, reference);

		// Address and code on the left, the QR code on the right.
		Table top = Kit.NewTable(sec, new[] { Kit.PortraitWidth - 1.3, 1.3 });
		Row r = top.AddRow();
		Paragraph addr = r.Cells[0].AddParagraph($"{s.Number}. {Address(s, reference)}");
		addr.Format.Font.Size = 17;
		addr.Format.Font.Bold = true;
		addr.Format.Font.Color = Theme.Accent;
		Paragraph sub = r.Cells[0].AddParagraph(string.Join("   |   ", new[] { Fmt.Join(", ", v.City.Trim(), v.Zip), team.Name, Clean(o.EventName) }.Where(x => !string.IsNullOrEmpty(x))));
		sub.Style = Theme.NoteStyle;
		Paragraph code = r.Cells[0].AddParagraph("Code " + (s.Assignment.ShortCode ?? ""));
		code.Format.Font.Size = 13;
		code.Format.Font.Bold = true;
		code.Format.SpaceBefore = 4;
		if (MapsLink(s.Property, reference) is string link)
		{
			Paragraph dir = r.Cells[0].AddParagraph();
			FormattedText text = dir.AddHyperlink(link, HyperlinkType.Url).AddFormattedText("Open in Maps");
			text.Color = Theme.Accent;
			text.Underline = Underline.Single;
		}
		if (!string.IsNullOrWhiteSpace(s.Assignment.ShortCode))
		{
			Paragraph q = r.Cells[1].AddParagraph();
			q.Format.Alignment = ParagraphAlignment.Right;
			MigraDoc.DocumentObjectModel.Shapes.Image img = q.AddImage("base64:" + Convert.ToBase64String(QrPng(s.Assignment.ShortCode)));
			img.Width = Unit.FromInch(1.1);
			img.LockAspectRatio = true;
		}
		Kit.Gap(sec, 8);

		byte[] map = Normalized(o.MapSnippet?.Invoke(s));
		if (map is { Length: > 0 })
		{
			Paragraph m = sec.AddParagraph();
			m.Format.Alignment = ParagraphAlignment.Center;
			MigraDoc.DocumentObjectModel.Shapes.Image img = m.AddImage("base64:" + Convert.ToBase64String(map));
			using (SKCodec codec = SKCodec.Create(new MemoryStream(map)))
			{
				// Full width, but never taller than MapHeight inches, so the worksheet stays on one page.
				img.Width = Unit.FromInch(Math.Min(Kit.PortraitWidth, MapHeight * codec.Info.Width / Math.Max(1, codec.Info.Height)));
			}
			img.LockAspectRatio = true;
			Kit.Gap(sec, 6);
		}

		PropertyRecord p = s.Property;
		Kit.Fields(sec, "On file", new[] { 1.2, 2.3, 1.3, 2.3 }, new[]
		{
			new[] { new Field("Owner", Fmt.Join(" ", p.OwnerFirstName, p.OwnerLastName)), new Field("Parcel number", Clean(p.ParcelNumber)) },
			new[] { new Field("Structure type", v.IsResidential ? "Residential" : p.StructureTypeID == 2 ? "Non-residential" : ""), new Field("Foundation", v.Name(p.FoundationTypeID)) },
			new[] { new Field("Flood zone", v.Name(p.FIRMZone)), new Field("BFE", Fmt.Number2(p.BFE)) },
			new[] { new Field("First floor elev.", Fmt.Number2(p.FirstFloorElevation)), new Field("Square feet", p.SquareFootage?.ToString("N0", Us) ?? "") },
			new[] { new Field("Last assessment", s.Latest == null ? "None yet" : Fmt.Join(", ", s.Latest.DateofInspection?.ToString("MM/dd/yyyy", Us), s.Latest.DamageMessage)), new Field("Due", s.Assignment.DueDate?.ToString("MM/dd/yyyy", Us) ?? "") },
			new[] { new Field("Notes", Clean(s.Assignment.Notes), Wide: true) },
		});
		Kit.Gap(sec, 10);

		Kit.Fields(sec, "In the field", new[] { 1.6, 2.0, 1.6, 1.9 }, new[]
		{
			new[] { new Field("Date of inspection", ""), new Field("Inspector", "") },
			new[] { new Field("Depth of flood (ft)", ""), new Field("Above first floor (ft)", "") },
			new[] { new Field("Duration (hours)", ""), new Field("Occupied?", "Yes  /  No") },
			new[] { new Field("Photos taken", "Front  /  Back  /  Sides  /  Water line  /  Interior (circle each)", Wide: true) },
		});
		Kit.Gap(sec, 10);
		Table notes = Kit.NewTable(sec, new[] { Kit.PortraitWidth });
		Kit.TitleCell(notes.AddRow(), 0, 1, "Damage notes");
		for (int i = 0; i < 6; i++)
		{
			Row line = notes.AddRow();
			line.Height = Unit.FromPoint(22);
			Kit.Hairline(line.Cells[0]);
		}
	}

	/// <summary>A QR code (PNG) of a worksheet code, for the phone's camera.</summary>
	public static byte[] QrPng(string text)
	{
		using QRCodeGenerator generator = new QRCodeGenerator();
		using QRCodeData data = generator.CreateQrCode(text ?? "", QRCodeGenerator.ECCLevel.M);
		return Normalized(new PngByteQRCode(data).GetGraphic(10));
	}

	/// <summary>
	/// The picture re-encoded as a 32-bit PNG, which PDFsharp always embeds (it skips some 1-bit and palette PNGs, such
	/// as QRCoder's). Null when the bytes aren't a picture.
	/// </summary>
	private static byte[] Normalized(byte[] picture)
	{
		using SKBitmap bitmap = picture is { Length: > 0 } ? SKBitmap.Decode(picture) : null;
		if (bitmap == null)
		{
			return null;
		}
		using SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888);
		using SKImage image = SKImage.FromBitmap(rgba ?? bitmap);
		using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}

	private static void Heading(Section sec, string title, FieldKitOptions o, DateTime printed)
	{
		Paragraph heading = sec.AddParagraph(title);
		heading.Format.Font.Size = 18;
		heading.Format.Font.Bold = true;
		heading.Format.Font.Color = Theme.Accent;
		string line = string.Join("   |   ", new[] { Clean(o.CommunityName), Clean(o.EventName), "Printed " + printed.ToString("MMMM d, yyyy", Us) }.Where(x => !string.IsNullOrEmpty(x)));
		Paragraph sub = sec.AddParagraph(line);
		sub.Style = Theme.NoteStyle;
		sub.Format.SpaceBefore = 2;
	}

	private static AssessmentView View(PropertyRecord p, ReferenceData reference) =>
		new AssessmentView(new AssessmentFile { Property = p, Assessment = new AssessmentRecord() }, reference);

	private static string Address(FieldKitStop s, ReferenceData reference)
	{
		string a = View(s.Property, reference).StreetAddress;
		return string.IsNullOrWhiteSpace(a) ? "(no address)" : a;
	}

	private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

	private static void Render(Document doc, string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = doc };
		renderer.RenderDocument();
		using MemoryStream buffer = new MemoryStream();
		renderer.PdfDocument.Save(buffer, false);
		File.WriteAllBytes(path, buffer.ToArray());
	}
}
