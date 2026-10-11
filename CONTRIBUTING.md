# Contributing

Report reproducible problems with Windows version, DPI, renderer status and steps. Remove personal file paths from reports. Do not attach your data directory or desktop screenshots containing private information.

Build with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1` and run `scripts/Test.ps1` before submitting changes. No global dependency installation is required. Native diagnostics require an interactive Windows desktop and may move the pointer temporarily; see docs/VERIFICATION.md.

Preserve explicit file-move consent, recovery journals, conflict rejection and opt-in startup. Use synthetic data for tests. Keep generated artifacts and managed files out of Git. Changes should describe the concrete behavior and validation. Contributions are provided under the MIT license.
