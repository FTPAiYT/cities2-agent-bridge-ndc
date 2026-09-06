using System.Collections.Generic;
using Colossal;
using Game.Modding;
using Game.Settings;

namespace CitiesIIAgentBridge
{
    public sealed class BridgeSettings : ModSetting
    {
        public BridgeSettings(IMod mod) : base(mod) { SetDefaults(); }

        [SettingsUISection("Main", "Control")]
        public bool AllowControl { get; set; }

        public override void SetDefaults() { AllowControl = false; }
    }

    public sealed class LocaleEN : IDictionarySource
    {
        private readonly BridgeSettings settings;
        public LocaleEN(BridgeSettings settings) { this.settings = settings; }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { settings.GetSettingsLocaleID(), "Cities II Agent Bridge" },
                { settings.GetOptionTabLocaleID("Main"), "Bridge" },
                { settings.GetOptionGroupLocaleID("Control"), "Local control" },
                { settings.GetOptionLabelLocaleID(nameof(BridgeSettings.AllowControl)), "Allow local bridge controls" },
                { settings.GetOptionDescLocaleID(nameof(BridgeSettings.AllowControl)), "Allow construction, demolition, zoning, tile purchases, budgets, taxes, saves, and camera/simulation commands. Analysis commands pause the city. Bounded simulation steps pause when they finish or time out. Construction uses native placement checks and spending limits. Off after loading a city. With controls off, pause the city manually before inspection." }
            };
        }
        public void Unload() { }
    }
}


