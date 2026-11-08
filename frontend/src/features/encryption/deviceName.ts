// A name for this browser in the list of devices, from what the browser says about itself: "Chrome on Windows". Text to show the
// person, nothing is decided from it. The order of the tests matters, because browsers name each other in their user agents
// (Chrome says Safari, Edge says Chrome, an Android phone says Linux, an iPhone says Mac OS X).
export function describeBrowser(userAgent: string): string {
  const browser = /Edg\//.test(userAgent)
    ? 'Edge'
    : /OPR\/|Opera/.test(userAgent)
      ? 'Opera'
      : /Firefox\/|FxiOS\//.test(userAgent)
        ? 'Firefox'
        : /Chrome\/|Chromium\/|CriOS\//.test(userAgent)
          ? 'Chrome'
          : /Safari\//.test(userAgent)
            ? 'Safari'
            : null

  const system = /Windows/.test(userAgent)
    ? 'Windows'
    : /Android/.test(userAgent)
      ? 'Android'
      : /iPhone|iPad|iPod/.test(userAgent)
        ? 'iOS'
        : /Mac OS X|Macintosh/.test(userAgent)
          ? 'macOS'
          : /CrOS/.test(userAgent)
            ? 'ChromeOS'
            : /Linux/.test(userAgent)
              ? 'Linux'
              : null

  if (browser && system) return `${browser} on ${system}`
  return browser ?? system ?? 'This browser'
}
