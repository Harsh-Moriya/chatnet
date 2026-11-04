namespace ChatNET.API.Push;

// Some of the browsers of an alert could not be reached for a reason that may pass (a push service that is down or busy). Thrown
// when the rest has been done, so the relay records it and tries the alert again with its growing wait; the message names only how
// many, because it is stored with the row.
public sealed class PushDeliveryException : Exception
{
    public PushDeliveryException(int unavailable)
        : base($"{unavailable} browsers could not be reached by their push service.")
    {
    }
}
