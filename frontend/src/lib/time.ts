// The API writes UTC timestamps such as "2026-10-11T13:26:55.494153Z", but it drops trailing zeros
// from the fraction, so the fraction can be any length (".5Z", ".49Z", ".494153Z", or missing).
// Compared as text, ".49Z" sorts after ".494153Z" because 'Z' is greater than '4'. Padding the
// fraction to a fixed width first makes a text comparison agree with the time order.
//
// Date.parse is not used because it keeps only milliseconds, and two messages sent within the
// same millisecond must still sort the way the server stored them.
function normalise(timestamp: string): string {
  const match = /^(.*?)(?:\.(\d+))?Z$/.exec(timestamp)
  if (!match) return timestamp

  const [, head, fraction = ''] = match
  return `${head}.${fraction.slice(0, 9).padEnd(9, '0')}Z`
}

export function compareTimestamps(a: string, b: string): number {
  const left = normalise(a)
  const right = normalise(b)

  if (left < right) return -1
  if (left > right) return 1
  return 0
}
