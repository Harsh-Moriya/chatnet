// The keys and the subscription as the page handles them. The server sends its key as base64url; the push manager takes bytes, and
// hands back a subscription whose parts are base64url again.

export function decodeKey(key: string): Uint8Array<ArrayBuffer> {
  const base64 = key.replaceAll('-', '+').replaceAll('_', '/')
  const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=')

  return Uint8Array.from(atob(padded), (character) => character.charCodeAt(0))
}

// Was the subscription made with this key? One made with another key (the server was given new keys) is of no use: the push
// service takes pushes signed by the key the browser subscribed with, and this server signs with its current one.
export function usesKey(subscriptionKey: ArrayBuffer | null, key: string): boolean {
  if (subscriptionKey === null) return false

  const have = new Uint8Array(subscriptionKey)
  const want = decodeKey(key)

  return have.length === want.length && have.every((byte, index) => byte === want[index])
}

// What the server takes: the shape of the browser's own PushSubscription.toJSON(), with everything it needs present.
export interface Registration {
  endpoint: string
  keys: { p256dh: string; auth: string }
}

export function toRegistration(json: PushSubscriptionJSON): Registration | null {
  const { endpoint, keys } = json
  if (!endpoint || !keys?.p256dh || !keys.auth) return null

  return { endpoint, keys: { p256dh: keys.p256dh, auth: keys.auth } }
}
