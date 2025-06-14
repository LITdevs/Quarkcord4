using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Quarkcord.Objects;

public class MessagePair
{
    [BsonId] public ObjectId Id;
    public ulong DiscordId;
    public string LqId;
    public bool NoDelete = false;
}