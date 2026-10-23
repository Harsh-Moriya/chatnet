import { Fragment, useMemo } from 'react'
import { cn } from '../../../lib/cn'
import { splitMentions } from '../mentions'
import type { Member } from '../types'

interface MessageTextProps {
  content: string
  // The people in the conversation; a name that is not one of them is plain text, as it is for the server.
  members: readonly Member[] | undefined
  selfUserName: string | undefined
  // The message is the reader's own, in a blue bubble, where the mention needs a different treatment.
  own: boolean
}

// The text of a message with the people it names picked out. The one that names the reader stands out most.
export function MessageText({ content, members, selfUserName, own }: MessageTextProps) {
  const parts = useMemo(() => splitMentions(content, members, selfUserName), [content, members, selfUserName])

  return (
    <>
      {parts.map((part, index) =>
        part.mention ? (
          <span
            key={index}
            className={cn(
              'font-semibold',
              own
                ? 'underline decoration-white/60 underline-offset-2'
                : part.mention.isSelf
                  ? 'rounded bg-blue-100 px-0.5 text-blue-700'
                  : 'text-blue-600',
            )}
          >
            {part.text}
          </span>
        ) : (
          <Fragment key={index}>{part.text}</Fragment>
        ),
      )}
    </>
  )
}
