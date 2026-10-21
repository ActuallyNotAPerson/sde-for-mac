using System.Text;
using Sde.Core.Assessments;
using Sde.Core.Exchange;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// The Mac-only field data: photo order, the details stored in photos (PhotoInfo) and depth-table suggestions
/// (DamageSuggestion). None of it may change FEMA's Export.json.
/// </summary>
public class FieldDataTests : IDisposable
{
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private AttachmentRecord Photo(AssessmentFile f, string name)
	{
		string path = Path.Combine(_db.Folder, name + ".jpg");
		File.WriteAllBytes(path, Encoding.UTF8.GetBytes(name));
		return _db.Store.ImportPhoto(f.Assessment.AssessmentID, path, name);
	}

	private AssessmentFile RanchWithPhotos(params string[] names)
	{
		AssessmentFile f = SampleCases.RanchFlood(_db.Store);
		foreach (string n in names)
		{
			AttachmentRecord a = Photo(f, n);
			a.DefaultImage = !f.Attachments.Any(x => x.Active);
			f.Attachments.Add(a);
		}
		_db.Store.SaveAssessment(f);
		return f;
	}

	private string Descriptions(Guid id) => string.Join(",", _db.Store.LoadAssessment(id).Attachments.Select(a => a.Description));

	[Fact]
	public void PhotoOrder_IsTheListOrder_AndIsSavedAsNewAttachmentIds()
	{
		AssessmentFile f = RanchWithPhotos("front", "kitchen", "basement");
		Guid id = f.Assessment.AssessmentID;
		Assert.Equal("front,kitchen,basement", Descriptions(id));

		AssessmentFile edit = _db.Store.LoadAssessment(id);
		AttachmentRecord basement = edit.Attachments[2];
		edit.Attachments.RemoveAt(2);
		edit.Attachments.Insert(1, basement);
		_db.Store.SaveAssessment(edit);

		AssessmentFile saved = _db.Store.LoadAssessment(id);
		Assert.Equal("front,basement,kitchen", Descriptions(id));
		Assert.Equal(saved.Attachments.Select(a => a.AttachmentID).Order(), saved.Attachments.Select(a => a.AttachmentID));
		// The in-memory copy got the new IDs too, so saving it again changes nothing.
		Assert.Equal(edit.Attachments.Select(a => a.AttachmentID), saved.Attachments.Select(a => a.AttachmentID));
		_db.Store.SaveAssessment(edit);
		Assert.Equal(saved.Attachments.Select(a => a.AttachmentID), _db.Store.LoadAssessment(id).Attachments.Select(a => a.AttachmentID));
		Assert.Equal(3, _db.Database.Read(c => Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT COUNT(*) FROM Attachment")));

		EditHistoryRecord order = Assert.Single(_db.Store.GetHistory(id), h => h.Field == AssessmentDiff.PhotoKey + ".Order");
		Assert.Equal("front, kitchen, basement", order.OldValue);
		Assert.Equal("front, basement, kitchen", order.NewValue);
	}

	[Fact]
	public void PhotoOrder_OfALockedAssessment_IsRefused()
	{
		AssessmentFile f = RanchWithPhotos("front", "kitchen");
		_db.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		AssessmentFile edit = _db.Store.LoadAssessment(f.Assessment.AssessmentID);
		edit.Attachments.Reverse();
		Assert.Throws<AssessmentLockedException>(() => _db.Store.SaveAssessment(edit));
		Assert.Equal("front,kitchen", Descriptions(f.Assessment.AssessmentID));
	}

	[Fact]
	public void PhotoOrder_ReachesTheExport_InTheSameShape()
	{
		AssessmentFile f = RanchWithPhotos("front", "kitchen", "basement");
		ExchangeService x = new ExchangeService(_db.Store);
		string before = SdeExchangeFolder.Serialize(x.ReadDataset());

		AssessmentFile edit = _db.Store.LoadAssessment(f.Assessment.AssessmentID);
		edit.Attachments.Reverse();
		edit.Attachments.ForEach(a => a.DefaultImage = a.Description == "basement");
		_db.Store.SaveAssessment(edit);
		SdeDataset after = x.ReadDataset();
		Assert.Equal(new[] { "basement", "kitchen", "front" }, after.Attachments.Select(a => a.Description));
		// Same records and keys; only the order and the (arbitrary) IDs and default flag differ.
		Assert.Equal(Keys(before), Keys(SdeExchangeFolder.Serialize(after)));
	}

	private static List<string> Keys(string json) =>
		System.Text.RegularExpressions.Regex.Matches(json, "\"(\\w+)\":").Select(m => m.Groups[1].Value).ToList();

	[Fact]
	public void PhotoInfo_IsStoredByFileName()
	{
		AssessmentFile f = RanchWithPhotos("front");
		AttachmentRecord a = _db.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments.Single();
		_db.Store.SavePhotoInfo(new PhotoInfoRecord
		{
			FileName = a.FileName, AssessmentID = f.Assessment.AssessmentID, TakenOn = new DateTime(2026, 9, 15, 10, 30, 0),
			Latitude = 39.103118, Longitude = -84.51202, OriginalName = "IMG_0001.HEIC",
		});
		PhotoInfoRecord info = _db.Store.GetPhotoInfo(a.FileName);
		Assert.Equal(new DateTime(2026, 9, 15, 10, 30, 0), info.TakenOn);
		Assert.Equal(39.103118, info.Latitude!.Value, 6);
		Assert.True(info.HasPosition);
		Assert.Equal("IMG_0001.HEIC", Assert.Single(_db.Store.GetPhotoInfos(f.Assessment.AssessmentID)).OriginalName);

		// Reordering (new attachment IDs) doesn't lose it; deleting the assessment does.
		AssessmentFile edit = _db.Store.LoadAssessment(f.Assessment.AssessmentID);
		edit.Attachments.Add(Photo(edit, "back"));
		edit.Attachments.Reverse();
		_db.Store.SaveAssessment(edit);
		Assert.NotNull(_db.Store.GetPhotoInfo(a.FileName));
		_db.Store.DeleteAssessment(f.Assessment.AssessmentID);
		Assert.Null(_db.Store.GetPhotoInfo(a.FileName));
	}

	[Fact]
	public void AttachPhotos_OneBatch_FirstBecomesReportPhoto_LockedSkipped()
	{
		AssessmentFile ranch = SampleCases.RanchFlood(_db.Store);
		PropertyRecord p = _db.Store.SaveProperty(new PropertyRecord { StreetNumber = "9", StreetName = "Test", StructureTypeID = 1 });
		AssessmentFile other = _db.Store.CreateAssessment(p.PropertyID, false);
		_db.Store.SetStatus(other.Assessment.AssessmentID, AssessmentStatus.Final);

		List<AttachedPhoto> photos = new()
		{
			new AttachedPhoto(ranch.Assessment.AssessmentID, Photo(ranch, "a"), new PhotoInfoRecord { Latitude = 39.1, Longitude = -84.5 }),
			new AttachedPhoto(ranch.Assessment.AssessmentID, Photo(ranch, "b"), null),
			new AttachedPhoto(other.Assessment.AssessmentID, Photo(other, "c"), null),
		};
		BulkResult r = _db.Store.AttachPhotos(photos, SaveContext.Batch(SaveVia.PhotoFolder, "Photos added from the folder Field"));
		Assert.Equal(1, r.Changed);
		Assert.Equal(new[] { other.Assessment.AssessmentID }, r.Locked);
		List<AttachmentRecord> attached = _db.Store.LoadAssessment(ranch.Assessment.AssessmentID).Attachments;
		Assert.Equal(new[] { "a", "b" }, attached.Select(a => a.Description));
		Assert.True(attached[0].DefaultImage);
		Assert.False(attached[1].DefaultImage);
		Assert.Empty(_db.Store.LoadAssessment(other.Assessment.AssessmentID).Attachments);
		Assert.Equal(39.1, _db.Store.GetPhotoInfo(attached[0].FileName).Latitude);
		Assert.Contains(_db.Store.GetBatch(r.BatchId), h => h.Label == "Photos added from the folder Field");
	}

	private AssessmentFile Suggest(params (string Element, decimal Percent)[] values)
	{
		AssessmentFile f = SampleCases.RanchFlood(_db.Store);
		foreach ((string element, decimal percent) in values)
		{
			f.ElementPercentages.Single(e => e.ElementName == element).UserDamage = percent;
		}
		_db.Store.SaveAssessment(f);
		int added = _db.Store.AddDamageSuggestions(f.Assessment.AssessmentID,
			values.Select(v => new DamageSuggestionRecord { ElementName = v.Element, SuggestedPercent = v.Percent, DepthFt = 2.5m, TableName = "Test table" }).ToList());
		Assert.Equal(values.Length, added);
		return f;
	}

	[Fact]
	public void DamageSuggestions_BlockFinal_UntilAcceptedOrChanged()
	{
		AssessmentFile f = Suggest(("Foundation", 15m), ("Plumbing", 40m), ("Electrical", 45m));
		Guid id = f.Assessment.AssessmentID;
		Assert.Equal(3, _db.Store.GetDamageSuggestions(id).Count);
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _db.Store.SetStatus(id, AssessmentStatus.Final));
		Assert.Contains("suggested from the depth table", ex.Message);
		Assert.Contains(_db.Store.GetHistory(id), h => h.Label.StartsWith("Damage suggested from depth table \"Test table\" at 2.5 ft (3 elements"));

		// Changing a value makes it the inspector's own: its suggestion goes on the next save.
		AssessmentFile edit = _db.Store.LoadAssessment(id);
		edit.ElementPercentages.Single(e => e.ElementName == "Plumbing").UserDamage = 55m;
		_db.Store.SaveAssessment(edit);
		Assert.Equal(new[] { "Electrical", "Foundation" }, _db.Store.GetDamageSuggestions(id).Select(s => s.ElementName).Order());

		Assert.Equal(1, _db.Store.AcceptDamageSuggestions(id, new[] { "Foundation" }));
		Assert.Contains(_db.Store.GetHistory(id), h => h.Label == "Accepted suggested damage: Foundation 15%");
		Assert.Throws<InvalidOperationException>(() => _db.Store.SetStatus(id, AssessmentStatus.Final));
		Assert.Equal(1, _db.Store.AcceptDamageSuggestions(id));
		Assert.Empty(_db.Store.GetDamageSuggestions(id));
		Assert.Equal(AssessmentStatus.Final, _db.Store.SetStatus(id, AssessmentStatus.Final).Value);
		Assert.Equal(55m, _db.Store.LoadAssessment(id).ElementPercentages.Single(e => e.ElementName == "Plumbing").UserDamage);
	}

	[Fact]
	public void DamageSuggestions_OnlyRecordedWhenTheSavedValueIsTheSuggestion()
	{
		AssessmentFile f = SampleCases.RanchFlood(_db.Store);
		int added = _db.Store.AddDamageSuggestions(f.Assessment.AssessmentID, new[]
		{
			new DamageSuggestionRecord { ElementName = "Foundation", SuggestedPercent = 10m, TableName = "T" },   // the saved value
			new DamageSuggestionRecord { ElementName = "Plumbing", SuggestedPercent = 99m, TableName = "T" },     // not saved
		});
		Assert.Equal(1, added);
		Assert.Equal("Foundation", Assert.Single(_db.Store.GetDamageSuggestions(f.Assessment.AssessmentID)).ElementName);
	}

	[Fact]
	public void ExportJson_IsUnchangedByPhotoInfoAndSuggestions()
	{
		AssessmentFile f = RanchWithPhotos("front", "kitchen");
		f = _db.Store.LoadAssessment(f.Assessment.AssessmentID);
		ExchangeService x = new ExchangeService(_db.Store);
		(string first, _) = x.Export(x.ReadDataset(), _db.Folder);

		_db.Store.SavePhotoInfo(new PhotoInfoRecord { FileName = f.Attachments[0].FileName, AssessmentID = f.Assessment.AssessmentID, Latitude = 39.1, Longitude = -84.5, TakenOn = DateTime.Today });
		_db.Store.AddDamageSuggestions(f.Assessment.AssessmentID, new[] { new DamageSuggestionRecord { ElementName = "Foundation", SuggestedPercent = 10m, TableName = "T", DepthFt = 1m } });
		_db.Store.AcceptDamageSuggestions(f.Assessment.AssessmentID, new[] { "Nothing" });
		(string second, _) = x.Export(x.ReadDataset(), _db.Folder);

		Assert.Equal(File.ReadAllBytes(Path.Combine(first, "Export.json")), File.ReadAllBytes(Path.Combine(second, "Export.json")));
		string json = File.ReadAllText(Path.Combine(second, "Export.json"));
		Assert.DoesNotContain("PhotoInfo", json);
		Assert.DoesNotContain("Suggest", json);
		Assert.DoesNotContain("TakenOn", json);
	}
}
