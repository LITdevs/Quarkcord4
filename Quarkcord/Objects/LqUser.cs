using Lightquark.Types.Federation;
using Lightquark.Types.Objects;

namespace Quarkcord.Objects;

public class LqUser : IUser<BaseId>, IUser<IBaseId>
{
    public BaseId Id { get; set; }

    public required byte[] PasswordHash { get; init; }

    IBaseId IUser<IBaseId>.Id
    {
        get => Id;
        set => Id = new BaseId(value.Id, value.Network);
    }

    public required string Email { get; set; }
    
    public required byte[] Salt { get; init; }
    
    public required string Username { get; set; }
    
    public bool Admin { get; set; }
    
    public bool IsBot { get; set; }
    
    public bool SecretThirdThing { get; set; }
    
    public string? Pronouns { get; set; }
    IBaseId? IUser<IBaseId>.AvatarFileId
    {
        get => AvatarFileId;
        set => AvatarFileId = value != null ? new BaseId(value.Id, value.Network) : null;
    }

    public BaseId? AvatarFileId { get; set; }

    public IUser<IBaseId> Safe => new LqUser 
    {
        Id = Id,
        Username = Username,
        AvatarFileId = AvatarFileId,
        IsBot = IsBot,
        Admin = Admin,
        SecretThirdThing = SecretThirdThing,
        Pronouns = Pronouns,
        PasswordHash = null!,
        Email = null!,
        Salt = null!
    };
    
}