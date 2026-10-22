using System.Diagnostics;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>
/// Field data in: photo order, the date and position stored in photos, matching a photo folder to structures, the
/// online look-up (Census geocoder and FEMA NFHL, from recorded answers) and depth-table suggestions.
/// </summary>
public class FieldDataUiTests
{
	/// <summary>Pumps the UI until <paramref name="done"/> (background jobs finish on the UI thread).</summary>
	private static async Task Until(UiSession ui, Func<bool> done, int timeoutMs = 15000)
	{
		Stopwatch clock = Stopwatch.StartNew();
		while (!done())
		{
			Assert.True(clock.ElapsedMilliseconds < timeoutMs, "Timed out waiting for the UI.");
			ui.Pump();
			await Task.Delay(10);
		}
		ui.Pump();
	}

	private static AssessmentEditorViewModel Open(UiSession ui, AssessmentFile f)
	{
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		return ui.Main.Editor;
	}

	private static string WritePhoto(string folder, string name, SKColor colour, DateTime? taken = null, double? lat = null, double? lon = null)
	{
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, name);
		File.WriteAllBytes(path, FieldPhotos.Jpeg(colour, taken, lat, lon));
		return path;
	}

	private static List<string> Names(AssessmentEditorViewModel e) => e.Photos.Select(p => p.Description).ToList();

	// ───────────── Photo order ─────────────

	[AvaloniaFact]
	public async Task PhotoOrder_MoveButtons_TheStarStaysFirst_AndTheOrderIsSaved()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		AssessmentEditorViewModel e = Open(ui, f);
		string folder = Path.Combine(ui.Folder, "in");
		await e.AddPhotosAsync(new[]
		{
			WritePhoto(folder, "front.jpg", new SKColor(70, 120, 180)),
			WritePhoto(folder, "kitchen.jpg", new SKColor(200, 160, 90)),
			WritePhoto(folder, "basement.jpg", new SKColor(120, 160, 110)),
		});
		await Task.WhenAll(e.Photos.Select(p => p.ThumbnailLoaded));
		e.SelectedSection = e.Sections.OfType<PhotosSectionViewModel>().Single();
		Assert.Equal(new[] { "front", "kitchen", "basement" }, Names(e));
		Assert.True(e.Photos[0].IsDefault);
		Assert.False(e.Photos[0].CanMoveLater);          // the report photo stays first
		Assert.False(e.Photos[1].CanMoveEarlier);        // nothing goes before it
		Assert.True(e.Photos[1].CanMoveLater);
		Assert.Equal("Move photo 3 earlier", e.Photos[2].MoveEarlierLabel);

		e.Photos[2].MoveEarlierCommand.Execute(null);
		Assert.Equal(new[] { "front", "basement", "kitchen" }, Names(e));
		e.MovePhotoTo(e.Photos[2], 0);                   // a drop on the report photo lands just after it
		Assert.Equal(new[] { "front", "kitchen", "basement" }, Names(e));
		e.Photos[1].MoveLaterCommand.Execute(null);
		Assert.Equal(new[] { "front", "basement", "kitchen" }, Names(e));
		e.Save();
		Assert.Equal(new[] { "front", "basement", "kitchen" }, ui.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments.Select(a => a.Description));

		// Starring a photo moves it to the front, since reports print it first.
		e.Photos[2].MakeDefaultCommand.Execute(null);
		Assert.Equal(new[] { "kitchen", "front", "basement" }, Names(e));
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("61-photo-order");
		e.Save();

		AssessmentEditorViewModel again = Open(ui, f);
		Assert.Equal(new[] { "kitchen", "front", "basement" }, Names(again));
		Assert.Contains(ui.Store.GetHistory(f.Assessment.AssessmentID), h => h.Label == "Photo order");

		// Undo puts the earlier order back.
		again.Undo();
		Assert.Equal(new[] { "front", "basement", "kitchen" }, Names(ui.Main.Editor));
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task PhotoDetails_AreStored_AndOffered_NeverAppliedSilently()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		f.Assessment.DateofInspection = null;
		ui.Store.SaveAssessment(f);
		AssessmentEditorViewModel e = Open(ui, f);
		string photo = WritePhoto(Path.Combine(ui.Folder, "in"), "IMG_0412.jpg", new SKColor(90, 110, 140), new DateTime(2026, 9, 15, 10, 30, 0), 39.103118, -84.51202);
		await e.AddPhotosAsync(new[] { photo });
		e.SelectedSection = e.Sections.OfType<PhotosSectionViewModel>().Single();

		Assert.True(e.HasPhotoOffer);
		Assert.True(e.CanUsePhotoLocation);
		Assert.True(e.CanUsePhotoDate);
		Assert.Contains("39.103118, -84.512020", e.PhotoOfferText);
		Assert.Contains("Sep 15, 2026", e.PhotoOfferText);
		Assert.Null(e.Property.Latitude);                // nothing changes until the inspector chooses
		Assert.Null(e.Assessment.DateofInspection);
		PhotoInfoRecord info = ui.Store.GetPhotoInfo(e.Photos.Single().Attachment.FileName);
		Assert.Equal(39.103118, info.Latitude!.Value, 5);
		Assert.Equal("IMG_0412.jpg", info.OriginalName);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		e.UsePhotoLocationCommand.Execute(null);
		Assert.Equal(39.103118m, e.Property.Latitude);
		Assert.Equal(-84.51202m, e.Property.Longitude);
		Assert.True(e.HasPhotoOffer);                    // the date is still on offer
		e.UsePhotoDateCommand.Execute(null);
		Assert.False(e.HasPhotoOffer);
		e.Save();
		AssessmentFile saved = ui.Store.LoadAssessment(f.Assessment.AssessmentID);
		Assert.Equal(39.103118m, saved.Property.Latitude);
		Assert.Equal(new DateTime(2026, 9, 15), saved.Assessment.DateofInspection);

		// A structure that already has coordinates and a date gets no offer.
		await e.AddPhotosAsync(new[] { WritePhoto(Path.Combine(ui.Folder, "in"), "IMG_0413.jpg", new SKColor(90, 110, 140), new DateTime(2026, 9, 16), 39.2, -84.6) });
		Assert.False(e.HasPhotoOffer);
	}

	[AvaloniaFact]
	public async Task PhotoFolder_IsMatchedToStructures_ThenTheTickedAreAttached()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		PropertyRecord mill = ui.Store.GetProperties().Single(p => p.StreetNumber == "48");
		mill.Latitude = 39.150000m;
		mill.Longitude = -84.400000m;
		ui.Store.SaveProperty(mill);
		string folder = Path.Combine(ui.Folder, "Field photos");
		WritePhoto(folder, "123 River St - front.jpg", new SKColor(70, 120, 180));
		WritePhoto(folder, "IMG_0001.jpg", new SKColor(200, 160, 90), new DateTime(2026, 9, 15, 9, 0, 0), 39.150010, -84.400010);
		WritePhoto(folder, "IMG_0002.jpg", new SKColor(120, 160, 110));
		File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a photo");

		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Dialogs.Answers.Enqueue(folder);
		Task match = ui.Main.ImportExport.MatchPhotoFolderCommand.ExecuteAsync(null);
		await Until(ui, () => ui.Main.Sheets.Current is PhotoFolderSheetViewModel);
		PhotoFolderSheetViewModel sheet = (PhotoFolderSheetViewModel)ui.Main.Sheets.Current;
		Assert.Equal(new[] { "123 River St - front.jpg", "IMG_0001.jpg", "IMG_0002.jpg" }, sheet.Rows.Select(r => r.FileName));
		PhotoMatchRowViewModel byName = sheet.Rows[0];
		Assert.Equal("123 River Street", byName.Structure);
		Assert.Equal("file name has the address", byName.Reason);
		Assert.True(byName.Include);
		PhotoMatchRowViewModel byGps = sheet.Rows[1];
		Assert.StartsWith("48 Mill Creek", byGps.Structure);
		Assert.Equal("1 m", byGps.Distance);
		PhotoMatchRowViewModel none = sheet.Rows[2];
		Assert.False(none.Include);
		Assert.False(none.CanInclude);
		Assert.Contains("3 photos in Field photos: 2 matched", sheet.Summary);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("62-photo-folder");

		await sheet.AttachCommand.ExecuteAsync(null);
		await match;
		Assert.Null(ui.Main.Sheets.Current);
		Assert.Contains(ui.Dialogs.Notifications, n => n == "Photos attached: Attached 2 photos to 2 structures.");
		AssessmentFile ranch = ui.Store.LoadAllAssessments().Single(a => a.Property.StreetNumber == "123");
		AttachmentRecord front = Assert.Single(ranch.Attachments);
		Assert.Equal("123 River St - front", front.Description);
		Assert.True(front.DefaultImage);
		Assert.True(File.Exists(ui.Store.PhotoPath(front)));
		AssessmentFile millAssessment = ui.Store.LoadAllAssessments().Single(a => a.Property.StreetNumber == "48");
		AttachmentRecord gps = Assert.Single(millAssessment.Attachments);
		Assert.Equal(39.15001, ui.Store.GetPhotoInfo(gps.FileName).Latitude!.Value, 5);
		Assert.Contains(ui.Store.GetHistory(ranch.Assessment.AssessmentID), h => h.Label == "Photos added from the folder Field photos" && h.Via == SaveVia.PhotoFolder);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	// ───────────── Online look-up ─────────────

	[AvaloniaFact]
	public async Task LookUpOnline_ShowsCurrentAndProposed_AppliesTheTicked_AndRecordsTheSource()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		FakeOnline online = FakeOnline.Recorded();
		ui.Services.HttpClientFactory = () => new HttpClient(online);
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		AssessmentEditorViewModel e = Open(ui, f);
		SectionViewModel location = e.Sections[0];
		Assert.True(location.HasOnlineLookup);
		Assert.True(e.Sections.Single(s => s.Title == "Flood zone").HasOnlineLookup);
		Assert.False(e.Sections.Single(s => s.Title == "Building").HasOnlineLookup);

		Task lookUp = e.LookUpOnlineCommand.ExecuteAsync(null);
		AutoFillSheetViewModel sheet = Assert.IsType<AutoFillSheetViewModel>(ui.Main.Sheets.Current);
		await sheet.Lookup;
		Assert.False(sheet.HasError);
		Assert.StartsWith("https://geocoding.geo.census.gov/", online.Urls[0]);      // no coordinates yet: geocode first
		Assert.Contains("address=123%20River%20Street%2C%20Exampleville%2C%20OH%2043702", online.Urls[0]);
		ProposalRowViewModel Row(string label) => sheet.Rows.Single(r => r.Label == label);
		Assert.Equal("None", Row("Latitude").CurrentText);
		Assert.Equal("38.889910", Row("Latitude").ProposedText);
		Assert.True(Row("Latitude").IsChecked);
		Assert.True(Row("FIRM zone").IsChecked);
		Assert.Equal("AE", Row("FIRM zone").ProposedText);
		Assert.Equal("FEMA flood map", Row("FIRM zone").SourceText);
		Assert.Equal("Census geocoder", Row("Longitude").SourceText);
		// Values that would replace what's entered aren't ticked.
		ProposalRowViewModel community = Row("NFIP community ID");
		Assert.Equal("399990", community.CurrentText);
		Assert.True(community.WouldOverwrite);
		Assert.False(community.IsChecked);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("63-auto-fill");

		sheet.ApplyCommand.Execute(null);
		await lookUp;
		Assert.Equal(38.88991m, e.Property.Latitude);
		Assert.Equal("AE", ui.Store.Reference.LookupName(e.Property.FIRMZone));
		Assert.Equal(12.5m, e.Property.BFE);
		Assert.Equal("399990", e.Property.NFIPCID);       // left as it was
		AssessmentFile saved = ui.Store.LoadAssessment(f.Assessment.AssessmentID);
		Assert.Equal("AE", ui.Store.Reference.LookupName(saved.Property.FIRMZone));
		List<EditHistoryRecord> history = ui.Store.GetHistory(f.Assessment.AssessmentID);
		EditHistoryRecord source = Assert.Single(history, h => h.Label.StartsWith("Flood data from FEMA NFHL", StringComparison.Ordinal));
		Assert.Contains("coordinates from the Census geocoder", source.Label);
		Assert.Equal(SaveVia.Online, source.Via);
		Assert.Contains(history, h => h.Field == "Property.FIRMZone");
		Assert.True(e.CanUndo);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task LookUpOnline_ServiceDown_IsExplainedInTheSheet()
	{
		using UiSession ui = new UiSession();
		ui.Services.HttpClientFactory = () => new HttpClient(FakeOnline.Down());
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		AssessmentEditorViewModel e = Open(ui, f);
		Task lookUp = e.LookUpOnlineCommand.ExecuteAsync(null);
		AutoFillSheetViewModel sheet = Assert.IsType<AutoFillSheetViewModel>(ui.Main.Sheets.Current);
		await sheet.Lookup;
		Assert.True(sheet.HasError);
		Assert.Equal("The Census geocoder had a problem and couldn't answer (error 503). Try again in a few minutes.", sheet.ErrorText);
		Assert.Empty(ui.Services.Errors.Shown);           // shown in the sheet, not as a crash
		Assert.False(sheet.ApplyCommand.CanExecute(null));
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// Try again once the service is back.
		ui.Services.HttpClientFactory = () => new HttpClient(FakeOnline.Recorded());
		sheet.RetryCommand.Execute(null);
		await sheet.Lookup;
		Assert.False(sheet.HasError);
		Assert.NotEmpty(sheet.Rows);
		sheet.CancelCommand.Execute(null);
		await lookUp;
		Assert.Null(ui.Store.LoadAssessment(f.Assessment.AssessmentID).Property.Latitude);
	}

	[AvaloniaFact]
	public async Task LookUpFloodData_ForTheChecked_ReviewThenApplyInOneBatch()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		ui.Services.HttpClientFactory = () => new HttpClient(FakeOnline.Recorded("nfhl-zone-floodway.json"));
		ScreenTourTests.AddSamples(ui.Store);
		StructuresViewModel s = ui.Main.Structures;
		s.Reload();
		StructureRowViewModel ranch = s.Rows.First(r => r.Address.StartsWith("123"));
		StructureRowViewModel mill = s.Rows.First(r => r.Address.StartsWith("48"));
		ranch.IsChecked = true;
		mill.IsChecked = true;
		ui.Store.SetStatus(mill.AssessmentID!.Value, AssessmentStatus.Final);

		Task lookUp = s.LookUpFloodDataCommand.ExecuteAsync(null);
		await Until(ui, () => ui.Main.Sheets.Current is BulkAutoFillSheetViewModel);
		BulkAutoFillSheetViewModel sheet = (BulkAutoFillSheetViewModel)ui.Main.Sheets.Current;
		Assert.Equal(2, sheet.Structures.Count);
		Assert.All(sheet.Structures, x => Assert.Contains(x.Rows, r => r.Label == "Regulatory floodway" && r.ProposedText == "Yes"));
		Assert.Contains("Values found for 2 of 2 structures.", sheet.Summary);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("63b-auto-fill-bulk");

		sheet.ApplyCommand.Execute(null);
		Assert.Equal("Updated 1 structure. 1 Final was left as it was.", sheet.ResultText);
		AssessmentFile saved = ui.Store.LoadAssessment(ranch.AssessmentID!.Value);
		Assert.Equal(38.88991m, saved.Property.Latitude);
		Assert.Equal("Yes", ui.Store.Reference.LookupName(saved.Property.RegulatoryFloodway));
		EditHistoryRecord source = Assert.Single(ui.Store.GetHistory(saved.Assessment.AssessmentID), h => h.Label.StartsWith("Flood data from FEMA NFHL", StringComparison.Ordinal));
		Assert.NotNull(source.BatchId);
		Assert.Contains(ui.Store.GetBatch(source.BatchId), h => h.Field == "Property.Latitude");
		Assert.Null(ui.Store.LoadAssessment(mill.AssessmentID.Value).Property.Latitude);   // Final: left alone
		sheet.CancelCommand.Execute(null);
		await lookUp;
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	// ───────────── Depth tables ─────────────

	[AvaloniaFact]
	public async Task DepthTable_FromSettings_SuggestsDamage_WhichMustBeAcceptedBeforeFinal()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(height: 1400);

		// Settings: a new table, saved as a file next to the database.
		DepthTablesViewModel tables = ui.Main.Settings.DepthTables;
		Assert.False(tables.HasTables);
		tables.NewTableCommand.Execute(null);
		tables.TableName = "Riverine, one story";
		Assert.Equal(13, tables.ElementNames.Count);     // site-built and manufactured houses' elements together
		Assert.Contains("Floor Finish", tables.ElementNames);
		DepthRowViewModel shallow = Assert.Single(tables.Rows);
		void Set(DepthRowViewModel row, string element, decimal value) => row.Cells.Single(c => c.Element == element).Percent = value;
		Set(shallow, "Floor Finish", 10);
		Set(shallow, "Interior Finish", 5);
		Set(shallow, "Plumbing", 0);
		tables.AddRowCommand.Execute(null);
		DepthRowViewModel deep = tables.Rows[1];
		deep.Depth = 4;
		Set(deep, "Floor Finish", 90);
		Set(deep, "Interior Finish", 85);
		Set(deep, "Plumbing", 40);
		Set(deep, "Foundation", 20);
		Assert.Equal(new[] { "Riverine, one story" }, ui.Services.DepthTables.Names());
		Assert.StartsWith(Path.GetDirectoryName(ui.Store.Database.FilePath)!, ui.Services.DepthTables.Folder);
		DepthDamageTable stored = ui.Services.DepthTables.Load("Riverine, one story");
		Assert.Equal(new[] { 0m, 4m }, stored.Rows.Select(r => r.DepthFt));
		Assert.Equal(DepthTableBuildingType.Residential, stored.BuildingType);
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Main.Settings.Show(SettingsCategory.DepthTables);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// An assessment with 2 ft of water above the floor; only the foundation has been entered.
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		f.ElementPercentages.ForEach(x => x.UserDamage = x.ElementName == "Foundation" ? 10m : 0m);
		f.Assessment.DepthOfFloodAboveFFE = 2m;
		ui.Store.SaveAssessment(f);
		AssessmentEditorViewModel e = Open(ui, f);
		e.SelectedSection = e.Sections.OfType<DamageSectionViewModel>().Single();
		Assert.True(e.HasDepthTables);
		Assert.Equal("Riverine, one story", e.SelectedDepthTable.Name);
		Assert.True(e.CanSuggestDamage);
		e.SuggestDamageCommand.Execute(null);
		ElementRowViewModel Element(string name) => e.Elements.Single(x => x.Name == name);
		Assert.Equal(50m, Element("Floor Finish").UserDamage);
		Assert.Equal(45m, Element("Interior Finish").UserDamage);
		Assert.Equal(20m, Element("Plumbing").UserDamage);
		Assert.Equal(10m, Element("Foundation").UserDamage);   // entered by the inspector: kept
		Assert.True(Element("Floor Finish").IsSuggested);
		Assert.False(Element("Foundation").IsSuggested);
		Assert.Equal(3, e.SuggestedCount);
		Assert.Equal("Suggested 3 elements from \"Riverine, one story\" at 2 ft. 1 you had entered was left as it was.", e.DepthResultText);
		Assert.Equal(3, ui.Store.GetDamageSuggestions(f.Assessment.AssessmentID).Count);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("64-depth-suggest");

		// Final is refused while suggestions remain, with the store's message.
		await e.SetStatusCommand.ExecuteAsync("Final");
		Assert.Contains("suggested from the depth table", ui.Services.Errors.Shown.Last().Message);
		Assert.Equal(AssessmentStatus.Draft, ui.Store.GetStatus(f.Assessment.AssessmentID).Value);
		ui.Main.Sheets.Current.Cancel();

		// Changing a value makes it the inspector's own; accepting does too.
		Element("Plumbing").UserDamage = 25m;
		Assert.False(Element("Plumbing").IsSuggested);
		e.Save();
		Assert.Equal(2, ui.Store.GetDamageSuggestions(f.Assessment.AssessmentID).Count);
		Element("Floor Finish").AcceptSuggestionCommand.Execute(null);
		Assert.False(Element("Floor Finish").IsSuggested);
		Assert.Equal(50m, Element("Floor Finish").UserDamage);
		Assert.Equal(1, e.SuggestedCount);
		e.AcceptAllSuggestionsCommand.Execute(null);
		Assert.False(e.HasSuggestions);
		Assert.Empty(ui.Store.GetDamageSuggestions(f.Assessment.AssessmentID));
		await e.SetStatusCommand.ExecuteAsync("Final");
		Assert.Equal(AssessmentStatus.Final, ui.Store.GetStatus(f.Assessment.AssessmentID).Value);
		Assert.Contains(ui.Store.GetHistory(f.Assessment.AssessmentID), h => h.Label == "Accepted suggested damage: Floor Finish 50%");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void DepthTable_NotShown_WithoutATableForTheBuildingType()
	{
		using UiSession ui = new UiSession();
		ui.Services.DepthTables.Save(new DepthDamageTable { Name = "Commercial", BuildingType = DepthTableBuildingType.NonResidential, Rows = { new DepthDamageRow { DepthFt = 1, Percents = { ["Foundation"] = 5 } } } });
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		AssessmentEditorViewModel e = Open(ui, f);
		Assert.False(e.HasDepthTables);
		Assert.False(e.CanSuggestDamage);
		Assert.Contains("Enter the depth", e.DepthHelperText);

		// A table added in Settings while the assessment is open shows on the Damage step.
		ui.Services.DepthTables.Save(new DepthDamageTable { Name = "Houses", Rows = { new DepthDamageRow { DepthFt = 1, Percents = { ["Foundation"] = 5 } } } });
		e.SelectedSection = e.Sections.OfType<DamageSectionViewModel>().Single();
		Assert.True(e.HasDepthTables);
		Assert.Equal("Houses", e.SelectedDepthTable.Name);
	}
}
