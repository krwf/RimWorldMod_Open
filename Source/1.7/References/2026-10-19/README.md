# RimWorld 1.7 — 2026-10-19 reference snapshot

- Captured from the installed game on 2026-10-19 (Asia/Seoul).
- Game version from Version.txt and Player.log: `1.7.4982 rev1280`; Assembly-CSharp file/assembly version: `1.7.9787.38423`.
- Includes Assembly-CSharp, UnityStubs, Waylith, Waylith.UnityCompat, and the installed 1.7 Harmony DLL.
- All four game DLL hashes differ from the October 17 snapshot. Harmony is unchanged: file/assembly version `3.0.0.0`, library `3.0.0.0-rc.3`. The log's Harmony Mod version `3.0.1.0` identifies the mod, not the DLL assembly version.
- `manifest.json` records original paths, source timestamps, file/assembly versions, sizes, SHA-256 hashes, capture time, and change status relative to October 17.
- All five copied DLL hashes match their sources before and after copying. The 21 existing files comprising root references, October 16/17 snapshots, and the project file were verified unchanged.
- Default builds still use the October 13 references in the parent folder. This dated folder is a comparison archive and is excluded from Git with the rest of `References`; it belongs to development sources, not the distribution package.
- The earlier focused stat investigation found `IsDisabledFor` and `ShouldShowFor` unchanged across the examined references, and `GetValueUnfinalized` unchanged across the 1.7 references. That finding does not establish compatibility of unrelated engine paths.
- Keep this snapshot unchanged. Store subsequent updates in a new dated/build folder.
