using System.Collections.Concurrent;
using System.Reflection;
using PdfSharp.Fonts;

namespace Sde.Reports.Fonts;

/// <summary>
/// The report typeface. The original reports used whatever Windows font ReportViewer picked (Arial/Segoe);
/// PDFsharp's Core build on macOS has no system fonts at all, so the reports embed Public Sans
/// (the U.S. Web Design System typeface, SIL OFL 1.1, see Fonts/OFL.txt) and resolve every family to it.
/// </summary>
internal static class ReportFonts
{
	/// <summary>Body/heading family (regular, bold, italic, bold italic).</summary>
	public const string Family = "Public Sans";

	/// <summary>A separate family name for the SemiBold weight (MigraDoc only knows bold/not bold).</summary>
	public const string SemiBoldFamily = "Public Sans SemiBold";

	private static readonly object _lock = new();
	private static readonly ReportFontResolver _resolver = new();

	/// <summary>
	/// Installs the resolver once (thread-safe, idempotent). PDFsharp allows the global resolver to be set
	/// only once and before the first font is used: if the host app already installed its own resolver,
	/// ours goes in as the fallback resolver so "Public Sans" still resolves.
	/// </summary>
	public static void EnsureRegistered()
	{
		lock (_lock)
		{
			if (ReferenceEquals(GlobalFontSettings.FontResolver, _resolver) || ReferenceEquals(GlobalFontSettings.FallbackFontResolver, _resolver))
			{
				return;
			}
			if (GlobalFontSettings.FontResolver == null)
			{
				GlobalFontSettings.FontResolver = _resolver;
			}
			else if (GlobalFontSettings.FallbackFontResolver == null)
			{
				try
				{
					GlobalFontSettings.FallbackFontResolver = _resolver;
				}
				catch (InvalidOperationException)
				{
					// Fonts were already used with the host's resolver; nothing more we can do.
				}
			}
		}
	}

	/// <summary>Resolves any family to Public Sans; faces are read from embedded resources and cached.</summary>
	private sealed class ReportFontResolver : IFontResolver
	{
		private readonly ConcurrentDictionary<string, byte[]> _cache = new();

		public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
		{
			if (string.Equals(familyName, SemiBoldFamily, StringComparison.OrdinalIgnoreCase))
			{
				return new FontResolverInfo(bold ? "PublicSans-Bold" : "PublicSans-SemiBold");
			}
			string face = (bold, italic) switch
			{
				(true, true) => "PublicSans-BoldItalic",
				(true, false) => "PublicSans-Bold",
				(false, true) => "PublicSans-Italic",
				_ => "PublicSans-Regular",
			};
			return new FontResolverInfo(face);
		}

		public byte[] GetFont(string faceName) => _cache.GetOrAdd(faceName, Load);

		private static byte[] Load(string faceName)
		{
			string name = "Sde.Reports.Fonts." + faceName + ".ttf";
			using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
				?? throw new InvalidOperationException("Missing embedded font " + name);
			using MemoryStream m = new MemoryStream();
			s.CopyTo(m);
			return m.ToArray();
		}
	}
}
