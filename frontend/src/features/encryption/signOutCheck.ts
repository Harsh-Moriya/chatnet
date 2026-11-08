import { indexedDbDeviceStore } from './deviceStore'

// Whether signing out of this browser would delete encryption keys. Kept apart from the rest of the feature, and importing no
// cryptography, because the sidebar asks it before every sign-out and must not carry the encryption code to do so.
export async function signOutDeletesKeys(userId: string): Promise<boolean> {
  try {
    return (await indexedDbDeviceStore.load(userId)) !== null
  } catch {
    // A browser that cannot read its store holds no keys that signing out could delete.
    return false
  }
}
