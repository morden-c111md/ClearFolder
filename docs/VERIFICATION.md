# Validation

2026-10-11 revision: build passed and 160 behavior checks passed. Added transparent ICO decoding, Unicode blank shortcut identity reload/repeated update/foreign identity rejection, and entry cell padding/neighbor input boundaries. Interactive WindowFromPoint checks confirmed the entry handles its left, right and lower padding while the outside desktop remains accessible. Existing managed entries and position were retained during the local update. Startup deselects only its placeholder; desktop select-all and external marquee selection remain a known limitation.

Run scripts/Build.ps1 and scripts/Test.ps1 on Windows with the system .NET Framework x64 compiler. Behavior tests use isolated artifacts/test-* synthetic data and retain it for inspection. No personal desktop files are moved by these tests.

Interactive diagnostic flags: --capture-test, --interaction-test, --drop-test, --transfer-test, --usability-test, --tray-test, --dynamic-background-test, --ripple-menu-test, --animation-test, --smoke. They require an interactive Windows desktop; some move the pointer temporarily and generate artifacts. Run serially, not simultaneously.

--render-preview uses synthetic defaults. To render a deliberately chosen image, set CLEARFOLDER_PREVIEW_IMAGE. --icon-test requires CLEARFOLDER_ICON_SHORTCUT pointing to a .url fixture whose ICO includes a valid 256px PNG frame. It is intentionally not a portable test without that fixture.

Shader availability is detected at runtime. Animation callback intervals and background upload rates are not presented FPS. Native tests depend on hardware, desktop composition, DPI and foreground applications; report environment details with failures. Runtime data and local validation logs are excluded from the repository.
