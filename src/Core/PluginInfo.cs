namespace RavenfieldVR.Core
{
    /// <summary>Identity of the plugin as BepInEx and SteamVR see it.</summary>
    public static class PluginInfo
    {
        public const string Guid = "com.qwavey.ravenfieldvr";
        public const string Name = "Ravenfield VR";
        public const string Version = "1.16.5";

        /// <summary>SteamVR application key used when the game is not launched through Steam.</summary>
        public const string SteamVrAppKey = "steam.app.636480";

        public const string SteamAppId = "636480";
    }
}
