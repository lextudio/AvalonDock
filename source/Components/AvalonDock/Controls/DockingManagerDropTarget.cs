using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AvalonDock.Layout;

namespace AvalonDock.Controls
{
	/// <summary>
	/// Represents the docking manager drop target.
	/// </summary>
	internal class DockingManagerDropTarget : DropTarget<DockingManager>
	{
		private DockingManager _manager;

		// Temporary diagnostics for the Linux preview-geometry investigation: the numbers behind the
		// last preview rectangle, so a mismatching preview can be read against what produced it.
		internal static string PreviewDiagnosticsForLastPreview { get; private set; }
		internal static double PreviewInsetXForDiagnostics { get; private set; }
		internal static double PreviewInsetYForDiagnostics { get; private set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="DockingManagerDropTarget"/> class.
		/// </summary>
		/// <param name="manager">The manager.</param>
		/// <param name="detectionRect">The detection rectangle.</param>
		/// <param name="type">The drop target type.</param>
		internal DockingManagerDropTarget(
			DockingManager manager,
			Rect detectionRect,
			DropTargetType type)
			: base(manager, detectionRect, type)
		{
			_manager = manager;
		}

		/// <inheritdoc/>
		protected override void Drop(LayoutAnchorableFloatingWindow floatingWindow)
		{
			switch (Type)
			{
				case DropTargetType.DockingManagerDockLeft:
					{
						if (_manager.Layout.RootPanel.Orientation != System.Windows.Controls.Orientation.Horizontal &&
							_manager.Layout.RootPanel.Children.Count == 1)
							_manager.Layout.RootPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;

						if (_manager.Layout.RootPanel.Orientation == System.Windows.Controls.Orientation.Horizontal)
						{
							var layoutAnchorablePaneGroup = floatingWindow.RootPanel as LayoutAnchorablePaneGroup;
							if (layoutAnchorablePaneGroup != null &&
								layoutAnchorablePaneGroup.Orientation == System.Windows.Controls.Orientation.Horizontal)
							{
								var childrenToTransfer = layoutAnchorablePaneGroup.Children.ToArray();
								for (int i = 0; i < childrenToTransfer.Length; i++)
									_manager.Layout.RootPanel.Children.Insert(i, childrenToTransfer[i]);
							}
							else
							{
								_manager.Layout.RootPanel.Children.Insert(0, floatingWindow.RootPanel);
							}
						}
						else
						{
							var newOrientedPanel = new LayoutPanel()
							{
								Orientation = System.Windows.Controls.Orientation.Horizontal
							};

							newOrientedPanel.Children.Add(floatingWindow.RootPanel);
							newOrientedPanel.Children.Add(_manager.Layout.RootPanel);

							_manager.Layout.RootPanel = newOrientedPanel;
						}
					}

					break;

				case DropTargetType.DockingManagerDockRight:
					{
						if (_manager.Layout.RootPanel.Orientation != System.Windows.Controls.Orientation.Horizontal &&
							_manager.Layout.RootPanel.Children.Count == 1)
							_manager.Layout.RootPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;

						if (_manager.Layout.RootPanel.Orientation == System.Windows.Controls.Orientation.Horizontal)
						{
							var layoutAnchorablePaneGroup = floatingWindow.RootPanel as LayoutAnchorablePaneGroup;
							if (layoutAnchorablePaneGroup != null &&
								layoutAnchorablePaneGroup.Orientation == System.Windows.Controls.Orientation.Horizontal)
							{
								var childrenToTransfer = layoutAnchorablePaneGroup.Children.ToArray();
								for (int i = 0; i < childrenToTransfer.Length; i++)
									_manager.Layout.RootPanel.Children.Add(childrenToTransfer[i]);
							}
							else
							{
								_manager.Layout.RootPanel.Children.Add(floatingWindow.RootPanel);
							}
						}
						else
						{
							var newOrientedPanel = new LayoutPanel()
							{
								Orientation = System.Windows.Controls.Orientation.Horizontal
							};

							newOrientedPanel.Children.Add(floatingWindow.RootPanel);
							newOrientedPanel.Children.Insert(0, _manager.Layout.RootPanel);

							_manager.Layout.RootPanel = newOrientedPanel;
						}
					}

					break;

				case DropTargetType.DockingManagerDockTop:
					{
						if (_manager.Layout.RootPanel.Orientation != System.Windows.Controls.Orientation.Vertical &&
							_manager.Layout.RootPanel.Children.Count == 1)
							_manager.Layout.RootPanel.Orientation = System.Windows.Controls.Orientation.Vertical;

						if (_manager.Layout.RootPanel.Orientation == System.Windows.Controls.Orientation.Vertical)
						{
							var layoutAnchorablePaneGroup = floatingWindow.RootPanel as LayoutAnchorablePaneGroup;
							if (layoutAnchorablePaneGroup != null &&
								layoutAnchorablePaneGroup.Orientation == System.Windows.Controls.Orientation.Vertical)
							{
								var childrenToTransfer = layoutAnchorablePaneGroup.Children.ToArray();
								for (int i = 0; i < childrenToTransfer.Length; i++)
									_manager.Layout.RootPanel.Children.Insert(i, childrenToTransfer[i]);
							}
							else
							{
								_manager.Layout.RootPanel.Children.Insert(0, floatingWindow.RootPanel);
							}
						}
						else
						{
							var newOrientedPanel = new LayoutPanel()
							{
								Orientation = System.Windows.Controls.Orientation.Vertical
							};

							newOrientedPanel.Children.Add(floatingWindow.RootPanel);
							newOrientedPanel.Children.Add(_manager.Layout.RootPanel);

							_manager.Layout.RootPanel = newOrientedPanel;
						}
					}

					break;

				case DropTargetType.DockingManagerDockBottom:
					{
						if (_manager.Layout.RootPanel.Orientation != System.Windows.Controls.Orientation.Vertical &&
							_manager.Layout.RootPanel.Children.Count == 1)
							_manager.Layout.RootPanel.Orientation = System.Windows.Controls.Orientation.Vertical;

						if (_manager.Layout.RootPanel.Orientation == System.Windows.Controls.Orientation.Vertical)
						{
							var layoutAnchorablePaneGroup = floatingWindow.RootPanel as LayoutAnchorablePaneGroup;
							if (layoutAnchorablePaneGroup != null &&
								layoutAnchorablePaneGroup.Orientation == System.Windows.Controls.Orientation.Vertical)
							{
								var childrenToTransfer = layoutAnchorablePaneGroup.Children.ToArray();
								for (int i = 0; i < childrenToTransfer.Length; i++)
									_manager.Layout.RootPanel.Children.Add(childrenToTransfer[i]);
							}
							else
							{
								_manager.Layout.RootPanel.Children.Add(floatingWindow.RootPanel);
							}
						}
						else
						{
							var newOrientedPanel = new LayoutPanel()
							{
								Orientation = System.Windows.Controls.Orientation.Vertical
							};

							newOrientedPanel.Children.Add(floatingWindow.RootPanel);
							newOrientedPanel.Children.Insert(0, _manager.Layout.RootPanel);

							_manager.Layout.RootPanel = newOrientedPanel;
						}
					}

					break;
			}

			base.Drop(floatingWindow);
		}

		/// <inheritdoc/>
		public override Geometry GetPreviewPath(
			OverlayWindow overlayWindow,
			LayoutFloatingWindow floatingWindowModel)
		{
			var anchorableFloatingWindowModel = floatingWindowModel as LayoutAnchorableFloatingWindow;
			var layoutAnchorablePane = anchorableFloatingWindowModel.RootPanel as ILayoutPositionableElement;
			var layoutAnchorablePaneWithActualSize = anchorableFloatingWindowModel.RootPanel as ILayoutPositionableElementWithActualSize;

			var targetScreenRect = TargetElement.GetScreenArea();

			// The docked content does not necessarily start at the DockingManager's own origin: some
			// backends render the template's empty side-panel slots with a nonzero size (LibreWPF
			// gives the empty Auto columns/rows a 6px minimum), so the pane after a drop is inset
			// from the manager's edge. Measure the actual rendered content area and preview against
			// it, so the preview rectangle lands exactly where the docked pane will be. The offset
			// is guarded so a backend that does not support this measurement simply keeps the
			// unadjusted manager area.
			if (TargetElement.LayoutRootPanel is FrameworkElement rootPanel && rootPanel.IsVisible)
			{
				try
				{
					var contentScreenOrigin = rootPanel.PointToScreen(new Point(0, 0));
					var managerScreenOrigin = TargetElement.PointToScreen(new Point(0, 0));
					var dx = contentScreenOrigin.X - managerScreenOrigin.X;
					var dy = contentScreenOrigin.Y - managerScreenOrigin.Y;
					PreviewInsetXForDiagnostics = dx;
					PreviewInsetYForDiagnostics = dy;
					if (dx > 0 || dy > 0)
					{
						targetScreenRect.X += dx;
						targetScreenRect.Y += dy;
						targetScreenRect.Width -= dx * 2;
						targetScreenRect.Height -= dy * 2;
					}
				}
				catch
				{
					// Measurement unavailable - fall back to the unadjusted manager area.
				}
			}

			// Preferred dock size used by the outer-edge rules: width for Left/Right, height for Top/Bottom.
			// The rendered actual size is preferred, but on backends where a Star pane is not yet laid
			// out (LibreWPF collapses it to its minimum while a floating window is being dragged) it is
			// unusable, so fall back to the floating window's size kept on the model - otherwise the
			// preview box collapses to a sliver that never matches the pane after the drop.
			var forVertical = Type == DropTargetType.DockingManagerDockTop || Type == DropTargetType.DockingManagerDockBottom;
			var dockSize = forVertical ? layoutAnchorablePane.DockHeight : layoutAnchorablePane.DockWidth;
			var actualSize = forVertical ? layoutAnchorablePaneWithActualSize.ActualHeight : layoutAnchorablePaneWithActualSize.ActualWidth;
			var minSize = forVertical ? layoutAnchorablePane.CalculatedDockMinHeight() : layoutAnchorablePane.CalculatedDockMinWidth();
			var floatingSize = forVertical ? layoutAnchorablePaneWithActualSize.FloatingHeight : layoutAnchorablePaneWithActualSize.FloatingWidth;
			var preferredSize = dockSize.IsAbsolute
				? dockSize.Value
				: (actualSize > minSize ? actualSize : floatingSize);

			if (OverlayPreviewRules.TryComputeManagerPreviewRect(
				Type,
				targetScreenRect.Width,
				targetScreenRect.Height,
				preferredSize,
				out var left,
				out var top,
				out var width,
				out var height))
			{
				var previewBoxRect = new Rect(
					targetScreenRect.Left - overlayWindow.Left + left,
					targetScreenRect.Top - overlayWindow.Top + top,
					width,
					height);

				PreviewDiagnosticsForLastPreview = string.Format(
					System.Globalization.CultureInfo.InvariantCulture,
					"type={0} inset={1:F1},{2:F1} area={3:F1},{4:F1} {5:F1}x{6:F1} preferred={7:F1} " +
					"(dock={8} actual={9:F1} min={10:F1} floating={11:F1}) " +
					"box={12:F1},{13:F1} {14:F1}x{15:F1} overlay={16:F1},{17:F1}",
					Type,
					PreviewInsetXForDiagnostics,
					PreviewInsetYForDiagnostics,
					targetScreenRect.X,
					targetScreenRect.Y,
					targetScreenRect.Width,
					targetScreenRect.Height,
					preferredSize,
					dockSize,
					actualSize,
					minSize,
					floatingSize,
					previewBoxRect.X,
					previewBoxRect.Y,
					previewBoxRect.Width,
					previewBoxRect.Height,
					overlayWindow.Left,
					overlayWindow.Top);

				return new RectangleGeometry(previewBoxRect);
			}

			throw new InvalidOperationException();
		}
	}
}