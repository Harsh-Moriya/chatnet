// What the sign-in page is told when it is reached after a password was changed: the reset page navigates there with this as the
// router state. The state is read once and then removed, so a reload does not show it again.
export const PASSWORD_CHANGED_STATE = { passwordChanged: true } as const

export function wasPasswordChanged(state: unknown): boolean {
  return typeof state === 'object' && state !== null && 'passwordChanged' in state && state.passwordChanged === true
}
