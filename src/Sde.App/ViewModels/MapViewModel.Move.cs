using System.Globalization;
using Sde.App.Maps;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Records;
using Sde.Data;
using Sde.Gis.Analysis;

namespace Sde.App.ViewModels;

/// <summary>Moving a structure on the map: drag its dot in Set location, confirm, and the new place is saved to its history.</summary>
public sealed partial class MapViewModel
{
	private static readonly string[] CompassPoints = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

	/// <summary>The question about a dragged dot was answered (or not asked): the map stops drawing the dot's new place.</summary>
	public event Action MoveAnswered;

	/// <summary>
	/// Why a structure can't be given a new place, or null when it can: its assessment is Final or Letter sent (which
	/// locks it), or checked out to another Mac in a work package.
	/// </summary>
	public string WhyCantMove(MapPointViewModel structure)
	{
		if (structure?.AssessmentID is not Guid assessment)
		{
			return null;
		}
		try
		{
			AssessmentStatusRecord status = _app.Store.GetStatus(assessment);
			if (status.IsLocked)
			{
				return $"{structure.Address} can't be moved: its assessment is {AssessmentStatuses.Label(status.Value)}, which locks it. Reopen the assessment to change its location.";
			}
			CheckOutRecord checkOut = _app.Store.GetCheckOut(assessment);
			if (checkOut != null)
			{
				string to = string.IsNullOrWhiteSpace(checkOut.CheckedOutTo) ? "another Mac" : checkOut.CheckedOutTo;
				return $"{structure.Address} can't be moved: its assessment is checked out to {to} in a work package. Check the package back in, or take the assessment back, first.";
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Services.AppLog.Write("Couldn't check whether a structure can be moved", ex);
		}
		return null;
	}

	/// <summary>
	/// A structure's dot was dragged to a new place (Set location): asks "Move 12 River Street about 40 ft north-east?",
	/// then saves the new latitude and longitude through the same path as every change made outside the editor.
	/// </summary>
	public async Task DragStructureAsync(Guid propertyId, LatLon to)
	{
		MapPointViewModel structure = _all.FirstOrDefault(p => p.Id == propertyId);
		if (structure == null || !structure.HasLocation)
		{
			MoveAnswered?.Invoke();
			return;
		}
		if (WhyCantMove(structure) is string reason)
		{
			MoveAnswered?.Invoke();
			Message = reason;
			return;
		}
		double metres = Measure.Distance(structure.Longitude, structure.Latitude, to.Longitude, to.Latitude);
		MoveStructureSheetViewModel sheet = new MoveStructureSheetViewModel(structure.Address, DistanceInWords(metres), Direction(structure.Latitude, structure.Longitude, to),
			new LatLon(structure.Latitude, structure.Longitude), to);
		bool move = await _app.Sheets.ShowAsync(sheet) is true;
		MoveAnswered?.Invoke();
		if (!move)
		{
			Message = $"{structure.Address} stays where it was.";
			return;
		}
		if (SaveLocation(structure, to.Latitude, to.Longitude) is (decimal lat, decimal lon))
		{
			Message = $"Moved {structure.Address} {sheet.Distance} {sheet.Direction}, to {lat:0.000000}, {lon:0.000000}.";
		}
	}

	/// <summary>Saves a structure's latitude and longitude (rounded to 6 places) as one change in its history; the rounded values, or null when it couldn't be saved.</summary>
	private (decimal Latitude, decimal Longitude)? SaveLocation(MapPointViewModel structure, double latitude, double longitude)
	{
		decimal lat = Math.Round((decimal)latitude, 6);
		decimal lon = Math.Round((decimal)longitude, 6);
		try
		{
			PropertyRecord p = _app.Store.GetProperty(structure.Id);
			if (p == null)
			{
				Message = "That structure no longer exists.";
				return null;
			}
			p.Latitude = lat;
			p.Longitude = lon;
			// Matched by structure: an editor showing any assessment of it (not only the newest) catches up.
			_app.Writes.WriteForStructures(new[] { p.PropertyID }, () => _app.Store.SaveProperty(p, new SaveContext { Via = SaveVia.Map }));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't save the location", ex);
			return null;
		}
		return (lat, lon);
	}

	/// <summary>"about 40 ft", "about 0.3 mi".</summary>
	public static string DistanceInWords(double metres)
	{
		double feet = Measure.MetresToFeet(metres);
		if (feet < 1)
		{
			return "less than a foot";
		}
		if (feet < 1000)
		{
			double rounded = feet < 100 ? Math.Round(feet) : Math.Round(feet / 10) * 10;
			return "about " + rounded.ToString("N0", US) + (rounded == 1 ? " foot" : " ft");
		}
		return "about " + (feet / 5280).ToString("N1", US) + " mi";
	}

	/// <summary>The compass direction from one place to another ("north-east"), from the initial bearing.</summary>
	public static string Direction(double latitude, double longitude, LatLon to)
	{
		double phi1 = latitude * Math.PI / 180;
		double phi2 = to.Latitude * Math.PI / 180;
		double dLambda = (to.Longitude - longitude) * Math.PI / 180;
		double y = Math.Sin(dLambda) * Math.Cos(phi2);
		double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
		double bearing = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
		return CompassPoints[(int)Math.Round(bearing / 45) % 8];
	}
}
