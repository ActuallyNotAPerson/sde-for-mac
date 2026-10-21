using Avalonia.Headless.XUnit;
using Sde.App.ViewModels;

namespace Sde.App.Tests;

/// <summary>Export ▾ → Submission folder on the Structures page.</summary>
public class SubmissionUiTests
{
	[AvaloniaFact]
	public async Task SubmissionFolder_FromTheStructuresList()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		SourceImportViewModel acc = ui.Main.SourceImport;
		await SourceImportUiTests.SetUpWideExport(acc);
		await acc.ImportCommand.ExecuteAsync(null);
		acc.ReviewCommand.Execute(null);
		StructuresViewModel s = ui.Main.Structures;
		s.ClearOnlyCommand.Execute(null);

		// The whole list: the empty sample office is incomplete and left out unless included.
		s.OpenSubmissionCommand.Execute(null);
		SubmissionViewModel sub = s.Submission;
		Assert.True(sub.IsOpen);
		Assert.Equal("6 assessments: 5 ready, 1 incomplete (left out)", sub.Count);
		sub.IncludeIncomplete = true;
		Assert.Equal("6 assessments: 5 ready, 1 incomplete (included)", sub.Count);
		sub.IncludeIncomplete = false;
		ui.Snapshot("39-structures-submission");

		string parent = Path.Combine(ui.Folder, "out");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		await sub.WriteCommand.ExecuteAsync(null);
		string folder = Assert.Single(Directory.GetDirectories(parent));
		Assert.StartsWith("SDE submission ", Path.GetFileName(folder));
		Assert.Contains("reveal:" + folder, ui.Dialogs.Opened);
		string note = Assert.Single(ui.Dialogs.Notifications, n => n.StartsWith("Submission folder written"));
		Assert.Contains("5 reports", note);
		Assert.Contains("Left out because they're incomplete: 700 Riverside", note);
		Assert.False(sub.IsOpen);
		string[] reports = Directory.GetFiles(Path.Combine(folder, "Reports")).Select(Path.GetFileName).ToArray();
		Assert.Equal(5, reports.Length);
		Assert.Contains("BLD-2026-00123 – 123 River Street.pdf", reports);   // named after the record ID
		Assert.Contains("123 River Street.pdf", reports);                     // the sample ranch, not imported
		Assert.True(File.Exists(Path.Combine(folder, "SDE_1", "Export.json")));
		Assert.Equal(1 + 5, File.ReadAllLines(Path.Combine(folder, "results.csv")).Length);

		// Only the checked structures.
		s.Rows.First(r => r.Address.StartsWith("45 Elm")).IsChecked = true;
		s.OpenSubmissionCommand.Execute(null);
		Assert.Equal("1 assessment", sub.Count);
		sub.CancelCommand.Execute(null);
		Assert.False(sub.IsOpen);

		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}
}
