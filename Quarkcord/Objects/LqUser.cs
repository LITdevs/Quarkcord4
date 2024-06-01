using Lightquark.Types.Mongo;
using MongoDB.Bson;

namespace Quarkcord.Objects;

public class LqUser : IUser
{
    public ObjectId Id { get; set; }

    public required byte[] PasswordHash { get; init; }
    
    public required string Email { get; set; }
    
    public required byte[] Salt { get; init; }
    
    public required string Username { get; set; }
    
    public bool Admin { get; set; }
    
    public bool IsBot { get; set; }
    
    public bool SecretThirdThing { get; set; }
    
    public string? Pronouns { get; set; }

    public IStatus? Status => null;

    public required Uri AvatarUri { get; set; }

    public Uri AvatarUriGetter => AvatarUri;
    
    public IUser Safe => new LqUser 
    {
        Id = Id,
        Username = Username,
        AvatarUri = AvatarUri,
        IsBot = IsBot,
        Admin = Admin,
        SecretThirdThing = SecretThirdThing,
        Pronouns = Pronouns,
        PasswordHash = null!,
        Email = null!,
        Salt = null!
    };
    
}