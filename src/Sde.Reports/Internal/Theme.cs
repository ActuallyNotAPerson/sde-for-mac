using MigraDoc.DocumentObjectModel;
using Sde.Reports.Fonts;

namespace Sde.Reports.Internal;

/// <summary>Colors, type sizes and paragraph styles shared by all reports.</summary>
internal static class Theme
{
	public static readonly Color Ink = new Color(0x1B, 0x22, 0x2C);
	public static readonly Color Muted = new Color(0x5D, 0x68, 0x78);
	public static readonly Color Rule = new Color(0xD8, 0xDD, 0xE4);
	public static readonly Color Faint = new Color(0xF3, 0xF5, 0xF8);
	public static readonly Color Accent = new Color(0x1C, 0x3A, 0x63);

	// Determination tones: text/edge color and a light fill.
	public static readonly Color SubstantialInk = new Color(0xA3, 0x1D, 0x1D);
	public static readonly Color SubstantialFill = new Color(0xFC, 0xEC, 0xEB);
	public static readonly Color NotSubstantialInk = new Color(0x1C, 0x6B, 0x35);
	public static readonly Color NotSubstantialFill = new Color(0xE9, 0xF5, 0xEC);
	public static readonly Color OtherInk = new Color(0x7A, 0x52, 0x00);
	public static readonly Color OtherFill = new Color(0xFD, 0xF3, 0xDC);

	public const double BodySize = 8;
	public const double LabelSize = 7;
	public const double TitleSize = 8.5;
	public const double NoteSize = 6.8;

	public const string LabelStyle = "SdeLabel";
	public const string ValueStyle = "SdeValue";
	public const string TitleStyle = "SdeTitle";
	public const string NoteStyle = "SdeNote";

	public static Color ToneInk(Determination d) => d switch
	{
		Determination.Substantial => SubstantialInk,
		Determination.NotSubstantial => NotSubstantialInk,
		_ => OtherInk,
	};

	public static Color ToneFill(Determination d) => d switch
	{
		Determination.Substantial => SubstantialFill,
		Determination.NotSubstantial => NotSubstantialFill,
		_ => OtherFill,
	};

	/// <summary>A new document with the report styles. Installs the embedded fonts first.</summary>
	public static Document NewDocument(string title, string subject)
	{
		ReportFonts.EnsureRegistered();
		Document doc = new Document();
		doc.Info.Title = title;
		doc.Info.Subject = subject;
		doc.Info.Author = "Substantial Damage Estimator";

		Style normal = doc.Styles[StyleNames.Normal];
		normal.Font.Name = ReportFonts.Family;
		normal.Font.Size = BodySize;
		normal.Font.Color = Ink;
		normal.ParagraphFormat.SpaceBefore = 0;
		normal.ParagraphFormat.SpaceAfter = 0;

		Style label = doc.Styles.AddStyle(LabelStyle, StyleNames.Normal);
		label.Font.Size = LabelSize;
		label.Font.Color = Muted;

		Style value = doc.Styles.AddStyle(ValueStyle, StyleNames.Normal);
		value.Font.Size = BodySize;
		value.Font.Color = Ink;

		Style heading = doc.Styles.AddStyle(TitleStyle, StyleNames.Normal);
		heading.Font.Size = TitleSize;
		heading.Font.Bold = true;
		heading.Font.Color = Accent;

		Style note = doc.Styles.AddStyle(NoteStyle, StyleNames.Normal);
		note.Font.Size = NoteSize;
		note.Font.Color = Muted;

		foreach (string s in new[] { StyleNames.Header, StyleNames.Footer })
		{
			doc.Styles[s].Font.Name = ReportFonts.Family;
			doc.Styles[s].Font.Size = 7.5;
			doc.Styles[s].Font.Color = Muted;
		}
		return doc;
	}
}
