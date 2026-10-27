/************************************************************************
   AvalonDock

   Copyright (C) 2007-2013 Xceed Software Inc.

   This program is provided to you under the terms of the Microsoft Public
   License (Ms-PL) as published at https://opensource.org/licenses/MS-PL
 ************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
#if LIBREWPF
using System.Windows.Media.ProGPU;
#endif
using System.Windows.Threading;
using AvalonDock.Core;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Platform;
using System.Diagnostics;
using System.IO;
using AvalonDock.Serializer.Xml;
using AvalonDock;
using AvalonDock.Themes;
using AvalonDock.Themes.VS;
using System.Diagnostics.CodeAnalysis;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace TestApp
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	[SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "MainWindow intentionally orchestrates many UI framework types in this sample app.")]
	[DevFlowUIThread]
	public partial class MainWindow : Window
	{
		private const string ObjC = "/usr/lib/libobjc.dylib";
		private static MainWindow s_positionedMainWindow;
		private static Point? s_positionedMainContentOrigin;
		private DispatcherTimer _mainWindowPositionGuardTimer;
		private Point _mainWindowPositionGuardOrigin;

		// null = show the real modal dialog; otherwise the test has already decided the answer.
		private string _dialogAnswer;

		// The confirmations the sample asked for, so a test can assert the lifecycle ran instead of
		// looking for a modal window it cannot see.
		private readonly List<string> _dialogQuestions = new List<string>();
		private Point? _mainWindowPositionGuardViolation;

		private enum GetWindowCmd : uint
		{
			GW_HWNDLAST = 1,
			GW_HWNDPREV = 3,
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

		[DllImport(ObjC, EntryPoint = "objc_getClass")]
		private static extern IntPtr ObjCGetClass(string name);

		[DllImport(ObjC, EntryPoint = "sel_registerName")]
		private static extern IntPtr Sel(string name);

		[DllImport(ObjC, EntryPoint = "objc_msgSend")]
		private static extern IntPtr ObjCMsgSend(IntPtr receiver, IntPtr selector);

		[DllImport(ObjC, EntryPoint = "objc_msgSend")]
		private static extern nuint ObjCMsgSendRetNUInt(IntPtr receiver, IntPtr selector);

		[DllImport(ObjC, EntryPoint = "objc_msgSend")]
		private static extern byte ObjCMsgSendRetBool(IntPtr receiver, IntPtr selector);

		[DllImport(ObjC, EntryPoint = "objc_msgSend")]
		private static extern void ObjCMsgSendBool(IntPtr receiver, IntPtr selector, byte value);

		[DllImport(ObjC, EntryPoint = "objc_msgSend")]
		private static extern IntPtr ObjCMsgSendNUInt(IntPtr receiver, IntPtr selector, nuint arg);

		// NOT cached like the selectors below: objc_getClass("NSApplication") only succeeds once
		// AppKit.framework has actually been dlopen'd into this process, which GLFW does lazily when
		// it creates the first native window - well after this type's static fields would otherwise
		// be initialized. Caching the lookup result here previously froze it at IntPtr.Zero forever
		// (queried before AppKit was loaded), which silently broke every OS z-order check on macOS.
		private static IntPtr NsApplicationClass => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
			? ObjCGetClass("NSApplication")
			: IntPtr.Zero;
		private static readonly IntPtr _selSharedApplication = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
			? Sel("sharedApplication")
			: IntPtr.Zero;
		private static readonly IntPtr _selOrderedWindows = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
			? Sel("orderedWindows")
			: IntPtr.Zero;
		private static readonly IntPtr _selCount = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
			? Sel("count")
			: IntPtr.Zero;
		private static readonly IntPtr _selObjectAtIndex = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
			? Sel("objectAtIndex:")
			: IntPtr.Zero;
		private static readonly IntPtr _selContentView = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("contentView") : IntPtr.Zero;
		private static readonly IntPtr _selSuperview = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("superview") : IntPtr.Zero;
		private static readonly IntPtr _selSubviews = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("subviews") : IntPtr.Zero;
		private static readonly IntPtr _selClassName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("className") : IntPtr.Zero;
		private static readonly IntPtr _selUtf8String = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("UTF8String") : IntPtr.Zero;
		private static readonly IntPtr _selIsHidden = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("isHidden") : IntPtr.Zero;
		private static readonly IntPtr _selStyleMask = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Sel("styleMask") : IntPtr.Zero;

		private readonly Dictionary<string, int> _inputEventCounts = new Dictionary<string, int>();
		private readonly HashSet<UIElement> _inputDiagnosticElements = new HashSet<UIElement>();
		private Point _lastDockManagerMousePosition;
		private MouseButtonState _lastDockManagerLeftButton;
		private string _lastInputOriginalSource;
		private bool _lastDownHandled;

		public MainWindow()
		{
			InitializeComponent();
			InstallInputDiagnostics();

			DispatcherTimer timer = new DispatcherTimer();
			Random rnd = new Random();
			timer.Interval = TimeSpan.FromSeconds(1.0);
			timer.Tick += (s, e) =>
				{
					TestTimer++;

					TestBackground = new SolidColorBrush(Color.FromRgb(
						(byte)rnd.Next(0, 255), (byte)rnd.Next(0, 255), (byte)rnd.Next(0, 255)));

					FocusedElement = Keyboard.FocusedElement == null ? string.Empty : Keyboard.FocusedElement.ToString();
					//Debug.WriteLine(string.Format("ActiveContent = {0}", dockManager.ActiveContent));

				};
			timer.Start();

			this.DataContext = this;

			if (OperatingSystem.IsWindows())
			{
				winFormsHost.Child = new UserControl1();
			}
			else
			{
				// WindowsFormsHost is an HwndHost: it hosts the WinForms control in a native child
				// window, and a portable backend (LibreWPF) has none to give it - asking for one aborts
				// the process with "BuildWindowCore failed to return the hosted child window handle".
				// The pane stays so window/docking scenarios keep their layout, but the interop content
				// is replaced with a note instead of a host that cannot work here.
				WinFormsWindow.Content = new TextBlock
				{
					Text = "Windows Forms interop is only available on Windows.",
					Margin = new Thickness(8),
					TextWrapping = TextWrapping.Wrap,
				};
			}

			UpdateThemeColors();

		}

		private void InstallInputDiagnostics()
		{
			RefreshInputDiagnostics();
			dockManager.Loaded += (s, e) => RefreshInputDiagnostics();
		}

		private void RefreshInputDiagnostics()
		{
			AddInputDiagnostics(dockManager, "manager");

			// The main menu is instrumented too: menus not opening on click needs to distinguish
			// "the click never arrives" from "it arrives but menu mode does not engage".
			if (mainMenu != null)
			{
				AddInputDiagnostics(mainMenu, "menu");
				foreach (var item in mainMenu.Items.OfType<MenuItem>())
				{
					AddInputDiagnostics(item, $"menuitem:{item.Header}");
					InstallMenuTrace(item);
				}

				// Menus close when their owner window deactivates; if opening a popup deactivates the
				// main window we would see open-then-immediate-close here.
				Activated += (s, e) => TraceMenu("window.Activated");
				Deactivated += (s, e) => TraceMenu("window.Deactivated");

				// A menu also closes if something steals keyboard focus or exits menu mode. Trace both
				// so the event that coincides with the self-close can be identified.
				InputManager.Current.EnterMenuMode += (s, e) => TraceMenu("InputManager.EnterMenuMode");
				InputManager.Current.LeaveMenuMode += (s, e) => TraceMenu("InputManager.LeaveMenuMode");

				AddHandler(PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
					TraceMenu($"GotKeyboardFocus -> {e.NewFocus?.GetType().Name ?? "null"}")), true);
				AddHandler(PreviewLostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
					TraceMenu($"LostKeyboardFocus {e.OldFocus?.GetType().Name ?? "null"} -> {e.NewFocus?.GetType().Name ?? "null"}")), true);
			}

			// Enumerate floating windows as well as the docked manager, so panes that have been torn
			// out are instrumented too.
			var hitRoots = dockManager.FloatingWindows.Cast<DependencyObject>()
				.Concat(new DependencyObject[] { dockManager });
			foreach (var root in hitRoots)
			{
				foreach (var title in FindVisualDescendants<AnchorablePaneTitle>(root))
					AddInputDiagnostics(title, $"anchorable-title:{title.Model?.ContentId}");
				foreach (var tab in FindVisualDescendants<LayoutAnchorableTabItem>(root))
					AddInputDiagnostics(tab, $"anchorable-tab:{tab.Model?.ContentId}");
				foreach (var resizer in FindVisualDescendants<LayoutGridResizerControl>(root))
					AddInputDiagnostics(resizer, "anchorable-resizer");
			}
		}

		private readonly List<string> _menuTrace = new List<string>();

		private void TraceMenu(string message)
		{
			lock (_menuTrace)
			{
				if (_menuTrace.Count < 400)
					_menuTrace.Add(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
			}
		}

		/// <summary>
		/// Records what a click actually does to a top-level menu item: whether Click fires, whether
		/// IsSubmenuOpen is set at all, and whether it is immediately cleared again (which would mean
		/// something dismisses the menu rather than it never opening).
		/// </summary>
		private void InstallMenuTrace(MenuItem item)
		{
			var header = item.Header?.ToString();
			item.Click += (s, e) => TraceMenu($"{header}.Click");
			item.SubmenuOpened += (s, e) => TraceMenu($"{header}.SubmenuOpened");
			item.SubmenuClosed += (s, e) => TraceMenu($"{header}.SubmenuClosed");

			var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
				MenuItem.IsSubmenuOpenProperty, typeof(MenuItem));
			descriptor?.AddValueChanged(item, (s, e) =>
				TraceMenu($"{header}.IsSubmenuOpen={item.IsSubmenuOpen} captured={Mouse.Captured?.GetType().Name ?? "null"} active={IsActive}"));
		}

		/// <summary>Screen-space rectangle of an element, or why it could not be determined.</summary>
		private static string DescribeScreenRect(FrameworkElement element)
		{
			if (element == null) return "(null)";
			try
			{
				if (PresentationSource.FromVisual(element) == null) return "(no presentation source)";
				var origin = element.PointToScreen(new Point(0, 0));
				return $"x={origin.X:F0} y={origin.Y:F0} w={element.ActualWidth:F0} h={element.ActualHeight:F0}";
			}
			catch (Exception ex)
			{
				return "(err: " + ex.GetType().Name + ")";
			}
		}

		public string SetSplitterWidth(double width)
		{
			dockManager.GridSplitterWidth = width;
			dockManager.GridSplitterHeight = width;

			// Force the layout to rebuild its splitters with the new size.
			var layout = dockManager.Layout;
			dockManager.Layout = null;
			dockManager.Layout = layout;
			return $"GridSplitterWidth/Height = {width}";
		}

		public string QuerySplitters()
		{
			var list = new List<Dictionary<string, object>>();
			foreach (var root in GetAvalonDockVisualRoots())
			{
				foreach (var splitter in FindVisualDescendants<LayoutGridResizerControl>(root))
				{
					list.Add(new Dictionary<string, object>
					{
						["cursor"] = splitter.Cursor?.ToString() ?? "(null)",
						["forceCursor"] = splitter.ForceCursor,
						["isHitTestVisible"] = splitter.IsHitTestVisible,
						["isEnabled"] = splitter.IsEnabled,
						["size"] = $"{splitter.ActualWidth:F0}x{splitter.ActualHeight:F0}",
						["screen"] = DescribeScreenRect(splitter),
						["styleSet"] = splitter.Style != null,
						// What actually sits under the pointer when hovering the splitter centre.
						["hitAtCentre"] = HitTestNameAtSplitterCentre(splitter),
					});
				}
			}

			return System.Text.Json.JsonSerializer.Serialize(list);
		}

		private static string HitTestNameAtSplitterCentre(LayoutGridResizerControl splitter)
		{
			try
			{
				var root = PresentationSource.FromVisual(splitter)?.RootVisual as Visual;
				if (root == null) return "(no root)";
				var centre = splitter.TransformToVisual(root).Transform(
					new Point(splitter.ActualWidth / 2, splitter.ActualHeight / 2));
				var hit = VisualTreeHelper.HitTest(root, centre);
				var element = hit?.VisualHit;
				if (element == null) return "(no hit)";

				// Report the hit element and whether the splitter is on its ancestor chain, which is
				// what QueryCursor bubbling depends on.
				var onChain = false;
				for (DependencyObject d = element; d != null; d = VisualTreeHelper.GetParent(d))
				{
					if (ReferenceEquals(d, splitter)) { onChain = true; break; }
				}

				return $"{element.GetType().Name} splitterOnAncestorChain={onChain}";
			}
			catch (Exception ex)
			{
				return "(err: " + ex.GetType().Name + ")";
			}
		}

		public string TimerQuery() => TestTimer.ToString(System.Globalization.CultureInfo.InvariantCulture);

		public string MenuTrace()
		{
			lock (_menuTrace)
			{
				var text = _menuTrace.Count == 0 ? "(no menu events recorded)" : string.Join("\n", _menuTrace);
				_menuTrace.Clear();
				return text;
			}
		}

		private void AddInputDiagnostics(UIElement element, string name)
		{
			if (!_inputDiagnosticElements.Add(element))
				return;

			element.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((s, e) => RecordInputEvent(name, "preview-down", e)), true);
			element.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((s, e) => RecordInputEvent(name, "preview-move", e)), true);
			element.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((s, e) => RecordInputEvent(name, "preview-up", e)), true);
			element.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((s, e) => RecordInputEvent(name, "down", e)), true);
			element.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler((s, e) => RecordInputEvent(name, "move", e)), true);
			element.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((s, e) => RecordInputEvent(name, "up", e)), true);
			element.AddHandler(Mouse.MouseLeaveEvent, new MouseEventHandler((s, e) => RecordInputEvent(name, "leave", e)), true);
		}

		private void RecordInputEvent(string name, string kind, MouseEventArgs e)
		{
			var key = $"{name}.{kind}";
			_inputEventCounts.TryGetValue(key, out var count);
			_inputEventCounts[key] = count + 1;
			_lastDockManagerMousePosition = Mouse.GetPosition(dockManager);
			_lastDockManagerLeftButton = Mouse.LeftButton;
			_lastInputOriginalSource = e.OriginalSource?.GetType().FullName;
			if (kind == "down")
				_lastDownHandled = e.Handled;
		}

		/// <summary>
		/// TestTimer Dependency Property
		/// </summary>
		public static readonly DependencyProperty TestTimerProperty =
			DependencyProperty.Register(nameof(TestTimer), typeof(int), typeof(MainWindow),
				new FrameworkPropertyMetadata((int)0));

		/// <summary>
		/// Gets or sets the TestTimer property.  This dependency property 
		/// indicates a test timer that elapses evry one second (just for binding test).
		/// </summary>
		public int TestTimer
		{
			get => (int)GetValue(TestTimerProperty);
			set => SetValue(TestTimerProperty, value);
		}

		/// <summary>
		/// TestBackground Dependency Property
		/// </summary>
		public static readonly DependencyProperty TestBackgroundProperty =
			DependencyProperty.Register(nameof(TestBackground), typeof(Brush), typeof(MainWindow),
				new FrameworkPropertyMetadata((Brush)null));

		/// <summary>
		/// Gets or sets the TestBackground property.  This dependency property 
		/// indicates a randomly changing brush (just for testing).
		/// </summary>
		public Brush TestBackground
		{
			get => (Brush)GetValue(TestBackgroundProperty);
			set => SetValue(TestBackgroundProperty, value);
		}

		/// <summary>
		/// FocusedElement Dependency Property
		/// </summary>
		public static readonly DependencyProperty FocusedElementProperty =
			DependencyProperty.Register(nameof(FocusedElement), typeof(string), typeof(MainWindow),
				new FrameworkPropertyMetadata((IInputElement)null));

		/// <summary>
		/// Gets or sets the FocusedElement property.  This dependency property 
		/// indicates ....
		/// </summary>
		public string FocusedElement
		{
			get => (string)GetValue(FocusedElementProperty);
			set => SetValue(FocusedElementProperty, value);
		}

		private void OnLayoutRootPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			var activeContent = ((LayoutRoot)sender).ActiveContent;
			if (e.PropertyName == "ActiveContent")
			{
				Debug.WriteLine(string.Format("ActiveContent-> {0}", activeContent));
			}
		}

        [SuppressMessage("Style", "IDE0063:使用简单的 \"using\" 语句", Justification = "<挂起>")]
        private void OnLoadLayout(object sender, RoutedEventArgs e)
		{
			var currentContentsList = dockManager.Layout.Descendents().OfType<LayoutContent>().Where(c => c.ContentId != null).ToArray();

			string fileName = (sender as MenuItem).Header.ToString();
			var serializer = new XmlLayoutSerializer(dockManager);
			//serializer.LayoutSerializationCallback += (s, args) =>
			//    {
			//        var prevContent = currentContentsList.FirstOrDefault(c => c.ContentId == args.Model.ContentId);
			//        if (prevContent != null)
			//            args.Content = prevContent.Content;
			//    };
			using (var stream = new StreamReader(string.Format(@".\AvalonDock_{0}.config", fileName)))
				serializer.Deserialize(stream);
		}

		[SuppressMessage("Style", "IDE0063:使用简单的 \"using\" 语句", Justification = "<挂起>")]
		private void OnSaveLayout(object sender, RoutedEventArgs e)
		{
			string fileName = (sender as MenuItem).Header.ToString();
			var serializer = new XmlLayoutSerializer(dockManager);
			using (var stream = new StreamWriter(string.Format(@".\AvalonDock_{0}.config", fileName)))
				serializer.Serialize(stream);
		}

		private void OnShowWinformsWindow(object sender, RoutedEventArgs e)
		{
			var winFormsWindow = dockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == "WinFormsWindow");
			if (winFormsWindow.IsHidden)
				winFormsWindow.Show();
			else if (winFormsWindow.IsVisible)
				winFormsWindow.IsActive = true;
			else
				winFormsWindow.AddToLayout(dockManager, AnchorableShowStrategy.Bottom | AnchorableShowStrategy.Most);
		}

		private void AddTwoDocuments_click(object sender, RoutedEventArgs e)
		{
			var firstDocumentPane = dockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
			if (firstDocumentPane != null)
			{
                LayoutDocument doc = new LayoutDocument
                {
                    Title = "Test1"
                };
                firstDocumentPane.Children.Add(doc);

                LayoutDocument doc2 = new LayoutDocument
                {
                    Title = "Test2"
                };
                firstDocumentPane.Children.Add(doc2);
			}

			var leftAnchorGroup = dockManager.Layout.LeftSide.Children.FirstOrDefault();
			if (leftAnchorGroup == null)
			{
				leftAnchorGroup = new LayoutAnchorGroup();
				dockManager.Layout.LeftSide.Children.Add(leftAnchorGroup);
			}

			leftAnchorGroup.Children.Add(new LayoutAnchorable() { Title = "New Anchorable" });

		}

		private void OnShowToolWindow1(object sender, RoutedEventArgs e)
		{
			var toolWindow1 = dockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == "toolWindow1");
			if (toolWindow1.IsHidden)
				toolWindow1.Show();
			else if (toolWindow1.IsVisible)
				toolWindow1.IsActive = true;
			else
				toolWindow1.AddToLayout(dockManager, AnchorableShowStrategy.Bottom | AnchorableShowStrategy.Most);
		}

		private void OnFloatToolWindow1(object sender, RoutedEventArgs e)
		{
			var toolWindow1 = dockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == "toolWindow1");
			if (toolWindow1.IsHidden)
				toolWindow1.Show();
			if (toolWindow1.CanFloat && !toolWindow1.IsFloating)
				toolWindow1.Float();
		}

		private void DockManager_DocumentClosing(object sender, DocumentClosingEventArgs e)
		{
			if (AskYesNo("Are you sure you want to close the document?", "AvalonDock Sample") == false)
				e.Cancel = true;
		}

		private void OnDumpToConsole(object sender, RoutedEventArgs e)
		{
			// Uncomment when TRACE is activated on AvalonDock project
			// dockManager.Layout.ConsoleDump(0);
		}

		private void OnReloadManager(object sender, RoutedEventArgs e)
		{
		}

		private void OnUnloadManager(object sender, RoutedEventArgs e)
		{
			if (layoutRoot.Children.Contains(dockManager))
				layoutRoot.Children.Remove(dockManager);
		}

		private void OnLoadManager(object sender, RoutedEventArgs e)
		{
			if (!layoutRoot.Children.Contains(dockManager))
				layoutRoot.Children.Add(dockManager);
		}

		private void OnToolWindow1Hiding(object sender, System.ComponentModel.CancelEventArgs e)
		{
			if (AskYesNo("Are you sure you want to hide this tool?", "AvalonDock") == false)
				e.Cancel = true;
		}

		private void OnShowHeader(object sender, RoutedEventArgs e)
		{
			////            LayoutDocumentPane.ShowHeader = !LayoutDocumentPane.ShowHeader;
		}

		/// <summary>
		/// Method create a new anchorable window to test whether a floating window will auto-adjust its size to the
		/// containing control. See <see cref="DockingManager.AutoWindowSizeWhenOpened"/> dependency property.
		/// and TestUserControl in this demo App for more details.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void OnNewFloatingWindow(object sender, RoutedEventArgs e)
        {
            var view = new TestUserControl();
            var anchorable = new LayoutAnchorable()
            {
                Title = "Floating window with initial usercontrol size",
				Content = view
			};
            anchorable.AddToLayout(dockManager,AnchorableShowStrategy.Most);
            anchorable.Float();
        }

		private void OnSwitchTheme(object sender, RoutedEventArgs e)
		{
			var menuItem = sender as MenuItem;
			if (menuItem == null)
				return;

			var themeTag = menuItem.Tag as string;
			if (themeTag == null)
				return;

			Theme theme;
			switch (themeTag)
			{
				case "ArcDark": theme = new ArcDarkTheme(); break;
				case "ArcLight": theme = new ArcLightTheme(); break;
				case "VS2013Dark": theme = new Vs2013DarkTheme(); break;
				case "VS2013Light": theme = new Vs2013LightTheme(); break;
				case "VS2013Blue": theme = new Vs2013BlueTheme(); break;
				case "VS2026Dark": theme = new VS2026DarkTheme(); break;
				case "VS2026Light": theme = new VS2026LightTheme(); break;
				case "VS2026Blue": theme = new VS2026BlueTheme(); break;
				case "VS2022Dark": theme = new VS2022DarkTheme(); break;
				case "VS2022Light": theme = new VS2022LightTheme(); break;
				case "VS2022Blue": theme = new VS2022BlueTheme(); break;
				case "VS2015Dark": theme = new VS2015DarkTheme(); break;
				case "VS2015Light": theme = new VS2015LightTheme(); break;
				case "VS2015Blue": theme = new VS2015BlueTheme(); break;
				case "VS2010": theme = new VS2010Theme(); break;
				case "ExpressionDark": theme = new ExpressionDarkTheme(); break;
				case "ExpressionLight": theme = new ExpressionLightTheme(); break;
				case "Metro": theme = new MetroTheme(); break;
				case "Aero": theme = new AeroTheme(); break;
				case "Generic": theme = new GenericTheme(); break;
				default: theme = new ArcDarkTheme(); break;
			}

			dockManager.Theme = theme;

			// Mark only the selected theme in the menu
			foreach (var item in themeMenu.Items)
			{
				if (item is MenuItem mi && mi.IsCheckable)
					mi.IsChecked = mi == menuItem;
			}

			// Adapt UI colors to match the theme
			UpdateThemeColors();
		}

		private void UpdateThemeColors()
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
			{
				bool isDark = IsDarkBrush(dockManager.Background);
				var foreground = isDark ? Brushes.White : Brushes.Black;

				// Set foreground on content that needs adaptive text
				Foreground = foreground;
				mainMenu.Foreground = foreground;
				tbToolWindow1Timer.Foreground = foreground;
				tbAutoHide1Timer.Foreground = foreground;
				tbDocument2Timer.Foreground = foreground;
				TextElement.SetForeground(spAutoHide2, foreground);

				// Update window title bar color via DWM
				UpdateTitleBarColor();
			}));
		}

		[DllImport("dwmapi.dll", PreserveSig = true)]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

		private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
		private const int DWMWA_CAPTION_COLOR = 35;

		private void UpdateTitleBarColor()
		{
			var hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd == IntPtr.Zero)
				return;

			bool isDark = IsDarkBrush(dockManager.Background);
			int darkMode = isDark ? 1 : 0;
			DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

			// Windows 11+: set exact caption color
			var scb = dockManager.Background as SolidColorBrush;
			if (scb != null)
			{
				var c = scb.Color;
				int colorRef = c.R | (c.G << 8) | (c.B << 16);
				DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
			}
		}

		private static bool IsDarkBrush(Brush brush)
		{
			if (brush is SolidColorBrush scb)
			{
				var c = scb.Color;
				double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
				return luminance < 128;
			}
			return false;
		}

		public string FloatAnchorable(string contentId)
		{
			var anchorable = dockManager.Layout.Descendents()
				.OfType<LayoutAnchorable>()
				.FirstOrDefault(a => a.ContentId == contentId && !a.IsFloating);
			if (anchorable == null)
				return $"Anchorable '{contentId}' not found";
			anchorable.Float();
			Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
			return $"Floated '{contentId}'";
		}

		public string PositionFloatingWindow(string contentId, double left = 900, double top = 200)
		{
			var floating = FindVisibleFloatingWindow(contentId);
			if (floating == null)
				return $"No floating window found for '{contentId}'";

			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				PlatformHelper.SetWindowPosition(floating, left, top);
			else
			{
				floating.Left = left;
				floating.Top = top;
			}
			floating.UpdateLayout();
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				AdoptNativeWindowFramePosition(floating);
			return $"Positioned floating window for '{contentId}' at {left},{top}";
		}

		public string QueryFloatingZOrder(string contentId)
		{
			var floating = FindVisibleFloatingWindow(contentId);
			if (floating == null)
			{
				return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
				{
					["found"] = false,
					["contentId"] = contentId,
				});
			}

			var mainHandle = GetNativeWindowHandle(this);
			var floatingHandle = GetNativeWindowHandle(floating);
			var floatingContentTitle = floating.Model?.Descendents()
				.OfType<LayoutAnchorable>()
				.FirstOrDefault(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal))
				?.Title;
			var mainFound = TryGetPlatformWindowZOrder(mainHandle, out var mainZ);
			var floatingFound = TryGetPlatformWindowZOrder(floatingHandle, out var floatingZ);
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["found"] = true,
				["contentId"] = contentId,
				["mainTitle"] = Title,
				["floatingTitle"] = string.IsNullOrWhiteSpace(floating.Title) ? floatingContentTitle : floating.Title,
				["mainHandle"] = mainHandle.ToInt64(),
				["floatingHandle"] = floatingHandle.ToInt64(),
				["mainZOrderFound"] = mainFound,
				["floatingZOrderFound"] = floatingFound,
				["mainZOrder"] = mainZ,
				["floatingZOrder"] = floatingZ,
				["isFloatingAboveMain"] = mainFound && floatingFound && IsPlatformZOrderAbove(floatingZ, mainZ),
				["zOrderConvention"] = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
					? "lower index is frontmost in NSApplication.orderedWindows"
					: RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
						? "higher index is frontmost in _NET_CLIENT_LIST_STACKING"
						: "higher z-order is frontmost in Win32 GetWindow walk",
				// Lets a caller tell "this platform cannot report z-order at all" (skip) apart from
				// "z-order is readable and the answer is no" (a real failure).
				["zOrderSource"] = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
					? "appkit"
					: RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
						? (X11WindowStacking.IsAvailable ? "x11" : "none")
						: "win32",
				["floatingLeft"] = floating.Left,
				["floatingTop"] = floating.Top,
				["floatingWidth"] = floating.ActualWidth,
				["floatingHeight"] = floating.ActualHeight,
				["mainIsActive"] = IsActive,
				["floatingIsActive"] = floating.IsActive,
				["floatingTopmost"] = floating.Topmost,
			});
		}

		public string QueryX11Stacking()
		{
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				return System.Text.Json.JsonSerializer.Serialize(new { supported = false });

			var known = new Dictionary<IntPtr, string>();
			foreach (var window in Application.Current.Windows.OfType<Window>())
			{
				var xid = X11WindowStacking.TryGetWindowId(window);
				if (xid != IntPtr.Zero)
					known[xid] = $"{window.GetType().Name}:{window.Title}";
			}

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["supported"] = true,
				["displayAvailable"] = X11WindowStacking.IsAvailable,
				["appWindows"] = known.Select(kv => new Dictionary<string, object>
				{
					["windowId"] = kv.Key.ToInt64(),
					["owner"] = kv.Value,
				}).ToArray(),
				["stacking"] = X11WindowStacking.DescribeStacking(known),
			});
		}

		public string QueryMacOSViewTree(string target = "main", string contentId = "dragTestTool")
		{
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return System.Text.Json.JsonSerializer.Serialize(new { supported = false, target });

			Window window = target switch
			{
				"main" => this,
				"floating" => FindVisibleFloatingWindow(contentId),
				"overlay" => Application.Current.Windows.OfType<OverlayWindow>().FirstOrDefault(w => w.IsVisible),
				_ => null,
			};
			var nsWindow = window == null ? IntPtr.Zero : GetNativeWindowHandle(window);
			var classes = new List<Dictionary<string, object>>();
			if (nsWindow != IntPtr.Zero)
			{
				var contentView = ObjCMsgSend(nsWindow, _selContentView);
				var root = contentView;
				for (var i = 0; i < 8 && root != IntPtr.Zero; i++)
				{
					var parent = ObjCMsgSend(root, _selSuperview);
					if (parent == IntPtr.Zero) break;
					root = parent;
				}
				AppendMacViewTree(root, 0, classes, new HashSet<IntPtr>());
			}

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["supported"] = true,
				["target"] = target,
				["contentId"] = contentId,
				["windowFound"] = window != null,
				["nsWindow"] = nsWindow.ToInt64(),
				["windowClass"] = GetMacObjectClassName(nsWindow),
				["styleMask"] = nsWindow == IntPtr.Zero ? 0UL : (ulong)ObjCMsgSendRetNUInt(nsWindow, _selStyleMask),
				["views"] = classes,
				["visualEffectViews"] = classes.Where(v => ((string)v["class"]).Contains("VisualEffect", StringComparison.Ordinal)).ToArray(),
			});
		}

		private static void AppendMacViewTree(IntPtr view, int depth, List<Dictionary<string, object>> output, HashSet<IntPtr> visited)
		{
			if (view == IntPtr.Zero || depth > 16 || !visited.Add(view)) return;
			output.Add(new Dictionary<string, object>
			{
				["depth"] = depth,
				["address"] = view.ToInt64(),
				["class"] = GetMacObjectClassName(view),
				["hidden"] = ObjCMsgSendRetBool(view, _selIsHidden) != 0,
			});
			var subviews = ObjCMsgSend(view, _selSubviews);
			if (subviews == IntPtr.Zero) return;
			var count = ObjCMsgSendRetNUInt(subviews, _selCount);
			for (nuint i = 0; i < count; i++)
				AppendMacViewTree(ObjCMsgSendNUInt(subviews, _selObjectAtIndex, i), depth + 1, output, visited);
		}

		private static string GetMacObjectClassName(IntPtr value)
		{
			if (value == IntPtr.Zero) return null;
			var name = ObjCMsgSend(value, _selClassName);
			var utf8 = name == IntPtr.Zero ? IntPtr.Zero : ObjCMsgSend(name, _selUtf8String);
			return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
		}

		public string DockAnchorable(string contentId)
		{
			var anchorable = dockManager.Layout.FloatingWindows
				.SelectMany(f => f.Descendents())
				.OfType<LayoutAnchorable>()
				.FirstOrDefault(a => a.ContentId == contentId && a.IsFloating);
			if (anchorable == null)
				return $"Anchorable '{contentId}' not found";
			anchorable.Dock();
			return $"Docked '{contentId}'";
		}

		public string HideAnchorable(string contentId)
		{
			var anchorable = dockManager.Layout.Descendents()
				.OfType<LayoutAnchorable>()
				.FirstOrDefault(a => a.ContentId == contentId);
			if (anchorable == null)
				return $"Anchorable '{contentId}' not found";
			anchorable.Hide();
			return $"Hidden '{contentId}'";
		}

		public string ShowAnchorable(string contentId)
		{
			var anchorable = dockManager.Layout.Descendents()
				.OfType<LayoutAnchorable>()
				.FirstOrDefault(a => a.ContentId == contentId);
			if (anchorable == null)
				return $"Anchorable '{contentId}' not found";
			if (!anchorable.IsHidden)
				return $"'{contentId}' is not hidden";
			anchorable.Show();
			return $"Shown '{contentId}'";
		}

		public string AddDocuments()
		{
			var firstPane = dockManager.Layout.Descendents()
				.OfType<LayoutDocumentPane>()
				.FirstOrDefault();
			if (firstPane == null)
				return "No document pane found";

			var id1 = $"doc-{Guid.NewGuid():N}";
			var id2 = $"doc-{Guid.NewGuid():N}";
			firstPane.Children.Add(new LayoutDocument { Title = "TestDoc1", ContentId = id1 });
			firstPane.Children.Add(new LayoutDocument { Title = "TestDoc2", ContentId = id2 });
			return $"Added documents '{id1}', '{id2}'";
		}

		// The drop-target compass buttons are Borders with a deliberately lopsided BorderThickness
		// ("20,5,5,5") scaled down by a Viewbox, and they render as solid blobs with overshooting
		// edges instead of hollow frames. This shows the same Border three ways - raw, raw with a
		// uniform thickness, and Viewbox-scaled - so the culprit (non-uniform thickness vs. the
		// Viewbox scale) can be told apart from a screenshot.
		private Window _borderReproWindow;

		public string ShowBorderRepro(double left = 60, double top = 700)
		{
			_borderReproWindow?.Close();

			Border MakeBorder(Thickness thickness) => new Border
			{
				Width = 50,
				Height = 80,
				Margin = new Thickness(10),
				BorderBrush = new SolidColorBrush(Color.FromRgb(0x41, 0x7F, 0xE8)),
				BorderThickness = thickness,
			};

			var raw = MakeBorder(new Thickness(20, 5, 5, 5));
			var uniform = MakeBorder(new Thickness(5));
			var scaled = new Viewbox
			{
				Stretch = Stretch.Uniform,
				Width = 40,
				Height = 40,
				Margin = new Thickness(10),
				Child = MakeBorder(new Thickness(20, 5, 5, 5)),
			};

			// Same border under a plain RenderTransform rather than a Viewbox: tells apart "Viewbox
			// mis-measures its child" from "any scale transform loses the border thickness".
			var renderScaled = MakeBorder(new Thickness(20, 5, 5, 5));
			renderScaled.RenderTransform = new ScaleTransform(0.5, 0.5);

			var row = new StackPanel { Orientation = Orientation.Horizontal };
			row.Children.Add(raw);
			row.Children.Add(uniform);
			row.Children.Add(scaled);
			row.Children.Add(renderScaled);

			_borderReproWindow = new Window
			{
				Title = "BorderRepro",
				Width = 340,
				Height = 140,
				Left = left,
				Top = top,
				WindowStartupLocation = WindowStartupLocation.Manual,
				ShowInTaskbar = false,
				Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
				Content = row,
			};
			_borderReproWindow.Show();
			_borderReproWindow.UpdateLayout();

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["left"] = _borderReproWindow.Left,
				["top"] = _borderReproWindow.Top,
				["width"] = _borderReproWindow.ActualWidth,
				["height"] = _borderReproWindow.ActualHeight,
				["order"] = "raw(20,5,5,5) | uniform(5) | viewbox-scaled(20,5,5,5)",
			});
		}

		public string AddAnchorable(string title = null)
		{
			var anchorable = new LayoutAnchorable
			{
				Title = title ?? "New Anchorable",
				ContentId = $"anch-{Guid.NewGuid():N}"
			};
			anchorable.AddToLayout(dockManager, AnchorableShowStrategy.Most);
			return $"Added anchorable '{anchorable.ContentId}'";
		}

		public string ResetDragDropTestLayout()
		{
			foreach (var floatingWindow in dockManager.FloatingWindows.ToArray())
			{
				floatingWindow.Close();
			}

			foreach (Window floatingWindow in Application.Current.Windows
				.OfType<Window>()
				.Where(w => w != this && w.GetType().Name.Contains("FloatingWindowControl", StringComparison.Ordinal))
				.ToArray())
			{
				floatingWindow.Close();
			}

			var tool = new LayoutAnchorable
			{
				Title = "Drag Test Tool",
				ContentId = "dragTestTool",
				CanHide = true,
				CanClose = false,
				Content = new Border
				{
					Background = Brushes.Transparent,
					Child = new TextBlock
					{
						Text = "Drag Test Tool",
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center
					}
				}
			};
				var toolPane = new LayoutAnchorablePane(tool)
				{
					DockWidth = new GridLength(260)
				};
				toolPane.SelectedContentIndex = 0;
			var documentPane = new LayoutDocumentPane();
			documentPane.Children.Add(new LayoutDocument
			{
				Title = "Drag Test Document",
				ContentId = "dragTestDocument",
				Content = new Grid { Background = Brushes.Transparent }
			});
			// A second, separate DOCKED anchorable pane. AnchorablePaneDock{Left,Top,Right,Bottom,Inside}
			// compass indicators only appear while the pointer hovers an EXISTING anchorable pane, and
			// with only one anchorable (dragTestTool, which is what gets floated/dragged in these tests)
			// there would be no remaining docked anchorable pane left to host them once it floats away.
			var tool2 = new LayoutAnchorable
			{
				Title = "Drag Test Tool 2",
				ContentId = "dragTestTool2",
				CanHide = true,
				CanClose = false,
				Content = new Border
				{
					Background = Brushes.Transparent,
					Child = new TextBlock
					{
						Text = "Drag Test Tool 2",
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center
					}
				}
			};
				var toolPane2 = new LayoutAnchorablePane(tool2)
				{
					DockWidth = new GridLength(260)
				};
				toolPane2.SelectedContentIndex = 0;
			var root = new LayoutRoot
			{
				RootPanel = new LayoutPanel
				{
					Orientation = Orientation.Horizontal,
					Children =
					{
						toolPane,
						documentPane,
						toolPane2
					}
				}
			};

				dockManager.Layout = root;
				tool.IsSelected = true;
				dockManager.UpdateLayout();
			RefreshInputDiagnostics();
			return QueryLayout();
		}

		public string SwitchTheme(string themeTag)
		{
			Theme theme = themeTag switch
			{
				"ArcDark" => new ArcDarkTheme(),
				"ArcLight" => new ArcLightTheme(),
				"VS2013Dark" => new Vs2013DarkTheme(),
				"VS2013Light" => new Vs2013LightTheme(),
				"VS2013Blue" => new Vs2013BlueTheme(),
				"VS2026Dark" => new VS2026DarkTheme(),
				"VS2026Light" => new VS2026LightTheme(),
				"VS2026Blue" => new VS2026BlueTheme(),
				"VS2022Dark" => new VS2022DarkTheme(),
				"VS2022Light" => new VS2022LightTheme(),
				"VS2022Blue" => new VS2022BlueTheme(),
				"VS2015Dark" => new VS2015DarkTheme(),
				"VS2015Light" => new VS2015LightTheme(),
				"VS2015Blue" => new VS2015BlueTheme(),
				"VS2010" => new VS2010Theme(),
				"ExpressionDark" => new ExpressionDarkTheme(),
				"ExpressionLight" => new ExpressionLightTheme(),
				"Metro" => new MetroTheme(),
				"Aero" => new AeroTheme(),
				"Generic" => new GenericTheme(),
				_ => null
			};
			if (theme == null) return $"Unknown theme '{themeTag}'";
			dockManager.Theme = theme;
			UpdateThemeColors();
			return $"Switched to '{themeTag}'";
		}

		public string SerializeLayout()
		{
			var serializer = new XmlLayoutSerializer(dockManager);
			using var ms = new MemoryStream();
			using (var writer = new StreamWriter(ms, leaveOpen: true))
				serializer.Serialize(writer);
			ms.Position = 0;
			using var reader = new StreamReader(ms);
			return reader.ReadToEnd();
		}

		public string RestoreLayout(string xml)
		{
			var serializer = new XmlLayoutSerializer(dockManager);
			using var reader = new StringReader(xml);
			serializer.Deserialize(reader);
			return "Layout restored";
		}

		public string QueryLayout()
		{
			var anchorables = dockManager.Layout.Descendents()
				.OfType<LayoutAnchorable>()
				.Select(a => new Dictionary<string, object>
				{
					["contentId"] = a.ContentId,
					["title"] = a.Title,
					["isVisible"] = a.IsVisible,
					["isHidden"] = a.IsHidden,
					["isFloat"] = a.IsFloating,
					["isAutoHidden"] = a.IsAutoHidden,
					["canClose"] = a.CanClose,
					["canHide"] = a.CanHide,
				}).ToList();

			var documents = dockManager.Layout.Descendents()
				.OfType<LayoutDocument>()
				.Select(d => new Dictionary<string, object>
				{
					["contentId"] = d.ContentId,
					["title"] = d.Title,
					["isVisible"] = d.IsVisible,
				}).ToList();

			var floatingWindows = dockManager.Layout.Descendents()
				.OfType<LayoutFloatingWindow>()
				.Select(f =>
				{
					var contents = f.Descendents().OfType<LayoutContent>().ToArray();
					var firstContent = contents.FirstOrDefault();
					return new Dictionary<string, object>
					{
						["type"] = f.GetType().Name,
						["contentIds"] = contents.Select(c => c.ContentId).ToArray(),
						["floatingLeft"] = firstContent?.FloatingLeft,
						["floatingTop"] = firstContent?.FloatingTop,
						["floatingWidth"] = firstContent?.FloatingWidth,
						["floatingHeight"] = firstContent?.FloatingHeight,
					};
				}).ToList();

			var result = new Dictionary<string, object>
			{
				["anchorables"] = anchorables,
				["documents"] = documents,
				["floatingWindows"] = floatingWindows,
				// "Window" mode: anchorables hosted by a standalone top-level window, which is not a
				// floating window and is not part of the layout's floating-window list.
				["detachedContentIds"] = dockManager.DetachedAnchorables.Select(a => a.ContentId).ToArray(),
				["activeContent"] = dockManager.ActiveContent?.ToString(),
				["activeContentId"] = dockManager.Layout.ActiveContent?.ContentId,
			};

			return System.Text.Json.JsonSerializer.Serialize(result);
		}

			public string QueryBounds(string target, string contentId = null)
			{
				FrameworkElement element = target switch
				{
				"main-window" => this,
				"menu" => mainMenu,
				"manager" => dockManager,
					"anchorable-title" => FindAnchorableTitle(contentId),
					"autohide-tab" => FindAutoHideTab(contentId),
					// The tab of that very content wins; a sibling tab in the same pane is only a fallback,
					// otherwise the first tab in the pane is returned for every content in it.
					"anchorable-tab" => FindVisualDescendants<LayoutAnchorableTabItem>(dockManager)
							.Where(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0)
							.OrderBy(x => string.Equals((x.Model as LayoutAnchorable)?.ContentId, contentId, StringComparison.Ordinal) ? 0 : 1)
							.FirstOrDefault(x => MatchesAnchorableContent(x, contentId)),
					"document-tab" => FindVisualDescendants<LayoutDocumentTabItem>(dockManager)
						.Where(x => string.Equals(x.Model?.ContentId, contentId, StringComparison.Ordinal))
						.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0),
					"document-pane" => string.IsNullOrEmpty(contentId)
						? FindVisualDescendant<LayoutDocumentPaneControl>(dockManager, _ => true)
						: FindVisualDescendant<LayoutDocumentPaneControl>(
							dockManager,
							x => x.Model?.Descendents().OfType<LayoutContent>()
							.Any(c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal)) == true),
					"anchorable-pane" => FindVisualDescendant<LayoutAnchorablePaneControl>(
						dockManager,
						x => x.Model?.Descendents().OfType<LayoutAnchorable>()
						.Any(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal)) == true),
				"anchorable-resizer" => FindAnchorablePaneResizer(contentId),
					"floating-window" => string.IsNullOrEmpty(contentId)
						? dockManager.FloatingWindows.FirstOrDefault()
						: FindVisibleFloatingWindow(contentId),
				_ => null
			};

				return System.Text.Json.JsonSerializer.Serialize(CreateBoundsPayload(target, contentId, element));
			}

			public string QueryDragHandle(string target, string contentId = null)
			{
				if (target == "floating-caption")
					return QueryFloatingCaptionDragHandle(contentId);

				FrameworkElement element = target switch
				{
					"docked-anchorable" => FindDockedAnchorableDragHandle(contentId),
					"anchorable-resizer" => FindAnchorablePaneResizer(contentId),
					"document-body" => FindVisualDescendant<LayoutDocumentPaneControl>(dockManager, _ => true),
					"manager" => dockManager,
					_ => null
				};

				var payload = CreateBoundsPayload(target, contentId, element);
				if (element != null)
				{
					payload["handleKind"] = target;
					payload["hitTest"] = HitTest((double)payload["centerX"], (double)payload["centerY"]);
					payload["managerBounds"] = CreateBoundsPayload("manager", null, dockManager);
					var window = Window.GetWindow(element);
					if (window != null)
						payload["windowBounds"] = CreateBoundsPayload("window", null, window);
				}

				return System.Text.Json.JsonSerializer.Serialize(payload);
			}

			private string QueryFloatingCaptionDragHandle(string contentId)
			{
				var floating = FindVisibleFloatingWindow(contentId);
				if (floating == null)
				{
					return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
					{
						["target"] = "floating-caption",
						["contentId"] = contentId,
						["found"] = false,
						["reason"] = "floating window not found",
					});
				}

				var captionHeight = Math.Min(28d, Math.Max(12d, floating.ActualHeight / 6d));
				var leftInset = 8d;
				var topInset = 6d;
				var width = Math.Max(24d, Math.Min(180d, floating.ActualWidth - leftInset * 2d));
				var height = Math.Max(12d, captionHeight - topInset);
				var floatingOrigin = GetNativeWindowOrigin(floating);
				var topLeft = new Point(floatingOrigin.X + leftInset, floatingOrigin.Y + topInset);
				var center = new Point(floatingOrigin.X + leftInset + width / 2d, floatingOrigin.Y + topInset + height / 2d);
				var result = new Dictionary<string, object>
				{
					["target"] = "floating-caption",
					["contentId"] = contentId,
					["found"] = true,
					["x"] = topLeft.X,
					["y"] = topLeft.Y,
					["width"] = width,
					["height"] = height,
					["centerX"] = center.X,
					["centerY"] = center.Y,
					["hitTestPoint"] = false,
					["handleKind"] = "floating-caption",
					["windowBounds"] = CreateBoundsPayload("floating-window", contentId, floating),
					["managerBounds"] = CreateBoundsPayload("manager", null, dockManager),
				};
				result["hitTest"] = HitTest((double)result["centerX"], (double)result["centerY"]);
				return System.Text.Json.JsonSerializer.Serialize(result);
			}

			public string DebugShowOverlay(string previewZone = null)
			{
				OverlayWindow.DebugBorderEnabled = true;

				var floating = dockManager.FloatingWindows.FirstOrDefault(fw => fw.IsLoaded && fw.IsVisible);
				if (floating == null)
				{
					var anchorable = dockManager.Layout.Descendents().OfType<LayoutAnchorable>()
						.FirstOrDefault(a => !a.IsFloating && a.IsVisible);
					if (anchorable == null)
						return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["shown"] = false, ["reason"] = "no anchorable to float" });
					anchorable.Float();
					floating = dockManager.FloatingWindows.FirstOrDefault(fw => fw.IsLoaded && fw.IsVisible);
				}

				if (floating == null)
					return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["shown"] = false, ["reason"] = "no floating window available" });

				var host = (IOverlayWindowHost)dockManager;
				var overlay = host.ShowOverlayWindow(floating);
				overlay.DragEnter(floating);
				foreach (var area in host.GetDropAreas(floating))
					overlay.DragEnter(area);
				if (overlay is Window overlayWindowForLayout)
					overlayWindowForLayout.UpdateLayout();

				object previewInfo = null;
				if (!string.IsNullOrWhiteSpace(previewZone))
				{
					var target = overlay.GetTargets().FirstOrDefault(t => t.Type.ToString() == previewZone);
					if (target != null)
					{
						overlay.DragEnter(target);
						var previewPath = target.GetPreviewPath((OverlayWindow)overlay, floating.Model as LayoutFloatingWindow);
						var previewBounds = previewPath.Bounds;
						var overlayPosition = (overlay as Window) is { } overlayWindow
							? GetNativeWindowOrigin(overlayWindow)
							: new Point(0, 0);
						// GetPreviewPath returns geometry in overlay-local coordinates; translate to
						// screen coordinates so tests can compare against docked pane bounds directly.
						previewInfo = new Dictionary<string, object>
						{
							["zone"] = previewZone,
							["targetScreenBounds"] = RectToPayload(target.GetScreenBounds()),
							["previewGeometryBounds"] = new Dictionary<string, object>
							{
								["x"] = previewBounds.X + overlayPosition.X,
								["y"] = previewBounds.Y + overlayPosition.Y,
								["width"] = previewBounds.Width,
								["height"] = previewBounds.Height,
							},
							["overlayPosition"] = RectToPayload(new Rect(overlayPosition, new Size(0, 0))),
							["previewIsEmpty"] = previewBounds.IsEmpty,
						};
					}
					else
					{
						previewInfo = new Dictionary<string, object> { ["zone"] = previewZone, ["found"] = false };
					}
				}

				var payload = new Dictionary<string, object>
				{
					["shown"] = true,
					["overlay"] = (overlay is Window w)
						? CreateNativeWindowBoundsPayload(w)
						: null,
					["menuBounds"] = CreateBoundsPayload("menu", null, mainMenu),
					["managerBounds"] = CreateBoundsPayload("manager", null, dockManager),
					["targets"] = overlay.GetTargets().Select(target =>
					{
						var bounds = target.GetScreenBounds();
						return new Dictionary<string, object>
						{
							["type"] = target.Type.ToString(),
							["x"] = bounds.X,
							["y"] = bounds.Y,
							["width"] = bounds.Width,
							["height"] = bounds.Height,
						};
					}).ToArray(),
					["preview"] = previewInfo,
				};
				return System.Text.Json.JsonSerializer.Serialize(payload);
			}

		private static Dictionary<string, object> RectToPayload(Rect r) => new Dictionary<string, object>
			{
				["x"] = r.X,
				["y"] = r.Y,
				["width"] = r.Width,
				["height"] = r.Height,
			};

			public string DebugHideOverlay()
			{
				((IOverlayWindowHost)dockManager).HideOverlayWindow();
				OverlayWindow.DebugBorderEnabled = false;
				return "Hidden debug overlay";
			}

			public string ActivateTestWindow()
			{
				if (WindowState == WindowState.Minimized)
					WindowState = WindowState.Normal;
			Activate();
			Focus();
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["isActive"] = IsActive,
					["isKeyboardFocusWithin"] = IsKeyboardFocusWithin,
				});
			}

			public string ActivateFloatingWindow(string contentId = null)
			{
				// Without a content id, the first floating window: the Tools menu creates one whose
				// anchorable has no ContentId at all, and a test that just created it needs no other
				// handle on it.
				var floating = string.IsNullOrEmpty(contentId)
					? dockManager.FloatingWindows.LastOrDefault(fw => fw.IsLoaded && fw.IsVisible)
					: FindVisibleFloatingWindow(contentId);
				if (floating == null)
					throw new InvalidOperationException($"Floating window not found for '{contentId}'.");
				floating.Activate();
				floating.Focus();
				Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
				return System.Text.Json.JsonSerializer.Serialize("Activated floating window");
			}

			public string PositionMainWindow(double left = 50, double top = 40)
			{
				if (WindowState == WindowState.Minimized)
					WindowState = WindowState.Normal;
				WindowStartupLocation = WindowStartupLocation.Manual;
				Left = left;
				Top = top;
				Width = 900;
				Height = 700;
				UpdateLayout();
				Activate();
				AdoptNativeWindowFramePosition(this);
				s_positionedMainWindow = this;
				s_positionedMainContentOrigin = GetNativeWindowOrigin(this);
				return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
				{
					["left"] = Left,
					["top"] = Top,
					["width"] = ActualWidth,
					["height"] = ActualHeight,
					["isActive"] = IsActive,
					["requestedLeft"] = left,
					["requestedTop"] = top,
				});
			}

			public string StartMainWindowPositionGuard()
			{
				_mainWindowPositionGuardTimer?.Stop();
				_mainWindowPositionGuardOrigin = PlatformHelper.GetWindowContentOrigin(this);
				_mainWindowPositionGuardViolation = null;
				_mainWindowPositionGuardTimer ??= new DispatcherTimer(
					TimeSpan.FromMilliseconds(20),
					DispatcherPriority.Send,
					(_, __) => SampleMainWindowPositionGuard(),
					Dispatcher);
				_mainWindowPositionGuardTimer.Start();
				return System.Text.Json.JsonSerializer.Serialize(new
				{
					x = _mainWindowPositionGuardOrigin.X,
					y = _mainWindowPositionGuardOrigin.Y,
				});
			}

			public string QueryMainWindowPositionGuard()
			{
				SampleMainWindowPositionGuard();
				var current = PlatformHelper.GetWindowContentOrigin(this);
				return System.Text.Json.JsonSerializer.Serialize(new
				{
					armed = _mainWindowPositionGuardTimer?.IsEnabled == true,
					baselineX = _mainWindowPositionGuardOrigin.X,
					baselineY = _mainWindowPositionGuardOrigin.Y,
					currentX = current.X,
					currentY = current.Y,
					moved = _mainWindowPositionGuardViolation.HasValue,
					violationX = _mainWindowPositionGuardViolation?.X,
					violationY = _mainWindowPositionGuardViolation?.Y,
				});
			}

			private void SampleMainWindowPositionGuard()
			{
				if (_mainWindowPositionGuardTimer?.IsEnabled != true || _mainWindowPositionGuardViolation.HasValue)
					return;

				var current = PlatformHelper.GetWindowContentOrigin(this);
				const double tolerance = 0.5;
				if (Math.Abs(current.X - _mainWindowPositionGuardOrigin.X) > tolerance ||
					Math.Abs(current.Y - _mainWindowPositionGuardOrigin.Y) > tolerance)
					_mainWindowPositionGuardViolation = current;
			}

			// The platform can refuse the requested frame: on macOS a window may not be placed with its
			// title bar underneath the system menu bar, so a small Top is silently clamped downward (a
			// requested Top of 40 lands at ~61 here, and the exact clamp varies with menu-bar height and
			// notched displays). LibreWPF never pushes that clamp back into the managed Left/Top, so
			// PointToScreen - which is derived from those managed values - keeps reporting the REQUESTED
			// position while synthetic OS-level mouse input is aimed at the REAL one. Every screen
			// coordinate computed from a managed query is then off by the clamp distance, which is what
			// made native drag tests land on the wrong element. Read the true native frame back and
			// adopt it so the managed and native sides agree again.
			private static void AdoptNativeWindowFramePosition(Window window)
			{
#if !LIBREWPF
				// Real WPF: the managed frame is the native frame, so there is nothing to adopt.
				return;
#else
				if (!ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) || host?.SilkWindow is not { } silk)
					return;

				// The native move runs on the native windowing loop, so an immediate read can still
				// return the pre-move frame. Wait for the position to stop changing before adopting it.
				var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
				var last = silk.Position;
				var stableReads = 0;
				while (DateTime.UtcNow < deadline && stableReads < 3)
				{
					window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
					System.Threading.Thread.Sleep(30);
					var current = silk.Position;
					stableReads = current.X == last.X && current.Y == last.Y ? stableReads + 1 : 0;
					last = current;
				}

				if (Math.Abs(last.X - window.Left) > 0.5)
					window.Left = last.X;
				if (Math.Abs(last.Y - window.Top) > 0.5)
					window.Top = last.Y;
				window.UpdateLayout();
#endif
			}

			public string ResetInputDiagnostics()
			{
				if (Mouse.Captured is Menu || Mouse.Captured is MenuItem)
					Mouse.Capture(null);
				_inputEventCounts.Clear();
				_lastDockManagerMousePosition = default;
				_lastDockManagerLeftButton = Mouse.LeftButton;
			_lastInputOriginalSource = null;
			_lastDownHandled = false;
			return "reset";
		}

		public string QueryInputDiagnostics()
		{
			var result = new Dictionary<string, object>
			{
				["counts"] = _inputEventCounts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
				["mouseX"] = _lastDockManagerMousePosition.X,
				["mouseY"] = _lastDockManagerMousePosition.Y,
				["leftButton"] = _lastDockManagerLeftButton.ToString(),
				["captured"] = Mouse.Captured?.GetType().FullName,
				["directlyOver"] = Mouse.DirectlyOver?.GetType().FullName,
				["originalSource"] = _lastInputOriginalSource,
				// True when something below the manager handled the last MouseDown, which stops
				// LayoutAnchorControl.OnMouseDown from opening the auto-hide flyout.
				["lastDownHandled"] = _lastDownHandled,
			};

			return System.Text.Json.JsonSerializer.Serialize(result);
		}

		public string MenuCaptureTest()
		{
			var report = new Dictionary<string, object>
			{
				["capturedBefore"] = Mouse.Captured?.GetType().FullName,
				["windowCount"] = Application.Current.Windows.Count,
				["floatingWindowCount"] = dockManager.FloatingWindows.Count(),
				["mainWindowIsActive"] = IsActive,
				["menuSource"] = PresentationSource.FromVisual(mainMenu)?.GetType().Name,
			};

			bool captured;
			try
			{
				captured = Mouse.Capture(mainMenu, CaptureMode.SubTree);
				report["captureResult"] = captured;
				report["capturedAfter"] = Mouse.Captured?.GetType().FullName;
			}
			catch (Exception ex)
			{
				report["captureResult"] = ex.GetType().Name + ": " + ex.Message;
				captured = false;
			}

			if (captured) Mouse.Capture(null);

			// Which source does the mouse device consider active? If it is not the main window's,
			// a capture request targeting an element in the main window is refused.
			try
			{
				var pi = typeof(MouseDevice).GetProperty("CriticalActiveSource",
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				report["mouseActiveSource"] = (pi?.GetValue(Mouse.PrimaryDevice) as PresentationSource)?.RootVisual?.GetType().Name;
			}
			catch (Exception ex) { report["mouseActiveSource"] = "err: " + ex.Message; }

			return System.Text.Json.JsonSerializer.Serialize(report);
		}

		public string MenuProbe(string header = "Layout")
		{
			var items = mainMenu.Items.OfType<MenuItem>().ToList();
			var report = new Dictionary<string, object>
			{
				["menuIsEnabled"] = mainMenu.IsEnabled,
				["menuIsVisible"] = mainMenu.IsVisible,
				["items"] = items.Select(i => new Dictionary<string, object>
				{
					["header"] = i.Header?.ToString(),
					["isEnabled"] = i.IsEnabled,
					["role"] = i.Role.ToString(),
					["itemCount"] = i.Items.Count,
				}).ToList(),
			};

			var target = items.FirstOrDefault(i => string.Equals(i.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase));
			if (target == null)
			{
				report["error"] = $"no top-level menu item with header '{header}'";
				return System.Text.Json.JsonSerializer.Serialize(report);
			}

			try
			{
				target.IsSubmenuOpen = true;
				report["setIsSubmenuOpen"] = "ok";
			}
			catch (Exception ex)
			{
				report["setIsSubmenuOpen"] = ex.GetType().FullName + ": " + ex.Message;
			}

			report["isSubmenuOpenAfter"] = target.IsSubmenuOpen;

			// Where does the popup actually land on screen? A popup positioned off-screen, at the
			// origin, or over another window looks identical to "not rendering" from the user's side.
			report["headerScreenRect"] = DescribeScreenRect(target);

			// If the submenu really opened, its popup child should now have a presentation source.
			var popup = target.Template?.FindName("PART_Popup", target) as System.Windows.Controls.Primitives.Popup;
			report["popupFound"] = popup != null;
			report["popupIsOpen"] = popup?.IsOpen;
			if (popup?.Child != null)
			{
				report["popupChildSource"] = PresentationSource.FromVisual(popup.Child)?.GetType().Name;
				report["popupChildScreenRect"] = DescribeScreenRect(popup.Child as FrameworkElement);
				report["popupChildVisible"] = (popup.Child as UIElement)?.IsVisible;

				// The popup's own window, if it has one: its position is what actually decides
				// whether the user sees anything.
				var popupRootSource = PresentationSource.FromVisual(popup.Child);
				if (popupRootSource?.RootVisual is FrameworkElement popupRoot)
					report["popupRootScreenRect"] = DescribeScreenRect(popupRoot);
			}

			return System.Text.Json.JsonSerializer.Serialize(report);
		}

		/// <summary>Describes a menu item the way a test asserts on it: header, state and what it carries.</summary>
		private static Dictionary<string, object> DescribeMenuItem(MenuItem item) => new Dictionary<string, object>
		{
			["header"] = item.Header?.ToString(),
			["isEnabled"] = item.IsEnabled,
			["isCheckable"] = item.IsCheckable,
			["isChecked"] = item.IsChecked,
			["isSubmenu"] = item.Items.Count > 0,
			["itemCount"] = item.Items.Count,
			["command"] = item.Command?.GetType().Name,
			["tag"] = item.Tag?.ToString(),
			["role"] = item.Role.ToString(),
		};

		/// <summary>
		/// Resolves a menu path such as <c>Layout/Save/Layout_1</c> against the live menu bar. A '/' or a
		/// '&gt;' separates the levels, the comparison against each header is case-insensitive, and a
		/// missing segment returns a message naming what was there instead (so a test failure says what
		/// the menu actually had).
		/// </summary>
		private MenuItem FindMenuItem(string path, out string error)
		{
			error = null;
			if (string.IsNullOrWhiteSpace(path))
				return null;

			MenuItem current = null;
			foreach (var segment in path.Split(new[] { '/', '>' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var siblings = current == null
					? mainMenu.Items.OfType<MenuItem>().ToArray()
					: current.Items.OfType<MenuItem>().ToArray();
				var header = segment.Trim();
				var next = siblings.FirstOrDefault(i =>
					string.Equals(i.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase));
				if (next == null)
				{
					error = $"No menu item '{header}' in " +
						(current == null ? "the menu bar" : $"'{current.Header}'") +
						". Available: " + string.Join(", ", siblings.Select(i => i.Header?.ToString()));
					return null;
				}

				current = next;
			}

			return current;
		}

		public string MenuItems(string path = null)
		{
			var parent = FindMenuItem(path, out var error);
			if (error != null)
				throw new InvalidOperationException(error);

			var items = parent == null
				? mainMenu.Items.OfType<MenuItem>().ToArray()
				: parent.Items.OfType<MenuItem>().ToArray();

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["path"] = path ?? string.Empty,
				["count"] = items.Length,
				["items"] = items.Select(DescribeMenuItem).ToArray(),
			});
		}

		/// <summary>
		/// Invokes a menu item by header path, which is what selecting it does: a command item runs its
		/// command, an item with a Click handler raises Click with itself as the sender (the handlers
		/// here read <c>(sender as MenuItem).Header</c>, so the item has to be the source).
		/// </summary>
		public string InvokeMenuItem(string path)
		{
			var item = FindMenuItem(path, out var error);
			if (error != null)
				throw new InvalidOperationException(error);
			if (item.Items.Count > 0)
				throw new InvalidOperationException($"'{path}' is a submenu; name the item inside it.");

			string via;
			if (item.Command != null)
			{
				if (!item.Command.CanExecute(item.CommandParameter))
					throw new InvalidOperationException($"The command of '{path}' cannot execute.");
				item.Command.Execute(item.CommandParameter);
				via = "command:" + item.Command.GetType().Name;
			}
			else
			{
				item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
				via = "click";
			}

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["invoked"] = path,
				["via"] = via,
				["isChecked"] = item.IsChecked,
				["item"] = DescribeMenuItem(item),
				["managerHosted"] = layoutRoot.Children.Contains(dockManager),
			});
		}

		public string SetPaneGroupWidth(string contentId, double width)
		{
			var content = dockManager.Layout.Descendents().OfType<LayoutContent>()
				.FirstOrDefault(c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal))
				?? throw new InvalidOperationException($"No content '{contentId}'.");

			ILayoutContainer parent = content.Parent;
			while (parent != null && parent is not LayoutAnchorablePaneGroup)
				parent = (parent as ILayoutElement)?.Parent;
			if (parent is not LayoutAnchorablePaneGroup group)
				return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["changed"] = false });

			group.DockWidth = new GridLength(width);
			dockManager.UpdateLayout();
			Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["changed"] = true, ["dockWidth"] = group.DockWidth.Value });
		}

		public string CloseMenus()
		{
			foreach (var item in FindVisualDescendants<MenuItem>(mainMenu))
				item.IsSubmenuOpen = false;

			var closed = new List<string>();
			foreach (var menu in new[] { dockManager.AnchorableContextMenu, dockManager.DocumentContextMenu })
			{
				if (menu?.IsOpen != true)
					continue;
				menu.IsOpen = false;
				closed.Add(menu.Name ?? "context-menu");
			}

			if (Keyboard.FocusedElement is UIElement focused)
				focused.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, focused, null)
				{
					// The routed event's handler type is KeyboardFocusChangedEventHandler, so plain
					// RoutedEventArgs cannot be delivered on WPF ("cannot be converted to type
					// KeyboardFocusChangedEventArgs").
					RoutedEvent = Keyboard.LostKeyboardFocusEvent,
					Source = focused,
				});

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["closed"] = closed.ToArray(),
			});
		}

		/// <summary>
		/// Invokes an entry of a tab's drop-down context menu. The menu belongs to the tab's
		/// <see cref="DropDownControlArea"/>, which is what gives its commands their LayoutItem data
		/// context, so the menu is opened through the same right-click the class handler handles rather
		/// than constructed here.
		/// </summary>
		public string InvokeTabContextMenuItem(string title, string header, string kind = "anchorable")
		{
			var area = FindTabDropDownArea(title, kind);
			var menu = area.DropDownContextMenu
				?? throw new InvalidOperationException($"The '{title}' tab has no context menu.");

			if (!menu.IsOpen)
			{
				area.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
				{
					RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
					Source = area,
				});
			}

			var item = menu.Items.OfType<MenuItem>()
				.FirstOrDefault(i => string.Equals(i.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException(
					$"No '{header}' entry in the '{title}' context menu. Available: " +
					string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString())));

			string via;
			if (item.Command != null)
			{
				if (!item.Command.CanExecute(item.CommandParameter))
					throw new InvalidOperationException($"The '{header}' entry is disabled in the '{title}' context menu.");
				item.Command.Execute(item.CommandParameter);
				via = "command:" + item.Command.GetType().Name;
			}
			else
			{
				item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
				via = "click";
			}

			menu.IsOpen = false;
			Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["title"] = title,
				["header"] = header,
				["via"] = via,
				["items"] = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToArray(),
				["layout"] = QueryLayout(),
			});
		}

		/// <summary>The drop-down area of a visible tab, which owns that tab's context menu.</summary>
		private DropDownControlArea FindTabDropDownArea(string title, string kind)
		{
			var roots = GetAvalonDockVisualRoots().ToArray();
			FrameworkElement tab = string.Equals(kind, "document", StringComparison.OrdinalIgnoreCase)
				? roots.SelectMany(FindVisualDescendants<LayoutDocumentTabItem>)
					.FirstOrDefault(t => string.Equals(t.Model?.Title, title, StringComparison.Ordinal)
						|| string.Equals(t.Model?.ContentId, title, StringComparison.Ordinal))
				: roots.SelectMany(FindVisualDescendants<LayoutAnchorableTabItem>)
					.FirstOrDefault(t => string.Equals(t.Model?.Title, title, StringComparison.Ordinal)
						|| string.Equals(t.Model?.ContentId, title, StringComparison.Ordinal));

			if (tab == null)
				throw new InvalidOperationException($"No {kind} tab titled '{title}' is visible.");

			return FindVisualDescendants<DropDownControlArea>(tab).FirstOrDefault()
				?? throw new InvalidOperationException($"The '{title}' tab has no drop-down area to open a menu from.");
		}

		/// <summary>
		/// Right-clicks a tab without a pointer: the tab's <see cref="DropDownControlArea"/> handles
		/// PreviewMouseRightButtonUp (a class handler) and opens the manager's context menu, so raising
		/// that event on the area runs the same code a real right click does. TestApp defines no context
		/// menu, so the useful assertion is that the gesture is a no-op: the tab survives and the app
		/// is still answering.
		/// </summary>
		public string RightClickTab(string title, string kind = "document")
		{
			var roots = GetAvalonDockVisualRoots().ToArray();
			FrameworkElement tab;
			if (string.Equals(kind, "anchorable", StringComparison.OrdinalIgnoreCase))
			{
				tab = roots.SelectMany(FindVisualDescendants<LayoutAnchorableTabItem>)
					.FirstOrDefault(t => string.Equals(t.Model?.Title, title, StringComparison.Ordinal)
						|| string.Equals(t.Model?.ContentId, title, StringComparison.Ordinal));
			}
			else
			{
				tab = roots.SelectMany(FindVisualDescendants<LayoutDocumentTabItem>)
					.FirstOrDefault(t => string.Equals(t.Model?.Title, title, StringComparison.Ordinal)
						|| string.Equals(t.Model?.ContentId, title, StringComparison.Ordinal));
			}

			if (tab == null)
				throw new InvalidOperationException($"No {kind} tab titled '{title}' is visible.");

			var area = FindVisualDescendants<DropDownControlArea>(tab).FirstOrDefault();
			if (area == null)
				throw new InvalidOperationException($"The '{title}' tab has no drop-down area to right-click.");

			var raised = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
			{
				RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
				Source = area,
			};
			area.RaiseEvent(raised);

			var menu = area.DropDownContextMenu;
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["title"] = title,
				["kind"] = kind,
				["handled"] = raised.Handled,
				["hasContextMenu"] = menu != null,
				["isMenuOpen"] = menu?.IsOpen ?? false,
				["menuItems"] = menu == null
					? Array.Empty<Dictionary<string, object>>()
					: menu.Items.OfType<MenuItem>().Select(DescribeMenuItem).ToArray(),
			});
		}

		public string QueryWindow() => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["title"] = Title,
			["left"] = Left,
			["top"] = Top,
			["width"] = ActualWidth,
			["height"] = ActualHeight,
			["state"] = WindowState.ToString(),
			["isActive"] = IsActive,
			["managerHosted"] = layoutRoot.Children.Contains(dockManager),
		});

		public string ResizeWindow(double width, double height)
		{
			if (width <= 0 || height <= 0)
				throw new ArgumentOutOfRangeException(nameof(width), "A window needs a positive size.");

			Width = width;
			Height = height;
			UpdateLayout();
			return QueryWindow();
		}

		/// <summary>
		/// Answers the sample's two confirmation dialogs from the test: a modal <c>MessageBox</c> cannot
		/// be clicked on a headless runner, and the dialog itself is not what the suite is about - the
		/// answer is. <c>ask</c> (the default) restores the real dialog.
		/// </summary>
		public string AnswerDialogs(string answer)
		{
			lock (_dialogQuestions)
				_dialogQuestions.Clear();

			var mode = (answer ?? "ask").Trim().ToLowerInvariant();
			switch (mode)
			{
				case "ask":
					_dialogAnswer = null;
					break;
				case "yes":
				case "no":
					_dialogAnswer = mode;
					break;
				default:
					throw new ArgumentException("Use 'yes', 'no' or 'ask'.", nameof(answer));
			}

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["answer"] = mode,
			});
		}

		private bool? AskYesNo(string message, string caption)
		{
			lock (_dialogQuestions)
				_dialogQuestions.Add(message);

			if (_dialogAnswer != null)
				return _dialogAnswer == "yes";

			return MessageBox.Show(message, caption, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
		}

		/// <summary>The confirmations the sample has asked for since the last avd.dialogs.answer.</summary>
		public string QueryDialogs()
		{
			string[] questions;
			lock (_dialogQuestions)
				questions = _dialogQuestions.ToArray();

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["answer"] = _dialogAnswer ?? "ask",
				["questions"] = questions,
			});
		}

		/// <summary>
		/// Closes a floating window through the path a window manager's close button takes: the
		/// window's own Closing/Closed lifecycle, which is what raises the anchorables' Hiding event.
		/// A WM_SYSCOMMAND/SC_CLOSE post ends in exactly this call, so the behaviour under test - not
		/// the Win32 message - is what this reproduces.
		/// </summary>
		public string CloseFloatingWindow(string contentId)
		{
			var floating = FindVisibleFloatingWindow(contentId);
			if (floating == null)
			{
				// A standalone "Window" mode window: closing it returns the anchorable to the layout.
				var detached = dockManager.DetachedAnchorables
					.FirstOrDefault(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal));
				if (detached == null)
					throw new InvalidOperationException($"No floating window found for '{contentId}'.");

				dockManager.ReattachAnchorable(detached);
				Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
				return QueryLayout();
			}

			// What a window manager's close ends in: Alt+F4, the title-bar close and the system menu
			// reach LayoutAnchorableFloatingWindowControl as WM_CLOSE, which runs the hide command
			// (issue #368). Window.Close() alone skips that and only hides the native window, so the
			// model keeps its floating window. Done through the command so it behaves the same where
			// there is no Win32 message to post.
			if (floating is LayoutAnchorableFloatingWindowControl anchorableWindow)
			{
				if (anchorableWindow.HideWindowCommand.CanExecute(null))
					anchorableWindow.HideWindowCommand.Execute(null);
				else
					floating.Close();
			}
			else
			{
				floating.Close();
			}

			Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
			return QueryLayout();
		}

		/// <summary>Adds the two documents by clicking the sample's own button, so its handler runs.</summary>
		public string AddTwoDocuments()
		{
			var button = FindDocumentContentElement<Button>("document1", b => string.Equals(b.Content?.ToString(), "Click to add 2 documents", StringComparison.Ordinal));
			if (button == null)
				return System.Text.Json.JsonSerializer.Serialize("The add-documents button was not found in Document 1.");

			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
			return QueryLayout();
		}

		public string ActivateDocument(string contentIdOrTitle)
		{
			var document = FindDocument(contentIdOrTitle);
			if (document == null)
				throw new InvalidOperationException($"No document matches '{contentIdOrTitle}'.");

			document.IsActive = true;
			dockManager.UpdateLayout();
			return QueryLayout();
		}

		/// <summary>
		/// Reports what a document or anchorable actually hosts, scoped to it: the text of its TextBoxes,
		/// its TextBlocks and the labels of its Buttons. The FlaUI cases asserted "some button exists in
		/// the window", which passed even when the content was empty; this says which controls the
		/// content has.
		/// </summary>
		public string ContentReport(string contentIdOrTitle)
		{
			var content = dockManager.Layout.Descendents().OfType<LayoutContent>()
				.FirstOrDefault(c =>
					string.Equals(c.ContentId, contentIdOrTitle, StringComparison.Ordinal)
					|| string.Equals(c.Title, contentIdOrTitle, StringComparison.Ordinal));
			if (content == null)
				throw new InvalidOperationException($"No document or anchorable matches '{contentIdOrTitle}'.");

			var view = content == null ? null : FindContentElement(contentIdOrTitle);
			var textBoxes = view == null
				? new List<TextBox>()
				: FindVisualDescendants<TextBox>(view).ToList();
			var textBlocks = view == null
				? new List<TextBlock>()
				: FindVisualDescendants<TextBlock>(view).Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
			var buttons = view == null
				? new List<Button>()
				: FindVisualDescendants<Button>(view).ToList();

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["contentId"] = content.ContentId,
				["title"] = content.Title,
				["kind"] = content is LayoutDocument ? "document" : "anchorable",
				["isActive"] = content.IsActive,
				["isVisible"] = content switch
				{
					LayoutDocument document => document.IsVisible,
					LayoutAnchorable anchorable => anchorable.IsVisible,
					_ => false,
				},
				["isAutoHidden"] = content is LayoutAnchorable autoHidden && autoHidden.IsAutoHidden,
				["viewLoaded"] = view != null,
				["textBoxes"] = textBoxes.Select(t => t.Text).ToArray(),
				["textBlocks"] = textBlocks.Select(t => t.Text).ToArray(),
				["buttons"] = buttons.Select(b => b.Content?.ToString()).ToArray(),
			});
		}

		/// <summary>
		/// The auto-hide flyout: which contents it hosts and whether the given one is the open one. The
		/// flyout is a separate native host window, so "open" has to be read from the manager rather
		/// than from the docked content's IsVisible.
		/// </summary>
		public string QueryAutoHide(string contentIdOrTitle = null)
		{
			// The flyout's model is the anchorable it was opened for (LayoutAutoHideWindowControl.Show
			// assigns it directly), not a pane, so it is read rather than descended into.
			var flyout = dockManager.AutoHideWindow;
			var hosted = flyout?.Model is LayoutAnchorable flyoutAnchorable && flyout.IsVisible
				? new[] { flyoutAnchorable.ContentId }
				: Array.Empty<string>();

			var requested = dockManager.Layout.Descendents().OfType<LayoutAnchorable>()
				.FirstOrDefault(a =>
					string.Equals(a.ContentId, contentIdOrTitle, StringComparison.Ordinal)
					|| string.Equals(a.Title, contentIdOrTitle, StringComparison.Ordinal));

			var anchorBounds = requested == null ? null : FindAutoHideTab(requested.ContentId);

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["open"] = hosted.Length > 0,
				["contentIds"] = hosted,
				["requested"] = contentIdOrTitle,
				["requestedIsOpen"] = requested != null
					&& hosted.Contains(requested.ContentId, StringComparer.Ordinal),
				["requestedIsVisible"] = requested?.IsVisible ?? false,
				["requestedIsAutoHidden"] = requested?.IsAutoHidden ?? false,
				["tabPresent"] = anchorBounds != null,
			});
		}

		/// <summary>The auto-hide sidebar button (the "tab" that opens the flyout) for a content id.</summary>
		private FrameworkElement FindAutoHideTab(string contentId)
			=> FindVisualDescendants<LayoutAnchorControl>(dockManager)
				.Where(x => string.Equals((x.Model as LayoutAnchorable)?.ContentId, contentId, StringComparison.Ordinal))
				.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0);

		/// <summary>The focused element, and whether it sits inside the docking manager's content.</summary>
		public string QueryFocus() => System.Text.Json.JsonSerializer.Serialize(DescribeFocus());

		/// <summary>Focuses the first TextBox a document or anchorable hosts (a click would do the same).</summary>
		public string SetFocus(string contentIdOrTitle)
		{
			var view = FindContentElement(contentIdOrTitle);
			if (view == null)
				throw new InvalidOperationException($"No document or anchorable matches '{contentIdOrTitle}'.");

			var textBox = FindVisualDescendants<TextBox>(view).FirstOrDefault();
			if (textBox == null)
				throw new InvalidOperationException($"'{contentIdOrTitle}' hosts no TextBox to focus.");

			textBox.Focus();
			Keyboard.Focus(textBox);
			Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
			return System.Text.Json.JsonSerializer.Serialize(DescribeFocus());
		}

		/// <summary>
		/// Raises a key through WPF's routed input on the focused element. The agent can insert text into
		/// a control on every platform, but it cannot hold a modifier or press an arrow key there (Linux
		/// injects one keysym, macOS goes through cliclick, Windows through SendInput - three different
		/// implementations, none of them chords), so the gesture is delivered the way a keyboard would
		/// deliver it to the element that has focus. A handler that inspects live <c>Keyboard.Modifiers</c>
		/// still sees no modifier; <see cref="DescribeFocus"/> and the handled flag are what a test can
		/// assert on.
		/// </summary>
		public string RaiseKey(string key, string modifiers = null)
		{
			if (!Enum.TryParse<Key>(key, ignoreCase: true, out var parsedKey))
				throw new ArgumentException($"'{key}' is not a WPF Key name.", nameof(key));

			var target = Keyboard.FocusedElement as UIElement ?? dockManager;
			var source = PresentationSource.FromVisual(target) ?? PresentationSource.FromVisual(this);
			var before = DescribeFocus();

			// A handler on the way can fail in the backend rather than in AvalonDock (LibreWPF has no
			// Win32 keyboard state to ask for, for instance). That is reported, because the focus
			// assertions are still meaningful, but an exception from the app itself is not: it means a
			// key handler is broken and has to fail the action like any other defect.
			string error = null;
			var handled = false;
			try
			{
				var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, parsedKey)
				{
					RoutedEvent = Keyboard.PreviewKeyDownEvent,
				};
				target.RaiseEvent(preview);

				handled = preview.Handled;
				if (!handled)
				{
					var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, parsedKey)
					{
						RoutedEvent = Keyboard.KeyDownEvent,
					};
					target.RaiseEvent(down);
					handled = down.Handled;
				}

				target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, parsedKey)
				{
					RoutedEvent = Keyboard.KeyUpEvent,
				});
			}
			catch (Exception ex) when (IsBackendLimitation(ex))
			{
				error = ex.GetType().Name + ": " + ex.Message;
			}

			Dispatcher.Invoke(() => { }, DispatcherPriority.Input);

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["key"] = parsedKey.ToString(),
				["modifiers"] = modifiers ?? string.Empty,
				["handled"] = handled,
				["focusBefore"] = before,
				["focusAfter"] = DescribeFocus(),
				["error"] = error,
				["navigatorEnabled"] = dockManager.ShowNavigator,
			});
		}

		/// <summary>
		/// Types text through WPF's text-input pipeline into whatever has focus, which is the part a
		/// keyboard drives. The agent can also insert text into a control by element id, but that path
		/// targets a control the caller has to name; this one goes wherever the app's focus is, exactly
		/// like the keys a user presses.
		/// </summary>
		public string RaiseTextInput(string text)
		{
			if (text == null)
				throw new ArgumentNullException(nameof(text));

			if (Keyboard.FocusedElement is not UIElement target)
				throw new InvalidOperationException("Nothing has focus; focus a control before typing into it.");

			var composition = new TextComposition(InputManager.Current, target, text);
			target.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
			{
				RoutedEvent = TextCompositionManager.TextInputEvent,
			});
			Dispatcher.Invoke(() => { }, DispatcherPriority.Input);

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["text"] = text,
				["focus"] = DescribeFocus(),
			});
		}

		/// <summary>
		/// True when a platform backend cannot perform an operation at all (a missing entry point in a
		/// Win32 shim, for example), as opposed to the app failing while performing it.
		/// </summary>
		private static bool IsBackendLimitation(Exception ex)
			=> ex is EntryPointNotFoundException || ex is DllNotFoundException || ex is PlatformNotSupportedException;

		private Dictionary<string, object> DescribeFocus()
		{
			var focused = Keyboard.FocusedElement as DependencyObject;
			var owner = focused is FrameworkElement element && element.Parent != null ? element.Parent.GetType().Name : null;
			var insideManager = focused is DependencyObject node && IsDescendantOf(node, dockManager);
			return new Dictionary<string, object>
			{
				["type"] = focused?.GetType().Name,
				["name"] = (focused as FrameworkElement)?.Name,
				["owner"] = owner,
				["insideManager"] = insideManager,
				["isDropDownControlArea"] = focused is DropDownControlArea,
				["root"] = focused == null ? null : GetVisualRoot(focused),
			};
		}

		private string GetVisualRoot(DependencyObject node)
		{
			DependencyObject current = node;
			while (true)
			{
				var parent = VisualTreeHelper.GetParent(current);
				if (parent == null)
					return current.GetType().Name;
				current = parent;
			}
		}

		private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
		{
			for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
			{
				if (ReferenceEquals(current, ancestor))
					return true;
			}

			return false;
		}

		private LayoutDocument FindDocument(string contentIdOrTitle)
			=> dockManager.Layout.Descendents().OfType<LayoutDocument>()
				.FirstOrDefault(d => string.Equals(d.ContentId, contentIdOrTitle, StringComparison.Ordinal)
					|| string.Equals(d.Title, contentIdOrTitle, StringComparison.Ordinal));

		private T FindDocumentContentElement<T>(string contentIdOrTitle, Func<T, bool> predicate)
			where T : DependencyObject
		{
			var view = FindContentElement(contentIdOrTitle);
			return view == null ? null : FindVisualDescendants<T>(view).FirstOrDefault(predicate);
		}

		/// <summary>The element that renders a document's or an anchorable's content.</summary>
		private FrameworkElement FindContentElement(string contentIdOrTitle)
		{
			var content = dockManager.Layout.Descendents().OfType<LayoutContent>()
				.FirstOrDefault(c =>
					string.Equals(c.ContentId, contentIdOrTitle, StringComparison.Ordinal)
					|| string.Equals(c.Title, contentIdOrTitle, StringComparison.Ordinal));
			if (content == null)
				return null;

			var item = dockManager.GetLayoutItemFromModel(content);
			if (item?.View != null)
				return item.View;

			return content.Content as FrameworkElement;
		}

		public string TransparencyProbe(bool show = true)
		{
			foreach (var w in Application.Current.Windows.OfType<Window>()
						.Where(w => (w.Tag as string) == "transparency-probe").ToArray())
			{
				w.Close();
			}

			if (!show) return "probe windows closed";

			Window Make(bool transparent, double left)
			{
				var w = new Window
				{
					Tag = "transparency-probe",
					Title = transparent ? "transparent probe" : "opaque probe",
					Width = 220,
					Height = 160,
					Left = left,
					Top = 120,
					ShowInTaskbar = false,
					WindowStartupLocation = WindowStartupLocation.Manual,
					Content = new Border
					{
						Background = System.Windows.Media.Brushes.Red,
						Width = 120,
						Height = 60,
						Child = new TextBlock
						{
							Text = transparent ? "TRANSPARENT" : "OPAQUE",
							Foreground = System.Windows.Media.Brushes.White,
							HorizontalAlignment = HorizontalAlignment.Center,
							VerticalAlignment = VerticalAlignment.Center,
						},
					},
				};

				// AllowsTransparency must be set before the window is shown.
				if (transparent)
				{
					w.WindowStyle = WindowStyle.None;
					w.AllowsTransparency = true;
					w.Background = System.Windows.Media.Brushes.Transparent;
				}

				w.Show();
				return w;
			}

			var t = Make(transparent: true, left: 900);
			var o = Make(transparent: false, left: 1140);
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["transparentVisible"] = t.IsVisible,
				["transparentActual"] = $"{t.ActualWidth}x{t.ActualHeight}",
				["opaqueVisible"] = o.IsVisible,
				["opaqueActual"] = $"{o.ActualWidth}x{o.ActualHeight}",
				["note"] = "Look at the screen: both should show a red block. If only OPAQUE is visible, transparent windows still do not render.",
			});
		}

		public string ShowOverlayDiagnostics(bool show = true)
		{
			var fwc = dockManager.FloatingWindows.FirstOrDefault();
			if (fwc == null)
				return "no floating window - call avd.float first";

			var host = (IOverlayWindowHost)dockManager;

			// The overlay is a full-size window sitting over the docking manager; leaving it up after a
			// diagnostic run swallows clicks aimed at the app underneath (it looked like "menus stopped
			// working"). Always offer an explicit way to take it back down.
			if (!show)
			{
				host.HideOverlayWindow();
				return "overlay hidden";
			}

			var overlay = host.ShowOverlayWindow(fwc);
			if (overlay == null)
				return "ShowOverlayWindow returned null";

			// A drop area must be entered for the compass grids to become visible; DragEnter(area)
			// also requires the floating window to have been entered first.
			overlay.DragEnter(fwc);
			overlay.DragEnter(new DropArea<DockingManager>(dockManager, DropAreaType.DockingManager));

			var win = overlay as Window;
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["overlayType"] = overlay.GetType().Name,
				["isWindow"] = win != null,
				["isVisible"] = win?.IsVisible,
				["allowsTransparency"] = win?.AllowsTransparency,
				["windowStyle"] = win?.WindowStyle.ToString(),
				["left"] = win?.Left,
				["top"] = win?.Top,
				["width"] = win?.Width,
				["height"] = win?.Height,
				["actualWidth"] = win?.ActualWidth,
				["actualHeight"] = win?.ActualHeight,
				["hasPresentationSource"] = win != null && PresentationSource.FromVisual(win) != null,
				["presentationSourceType"] = win == null ? null : PresentationSource.FromVisual(win)?.GetType().FullName,
				["visibleTargets"] = overlay.GetTargets().Count(),
				["windows"] = DescribeWindowHosting(),
			});
		}

		/// <summary>
		/// Diagnostic: for every open window, report how LibreWPF is hosting it. A portable window
		/// either gets its own native ProGPU window or is composited into the owner's; the latter has
		/// no transparent framebuffer of its own, which is the suspected reason a transparent overlay
		/// renders nothing.
		/// </summary>
		private static List<Dictionary<string, object>> DescribeWindowHosting()
		{
			var list = new List<Dictionary<string, object>>();
			foreach (Window w in Application.Current.Windows)
			{
				var src = PresentationSource.FromVisual(w);
				var info = new Dictionary<string, object>
				{
					["type"] = w.GetType().Name,
					["isVisible"] = w.IsVisible,
					["allowsTransparency"] = w.AllowsTransparency,
					["windowStyle"] = w.WindowStyle.ToString(),
					["hasOwner"] = w.Owner != null,
					["sourceType"] = src?.GetType().Name,
					["compositionTargetType"] = src?.CompositionTarget?.GetType().Name,
				};

				// HwndSource.IsPortable / PortableOwner are LibreWPF additions; read reflectively so
				// this compiles against classic WPF too.
				if (src != null)
				{
					foreach (var prop in new[] { "IsPortable", "PortableOwner" })
					{
						var pi = src.GetType().GetProperty(prop);
						if (pi != null)
						{
							var val = pi.GetValue(src);
							info[prop] = prop == "PortableOwner" ? val?.GetType().Name : val;
						}
					}
				}

				list.Add(info);
			}

			return list;
		}

		public string QueryPlatformDiagnostics()
		{
			var source = PresentationSource.FromVisual(this);
			var windowOrigin = PointToScreen(new Point(0, 0));
			var managerOrigin = dockManager.PointToScreen(new Point(0, 0));
			var clientOrigin = TryReadPortableClientOrigin(source);
			var assemblies = AppDomain.CurrentDomain.GetAssemblies()
				.Where(a => a.GetName().Name is "ProGPU.Wpf" or "PresentationCore" or "PresentationFramework")
				.Select(a => new Dictionary<string, object>
				{
					["name"] = a.GetName().Name,
					["version"] = a.GetName().Version?.ToString(),
					["location"] = a.Location,
				})
				.ToArray();

			var result = new Dictionary<string, object>
			{
				["windowLeft"] = Left,
				["windowTop"] = Top,
				["actualWidth"] = ActualWidth,
				["actualHeight"] = ActualHeight,
				["windowPointToScreenX"] = windowOrigin.X,
				["windowPointToScreenY"] = windowOrigin.Y,
				["managerPointToScreenX"] = managerOrigin.X,
				["managerPointToScreenY"] = managerOrigin.Y,
				["sourceType"] = source?.GetType().FullName,
				["clientOrigin"] = clientOrigin,
				["assemblies"] = assemblies,
			};

			// Managed Left/Top and PointToScreen are all derived from the same managed window state,
			// so they agree with each other even when the real native window never moved. Report the
			// actual native (Silk.NET/GLFW) frame separately - that is what synthetic OS-level mouse
			// input is actually aimed at, so any divergence here is the root cause of "the click
			// landed somewhere else" failures.
#if LIBREWPF
			if (ProGpuWpfDiagnostics.TryGetWindowHost(this, out var diagHost) && diagHost?.SilkWindow is { } silk)
			{
				result["nativePositionX"] = silk.Position.X;
				result["nativePositionY"] = silk.Position.Y;
				result["nativeSizeX"] = silk.Size.X;
				result["nativeSizeY"] = silk.Size.Y;
			}
#endif

			return System.Text.Json.JsonSerializer.Serialize(result, PlatformDiagnosticsJsonOptions);
		}

		// Early in the window lifecycle (before the first layout pass / native frame realization),
		// PointToScreen and ActualWidth/Height can legitimately report NaN/Infinity. The default
		// JsonSerializer throws on those, which crashes this diagnostics query exactly when it would
		// be most useful (right after startup, mid-drag re-layout). Allow the named literals instead.
		private static readonly System.Text.Json.JsonSerializerOptions PlatformDiagnosticsJsonOptions = new()
		{
			NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
		};

		private static Dictionary<string, object> CreateNativeWindowBoundsPayload(Window window)
		{
			var origin = GetNativeWindowOrigin(window);
			return new Dictionary<string, object>
			{
				["left"] = origin.X,
				["top"] = origin.Y,
				["width"] = window.ActualWidth,
				["height"] = window.ActualHeight,
				["bottom"] = origin.Y + window.ActualHeight,
			};
		}

		private static object TryReadPortableClientOrigin(PresentationSource source)
		{
			if (source == null)
				return null;

			try
			{
				var portableSource = source;
				var portableOwnerProperty = source.GetType().GetProperty(
					"PortableOwner",
					System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
				if (portableOwnerProperty?.GetValue(source) is PresentationSource portableOwner)
					portableSource = portableOwner;

				var property = portableSource.GetType().GetProperty(
					"ClientOrigin",
					System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
				if (property?.GetValue(portableSource) is Point point)
					return new Dictionary<string, object> { ["x"] = point.X, ["y"] = point.Y };
			}
			catch
			{
			}

			return null;
		}

		public string QueryActiveDropTargets()
		{
			var results = new List<Dictionary<string, object>>();
			foreach (var floating in dockManager.FloatingWindows)
			{
				var overlay = floating.CurrentDragService?.CurrentOverlayWindow;
				if (overlay == null)
					continue;

				foreach (var target in overlay.GetTargets())
				{
					var bounds = target.GetScreenBounds();
					if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
						!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
						continue;
					results.Add(new Dictionary<string, object>
					{
						["type"] = target.Type.ToString(),
						["x"] = bounds.X,
						["y"] = bounds.Y,
						["width"] = bounds.Width,
						["height"] = bounds.Height,
						["centerX"] = bounds.X + bounds.Width / 2d,
						["centerY"] = bounds.Y + bounds.Height / 2d,
					});
				}
			}

			return System.Text.Json.JsonSerializer.Serialize(results);
		}

		// The drop-target compass paints an opaque background on macOS even though the OverlayWindow
		// is Background=Transparent/AllowsTransparency=true. Report how the overlay is actually
		// hosted (its PresentationSource, and whether that source got a real native window) so the
		// opaque fill can be traced to the layer that produces it.
		private static string DescribeOverlaySource(Window overlay)
		{
			if (overlay == null)
				return null;

			var source = PresentationSource.FromVisual(overlay);
			var payload = new Dictionary<string, object>
			{
				["sourceType"] = source?.GetType().FullName,
				["rootVisual"] = source?.RootVisual?.GetType().FullName,
			};
#if LIBREWPF
			var hasHost = ProGpuWpfDiagnostics.TryGetWindowHost(overlay, out var host);
			payload["hasProGpuHost"] = hasHost;
			payload["hasSilkWindow"] = hasHost && host?.SilkWindow != null;
			payload["silkWindowTitle"] = hasHost ? host?.SilkWindow?.Title : null;
#else
			// Real WPF has no ProGPU host; the source fields above still describe the overlay.
			payload["hasProGpuHost"] = false;
			payload["hasSilkWindow"] = false;
			payload["silkWindowTitle"] = null;
#endif
			return System.Text.Json.JsonSerializer.Serialize(payload);
		}

		// DockingManager registers a LayoutContent's Content as a logical child when it creates the
		// layout item, so a live count shows whether that registration is being released again when
		// the item goes away. A count that climbs across repeated float/dock/restore cycles is the
		// leak that also makes InternalAddLogicalChild's debug assertion fire on a later re-add.

		public string QueryLogicalChildren()
		{
			var live = 0;
			var dead = 0;
			var enumerator = dockManager.LogicalChildrenPublic;
			while (enumerator.MoveNext())
			{
				if (enumerator.Current == null) dead++;
				else live++;
			}

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["live"] = live,
				["dead"] = dead,
			});
		}

		public string QueryDragState()
		{
			return System.Text.Json.JsonSerializer.Serialize(dockManager.FloatingWindows.Select(floating =>
			{
				var overlayWindow = floating.CurrentDragService?.CurrentOverlayWindow as Window;
				var overlayOrigin = overlayWindow == null ? (Point?)null : GetNativeWindowOrigin(overlayWindow);
				var floatingOrigin = PlatformHelper.GetWindowContentOrigin(floating);
				return new
				{
				overlayLeft = overlayOrigin?.X,
				overlayTop = overlayOrigin?.Y,
				overlayWidth = (floating.CurrentDragService?.CurrentOverlayWindow as Window)?.ActualWidth,
				overlayHeight = (floating.CurrentDragService?.CurrentOverlayWindow as Window)?.ActualHeight,
				overlayBackground = (floating.CurrentDragService?.CurrentOverlayWindow as Window)?.Background?.ToString(),
				overlayAllowsTransparency = (floating.CurrentDragService?.CurrentOverlayWindow as Window)?.AllowsTransparency,
					overlaySourceType = DescribeOverlaySource(floating.CurrentDragService?.CurrentOverlayWindow as Window),
				menuBounds = CreateBoundsPayload("menu", null, mainMenu),
				managerBounds = CreateBoundsPayload("manager", null, dockManager),
					title = floating.Title,
					currentDropTarget = floating.CurrentDragService?.CurrentDropTargetType,
					previewGeometryBounds = GetLivePreviewScreenBounds(floating),
					left = floatingOrigin.X,
					top = floatingOrigin.Y,
					dragOffset = floating.PortableDragOffsetForDiagnostics,
					currentPointer = floating.CurrentPointerScreenPosition,
					nativeDrag = floating.PortableNativeDraggingForDiagnostics,
					dragging = floating.PortableDraggingForDiagnostics,
				};
			}).ToArray());
		}

		public string QueryNativeCursor()
		{
			var point = PlatformHelper.GetCursorPosition();
			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["x"] = point.X,
				["y"] = point.Y,
			});
		}

		public string CompleteCurrentDrop(string contentId)
		{
			var floating = FindVisibleFloatingWindow(contentId);
			if (floating == null || floating.CurrentDragService?.CurrentDropTarget == null)
				throw new InvalidOperationException($"No current drop target for floating content '{contentId}'.");

			var target = floating.CurrentDragService.CurrentDropTargetType;
			floating.CompletePortableDragForDiagnostics();
			return target;
		}

		private static Dictionary<string, object> GetLivePreviewScreenBounds(LayoutFloatingWindowControl floating)
		{
			var dragService = floating.CurrentDragService;
			if (dragService?.CurrentOverlayWindow is not OverlayWindow overlay || dragService.CurrentDropTarget == null)
				return null;

			var previewPath = dragService.CurrentDropTarget.GetPreviewPath(
				overlay,
				floating.Model as LayoutFloatingWindow);
			if (previewPath == null || previewPath.Bounds.IsEmpty)
				return null;

			var bounds = previewPath.Bounds;
			var overlayOrigin = GetNativeWindowOrigin(overlay);
			return new Dictionary<string, object>
			{
				["x"] = bounds.X + overlayOrigin.X,
				["y"] = bounds.Y + overlayOrigin.Y,
				["width"] = bounds.Width,
				["height"] = bounds.Height,
			};
		}

		public string HitTest(double screenX, double screenY)
		{
			var screenPoint = new Point(screenX, screenY);
			var managerPoint = PointFromScreenPortable(dockManager, screenPoint);
			DependencyObject hit = null;
			DependencyObject hitRoot = null;
			foreach (var root in GetAvalonDockVisualRoots())
			{
				if (root is not FrameworkElement rootElement || root is not Visual rootVisual)
					continue;

				Point rootPoint;
				try
				{
					rootPoint = PointFromScreenPortable(rootElement, screenPoint);
				}
				catch (InvalidOperationException)
				{
					continue;
				}

				if (rootPoint.X < 0 || rootPoint.Y < 0 || rootPoint.X > rootElement.ActualWidth || rootPoint.Y > rootElement.ActualHeight)
					continue;

				// InputHitTest does not descend past the DockingManager template's ContentPresenter in
				// this portable (LibreWPF/ProGPU) rendering backend - it returns the same shallow
				// ContentPresenter for every point, even pane centers. Resolve the hit geometrically
				// instead, using each element's own PointToScreen bounds (the same transform path
				// avd.query.bounds uses and which is reliable here, unlike TransformToDescendant): find
				// the deepest visible visual descendant whose screen rect contains the point. This
				// mirrors how drag handles are computed (from real element bounds), so a caller can
				// reliably verify a computed drag-start point actually lands on the intended AvalonDock
				// element (resizer, pane, caption) rather than a random position.
				hit = FindDeepestVisualContaining(rootElement, screenPoint) ?? rootElement;
				hitRoot = root;
				break;
			}

			var ancestors = new List<string>();
			string hitContentTitle = null;
			for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
			{
				// Which tab or pane the point is really on: a click on a clipped, overlapping tab can
				// land on its neighbour while still hitting "a TextBlock".
				if (hitContentTitle == null && current is FrameworkElement owner)
				{
					hitContentTitle = owner.DataContext switch
					{
						LayoutContent content => content.Title,
						LayoutItem item => item.LayoutElement is LayoutContent model ? model.Title : null,
						_ => null,
					};
				}

				ancestors.Add(current.GetType().FullName);
				if (ReferenceEquals(current, hitRoot))
					break;
			}

			return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
			{
				["screenX"] = screenX,
				["screenY"] = screenY,
				["managerX"] = managerPoint.X,
				["managerY"] = managerPoint.Y,
				["root"] = hitRoot?.GetType().FullName,
				["hit"] = hit?.GetType().FullName,
				["hitContentTitle"] = hitContentTitle,
				["ancestors"] = ancestors,
			});
		}

			public string QueryAnchorableTabs()
			{
				var tabs = FindVisualDescendants<LayoutAnchorableTabItem>(dockManager)
				.Select(tab => new Dictionary<string, object>
				{
					["contentId"] = tab.Model?.ContentId,
					["title"] = tab.Model?.Title,
					["isVisible"] = tab.IsVisible,
					["actualWidth"] = tab.ActualWidth,
					["actualHeight"] = tab.ActualHeight,
					["isHitTestableAtCenter"] = IsHitTestableAtCenter(tab),
					["bounds"] = CreateBoundsPayload("anchorable-tab", tab.Model?.ContentId, tab),
				})
				.ToArray();

				return System.Text.Json.JsonSerializer.Serialize(tabs);
			}

			public string QueryAnchorableDragSurfaces()
			{
				var roots = GetAvalonDockVisualRoots().ToArray();
				var titles = roots.SelectMany(FindVisualDescendants<AnchorablePaneTitle>)
					.Select(title => new Dictionary<string, object>
					{
						["contentId"] = title.Model?.ContentId,
						["title"] = title.Model?.Title,
						["isVisible"] = title.IsVisible,
						["isHitTestVisible"] = title.IsHitTestVisible,
						["actualWidth"] = title.ActualWidth,
						["actualHeight"] = title.ActualHeight,
						["isHitTestableAtCenter"] = IsHitTestableAtCenter(title),
						["bounds"] = CreateBoundsPayload("anchorable-title", title.Model?.ContentId, title),
					})
					.ToArray();
				var controls = roots.SelectMany(FindVisualDescendants<LayoutAnchorableControl>)
					.Select(control => new Dictionary<string, object>
					{
						["contentId"] = control.Model?.ContentId,
						["title"] = control.Model?.Title,
						["isVisible"] = control.IsVisible,
						["actualWidth"] = control.ActualWidth,
						["actualHeight"] = control.ActualHeight,
						["bounds"] = CreateBoundsPayload("anchorable-control", control.Model?.ContentId, control),
					})
					.ToArray();
				return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
				{
					["titles"] = titles,
					["controls"] = controls,
				});
			}

			private FrameworkElement FindAnchorablePaneResizer(string contentId)
		{
			var pane = FindVisualDescendant<LayoutAnchorablePaneControl>(
				dockManager,
				x => x.Model?.Descendents().OfType<LayoutAnchorable>()
					.Any(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal)) == true);
			if (pane == null)
			{
				return contentId == "dragTestTool"
					? FindVisualDescendants<LayoutGridResizerControl>(dockManager)
						.FirstOrDefault(r => r.IsVisible && r.ActualWidth > 0 && r.ActualHeight > 0)
					: null;
			}

			var paneLeft = pane.PointToScreen(new Point(0, 0)).X;
			var resizers = FindVisualDescendants<LayoutGridResizerControl>(dockManager)
				.Select(r => new
				{
					Resizer = r,
					CenterX = r.PointToScreen(new Point(r.ActualWidth / 2d, r.ActualHeight / 2d)).X
				})
				.Where(x => x.CenterX <= paneLeft + 2d)
				.OrderByDescending(x => x.CenterX)
				.Select(x => x.Resizer)
				.FirstOrDefault();
			return resizers ?? FindVisualDescendants<LayoutGridResizerControl>(dockManager)
				.FirstOrDefault(r => r.IsVisible && r.ActualWidth > 0 && r.ActualHeight > 0);
		}

		private static bool IsHitTestableAtCenter(FrameworkElement element)
		{
			if (element == null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
				return false;

			var center = new Point(element.ActualWidth / 2d, element.ActualHeight / 2d);
			var root = FindVisualRoot(element);
			if (root is not UIElement rootElement || root is not Visual rootVisual)
				return false;

			var pointInRoot = element.TransformToAncestor(rootVisual).Transform(center);
			var hit = rootElement.InputHitTest(pointInRoot) as DependencyObject;
			for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
			{
				if (ReferenceEquals(current, element))
					return true;
			}

			return false;
		}

		private static DependencyObject FindVisualRoot(DependencyObject element)
		{
			var current = element;
			while (VisualTreeHelper.GetParent(current) is { } parent)
				current = parent;
			return current;
		}

		private static Dictionary<string, object> CreateBoundsPayload(string target, string contentId, FrameworkElement element)
		{
			var result = new Dictionary<string, object>
			{
				["target"] = target,
				["contentId"] = contentId,
				["found"] = element != null,
			};

			if (element == null)
				return result;

			Point topLeft;
			Point bottomRight;
#if LIBREWPF
			if (element is Window window && ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) && host?.SilkWindow is { } silk)
			{
				topLeft = new Point(silk.Position.X, silk.Position.Y);
				bottomRight = new Point(silk.Position.X + silk.Size.X, silk.Position.Y + silk.Size.Y);
			}
			else
#endif
			{
				// All four corners: the side auto-hide tabs sit under a 90 degree LayoutTransform, so the
				// (0,0)-(w,h) diagonal alone yields a negative width or height there.
				var corners = new[]
				{
					PointToScreenPortable(element, new Point(0, 0)),
					PointToScreenPortable(element, new Point(element.ActualWidth, 0)),
					PointToScreenPortable(element, new Point(0, element.ActualHeight)),
					PointToScreenPortable(element, new Point(element.ActualWidth, element.ActualHeight)),
				};
				topLeft = new Point(corners.Min(c => c.X), corners.Min(c => c.Y));
				bottomRight = new Point(corners.Max(c => c.X), corners.Max(c => c.Y));
			}
			// What WPF itself thinks, next to the native frame the numbers below may come from: when the
			// two disagree the window was not resized to its content (compare issue #174).
			result["managedWidth"] = element.ActualWidth;
			result["managedHeight"] = element.ActualHeight;
			result["x"] = topLeft.X;
			result["y"] = topLeft.Y;
			result["width"] = bottomRight.X - topLeft.X;
			result["height"] = bottomRight.Y - topLeft.Y;
			if (TryFindHitTestableScreenPoint(element, out var hitPoint))
			{
				result["centerX"] = hitPoint.X;
				result["centerY"] = hitPoint.Y;
				result["hitTestPoint"] = true;
			}
			else
			{
				result["centerX"] = topLeft.X + (bottomRight.X - topLeft.X) / 2d;
				result["centerY"] = topLeft.Y + (bottomRight.Y - topLeft.Y) / 2d;
				result["hitTestPoint"] = false;
			}
			return result;
		}

		private static Point GetNativeWindowOrigin(Window window)
		{
#if LIBREWPF
			if (ProGpuWpfDiagnostics.TryGetWindowHost(window, out var host) && host?.SilkWindow is { } silk)
				return new Point(silk.Position.X, silk.Position.Y);
#endif
			// Not PointToScreenPortable: for a floating window that resolves the origin right back
			// through this method, which overflowed the stack on real WPF (0xC00000FD) as soon as a
			// floating window's drag handle was queried.
			try
			{
				return window.PointToScreen(new Point(0, 0));
			}
			catch (InvalidOperationException)
			{
				return new Point(window.Left, window.Top);
			}
		}

			private static bool TryFindHitTestableScreenPoint(FrameworkElement element, out Point screenPoint)
		{
			screenPoint = default;
			var root = FindVisualRoot(element);
			if (root is not UIElement rootElement || root is not Visual rootVisual)
				return false;

			var xs = new[] { 0.5, 0.25, 0.75, 0.1, 0.9 };
			var ys = new[] { 0.5, 0.25, 0.75, 0.1, 0.9 };
			foreach (var yRatio in ys)
			{
				foreach (var xRatio in xs)
				{
					var local = new Point(element.ActualWidth * xRatio, element.ActualHeight * yRatio);
					var pointInRoot = element.TransformToAncestor(rootVisual).Transform(local);
					var hit = rootElement.InputHitTest(pointInRoot) as DependencyObject;
					for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
					{
						if (!ReferenceEquals(current, element))
							continue;

						if (rootElement is FrameworkElement rootFrameworkElement)
						{
							screenPoint = PointToScreenPortable(rootFrameworkElement, pointInRoot);
							return true;
						}
					}
				}
			}

				return false;
			}

			private static Point PointToScreenPortable(FrameworkElement element, Point point)
			{
				var containingWindow = element as Window ?? Window.GetWindow(element);
				if (ReferenceEquals(containingWindow, s_positionedMainWindow) && s_positionedMainContentOrigin is { } mainOrigin)
				{
					var pointInWindow = ReferenceEquals(element, containingWindow)
						? point
						: element.TransformToAncestor(containingWindow).Transform(point);
					return new Point(mainOrigin.X + pointInWindow.X, mainOrigin.Y + pointInWindow.Y);
				}
				if (containingWindow is LayoutFloatingWindowControl floatingWindow)
				{
					var pointInWindow = ReferenceEquals(element, floatingWindow)
						? point
						: element.TransformToAncestor(floatingWindow).Transform(point);
					var nativeOrigin = GetNativeWindowOrigin(floatingWindow);
					return new Point(nativeOrigin.X + pointInWindow.X, nativeOrigin.Y + pointInWindow.Y);
				}

				try
				{
					return element.PointToScreen(point);
				}
				catch (InvalidOperationException)
				{
				}

				if (element is Window directWindow)
					return new Point(directWindow.Left + point.X, directWindow.Top + point.Y);

				throw new InvalidOperationException("Could not convert element point to screen coordinates.");
			}

			private static Point PointFromScreenPortable(UIElement element, Point point)
			{
				if (element is FrameworkElement floatingElement)
				{
					var containingWindow = floatingElement as Window ?? Window.GetWindow(floatingElement);
					if (ReferenceEquals(containingWindow, s_positionedMainWindow) &&
						s_positionedMainContentOrigin is { } mainOrigin)
					{
						var pointInWindow = new Point(point.X - mainOrigin.X, point.Y - mainOrigin.Y);
						return ReferenceEquals(floatingElement, containingWindow)
							? pointInWindow
							: containingWindow.TransformToDescendant(floatingElement).Transform(pointInWindow);
					}

					var floatingWindow = floatingElement as LayoutFloatingWindowControl
						?? Window.GetWindow(floatingElement) as LayoutFloatingWindowControl;
					if (floatingWindow != null)
					{
						var nativeOrigin = GetNativeWindowOrigin(floatingWindow);
						var pointInWindow = new Point(point.X - nativeOrigin.X, point.Y - nativeOrigin.Y);
						return ReferenceEquals(floatingElement, floatingWindow)
							? pointInWindow
							: floatingWindow.TransformToDescendant(floatingElement).Transform(pointInWindow);
					}
				}

				try
				{
					return element.PointFromScreen(point);
				}
				catch (InvalidOperationException)
				{
				}

				if (element is FrameworkElement frameworkElement)
				{
					var window = Window.GetWindow(frameworkElement);
					if (window is LayoutFloatingWindowControl && !ReferenceEquals(window, frameworkElement))
					{
						try
						{
							var pointInWindow = window.PointFromScreen(point);
							return window.TransformToDescendant(frameworkElement).Transform(pointInWindow);
						}
						catch (InvalidOperationException)
						{
						}
					}
				}

				if (element is Window directWindow)
					return new Point(point.X - directWindow.Left, point.Y - directWindow.Top);

				throw new InvalidOperationException("Could not convert screen point to element coordinates.");
			}

		// Deepest FrameworkElement whose own on-screen rect contains the screen point, across the whole
		// subtree. Uses each element's own PointToScreen (the transform path that is reliable in this
		// rendering backend) rather than TransformToDescendant. Preferring the DEEPEST match (rather
		// than the topmost z-order sibling) is deliberate: full-size but content-empty overlays such as
		// LayoutAutoHideWindowControl sit on top of the real panes and contain every point shallowly,
		// so a topmost-first walk would always resolve to the overlay and never the pane/resizer the
		// caller is actually verifying. Returns null when nothing qualifies.
		private static FrameworkElement FindDeepestVisualContaining(DependencyObject node, Point screenPoint)
		{
			return FindDeepestVisualContaining(node, screenPoint, 0).Element;
		}

		private static (FrameworkElement Element, int Depth) FindDeepestVisualContaining(
			DependencyObject node, Point screenPoint, int depth)
		{
			(FrameworkElement Element, int Depth) best = (null, -1);
			var count = VisualTreeHelper.GetChildrenCount(node);
			for (var i = 0; i < count; i++)
			{
				var childBest = FindDeepestVisualContaining(VisualTreeHelper.GetChild(node, i), screenPoint, depth + 1);
				if (childBest.Element != null && childBest.Depth > best.Depth)
					best = childBest;
			}

			if (best.Element != null)
				return best;

			if (node is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
			{
				try
				{
					var topLeft = PointToScreenPortable(fe, new Point(0, 0));
					var bottomRight = PointToScreenPortable(fe, new Point(fe.ActualWidth, fe.ActualHeight));
					if (new Rect(topLeft, bottomRight).Contains(screenPoint))
						return (fe, depth);
				}
				catch (InvalidOperationException)
				{
				}
			}

			return (null, -1);
		}

		private static T FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
			where T : DependencyObject
		{
			if (root is T current && predicate(current))
				return current;

			var count = VisualTreeHelper.GetChildrenCount(root);
			for (var i = 0; i < count; i++)
			{
				var found = FindVisualDescendant(VisualTreeHelper.GetChild(root, i), predicate);
				if (found != null)
					return found;
			}

			return null;
		}

		private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
			where T : DependencyObject
		{
			if (root is T current)
				yield return current;

			var count = VisualTreeHelper.GetChildrenCount(root);
			for (var i = 0; i < count; i++)
			{
				foreach (var found in FindVisualDescendants<T>(VisualTreeHelper.GetChild(root, i)))
					yield return found;
			}
		}

		private IEnumerable<DependencyObject> GetAvalonDockVisualRoots()
		{
			yield return dockManager;
			foreach (var floatingWindow in dockManager.FloatingWindows)
				yield return floatingWindow;
		}

		// Drag handle for a single-content floating anchorable's caption, which is a
		// DropDownControlArea in the floating window's own template rather than an
		// AnchorablePaneTitle (that control only exists for multi-tab docked panes).
		private FrameworkElement FindFloatingWindowCaption(string contentId)
		{
			var floating = FindVisibleFloatingWindow(contentId) as LayoutAnchorableFloatingWindowControl;
			if (floating == null)
				return null;

			return FindVisualDescendants<DropDownControlArea>(floating)
				.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0);
		}

			private FrameworkElement FindAnchorableTitle(string contentId)
			{
				return FindFloatingWindowCaption(contentId) ??
					GetAvalonDockVisualRoots()
						.SelectMany(FindVisualDescendants<AnchorablePaneTitle>)
					.Where(x => string.Equals(x.Model?.ContentId, contentId, StringComparison.Ordinal) ||
						x.FindVisualAncestor<LayoutAnchorablePaneControl>()?.Model?.Descendents().OfType<LayoutAnchorable>()
						.Any(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal)) == true)
						.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0);
			}

				private FrameworkElement FindDockedAnchorableDragHandle(string contentId)
				{
					var tab = FindVisualDescendants<LayoutAnchorableTabItem>(dockManager)
						.Where(x => MatchesAnchorableContent(x, contentId))
						.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0);
					if (tab != null)
						return tab;

					var title = FindVisualDescendants<AnchorablePaneTitle>(dockManager)
						.Where(x => string.Equals(x.Model?.ContentId, contentId, StringComparison.Ordinal) ||
							x.FindVisualAncestor<LayoutAnchorablePaneControl>()?.Model?.Descendents().OfType<LayoutAnchorable>()
								.Any(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal)) == true)
						.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0);
					if (title != null)
						return title;

					return FindVisualDescendants<LayoutAnchorableTabItem>(dockManager)
						.Where(x => MatchesAnchorableContent(x, contentId))
						.FirstOrDefault(x => x.IsVisible && x.ActualWidth > 0 && x.ActualHeight > 0);
				}

				private static bool MatchesAnchorableContent(LayoutAnchorableTabItem tab, string contentId)
				{
					return string.Equals((tab.Model as LayoutAnchorable)?.ContentId, contentId, StringComparison.Ordinal) ||
						tab.FindVisualAncestor<LayoutAnchorablePaneControl>()?.Model?.Descendents().OfType<LayoutAnchorable>()
							.Any(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal)) == true;
				}

			private LayoutFloatingWindowControl FindVisibleFloatingWindow(string contentId)
		{
			return dockManager.FloatingWindows
				.Where(fw => fw.IsLoaded && fw.IsVisible &&
					fw.Model?.Descendents().OfType<LayoutAnchorable>()
						.Any(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal)) == true)
				.LastOrDefault();
		}

		public string NewFloatingWindow(string title = null)
		{
			var anchorable = new LayoutAnchorable
			{
				Title = title ?? "Floating window",
				ContentId = $"float-{Guid.NewGuid():N}",
				Content = new TestUserControl()
			};
			anchorable.AddToLayout(dockManager, AnchorableShowStrategy.Most);
			anchorable.Float();
			return $"Created floating '{anchorable.ContentId}'";
		}

		// WindowInteropHelper(window).Handle is a Win32-shaped HWND surrogate; it is never the real
		// NSWindow* pointer on macOS, so comparing it against NSApplication.orderedWindows (an array
		// of actual NSWindow* Objective-C objects) in TryGetMacWindowOrder could never match. Resolve
		// the genuine Cocoa window pointer via the LibreWPF/Silk.NET native window instead. Linux has
		// the same mismatch: the surrogate is a PortablePresentationSource counter value, so the X11
		// window id has to come from the native window too (see X11WindowStacking).
		private static IntPtr GetNativeWindowHandle(Window window)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return PlatformHelper.GetNativeWindowHandle(window);

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				var xid = X11WindowStacking.TryGetWindowId(window);
				if (xid != IntPtr.Zero)
					return xid;
			}

			return new WindowInteropHelper(window).Handle;
		}

		private static bool TryGetPlatformWindowZOrder(IntPtr hwnd, out int zOrder)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return TryGetMacWindowOrder(hwnd, out zOrder);

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				return X11WindowStacking.TryGetStackIndex(hwnd, out zOrder);

			return TryGetWindowZOrder(hwnd, out zOrder);
		}

		private static bool IsPlatformZOrderAbove(int candidateZOrder, int referenceZOrder)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return candidateZOrder < referenceZOrder;

			// Win32 (walked from GW_HWNDLAST upwards) and _NET_CLIENT_LIST_STACKING (published
			// bottom-to-top) share the same convention: the larger index is nearer the front.
			return candidateZOrder > referenceZOrder;
		}

		private static bool TryGetMacWindowOrder(IntPtr window, out int zOrder)
		{
			try
			{
				var app = ObjCMsgSend(NsApplicationClass, _selSharedApplication);
				var windows = ObjCMsgSend(app, _selOrderedWindows);
				var count = ObjCMsgSendRetNUInt(windows, _selCount);
				for (nuint i = 0; i < count; i++)
				{
					if (ObjCMsgSendNUInt(windows, _selObjectAtIndex, i) == window)
					{
						zOrder = checked((int)i);
						return true;
					}
				}
			}
			catch
			{
			}

			zOrder = int.MinValue;
			return false;
		}

		private static bool TryGetWindowZOrder(IntPtr hwnd, out int zOrder)
		{
			var lowestHwnd = GetWindow(hwnd, (uint)GetWindowCmd.GW_HWNDLAST);
			var z = 0;
			var current = lowestHwnd;
			while (current != IntPtr.Zero)
			{
				if (current == hwnd)
				{
					zOrder = z;
					return true;
				}

				current = GetWindow(current, (uint)GetWindowCmd.GW_HWNDPREV);
				z++;
			}

			zOrder = int.MinValue;
			return false;
		}

		public string CloseContent(string contentIdOrTitle)
		{
			var content = dockManager.Layout.Descendents()
				.OfType<LayoutContent>()
				.FirstOrDefault(c =>
					string.Equals(c.ContentId, contentIdOrTitle, StringComparison.Ordinal)
					|| string.Equals(c.Title, contentIdOrTitle, StringComparison.Ordinal));
			if (content == null)
				return System.Text.Json.JsonSerializer.Serialize($"Content '{contentIdOrTitle}' not found");

			// CanClose is the sample's own contract (WinForms Window is CanClose=False), so a close
			// attempt on it must report that instead of removing it.
			if (!content.CanClose)
				return System.Text.Json.JsonSerializer.Serialize($"'{content.Title}' cannot be closed (CanClose=false)");

			switch (content)
			{
				case LayoutDocument document:
					document.Close();
					break;
				case LayoutAnchorable anchorable:
					anchorable.Close();
					break;
			}

			Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
			return QueryLayout();
		}
    }
}
