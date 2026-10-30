using System.Text.Json.Serialization;

namespace Augora.Providers.TuneIn;

// Response shapes of the RadioTime OPML API with render=json. Numbers are sometimes sent as strings.

internal sealed record OpmlResponse<T>(
    [property: JsonPropertyName("head")] OpmlHead? Head,
    [property: JsonPropertyName("body")] List<T>? Body);

internal sealed record OpmlHead(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("fault")] string? Fault);

internal sealed record OpmlOutline(
    [property: JsonPropertyName("element")] string? Element,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("URL")] string? Url,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("guide_id")] string? GuideId,
    [property: JsonPropertyName("subtext")] string? Subtext,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("item")] string? Item,
    [property: JsonPropertyName("stream_type")] string? StreamType,
    [property: JsonPropertyName("topic_duration")] int? TopicDuration,
    [property: JsonPropertyName("children")] List<OpmlOutline>? Children);

internal sealed record OpmlStream(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("bitrate")] int? Bitrate,
    [property: JsonPropertyName("reliability")] int? Reliability,
    [property: JsonPropertyName("is_hls_advanced")] string? IsHlsAdvanced);

internal sealed record OpmlDescription(
    [property: JsonPropertyName("guide_id")] string? GuideId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slogan")] string? Slogan,
    [property: JsonPropertyName("logo")] string? Logo);

[JsonSourceGenerationOptions(
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(OpmlResponse<OpmlOutline>))]
[JsonSerializable(typeof(OpmlResponse<OpmlStream>))]
[JsonSerializable(typeof(OpmlResponse<OpmlDescription>))]
internal sealed partial class TuneInJsonContext : JsonSerializerContext;
