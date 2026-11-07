import { anonymousApi } from '../../../lib/anonymousApi'

// Asks for an email with a link to choose a new password. The server answers the same for every address (an address with no
// account is not told apart), so success says nothing about whether the email exists.
export async function requestPasswordReset(email: string): Promise<void> {
  await anonymousApi.post('/auth/forgot-password', { email })
}

// Sets the new password with the token from the link. The server ends every session of that person.
export async function resetPassword(token: string, newPassword: string): Promise<void> {
  await anonymousApi.post('/auth/reset-password', { token, newPassword })
}
