import { PASSPHRASE_MIN_LENGTH, passphraseLength } from '../../lib/crypto'

// What is wrong with a passphrase the person typed twice, or null. The length is counted the way the backup counts it, so the
// form never accepts what the encryption would refuse.
export function passphraseProblem(passphrase: string, confirmation: string): string | null {
  if (passphraseLength(passphrase) < PASSPHRASE_MIN_LENGTH) return `Use at least ${PASSPHRASE_MIN_LENGTH} characters.`
  if (passphrase !== confirmation) return 'The two passphrases do not match.'
  return null
}
