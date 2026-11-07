using System.Net;
using ChatNET.API.Email;

namespace ChatNET.API.Auth.PasswordReset;

// The words of the reset email, with a plain text part and an HTML part. There is no name in it: the display name is whatever the
// person who registered the account typed, and with no confirmation of the address anyone can register an account under an
// address that is not theirs and make a name that reads like an instruction. A fixed greeting cannot be made to say something.
public static class PasswordResetEmail
{
    public const string Subject = "Reset your ChatNET password";

    public static EmailMessage For(string to, string link, TimeSpan lifetime)
    {
        var minutes = (int)Math.Round(lifetime.TotalMinutes);
        var validFor = minutes == 1 ? "1 minute" : $"{minutes} minutes";

        var text = $"""
            Someone asked to reset the password of the ChatNET account for this email address.

            To choose a new password, open this link within {validFor}:

            {link}

            If you did not ask for this, ignore this message. Your password stays as it is.
            """;

        var safeLink = WebUtility.HtmlEncode(link);
        var html = $"""
            <!doctype html>
            <html>
            <body style="margin:0;padding:24px;background:#f8fafc;font-family:Arial,Helvetica,sans-serif;color:#0f172a;">
              <div style="max-width:480px;margin:0 auto;background:#ffffff;border:1px solid #cbd5e1;border-radius:8px;padding:24px;">
                <h1 style="margin:0 0 16px;font-size:20px;">Reset your ChatNET password</h1>
                <p style="margin:0 0 16px;line-height:1.5;">Someone asked to reset the password of the ChatNET account for this email address.</p>
                <p style="margin:0 0 24px;line-height:1.5;">To choose a new password, use the button within {validFor}.</p>
                <p style="margin:0 0 24px;"><a href="{safeLink}" style="display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;border-radius:8px;padding:10px 20px;font-weight:bold;">Choose a new password</a></p>
                <p style="margin:0 0 8px;line-height:1.5;color:#64748b;font-size:14px;">If the button does not work, copy this link into your browser:</p>
                <p style="margin:0 0 24px;word-break:break-all;font-size:14px;"><a href="{safeLink}" style="color:#2563eb;">{safeLink}</a></p>
                <p style="margin:0;line-height:1.5;color:#64748b;font-size:14px;">If you did not ask for this, ignore this message. Your password stays as it is.</p>
              </div>
            </body>
            </html>
            """;

        return new EmailMessage(to, Subject, text, html);
    }
}
