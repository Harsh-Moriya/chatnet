// Decides whether a request that came back 401 is worth sending again after the session is renewed.
//
// A 401 on a request that carried a token usually means the access token ran out between the moment it
// was attached and the moment the server looked at it. Renewing the session and sending the request once
// more is the answer. Anything else is left alone:
// - a request with no token (a failed sign-in answers 401 too, and must not trigger a renewal),
// - a request that was already sent again (otherwise a server that always says 401 loops for ever),
// - any other status.
export interface RetryableRequest {
  headers?: { Authorization?: unknown }
  // Set on the request when it is sent again.
  retriedAfterRenewal?: boolean
}

export function canRetryAfterRenewal(status: number | undefined, request: RetryableRequest | undefined): boolean {
  return status === 401 && request !== undefined && Boolean(request.headers?.Authorization) && !request.retriedAfterRenewal
}
