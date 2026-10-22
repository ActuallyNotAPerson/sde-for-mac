using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Photos;
using Sde.Data;
using Sde.Reports;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A colour to draw marks with.</summary>
public sealed record MarkupColour(string Name, string Hex)
{
	public IBrush Brush { get; } = SolidColorBrush.Parse(Hex);
}

/// <summary>
/// Marks up one photo (C10): a water line, arrows, boxes, ellipses and text, drawn by dragging across the picture. The
/// photo file never changes: the marks are kept beside it and drawn on the thumbnail, the reports and the submission
/// folder's marked-up copies, while FEMA's export keeps the original. Read-only for a Final or Letter sent assessment.
/// Closes with the saved <see cref="PhotoMarkup"/> (empty when every mark was cleared), or null when cancelled.
/// </summary>
public sealed partial class PhotoMarkupSheetViewModel : SheetViewModel
{
	/// <summary>The long edge of the picture the sheet draws on, in pixels.</summary>
	public const int PictureEdge = 1600;

	/// <summary>The smallest mark, as a fraction of the picture: a shorter drag is taken as a slip.</summary>
	public const double MinimumSize = 0.01;

	public static readonly IReadOnlyList<string> ToolNames = new[] { "Water line", "Arrow", "Box", "Ellipse", "Text" };

	public static readonly IReadOnlyList<MarkupColour> Colours = new[]
	{
		new MarkupColour("Red", "#E53935"),
		new MarkupColour("Blue", "#1565C0"),
		new MarkupColour("Yellow", "#FDD835"),
		new MarkupColour("White", "#FFFFFF"),
		new MarkupColour("Black", "#212121"),
	};

	private readonly AppServices _app;
	private readonly string _fileName;
	private readonly byte[] _picture;
	private readonly Stack<List<MarkupShape>> _undo = new();
	private readonly PhotoMarkup _markup;

	public PhotoMarkupSheetViewModel(AppServices app, string photoPath, bool readOnly) : base("Mark up photo")
	{
		_app = app;
		_fileName = Path.GetFileName(photoPath);
		IsReadOnly = readOnly;
		_markup = PhotoMarkup.Parse(app.Store.GetPhotoMarkup(_fileName)) ?? new PhotoMarkup();
		if (PhotoMarkupRenderer.Render(photoPath, null, PictureEdge) is (byte[] jpeg, _, _))
		{
			_picture = jpeg;
		}
		Colour = Colours.First(c => c.Hex == PhotoMarkup.DefaultColor(MarkupKinds.WaterLine));
		Redraw();
	}

	public override SheetSize Size => SheetSize.Large;

	/// <summary>True for a Final or Letter sent assessment: its reports must not change, so the marks can only be looked at.</summary>
	public bool IsReadOnly { get; }

	public bool IsEditable => !IsReadOnly && HasPicture;

	public bool HasPicture => _picture != null;

	public string FileName => _fileName;

	/// <summary>The photo with its marks drawn on, as the reports print it.</summary>
	[ObservableProperty]
	public partial Bitmap Picture { get; private set; }

	/// <summary>The chosen tool, as an index into <see cref="ToolNames"/> and <see cref="MarkupKinds.All"/>.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Tool), nameof(Instruction), nameof(UsesLabel), nameof(LabelPlaceholder))]
	public partial int ToolIndex { get; set; }

	/// <summary>See <see cref="MarkupKinds"/>.</summary>
	public string Tool => MarkupKinds.All[Math.Clamp(ToolIndex, 0, MarkupKinds.All.Count - 1)];

	[ObservableProperty]
	public partial MarkupColour Colour { get; set; }

	/// <summary>A water line's label, or a text mark's words.</summary>
	[ObservableProperty]
	public partial string Label { get; set; }

	public bool UsesLabel => Tool is MarkupKinds.WaterLine or MarkupKinds.Text;

	public string LabelPlaceholder => Tool == MarkupKinds.Text ? "The words to put on the photo" : "Label (optional), for example Water 2 ft 6 in";

	public string Instruction
	{
		get
		{
			if (!HasPicture)
			{
				return "SDE can't read this photo, so it can't be marked up.";
			}
			if (IsReadOnly)
			{
				return "This assessment is locked, so its photo marks can't change. Reopen it to make changes.";
			}
			return Tool switch
			{
				MarkupKinds.WaterLine => "Drag across the photo where the water reached.",
				MarkupKinds.Arrow => "Drag from the tail of the arrow to its point.",
				MarkupKinds.Text => "Type the words, then click where they go.",
				_ => "Drag from one corner to the opposite corner.",
			};
		}
	}

	public string CountText => _markup.Shapes.Count switch
	{
		0 => "No marks yet",
		1 => "1 mark",
		int n => $"{n} marks",
	};

	public bool CanUndo => _undo.Count > 0;

	public bool HasMarks => !_markup.IsEmpty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	/// <summary>The marks as they stand (for tests and the canvas).</summary>
	public IReadOnlyList<MarkupShape> Shapes => _markup.Shapes;

	partial void OnToolIndexChanged(int oldValue, int newValue)
	{
		// A colour the user picked stays; the previous tool's own colour follows the tool.
		string previous = MarkupKinds.All[Math.Clamp(oldValue, 0, MarkupKinds.All.Count - 1)];
		if (Colour == null || Colour.Hex == PhotoMarkup.DefaultColor(previous))
		{
			Colour = Colours.FirstOrDefault(c => c.Hex == PhotoMarkup.DefaultColor(Tool)) ?? Colours[0];
		}
		Error = null;
	}

	partial void OnLabelChanged(string value) => Error = null;

	/// <summary>
	/// Adds a mark with the chosen tool from one point to another (fractions of the picture, 0 to 1). A text mark goes at
	/// the first point. Returns false when nothing was added: a drag too short to mean anything, or text with no words.
	/// </summary>
	public bool AddShape(double x1, double y1, double x2, double y2)
	{
		if (!IsEditable)
		{
			return false;
		}
		x1 = Clamp(x1);
		y1 = Clamp(y1);
		x2 = Clamp(x2);
		y2 = Clamp(y2);
		string words = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();
		if (Tool == MarkupKinds.Text)
		{
			if (words == null)
			{
				Error = "Type the words first, then click where they go.";
				return false;
			}
			x2 = x1;
			y2 = y1;
		}
		else if (Tool is MarkupKinds.Box or MarkupKinds.Ellipse
			? Math.Abs(x2 - x1) < MinimumSize || Math.Abs(y2 - y1) < MinimumSize
			: Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) < MinimumSize)
		{
			return false;
		}
		if (_markup.Shapes.Count >= PhotoMarkup.MaxShapes)
		{
			Error = $"A photo can have up to {PhotoMarkup.MaxShapes} marks.";
			return false;
		}
		Remember();
		_markup.Shapes.Add(new MarkupShape
		{
			Kind = Tool,
			X1 = x1,
			Y1 = y1,
			X2 = x2,
			Y2 = y2,
			Text = UsesLabel ? words : null,
			Color = (Colour ?? Colours[0]).Hex,
		});
		Changed();
		return true;
	}

	[RelayCommand]
	private void Undo()
	{
		if (_undo.Count == 0)
		{
			return;
		}
		_markup.Shapes = _undo.Pop();
		Changed();
	}

	/// <summary>Takes every mark off (Undo brings them back).</summary>
	[RelayCommand]
	private void ClearAll()
	{
		if (_markup.IsEmpty || !IsEditable)
		{
			return;
		}
		Remember();
		_markup.Shapes = new List<MarkupShape>();
		Changed();
	}

	[RelayCommand]
	private void Save()
	{
		if (!IsEditable)
		{
			Close(null);
			return;
		}
		try
		{
			_app.Store.SavePhotoMarkup(_fileName, _markup.IsEmpty ? null : _markup.ToJson());
			Close(_markup);
		}
		catch (AssessmentLockedException ex)
		{
			Error = ex.Message;
		}
	}

	private void Remember() => _undo.Push(new List<MarkupShape>(_markup.Shapes));

	private void Changed()
	{
		Error = null;
		Redraw();
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(CanUndo));
		OnPropertyChanged(nameof(HasMarks));
	}

	private void Redraw()
	{
		byte[] drawn = PhotoMarkupRenderer.DrawOnto(_picture, _markup);
		Picture = drawn == null ? null : new Bitmap(new MemoryStream(drawn));
	}

	private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
}
