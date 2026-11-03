namespace ChatNET.API.Push.Persistence;

// What a push needs to reach one browser.
public record PushTarget(Guid Id, Guid UserId, string Endpoint, string P256dh, string Auth);
