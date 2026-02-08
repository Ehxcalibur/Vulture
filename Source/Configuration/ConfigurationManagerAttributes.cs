using System;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Attributes for controlling how config entries appear in BepInEx Configuration Manager.
    /// Used to hide options from F12 (they're instead shown in the custom F7 GUI).
    /// </summary>
    public class ConfigurationManagerAttributes
    {
        /// <summary>
        /// If false, the config entry will not be shown in the Configuration Manager.
        /// </summary>
        public bool? Browsable;

        /// <summary>
        /// If true, the value cannot be changed via Configuration Manager (read-only).
        /// </summary>
        public bool? ReadOnly;

        /// <summary>
        /// Display order in the Configuration Manager (lower = higher in list).
        /// </summary>
        public int? Order;

        /// <summary>
        /// Name of the category to put this entry in.
        /// </summary>
        public string Category;

        /// <summary>
        /// If set, show this text instead of the config key.
        /// </summary>
        public string DispName;

        /// <summary>
        /// Description shown in the Configuration Manager.
        /// </summary>
        public string Description;

        /// <summary>
        /// If true, the config entry is considered advanced settings.
        /// </summary>
        public bool? IsAdvanced;

        /// <summary>
        /// Custom draw action for the config entry.
        /// </summary>
        public Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
    }
}
