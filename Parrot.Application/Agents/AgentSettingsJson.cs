using System.Text.Json;
using Parrot.Application.DTOs.Agents;

namespace Parrot.Application.Agents;

/// <summary>
/// How an agent's settings are written to its column and read back: camelCase,
/// the same names the API and the orchestrator use, so the column can be read
/// by eye and by either side.
/// </summary>
public static class AgentSettingsJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Write(AgentSettingsDto? settings) =>
        JsonSerializer.Serialize(settings ?? new AgentSettingsDto(), Options);

    /// <summary>
    /// The stored settings. A column that is empty or not this shape reads as
    /// the defaults rather than failing the whole agent: the owner can still
    /// open it and save it again.
    /// </summary>
    public static AgentSettingsDto Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AgentSettingsDto();

        try
        {
            return JsonSerializer.Deserialize<AgentSettingsDto>(json, Options) ?? new AgentSettingsDto();
        }
        catch (JsonException)
        {
            return new AgentSettingsDto();
        }
    }
}
