using System.Text.Json.Serialization;

namespace YewCone.AudiobookGenerator.Core;

internal sealed record OpenAiSpeechRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("voice")] string Voice,
    [property: JsonPropertyName("response_format")] string ResponseFormat,
    [property: JsonPropertyName("speed")] double Speed);

internal sealed record OpenAiChatCompletionsRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] OpenAiChatMessage[] Messages,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("max_tokens")] int MaximumTokens);

[JsonDerivedType(typeof(OpenAiSystemChatMessage))]
[JsonDerivedType(typeof(OpenAiUserChatMessage))]
internal abstract record OpenAiChatMessage([property: JsonPropertyName("role")] string Role);

internal sealed record OpenAiSystemChatMessage(
    [property: JsonPropertyName("content")] string Content) : OpenAiChatMessage("system");

internal sealed record OpenAiUserChatMessage(
    [property: JsonPropertyName("content")] OpenAiUserContentPart[] Content) : OpenAiChatMessage("user");

[JsonDerivedType(typeof(OpenAiUserTextContentPart))]
[JsonDerivedType(typeof(OpenAiUserImageUrlContentPart))]
internal abstract record OpenAiUserContentPart([property: JsonPropertyName("type")] string Type);

internal sealed record OpenAiUserTextContentPart(
    [property: JsonPropertyName("text")] string Text) : OpenAiUserContentPart("text");

internal sealed record OpenAiUserImageUrlContentPart(
    [property: JsonPropertyName("image_url")] OpenAiImageUrl ImageUrl) : OpenAiUserContentPart("image_url");

internal sealed record OpenAiImageUrl([property: JsonPropertyName("url")] string Url);
