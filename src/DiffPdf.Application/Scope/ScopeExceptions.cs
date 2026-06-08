namespace DiffPdf.Application.Scope;

/// <summary>
/// Input/business-rule failures raised by the branch and instance application services. Each maps to a
/// specific HTTP status at the endpoint (see the endpoint's error mapper); the services themselves stay
/// transport-agnostic. Messages are user-facing (mirrors the <c>TriggerService</c> convention).
/// </summary>
public sealed class ScopeValidationException(string message) : Exception(message);

/// <summary>A business-rule conflict (duplicate name, branch still holds instances, an active job exists). Maps to 409.</summary>
public sealed class ScopeConflictException(string message) : Exception(message);

/// <summary>A referenced parent scope does not exist (e.g. creating an instance under an unknown branch). Maps to a 404 problem.</summary>
public sealed class ScopeNotFoundException(string message) : Exception(message);
