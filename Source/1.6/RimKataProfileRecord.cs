using Verse;

namespace KRWF.RimKata
{
    // Keep the complete profile catalog in the normal settings payload. Multiplayer's
    // config synchronization must carry values as well as the selected profile ID.
    public sealed class RimKataProfileRecord : IExposable
    {
        public string id;
        public string name;
        public RimKataSettingsProfile values;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref name, "name");
            Scribe_Deep.Look(ref values, "values");
        }
    }
}
