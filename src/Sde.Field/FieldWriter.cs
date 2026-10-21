using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Field;

/// <summary>
/// How the phone form saves. The Mac app implements it with its write coordinator, so a change from a phone reaches an
/// assessment open in the editor; <see cref="DirectFieldWriter"/> saves straight to the store (tests, command line).
/// </summary>
public interface IFieldWriter
{
	/// <summary>Loads an assessment, changes it, and saves it (recalculated, with history and lock checks).</summary>
	Task<AssessmentFile> ApplyAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context);

	/// <summary>Saves a new assessment (and its structure).</summary>
	Task<AssessmentFile> AddAsync(AssessmentFile file, SaveContext context);
}

/// <summary>Turns a photo into one SDE can use (HEIC to JPEG on a Mac).</summary>
public interface IPhotoConverter
{
	/// <summary>
	/// The file to store: <paramref name="path"/> itself, or a converted copy in <paramref name="tempFolder"/>. Throws
	/// <see cref="IOException"/> when a conversion fails.
	/// </summary>
	string ToCompatible(string path, string tempFolder);
}

/// <summary>Keeps every photo as it is (used where no converter is available).</summary>
public sealed class KeepPhotoConverter : IPhotoConverter
{
	public string ToCompatible(string path, string tempFolder) => path;
}

/// <summary>Saves straight to the store, one write at a time.</summary>
public sealed class DirectFieldWriter(Func<SdeStore> store) : IFieldWriter
{
	private readonly object _gate = new object();

	public Task<AssessmentFile> ApplyAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context)
	{
		lock (_gate)
		{
			return Task.FromResult(store().Apply(assessmentId, change, context));
		}
	}

	public Task<AssessmentFile> AddAsync(AssessmentFile file, SaveContext context)
	{
		lock (_gate)
		{
			store().SaveAssessment(file, context);
			return Task.FromResult(file);
		}
	}
}
