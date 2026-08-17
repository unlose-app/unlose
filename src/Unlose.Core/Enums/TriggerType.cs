using System.Text.Json.Serialization;

namespace Unlose.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerType
{
    Scheduled,
    AgentPreSession,
    Manual,
    AgentInitiated,
    // Automatic protection snapshot taken before a restore (appended at the end; SQLite stores enums as strings, so existing data is unaffected)
    PreRestore
}
