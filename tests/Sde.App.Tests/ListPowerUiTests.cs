using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Fields;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.Tests;

/// <summary>The Structures list's sorting, filters, columns and saved views, and the sheets that change many at once.</summary>
public class ListPowerUiTests
{
	private static UiSession WithSamples()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	[AvaloniaFact]
	public void Sorting_IsTheOrderReportsUse()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		s.SortBy("Damaged");
		Assert.Equal(new[] { "123 River Street", "48 Mill Creek Street", "700 Riverside" }, s.Rows.Select(r => r.Address));
		Assert.Equal(s.Rows.Where(r => r.AssessmentID != null).Select(r => r.AssessmentID!.Value), s.TargetIds());
		s.SortBy("Damaged");
		Assert.Equal("700 Riverside", s.Rows[0].Address);
		s.SortBy("Address");
		Assert.Equal(new[] { "48 Mill Creek Street", "123 River Street", "700 Riverside" }, s.Rows.Select(r => r.Address));
		ui.Pump();
		DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single();
		Assert.Equal("Structure ▲", grid.Columns.Single(c => c.SortMemberPath == "Address").Header);
	}

	[AvaloniaFact]
	public void Filters_AreRemembered_ForThisDatabase()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		s.MinPercent = 70;
		Assert.Equal(new[] { "123 River Street" }, s.Rows.Select(r => r.Address));
		Assert.Equal("Filters (1)", s.FiltersButtonText);
		s.MinPercent = null;
		s.StreetFilter = "river";
		Assert.Equal(2, s.Rows.Count);
		s.StatusFilter = 3;   // Final
		Assert.Empty(s.Rows);
		Assert.True(s.NoMatches);

		StructuresViewModel again = new StructuresViewModel(ui.Services, ui.Main);
		Assert.Equal("river", again.StreetFilter);
		Assert.Equal(3, again.StatusFilter);
		s.ClearFiltersCommand.Execute(null);
		Assert.Equal(3, s.Rows.Count);
	}

	[AvaloniaFact]
	public async Task SavedViews_KeepFiltersSortAndColumns()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		s.TypeFilter = 1;
		s.SortBy("Damaged");
		s.Columns.Single(c => c.Key == "Zip").IsVisible = true;
		ui.Pump();
		DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single();
		Assert.True(grid.Columns.Single(c => c.SortMemberPath == "Zip").IsVisible);

		Task save = s.SaveViewCommand.ExecuteAsync(null);
		NameSheetViewModel name = Assert.IsType<NameSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		name.Name = "Houses by damage";
		name.OkCommand.Execute(null);
		await save;
		Assert.Equal(new[] { "Houses by damage" }, s.SavedViews);

		s.TypeFilter = 0;
		s.Columns.Single(c => c.Key == "Zip").IsVisible = false;
		s.ApplyViewCommand.Execute("Houses by damage");
		Assert.Equal(1, s.TypeFilter);
		Assert.Equal("Damaged", s.SortKey);
		Assert.True(s.IsColumnVisible("Zip"));
		Assert.Equal(2, s.Rows.Count);
	}

	[AvaloniaFact]
	public void BulkEdit_SetsAFieldOnTheChecked_AndCanBeUndone()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		s.Rows[0].IsChecked = true;
		s.Rows[1].IsChecked = true;
		_ = s.BulkEditCommand.ExecuteAsync(null);
		ui.Pump();
		BulkEditSheetViewModel sheet = Assert.IsType<BulkEditSheetViewModel>(ui.Main.Sheets.Current);
		Assert.DoesNotContain(sheet.Fields, f => f.Field.PerStructure);
		sheet.SelectedField = sheet.Fields.Single(f => f.Field.Member == nameof(AssessmentRecord.InspectorName));
		TextFieldViewModel input = Assert.IsType<TextFieldViewModel>(sheet.Input);
		input.Text = "Team B";
		Assert.Contains("Sets Inspector to Team B on 2 assessments", sheet.Summary);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("58-bulk-edit");

		sheet.ApplyCommand.Execute(null);
		Assert.StartsWith("Changed 2", sheet.ResultText);
		List<Guid> ids = s.Rows.Where(r => r.IsChecked).Select(r => r.AssessmentID!.Value).ToList();
		Assert.All(ids, id => Assert.Equal("Team B", ui.Store.LoadAssessment(id).Assessment.InspectorName));

		sheet.RevertCommand.Execute(null);
		Assert.All(ids, id => Assert.NotEqual("Team B", ui.Store.LoadAssessment(id).Assessment.InspectorName));
	}

	[AvaloniaFact]
	public void SearchAndReplace_PreviewsThenReplaces()
	{
		using UiSession ui = WithSamples();
		_ = ui.Main.Structures.SearchReplaceCommand.ExecuteAsync(null);
		ui.Pump();
		SearchReplaceSheetViewModel sheet = Assert.IsType<SearchReplaceSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Street Name", sheet.SelectedField.Label);
		sheet.Find = "creek";
		sheet.ReplaceWith = "Brook";
		Assert.Equal("Mill Brook", Assert.Single(sheet.Matches).After);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("59-search-replace");
		sheet.ReplaceAllCommand.Execute(null);
		Assert.Contains(ui.Store.GetProperties(), p => p.StreetName == "Mill Brook");
		Assert.Empty(sheet.Matches);
	}

	[AvaloniaFact]
	public async Task CopyFrom_FillsTheBuildingAndCost_ButNotTheDamage()
	{
		using UiSession ui = WithSamples();
		AssessmentFile ranch = ui.Store.LoadAllAssessments().First(a => a.Property.StreetNumber == "123");
		StructureRowViewModel target = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("700"));
		AssessmentFile empty = ui.Store.LoadAssessment(target.AssessmentID!.Value);
		Assert.Equal(2, empty.Property.StructureTypeID);

		Task copy = ui.Main.Structures.CopyFromCommand.ExecuteAsync(target);
		ui.Pump();
		CopyFromSheetViewModel sheet = Assert.IsType<CopyFromSheetViewModel>(ui.Main.Sheets.Current);
		sheet.Search = "123";
		sheet.Source = Assert.Single(sheet.Sources);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("60-copy-from");
		sheet.CopyCommand.Execute(null);
		await copy;

		AssessmentFile copied = ui.Store.LoadAssessment(target.AssessmentID!.Value);
		Assert.Equal(1, copied.Property.StructureTypeID);
		Assert.Equal(ranch.Property.FoundationTypeID, copied.Property.FoundationTypeID);
		Assert.Equal(ranch.Assessment.BaseCost, copied.Assessment.BaseCost);
		Assert.Equal("700", copied.Property.StreetNumber);                         // location is never copied
		Assert.All(copied.ElementPercentages, e => Assert.Equal(0m, e.UserDamage));   // nor damage
		Assert.Equal(32.50m, copied.CostAdjustments.Single(c => ui.Store.Reference.LookupName(c.CostAdjustmentLookupID) == "Garage").UnitCost);
		Assert.Contains(ui.Store.GetHistory(copied.Assessment.AssessmentID), h => h.Label == "Copied from 123 River Street");
	}

	[AvaloniaFact]
	public void Snippets_FromSettings_CanBeInsertedInTheEditor()
	{
		using UiSession ui = WithSamples();
		ui.Main.Settings.AddSnippetCommand.Execute(null);
		SnippetViewModel snippet = Assert.Single(ui.Main.Settings.Snippets);
		snippet.Name = "Mud line";
		snippet.Text = "Mud line 18 in. above floor.";
		ui.Main.ShowCommand.Execute(ViewModels.Page.Settings);
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		ui.Main.OpenAssessment(ui.Main.Structures.Rows[0].AssessmentID!.Value);
		TextFieldViewModel notes = ui.Main.Editor.Sections.SelectMany(x => x.AllFields).OfType<TextFieldViewModel>()
			.First(f => f.Member == nameof(PropertyRecord.StructureInformation));
		Assert.True(notes.HasSnippets);
		notes.Text = "Water in basement.";
		notes.InsertSnippetCommand.Execute(notes.Snippets[0]);
		Assert.Equal("Water in basement." + Environment.NewLine + "Mud line 18 in. above floor.", notes.Text);
	}

	[AvaloniaFact]
	public void RowMenu_MoveToRecentlyDeleted()
	{
		using UiSession ui = WithSamples();
		StructureRowViewModel row = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("700"));
		ui.Main.Structures.TrashCommand.Execute(row);
		ui.Pump();
		Assert.Equal(2, ui.Main.Structures.Rows.Count);
		Assert.Single(ui.Store.GetDeletedItems());
	}
}
