using System.IO.Compression;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Reports.Tests;

/// <summary>Sample structures for the report tests (in addition to SampleCases.RanchFlood).</summary>
internal static class Fixtures
{
	public const int UnionCountyOhio = 2122;

	public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

	/// <summary>SampleCases.RanchFlood with the non-calculation fields filled in (so every label has a value to check).</summary>
	public static AssessmentFile Ranch(SdeStore store)
	{
		AssessmentFile f = SampleCases.RanchFlood(store);
		PropertyRecord p = f.Property;
		p.CountyID = UnionCountyOhio;
		p.Subdivision = "Riverside Addition";
		p.ParcelNumber = "092-0004-0118-00";
		p.LotNumber = "18";
		p.FirstFloorElevation = 486.5m;
		p.Datum = "NAVD 88";
		p.Latitude = 39.098712m;
		p.Longitude = -84.507634m;
		p.PhoneNumber = "(555) 555-0142";
		p.FIRMPanelNumber = "39061C0232";
		p.PanelSuffix = "E";
		p.FIRMPanelDate = new DateTime(2010, 9, 17);
		p.FIRMZone = 30;               // AE
		p.BFE = 489.25m;
		p.RegulatoryFloodway = 41;     // No
		p.StructureInformation = "Brick ranch, attached one-car garage.";
		p.MailingOwnerFirstName = "Pat";
		p.MailingOwnerLastName = "Example";
		p.MailingStreetNumber = "77";
		p.MailingStreetName = "Hill";
		p.MailingStreetSuffixID = 172;
		p.MailingCity = "Exampleville";
		p.MailingStateID = 36;
		p.MailingCountyID = UnionCountyOhio;
		p.MailingZipCode = "437061234";
		p.MailingCareOf = "c/o J. Example";
		p.CommunityInformation = "Permit office: 805 Central Ave, Suite 500.";
		AssessmentRecord a = f.Assessment;
		a.DateofInspection = new DateTime(2026, 9, 21);
		a.InspectorPhoneNumber = "(555) 555-0100";
		a.DepthOfFlood = 490.75m;
		a.DepthOfFloodAboveFFE = 4.25m;
		a.CostData = "RSMeans 2026";
		a.CostDataDate = new DateTime(2026, 1, 1);
		CustomFieldRecord cf = store.Reference.CustomFields.OrderBy(c => c.zOrder).First();
		f.CustomFields = new List<CustomFieldResultRecord>
		{
			new CustomFieldResultRecord { CustomFieldID = cf.CustomFieldID, Result = "Block 12 sweep", zOrder = cf.zOrder },
		};
		store.SaveAssessment(f);
		return f;
	}

	/// <summary>A flooded 3-story office building (non-residential: StoryTypeID 102 "2 thru 4", StructureUseID 106 "Office Building").</summary>
	public static AssessmentFile Office(SdeStore store, string nfipId = "399990", string communityName = "Exampleville, City of")
	{
		PropertyRecord p = store.SaveProperty(new PropertyRecord
		{
			OwnerLastName = "Riverside Holdings LLC",
			StreetNumber = "400", StreetName = "Front", StreetSuffixID = 172, City = "Exampleville", StateID = 36, ZipCode = "437021234",
			CountyID = UnionCountyOhio, NFIPCID = nfipId, NFIPCommunityName = communityName,
			StructureTypeID = 2, StoryTypeID = 102, StructureUseID = 106, StructureQualityID = 54,
			YearOfConstruction = 1978, SquareFootage = 12000m, SprinklerSystem = 99,
			StructureInformation = "Three-story steel frame office.",
		});
		AssessmentFile f = store.CreateAssessment(p.PropertyID, applyDefaults: false);
		AssessmentRecord a = f.Assessment;
		a.InspectorName = "R. Inspector";
		a.DateofInspection = new DateTime(2026, 9, 22);
		a.DateDamageOccurred = new DateTime(2026, 9, 14);
		a.CauseofDamageID = LookupIds.CauseFlood;
		a.DurationOfFlood = 3;
		a.DurationOfFloodUnitsID = 359;
		a.DepthOfFloodAboveFFE = 2.5m;
		a.BaseCost = 165m;
		a.GeographicAdjustment = 0.93m;
		AssessmentCalculator calc = store.Calculator;
		calc.NormalizeRows(f);
		calc.ApplyDepreciationRating(f, 385);
		Dictionary<string, decimal> damage = new()
		{
			["Foundation"] = 5, ["Superstructure"] = 10, ["Roof Covering"] = 0, ["Plumbing"] = 40,
			["Electrical"] = 45, ["HVAC"] = 60, ["Interiors"] = 55,
		};
		foreach (ElementPercentageRecord e in f.ElementPercentages)
		{
			e.UserDamage = damage.GetValueOrDefault(e.ElementName);
		}
		CostAdjustmentResultRecord equipment = f.CostAdjustments.FirstOrDefault(c => store.Reference.LookupName(c.CostAdjustmentLookupID) == "Built-In Equipment");
		if (equipment != null)
		{
			equipment.Quantity = 2;
			equipment.UnitCost = 5000m;
		}
		store.SaveAssessment(f);
		return f;
	}

	/// <summary>Copies a photo into the attachments folder and saves it on the assessment.</summary>
	public static AttachmentRecord AddPhoto(SdeStore store, AssessmentFile f, string source, bool isDefault = false, string description = null)
	{
		AttachmentRecord att = store.ImportPhoto(f.Assessment.AssessmentID, source, description);
		att.DefaultImage = isDefault;
		f.Attachments.Add(att);
		store.SaveAssessment(f);   // assigns AttachmentID
		return att;
	}

	/// <summary>A solid-color RGB PNG of the given size (so tests can tell photos apart by pixel size).</summary>
	public static string SolidPng(string folder, int width, int height, byte r, byte g, byte b)
	{
		using MemoryStream raw = new MemoryStream();
		for (int y = 0; y < height; y++)
		{
			raw.WriteByte(0);
			for (int x = 0; x < width; x++)
			{
				raw.WriteByte(r);
				raw.WriteByte(g);
				raw.WriteByte(b);
			}
		}
		using MemoryStream z = new MemoryStream();
		using (ZLibStream zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
		{
			raw.Position = 0;
			raw.CopyTo(zs);
		}
		using MemoryStream png = new MemoryStream();
		png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
		byte[] ihdr = new byte[13];
		WriteBigEndian(ihdr, 0, (uint)width);
		WriteBigEndian(ihdr, 4, (uint)height);
		ihdr[8] = 8;   // bit depth
		ihdr[9] = 2;   // RGB
		Chunk(png, "IHDR", ihdr);
		Chunk(png, "IDAT", z.ToArray());
		Chunk(png, "IEND", Array.Empty<byte>());
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, $"solid-{width}x{height}-{Guid.NewGuid():N}.png");
		File.WriteAllBytes(path, png.ToArray());
		return path;
	}

	private static void Chunk(Stream s, string type, byte[] data)
	{
		byte[] len = new byte[4];
		WriteBigEndian(len, 0, (uint)data.Length);
		s.Write(len);
		byte[] typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
		s.Write(typeAndData);
		byte[] crc = new byte[4];
		WriteBigEndian(crc, 0, Crc32(typeAndData));
		s.Write(crc);
	}

	private static void WriteBigEndian(byte[] b, int at, uint v)
	{
		b[at] = (byte)(v >> 24);
		b[at + 1] = (byte)(v >> 16);
		b[at + 2] = (byte)(v >> 8);
		b[at + 3] = (byte)v;
	}

	private static uint Crc32(byte[] data)
	{
		uint crc = 0xFFFFFFFF;
		foreach (byte d in data)
		{
			crc ^= d;
			for (int k = 0; k < 8; k++)
			{
				crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
			}
		}
		return ~crc;
	}
}
