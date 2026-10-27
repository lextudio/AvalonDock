using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace AvalonDock.DevFlowIntegrationTests
{
	/// <summary>
	/// Context-menu regressions (#193 disabled items, #200 <c>CompositeCollection</c> binding,
	/// #234 disabled items in the drop-down, #223 theme styling), ported from the FlaUI
	/// <c>ContextMenuTests</c> fixture.
	/// </summary>
	/// <remarks>
	/// TestApp does not assign <c>DockingManager.AnchorableContextMenu</c> or <c>DocumentContextMenu</c>,
	/// so the FlaUI originals of the two right-click cases could only assert "the app did not crash":
	/// there was no menu to inspect. The same is true here, and saying so is more useful than pretending
	/// otherwise - what these cases still guard is that raising the right-button event on a tab (the
	/// handler that used to throw for #200) leaves the app and its tabs intact. The menu *structure*
	/// assertions moved to the menu-bar cases, where TestApp does publish a menu.
	/// </remarks>
	[Collection("DevFlow")]
	public sealed class ContextMenuTests : PortedTestAppTestBase
	{
		public ContextMenuTests(TestAppFixture fixture)
			: base(fixture)
		{
		}

		[Fact]
		public async Task RightClickDocumentTab_ShowsContextMenu_Issue193()
		{
			using var client = await TryConnectAsync();

			using var result = JsonDocument.Parse(await client.InvokeAsync("avd.tab.right-click", "Document 1", "document"));
			Console.WriteLine($"Document tab right-click: {result.RootElement.GetRawText()}");

			var layout = await QueryLayoutAsync(client);
			Assert.NotNull(FindContent(layout, "documents", "document1"));
			await AssertAppAliveAsync(client);
		}

		[Fact]
		public async Task RightClickToolWindowTab_DoesNotCrash_Issue200()
		{
			using var client = await TryConnectAsync();

			using var result = JsonDocument.Parse(await client.InvokeAsync("avd.tab.right-click", "Tool Window 1", "anchorable"));
			Console.WriteLine($"Tool window tab right-click: {result.RootElement.GetRawText()}");

			var layout = await QueryLayoutAsync(client);
			Assert.NotNull(FindContent(layout, "anchorables", "Tool Window 1"));
			await AssertAppAliveAsync(client);
		}

		[Fact]
		public async Task ApplicationMenu_ItemsAreAccessible()
		{
			using var client = await TryConnectAsync();

			using var document = JsonDocument.Parse(await client.InvokeAsync("avd.menu.items"));
			var headers = ReadArray(document.RootElement, "items")
				.Select(i => ReadString(i, "header"))
				.ToArray();

			Assert.Contains("Edit", headers);
			Assert.Contains("Layout", headers);
			Assert.Contains("Tools", headers);
		}

		[Fact]
		public async Task EditMenu_OpensAndCloses()
		{
			using var client = await TryConnectAsync();

			using var document = JsonDocument.Parse(await client.InvokeAsync("avd.menu.items", "Edit"));
			var items = ReadArray(document.RootElement, "items").ToArray();

			// The FlaUI case only logged whether Undo was there. Enumerating the submenu is what the
			// missing "open the menu and read its items" primitive was for, so the headers are asserted.
			var headers = items.Select(i => ReadString(i, "header")).ToArray();
			Assert.Equal(new[] { "Undo", "Redo" }, headers.Take(2).ToArray());
			Assert.Contains("Cut", headers);
			Assert.Contains("Copy", headers);
			Assert.Contains("Paste", headers);

			// #193/#234: the command-bound items must reflect their command's CanExecute instead of
			// being shown as always-available (or always-disabled) entries.
			Assert.All(items, item => Assert.False(ReadBool(item, "isEnabled")));

			await client.InvokeAsync("avd.menu.close");
			await AssertAppAliveAsync(client);
		}

		[Fact]
		public async Task ToolsMenu_ItemsAreAccessible()
		{
			using var client = await TryConnectAsync();

			using var document = JsonDocument.Parse(await client.InvokeAsync("avd.menu.items", "Tools"));
			var headers = ReadArray(document.RootElement, "items")
				.Select(i => ReadString(i, "header"))
				.ToArray();

			// Exact headers: the menu entry is "Tool Window1" while the tab title is "Tool Window 1".
			Assert.Contains("WinForms Window", headers);
			Assert.Contains("Tool Window1", headers);
			Assert.Contains("New floating window", headers);
		}
	}
}
