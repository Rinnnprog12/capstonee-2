namespace TsuOrg.Application.Common;

public sealed class NotFoundException : Exception
{
    public NotFoundException(string entity, object key)
        : base($"{entity} with key '{key}' was not found.") { }
}

public sealed class BusinessRuleException : Exception
{
    public BusinessRuleException(string message) : base(message) { }
}

public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
}
