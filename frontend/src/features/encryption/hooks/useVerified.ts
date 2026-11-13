import { useEffect, useState } from 'react'
import { useAuthStore } from '../../auth/store/authStore'
import { useIdentityChanges } from '../identityChanges'
import { indexedDbPinStore } from '../pinStore'

// Whether the signed-in person marked this person as verified, for the mark beside a name. Reads the remembered key from this browser,
// and again whenever a mark is set or taken away or a changed key is accepted. A person who was never seen is not verified.
export function useVerified(contactId: string | null): boolean {
  const selfId = useAuthStore((state) => state.user?.userId)
  const marks = useIdentityChanges((state) => state.marks)
  const [verified, setVerified] = useState(false)

  useEffect(() => {
    if (contactId === null || selfId === undefined) return

    let current = true
    indexedDbPinStore.load(selfId, contactId).then(
      (pin) => {
        if (current) setVerified(pin?.verifiedAt !== undefined)
      },
      () => undefined,
    )
    return () => {
      current = false
    }
  }, [contactId, selfId, marks])

  return contactId !== null && verified
}
