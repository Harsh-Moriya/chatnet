import type { RefreshResult, SessionStatus } from './sessionManager'

// A password reset ends every session of that person on the server, but the access token this browser holds keeps working until it
// expires, and other tabs do not know. The browser may also belong to someone else: another person can be signed in here while the
// reset is for a different account, and that person must stay signed in.
//
// Asking the server to renew the session tells the two apart. It refuses when the cookie belongs to the person whose password was
// reset (the session is over), and renews it when the cookie is someone else's. Only a refusal ends this browser's session, the
// other tabs are told, and the cookie is removed.
export async function endSessionAfterReset(deps: {
  status: () => SessionStatus
  refresh: () => Promise<RefreshResult>
  signOut: () => Promise<void>
}): Promise<void> {
  if (deps.status() !== 'authenticated') return

  if ((await deps.refresh()) === 'ended') await deps.signOut()
}
