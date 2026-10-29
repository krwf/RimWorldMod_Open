# Building Do work right now

The ready-to-install mod already includes both compiled DLLs. Players do not
need the .NET SDK, a compiler, or these instructions.

## Requirements for rebuilding

- .NET 10 SDK.
- Internet access for the first NuGet restore, or the pinned packages and their
  transitive dependencies in a local NuGet cache.
- No local RimWorld installation is required to compile.

From this directory, run:

```sh
dotnet build Source/DoWorkRightNow.csproj -c Release
```

`build.ps1` and `build.sh` run the same command.

| Game | Target | Reference package | Compile-time Harmony | Output |
|---|---|---|---|---|
| 1.6 | `net472` | `Krafs.Rimworld.Ref` 1.6.4871 | `Lib.Harmony` 2.4.3 | `1.6/Assemblies/DoWorkRightNow.dll` |
| 1.7 | `net10.0` | `Krafs.Rimworld.Ref` 1.7.4990-unstable | `Lib.Harmony` 3.0.0-rc.3 | `1.7/Assemblies/DoWorkRightNow.dll` |

Both builds compile the same original source. `LoadFolders.xml` selects the
shared content and the DLL for the running game. No game or Harmony DLL is
bundled with the mod; the installed game and enabled Harmony mod supply those
at runtime. NuGet references use `IncludeAssets="compile"` and
`PrivateAssets="all"` to prevent dependency DLLs being copied into the mod.
The .NET Framework targeting pack (`Microsoft.NETFramework.ReferenceAssemblies`
1.0.3) is a build dependency only; it allows building the 1.6 DLL on Linux too.

## Source layout

- `Core.cs`: Harmony startup and the per-map saved work selection.
- `WorkController.cs`: forced work selection, normal-work suppression,
  availability polling, and job scheduling hooks.
- `UserInterface.cs`: toolbar tool, cursor, header clicks, tooltips, and red
  gradient borders.
- `Defs/ThinkTreeDefs/DWRN_ForcedWork.xml`: a registered work-giver tree so a
  forced job has a stable source node during save/load.
- `Languages`: Korean and English keyed text.
- `Textures/UI/DoWorkRightNow/Warning.png`: the same icon is used for the toolbar
  button and cursor attachment.

## Updating version references

RimWorld 1.7 was on the unstable branch when this build was made. A later game
update can change a private Harmony target without causing a C# compilation
error. After changing package versions, inspect the patched target signatures
and test the mod in the game. Do not treat a successful compile as an in-game
compatibility test.

The bootstrap unpatches this mod's patches if patch registration fails, and
logs an error prefixed `[Do work right now]`.

## Sources used to verify the build targets

- Ludeon 1.7 announcement: https://ludeon.com/blog/2026/10/announcing-the-waylith-engine-and-update-1-7/
- RimRef project and framework mapping: https://github.com/krafs/RimRef
- Harmony RimWorld version mapping: https://github.com/pardeike/HarmonyRimWorld
- RimWorld 1.6 references: https://www.nuget.org/packages/Krafs.Rimworld.Ref/1.6.4871
- RimWorld 1.7 references: https://www.nuget.org/packages/Krafs.Rimworld.Ref/1.7.4990-unstable

Only this mod's original code, icon, and documentation are distributed. The
game implementation and reference assemblies are not included.
