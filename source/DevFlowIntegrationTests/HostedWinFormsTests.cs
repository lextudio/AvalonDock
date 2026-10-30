using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace AvalonDock.DevFlowIntegrationTests
{
	/// <summary>
	/// The interop pane (<c>WindowsForms Host</c>) under LibreWPF's visual <c>WindowsFormsHost</c>: text
	/// reaches the hosted WinForms control, Tab walks that control's own tab order and is left at its last
	/// tab stop, a mnemonic fires its button, and a click aimed from the reported geometry lands on the
	/// control the geometry describes even when the host is scaled.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the only suite that drives the hosted control tree: the docking cases use the pane as a
	/// layout element and never touch its content. The behaviours here are the ones the visual host
	/// implements for it - pointer conversion, keyboard and text forwarding, Tab order and mnemonic
	/// matching - so a failure names the host, not AvalonDock. What it paints is not covered: under the
	/// managed renderer the host draws no pixels at all (docs/librewpf.md, 11.9).
	/// </para>
	/// <para>
	/// Every case here needs the visual host: LibreWPF draws the hosted tree itself, so keyboard, text
	/// and pointer input arrive as WPF events this suite can drive, and a layout transform scales what is
	/// drawn. A build that hands the pane to a native child window instead (real WPF) takes input through
	/// Win32 and is moved but not scaled by a layout transform; the cases skip on the host report
	/// (<c>visualHost</c> in <c>avd.winforms.tree</c>, which says which host this build has) rather than
	/// on the OS, because the LibreWPF line also runs on Windows. Not covered: Shift+Tab (the agent cannot
	/// hold a modifier, and the host chooses the direction from <c>Keyboard.Modifiers</c>), a real
	/// Alt+letter through the WPF side (the mnemonic case reaches the same WinForms matching directly),
	/// and painting.
	/// </para>
	/// </remarks>
	[Collection("DevFlow")]
	public sealed class HostedWinFormsTests : PortedTestAppTestBase
	{
		private const string PaneContentId = "WinFormsWindow";

		/// <summary>Skips the case unless the pane is hosted by the visual host this suite drives.</summary>
		private static async Task SkipWithoutVisualHostAsync(DevFlowClient client, string reason)
		{
			using var tree = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.tree"));
			if (!ReadBool(tree.RootElement, "visualHost"))
				Assert.Skip(reason);
		}

		private const string KeyboardOnNativeHost =
			"this build hosts the pane in a native child window that takes keyboard and text input through Win32; the agent's WPF-level key and text events do not reach it";
		private const string PaneTitle = "WinForms Window";

		public HostedWinFormsTests(TestAppFixture fixture)
			: base(fixture)
		{
		}

		[Fact]
		public async Task TextInput_ReachesHostedTextBox()
		{
			// The cases click, which is OS-level pointer input on some paths (see NativeInputEnvironment).
			NativeInputEnvironment.EnsureNativeDragAvailable();
			using var client = await TryConnectAsync();
			await SkipWithoutVisualHostAsync(client, KeyboardOnNativeHost);

			await RunWithFailureArtifactsAsync(client, nameof(TextInput_ReachesHostedTextBox), async () =>
			{
				await ActivatePaneAsync(client);

				await ClickHostedControlAsync(client, "textBox1");
				await WaitForFocusedControlAsync(client, "textBox1");

				await client.InvokeAsync("avd.input.text", "Hello WinForms");

				await WaitUntilAsync(
					async () => HostedControlText(await QueryWinFormsAsync(client), "textBox1")?.Contains("Hello WinForms", StringComparison.Ordinal) == true,
					TimeSpan.FromSeconds(5),
					"Text typed at the WPF host did not reach the hosted WinForms text box.");

				await AssertAppAliveAsync(client);
			});
		}

		[Fact]
		public async Task TabKey_MovesThroughHostedControlsAndLeavesTheHost()
		{
			NativeInputEnvironment.EnsureNativeDragAvailable();
			using var client = await TryConnectAsync();
			await SkipWithoutVisualHostAsync(client, KeyboardOnNativeHost);

			await RunWithFailureArtifactsAsync(client, nameof(TabKey_MovesThroughHostedControlsAndLeavesTheHost), async () =>
			{
				await ActivatePaneAsync(client);

				await ClickHostedControlAsync(client, "textBox1");
				await WaitForFocusedControlAsync(client, "textBox1");

				// Forward through the hosted tab order: textBox1 -> textBox2 -> mnemonicButton. Each of those
				// is handled by the host, so WPF does not move its focus.
				Assert.True(await TabAsync(client, "Tab"), "Tab inside the host reported itself unhandled before the last tab stop.");
				await WaitForFocusedControlAsync(client, "textBox2");
				Assert.True(await TabAsync(client, "Tab"), "Tab inside the host reported itself unhandled before the last tab stop.");
				await WaitForFocusedControlAsync(client, "mnemonicButton");

				// The last tab stop does not wrap: the host leaves the key unhandled. That is the difference
				// to WinForms' own container, which cycles. Leave the rest to WPF: its tab traversal runs in
				// InputManager's translate-accelerator stage, which a routed key event raised by the agent
				// never reaches, so the focus after it is not observable here - only the signal is.
				Assert.False(await TabAsync(client, "Tab"), "Tab at the last tab stop was handled, so focus can never leave the host.");
			});
		}

		[Fact]
		public async Task Mnemonic_InvokesHostedButton()
		{
			NativeInputEnvironment.EnsureNativeDragAvailable();
			using var client = await TryConnectAsync();
			await SkipWithoutVisualHostAsync(client, KeyboardOnNativeHost);

			await RunWithFailureArtifactsAsync(client, nameof(Mnemonic_InvokesHostedButton), async () =>
			{
				await ActivatePaneAsync(client);

				await ClickHostedControlAsync(client, "textBox1");
				await WaitForFocusedControlAsync(client, "textBox1");

				// The marker must not be there already, or a leftover from an earlier step would pass this case.
				Assert.DoesNotContain(
					"mnemonic: clicked",
					HostedControlText(await QueryWinFormsAsync(client), "label2") ?? string.Empty,
					StringComparison.Ordinal);

				// This reaches the WinForms side of a mnemonic - the system-character path that runs
				// ProcessMnemonic - directly. It does not go through the WPF host's OnMnemonicCore, which only
				// HwndSource calls and which the agent cannot drive (no chords), so the WPF routing of
				// Alt+letter stays unverified. A build whose host cannot be reached this way says so instead
				// of pretending, so that is a failure rather than a silent pass.
				using var result = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.mnemonic", "M"));
				Assert.False(
					result.RootElement.TryGetProperty("supported", out var supported)
						&& supported.ValueKind == JsonValueKind.False,
					ReadString(result.RootElement, "reason") ?? "the mnemonic action refused to run on this build");

				// The button's text starts with "&M", so ProcessMnemonic matches "M" and runs its Click
				// handler, which writes a marker into label2. That marker is the only observable proof.
				await WaitUntilAsync(
					async () => HostedControlText(await QueryWinFormsAsync(client), "label2")?.Contains("mnemonic: clicked", StringComparison.Ordinal) == true,
					TimeSpan.FromSeconds(5),
					"The hosted button's mnemonic did not run its Click handler.");
			});
		}

		[Fact]
		public async Task ScaledHost_ClickLandsOnTheControlItsBoundsDescribe()
		{
			NativeInputEnvironment.EnsureNativeDragAvailable();
			using var client = await TryConnectAsync();
			await SkipWithoutVisualHostAsync(
				client,
				"this build hosts the pane in a native child window, which a layout transform moves but does not scale");

			await RunWithFailureArtifactsAsync(client, nameof(ScaledHost_ClickLandsOnTheControlItsBoundsDescribe), async () =>
			{
				await ActivatePaneAsync(client);

				// pixels per unit already contains the DPI scale (it is 2 on a Retina display at 1x), so the
				// baseline is measured rather than assumed: otherwise a scale that silently did nothing would
				// pass. It is polled, because the factor comes from an arranged layout pass.
				double unitAtOne = 0;
				await WaitUntilAsync(
					async () =>
					{
						using var unscaled = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.scale", 1d, 1d));
						unitAtOne = unscaled.RootElement.GetProperty("pixelsPerUnitX").GetDouble();
						return unitAtOne > 0;
					},
					TimeSpan.FromSeconds(5),
					"The host reported no pixels per unit at 1x, so there is nothing to scale.");

				// The application is restarted before every case, so the scale does not have to be put back.
				double pixelsPerUnit = 0;
				string lastScale = null;
				await WaitUntilAsync(
					async () =>
					{
						using var scaled = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.scale", 2d, 2d));
						pixelsPerUnit = scaled.RootElement.GetProperty("pixelsPerUnitX").GetDouble();
						lastScale = scaled.RootElement.GetRawText();
						return pixelsPerUnit > unitAtOne * 1.5;
					},
					TimeSpan.FromSeconds(5),
					$"The host did not take the layout scale: pixels per unit stayed at {unitAtOne}. Scale={lastScale}");

				// The bounds are computed with that same factor, so a click aimed at their centre has to land
				// on textBox2. A mismatch between the drawing factor and the pointer factor lands elsewhere,
				// which is exactly what this catches.
				await ClickHostedControlAsync(client, "textBox2");
				await WaitForFocusedControlAsync(client, "textBox2");
			});
		}

		[Fact]
		public async Task ManagedRenderer_PaintsTheHostedTreeAsATexture()
		{
			NativeInputEnvironment.EnsureNativeDragAvailable();
			using var client = await TryConnectAsync();
			await SkipWithoutVisualHostAsync(client, KeyboardOnNativeHost);

			await RunWithFailureArtifactsAsync(client, nameof(ManagedRenderer_PaintsTheHostedTreeAsATexture), async () =>
			{
				await ActivatePaneAsync(client);

				using var paint = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.paint"));
				Assert.True(
					ReadBool(paint.RootElement, "hasRenderData"),
					$"The host recorded no render data, so the pane draws nothing. Paint={paint.RootElement.GetRawText()}");

				var images = ReadArray(paint.RootElement, "images");
				Assert.True(
					images.Any(),
					$"The host's retained content does not depend on any image, so the hosted tree is not drawn. Paint={paint.RootElement.GetRawText()}");

				// A portable native image is drawn from a backend texture, so the pixels never come back to
				// the CPU; a pixel bitmap here would mean the tree is copied through managed memory.
				var image = images.First();
				Assert.True(
					ReadBool(image, "portable"),
					$"The host draws the tree from a CPU bitmap rather than a backend texture. Image={image.GetRawText()}");

				// The texture is the control tree in WinForms pixels: one pixel per WinForms pixel, so the
				// host's DIP size times the factor the pointer conversion uses. Fewer pixels would mean the
				// drawing is upscaled from a coarser surface.
				using var tree = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.tree"));
				var pixelsPerUnit = tree.RootElement.GetProperty("pixelsPerUnitX").GetDouble();
				var hostWidth = paint.RootElement.GetProperty("hostWidthInDip").GetDouble();
				var pixelWidth = image.GetProperty("pixelWidth").GetInt32();
				Assert.True(
					pixelsPerUnit > 0 && hostWidth > 0,
					$"The host reports no size to compare the painted texture with. Tree={tree.RootElement.GetRawText()}");
				Assert.InRange(pixelWidth, hostWidth * pixelsPerUnit - 1, hostWidth * pixelsPerUnit + 1);
			});
		}

		/// <summary>Makes the interop pane active, frontmost and wide enough that its content is not clipped.</summary>
		private async Task ActivatePaneAsync(DevFlowClient client)
		{
			// The pane is declared 50 wide and the hosted control is 425 pixels wide, so a click aimed at
			// its centre would land outside the pane without this.
			using (var widened = JsonDocument.Parse(await client.InvokeAsync("avd.pane.set-width", PaneContentId, 480)))
			{
				Assert.True(
					ReadBool(widened.RootElement, "changed"),
					$"The '{PaneTitle}' pane could not be widened, so a click aimed at its content would land outside it. Width={widened.RootElement.GetRawText()}");
			}

			// The auto-hide flyout is an overlay across the panes: while it is open a click aimed at the
			// interop pane lands in the flyout, which is what made this case fail on windows-x64 only.
			await DismissAutoHideFlyoutAsync(client);

			// A pane with a single anchorable shows a title bar rather than a tab strip, so there is no
			// tab to click: the pane is visible at startup and stays visible after being widened.
			//
			// The clicks below are native OS pointer input at screen coordinates, so the window has to be
			// in front; with another window over it the click would land on that one and the case would
			// fail for a reason that has nothing to do with the host.
			await IsolateDesktopForNativeInputAsync();
			await WaitUntilAsync(
				async () =>
				{
					using var active = JsonDocument.Parse(await client.InvokeAsync("avd.activate"));
					return ReadBool(active.RootElement, "isActive");
				},
				TimeSpan.FromSeconds(15),
				"The test window could not be brought to the front, so a native click cannot reach the interop pane.");

			await WaitUntilAsync(
				async () =>
				{
					using var tree = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.tree"));
					return tree.RootElement.GetProperty("hosted").GetBoolean()
						&& ReadArray(tree.RootElement, "controls").Any();
				},
				TimeSpan.FromSeconds(10),
				$"The '{PaneTitle}' pane hosts no WinForms control tree (DEVFLOW_HOST_WINFORMS=0?).");
		}

		/// <summary>Closes the auto-hide flyout and fails if it is still covering the panes.</summary>
		private static async Task DismissAutoHideFlyoutAsync(DevFlowClient client)
		{
			using var dismissed = JsonDocument.Parse(await client.InvokeAsync("avd.autohide.dismiss"));
			Assert.False(
				ReadBool(dismissed.RootElement, "open"),
				$"The auto-hide flyout is still open after being dismissed, so it covers the interop pane. Dismiss={dismissed.RootElement.GetRawText()}");
		}

		/// <summary>
		/// Clicks the centre of a hosted control until it takes focus. A click can miss even when the
		/// geometry is right - the first click on a window that is not the foreground window is consumed
		/// by activation, and anything else that moves the layout between the query and the click moves
		/// the point - so the geometry is read again for the second attempt instead of being reused.
		/// </summary>
		private static async Task ClickHostedControlAsync(DevFlowClient client, string name)
		{
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				await DismissAutoHideFlyoutAsync(client);
				if (await ClickOnceAsync(client, name))
				{
					return;
				}
			}

			Assert.Fail($"'{name}' did not take focus after two clicks; see the failure artifacts for the screen and the hit target.");
		}

		private static async Task<bool> ClickOnceAsync(DevFlowClient client, string name)
		{
			using var bounds = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.bounds", name));
			Assert.True(
				bounds.RootElement.GetProperty("found").GetBoolean(),
				$"The hosted control '{name}' was not found. Bounds={bounds.RootElement.GetRawText()}");
			Assert.True(
				bounds.RootElement.GetProperty("hitTestPoint").GetBoolean(),
				$"'{name}' cannot be clicked where its bounds point: the host reports no pixels per unit (nothing laid out yet), or the control's centre is outside the pane. Bounds={bounds.RootElement.GetRawText()}");

			await client.ClickAsync(
				bounds.RootElement.GetProperty("centerX").GetDouble(),
				bounds.RootElement.GetProperty("centerY").GetDouble(),
				TestContext.Current.CancellationToken);

			return await IsActiveControlAsync(client, name, TimeSpan.FromSeconds(2));
		}

		/// <summary>Whether the hosted container's active control is <paramref name="name"/> within a timeout.</summary>
		private static async Task<bool> IsActiveControlAsync(DevFlowClient client, string name, TimeSpan timeout)
		{
			try
			{
				await WaitUntilAsync(
					async () =>
					{
						var tree = await QueryWinFormsAsync(client);
						return string.Equals(ReadString(tree, "activeControl"), name, StringComparison.Ordinal);
					},
					timeout,
					$"'{name}' did not become the hosted container's active control.");
				return true;
			}
			catch (Xunit.Sdk.XunitException)
			{
				return false;
			}
		}

		/// <summary>
		/// Waits for the hosted container's active control to be <paramref name="name"/>. The container's
		/// active control is used rather than <c>Control.Focused</c>, which also requires the WPF window to
		/// be the active window.
		/// </summary>
		private static async Task WaitForFocusedControlAsync(DevFlowClient client, string name)
		{
			if (await IsActiveControlAsync(client, name, TimeSpan.FromSeconds(5)))
			{
				return;
			}

			Assert.Fail($"'{name}' did not become the hosted container's active control.");
		}

		/// <summary>Raises one Tab at the focused element and reports whether the host handled it.</summary>
		private static async Task<bool> TabAsync(DevFlowClient client, string key)
		{
			using var result = JsonDocument.Parse(await client.InvokeAsync("avd.input.key", key));
			return result.RootElement.GetProperty("handled").GetBoolean();
		}

		private static async Task<JsonElement> QueryWinFormsAsync(DevFlowClient client)
		{
			using var document = JsonDocument.Parse(await client.InvokeAsync("avd.winforms.tree"));
			return document.RootElement.Clone();
		}

		private static string HostedControlText(JsonElement tree, string name)
			=> ReadArray(tree, "controls")
				.Where(c => string.Equals(ReadString(c, "name"), name, StringComparison.Ordinal))
				.Select(c => ReadString(c, "text"))
				.FirstOrDefault();
	}
}
