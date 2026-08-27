using System.Text.Json.Serialization;

namespace FactorioLibrary.Models
{
    public class ServerSettingsModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Factorio Server";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "Hosted by Factorio Manager";

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = ["game", "tags"];

        [JsonPropertyName("max_players")]
        public int MaxPlayers { get; set; } = 0;

        [JsonPropertyName("visibility")]
        public ServerVisibility Visibility { get; set; } = new();

        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Password { get; set; } = "";

        [JsonPropertyName("token")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Token { get; set; } = "";

        [JsonPropertyName("game_password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GamePassword { get; set; } = "";

        [JsonPropertyName("require_user_verification")]
        public bool RequireUserVerification { get; set; } = true;

        [JsonPropertyName("max_upload_in_kilobytes_per_second")]
        public int MaxUploadInKilobytesPerSecond { get; set; } = 0;

        [JsonPropertyName("max_upload_slots")]
        public int MaxUploadSlots { get; set; } = 5;

        [JsonPropertyName("minimum_latency_in_ticks")]
        public int MinimumLatencyInTicks { get; set; } = 0;

        [JsonPropertyName("max_heartbeats_per_second")]
        public int MaxHeartbeatsPerSecond { get; set; } = 60;

        [JsonPropertyName("ignore_player_limit_for_returning_players")]
        public bool IgnorePlayerLimitForReturningPlayers { get; set; } = false;

        [JsonPropertyName("allow_commands")]
        public string AllowCommands { get; set; } = "admins-only";

        [JsonPropertyName("autosave_interval")]
        public int AutosaveInterval { get; set; } = 10;

        [JsonPropertyName("autosave_slots")]
        public int AutosaveSlots { get; set; } = 5;

        [JsonPropertyName("afk_autokick_interval")]
        public int AfkAutokickInterval { get; set; } = 0;

        [JsonPropertyName("auto_pause")]
        public bool AutoPause { get; set; } = true;

        [JsonPropertyName("auto_pause_when_players_connect")]
        public bool AutoPauseWhenPlayersConnect { get; set; } = true;

        [JsonPropertyName("only_admins_can_pause_the_game")]
        public bool OnlyAdminsCanPauseTheGame { get; set; } = true;

        [JsonPropertyName("autosave_only_on_server")]
        public bool AutosaveOnlyOnServer { get; set; } = true;

        [JsonPropertyName("non_blocking_saving")]
        public bool NonBlockingSaving { get; set; } = false;

        [JsonPropertyName("minimum_segment_size")]
        public int MinimumSegmentSize { get; set; } = 25;

        [JsonPropertyName("minimum_segment_size_peer_count")]
        public int MinimumSegmentSizePeerCount { get; set; } = 20;

        [JsonPropertyName("maximum_segment_size")]
        public int MaximumSegmentSize { get; set; } = 100;

        [JsonPropertyName("maximum_segment_size_peer_count")]
        public int MaximumSegmentSizePeerCount { get; set; } = 10;
    }

    public class ServerVisibility
    {
        [JsonPropertyName("public")]
        public bool Public { get; set; } = true;

        [JsonPropertyName("lan")]
        public bool Lan { get; set; } = true;
    }
}
