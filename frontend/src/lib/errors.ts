import axios from 'axios'

// Extracts a message fit to show a person from an API error.
//
// The backend always returns RFC 7807 ProblemDetails, so the `detail` field is read first. Two cases
// have no detail and would otherwise show the library's wording ("Network Error", "Request failed
// with status code 500"): no response at all (the server is down, the network dropped, the request
// timed out), and a server error from something in front of the API, such as a proxy, that does not
// send ProblemDetails.
export function getApiError(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const detail = error.response?.data?.detail
    if (typeof detail === 'string' && detail !== '') return detail

    if (!error.response) return "Can't reach the server. Check your connection and try again."
    if (error.response.status >= 500) return 'The server ran into a problem. Please try again.'

    return error.message
  }

  return 'An unexpected error occurred'
}
