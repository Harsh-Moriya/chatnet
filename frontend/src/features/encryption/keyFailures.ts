// What a screen tells a person when starting a conversation or adding people was stopped by the keys. The errors are recognised by their
// name and the details they carry, not by their class, so a screen can use this without loading the cryptography that raises them.

function joinNames(names: string[]): string {
  if (names.length <= 1) return names[0] ?? 'Someone'
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}

// The sentence for the failure, or null when it is not one about keys. `nameOf` turns a person's id into what to call them.
export function describeKeyFailure(error: unknown, nameOf: (userId: string) => string): string | null {
  if (!(error instanceof Error)) return null

  switch (error.name) {
    case 'MissingKeysError': {
      const names = ((error as { userIds?: string[] }).userIds ?? []).map(nameOf)
      return `${joinNames(names)} ${names.length === 1 ? 'has' : 'have'} not set up encryption yet. Ask them to sign in and finish setting it up, then try again.`
    }

    case 'IdentityChangedError': {
      const userId = (error as { userId?: string }).userId
      return `${userId === undefined ? 'Someone' : nameOf(userId)} has a different security key from the one this browser saw before, so nothing was sent to them.`
    }

    case 'EpochRefused': {
      const code = (error as { code?: string | null }).code
      if (code === 'member-has-no-keys') return 'Someone in this conversation has not set up encryption yet.'
      if (code === 'epoch-too-soon') return 'The keys of this conversation were replaced recently, so they cannot be replaced again yet.'
      if (code === 'edit-count-stale') return 'This message was edited somewhere else first. Its latest text is shown now; edit it again from there.'
      return 'The conversation changed while its keys were being made. Please try again.'
    }

    case 'EpochContestedError':
      return 'The conversation changed too many times while its keys were being made. Please try again.'

    case 'KeysMismatchError':
      return 'The keys on this browser are not the ones on the server, so nothing was sent.'

    case 'EpochKeyMissingError':
      return 'The key of this conversation is not available on this browser, so nothing was sent.'

    case 'EncryptionNotReadyError':
      return 'Your encryption keys are not available on this browser.'

    default:
      return null
  }
}
