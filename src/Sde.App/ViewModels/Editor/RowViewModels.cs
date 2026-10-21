using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.Core.Records;

namespace Sde.App.ViewModels.Editor;

/// <summary>One building element on the Damage step: its share of the replacement cost and the inspector's % damaged.</summary>
public sealed partial class ElementRowViewModel : ObservableObject
{
	private readonly Action _edited;

	public ElementRowViewModel(ElementPercentageRecord row, Action edited)
	{
		Row = row;
		_edited = edited;
		_userDamage = row.UserDamage;
	}

	/// <summary>The value a depth table suggested and nobody has accepted yet, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSuggested), nameof(SuggestedLabel), nameof(AcceptLabel))]
	public partial decimal? SuggestedPercent { get; set; }

	/// <summary>Shows the "suggested" badge: the value is still the depth table's, not yet the inspector's own.</summary>
	public bool IsSuggested => SuggestedPercent is decimal s && s == _userDamage;

	public string SuggestedLabel => $"Suggested from the depth table: {Name} {SuggestedPercent:0.##}%. Accept it, or change the value.";

	public string AcceptLabel => $"Accept the suggested {Name} damage";

	/// <summary>Called by Accept (set by the editor).</summary>
	public Action<ElementRowViewModel> Accept { get; set; }

	[RelayCommand]
	private void AcceptSuggestion() => Accept?.Invoke(this);

	public ElementPercentageRecord Row { get; }

	public string Name => Row.ElementName;

	/// <summary>% of the replacement cost this element represents (FEMA's tables).</summary>
	public decimal? Share => Row.ElementPercentage;

	public decimal ItemCost => Parse(Row.ItemCost);

	public decimal DamageValue => Parse(Row.DamageValues);

	private decimal _userDamage;

	/// <summary>% damaged, 0–100 (the slider and the number box).</summary>
	public decimal UserDamage
	{
		get => _userDamage;
		set
		{
			decimal v = Math.Clamp(Math.Round(value, 2), 0m, 100m);
			if (SetProperty(ref _userDamage, v))
			{
				Row.UserDamage = v;
				OnPropertyChanged(nameof(UserDamageDouble));
				OnPropertyChanged(nameof(IsSuggested));
				_edited();
			}
		}
	}

	/// <summary>Slider binding (Slider.Value is double).</summary>
	public double UserDamageDouble
	{
		get => (double)_userDamage;
		set => UserDamage = Math.Round((decimal)value);
	}

	[RelayCommand]
	private void SetDamage(string percent) => UserDamage = decimal.Parse(percent, CultureInfo.InvariantCulture);

	public void RefreshComputed()
	{
		OnPropertyChanged(nameof(Share));
		OnPropertyChanged(nameof(ItemCost));
		OnPropertyChanged(nameof(DamageValue));
		OnPropertyChanged(nameof(Name));
	}

	private static decimal Parse(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : 0m;
}

/// <summary>A standard cost adjustment line (garage, porch, …) — quantity × unit cost.</summary>
public sealed partial class CostAdjustmentRowViewModel : ObservableObject
{
	private readonly Action _edited;

	public CostAdjustmentRowViewModel(CostAdjustmentResultRecord row, string name, Action edited)
	{
		Row = row;
		Name = name;
		_edited = edited;
	}

	public CostAdjustmentResultRecord Row { get; }

	public string Name { get; }

	public string Units => Row.Units;

	public decimal? Quantity
	{
		get => Row.Quantity;
		set
		{
			if (Row.Quantity != value)
			{
				Row.Quantity = value;
				OnPropertyChanged();
				_edited();
			}
		}
	}

	public decimal? UnitCost
	{
		get => Row.UnitCost;
		set
		{
			if (Row.UnitCost != value)
			{
				Row.UnitCost = value;
				OnPropertyChanged();
				_edited();
			}
		}
	}

	public decimal? TotalCost => Row.TotalCost;

	public void RefreshComputed() => OnPropertyChanged(nameof(TotalCost));
}

/// <summary>One of the 7 free-form "additional adjustment" lines.</summary>
public sealed partial class AdditionalAdjustmentRowViewModel : ObservableObject
{
	private readonly Action _edited;

	public AdditionalAdjustmentRowViewModel(AdditionalAdjustmentRecord row, Action edited)
	{
		Row = row;
		_edited = edited;
	}

	public AdditionalAdjustmentRecord Row { get; }

	public int Number => Row.zOrder ?? 0;

	public string Name
	{
		get => Row.AdjustmentName;
		set
		{
			if (Row.AdjustmentName != value)
			{
				Row.AdjustmentName = string.IsNullOrWhiteSpace(value) ? null : value;
				OnPropertyChanged();
				_edited();
			}
		}
	}

	public decimal? Quantity
	{
		get => Row.Quantity;
		set
		{
			if (Row.Quantity != value)
			{
				Row.Quantity = value;
				OnPropertyChanged();
				_edited();
			}
		}
	}

	public decimal? UnitCost
	{
		get => Row.UnitCost;
		set
		{
			if (Row.UnitCost != value)
			{
				Row.UnitCost = value;
				OnPropertyChanged();
				_edited();
			}
		}
	}

	public decimal? TotalCost => Row.TotalCost;

	public void RefreshComputed() => OnPropertyChanged(nameof(TotalCost));
}

/// <summary>
/// One area of the building for the square-footage calculator. FEMA's L/T/U shapes are just 2–3 rectangles,
/// so every shape here is up to three rectangles (A, B, C) of length × width × stories, or a total typed directly ('c').
/// </summary>
public sealed partial class ShapeViewModel : ObservableObject
{
	private readonly Action _edited;
	private readonly Action<ShapeViewModel> _remove;

	public ShapeViewModel(ShapeRecord shape, Action edited, Action<ShapeViewModel> remove)
	{
		Shape = shape;
		_edited = edited;
		_remove = remove;
	}

	[RelayCommand]
	private void Remove() => _remove(this);

	public ShapeRecord Shape { get; }

	public bool IsTotalOnly => Shape.ShapeType == "c";

	public bool HasB => Shape.ShapeType is "l" or "t" or "u" || Shape.LengthB > 0;

	public bool HasC => Shape.ShapeType == "u" || Shape.LengthC > 0;

	public string Kind => Shape.ShapeType switch
	{
		"c" => "Total area",
		"l" => "L-shape",
		"t" => "T-shape",
		"u" => "U-shape",
		_ => "Rectangle",
	};

	public string Description { get => Shape.DescriptionA; set => Set(() => Shape.DescriptionA = value); }

	public decimal? LengthA { get => Shape.LengthA; set => Set(() => Shape.LengthA = value); }
	public decimal? WidthA { get => Shape.WidthA; set => Set(() => Shape.WidthA = value); }
	public decimal? StoriesA { get => Shape.StoriesA; set => Set(() => Shape.StoriesA = (int?)value); }
	public decimal? LengthB { get => Shape.LengthB; set => Set(() => Shape.LengthB = value); }
	public decimal? WidthB { get => Shape.WidthB; set => Set(() => Shape.WidthB = value); }
	public decimal? StoriesB { get => Shape.StoriesB; set => Set(() => Shape.StoriesB = (int?)value); }
	public decimal? LengthC { get => Shape.LengthC; set => Set(() => Shape.LengthC = value); }
	public decimal? WidthC { get => Shape.WidthC; set => Set(() => Shape.WidthC = value); }
	public decimal? StoriesC { get => Shape.StoriesC; set => Set(() => Shape.StoriesC = (int?)value); }

	/// <summary>For 'c' shapes, the total is typed in.</summary>
	public decimal? Total
	{
		get => Shape.TotalSqFt;
		set
		{
			if (IsTotalOnly)
			{
				Set(() => Shape.TotalSqFt = value);
			}
		}
	}

	private void Set(Action apply)
	{
		apply();
		if (!IsTotalOnly)
		{
			Shape.TotalSqFt = Area(Shape.LengthA, Shape.WidthA, Shape.StoriesA) + Area(Shape.LengthB, Shape.WidthB, Shape.StoriesB) + Area(Shape.LengthC, Shape.WidthC, Shape.StoriesC);
		}
		OnPropertyChanged(string.Empty);
		_edited();
	}

	/// <summary>Length × width × stories (stories default to 1), rounded to whole square feet like FEMA's Numeric(18,0) column.</summary>
	private static decimal Area(decimal? length, decimal? width, int? stories) =>
		length is > 0 && width is > 0 ? Math.Round(length.Value * width.Value * Math.Max(stories ?? 1, 1), 0, MidpointRounding.AwayFromZero) : 0m;
}

/// <summary>A photo attached to the assessment.</summary>
public sealed partial class PhotoViewModel : ObservableObject
{
	private readonly Action _edited;
	private readonly Action<PhotoViewModel> _makeDefault;
	private readonly Action<PhotoViewModel> _remove;
	private readonly Action<PhotoViewModel> _open;

	public PhotoViewModel(AttachmentRecord attachment, string path, Action edited, Action<PhotoViewModel> makeDefault, Action<PhotoViewModel> remove, Action<PhotoViewModel> open)
	{
		_open = open;
		Attachment = attachment;
		Path = path;
		_edited = edited;
		_makeDefault = makeDefault;
		_remove = remove;
		ThumbnailLoaded = LoadThumbnailAsync(path);
	}

	/// <summary>Decodes a small preview off the UI thread, so opening an assessment with many photos is instant.</summary>
	private async Task LoadThumbnailAsync(string path)
	{
		Bitmap thumbnail = await Task.Run(() =>
		{
			try
			{
				if (!File.Exists(path))
				{
					return null;
				}
				using FileStream s = File.OpenRead(path);
				return Bitmap.DecodeToWidth(s, 360);
			}
			catch (Exception)
			{
				return null; // not an image the decoder understands; still listed
			}
		});
		Thumbnail = thumbnail;
	}

	public AttachmentRecord Attachment { get; }

	public string Path { get; }

	/// <summary>Completes when the preview has been decoded (or found unreadable).</summary>
	public Task ThumbnailLoaded { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasThumbnail))]
	public partial Bitmap Thumbnail { get; private set; }

	public bool HasThumbnail => Thumbnail != null;

	public string FileName => System.IO.Path.GetFileName(Path);

	public bool IsDefault => Attachment.DefaultImage;

	/// <summary>The star's label: what VoiceOver reads and the tooltip shows.</summary>
	public string DefaultLabel => IsDefault ? "Report photo (shown on reports)" : "Use this photo on reports";

	public string Description
	{
		get => Attachment.Description;
		set
		{
			if (Attachment.Description != value)
			{
				Attachment.Description = value;
				OnPropertyChanged();
				_edited();
			}
		}
	}

	public void RefreshDefault()
	{
		OnPropertyChanged(nameof(IsDefault));
		OnPropertyChanged(nameof(DefaultLabel));
	}

	/// <summary>Moves the photo by a number of places (set by the editor; the order is the report order).</summary>
	public Action<PhotoViewModel, int> Mover { get; init; }

	/// <summary>1-based place in the list (the order reports print the photos in).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(MoveEarlierLabel), nameof(MoveLaterLabel), nameof(PositionText))]
	public partial int Position { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(MoveEarlierCommand))]
	public partial bool CanMoveEarlier { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(MoveLaterCommand))]
	public partial bool CanMoveLater { get; set; }

	public string PositionText => Position.ToString(CultureInfo.InvariantCulture);

	public string MoveEarlierLabel => $"Move photo {Position} earlier";

	public string MoveLaterLabel => $"Move photo {Position} later";

	[RelayCommand(CanExecute = nameof(CanMoveEarlier))]
	private void MoveEarlier() => Mover?.Invoke(this, -1);

	[RelayCommand(CanExecute = nameof(CanMoveLater))]
	private void MoveLater() => Mover?.Invoke(this, 1);

	[RelayCommand]
	private void MakeDefault() => _makeDefault(this);

	[RelayCommand]
	private void Remove() => _remove(this);

	[RelayCommand]
	private void Open() => _open(this);
}
