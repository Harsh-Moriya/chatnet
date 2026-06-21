import axios from 'axios'

// Extracts the human-readable message from an API error response.
// The backend always returns RFC 7807 ProblemDetails, so we read the
// `detail` field first. If the error is not from the API (network failure,
// timeout), we fall back to the generic axios message.
export function getApiError(error: unknown): string {
  if (axios.isAxiosError(error)) {
    return error.response?.data?.detail ?? error.message
  }
  return 'An unexpected error occurred'
}
