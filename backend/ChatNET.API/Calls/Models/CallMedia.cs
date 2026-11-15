namespace ChatNET.API.Calls.Models;

// What a call started as. A call that began as audio may turn its camera on later; the log keeps what it began as.
public enum CallMedia
{
    Audio,
    Video,
}
