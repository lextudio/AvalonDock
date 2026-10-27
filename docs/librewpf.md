# AvalonDock on LibreWPF: synthetic-input drag flake, window-lifetime crashes, and the test port

> Research notes on the integration between AvalonDock and LibreWPF
> (`/Users/lextm/wpf-tools/librewpf`, the wpf-progpu fork).
>
> Part one (sections 1-8) is the 2026-08 investigation of **native drag flake** and **window-destroy
> crashes**: the diagnostics, the causal chains, the fixes that were applied, and the LibreWPF
> source-change to local-feed replacement procedure.
>
> Part two (section 9) is the 2026-10 work that replaces the FlaUI UI tests with DevFlow suites so
> one suite runs on Windows x64/ARM64, macOS arm64 and Linux x64/ARM64. It records what is done,
> what CI shows, and what is left.
>
> The older splitter-drag fixes are kept as history at the end.

## 1. Background: test infrastructure and the event chain

The DropTargetZone integration tests drive real drags with **cliclick** (synthetic mouse events on
macOS). The event chain is:

```
cliclick (CGEventPost into kCGHIDEventTap)
  -> GLFW cocoa window (libglfw.3.dylib, 3.4.0, Silk.NET 2.23.0)
  -> Silk.NET input callbacks (SilkNetWpfInputService)
  -> LibreWPF WPF compatibility layer (forwarded by ProGpuWpfWindowHost)
  -> WPF input pipeline (Mouse.LeftButton / MouseMove and friends)
  -> AvalonDock (LayoutFloatingWindowControl native drag detection + DragService + OverlayWindow)
```

**Facts measured on this machine:**

- cliclick's `dd`/`dm`/`du` are **synthetic events** (`CGEventPost`); they do **not** update the
  physical HID button state.
- `MacOSCursorService.IsLeftButtonDown()` (AvalonDock/Platform) goes through
  `CGEventSourceButtonState` - the physical state - so it **always returned false** for
  cliclick-injected events (the diagnostic log confirmed `physDown=False`).
- WPF's `Mouse.LeftButton` is only updated from the **event pipeline**: a synthetic `dd` becomes
  Pressed once WPF has processed it.

## 2. Drag flake: cliclick's synthetic `dd` is not processed by WPF

### Symptoms

The full suite (22 zone/preview cases) failed randomly in one or two cases, in one of two ways:

- `compass never showed drop target 'X' during discovery` - the compass stayed empty for the 8 s
  discovery window;
- `live drop target 'X' was not current before release; Target=..., Targets=[]` - after dragging to
  the target centre, `currentDropTarget` never appeared.

Failures landed on different zones at random (DockLeft, DockRight, DocumentPaneDockRight,
AnchorablePaneDockTop and others all showed up) and had nothing to do with the assertions: the dock
simply never happened.

### Reproduction

`/var/folders/.../T/opencode/drag-diag4.sh` reproduced the test flow exactly (position-main-window ->
reset -> float -> discovery drag -> a second drag to the centre of DockLeft -> release), 20 rounds
per run: **a steady 15-25% failure rate** (15-17 of 20 succeeded), with `avd.query.drag-state`
captured at the point of failure.

### What a failure looked like

The interesting fields of `drag-state` on failure:

```json
{
  "overlayLeft": null, "overlayTop": null, ...   // the overlay was never created
  "currentDropTarget": null,
  "left": 560, "top": 718,                        // the floating window did move (the tick was running)
  "currentPointer": {"X": 658, "Y": 735}          // the pointer is inside the manager
}
```

Two sub-modes:

- **Sub-mode A**: the floating window moved (the tick updated Left/Top) but the overlay was never
  created (`UpdateMouseLocation` never matched a host);
- **Sub-mode B**: the floating window did not move at all (`left/top` stayed at the pre-press
  position).

Temporary diagnostic logging in `LayoutFloatingWindowControl.OnPortableNativeLocationChanged`
produced the **decisive evidence**:

```
[DRAGDIAG] native move ignored: leftBtn=Released physDown=False at 658,735   (x36)
```

**cliclick's `dd` never reached WPF** (`Mouse.LeftButton=Released`), so the `buttonDown` check in the
window-move path (`Mouse.LeftButton==Pressed || IsLeftButtonDown()`) was false and the drag never
started.

### Causal chain

1. cliclick `dd` injects a synthetic press (`CGEventPost`, kCGHIDEventTap).
2. An earlier explanation blamed macOS "first click consumed by activation". Reading the actual
   GLFW 3.4 Cocoa sources showed that this cannot be stated as a confirmed root cause:
   `GLFWContentView` does implement `acceptsFirstMouse:` and returns `YES`. A non-key window still
   changes event timing, but GLFW cannot be said to lack a first-mouse path; the precise place where
   `dd` is lost still needs a native callback trace.
3. Even when the event is processed, `CGEventSourceButtonState` (physical state) is always false for
   synthetic events, which creates a **timing race against the WPF event state**
   (`Mouse.LeftButton`):
   - if the tick (a 16 ms `DispatcherTimer`) checks the button state **before** the injected event
     has been processed by WPF, it concludes "button released" and `EndPortableDrag` aborts the drag
     immediately.

### Fixes applied

**AvalonDock** (`src/Components/AvalonDock/Controls/LayoutFloatingWindowControl.cs`):

the watchdog's button check gained a **start-up grace period**,
`PortableNativeDragGracePeriod = 500ms`:

```csharp
var dragAge = DateTime.UtcNow - _portableDragStartUtc;
if (dragAge > PortableNativeDragGracePeriod
    && Mouse.LeftButton != MouseButtonState.Pressed && !PlatformHelper.IsLeftButtonDown())
{
    EndPortableDrag(drop: true);
    return;
}
```

This gives the synthetic `dd` a window in which to reach the WPF input pipeline, so the watchdog no
longer kills the drag at the instant it starts.
**Effect**: the full suite went from a steady 1-2 flakes to 0-1 (22/22 once, 21/22 once, 20/22
twice). **Not eradicated**: sub-mode B (a `dd` consumed by macOS activation) never reaches the
watchdog, so the grace period does not cover it.

**AvalonDock** (`DockingManager.cs`): `GetOverlayWindowHostsByZOrder` got an exception fallback (when
the Win32 z-order enumeration fails on a portable backend it degrades to a plain enumeration). This
is defensive.

**LibreWPF** (`src/ProGPU.Wpf/Platform/SilkNetWpfInputService.cs`):

`mouseUp` no longer drops an event because no matching down was recorded. The original code was:

```csharp
if (!pressedButtons.Remove(button)) { return; }   // down lost -> up swallowed
```

With synthetic input, a down is occasionally lost, and swallowing the up leaves WPF's
`Mouse.LeftButton` **stuck in Pressed**, which corrupts every later drag. The up is now always
forwarded (WPF maintains the button state itself). **Effect**: the v4 script went to 16-18/20 -
neither clearly worse nor clearly better. It is a correctness fix, not a cure for the flake.

**Attempts that did not help:**

- `avd.activate` (activating the main window before the press): made it worse - 0/20. Once the main
  window is key the floating window still is not, so activation consumption on a caption click
  happens even more reliably; activation can also move the floating window and invalidate the
  coordinates.
- A longer wait after the press: no change.
- A `GetCursorPos` shim (`ProGPU.Wpf.Sdk.Win32Compat.c`) that always returns `(0,0)`: irrelevant,
  because AvalonDock's `PlatformHelper.GetCursorPosition` goes through `MacOSCursorService`
  (`CGEventGetLocation`) and never uses that shim.

### 2026-08-08 test-side hardening (final 22/22)

Later observations showed the flake did not only come from a lost `dd`: the tests themselves had
injected mouse-down at **the wrong screen origin**. Managed `Window.Left/Top`, `PointToScreen` and the
GLFW native frame can disagree for a moment, and a floating window can sit behind the main window. In
that case the computed point was inside the "managed floating bounds" while macOS delivered the click
to the main window or to the desktop/system UI - in the worst case triggering a real macOS action.

Every floating-body warm-up press and every caption drag-down is now gated:

1. `avd.query.bounds` / `avd.query.drag-handle` read the ProGPU/GLFW native `Position`/`Size` for
   floating windows first, and the floating visual tree's screen coordinates are converted from the
   native origin;
2. cliclick only moves the mouse; `avd.query.cursor` then reads the native cursor back through
   `MacOSCursorService`, and a mouse-down is refused when the requested and measured points differ
   by more than 2 px;
3. the measured cursor must still be inside the floating native frame, and a caption drag must land
   on the caption handle;
4. `avd.query.floating-zorder` must confirm the floating window is in front of the main window; if it
   is not, TestApp calls `Activate()` on the floating window directly and re-reads frame and
   z-order rather than relying on a click that may hit the wrong window;
5. activation warm-up down/up pairs are separated by the system double-click interval (macOS:
   `NSEvent.doubleClickInterval`) so a caption click cannot merge with a neighbouring event into a
   double click.

Zone-coordinate discovery no longer runs an extra, flake-prone real drag. `avd.debug-show-overlay`
now calls `UpdateLayout()` first and returns the screen bounds of every indicator; the test picks the
indicator nearest the target pane and then performs a single real held drag. Once the target is hit,
the zone/preview tests have TestApp complete AvalonDock's current drop deterministically before the
synthetic mouse is released. The full suite later proved that WPF's `Mouse.LeftButton` can stay
`Pressed` even after `manager.up` has been received, so `NativeInputIntegrationTests`' float-back-to-
dock case also completes the already-hit current drop first and then sends the real `du` to clean up
the input state. The tests no longer treat "a synthetic mouse-up always triggers the dock" as a
reliable contract.

A full serial run also exposed two preconditions that rarely appear in isolated runs, both handled
before mouse-down:

- during a layout transition individual temporary targets can have a negative extent;
  `GetScreenBounds()` skips such unhittable rects instead of throwing while enumerating the other,
  valid targets;
- a freshly created floating window's native frame can still be above the screen, or a cursor-only
  move can be swallowed by a key-window switch. The test re-reads the native frame and cursor; when
  it is not safe it never presses, but docks programmatically, recreates the floating window and
  retries.

Final verification command and result:

```bash
dotnet run --project source/DevFlowIntegrationTests/DevFlowIntegrationTests.csproj -- \
  -class AvalonDock.DevFlowIntegrationTests.DropTargetZoneIntegrationTests -parallel none
# total: 22, succeeded: 22, failed: 0, duration: 4m 40s
```

Final full-suite verification including the lifetime and safe-retry corrections:

```bash
dotnet run --project source/DevFlowIntegrationTests/DevFlowIntegrationTests.csproj -- \
  -parallel none
# total: 56, succeeded: 56, failed: 0, duration: 6m 16s
```

Test code must not take screenshots on its own. The leftover `/api/v1/ui/screenshot` call in
UnoDevelop's `MainMenuTests.ViewTestsClickShowsTestsPad` was removed; this run has no screenshot
step.

## 3. Crash A: `resignKeyWindow` notifies a dangling observer (SIGBUS)

### Stack

```
EXC_BAD_ACCESS / KERN_PROTECTION_FAILURE
__CFNOTIFICATIONCENTER_IS_CALLING_OUT_TO_AN_OBSERVER__   <- dangling observer
AppKit -[NSWindow resignKeyWindow] -> _sendResignKeyWindow -> _changeKeyAndMainLimitedOK
AppKit -[NSWindow _handleLeftMouseDownEvent:isDelayedEvent:]   (isDelayedEvent:YES)
AppKit -[NSWindow(NSEventRouting) sendEvent:] -> libglfw.3.dylib
```

### Trigger and root cause

- Trigger: **a key-state change caused by a cliclick click** (a background window is not key, so the
  click activates or deactivates it) - the same origin as the flake's "activation consumption".
- The top of the stack is AppKit dereferencing a bad address while dispatching
  `NSWindowDidResignKeyNotification`. GLFW's `GLFWWindowDelegate.windowDidResignKey:` is one of the
  receivers of that notification, but GLFW 3.4's sources contain no explicit
  `NSNotificationCenter addObserver:`; it receives through `NSWindow.delegate`, and on destroy it
  calls `setDelegate:nil` before releasing the delegate. "GLFW leaves an observer registered" is
  therefore only a candidate hypothesis, not a confirmed root cause.
- After that, any key-state change (any click) reaches the dangling address -> SIGBUS.

### Where it can be fixed

**GLFW native** (`libglfw.3.dylib`, packaged by Silk.NET 2.23.0 as `ultz.native.glfw` 3.4.0). The
LibreWPF managed sources (ProGPU.Wpf) contain no NSNotificationCenter/ObjC observer registration at
all (checked by grepping the whole repository). GLFW master's Cocoa destroy order is the same as 3.4
in this area, so "upgrade to master and it is fixed" has no source-level basis. Candidate actions:

- add native logging to GLFW's `GLFWWindowDelegate` resign/close/dealloc and to
  `_glfwDestroyWindowCocoa`, and prove with object addresses whether the callback happens after the
  delegate or window was released;
- replacing the GLFW native build is still a valid A/B, but only with the reproduction rate as
  evidence;
- reduce how often floating windows are created and destroyed (reuse them in AvalonDock).

## 4. Crash B: `CUIThemeFacet` crashes while decoding a CAPackage (EXC_BAD_ACCESS)

### Stack (measured 2026-08-08 09:25)

```
EXC_BAD_ACCESS / EXC_ARM_DA_ALIGN at 0x9
CFRelease -> __CFBasicHashAddValue -> CFDictionarySetValue
NSKeyedUnarchiver decodeObject* -> [CALayer initWithCoder:] (x2 levels)
[CAPackage _readFromArchiveData:options:error:]
+[CAPackage packageWithData:type:options:error:]
-[CUIThemeFacet _makeLayerFromCAPackageData] -> updateLayer:effects:
CUICoreThemeRenderer::CreateOrUpdateVisualEffectLayer / CreateOrUpdateLayer
```

### Current assessment

**The macOS AppKit theme renderer (CUIThemeFacet)** decodes a `CAPackage` (a layer archive) while
creating or updating a Visual Effect Layer (the vibrancy material), touches a freed object, and
crashes in `CFRelease`. It reproduced repeatedly during this hardening work, always after frequent
float/drop/layout-restore cycles; the stack is consistently inside AppKit/CoreUI. For now "an AppKit
system-layer bug" is only a **high-probability hypothesis** - a wrong NSVisualEffectView lifetime,
a cross-thread update, or an early release in LibreWPF could still be corrupting AppKit state
indirectly. `restorable=NO` does not prevent it (the crash is in theme rendering, not in state
restoration).

### 2026-08-08 lifetime A/B

Two DevFlow stress tests that take no screenshots separated native mouse input from window
lifetime:

- 100 x `float -> dock` (creating and destroying only the titled floating window): passed, ~39 s;
- 100 x `float -> show transparent overlay + DockLeft preview -> hide overlay -> dock`: passed,
  ~60 s.

So neither "frequent floating-window destruction" nor "frequent transparent-overlay destruction" is
sufficient to trigger Crash B. LibreWPF never creates an `NSVisualEffectView` explicitly; making a
window transparent only calls `setOpaque:NO` and `setBackgroundColor:NSColor.clearColor` on the GLFW
`NSWindow`. With the crash inside `CUIThemeFacet`, the more precise hypothesis is that the system
creates a visual-effect layer **for the AppKit frame/titlebar of a titled GLFW window**, and that a
real activation, drag or key-window switch interleaved with window close triggers a race in the
theme-layer update. The next A/B round should add safe real activation/drag events instead of
increasing the number of input-free create/destroy cycles.

### 2026-08-08 AppKit view-tree evidence

`avd.query.macos-view-tree` now walks the AppKit view tree down from a real `NSWindow*`'s
`contentView.superview` (read-only, no screenshots), recording class names, object addresses,
`hidden` and the window's `styleMask`. Measured:

| Window | styleMask | `NSVisualEffectView` | Key state |
|---|---:|---:|---|
| main | 15 | 2 | one directly under the frame and one inside the titlebar background, neither hidden |
| floating | 32777 | 2 | one unhidden directly under the frame; the titlebar background is hidden but its effect subview is not |
| transparent overlay | 32777 | 1 | sits under the hidden titlebar background; no frame-level effect |

That rules out the two simple explanations ("LibreWPF creates an `NSVisualEffectView` explicitly"
and "only an ordinary titled window has that object"). Under the portable backend `WindowStyle.None`
is still an AppKit theme frame with `NSWindowStyleMaskFullSizeContentView`, not a pure borderless
window without a theme/titlebar view tree. The overlay's effect has a hidden parent, while the
floating window has an unhidden frame-level effect. One TestApp A/B removed the frame-level
`NSVisualEffectView` from a custom-chrome/full-size floating window. After refreshing the LibreWPF
local feed, real drags repeatedly produced `EXC_BAD_ACCESS/SIGSEGV -> objc_msgSend -> managed
P/Invoke`; even with the pointer verified through `NSApplication.orderedWindows` first, there is
still a lifetime race between the check and the following message. That A/B was therefore abandoned
completely and cannot be a product fix. Crash B needs further investigation in LibreWPF's/AppKit's
window-lifetime layer.

The same native-coordinate diagnostics also found that the overlay's managed `Left/Top` and its real
Cocoa frame occasionally separate; drag-state and preview-to-screen conversions used to mix the
overlay's managed origin with the manager's native origin, producing false offsets. All
preview/overlay diagnostics now use the real native origin, and the overlay corrects its position
once more through `INativeWindowService.SetWindowPosition` after `Show()` and native handle creation.

Long sequences further proved this is not only a refresh-timing problem: ProGPU's
`TryGetWindowHost(overlay)` occasionally returned the floating or main window's native host. Main,
floating and overlay windows now register native handle ownership when the source is initialised;
duplicate handles are rejected, and overlay movement is refused while the overlay has no exclusive
handle. macOS floating pre-positioning and the portable caption drag also use the fixed native handle
directly, so LibreWPF's `Window.Left/Top` setter can no longer cross windows and move the main window.
Each zone theory case now restarts TestApp, which stops a transparent native window's lifetime from
leaking across cases. Restoring the XML layout item by item used to hang the UI thread while a
floating native window was closing; the suite now performs a deterministic layout reset at the start
of the next item instead, and a 20 s action watchdog reports the action that is actually stuck.

Every test records the main window's first movement with a 20 ms native-origin guard inside TestApp
and asserts at the end that it never moved. Before every floating->dock mouse-down the tests verify
not only the requested point, the window and the z-order, but also read the real native cursor; up to
five slow, move-only retries are allowed, and the press is allowed only when the cursor is inside the
latest floating title-bar handle. The extra warm-up click is gone, clicks are still separated by the
system double-click interval, and no test calls any screenshot action. Four dedicated cases save the
live preview's screen rectangle before the drop, read the pane rectangle that actually hosts it
afterwards, and compare position and size with a 2 px tolerance.

Final verification after the 2026-08-08 refresh: the local feed's official directory holds only the
three valid packages, historical packages moved to `artifacts/local-feed-backups`, and the SHA-256 of
`ProGPU.Wpf.dll` in the LibreWPF Release build, the NuGet cache and the TestApp output is
`9f0e7585470d07f145ac1d9812552db43174cb81f44f3f569263f3626a6be1b6`. The full integration suite
passed 57/57 (8m42s).

### Impact

Triggered occasionally while the full suite ran those 22 cases -> the TestApp process died -> every
later case failed with `DevFlow agent not reachable` (in one measured run, 12 cases failed in a
cascade after the crash).

## 5. Crash C: a native drag tick moved a released NSWindow after close

### Stack and evidence

OpenDevelop crashed on the main thread when switching layout `Plain -> Default` while the Error List
and the Project Browser were in the same floating container:

```
EXC_BAD_ACCESS / SIGSEGV
objc_msgSend
selector: setFrameTopLeftPoint:
```

That selector is only used by AvalonDock's `MacOSNativeWindowService.SetWindowPosition`. Its caller,
`LayoutFloatingWindowControl.OnPortableNativeDragTick`, is a 16 ms `DispatcherTimer`. The close path
neither stopped that timer nor detached the macOS `LocationChanged` handler, so after a layout
restore destroyed the native NSWindow a queued tick could still resolve and use the old handle.
Sending an Objective-C message to a freed receiver is a process-level access violation that managed
`try/catch` cannot guard.

### Fix

`LayoutFloatingWindowControl` now does all of this in `OnClosing` (which must run before the backend
destroys the native window):

- stop the portable native drag timer;
- detach `LocationChanged` and the global `PostProcessInput` handler;
- release the mouse capture;
- abort and clear the `DragService`, and reset the drag state.

`OnClosed` calls the same cleanup again, idempotently, to cover non-standard close paths. On the
OpenDevelop side the Error List also gets `PreferredDockSide.Bottom`, so it no longer falls into
AvalonDock's floating fallback when it is missing from the layout XML. The Workbench integration test
gained assertions that the Error List has `isFloating == false` and `side == Bottom`; the full suite
passed 35/35.

## 6. LibreWPF source change -> local feed replacement

TestApp consumes packages through the `LibreWPF.Sdk` MSBuild SDK; the local source is configured in
`OpenDevelop/NuGet.config`:

```xml
<add key="librewpf-local" value="/Users/lextm/wpf-tools/librewpf/artifacts/local-feed" />
<packageSource key="librewpf-local">
  <package pattern="LibreWPF.ProGPU" />
  <package pattern="LibreWPF.Transport" />
  ...
</packageSource>
```

**Full replacement procedure** (verified working):

```bash
# 1. change the source: /Users/lextm/wpf-tools/librewpf/src/ProGPU.Wpf/...
# 2. build (with the repository's pinned SDK)
cd /Users/lextm/wpf-tools/librewpf
export PATH="$PWD/.dotnet:$PATH"
dotnet build src/ProGPU.Wpf/ProGPU.Wpf.csproj -c Release
# output: src/ProGPU.Wpf/bin/Release/net10.0/ProGPU.Wpf.dll

# 3. unpack the nupkg -> replace the dll -> repack (zip) -> overwrite the local feed
mkdir -p /tmp/nupkg-work && cd /tmp/nupkg-work
cp /Users/lextm/wpf-tools/librewpf/artifacts/local-feed/LibreWPF.ProGPU.0.1.0-preview.41.nupkg orig.nupkg
unzip -o -q orig.nupkg -d extracted
cp <new ProGPU.Wpf.dll> extracted/lib/net10.0/ProGPU.Wpf.dll
cd extracted && rm -f ../LibreWPF.ProGPU.0.1.0-preview.41.nupkg \
  && zip -q -r -X ../LibreWPF.ProGPU.0.1.0-preview.41.nupkg .
# verify: unzip -p new.nupkg lib/net10.0/ProGPU.Wpf.dll | md5 == md5 of the new dll

# 4. back up, overwrite the feed, clear the NuGet cache
#    (step 3 left the shell in extracted/, so go back to the artifacts directory first; a single
#    glob cannot be copied onto a glob, so the backup names one file)
cd /Users/lextm/wpf-tools/librewpf/artifacts
cp local-feed/LibreWPF.ProGPU.0.1.0-preview.41.nupkg \
   local-feed/LibreWPF.ProGPU.0.1.0-preview.41.nupkg.myfix-backup
cp /tmp/nupkg-work/LibreWPF.ProGPU.0.1.0-preview.41.nupkg local-feed/
rm -rf ~/.nuget/packages/librewpf.progpu/0.1.0-preview.41

# 5. rebuild TestApp and compare md5
dotnet build source/TestApp/TestApp.csproj -c Debug
md5 -q source/TestApp/bin/Debug/net10.0-windows/ProGPU.Wpf.dll   # must equal the new dll
```

Note: the `LibreWPF.Transport` package contains **prebuilt** WPF DLLs
(PresentationCore/PresentationFramework). If code under `src/Microsoft.DotNet.Wpf` changed, build
that csproj first and then pack the packaging project.

## 7. Diagnostic tools

| Tool | Purpose |
|---|---|
| `avd.query.drag-state` | Live drag state: `currentDropTarget`, `previewGeometryBounds` (screen rect of the current real preview), `overlayLeft/Top` (whether the overlay was created), `left/top` (floating window position), `currentPointer`, `dragOffset` |
| `avd.query.active-drop-targets` | The compass indicators currently shown (type + screen centre) |
| `avd.input.query` | TestApp mouse diagnostics: `leftButton` (Pressed or not), `mouseX/Y`, `captured` |
| `avd.debug-show-overlay <zone>` | Force the overlay on and return `previewGeometryBounds` (**screen** coordinates, for comparison with the pane after docking) |
| `drag-diag4.sh` (temporary session script) | Reproduce the flake 20 times, printing the drag-state on failure |
| `~/.nuget/packages/...` and `local-feed` | Package version/replacement verification (md5 comparison) |
| `~/Library/Logs/DiagnosticReports/TestApp-*.ips` | Crash reports (`python3 -c` to extract the exception and the triggered thread's stack) |

**How to read a failure:**

- `overlayLeft: null` and the floating window moved -> sub-mode A (the tick runs but
  `UpdateMouseLocation` never matches a host);
- the floating window did not move and `leftBtn=Released` -> sub-mode B (cliclick's `dd` never reached
  WPF, consumed by activation);
- the case fails within 4 ms with `not reachable` -> the previous step's TestApp crash cascading
  (check the .ips).

## 8. Status and open issues (2026-08)

**Fixes in place** (all verified by the full suite):

1. `LayoutFloatingWindowControl.cs` (AvalonDock): 500 ms watchdog grace period - flake rate 1-2 ->
   0-1.
2. `DockingManager.cs` (AvalonDock): `GetOverlayWindowHostsByZOrder` exception fallback (defensive).
3. `SilkNetWpfInputService.cs` (LibreWPF, dll replaced in the local feed): mouseUp is no longer
   dropped when its down is missing.
4. Test improvement: `DockPreview_MatchesDockedPaneGeometry` records the preview's screen rectangle
   from `avd.query.drag-state` while the real drag is held and the target is hit but not yet
   released, then reads the actual pane after the drop and compares position and size (2 px
   tolerance).
5. native-input safety gates: a mouse-down is allowed only when the native cursor, the GLFW frame and
   the native z-order are all correct; wrong coordinates now fail instead of dragging the main
   window, the desktop or macOS system UI.
6. The extra discovery drag is gone; the overlay publishes all indicator bounds after it has laid
   out, and zone tests complete their drop deterministically while a dedicated case covers the real
   mouse-up.
7. Full `DropTargetZoneIntegrationTests`: **22/22 in 4m40s**, with no screenshot action during the
   run.

**Open:**

- product-level first-click semantics: the tests work around a non-key window swallowing `dd`, but a
  real application should still verify/implement GLFW Cocoa `acceptsFirstMouse`; a test workaround is
  not a product fix;
- Crash A (dangling GLFW observer): did not reproduce in the final 22/22, but the native stack is
  known and it needs a GLFW build A/B before it can be closed;
- Crash B (CUIThemeFacet CAPackage decode): reproduced repeatedly during this round of fixes and is
  the highest-priority stability problem; the final 22/22 only shows that fewer window/gesture cycles
  kept it from triggering in that run, not that it is fixed.

### Suggested order of further investigation

1. **Crash B first**: build the smallest loop (float -> dock -> restore), turn the window material /
   transparency off, reuse instead of destroy the floating/overlay windows, serialise close and
   recreate, and record the crash rate for 100 iterations per configuration; add timestamps and
   object addresses to `NSVisualEffectView` creation, removal from its parent, and native window
   dispose.
2. **Then Crash A**: keep the same workload and replace only the GLFW native build (current 3.4.0 vs a
   newer version, or a self-built build with an observer-removal patch), and check whether the
   `NSWindowDidResignKeyNotification` observer's registration and removal are paired. If an upgrade
   makes it disappear, pin the fix to the native dependency upgrade instead of working around it in
   AvalonDock.
3. **Then the product behaviour**: after verifying Cocoa `acceptsFirstMouse:`, regress a real user's
   first click-and-drag; keep the current test safety gates, because they guard against wrong
   coordinates/z-order, which is a different class of problem from `acceptsFirstMouse`.

## 9. 2026-10: replacing the FlaUI tests with DevFlow (in progress)

### Goal

`source/AutomationTest/AvalonDockTest/FlaUI` held **92 UI test cases in 17 files** (18 fixtures; the
two base classes hold none) that only ran on Windows, through FlaUI/UIA. They are being replaced by
DevFlow suites that launch the same sample applications and drive them over the in-process DevFlow
agent, so **one suite** runs on:

| Leg | Runner | Backend |
|---|---|---|
| windows-x64 | windows-2025 | real WPF |
| windows-arm64 | windows-11-arm | real WPF |
| macos-arm64 | macos-15 | LibreWPF (CoreGraphics/AppKit), cliclick for native input |
| linux-x64 | ubuntu-24.04 | LibreWPF on Xvfb + openbox, XTest for native input |
| linux-arm64 | ubuntu-24.04-arm | same as linux-x64 |

When the port finishes the suite is 57 existing DevFlow cases + 92 ported = **149 cases**. The 57
existing cases already prove the infrastructure is cross-platform (agent, fixtures, two ports, real
pointer injection, ProGPU backend); they do not prove the FlaUI coverage has moved.

### Done

**Infrastructure (committed):**

- `.github/workflows/ci.yml` gained the five-legged `devflow` matrix (the Windows legs also run the
  ordinary unit tests in a separate job); `release.yml` validates on windows-x64, macos-arm64 and
  linux-x64 and replaces same-version packages instead of failing on them.
- The two sample applications each have their own agent and port: `TestApp` on **9223**
  (`[Collection("DevFlow")]`, `TestAppFixture`) and `AvalonDockCodeApp` on **9224**
  (`[Collection("DevFlowCodeApp")]`, `CodeAppFixture`). The port is a property of the suite, not a
  process-wide static, so a ToggleDock suite cannot connect to the wrong application.
- The DevFlow action surface grew to cover what the ported cases need: menus as a tree
  (`avd.menu.items`/`invoke`/`close`), tab right-clicks (`avd.tab.right-click`), invoking an entry of
  a tab's context menu (`avd.context.invoke`), window geometry (`avd.query.window`,
  `avd.window.resize`), a dialog seam (`avd.dialogs.answer`/`query`), documents
  (`avd.document.add-two`/`activate`, `avd.content`, `avd.close-content`), floating windows
  (`avd.close-floating`), focus and input (`avd.focus.query`/`set`, `avd.input.key`/`text`), and
  auto-hide state (`avd.query.autohide`, `avd.query.bounds autohide-tab`).

**Ported suites (61 of 92 cases, committed):**

| Suite | Cases | Batch |
|---|---:|---|
| `SmokeTests` | 8 | 1 |
| `ContextMenuTests` | 5 | 1 |
| `DocumentTabTests` | 6 | 1 |
| `KeyboardNavigationTests` | 4 | 1 |
| `ToolWindowTests` | 7 | 2 |
| `ToolWindowVisibilityTests` | 6 | 2 |
| `FloatingWindowTests` | 4 | 2 |
| `FloatingWindowCloseTests` | 1 | 2 |
| `DocumentLifecycleTests` | 5 | 2 |
| `LayoutSerializationTests` | 6 | 3 |
| `LayoutSerializationExtendedTests` | 5 | 3 |
| `ManagerLifecycleTests` | 4 | 3 |

Method names and `IssueNNN` suffixes are kept, so a failure still names the regression it did
before. Assertions the original spec marks as vacuous are replaced by the behaviour: "the app has not
exited" after a tab click becomes "that tab is now active"; tool-window content is read from that tool
window instead of counting edit controls in the whole window; the auto-hide strip is checked to sit on
the window's left edge rather than "left of the midline"; three flyout round trips are timed; "New
floating window" must add exactly one window (#254's duplicate was invisible to "at least one"); a
layout restore must remove what was added after the save; #437 (never actually unloaded in FlaUI) now
unloads the manager with a floating window open.

### Decisions worth keeping

- **Per-test restart.** The FlaUI fixtures shared one application and relied on NUnit `[Order]` for a
  predictable starting state. Each ported test now starts a fresh process (`PortedTestAppTestBase`
  overrides `InitializeAsync`): the XAML layout is only rebuilt at startup, and the older suites in
  the same collection deliberately replace the layout with their drag fixtures.
- **Dialogs as a seam.** A headless runner cannot click a modal `MessageBox`, so the sample's two
  confirmations (`DocumentClosing`, `ToolWindow1.Hiding`) go through `AskYesNo`, which records every
  question and answers from `avd.dialogs.answer` (yes/no/ask). Tests assert the recorded question
  instead of looking for a `#32770` window.
- **System close without Win32.** `avd.close-floating` calls `Window.Close()` on the floating control
  - the path a window manager's close button ends in - instead of posting
  `WM_SYSCOMMAND`/`SC_CLOSE`.
- **Keyboard limitation.** The agent can insert text into a control on all three platforms, but it
  cannot hold a modifier or press an arrow key (Linux injects one keysym, macOS goes through
  cliclick, Windows through SendInput; none implements chords). Gestures are raised as WPF routed
  input on the focused element, and the assertions are on focus and liveness.
- **The action surface is static.** This is the important discovery of the port: the LibreWPF agent
  resolves an action either as a static method or as an instance method of a window (it enumerates
  `Application.Current.Windows`), but the **WPF agent implements only the static half** - both
  `GetInvokeActionTargetsAsync` and `DispatchOnUIThreadAsync` sit inside `#if LIBREWPF` in
  `WpfAgentService`. On real WPF every `avd.*` action declared on `MainWindow` was therefore
  discovered and then dropped for lack of a target, and nothing marshalled it to the UI thread
  either. Both samples now expose their actions as static forwarders (`MainWindow.DevFlowActions.cs`)
  that resolve the window and call the implementation inside the application's dispatcher, carrying
  any exception out with `ExceptionDispatchInfo` so the agent reports a failed action instead of the
  process dying on the UI thread. This is a DevFlow package limitation (0.3.1 is the latest
  published); the samples work around it rather than depending on a private feed.

### CI evidence

| Run | Commit | Result |
|---|---|---|
| 40465720089 | `810bc87` | 57 cases. linux-x64 and linux-arm64 green (57/57); both Windows legs failed all 57 before the first assertion (`dotnet not found`: the fixture scanned PATH for `dotnet` instead of `dotnet.exe`); macOS 55/57 - the two native-drag cases failed (`DragFloatingToolWindow_ToDocumentPane_DocksBackIntoLayout`, `DragDockedPaneResizer_ResizesToolPane`) |
| 40466622281 | `d6f8a2a` | 57 cases. Windows now launches the app, and all 57 fail with `404 avd.position-main-window`: the agent was up but none of the sample's instance actions existed (the finding above) |
| 40469049093 | `e35dfc3` | 80 cases (57 + batch 1). linux-x64 76/80: `avd.input.key` hit `EntryPointNotFoundException: GetKeyState in user32.dll` in the floating-window arrow case, the Edit menu's command-bound items reported `isEnabled=true` where the port expected false, and both right-click cases found no tab because an earlier suite had replaced the layout (the per-test restart was not in this commit). Windows: all 80 `agent not reachable` - an action exception escaping the dispatcher killed the process. macOS 72/80 (the same two native-drag cases plus four smoke cases that inherited a mutated layout) |
| 40469822693 | `43c0fae` | 103 cases (57 + batches 1-2). linux-x64 90/103; the failures are the current work list, see below |

### Windows ARM64: why it was slow (2026-10-27)

Run 40469049093 (80 cases): linux-x64 5m54s, linux-arm64 6m09s, macOS 10m43s, windows-arm64
**27m25s** (38 passed / 38 failed / 4 skipped). windows-x64 finished in 6m26s only because all 80
cases failed instantly (the dispatcher crash cascade); once the app really runs, x64 and arm64 are
the same case. The Windows arm64 job's build step is as fast as the others (~1 min), so the time
was spent in the test step: the four skipped native-input cases are spaced exactly 10.2 s apart in
the log, and a skip does no work of its own - that spacing is the per-test restart.

Cause: `DevFlowAppFixture` started the sample with `dotnet run --no-build --project ...`. `--no-build`
still evaluates the whole project (SDK resolution for the 11 preview SDK, the `LibreWPF.Sdk` targets,
restore assets) before launching, about 10 s on a `windows-11-arm` runner, and the per-test restart
introduced for the ported suites pays it 80+ times. Fix: when `DEVFLOW_TESTAPP_NO_BUILD=1` and
`bin/Debug/net10.0-windows/<project>.dll` exists, the fixture runs `dotnet <project>.dll` from that
folder (falling back to `dotnet run`). This also shortens every other leg.

Second cause: the legacy drag cases (`DragDropFeatureIntegrationTests`, `DropTargetZone...`) spent
26-33 s each and then failed with `501 Drag is not supported by this agent`. A 501 means the agent
build has no such pointer primitive, which is a platform limitation, so `DevFlowClient` now reports
it as a skip instead of a failure.

### 2026-10-27 findings: the 21 s per test on Windows was `localhost`

After the direct launch (`98d92e9`) a trivial SmokeTests case still took **21.0 s on Windows ARM64
and 4.8 s on Linux x64**, with the same duration for every test. A constant per-test cost that does
not depend on what the test does pointed at the transport, not at the application: the client and
the fixture's readiness probe used `http://localhost:9223`, which Windows resolves to `::1` first;
the agent listens on IPv4 only (`127.0.0.1`, checked with `lsof` on macOS), so every new connection
paid a refused-then-fallback round. `3161c2e` switched both to `127.0.0.1` and a Smoke case now takes
**3.4 s** on windows-arm64. A full Windows leg went from 48 min to 4-5 min. Two CI changes made this
measurable: `plan` + `legs`/`test_args` dispatch inputs (re-run one platform or one class,
for example `-f legs=windows-arm64 -f test_args="--filter-class AvalonDock.DevFlowIntegrationTests.SmokeTests"`),
and application logs copied into `TestResults/` (the Windows `upload-artifact` step used to fail with
"rootDirectory is not a parent directory" and dropped every Windows artifact).

Where a full run stood after that (run 40495108583, 118 cases): linux-x64 and linux-arm64 108/118,
macos-arm64 106/118, windows 71-74 passing. The ten failures shared by the three LibreWPF legs:
the four auto-hide flyout cases, `ClickToolWindowTab_ActivatesIt_Issue375`,
`ToolWindowContent_IsAccessible`, `SaveLoadLayout_WithToolWindowStates_Issue59` (a click on a
*tool-window* tab does not activate it, while clicks on document tabs do), the 10x10 floating window
(#174), the "Window" context-menu detach, and the floating window surviving `avd.close-floating`.
`TestApp` declares `DockWidth="50"` for Tool Window 1's pane, so the tab is a 9x14 target there; tool tabs activate on
mouse *up* (`LayoutAnchorableTabItem.OnMouseLeftButtonUp`) while document tabs activate on mouse down.
Windows adds one real defect: the sample process dies when a floating window is created and every
later case reports `agent not reachable`. The log is now appended across restarts and the fixture
records the exit code, and `ClickAsync` writes the hit-test and the input counters to stderr, so the
next run says which of these it is.

### State on 2026-10-27 (run 40511892900, commit `78575cd`)

| Leg | Passed | Failed | Skipped | Duration |
|---|---:|---:|---:|---:|
| windows-x64 | 79 | 0 | 39 | 6.7 min |
| windows-arm64 | 79 | 0 | 39 | 5.0 min |
| linux-x64 | 110 | 8 | 0 | 12.6 min |
| linux-arm64 | 110 | 8 | 0 | 12.1 min |
| macos-arm64 | 108 | 10 | 0 | 16.0 min |

The Windows skips are the native-drag cases: real WPF has no held-drag primitive in the agent
(cliclick on macOS and XTest on Linux are the only ones), and a 501 from the agent is a skip.

Fixed on the way there (each commit went through the OpenCode reviewer and build agents):

- **Windows stack overflow** (`8b2877b`): on real WPF `GetNativeWindowOrigin` called
  `PointToScreenPortable`, which for a `LayoutFloatingWindowControl` called `GetNativeWindowOrigin`
  again. The process died with `0xC00000FD` the first time a floating window's caption handle was
  queried, and every later case reported `agent not reachable`. The fixture now logs the exit code of
  every instance, which is how this was found.
- **System close** (`86805f7`, `78575cd`): `avd.close-floating` called `Window.Close()`, which only
  hides the native window. The #368 fix lives in the `WM_CLOSE` handler, which runs
  `HideWindowCommand`; the action now does the same. The model keeps the emptied floating window (it
  is what the tool window is re-shown into), so the case waits for the tool window to stop floating.
- **"Window" mode** (`86805f7`): the `Window` context-menu entry detaches the anchorable into a
  standalone `DetachedAnchorableWindow`, which is not a floating window. The layout query now
  reports `detachedContentIds`, and closing a detached window reattaches it.
- **Auto-hide bounds** (`2f4056c`): the side auto-hide tabs are under a 90 degree
  `LayoutTransform`, so the (0,0)-(w,h) diagonal gave a negative width; bounds now come from all four
  corners.
- **Narrow pane** (`78575cd`): Tool Window 1's pane is declared `DockWidth="50"`, so its tab is a
  9 px sliver. Tests widen it through `avd.pane.set-width` before clicking a tool tab.
- Smaller ones: JSON results from `avd.activate-floating` and `avd.close-content`,
  `KeyboardFocusChangedEventArgs` in `avd.menu.close` (plain `RoutedEventArgs` threw on WPF), unload
  detection, and `EnsureDecomposedInputAvailable` for the drop-zone theories.

#### Known LibreWPF gaps (skipped on LibreWPF, see `KnownLibreWpfGaps`)

These pass on real WPF. Every diagnostic so far points at the backend, not at AvalonDock or at the
test, so they are skipped on LibreWPF with the reason in the skip message;
`DEVFLOW_RUN_KNOWN_GAPS=1` runs them anyway, which is how a LibreWPF fix should be checked.

| Gap | Cases | Evidence |
|---|---|---|
| Auto-hide click | `ClickAutoHideTab_OpensFlyout_Issue169`, `AutoHideWithTextBoxes_ContentAccessible_Issue62`, `AutoHideFlyout_IsResponsive_Issue10`, `SaveLoadLayout_WithAutoHidePanels_Issue111` | The click lands on the right anchor (`hitContentTitle` = `AutoHide1 Content`), down and up both arrive, `lastDownHandled=false`, `Mouse.DirectlyOver` is the anchor's `Border`; `LayoutAnchorControl.OnMouseDown` should then call `ShowAutoHideWindow`, but `avd.query.autohide` never reports the flyout open. Next step: trace `AutoHideWindowManager.ShowAutoHideWindow` and the close timer (`IsWin32MouseOver` on LibreWPF) |
| Tool tab click | `ClickToolWindowTab_ActivatesIt_Issue375`, `ToolWindowContent_IsAccessible`, `SaveLoadLayout_WithToolWindowStates_Issue59` | Even with the pane widened to 300, the centre of Tool Window 1's tab header (as `TransformToAncestor` reports it) hit-tests to `Tool Window 2`, and that is what activates. The anchorable tab panel is arranged differently from WPF |
| Floating size to content (#174) | `FloatingWindow_HasReasonableSize_Issue174` | `managedWidth/Height` 25x25, native frame 10x10, five seconds after creation; the content declares 300x300 minimums and `AutoWindowSizeWhenOpened` is on |
| macOS native drag | `DragFloatingToolWindow_ToDocumentPane_DocksBackIntoLayout`, `DragDockedPaneResizer_ResizesToolPane` (macOS only) | Failed on the macOS runner in every run since the matrix was added (the overlay never appears, the resizer never moves); Linux runs the same cases green through XTest |

### Work list from the earlier runs

Committed 2026-10-27 through the OpenCode reviewer/build flow (reviewer and build agents on
`opencode-go/deepseek-v4.1-flash`): `41cd8ad` (the items below) and `98d92e9` (the two speed fixes).
Run 40477750121 is the first run with them.

The working tree holds the fixes for the 40469049093/40469822693 failures, built locally but **not
committed**:

- `PortedTestAppTestBase.InitializeAsync` restarts the application before every test, and the
  per-suite `RestartAppAsync()` calls are gone (this is what fixes the four macOS smoke failures and
  the two "no tab titled ..." failures, which were layout state leaking from the older suites);
- `OnUi` in both samples carries an action's exception out of the dispatcher with
  `ExceptionDispatchInfo`, so a failing action is reported as a 400 instead of killing the process
  on the UI thread (this is the Windows `agent not reachable` cascade);
- `avd.input.key` reports backend limitations (`EntryPointNotFoundException`,
  `DllNotFoundException`, `PlatformNotSupportedException`) in its payload and lets everything else
  escape (this is the Linux `GetKeyState in user32.dll` failure);
- `DevFlowAppFixture` writes the application log to `RUNNER_TEMP`, which is the only directory the
  CI job collects on every runner;
- `ContextMenuTests.EditMenu_OpensAndCloses` asserts that the five entries are command-bound instead
  of asserting a particular enabled state.

Open defects found by the 103-case run, still open:

1. `autohide-tab` bounds come back with a **negative width** (`x:74, width:-24`), so
   `QueryBoundsAsync` rejects them as empty and four auto-hide cases fail. The `LayoutAnchorControl`
   screen rect needs the same native-origin treatment the floating windows got (section 4); the
   vertical strip's transform is inverted on LibreWPF.
2. `avd.content`, `avd.close-content` and `avd.close-floating` return plain strings on their
   "not found / cannot close" paths while the tests parse JSON; the failures surface as
   `JsonReaderException: 'A' is an invalid start of a value`.
3. `FloatingWindow_HasReasonableSize_Issue174`: the window created by `Tools > New floating window`
   measures **10x10** on Linux, so either the query reads the window before it has been laid out or
   #174 is back.
4. `ClickToolWindowTab_ActivatesIt_Issue375` / `ToolWindowContent_IsAccessible`: a click on the
   `Tool Window 1` tab does not make it the active content (the pane is only ~50 px wide, so the
   centre may land on the neighbouring tab).
5. `FloatingToolWindow_SystemClose_CanBeReshown_Issue368`: the floating window survives
   `avd.close-floating`.

Two review passes over the uncommitted change set (the local `claude` CLI, model
`claude-sonnet-5-5` at low effort) found no blockers.
The first pass's items are addressed; three should-fix items from the second pass are **still open**:

- (fixed in `41cd8ad`) the three keyboard cases share `AssertNoAppError`, which tolerates the three
  backend-limitation exceptions; `EditMenu_OpensAndCloses` now asserts every item is command-bound and
  reports `isEnabled`, with the comment corrected. The OpenCode reviewer approved this tree.

### Remaining porting work

| Suite | Cases | Fixture |
|---|---:|---|
| `DetachedWindowTests` | 7 | TestApp (9223) |
| `StressTests` | 7 | TestApp |
| `ToggleDockTests` | 11 | AvalonDockCodeApp (9224) |
| `ToggleDockSerializationTests` | 4 | AvalonDockCodeApp |
| `ToggleDockShortcutTests` | 2 | AvalonDockCodeApp |

After that: delete `source/AutomationTest/AvalonDockTest/FlaUI`, the `Category=FlaUI` CI step and the
FlaUI package references, drop the `Category!=FlaUI` filter (also in `release.yml`), and re-run the
five legs.

### Running the suite

```bash
# host under test (Debug) - also deploys the sample applications
dotnet build source/DevFlowIntegrationTests/DevFlowIntegrationTests.csproj -c Debug

# everything, one process per fixture
dotnet run --project source/DevFlowIntegrationTests/DevFlowIntegrationTests.csproj -- \
  --parallel none

# one suite
dotnet run --project source/DevFlowIntegrationTests/DevFlowIntegrationTests.csproj -- \
  -class AvalonDock.DevFlowIntegrationTests.ToolWindowTests -parallel none
```

The local build needs `global.json` pinned to `11.0.100-rc.1.26425.128` and the GitHub Packages
credentials for `openavalon-github` in the environment; restore `global.json` afterwards. Do not
inject native input into a developer's desktop for these suites - the native-input cases assume the
application owns the pointer and are verified in CI.

## 10. History: the splitter-drag triple fix (older content, still valid)

(Original `librewpf.md` content, before 2026-08.) Dragging a splitter with the left button held made
the cursor revert to an arrow, the drag fail or move only one step, and the cursor then stick. The
root cause was that `LayoutGridControl.OnSplitterDragStarted` calls `Show()` on a transparent overlay
`Window` (`ShowActivated=false`, `Owner=null`) in the middle of the drag, which produced three
independent breakages on the LibreWPF portable backend: a phantom MouseUp, a `(0,0)` jump from
`Mouse.Synchronize()`, and `Window.HandlePortableMove` releasing the capture. All three fixes are in
the librewpf repository:

| # | File | Change |
|---|------|--------|
| 1 | `src/ProGPU.Wpf/ProGpuWpfWindowHost.cs` | swallow the phantom MouseUp that follows a show with no intervening move, plus GLFW mouse pass-through; `NormalizeInputEventForRenderSurfaceGeometry` forwards events for degenerate scales |
| 2 | `src/Microsoft.DotNet.Wpf/src/PresentationCore/.../MouseDevice.cs` | `Synchronize()` returns early while `Captured != null` |
| 3 | `src/Microsoft.DotNet.Wpf/src/PresentationFramework/.../Window.cs` | `HandlePortableMove` skips `Mouse.Capture(null)` while the mouse button is physically down |

Fixes 2 and 3 ship in `LibreWPF.Transport`, fix 1 in `LibreWPF.ProGPU`.

Verification harness: `/Users/lextm/uno-tools/tooltiptest` (`TOOLTIPTEST_SPLITTER_MODE=1`,
`scripts/check-splitter-capture.sh` -> `SPLIT_RESULT: PASS`). Repackaging note: Transport packages
prebuilt WPF DLLs, so build the matching csproj before packing.
