# ClearFolder contributor workspace

- src/: C# modules, PascalCase.cs, one responsibility per file.
- scripts/: repeatable PowerShell build and test commands.
- tests/: synthetic behavior tests; never move a contributor's real files.
- docs/: public architecture, validation and upstream references.
- artifacts/: generated executables, diagnostics and synthetic test data; ignored by Git.
- data/: local layout and managed user files; ignored by Git. Never publish or automatically delete.
- Update public design notes before changing behavior. Review changes and run Build.ps1 and Test.ps1.
- Do not commit secrets, personal paths, screenshots, local diagnostics or runtime data.
- File moves require explicit user interaction, retain recovery journals, and never overwrite conflicts.
- Startup registration is opt-in. Do not change other applications' registry settings.
- Native diagnostics require an interactive Windows desktop and may temporarily move the pointer.
- Each deck keeps a `.lnk` on the current user's Desktop as an Explorer-owned grid reservation, with a transparent icon and an ID-encoded blank Unicode filename. Use IShellLinkW/IPersistFile for Unicode-safe access and verify the per-deck identity before updating it. The entry intercepts its own cell; never clear other desktop items' selection.
- Temporary test data is retained for inspection; cleanup requires the owner's consent.
