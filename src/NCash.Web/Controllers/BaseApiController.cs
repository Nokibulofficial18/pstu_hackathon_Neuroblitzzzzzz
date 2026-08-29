using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NCash.Domain.Common;

namespace NCash.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class BaseApiController : ControllerBase
{
    protected Guid CurrentUserId
    {
        get
        {
            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? User.FindFirst("sub")?.Value;

            if (Guid.TryParse(idClaim, out var userId))
                return userId;

            throw new DomainException(ErrorCodes.UnauthorizedAccess, "User authentication claim is missing or invalid.", 401);
        }
    }

    protected string CurrentUsername => User.FindFirst("username")?.Value ?? "Anonymous";

    protected Guid? CurrentAccountId
    {
        get
        {
            var claim = User.FindFirst("account_id")?.Value;
            return Guid.TryParse(claim, out var id) ? id : null;
        }
    }
}
