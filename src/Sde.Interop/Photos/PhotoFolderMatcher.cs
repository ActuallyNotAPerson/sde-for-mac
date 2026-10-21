using System.Globalization;
using System.Text;

namespace Sde.Interop.Photos;

/// <summary>A structure a photo might belong to.</summary>
/// <param name="Id">The structure's PropertyID.</param>
/// <param name="Address">Street address as shown to the user, e.g. "123 Main St".</param>
/// <param name="Parcel">Parcel number, or null.</param>
/// <param name="RecordId">The community's own record or permit ID, or null.</param>
/// <param name="Latitude">Structure latitude, or null when unknown.</param>
/// <param name="Longitude">Structure longitude, or null when unknown.</param>
public sealed record PhotoMatchCandidate(Guid Id, string Address, string Parcel, string RecordId, double? Latitude, double? Longitude);

/// <summary>The structure chosen for a photo and why. <see cref="Candidate"/> is null when none could be chosen.</summary>
public sealed record PhotoMatch(string PhotoPath, PhotoMatchCandidate Candidate, string Reason, double? DistanceMeters);

/// <summary>Works out which structure each photo in a folder belongs to, from file names first and then from GPS.</summary>
public static class PhotoFolderMatcher
{
	private const double AmbiguousMeters = 10;

	/// <summary>
	/// Matches each photo, in the order given. A file name that contains a candidate's record ID wins, then its parcel
	/// number, then its house number and street name (ignoring case, spaces and punctuation). Otherwise the nearest
	/// candidate within <paramref name="toleranceMeters"/> of the photo's GPS position is used, unless two such
	/// candidates are within 10 m of each other, which is reported as ambiguous.
	/// </summary>
	public static IReadOnlyList<PhotoMatch> Match(IReadOnlyList<string> photoPaths, IReadOnlyList<PhotoMatchCandidate> candidates, double toleranceMeters = 40)
	{
		List<PhotoMatch> matches = new List<PhotoMatch>();
		foreach (string path in photoPaths)
		{
			matches.Add(MatchOne(path, candidates, toleranceMeters));
		}
		return matches;
	}

	private static PhotoMatch MatchOne(string path, IReadOnlyList<PhotoMatchCandidate> candidates, double toleranceMeters)
	{
		string name = Normalize(Path.GetFileNameWithoutExtension(path));

		PhotoMatchCandidate byName = FindByName(name, candidates, c => Normalize(c.RecordId), out _);
		if (byName != null)
		{
			return new PhotoMatch(path, byName, "file name has the record ID", null);
		}
		byName = FindByName(name, candidates, c => Normalize(c.Parcel), out _);
		if (byName != null)
		{
			return new PhotoMatch(path, byName, "file name has the parcel number", null);
		}
		byName = FindByName(name, candidates, AddressKey, out _);
		if (byName != null)
		{
			return new PhotoMatch(path, byName, "file name has the address", null);
		}

		PhotoInfo info;
		try
		{
			info = PhotoMetadata.Read(path);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			info = new PhotoInfo(null, null, null);
		}
		if (info.Latitude == null || info.Longitude == null)
		{
			return new PhotoMatch(path, null, "no match", null);
		}

		List<(PhotoMatchCandidate Candidate, double Distance)> near = new List<(PhotoMatchCandidate, double)>();
		foreach (PhotoMatchCandidate c in candidates)
		{
			if (c.Latitude == null || c.Longitude == null)
			{
				continue;
			}
			double d = DistanceMeters(info.Latitude.Value, info.Longitude.Value, c.Latitude.Value, c.Longitude.Value);
			if (d <= toleranceMeters)
			{
				near.Add((c, d));
			}
		}
		if (near.Count == 0)
		{
			return new PhotoMatch(path, null, "no match", null);
		}
		near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
		if (near.Count > 1)
		{
			double between = DistanceMeters(near[0].Candidate.Latitude.Value, near[0].Candidate.Longitude.Value,
				near[1].Candidate.Latitude.Value, near[1].Candidate.Longitude.Value);
			if (between <= AmbiguousMeters)
			{
				return new PhotoMatch(path, null, "ambiguous: two structures within 10 m", near[0].Distance);
			}
		}
		return new PhotoMatch(path, near[0].Candidate, string.Format(CultureInfo.InvariantCulture, "GPS, {0:0} m from the structure", near[0].Distance), near[0].Distance);
	}

	/// <summary>The one candidate whose key is in the file name (the longest key wins; a tie at the longest length matches nothing).</summary>
	private static PhotoMatchCandidate FindByName(string name, IReadOnlyList<PhotoMatchCandidate> candidates, Func<PhotoMatchCandidate, string> key, out bool tie)
	{
		tie = false;
		PhotoMatchCandidate best = null;
		int bestLength = 0;
		foreach (PhotoMatchCandidate c in candidates)
		{
			string k = key(c);
			if (k.Length == 0 || !name.Contains(k, StringComparison.Ordinal))
			{
				continue;
			}
			if (k.Length > bestLength)
			{
				best = c;
				bestLength = k.Length;
				tie = false;
			}
			else if (k.Length == bestLength && !ReferenceEquals(best, c))
			{
				tie = true;
			}
		}
		return tie ? null : best;
	}

	/// <summary>House number plus the first word of the street name, e.g. "123 N Main St" gives "123n"; "123 Main St" gives "123main".</summary>
	private static string AddressKey(PhotoMatchCandidate c)
	{
		if (string.IsNullOrWhiteSpace(c.Address))
		{
			return "";
		}
		string[] words = c.Address.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
		if (words.Length < 2 || !char.IsDigit(words[0][0]))
		{
			return "";
		}
		return Normalize(words[0] + words[1]);
	}

	/// <summary>Lower case letters and digits only.</summary>
	private static string Normalize(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		StringBuilder sb = new StringBuilder();
		foreach (char ch in text)
		{
			if (char.IsLetterOrDigit(ch))
			{
				sb.Append(char.ToLowerInvariant(ch));
			}
		}
		return sb.ToString();
	}

	/// <summary>Great-circle distance in meters (haversine).</summary>
	internal static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
	{
		const double EarthRadius = 6371008.8;
		double dLat = (lat2 - lat1) * Math.PI / 180;
		double dLon = (lon2 - lon1) * Math.PI / 180;
		double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
			+ Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
		return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
	}
}
