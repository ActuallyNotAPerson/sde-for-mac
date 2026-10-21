using Sde.Core.Assessments;
using Sde.Core.Photos;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports.Internal;
using SkiaSharp;

namespace Sde.Reports.Tests;

/// <summary>Photo markup (C10): stored as JSON, drawn on copies, never on the photo itself or FEMA's package.</summary>
public class PhotoMarkupTests
{
	private static string WhitePhoto(string folder, string name, int width = 800, int height = 600)
	{
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, name);
		using SKBitmap bitmap = new SKBitmap(width, height);
		using (SKCanvas canvas = new SKCanvas(bitmap))
		{
			canvas.Clear(SKColors.White);
		}
		using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
		File.WriteAllBytes(path, data.ToArray());
		return path;
	}

	private static SKColor Pixel(byte[] jpeg, double fx, double fy)
	{
		using SKBitmap b = SKBitmap.Decode(jpeg);
		return b.GetPixel((int)(fx * b.Width), (int)(fy * b.Height));
	}

	[Fact]
	public void Parse_KeepsKnownMarks_AndClampsPositions()
	{
		PhotoMarkup m = PhotoMarkup.Parse("""{"Shapes":[{"Kind":"Box","X1":-1,"Y1":0.2,"X2":1.5,"Y2":0.8},{"Kind":"Laser"},{"Kind":"Text","X1":0.1,"Y1":0.1,"Text":"Mud line"}]}""");
		Assert.Equal(2, m.Shapes.Count);
		Assert.Equal((0, 1), (m.Shapes[0].X1, m.Shapes[0].X2));
		Assert.Equal("#E53935", m.Shapes[0].Color);
		Assert.Null(PhotoMarkup.Parse("not json"));
		Assert.Null(PhotoMarkup.Parse(null));
		Assert.Equal(m.Shapes.Count, PhotoMarkup.Parse(m.ToJson()).Shapes.Count);
	}

	[Fact]
	public void TheMarksAreDrawnOnThePreparedCopy_AtTheirPlace()
	{
		string folder = Path.Combine(Path.GetTempPath(), "sde-markup-" + Guid.NewGuid().ToString("N"));
		try
		{
			string photo = WhitePhoto(folder, "wall.jpg");
			byte[] original = File.ReadAllBytes(photo);
			PhotoMarkup markup = new PhotoMarkup
			{
				Shapes =
				{
					new MarkupShape { Kind = MarkupKinds.Box, X1 = 0.25, Y1 = 0.25, X2 = 0.75, Y2 = 0.75, Color = "#E53935", Width = 0.01 },
					new MarkupShape { Kind = MarkupKinds.WaterLine, X1 = 0.05, Y1 = 0.9, X2 = 0.95, Y2 = 0.9, Text = "Water line 3 ft", Color = "#1565C0", Width = 0.01 },
				},
			};
			(byte[] jpeg, int w, int h) = PhotoPrep.Prepare(photo, markup).Value;
			Assert.Equal((800, 600), (w, h));
			SKColor edge = Pixel(jpeg, 0.25, 0.5);
			Assert.True(edge.Red > 180 && edge.Green < 120, "the box's left edge is red: " + edge);
			SKColor inside = Pixel(jpeg, 0.5, 0.5);
			Assert.True(inside.Red > 240 && inside.Green > 240, "inside the box stays white: " + inside);
			Assert.True(Pixel(jpeg, 0.5, 0.9).Blue > Pixel(jpeg, 0.5, 0.9).Red, "the water line is blue");
			// The photo file itself is never changed.
			Assert.Equal(original, File.ReadAllBytes(photo));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void TheSubmissionFolder_AddsMarkedUpCopies_AndKeepsFemasPackageOriginal()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string photo = WhitePhoto(t.Store.AttachmentsFolder, "front.jpg");
		f.Attachments.Add(new AttachmentRecord { FileName = "front.jpg", Active = true, DefaultImage = true, AttachmentType = "Photo" });
		t.Store.SaveAssessment(f);
		t.Store.SavePhotoMarkup("front.jpg", new PhotoMarkup { Shapes = { new MarkupShape { Kind = MarkupKinds.Arrow, X1 = 0.1, Y1 = 0.1, X2 = 0.5, Y2 = 0.5 } } }.ToJson());

		SubmissionResult result = SubmissionFolder.Write(t.Store, new[] { f.Assessment.AssessmentID }, Path.Combine(t.Folder, "out"),
			new SubmissionOptions { IncludeMarkedUpCopies = true });

		string copy = Path.Combine(result.Folder, SubmissionFolder.MarkedUpFolder, "front (marked up).jpg");
		Assert.True(File.Exists(copy));
		Assert.NotEqual(File.ReadAllBytes(photo), File.ReadAllBytes(copy));
		string packaged = Directory.GetFiles(result.Folder, "front.jpg", SearchOption.AllDirectories).Single();
		Assert.Equal(File.ReadAllBytes(photo), File.ReadAllBytes(packaged));
	}
}
