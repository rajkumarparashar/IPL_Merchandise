using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.Application.Common;

/// <summary>Base for errors raised by use cases (as opposed to domain rule violations).</summary>
public abstract class ApplicationErrorException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>400: one or more inputs are invalid.</summary>
public sealed class RequestValidationException(IDictionary<string, string[]> errors, string code = ErrorCodes.ValidationFailed)
    : ApplicationErrorException(code, "One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static RequestValidationException For(string field, string message, string code = ErrorCodes.ValidationFailed) =>
        new(new Dictionary<string, string[]> { [field] = [message] }, code);
}

/// <summary>401: credentials are wrong. Message is deliberately generic.</summary>
public sealed class AuthenticationFailedException()
    : ApplicationErrorException(ErrorCodes.InvalidCredentials, "Email or password is incorrect.");

/// <summary>404.</summary>
public sealed class NotFoundException(string resource, string id)
    : ApplicationErrorException(ErrorCodes.NotFound, $"{resource} '{id}' was not found.");

/// <summary>409: valid request that conflicts with the current state. Extensions are copied to the problem details.</summary>
public sealed class ConflictException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
    : ApplicationErrorException(code, message)
{
    public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}

/// <summary>412: optimistic concurrency check failed (If-Match did not match the current version).</summary>
public sealed class ConcurrencyConflictException(string message, string? currentETag = null)
    : ApplicationErrorException(ErrorCodes.ConcurrencyConflict, message)
{
    public string? CurrentETag { get; } = currentETag;
}

/// <summary>428: the request must be conditional (If-Match) but was not.</summary>
public sealed class PreconditionRequiredException(string message)
    : ApplicationErrorException(ErrorCodes.PreconditionRequired, message);

/// <summary>503: a dependency other than the database (e.g. file storage) is unreachable. Safe to retry.</summary>
public sealed class DependencyUnavailableException(string message, Exception? inner = null)
    : ApplicationErrorException(ErrorCodes.DependencyUnavailable, message, inner);
