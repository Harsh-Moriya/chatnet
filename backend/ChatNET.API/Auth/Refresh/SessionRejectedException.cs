using ChatNET.API.Common.Exceptions;

namespace ChatNET.API.Auth.Refresh;

// A refresh was refused: the session cannot be renewed and the person has to sign in again. It is a 401
// like any other, with one extra fact for the exception handler: whether the browser's cookie is now
// useless and should be removed. A request that carried no cookie has nothing to remove.
public sealed class SessionRejectedException : UnauthorizedException
{
    public const string Text = "Your session has expired. Sign in again.";

    public SessionRejectedException(bool clearCookie) : base(Text)
    {
        ClearCookie = clearCookie;
    }

    public bool ClearCookie { get; }
}
