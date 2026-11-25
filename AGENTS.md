# Agent notes

## Repo facts

- Unity Editor package (`com.nsawill1405.playtest-logger`, minimum Unity 2021.3). All code is in `Editor/`, one asmdef, no runtime assembly.
- No build, test, or lint commands, and no compile-check project in the repo.
- Agent VMs have no Unity Editor. If dotnet is missing, install .NET 8 with `dotnet-install.sh`.
- To compile-check, make a throwaway netstandard2.1 csproj outside the repo that includes `Editor/**/*.cs`, defines `UNITY_EDITOR`, references the `Unity3D.SDK` 2021.1.14.1 NuGet package, and adds `NU1701` to `NoWarn`. Then run `dotnet build`.
- The reference DLLs are Unity 2021.1. APIs added in 2021.2 or 2021.3 (e.g. `ShortcutModifiers.Control`) need `#if UNITY_2021_2_OR_NEWER` to compile.
- Never call `Object.GetInstanceID()`. William's Unity version makes it error CS0619 ("Use GetEntityId instead"), and the 2021.1 check won't catch it. Key by reference or use `GetHashCode()`.
- Quick capture must stay a `[MenuItem("... _F1")]` hotkey, not a `[Shortcut]`. Unity skips non-menu shortcuts while a playing, unpaused Game View has focus. Rebind it through the shortcut id `"Main Menu/" + menuPath` (see `QuickCaptureShortcutId` in `Editor/PlaytestLoggerWindow.cs`).
