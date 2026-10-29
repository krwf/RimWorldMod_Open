# Verification record / 검증 기록

Build: **Do work right now 1.0.0**  
Date: **2026-10-29 (Asia/Seoul)**  
Configuration: **Release**, .NET SDK **10.0.403**

## Compiled artifacts

Both builds completed with **0 warnings and 0 errors**.

| Game | Game reference | Binary target framework | Bytes | Compilation |
|---|---|---|---:|---|
| 1.6 | 1.6.4871 | `.NETFramework,Version=v4.7.2` | 23,552 | Pass |
| 1.7 | 1.7.4990-unstable | `.NETCoreApp,Version=v10.0` | 23,040 | Pass |

The binaries were inspected separately. The 1.6 binary references mscorlib,
Harmony 2.4.3, and UnityEngine modules. The 1.7 binary references System.Runtime
10, Harmony 3, and UnityStubs. They are different binaries built for their
respective runtime; the version directories do not contain duplicate copies
of one framework build.

## Harmony binding inspection

Against **each** version's actual reference assemblies, a metadata-only
validator checked:

- 15 distinct Harmony target methods.
- 16 prefix/postfix methods.
- 29 typed instance, result, argument, and field injections.
- 0 missing targets, ambiguous targets, or invalid injections.

The private constant-tree and leave-lord helper methods were separately
checked in both versions. The private `determiningNextJob` field is present
with the expected bool type. The metadata validator does not execute stub
methods from reference assemblies.

## Package checks

- About.xml, LoadFolders.xml, the think-tree definition, both translation XML
  files, and the project file parse successfully.
- Both supported game versions are listed and select only their own DLL.
- Harmony is the sole declared prerequisite mod.
- Every used translation key exists in Korean and English; placeholders match.
- The warning icon is a valid 64 × 64 RGBA PNG.
- Final C# source and DLL hashes match the versions used for the final build.
- The ZIP excludes game assemblies, Harmony assemblies, compiler caches,
  downloaded tools, and the uploaded reference screenshot.

## Source review

The scheduling and UI code were independently reviewed for work-before-needs
selection, need fallback, assignment changes, release of already-running jobs,
reentrancy, job reservations, source identity on save/load, mandatory cleanup,
window layering, and cursor event ordering. Source review found and corrected
issues before this final build; it is not a substitute for execution.

## Not executed

**No actual RimWorld game session was run in the creation environment.**
In-game loading, rendering at different UI scales, pawn behavior, save/load,
and interactions with other mods remain to be verified in a real installation.
See TESTING.md for the acceptance scenarios. The 1.7 build is pinned to
1.7.4990-unstable and may need an update if the game's APIs change.

컴파일과 API 연결 검사는 완료했습니다. 실게임 실행과 다른 모드 조합 테스트는
수행하지 않았으며, 이 기록은 그 테스트를 완료했다고 주장하지 않습니다.

## SHA-256

```text
146daabc39407080e14ca1806f52d05af6b6d31b4e040a49d96b3663082dd77f  1.6/Assemblies/DoWorkRightNow.dll
ceff4d4b6ccff1fcb85ff204c0d3ff677628e814254ed820d28a9c8e28518c57  1.7/Assemblies/DoWorkRightNow.dll
```
