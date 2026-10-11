// Joins class names, skipping the ones that are off: cn('a', isActive && 'b') gives 'a b' or 'a'.
export function cn(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(' ')
}
