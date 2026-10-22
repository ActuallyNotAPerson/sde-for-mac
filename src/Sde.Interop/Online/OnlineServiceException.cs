namespace Sde.Interop.Online;

/// <summary>
/// An online lookup (geocoder, flood map) failed. <see cref="Exception.Message"/> is written for the person using the
/// app; the technical detail (URL, status code, response text) is in <see cref="Detail"/> and <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class OnlineServiceException : Exception
{
	public OnlineServiceException(string message, string detail, Exception inner = null) : base(message, inner)
	{
		Detail = detail;
	}

	/// <summary>Technical detail for logs and support: what was asked and what came back.</summary>
	public string Detail { get; }

	/// <summary>The HTTP status code when the service answered with an error, otherwise null.</summary>
	public int? StatusCode { get; init; }
}
