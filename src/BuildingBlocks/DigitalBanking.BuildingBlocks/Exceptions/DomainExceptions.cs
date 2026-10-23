namespace DigitalBanking.BuildingBlocks.Exceptions;

public abstract class DomainException(string message) : Exception(message);

public sealed class NotFoundException(string message) : DomainException(message);

public sealed class ConflictException(string message) : DomainException(message);

public sealed class ValidationException(string message) : DomainException(message);

public sealed class UnauthorizedException(string message) : DomainException(message);

public sealed class ForbiddenException(string message) : DomainException(message);

public sealed class BusinessRuleException(string message) : DomainException(message);

// A service this call depends on is down or timing out; retrying later can succeed.
public sealed class ServiceUnavailableException(string message) : DomainException(message);
