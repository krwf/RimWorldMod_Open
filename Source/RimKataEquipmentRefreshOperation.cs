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
        private bool disposed;
        private bool committed;

        internal bool Complete { get; private set; }
        internal string Error { get; private set; }

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
            if (!memory.Stage()) throw new IOException("Could not save the temporary equipment memory.");
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
                        steps.Dispose();
                        return;
                    }
                } while (elapsed.ElapsedMilliseconds < 4);
            }
            catch (Exception exception)
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
            var installed = discovery.Install();
            if (!memory.Commit()) throw new IOException("Could not replace the equipment memory file.");
            RimKataWeaponRenderDiscovery.Apply(installed);
            definitions.Apply(installed);
            RimKataPatchTargetCatalog.Invalidate();
            committed = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            steps.Dispose();
            memory.Dispose();
        }
    }
}
