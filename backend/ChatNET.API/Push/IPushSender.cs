using ChatNET.API.Push.Persistence;

namespace ChatNET.API.Push;

public enum PushOutcome
{
    // The push service accepted it.
    Sent,

    // The push service says the browser is gone (404 or 410), or the address may not be posted to: the registration is of
    // no use any more.
    Gone,

    // The push service refused it for a reason that trying again will not change (the keys were refused, the request was
    // malformed). Logged; not retried.
    Rejected,

    // The push service could not be reached or could not take it now (a timeout, a 429, a 5xx). Worth trying again.
    Unavailable,
}

// Sends one alert to one browser.
public interface IPushSender
{
    Task<PushOutcome> SendAsync(PushTarget target, PushAlert alert, CancellationToken cancellationToken);
}
