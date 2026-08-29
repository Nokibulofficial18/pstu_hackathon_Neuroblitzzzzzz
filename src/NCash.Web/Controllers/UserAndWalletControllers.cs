using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCash.Application.Common;
using NCash.Application.Modules.Users;
using NCash.Application.Modules.Wallet;

namespace NCash.Web.Controllers;

[Authorize]
[Route("api/[controller]")]
public class UsersController : BaseApiController
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// Get the profile of the current authenticated user.
    /// </summary>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var profile = await _userService.GetUserProfileAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<UserProfileDto>.Ok(profile));
    }

    /// <summary>
    /// Search / lookup recipient by username, account number, or email.
    /// </summary>
    [HttpGet("search")]
    [HttpGet("lookup")]
    [ProducesResponseType(typeof(ApiResponse<ReceiverSearchResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SearchReceiver([FromQuery] string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(ApiResponse.Fail("INVALID_QUERY", "Search query cannot be empty."));

        var result = await _userService.LookupReceiverAsync(q, cancellationToken);
        if (result == null)
            return NotFound(ApiResponse.Fail("RECIPIENT_NOT_FOUND", $"No recipient found matching '{q}'", 404));

        return Ok(ApiResponse<ReceiverSearchResultDto>.Ok(result));
    }

    /// <summary>
    /// List suggested or recent transfer counterparties.
    /// </summary>
    [HttpGet("suggested")]
    [ProducesResponseType(typeof(ApiResponse<List<ReceiverSearchResultDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSuggested(CancellationToken cancellationToken)
    {
        var recipients = await _userService.GetSuggestedRecipientsAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<ReceiverSearchResultDto>>.Ok(recipients));
    }
}

[Authorize]
[Route("api/[controller]")]
public class WalletController : BaseApiController
{
    private readonly IWalletService _walletService;

    public WalletController(IWalletService walletService)
    {
        _walletService = walletService;
    }

    /// <summary>
    /// Retrieve current wallet overview, balance, and quick statistics.
    /// </summary>
    [HttpGet]
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ApiResponse<WalletSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWalletSummary(CancellationToken cancellationToken)
    {
        var summary = await _walletService.GetWalletSummaryAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<WalletSummaryDto>.Ok(summary));
    }
}
