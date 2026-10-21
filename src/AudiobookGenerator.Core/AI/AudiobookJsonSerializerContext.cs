using System.Text.Json;
using System.Text.Json.Serialization;

namespace YewCone.AudiobookGenerator.Core;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true)]
[JsonSerializable(typeof(TtsSettings))]
[JsonSerializable(typeof(VisionSettings))]
[JsonSerializable(typeof(ImageDescriptionProject))]
internal sealed partial class AudiobookFileJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OpenAiSpeechRequest))]
[JsonSerializable(typeof(OpenAiChatCompletionsRequest))]
[JsonSerializable(typeof(OpenAiChatMessage))]
[JsonSerializable(typeof(OpenAiSystemChatMessage))]
[JsonSerializable(typeof(OpenAiUserChatMessage))]
[JsonSerializable(typeof(OpenAiUserContentPart))]
[JsonSerializable(typeof(OpenAiUserTextContentPart))]
[JsonSerializable(typeof(OpenAiUserImageUrlContentPart))]
[JsonSerializable(typeof(OpenAiImageUrl))]
internal sealed partial class OpenAiRequestJsonContext : JsonSerializerContext;
