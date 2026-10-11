# Architecture

ClearFolder is an independent Windows 11 desktop overlay written in C# using WPF for glass rendering and composition, and WinForms for tray and management UI. It is not a Windows Shell folder extension.

Layout reads the actual desktop grid when available, snaps the entry to a nearby free cell, and preserves a separate hover corridor between entry and panel. Each deck creates a `.lnk` targeting ClearFolder; Explorer owns that shortcut's cell. A transparent ICO and ID-encoded blank Unicode filename prevent duplicate icon and label imagery. IShellLinkW/IPersistFile retain Unicode names and per-deck identity across restarts. The glass entry intercepts its complete cell using an alpha=1 input layer, while adjacent desktop cells remain accessible. Startup clears only this placeholder's selection. External desktop select-all or marquee selection can still select it.

Glass edges use a one-pixel white gradient with alpha 120/42/78 for a clearer boundary without an outer glow.

Background capture runs asynchronously with one task in flight and reusable buffers. Hardware-supported PS 3.0 ShaderEffect refracts the background and adds localized short-lived light/dark waves. Foreground icons and text remain separate. Capture targets do not guarantee presented frame rate; hidden or fully covered entries pause sampling.

The model persists layout and uses journaled real file moves. Explicit drag-in moves an item into data/files; drag-out resolves a filesystem destination before moving. Cancellation, unknown destinations and name conflicts preserve the managed item. Pending journals block new transfers and normal exit until resolved. Managed items also lock normal tray exit. This does not prevent shutdown or forced process termination.

Startup registration is disabled by default and requires explicit UI confirmation. Diagnostic flags use isolated synthetic data except explicitly supplied preview/icon inputs. Runtime capture is memory-only; explicit diagnostics may export screenshots.
