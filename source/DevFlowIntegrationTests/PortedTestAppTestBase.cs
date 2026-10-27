using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace AvalonDock.DevFlowIntegrationTests
{
	/// <summary>
	/// Shared plumbing for the suites ported from the FlaUI UI tests.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The FlaUI fixtures shared one application instance and relied on NUnit <c>[Order]</c> to make the
	/// state each test inherited predictable. These suites run the same scenarios against one DevFlow
	/// agent, but they start the application again per test: a restart is the only way back to the layout
	/// TestApp declares in XAML (the layout model is rebuilt from XAML at startup, and serializing and
	/// restoring it would drop the content the assertions look for). That costs a few seconds per test and
	/// buys independence from the order the runner picks.
	/// </para>
	/// <para>
	/// Assertions are the ones the FlaUI case made, except where the spec marks the original as vacuous
	/// (for example "the app has not exited" with no state check): those state checks are added here,
	/// because the port is the only place left to make them.
	/// </para>
	/// </remarks>
	public abstract class PortedTestAppTestBase : TestAppIntegrationTestBase
	{
		protected PortedTestAppTestBase(TestAppFixture fixture)
			: base(fixture)
		{
		}

		/// <summary>Restarts the application, which is how a test gets the startup layout back.</summary>
		protected Task RestartAppAsync() => RestartTestAppAsync();

		protected async Task<JsonElement> QueryLayoutAsync(DevFlowClient client)
		{
			using var document = JsonDocument.Parse(await client.InvokeAsync("avd.query.layout"));
			return document.RootElement.Clone();
		}

		/// <summary>The application still answers: a crash, a deadlock or a modal dialog would all show up here.</summary>
		protected static async Task AssertAppAliveAsync(DevFlowClient client)
		{
			var status = await client.GetStatusAsync();
			Assert.Equal(JsonValueKind.Object, status.ValueKind);
		}

		protected static string[] DocumentTitles(JsonElement layout)
			=> ReadArray(layout, "documents")
				.Select(d => ReadString(d, "title"))
				.Where(t => t != null)
				.ToArray();

		protected static string[] AnchorableTitles(JsonElement layout)
			=> ReadArray(layout, "anchorables")
				.Select(a => ReadString(a, "title"))
				.Where(t => t != null)
				.ToArray();

		protected static JsonElement? FindContent(JsonElement layout, string collection, string contentIdOrTitle)
			=> ReadArray(layout, collection)
				.Where(e => string.Equals(ReadString(e, "contentId"), contentIdOrTitle, StringComparison.Ordinal)
					|| string.Equals(ReadString(e, "title"), contentIdOrTitle, StringComparison.Ordinal))
				.Select(e => (JsonElement?)e)
				.FirstOrDefault();

		protected static string ActiveContentId(JsonElement layout)
			=> ReadString(layout, "activeContentId");

		protected static string ReadString(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		protected static bool ReadBool(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

		protected static IEnumerable<JsonElement> ReadArray(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
				? value.EnumerateArray().ToArray()
				: Array.Empty<JsonElement>();

		/// <summary>
		/// Clicks a tab header the way a user does: at the point the manager reports as hit-testable
		/// inside the tab. The FlaUI cases that guard a mouse-down handler (Issue 511) need the real
		/// gesture, so they use this rather than a semantic "activate".
		/// </summary>
		protected static async Task ClickTabAsync(DevFlowClient client, string kind, string contentId)
		{
			var bounds = await client.QueryBoundsAsync(kind, contentId);
			await client.ClickAsync(bounds.CenterX, bounds.CenterY);
		}

		protected static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string message)
		{
			var deadline = DateTime.UtcNow + timeout;
			Exception last = null;
			while (DateTime.UtcNow < deadline)
			{
				try
				{
					if (await condition())
						return;
				}
				catch (Exception ex)
				{
					last = ex;
				}

				await Task.Delay(100, TestContext.Current.CancellationToken);
			}

			throw new Xunit.Sdk.XunitException(
				last == null ? message : $"{message} (last error: {last.Message})");
		}
	}
}
