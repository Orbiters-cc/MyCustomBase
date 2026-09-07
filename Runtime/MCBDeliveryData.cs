using Newtonsoft.Json;

[JsonObject(MemberSerialization.OptIn)]
public sealed class MCBPayloadVariant
{
    [JsonProperty] public string codec;
    [JsonProperty] public string path;
    [JsonProperty] public string hash;
    [JsonProperty] public string outputHash;
    [JsonProperty] public long bytes;
    [JsonProperty] public long decodedBytes;
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class MCBDeliveryVariant
{
    [JsonProperty] public string codec;
    [JsonProperty] public long packageBytes;
    [JsonProperty] public long decodedBytes;
}
