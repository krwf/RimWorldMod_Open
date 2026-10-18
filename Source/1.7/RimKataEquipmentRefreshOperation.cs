using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Verse;

namespace KRWF.RimKata
{
    internal sealed class RimKataEquipmentRefreshOperation : IDisposable
    {
        private readonly RimKataEquipmentMemory.View memory;
        private readonly IEnumerator<object> steps;
        private RimKataWeaponRenderDiscovery.DiscoveryPlan discovery;
        private RimKataWeaponRenderProbe.DefinitionPlan definitions;
        private RimKataAllowedWeaponStore.RefreshPlan weapons;
        private bool disposed;
        private bool committed;
        private bool renderReady;
        private bool renderAttempted;
        private bool renderApplied;
        private bool preparationAttempted;

        internal bool Complete { get; private set; }
        internal string Error { get; private set; }
        internal bool CanCommitPartial => !disposed && !committed
            && (weapons?.CanCommitPartial == true || (renderReady && !renderAttempted));

        internal RimKataEquipmentRefreshOperation()
        {
            memory = RimKataEquipmentMemory.BeginRefresh();
            steps = Scan().GetEnumerator();
        }

        private IEnumerable<object> Scan()
        {
            foreach (object step in RimKataPatchTargetCatalog.ScanRefresh(memory)) yield return step;
            discovery = new RimKataWeaponRenderDiscovery.DiscoveryPlan(memory);
            foreach (object step in discovery.Scan()) yield return step;
            definitions = new RimKataWeaponRenderProbe.DefinitionPlan(discovery.Renderers, memory);
            foreach (object step in definitions.Scan()) yield return step;
            weapons = new RimKataAllowedWeaponStore.RefreshPlan();
            foreach (object step in weapons.Scan()) yield return step;
            if (!memory.Stage()) throw new IOException("Could not save the temporary equipment memory.");
            renderReady = true;
        }

        internal void Step()
        {
            if (disposed || Complete || Error != null) return;
            try
            {
                var elapsed = Stopwatch.StartNew();
                do
                {
                    if (!steps.MoveNext())
                    {
                        Complete = true;
                        Error = weapons.Error;
                        steps.Dispose();
                        return;
                    }
                } while (elapsed.ElapsedMilliseconds < 4);
            }
            catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
            {
                Error = exception.Message;
                steps.Dispose();
                Log.Warning("[RimKata] Equipment refresh could not finish: " + exception);
            }
        }

        internal void Commit()
        {
            if (disposed || !Complete || Error != null || committed)
                throw new InvalidOperationException("Equipment refresh is not ready.");
            CommitRender();
            weapons.Commit();
            RefreshPreparedWeapons();
            committed = true;
        }

        internal void CommitPartial(out int saved, out int failed)
        {
            if (!CanCommitPartial)
                throw new InvalidOperationException("Equipment refresh has no completed parts to save.");
            // A second confirmation is a single attempt, never a retry loop for failed parts.
            committed = true;
            if (renderReady && !renderAttempted)
            {
                try { CommitRender(); }
                catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
                {
                    Log.Warning("[RimKata] The render data could not be saved during partial refresh: " + exception);
                }
            }
            saved = renderApplied ? 1 : 0;
            failed = renderApplied ? 0 : 1;
            if (weapons?.Ready == true)
            {
                weapons.CommitPartial(out int savedWeapons, out int failedWeapons);
                saved += savedWeapons;
                failed += failedWeapons;
            }
            if (saved > 0)
            {
                try { RefreshPreparedWeapons(); }
                catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
                {
                    failed++;
                    Log.Warning("[RimKata] Saved partial refresh data, but could not finish weapon preparation: " + exception);
                }
            }
            Log.Message("[RimKata] Partial equipment refresh finished. Saved: " + saved + ", failed: " + failed + ".");
        }

        private void CommitRender()
        {
            if (!renderReady || renderAttempted)
                throw new InvalidOperationException("Render refresh is not ready or was already attempted.");
            renderAttempted = true;
            var installed = discovery.Install();
            if (!memory.Commit()) throw new IOException("Could not replace the equipment memory file.");
            RimKataWeaponRenderDiscovery.Apply(installed);
            definitions.Apply(installed);
            RimKataPatchTargetCatalog.Invalidate();
            renderApplied = true;
        }

        private void RefreshPreparedWeapons()
        {
            if (preparationAttempted) return;
            preparationAttempted = true;
            RimKataCloseFireMeleeStore.InvalidatePreparedCache();
            RimKataEquipmentUtility.RefreshPreparedWeapons(true);
            RimKataEquipmentMemory.Flush();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { steps.Dispose(); }
            finally { memory.Dispose(); }
        }
    }
}
