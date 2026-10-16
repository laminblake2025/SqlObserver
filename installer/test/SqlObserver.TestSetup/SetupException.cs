namespace SqlObserver.TestSetup;

// Only installer-authored messages are displayed. Provider/Windows exceptions are reduced to type names.
public sealed class SetupException(string message) : Exception(message);
