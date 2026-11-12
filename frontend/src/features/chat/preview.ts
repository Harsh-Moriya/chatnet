// How much of a message's text the sidebar and the bell show. Counted in UTF-16 code units, like the limit on a message itself.
export const PREVIEW_LENGTH = 100

// The start of a text, for a line of it. Null stays null: a message with no text to show.
export function previewOf(text: string | null): string | null {
  return text !== null && text.length > PREVIEW_LENGTH ? text.slice(0, PREVIEW_LENGTH) : text
}
