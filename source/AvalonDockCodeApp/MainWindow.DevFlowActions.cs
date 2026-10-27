// Static action surface of the sample; see MainWindow.DevFlowActions.cs in TestApp for why the
// DevFlow actions are static and marshal to the window's thread themselves.
using System;
using System.Linq;
using System.Windows;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ToggleTestApp
{
	public partial class MainWindow
	{
		/// <summary>
		/// The window the actions below act on. DevFlow starts the agent from
		/// <c>App.OnStartup</c>, before the window exists, so it is looked up per call.
		/// </summary>
		private static MainWindow Instance =>
			(Application.Current?.MainWindow as MainWindow)
			?? Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault()
			?? throw new InvalidOperationException("The sample's main window is not available yet.");

		/// <summary>
		/// Runs an action body on the thread that owns the window. The LibreWPF agent dispatches
		/// actions that touch UI to the application's thread; the WPF agent invokes them on the HTTP
		/// request thread, where reading or changing any WPF object throws.
		/// </summary>
		private static T OnUi<T>(Func<MainWindow, T> action)
		{
			var window = Instance;
			return window.Dispatcher.CheckAccess()
				? action(window)
				: window.Dispatcher.Invoke(() => action(window));
		}

		[DevFlowAction("avd.query.toggle-bars", Description = "Query every sidebar toggle button: zone, title, content id, checked state and bounds")]
		public static string QueryToggleBarsAction() => OnUi(w => w.QueryToggleBars());

		[DevFlowAction("avd.query.anchorables", Description = "Query the layout's anchorables: title, content id, visibility, auto-hide state")]
		public static string QueryAnchorablesAction() => OnUi(w => w.QueryAnchorables());

		[DevFlowAction("avd.toggle.click", Description = "Press a sidebar toggle button (content id, else title) and report the resulting state")]
		public static string ClickToggleButtonAction(string contentIdOrTitle) => OnUi(w => w.ClickToggleButton(contentIdOrTitle));

		[DevFlowAction("avd.query.shortcuts", Description = "Query the window's key bindings: gesture, toolbox and whether the gesture is registered")]
		public static string QueryShortcutsAction() => OnUi(w => w.QueryShortcuts());

		[DevFlowAction("avd.shortcut.invoke", Description = "Execute the key binding registered for a gesture (e.g. 'Ctrl+Alt+E')")]
		public static string InvokeShortcutAction(string gesture) => OnUi(w => w.InvokeShortcut(gesture));

		[DevFlowAction("avd.layout.reset", Description = "Delete the saved layout files and close every sidebar section")]
		public static string ResetLayoutAction() => OnUi(w => w.ResetLayout());

		[DevFlowAction("avd.layout.save", Description = "Save the current layout to the app's layout file")]
		public static string SaveLayoutToFileAction() => OnUi(w => w.SaveLayoutToFile());

		[DevFlowAction("avd.layout.load", Description = "Load the app's saved layout file")]
		public static string LoadLayoutFromFileAction() => OnUi(w => w.LoadLayoutFromFile());

		[DevFlowAction("avd.layout.load-unknown", Description = "Load a saved layout that also carries a tool window unknown to this app")]
		public static string LoadLayoutWithUnknownToolboxAction() => OnUi(w => w.LoadLayoutWithUnknownToolbox());
	}
}
