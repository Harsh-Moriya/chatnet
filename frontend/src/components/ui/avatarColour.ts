// Eight hues, each with a light and a dark pair. The same name always gets the same one, so a person keeps their colour everywhere.
// (Red is left out: it means an error.) The classes are written out because Tailwind only makes the ones it can read.
const PALETTE = [
  'bg-blue-100 text-blue-700 dark:bg-blue-900/60 dark:text-blue-200',
  'bg-teal-100 text-teal-700 dark:bg-teal-900/60 dark:text-teal-200',
  'bg-violet-100 text-violet-700 dark:bg-violet-900/60 dark:text-violet-200',
  'bg-amber-100 text-amber-700 dark:bg-amber-900/60 dark:text-amber-200',
  'bg-rose-100 text-rose-700 dark:bg-rose-900/60 dark:text-rose-200',
  'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/60 dark:text-emerald-200',
  'bg-orange-100 text-orange-700 dark:bg-orange-900/60 dark:text-orange-200',
  'bg-cyan-100 text-cyan-700 dark:bg-cyan-900/60 dark:text-cyan-200',
]

export function colourFor(name: string): string {
  let hash = 0
  for (const char of name) hash = (hash * 31 + char.codePointAt(0)!) >>> 0
  return PALETTE[hash % PALETTE.length]
}
