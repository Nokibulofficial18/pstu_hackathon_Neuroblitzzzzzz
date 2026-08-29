using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NCash.Application.Common;
using NCash.Application.Modules.Auth;
using NCash.Application.Modules.Auth.DTOs;

namespace NCash.Web.Controllers;

[Route("api/[controller]")]
public class AuthController : BaseApiController
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Register a new user with transactional wallet creation and BDT 100,000 controlled system issuance.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-limiter")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _authService.RegisterAsync(request, cancellationToken);
        return Ok(ApiResponse<AuthResponseDto>.Ok(result, "User registered and automatically funded with BDT 100,000 welcome simulated funds."));
    }

    /// <summary>
    /// Authenticate user via username or email and retrieve JWT token.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-limiter")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        return Ok(ApiResponse<AuthResponseDto>.Ok(result, "Authentication successful."));
    }

    /// <summary>
    /// Get the current authenticated user's profile, wallet details, and security status.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var result = await _authService.GetCurrentUserAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<CurrentUserResponseDto>.Ok(result));
    }

    /// <summary>
    /// Set or update the account's 4-6 digit numeric transaction PIN.
    /// </summary>
    [HttpPost("pin/set")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<PinOperationResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetTransactionPin([FromBody] SetPinRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _authService.SetTransactionPinAsync(CurrentUserId, request, cancellationToken);
        return Ok(ApiResponse<PinOperationResultDto>.Ok(result));
    }

    /// <summary>
    /// Verify the user's transaction PIN.
    /// </summary>
    [HttpPost("pin/verify")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<PinOperationResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyTransactionPin([FromBody] VerifyPinRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _authService.VerifyTransactionPinAsync(CurrentUserId, request, cancellationToken);
        return Ok(ApiResponse<PinOperationResultDto>.Ok(result));
    }
}
