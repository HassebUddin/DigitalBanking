using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Http;

namespace DigitalBanking.BuildingBlocks.Authentication;

public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor)
{
    public Guid UserId
    {
        get
        {
            var value = FindClaim(ClaimTypes.NameIdentifier)
                ?? FindClaim(JwtRegisteredClaimNames.Sub);

            if (!Guid.TryParse(value, out var userId))
            {
                throw new UnauthorizedException("The current user could not be identified.");
            }

            return userId;
        }
    }

    public string Email =>
        FindClaim(ClaimTypes.Email)
        ?? FindClaim(JwtRegisteredClaimNames.Email)
        ?? string.Empty;

    public string Role =>
        FindClaim(ClaimTypes.Role)
        ?? FindClaim("role")
        ?? string.Empty;

    public string FullName =>
        FindClaim("fullName")
        ?? FindClaim(ClaimTypes.Name)
        ?? Email;

    public bool IsStaff => Role is "Admin" or "InternalEmployee" or "ExternalEmployee";

    public Guid? CustomerId
    {
        get
        {
            var value = FindClaim("customerId");
            return Guid.TryParse(value, out var customerId) ? customerId : null;
        }
    }

    public bool IsAdmin => string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase);

    public bool CanViewBankRecords => IsStaff;

    public bool CanReviewApplications => Role is "Admin" or "InternalEmployee";

    private string? FindClaim(string claimType)
    {
        return httpContextAccessor.HttpContext?.User.FindFirstValue(claimType);
    }
}
