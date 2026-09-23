/** Read only the bounded diagnostic reference, never a server error body. */
export function responseFailure(response: Response, message: string): Error {
  const reference = response.headers.get("X-Correlation-ID");
  return new Error(reference && /^[A-Za-z0-9._:-]{1,128}$/.test(reference)
    ? `${message} Reference: ${reference}`
    : message);
}

export function failureReference(message: string): string | undefined {
  return / Reference: ([A-Za-z0-9._:-]{1,128})$/.exec(message)?.[1];
}
