using System.Web;
using Discord;
using Discord.WebSocket;
using Lightquark.Types;
using Lightquark.Types.EventBus;
using Lightquark.Types.EventBus.Events;
using Lightquark.Types.EventBus.Messages;
using Lightquark.Types.Federation;
using Lightquark.Types.Objects;
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
    private IUser<IBaseId> _user;
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
        Console.WriteLine("[Quarkcord] Initialize");
        Task.Run(async () =>
        {
            Console.WriteLine("[Quarkcord] Subscribing to BusReadyEvent");
            eventBus.Subscribe<BusReadyEvent>(_ => _eventBusGetEvent.Set());
            _eventBusGetEvent.WaitOne();
            Console.WriteLine("[Quarkcord] BusReadyEvent obtained");
            _eventBusGetEvent.Reset();
            Console.WriteLine("[Quarkcord] Retrieving NetworkInformation");
            eventBus.Publish(new GetNetworkMessage
            {
                Callback = network =>
                {
                    Console.WriteLine("[Quarkcord] NetworkInformation obtained");
                    _networkInformation = network;
                    _eventBusGetEvent.Set();
                }
            });
            _eventBusGetEvent.WaitOne();
            _eventBus = eventBus;
            Console.WriteLine("[Quarkcord] Loading configuration");
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
                    LqId = (new BaseId(Guid.Empty, _networkInformation!.LinkBase)).ToString()
                });
                await MessagePairs.InsertOneAsync(new MessagePair
                {
                    DiscordId = 0,
                    Id = ObjectId.GenerateNewId(),
                    LqId = (new BaseId(Guid.Empty, _networkInformation!.LinkBase)).ToString()
                });
            }

            eventBus.Publish(new GetUserMessage<IBaseId>
            {
                UserId = new BaseId(_botUserId),
                Callback = user =>
                {
                    _user = user;
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
            // eventBus.Subscribe<MessageUpdateEvent>(LqMessageUpdated);
            eventBus.Subscribe<MessageDeleteEvent>(LqMessageDeleted);

            await _client.StartAsync();
        });
    }

    private async void LqMessageDeleted(MessageDeleteEvent deleteEvent)
    {
        try
        {
            if (_bridgeChannels.All(bc => bc.LqId != deleteEvent.Message.ChannelId.ToString())) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.LqId == deleteEvent.Message.ChannelId.ToString());
            if (_client!.GetChannel(bridgeChannel!.DiscordId) is not ITextChannel discordChannel) return;
            var messagePairCursor = await MessagePairs.FindAsync(mp => mp.LqId == deleteEvent.Message.Id.ToString());
            var messagePair = await messagePairCursor.FirstOrDefaultAsync();
            if (messagePair == null) return;
            if (messagePair.NoDelete) return;
            await discordChannel.DeleteMessageAsync(messagePair.DiscordId);
            await MessagePairs.DeleteOneAsync(mp => mp.Id == messagePair.Id);
        }
        catch (Exception _)
        {
            // Plugin error ignored
        }
    }

    // private async void LqMessageUpdated
    
    private async void LqMessageReceived(MessageCreateEvent createEvent)
    {
        try
        {
            if (createEvent.Message.AuthorId.Id == _user.Id.Id && createEvent.Message.AuthorId.Network == _user.Id.Network) return;
            await Console.Error.WriteLineAsync("[Quarkcord] Got a message event!");
            IUser<IBaseId>? author = null;
            var getEvent = new ManualResetEventSlim(false);
            _eventBus.Publish(new GetUserMessage<IBaseId>
            {
                UserId = createEvent.Message.AuthorId,
                Callback = user =>
                {
                    Console.WriteLine("[Quarkcord] Got author information");
                    author = user;
                    getEvent.Set();
                }
            });
            getEvent.Wait(5000);
            Console.WriteLine($"[Quarkcord] Author data? {author == null}");
            if (_bridgeChannels.All(bc => bc.LqId != createEvent.Message.ChannelId.ToString())) return;
            var bridgeChannel = _bridgeChannels.Find(bc => bc.LqId == createEvent.Message.ChannelId.ToString());
            if (_client!.GetChannel(bridgeChannel!.DiscordId) is not ITextChannel discordChannel) return;
            var webhooks = await discordChannel.GetWebhooksAsync();
            var webhook = webhooks.FirstOrDefault(w => w.Name == $"Quarkcord {_networkInformation?.Name}")
                          ?? await discordChannel.CreateWebhookAsync($"Quarkcord {_networkInformation?.Name}");
            var webhookClient = new Discord.Webhook.DiscordWebhookClient(webhook.Id, webhook.Token);
            var username =
                $"{author.Username} via {createEvent.Message.UserAgent} ({_networkInformation!.Name})";
            if (username.Length > 80)
            {
                username = $"{author.Username} ({_networkInformation!.Name})";
            }

            if (username.Length > 80)
            {
                var toRemove = $" ({_networkInformation!.Name})".Length;
                username = $"{author.Username[..(80 - toRemove)]} ({_networkInformation!.Name})";
            }

            var message = await webhookClient.SendMessageAsync(
                createEvent.Message.Content?.Length <= 2000
                    ? createEvent.Message.Content
                    : "A message was sent but it is too long. Please view it on Lightquark",
                false,
                createEvent.Message.Attachments.Select(a => a.MimeType.StartsWith("image")
                    ? new EmbedBuilder().WithImageUrl($"{_networkInformation.CdnBaseUrl}/{a.FileId}").Build()
                    : new EmbedBuilder().WithTitle($"{a.Filename} ({HumanReadable.BytesToString(a.Size)})")
                        .WithUrl($"{_networkInformation.CdnBaseUrl}/{a.FileId}").Build()),
                username,
                $"{_networkInformation.CdnBaseUrl}/{author.AvatarFileId}",
                null,
                AllowedMentions.None);

            var messagePair = new MessagePair
            {
                Id = ObjectId.GenerateNewId(),
                LqId = createEvent.Message.Id.ToString()!,
                DiscordId = message,
                NoDelete = author.Username.ToLowerInvariant().Contains("hakase")
            };
            await MessagePairs.InsertOneAsync(messagePair);
        }
        catch (Exception ex)
        {
            await Log(new LogMessage(LogSeverity.Warning, "Quarkcord", "Error :(", ex));
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

            _eventBus.Publish(new CreateMessageMessage<IBaseId, IAttachment<IBaseId>>
            {
                Message = new LqMessage
                {
                    ChannelId = new BaseId(bridgeChannel!.LqId),
                    Id = new BaseId(Guid.NewGuid(), _networkInformation.LinkBase),
                    AuthorId = new BaseId(_user!.Id.Id, _user!.Id.Network),
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

            _eventBus.Publish(new DeleteMessageMessage<IBaseId>
            {
                MessageId = new BaseId(messagePair.LqId)
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

        var lqMessageId = existingMessagePair?.LqId != null ? new BaseId(existingMessagePair?.LqId) : new BaseId(Guid.NewGuid(), _networkInformation.LinkBase);
        var messagePair = existingMessagePair ?? new MessagePair
        {
            DiscordId = message.Id,
            Id = ObjectId.GenerateNewId(),
            LqId = lqMessageId.ToString()
        };
        if (!update)
        {
            await MessagePairs.InsertOneAsync(messagePair);
        }

        var lqAttachments = message.Attachments.Select(a => new LqAttachment
        {
            FileId = new BaseId(Guid.NewGuid(), _networkInformation.LinkBase),
            Filename = a.Filename,
            MimeType = a.ContentType,
            Size = a.Size,
            Width = a.Width,
            Height = a.Height,
            OverrideUri = new Uri($"{_networkInformation!.CdnBaseUrl}/external/{HttpUtility.UrlEncode(a.Url)}")
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
                AuthorId = new BaseId(_user!.Id.Id, _user!.Id.Network),
                Content = "Discord message with unsupported content",
                ChannelId = new BaseId(bridgeChannel!.LqId),
                UserAgent = "Quarkcord",
                Timestamp = message.Timestamp.ToUnixTimeMilliseconds(),
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
                AuthorId = new BaseId(_user!.Id.Id, _user!.Id.Network),
                Content = message.Content,
                ChannelId = new BaseId(bridgeChannel!.LqId),
                UserAgent = "Quarkcord",
                Timestamp = message.Timestamp.ToUnixTimeMilliseconds(),
                Edited = update,
                Attachments = lqAttachments,
                SpecialAttributes = specialAttributes
            };
        }

        if (update)
        {
            _eventBus.Publish(new EditMessageMessage<IBaseId, IAttachment<IBaseId>>
            {
                Message = lqMessage
            });
        }
        else
        {
            _eventBus.Publish(new CreateMessageMessage<IBaseId, IAttachment<IBaseId>>
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

public class LqMessage : IMessage<BaseId, LqAttachment>, IMessage<IBaseId, IAttachment<IBaseId>>
{
    private ICollection<IAttachment<IBaseId>> _attachments;
    public BaseId Id { get; set; }
    IBaseId IMessage<IBaseId, IAttachment<IBaseId>>.AuthorId
    {
        get => AuthorId;
        set => AuthorId = new BaseId(value.Id, value.Network);
    }

    IBaseId IMessage<IBaseId, IAttachment<IBaseId>>.ChannelId
    {
        get => ChannelId;
        set => ChannelId = new BaseId(value.Id, value.Network);
    }

    IBaseId IMessage<IBaseId, IAttachment<IBaseId>>.Id
    {
        get => Id;
        set => Id = new BaseId(value.Id, value.Network);
    }

    public BaseId AuthorId { get; set; }
    public string? Content { get; set; }
    public BaseId ChannelId { get; set; }
    public string UserAgent { get; set; }
    public long Timestamp { get; set; }
    public bool Edited { get; set; }

    ICollection<IAttachment<IBaseId>> IMessage<IBaseId, IAttachment<IBaseId>>.Attachments
    {
        get => new List<IAttachment<IBaseId>>(Attachments);
        set => throw new NotImplementedException();  // idk if the following would work: Attachments = value.Select(a => (Attachment)a).ToList();
    }

    public ICollection<LqAttachment> Attachments { get; set; }
    public JArray SpecialAttributes { get; set; }
}

public class LqAttachment : IAttachment<BaseId>, IAttachment<IBaseId>
{
    public long Size { get; set; }
    public string MimeType { get; set; }
    public string Filename { get; set; }
    public BaseId FileId { get; set; }

    public Uri? OverrideUri { get; set; }

    IBaseId IAttachment<IBaseId>.FileId
    {
        get => FileId;
        set => FileId = new BaseId(value.Id, value.Network);
    }

    public int? Height { get; set; }
    public int? Width { get; set; }
}