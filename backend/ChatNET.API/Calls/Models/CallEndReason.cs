namespace ChatNET.API.Calls.Models;

// How a call ended. Completed is the only one that was answered and ended by someone hanging up; Failed may be either, since a
// connection can break before or after the answer. Declined is the callee's refusal, Cancelled is the caller giving up while it
// rang, Missed is the ring running out and Busy is the callee being in another call.
public enum CallEndReason
{
    Completed,
    Missed,
    Declined,
    Cancelled,
    Busy,
    Failed,
}
