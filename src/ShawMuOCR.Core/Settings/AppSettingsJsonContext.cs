using System.Text.Json.Serialization;

namespace ShawMuOCR.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
public partial class AppSettingsJsonContext : JsonSerializerContext
{
}
