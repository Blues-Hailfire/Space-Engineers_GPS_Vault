using GpsSyncPlugin.Settings;
using GpsSyncPlugin.Settings.Elements;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GpsSyncPlugin
{
    public class Config : INotifyPropertyChanged
    {
        #region Options

        private string endpoint = "http://localhost:8765";
        private string token = "";
        private string channelId = "";
        private bool syncEnabled = true;
        private int gpsIntervalSeconds = 30;
        private int positionIntervalSeconds = 5;
        private int hostilesIntervalSeconds = 5;

        #endregion

        #region User interface

        public readonly string Title = "GPS Vault Sync";

        [Separator("Discord bot connection")]

        [Textbox(description: "Base URL of the GPS Manager bot's sync API, e.g. http://localhost:8765")]
        public string Endpoint
        {
            get => endpoint;
            set => SetField(ref endpoint, value);
        }

        [Textbox(description: "Sync token from the bot's /create_sync_token command")]
        public string Token
        {
            get => token;
            set => SetField(ref token, value);
        }

        [Textbox(description: "Optional: Discord channel ID to sync to. Leave blank to use the " +
                               "channel the token was created for — the token authorizes every " +
                               "channel the bot is bound to in that same Discord server, so this " +
                               "just picks which one.")]
        public string ChannelId
        {
            get => channelId;
            set => SetField(ref channelId, value);
        }

        [Checkbox(description: "Push/pull GPS points and live position to the vault. Turn off to " +
                                "pause all syncing without losing your Endpoint/Token/Channel ID " +
                                "settings. Can also be toggled in-game with /vault sync.")]
        public bool SyncEnabled
        {
            get => syncEnabled;
            set => SetField(ref syncEnabled, value);
        }

        [Separator("Sync intervals")]

        [Slider(1f, 300f, 1f, SliderAttribute.SliderType.Integer, description: "How often (seconds) to push the in-game GPS list")]
        public int GpsIntervalSeconds
        {
            get => gpsIntervalSeconds;
            set => SetField(ref gpsIntervalSeconds, value);
        }

        [Slider(1f, 60f, 1f, SliderAttribute.SliderType.Integer, description: "How often (seconds) to push live player position")]
        public int PositionIntervalSeconds
        {
            get => positionIntervalSeconds;
            set => SetField(ref positionIntervalSeconds, value);
        }

        [Slider(1f, 60f, 1f, SliderAttribute.SliderType.Integer, description: "How often (seconds) to push detected hostile signals (antenna/sensor contacts currently visible on your HUD)")]
        public int HostilesIntervalSeconds
        {
            get => hostilesIntervalSeconds;
            set => SetField(ref hostilesIntervalSeconds, value);
        }

        #endregion

        #region Property change notification boilerplate

        public static readonly Config Default = new Config();
        public static readonly Config Current = ConfigStorage.Load();

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        #endregion
    }
}
