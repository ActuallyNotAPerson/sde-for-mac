using Sde.Core;
using Sde.Core.Assessments;

namespace Sde.App.ViewModels.Fields;

/// <summary>
/// Builds the editor's field for any <see cref="FieldInfo"/>, bound to an assessment, so bulk edit (and anything else
/// that edits a field outside the editor) shows the same kind of input the editor does.
/// </summary>
public static class FieldFactory
{
	public static FieldViewModel Create(FieldInfo f, AssessmentFile file, ReferenceData reference)
	{
		Func<object> target = f.Model == FieldModel.Property ? () => file.Property : () => file.Assessment;
		FieldViewModel vm = f.Kind switch
		{
			FieldKind.Number => new NumberFieldViewModel(f.Label, target, f.RecordType, f.Member)
			{
				Format = f.Format,
				Prefix = f.Unit == "$" ? "$" : null,
				Suffix = f.Unit is null or "$" ? null : f.Unit,
			},
			FieldKind.Date => new DateFieldViewModel(f.Label, target, f.RecordType, f.Member),
			FieldKind.YesNo => new BoolFieldViewModel(f.Label, target, f.RecordType, f.Member),
			FieldKind.Lookup or FieldKind.State or FieldKind.County or FieldKind.StructureType =>
				new ChoiceFieldViewModel(f.Label, target, f.RecordType, f.Member,
					() => FieldCatalog.Options(f, file, reference).Select(o => new ChoiceOption(o.Id, o.Name)).ToList()),
			FieldKind.MultilineText => new TextFieldViewModel(f.Label, target, f.RecordType, f.Member) { IsMultiline = true, IsWide = true },
			_ => new TextFieldViewModel(f.Label, target, f.RecordType, f.Member),
		};
		vm.Load();
		return vm;
	}
}
