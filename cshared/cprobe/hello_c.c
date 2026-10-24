/* Tiny C control library for the spike: no Go runtime, no TLS, a few KB.
 * If even this fails to dlopen from a location, that location (or app-bundled
 * native code in general) is blocked by policy — noexec mount or Smack. */
int hello_c(void) { return 42; }
