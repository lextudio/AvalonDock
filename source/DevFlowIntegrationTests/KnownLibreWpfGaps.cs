using System;
using Xunit;

namespace AvalonDock.DevFlowIntegrationTests
{
	/// <summary>
	/// Cases that pass on real WPF (the Windows legs) and fail on LibreWPF for a reason the run has
	/// already narrowed down to the backend rather than to AvalonDock or to the test. They are skipped
	/// on LibreWPF with that reason, so the gap stays visible in every run instead of turning a leg red,
	/// and docs/librewpf.md (section 9) records the evidence for each. Set
	/// <c>DEVFLOW_RUN_KNOWN_GAPS=1</c> to run them anyway, e.g. when checking a LibreWPF fix.
	/// </summary>
	internal static class KnownLibreWpfGaps
	{
		/// <summary>The backend is LibreWPF: everywhere but Windows, where the samples use real WPF.</summary>
		public static bool IsLibreWpf => !OperatingSystem.IsWindows();

		private static bool RunAnyway =>
			string.Equals(Environment.GetEnvironmentVariable("DEVFLOW_RUN_KNOWN_GAPS"), "1", StringComparison.Ordinal);

		/// <summary>Skips on LibreWPF unless known gaps were asked for.</summary>
		public static void SkipOnLibreWpf(string gap)
		{
			if (IsLibreWpf && !RunAnyway)
				Assert.Skip($"Known LibreWPF gap (passes on real WPF): {gap}");
		}

		/// <summary>Skips on macOS unless known gaps were asked for.</summary>
		public static void SkipOnMacOS(string gap)
		{
			if (OperatingSystem.IsMacOS() && !RunAnyway)
				Assert.Skip($"Known LibreWPF/macOS gap: {gap}");
		}

		public const string AutoHideClick =
			"the auto-hide flyout opens on a click but is not dismissed afterwards (it stays open after a document is activated or on a second round)";

		public const string MacNativeDrag =
			"the cliclick-driven drag/resizer gesture does not reach AvalonDock on the macOS runner";
	}
}
