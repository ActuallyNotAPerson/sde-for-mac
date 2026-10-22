using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One comment or reply in a review thread: who wrote it, when, and what it says.</summary>
public sealed class CommentViewModel(ReviewCommentRecord record)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public ReviewCommentRecord Record { get; } = record;

	public string Author => string.IsNullOrWhiteSpace(Record.Author) ? "Someone" : Record.Author.Trim();

	public string Body => Record.Body;

	/// <summary>"Reviewer A · Oct 22, 2026 3:14 PM", with "(AI assistant)" for a comment an assistant wrote.</summary>
	public string Byline => (Record.Via == SaveVia.AiAssistant ? Author + " (AI assistant)" : Author) + " · " + Record.CreatedOn.ToString("MMM d, yyyy h:mm tt", Us);
}

/// <summary>
/// A comment and its replies. A thread is open until someone resolves it; resolving it (or opening it again) covers
/// the replies too.
/// </summary>
public sealed partial class CommentThreadItemViewModel : ObservableObject
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
	private readonly CommentThreadsViewModel _owner;

	public CommentThreadItemViewModel(CommentThreadsViewModel owner, ReviewCommentRecord root, IEnumerable<ReviewCommentRecord> replies)
	{
		_owner = owner;
		Root = new CommentViewModel(root);
		Replies = replies.Select(r => new CommentViewModel(r)).ToList();
	}

	public CommentViewModel Root { get; }

	public IReadOnlyList<CommentViewModel> Replies { get; }

	public bool HasReplies => Replies.Count > 0;

	public Guid CommentId => Root.Record.CommentID;

	/// <summary>The field the comment is about (a FieldCatalog key), or null for the whole assessment.</summary>
	public string FieldKey => Root.Record.FieldKey;

	/// <summary>"About: Date damage occurred", or null when the comment is about the whole assessment.</summary>
	public string FieldText => FieldLabel(FieldKey) is string label ? "About: " + label : null;

	public bool HasField => FieldKey != null;

	/// <summary>True where the thread can take you to its field (in the editor).</summary>
	public bool CanGoToField => HasField && _owner.CanGoToField;

	/// <summary>The field's name as plain text, where it can't be gone to (beside the Review page's list).</summary>
	public bool ShowFieldText => HasField && !CanGoToField;

	public bool IsResolved => Root.Record.IsResolved;

	public bool IsOpen => !IsResolved;

	/// <summary>"Resolved by Reviewer A on Oct 22, 2026".</summary>
	public string ResolvedText => !IsResolved ? null
		: "Resolved" + (string.IsNullOrWhiteSpace(Root.Record.ResolvedBy) ? "" : " by " + Root.Record.ResolvedBy.Trim()) + " on " + Root.Record.ResolvedOn.Value.ToString("MMM d, yyyy", Us);

	/// <summary>True while the reply box is showing.</summary>
	[ObservableProperty]
	public partial bool IsReplying { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SendReplyCommand))]
	public partial string ReplyText { get; set; }

	public string AccessibleReply => "Reply to " + Root.Author;

	public string AccessibleReplyBox => "Your reply to " + Root.Author;

	public string AccessibleResolve => "Resolve the comment from " + Root.Author;

	public string AccessibleReopen => "Open the comment from " + Root.Author + " again";

	[RelayCommand]
	private void StartReply() => IsReplying = true;

	[RelayCommand]
	private void CancelReply()
	{
		IsReplying = false;
		ReplyText = null;
	}

	[RelayCommand(CanExecute = nameof(CanSendReply))]
	private void SendReply()
	{
		if (_owner.Reply(this, ReplyText))
		{
			ReplyText = null;
			IsReplying = false;
		}
	}

	private bool CanSendReply() => !string.IsNullOrWhiteSpace(ReplyText);

	[RelayCommand]
	private void Resolve() => _owner.SetResolved(this, true);

	[RelayCommand]
	private void Reopen() => _owner.SetResolved(this, false);

	[RelayCommand]
	private void GoToField() => _owner.GoToField(FieldKey);

	/// <summary>A field's name for a comment about it: the field's label, or the element for a damage percentage.</summary>
	public static string FieldLabel(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		if (key.StartsWith("Element.", StringComparison.Ordinal))
		{
			return "Damage to " + key["Element.".Length..];
		}
		return FieldCatalog.Get(key)?.Label ?? key;
	}
}

/// <summary>
/// The review comments on one assessment, as threads: add a comment, reply, resolve and open again. Shown beside the
/// Review page's list and in the editor's Comments sheet. Comments never change the assessment, so they can be added
/// to Final ones too.
/// </summary>
public sealed partial class CommentThreadsViewModel : ObservableObject
{
	private readonly AppServices _app;
	private readonly Action<string> _goToField;

	/// <param name="goToField">Opens a field by its FieldCatalog key (the editor passes one; elsewhere null).</param>
	public CommentThreadsViewModel(AppServices app, Guid assessmentId, Action<string> goToField = null)
	{
		_app = app;
		AssessmentId = assessmentId;
		_goToField = goToField;
		Reload();
	}

	public Guid AssessmentId { get; }

	/// <summary>Raised after a comment was added, answered, resolved or opened again.</summary>
	public event Action Changed;

	/// <summary>The threads shown: the open ones, then (when <see cref="ShowResolved"/> is on) the resolved ones.</summary>
	public ObservableCollection<CommentThreadItemViewModel> Threads { get; } = new();

	private List<CommentThreadItemViewModel> _all = new();

	public int OpenCount => _all.Count(t => t.IsOpen);

	public int ResolvedCount => _all.Count(t => t.IsResolved);

	public bool HasResolved => ResolvedCount > 0;

	public bool IsEmpty => _all.Count == 0;

	/// <summary>True when there are comments, but all of them are resolved and hidden.</summary>
	public bool AllResolvedHidden => !IsEmpty && OpenCount == 0 && !ShowResolved;

	public string ResolvedToggleText => ShowResolved ? "Hide resolved" : $"Show {ResolvedCount} resolved";

	internal bool CanGoToField => _goToField != null;

	/// <summary>Whether resolved threads are listed (after the open ones).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ResolvedToggleText), nameof(AllResolvedHidden))]
	public partial bool ShowResolved { get; set; }

	partial void OnShowResolvedChanged(bool value) => Fill();

	/// <summary>The new comment being typed.</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddCommand))]
	public partial string NewComment { get; set; }

	/// <summary>Reads the comments again.</summary>
	public void Reload()
	{
		List<ReviewCommentRecord> all = _app.Store.GetReviewComments(AssessmentId);
		ILookup<Guid?, ReviewCommentRecord> replies = all.Where(c => c.ParentID != null).ToLookup(c => c.ParentID);
		_all = all.Where(c => c.ParentID == null)
			.Select(c => new CommentThreadItemViewModel(this, c, replies[c.CommentID]))
			.ToList();
		Fill();
		OnPropertyChanged(nameof(OpenCount));
		OnPropertyChanged(nameof(ResolvedCount));
		OnPropertyChanged(nameof(HasResolved));
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(AllResolvedHidden));
		OnPropertyChanged(nameof(ResolvedToggleText));
	}

	private void Fill()
	{
		Threads.Clear();
		foreach (CommentThreadItemViewModel t in _all.Where(t => t.IsOpen).Concat(ShowResolved ? _all.Where(t => t.IsResolved) : Enumerable.Empty<CommentThreadItemViewModel>()))
		{
			Threads.Add(t);
		}
	}

	[RelayCommand]
	private void ToggleResolved() => ShowResolved = !ShowResolved;

	/// <summary>Adds the typed comment as a new thread.</summary>
	[RelayCommand(CanExecute = nameof(CanAdd))]
	public void Add()
	{
		if (Write(() => _app.Store.AddReviewComment(AssessmentId, NewComment), "Couldn't add the comment"))
		{
			NewComment = null;
		}
	}

	private bool CanAdd() => !string.IsNullOrWhiteSpace(NewComment);

	internal bool Reply(CommentThreadItemViewModel thread, string text) =>
		Write(() => _app.Store.AddReviewComment(AssessmentId, text, parentId: thread.CommentId), "Couldn't add the reply");

	internal void SetResolved(CommentThreadItemViewModel thread, bool resolved) =>
		Write(() => _app.Store.ResolveReviewComment(thread.CommentId, resolved), resolved ? "Couldn't resolve the comment" : "Couldn't open the comment again");

	internal void GoToField(string key) => _goToField?.Invoke(key);

	private bool Write(Action write, string failure)
	{
		try
		{
			write();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show(failure, ex);
			return false;
		}
		Reload();
		Changed?.Invoke();
		return true;
	}
}
