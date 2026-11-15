namespace ChatNET.API.Messaging.Models;

// What a row of the timeline is. A Text row is what someone said: sealed in their browser and signed. A Call row is written by the
// server when a call ends, says nothing under any key, and points at its call.
public enum MessageKind
{
    Text,
    Call,
}
