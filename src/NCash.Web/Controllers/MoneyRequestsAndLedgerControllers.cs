using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCash.Application.Common;
using NCash.Application.Modules.Ledger;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Domain.Common;

namespace NCash.Web.Controllers;

[Authorize]
[Route("api/requests")]
[Route("api/money-requests")]
public class MoneyRequestsController : BaseApiController
{
    private readonly IMoneyRequestService _requestService;

    public MoneyRequestsController(IMoneyRequestService requestService)
    {
        _requestService = requestService;
    }

    /// <summary>
    /// Create a new Money Request targeted at another user account.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<MoneyRequestResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateRequest([FromBody] CreateMoneyRequestDto dto, CancellationToken cancellationToken)
    {
        var result = await _requestService.CreateRequestAsync(CurrentUserId, dto, cancellationToken);
        return Ok(ApiResponse<MoneyRequestResponseDto>.Ok(result));
    }

    /// <summary>
    /// List all requests (incoming and outgoing) for the authenticated user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<MoneyRequestResponseDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRequests(CancellationToken cancellationToken)
    {
        var result = await _requestService.GetUserRequestsAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<MoneyRequestResponseDto>>.Ok(result));
    }

    /// <summary>
    /// Get details of a specific Money Request.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MoneyRequestResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRequestById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _requestService.GetRequestByIdAsync(CurrentUserId, id, cancellationToken);
        return Ok(ApiResponse<MoneyRequestResponseDto>.Ok(result));
    }

    /// <summary>
    /// List incoming requests where current user is the payer.
    /// </summary>
    [HttpGet("incoming")]
    [ProducesResponseType(typeof(ApiResponse<List<MoneyRequestResponseDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetIncoming(CancellationToken cancellationToken)
    {
        var result = await _requestService.GetIncomingRequestsAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<MoneyRequestResponseDto>>.Ok(result));
    }

    /// <summary>
    /// List outgoing requests created by current user.
    /// </summary>
    [HttpGet("outgoing")]
    [ProducesResponseType(typeof(ApiResponse<List<MoneyRequestResponseDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOutgoing(CancellationToken cancellationToken)
    {
        var result = await _requestService.GetOutgoingRequestsAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<MoneyRequestResponseDto>>.Ok(result));
    }

    /// <summary>
    /// Accept / Pay full remaining amount on a money request.
    /// </summary>
    [HttpPost("{id:guid}/accept")]
    [HttpPost("{id:guid}/pay")]
    [ProducesResponseType(typeof(ApiResponse<TransferResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptOrPayRequest(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PayMoneyRequestDto? dto,
        CancellationToken cancellationToken)
    {
        var payload = dto ?? new PayMoneyRequestDto(null, idempotencyKey ?? $"REQ-ACCEPT-{id:N}-{Guid.NewGuid():N}");
        var key = !string.IsNullOrWhiteSpace(idempotencyKey) ? idempotencyKey.Trim() : payload.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(ApiResponse.Fail(ErrorCodes.IdempotencyKeyRequired, "Idempotency-Key header or payload is required."));
        }

        var result = await _requestService.PayRequestAsync(CurrentUserId, id, payload with { IdempotencyKey = key }, cancellationToken);
        return Ok(ApiResponse<TransferResultDto>.Ok(result));
    }

    /// <summary>
    /// Partially pay an active money request.
    /// </summary>
    [HttpPost("{id:guid}/partial-pay")]
    [ProducesResponseType(typeof(ApiResponse<TransferResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PartialPayRequest(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PayMoneyRequestDto dto,
        CancellationToken cancellationToken)
    {
        var key = !string.IsNullOrWhiteSpace(idempotencyKey) ? idempotencyKey.Trim() : dto.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(ApiResponse.Fail(ErrorCodes.IdempotencyKeyRequired, "Idempotency-Key header or payload is required."));
        }

        var result = await _requestService.PayRequestAsync(CurrentUserId, id, dto with { IdempotencyKey = key }, cancellationToken);
        return Ok(ApiResponse<TransferResultDto>.Ok(result));
    }

    /// <summary>
    /// Reject an incoming money request.
    /// </summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(ApiResponse<MoneyRequestResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RejectRequest(Guid id, CancellationToken cancellationToken)
    {
        var result = await _requestService.RejectRequestAsync(CurrentUserId, id, cancellationToken);
        return Ok(ApiResponse<MoneyRequestResponseDto>.Ok(result));
    }

    /// <summary>
    /// Cancel an outgoing money request created by current user.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<MoneyRequestResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelRequest(Guid id, CancellationToken cancellationToken)
    {
        var result = await _requestService.CancelRequestAsync(CurrentUserId, id, cancellationToken);
        return Ok(ApiResponse<MoneyRequestResponseDto>.Ok(result));
    }
}

[Authorize]
[Route("api/ledger")]
public class LedgerController : BaseApiController
{
    private readonly ILedgerService _ledgerService;

    public LedgerController(ILedgerService ledgerService)
    {
        _ledgerService = ledgerService;
    }

    [HttpGet("entries")]
    [ProducesResponseType(typeof(ApiResponse<List<LedgerEntryDetailDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyLedgerEntries([FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var result = await _ledgerService.GetAccountLedgerEntriesAsync(CurrentUserId, limit, cancellationToken);
        return Ok(ApiResponse<List<LedgerEntryDetailDto>>.Ok(result));
    }

    [HttpGet("reconciliation")]
    [ProducesResponseType(typeof(ApiResponse<GlobalReconciliationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReconciliation(CancellationToken cancellationToken)
    {
        var result = await _ledgerService.GetGlobalReconciliationAsync(cancellationToken);
        return Ok(ApiResponse<GlobalReconciliationDto>.Ok(result));
    }
}
