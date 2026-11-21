// The actions of a conversation header that go into a three-dot menu when the header is too narrow to show them all as buttons: the ones that are
// not essential, in the order they have in the header. A menu with a single item in it is not worth a button of its own, so with fewer than two
// there is no menu and the action stays a button at every width.
export function actionsInMenu<T extends { essential: boolean }>(actions: readonly T[]): T[] {
  const others = actions.filter((action) => !action.essential)
  return others.length >= 2 ? others : []
}
