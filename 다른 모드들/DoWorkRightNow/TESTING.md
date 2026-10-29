# In-game verification checklist

These are manual acceptance scenarios for an actual RimWorld installation.
They are **not reported as completed** by the build environment. Run them
separately in 1.6 and 1.7, initially with Core and Harmony.

1. Open Work at the usual UI scale. Confirm the warning button is centered
   between the two priority labels and does not cover either label.
2. Click it: the same icon follows the mouse; the Work window and bottom Work
   button have red gradient borders. Esc and right-click put the tool away.
3. Enable construction for two colonists and disable it for a third. Put a
   reachable construction job and materials on the map. Force Construction.
   Only the two assigned colonists should be forced to build.
4. Set those colonists' timetable to Sleep/Recreation and make food/recreation
   desirable. Reachable construction should still take precedence.
5. Remove all eligible construction jobs or their materials. Needs/idle should
   resume; hauling, cleaning, and other ordinary work should remain suppressed.
6. While a colonist satisfies a need, create eligible construction again. They
   should switch within the next scheduled 60-tick probe.
7. Watch long-running research and multi-stage bills: they should progress
   without repeated restarts every probe.
8. Begin the matching work normally, then force it, then clear it. The normal
   need/work selection should be reevaluated on release too.
9. Change the forced type, turn an affected pawn's checkbox off/on, and change
   maps. Old forced jobs should release correctly; priorities should not change.
10. Check Firefight, Patient, and BedRest with valid jobs. Their work givers may
    be emergency or non-scanning givers; both kinds must be included.
11. Interrupt a job with a required finalizer (for example carrying a baby).
    Verify the cleanup completes, then the selected work resumes without
    stranded reservations or a restart loop.
12. Draft/undraft, down/recover, and trigger/resolve a mental state. Vanilla
    state handling should take control; available forced work resumes afterward.
13. Save and reload while work is forced, including while a cleanup job is
    running. The selected work and correct next-job source should survive.
14. Close Work with an active order: bottom button remains red. Reopen it and
    clear the same heading: borders disappear after the selection tool closes.
15. Inspect Player.log. There should be no missing method, failed patch,
    unknown think-tree node, reservation loop, or repeating job error.

After the base scenarios, repeat relevant ones with the user's work-tab and AI
mods. A replacement work-tab worker or replacement scheduler may require a
specific compatibility patch.
