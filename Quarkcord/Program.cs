using System.Web;
using Discord;
using Discord.WebSocket;
using Lightquark.Types;
using Lightquark.Types.EventBus;
using Lightquark.Types.EventBus.Events;
using Lightquark.Types.EventBus.Messages;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using Quarkcord.Objects;

namespace Quarkcord;

public class QuarkcordPlugin : IPlugin
{
    private static Task Log(LogMessage msg)
    {
        Console.WriteLine($"[Quarkcord] {msg.ToString()}");
        return Task.CompletedTask;
    }

    private string? _token;
    private string? _botUserId;
    private Lightquark.Types.Mongo.IUser? _user;
    private DiscordSocketClient? _client;
    private readonly ManualResetEvent _eventBusGetEvent = new(false);
    private IEventBus _eventBus = null!;
    private NetworkInformation? _networkInformation;
    private MongoClient _mongoClient = null!;
    private IMongoDatabase _database = null!;
    private IMongoCollection<MessagePair> MessagePairs => _database.GetCollection<MessagePair>("messages");
    private IMongoCollection<ChannelPair> ChannelPairs => _database.GetCollection<ChannelPair>("channels");
    private List<ChannelPair> _bridgeChannels = null!;

    public void Initialize(IEventBus eventBus)
    {
        Task.Run(async () =>
        {
            eventBus.Subscribe<BusReadyEvent>(_ => _eventBusGetEvent.Set());
            _eventBusGetEvent.WaitOne();
            _eventBus = eventBus;
            var filePath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lightquark",
                "quarkcord");
            string mongoConnectionString;
            string mongoDb;
            if (File.Exists(filePath))
            {
                var text = (await File.ReadAllTextAsync(filePath)).Trim().Split(";");
                _token = text[0];
                _botUserId = text[1];
                mongoConnectionString = text[2];
                mongoDb = text[3];
            }
            else
            {
                await File.WriteAllTextAsync(filePath,
                    "INSERT_DISCORD_TOKEN_HERE;INSERT_LQ_BOT_USER_ID_HERE;INSERT_MONGO_CONNECTION_STRING_HERE;INSERT_MONGO_DB_HERE");
                _token = "INSERT_DISCORD_TOKEN_HERE";
                _botUserId = "INSERT_LQ_BOT_USER_ID_HERE";
                mongoConnectionString = "INSERT_MONGO_CONNECTION_STRING_HERE";
                mongoDb = "INSERT_MONGO_DB_HERE";
            }

            if (_token == "INSERT_DISCORD_TOKEN_HERE") throw new Exception($"Please add discord token to {filePath}");
            if (_botUserId == "INSERT_LQ_BOT_USER_ID_HERE")
                throw new Exception($"Please add bot user id to {filePath}");
            if (mongoConnectionString == "INSERT_MONGO_CONNECTION_STRING_HERE")
                throw new Exception($"Please add mongo connection string to {filePath}");
            if (mongoDb == "INSERT_MONGO_DB_HERE") throw new Exception($"Please add mongo db to {filePath}");

            _mongoClient = new MongoClient(mongoConnectionString);
            _database = _mongoClient.GetDatabase(mongoDb);

            var channelPairCursor = await ChannelPairs.FindAsync(new BsonDocument());
            var channelPairs = await channelPairCursor.ToListAsync();
            _bridgeChannels = channelPairs;
            if (_bridgeChannels.Count == 0)
            {
                await ChannelPairs.InsertOneAsync(new ChannelPair
                {
                    DiscordId = 0,
                    Id = ObjectId.GenerateNewId(),
                    LqId = ObjectId.Empty
                });
                await MessagePairs.InsertOneAsync(new MessagePair
                {
                    DiscordId = 0,
                    Id = ObjectId.GenerateNewId(),
                    LqId = ObjectId.Empty
                });
            }

            eventBus.Publish(new GetUserMessage
            {
                UserId = new ObjectId(_botUserId),
                Callback = user =>
                {
                    _user = user;
                    _eventBusGetEvent.Set();
                }
            });
            _eventBusGetEvent.WaitOne();
            _eventBusGetEvent.Reset();
            eventBus.Publish(new GetNetworkMessage
            {
                Callback = network =>
                {
                    _networkInformation = network;
                    _eventBusGetEvent.Set();
                }
            });
            _eventBusGetEvent.WaitOne();
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = (GatewayIntents)130813
            });
            _client.Log += Log;
            await Log(new LogMessage(LogSeverity.Info, "Quarkcord", "Logging in"));
            await _client.LoginAsync(TokenType.Bot, _token);
            _client.Ready += () =>
            {
                Log(new LogMessage(LogSeverity.Info, "Quarkcord", "Connected!"));
                return Task.CompletedTask;
            };

            _client.MessageReceived += DiscordMessageReceived;
            _client.MessageUpdated += DiscordMessageUpdated;
            _client.MessageDeleted += DiscordMessageDeleted;
            _client.ReactionAdded += DiscordReactionAdded;

            eventBus.Subscribe<MessageCreateEvent>(LqMessageReceived);
            eventBus.Subscribe<MessageDeleteEvent>(LqMessageDeleted);

            await _client.StartAsync();
        });
    }

    private async void LqMessageDeleted(MessageDeleteEvent deleteEvent)
    {
        try
        {
            if (_bridgeChannels.All(bc => bc.LqId != deleteEvent.Message.ChannelId)) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.LqId == deleteEvent.Message.ChannelId);
            if (_client!.GetChannel(bridgeChannel!.DiscordId) is not ITextChannel discordChannel) return;
            var messagePairCursor = await MessagePairs.FindAsync(mp => mp.LqId == deleteEvent.Message.Id);
            var messagePair = await messagePairCursor.FirstOrDefaultAsync();
            if (messagePair == null) return;
            await discordChannel.DeleteMessageAsync(messagePair.DiscordId);
            await MessagePairs.DeleteOneAsync(mp => mp.Id == messagePair.Id);
        }
        catch (Exception _)
        {
            // Plugin error ignored
        }
    }

    private async void LqMessageReceived(MessageCreateEvent createEvent)
    {
        try
        {
            if (createEvent.Message.Author!.Id == _user!.Id) return;
            if (_bridgeChannels.All(bc => bc.LqId != createEvent.Message.ChannelId)) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.LqId == createEvent.Message.ChannelId);
            if (_client!.GetChannel(bridgeChannel!.DiscordId) is not ITextChannel discordChannel) return;
            var webhooks = await discordChannel.GetWebhooksAsync();
            var webhook = webhooks.FirstOrDefault(w => w.Name == $"Quarkcord {_networkInformation?.Name}")
                          ?? await discordChannel.CreateWebhookAsync($"Quarkcord {_networkInformation?.Name}");
            var webhookClient = new Discord.Webhook.DiscordWebhookClient(webhook.Id, webhook.Token);
            var username =
                $"{createEvent.Message.Author.Username} via {createEvent.Message.UserAgent} ({_networkInformation!.Name})";
            if (username.Length > 80)
            {
                username = $"{createEvent.Message.Author.Username} ({_networkInformation!.Name})";
            }

            if (username.Length > 80)
            {
                var toRemove = $" ({_networkInformation!.Name})".Length;
                username = $"{createEvent.Message.Author.Username[..(80 - toRemove)]} ({_networkInformation!.Name})";
            }

            var message = await webhookClient.SendMessageAsync(
                createEvent.Message.Content?.Length <= 2000
                    ? createEvent.Message.Content
                    : "A message was sent but it is too long. Please view it on Lightquark",
                false,
                createEvent.Message.Attachments.Select(a => a.MimeType.StartsWith("image")
                    ? new EmbedBuilder().WithImageUrl(a.Url.ToString()).Build()
                    : new EmbedBuilder().WithTitle($"{a.Filename} ({HumanReadable.BytesToString(a.Size)})")
                        .WithUrl(a.Url.ToString()).Build()),
                username,
                createEvent.Message.Author.AvatarUriGetter.ToString(),
                null,
                AllowedMentions.None);

            var messagePair = new MessagePair
            {
                Id = ObjectId.GenerateNewId(),
                LqId = createEvent.Message.Id,
                DiscordId = message
            };
            await MessagePairs.InsertOneAsync(messagePair);
        }
        catch (Exception _)
        {
            // Ignore plugin error
        }
    }

    private async Task DiscordMessageUpdated(Cacheable<IMessage, ulong> oldMessageParam, SocketMessage messageParam,
        ISocketMessageChannel channelParam)
    {
        try
        {
            if (messageParam is not SocketUserMessage message) return;
            if (message.Author.Id == _client!.CurrentUser.Id) return;
            if (_user == null)
            {
                await Log(new LogMessage(LogSeverity.Warning, "Quarkcord", "Message received but bot user is null"));
                return;
            }

            if (_bridgeChannels.All(bc => bc.DiscordId != message.Channel.Id)) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.DiscordId == message.Channel.Id);
            var messagePairCursor = await MessagePairs.FindAsync(mp => mp.DiscordId == message.Id);
            var messagePair = await messagePairCursor.FirstOrDefaultAsync();
            if (messagePair == null)
            {
                // Somehow we didn't have this one, lets bridge it now!
                await SendOrUpdateLqMessage(message, bridgeChannel!);
            }
            else
            {
                await SendOrUpdateLqMessage(message, bridgeChannel!, true, messagePair);
            }
        }
        catch (Exception _)
        {
            //
        }
    }


    private async Task DiscordReactionAdded(Cacheable<IUserMessage, ulong> messageParam,
        Cacheable<IMessageChannel, ulong> channelParam, SocketReaction reactionParam)
    {
        try
        {
            if (_bridgeChannels.All(bc => bc.DiscordId != channelParam.Id)) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.DiscordId == channelParam.Id);
            var discordMessageId = messageParam.Id;
            var messagePairCursor = await MessagePairs.FindAsync(m => m.DiscordId == discordMessageId);
            var messagePair = await messagePairCursor.FirstOrDefaultAsync();
            if (messagePair == null) return;
            var message = await messageParam.DownloadAsync();

            var specialAttributes = new JArray
            {
                new JObject
                {
                    ["type"] = "reply",
                    ["replyTo"] = messagePair.LqId.ToString()
                }
            };

            _eventBus.Publish(new CreateMessageMessage
            {
                Message = new LqMessage
                {
                    VirtualAuthors = [_user!],
                    ChannelId = bridgeChannel!.LqId,
                    Id = ObjectId.GenerateNewId(),
                    AuthorId = _user!.Id,
                    Content = $"{(reactionParam.User.GetValueOrDefault() as SocketGuildUser)?.Nickname
                                 ?? reactionParam.User.GetValueOrDefault().GlobalName
                                 ?? reactionParam.User.GetValueOrDefault().Username
                                 ?? "Someone"} reacted with {reactionParam.Emote}",
                    UserAgent = "Quarkcord",
                    Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Edited = false,
                    Attachments = [],
                    SpecialAttributes = specialAttributes
                }
            });
        }
        catch (Exception _)
        {
            // shut up
        }
    }

    private async Task DiscordMessageDeleted(Cacheable<IMessage, ulong> messageParam,
        Cacheable<IMessageChannel, ulong> channelParam)
    {
        try
        {
            if (_bridgeChannels.All(bc => bc.DiscordId != channelParam.Id)) return;
            var discordMessageId = messageParam.Id;
            var messagePairCursor = await MessagePairs.FindAsync(m => m.DiscordId == discordMessageId);
            var messagePair = await messagePairCursor.FirstOrDefaultAsync();
            if (messagePair == null) return;

            _eventBus.Publish(new DeleteMessageMessage
            {
                MessageId = messagePair.LqId
            });

            await MessagePairs.DeleteOneAsync(mp => mp.Id == messagePair.Id);
        }
        catch (Exception _)
        {
            // Ignore
        }
    }

    private async Task SendOrUpdateLqMessage(SocketUserMessage message, ChannelPair bridgeChannel, bool update = false,
        MessagePair? existingMessagePair = null)
    {
        if (message.Author.Username.EndsWith($"({_networkInformation!.Name})")) return;
        var specialAttributes = new JArray
        {
            new JObject
            {
                ["type"] = "botMessage",
                ["username"] = (message.Author as SocketGuildUser)?.Nickname ??
                               message.Author.GlobalName ?? message.Author.Username,
                ["avatarUri"] =
                    $"{_networkInformation!.CdnBaseUrl}/external/{HttpUtility.UrlEncode(message.Author.GetDisplayAvatarUrl())}"
            }
        };
        if (message.ReferencedMessage != null)
        {
            var replyCursor = await MessagePairs.FindAsync(mp => mp.DiscordId == message.ReferencedMessage.Id);
            var reply = await replyCursor.FirstOrDefaultAsync();
            if (reply != null)
            {
                specialAttributes.Add(new JObject
                {
                    ["type"] = "reply",
                    ["replyTo"] = reply.LqId.ToString()
                });
            }
        }

        var lqMessageId = existingMessagePair?.LqId ?? ObjectId.GenerateNewId();
        var messagePair = existingMessagePair ?? new MessagePair
        {
            DiscordId = message.Id,
            Id = ObjectId.GenerateNewId(),
            LqId = lqMessageId
        };
        if (!update)
        {
            await MessagePairs.InsertOneAsync(messagePair);
        }

        var lqAttachments = message.Attachments.Select(a => new LqAttachment
        {
            FileId = ObjectId.Empty,
            Filename = a.Filename,
            MimeType = a.ContentType,
            Size = a.Size,
            Url = new Uri($"{_networkInformation!.CdnBaseUrl}/external/{HttpUtility.UrlEncode(a.Url)}")
        }).ToArray();
        LqMessage lqMessage;
        if (message.Attachments.Count == 0 && message.Content.Length == 0)
        {
            specialAttributes.Add(new JObject
            {
                ["type"] = "clientAttributes",
                ["discordMessageId"] = message.Id,
                ["quarkcord"] = true,
                ["quarkcordUnsupported"] = true
            });
            lqMessage = new LqMessage
            {
                Id = lqMessageId,
                AuthorId = _user!.Id,
                Content = "Discord message with unsupported content",
                ChannelId = bridgeChannel!.LqId,
                UserAgent = "Quarkcord",
                Timestamp = message.Timestamp.ToUnixTimeMilliseconds(),
                VirtualAuthors = [_user],
                Edited = update,
                Attachments = [],
                SpecialAttributes = specialAttributes
            };
        }
        else
        {
            specialAttributes.Add(new JObject
            {
                ["type"] = "clientAttributes",
                ["discordMessageId"] = message.Id,
                ["quarkcord"] = true
            });
            lqMessage = new LqMessage
            {
                Id = lqMessageId,
                AuthorId = _user!.Id,
                Content = message.Content,
                ChannelId = bridgeChannel!.LqId,
                UserAgent = "Quarkcord",
                Timestamp = message.Timestamp.ToUnixTimeMilliseconds(),
                VirtualAuthors = [_user],
                Edited = update,
                Attachments = lqAttachments,
                SpecialAttributes = specialAttributes
            };
        }

        if (update)
        {
            _eventBus.Publish(new EditMessageMessage
            {
                Message = lqMessage
            });
        }
        else
        {
            _eventBus.Publish(new CreateMessageMessage
            {
                Message = lqMessage
            });
        }
    }

    private async Task DiscordMessageReceived(SocketMessage messageParam)
    {
        try
        {
            if (messageParam is not SocketUserMessage message) return;
            if (message.Author.Id == _client!.CurrentUser.Id) return;
            if (_user == null)
            {
                await Log(new LogMessage(LogSeverity.Warning, "Quarkcord", "Message received but bot user is null"));
                return;
            }

            if (_bridgeChannels.All(bc => bc.DiscordId != message.Channel.Id)) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.DiscordId == message.Channel.Id);

            await SendOrUpdateLqMessage(message, bridgeChannel!);
        }
        catch (Exception _)
        {
            // Plugin error ignor
        }
    }
}

public class LqMessage : Lightquark.Types.Mongo.IMessage
{
    public ObjectId Id { get; set; }
    public ObjectId AuthorId { get; set; }
    public string? Content { get; set; }
    public ObjectId ChannelId { get; set; }
    public string UserAgent { get; set; }
    public long Timestamp { get; set; }
    public bool Edited { get; set; }
    public Lightquark.Types.Mongo.IAttachment[] Attachments { get; set; }
    public JArray SpecialAttributes { get; set; }
    public Lightquark.Types.Mongo.IUser? Author => VirtualAuthors?.FirstOrDefault();
    public Lightquark.Types.Mongo.IUser[]? VirtualAuthors { get; set; }
}

public class LqAttachment : Lightquark.Types.Mongo.IAttachment
{
    public Uri Url { get; set; }
    public long Size { get; set; }
    public string MimeType { get; set; }
    public string Filename { get; set; }
    public ObjectId FileId { get; set; }
}