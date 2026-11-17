using System.Text.Json.Serialization;
using ShawMuOCR.Core.TextClean;

namespace ShawMuOCR.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GeneralSettings))]
[JsonSerializable(typeof(HotkeySettings))]
[JsonSerializable(typeof(OcrSettings))]
[JsonSerializable(typeof(TextCleanSettings))]
[JsonSerializable(typeof(TextCleanMode))]
[JsonSerializable(typeof(AiOcrSettings))]
[JsonSerializable(typeof(AiSettings))]
[JsonSerializable(typeof(AiApiProfile))]
[JsonSerializable(typeof(RecordingSettings))]
[JsonSerializable(typeof(TranslationSettings))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(BatchSettings))]
[JsonSerializable(typeof(HttpApiSettings))]
public partial class AppSettingsJsonContext : JsonSerializerContext
{
}
