import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { Pill } from './Pill'

const html = (props: Parameters<typeof Pill>[0]) => renderToStaticMarkup(createElement(Pill, props))

describe('Pill', () => {
  it('is a quiet tag by default, with its words inside', () => {
    const markup = html({ children: 'Left' })

    expect(markup).toContain('Left')
    expect(markup).toContain('bg-subtle')
    expect(markup).toContain('rounded-full')
  })

  it('takes its colours from its tone', () => {
    expect(html({ tone: 'accent', children: 'x' })).toContain('bg-accent-soft')
    expect(html({ tone: 'solid', children: 'x' })).toContain('bg-blue-600')
    expect(html({ tone: 'quiet', children: 'x' })).toContain('bg-slate-500')
  })

  it('is a round badge for a count', () => {
    const markup = html({ size: 'count', tone: 'solid', children: '3' })

    expect(markup).toContain('h-5')
    expect(markup).toContain('justify-center')
  })

  it('passes attributes through to the span, such as the name a screen reader gets', () => {
    expect(html({ 'aria-label': '3 unread', children: '3' })).toContain('aria-label="3 unread"')
  })
})
