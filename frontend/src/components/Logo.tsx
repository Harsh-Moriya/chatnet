import { useId } from 'react'

interface LogoProps {
  size?: number
  // On the brand gradient, as the app icon is. Without it only the mark is drawn, in the colour of the text around it, for a surface that
  // is already the brand's (the panel beside the sign-in form).
  tile?: boolean
  className?: string
}

// The mark: a speech bubble drawn as a C, with the dot of a message in its gap. The same shapes are in `public/favicon.svg` and
// `public/icons/`, which are files because a browser, a phone and a push alert cannot use a component.
export function Logo({ size = 24, tile = false, className }: LogoProps) {
  const gradient = `logo${useId().replace(/[^a-zA-Z0-9]/g, '')}`

  return (
    <svg width={size} height={size} viewBox="0 0 100 100" fill="none" aria-hidden="true" className={className}>
      {tile && (
        <>
          <defs>
            <linearGradient id={gradient} x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stopColor="#1d4ed8" />
              <stop offset="1" stopColor="#0ea5e9" />
            </linearGradient>
          </defs>
          <rect width="100" height="100" rx="24" fill={`url(#${gradient})`} />
        </>
      )}
      <g className={tile ? 'text-white' : undefined}>
        <path d="M71.5 30A27 27 0 1 0 71.5 62" stroke="currentColor" strokeWidth="11" strokeLinecap="round" />
        <polygon points="33,64 29,88 56,72" fill="currentColor" stroke="currentColor" strokeWidth="5" strokeLinejoin="round" />
      </g>
      <circle cx="70" cy="46" r="7" className="fill-sky-200" />
    </svg>
  )
}
