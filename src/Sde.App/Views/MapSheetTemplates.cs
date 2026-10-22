using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.Views;

/// <summary>
/// The Map page's sheets and the views that show them, in one place: the main window lists this once among its data
/// templates, so new map sheets don't each need a line there.
/// </summary>
public sealed class MapSheetTemplates : IDataTemplate
{
	private static readonly Dictionary<Type, Func<Control>> Views = new Dictionary<Type, Func<Control>>
	{
		[typeof(OnlineLayerSheetViewModel)] = () => new OnlineLayerSheetView(),
		[typeof(LayerStyleSheetViewModel)] = () => new LayerStyleSheetView(),
		[typeof(SelectByAttributeSheetViewModel)] = () => new SelectByAttributeSheetView(),
		[typeof(AssignTeamSheetViewModel)] = () => new AssignTeamSheetView(),
		[typeof(WebMapSheetViewModel)] = () => new WebMapSheetView(),
		[typeof(SaveEditsSheetViewModel)] = () => new SaveEditsSheetView(),
		[typeof(NewLayerSheetViewModel)] = () => new NewLayerSheetView(),
		[typeof(MoveStructureSheetViewModel)] = () => new MoveStructureSheetView(),
		[typeof(SelectByLocationSheetViewModel)] = () => new SelectByLocationSheetView(),
		[typeof(CopyValuesSheetViewModel)] = () => new CopyValuesSheetView(),
		[typeof(PrintMapSheetViewModel)] = () => new PrintMapSheetView(),
	};

	public bool Match(object data) => data != null && Views.ContainsKey(data.GetType());

	public Control Build(object param) => param != null && Views.TryGetValue(param.GetType(), out Func<Control> make) ? make() : null;
}
