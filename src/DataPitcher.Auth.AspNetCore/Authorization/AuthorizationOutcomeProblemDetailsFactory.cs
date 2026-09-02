using DataPitcher.Auth.Abstractions.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DataPitcher.Auth.AspNetCore.Authorization;

public static class AuthorizationOutcomeProblemDetailsFactory
{
    public static ProblemDetails? Create(AuthorizationDecision decision) => decision.Outcome switch
    {
        AuthorizationOutcome.Granted => null,
        AuthorizationOutcome.Denied => new ProblemDetails { Status = StatusCodes.Status403Forbidden, Extensions = { ["code"] = "authorization_denied" } },
        AuthorizationOutcome.Indeterminate => new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Extensions = { ["code"] = "authorization_indeterminate" } },
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };
}
