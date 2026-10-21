using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Sde.Interop.Photos;

namespace Sde.App.ViewModels;

/// <summary>Match a photo folder to structures (a folder of field photos, by file name or GPS).</summary>
public sealed partial class ImportExportViewModel
{
	/// <summary>How far (in metres) a photo's GPS position may be from a structure to match it.</summary>
	public const double PhotoToleranceMeters = 40;

	/// <summary>
	/// Picks a folder of photos and works out which structure each belongs to: a file name containing a structure's
	/// record ID, parcel number or address, otherwise the nearest structure to the photo's GPS position. Shows the
	/// matches for review; nothing is attached until then.
	/// </summary>
	[RelayCommand]
	private async Task MatchPhotoFolder()
	{
		string folder = await _app.Dialogs.PickFolderAsync("Choose a folder of photos");
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return;
		}
		List<string> photos = Directory.EnumerateFiles(folder)
			.Where(p => AssessmentEditorViewModel.PhotoPatterns.Any(pat => p.EndsWith(pat[1..], StringComparison.OrdinalIgnoreCase)))
			.Where(p => !Path.GetFileName(p).StartsWith('.'))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (photos.Count == 0)
		{
			_app.Dialogs.Notify("No photos found", "That folder has no photos (JPEG, PNG, HEIC, GIF, BMP or TIFF) in it.");
			return;
		}

		(List<PhotoMatchCandidate> candidates, Dictionary<Guid, Guid> latest) = PhotoCandidates(_app.Store);
		if (candidates.Count == 0)
		{
			_app.Dialogs.Notify("No structures to match", "Photos are attached to assessments. Add the structures (and their assessments) first.");
			return;
		}
		JobOutcome<IReadOnlyList<PhotoMatch>> outcome = await _app.Jobs.RunAsync("Matching photos to structures",
			(progress, ct) => PhotoFolderMatcher.Match(photos, candidates, PhotoToleranceMeters), "Couldn't match the photos");
		if (!outcome.Completed)
		{
			return;
		}
		await _app.Sheets.ShowAsync(new PhotoFolderSheetViewModel(_app, folder, outcome.Value, latest));
	}

	/// <summary>
	/// Every active structure that has an assessment, with what a photo can be matched on (address, parcel, the record ID
	/// it was imported from, coordinates), and each structure's latest assessment, which matched photos are attached to.
	/// </summary>
	public static (List<PhotoMatchCandidate> Candidates, Dictionary<Guid, Guid> LatestAssessment) PhotoCandidates(SdeStore store)
	{
		Dictionary<Guid, StructureSummary> summaries = store.ListStructures()
			.GroupBy(s => s.PropertyID)
			.ToDictionary(g => g.Key, g => g.FirstOrDefault(s => !string.IsNullOrEmpty(s.ExternalId)) ?? g.First());
		List<PhotoMatchCandidate> candidates = new List<PhotoMatchCandidate>();
		Dictionary<Guid, Guid> latest = new Dictionary<Guid, Guid>();
		foreach (MapPointRow p in store.ListMapPoints().Where(p => p.AssessmentID != null))
		{
			summaries.TryGetValue(p.PropertyID, out StructureSummary s);
			candidates.Add(new PhotoMatchCandidate(p.PropertyID, p.Address, s?.ParcelNumber, s?.ExternalId,
				p.Latitude is decimal lat ? (double)lat : null, p.Longitude is decimal lon ? (double)lon : null));
			latest[p.PropertyID] = p.AssessmentID.Value;
		}
		return (candidates, latest);
	}
}
