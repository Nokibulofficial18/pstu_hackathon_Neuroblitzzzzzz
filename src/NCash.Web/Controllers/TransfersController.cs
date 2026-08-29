using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCash.Application.Common;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield.DTOs;
using NCash.Domain.Common;

namespace NCash.Web.Controllers;

[Authorize]
[Route("api/[controller]")]
public class TransfersController : BaseApiController
{
    private readonly ITransferService _transferService;

    public TransfersController(ITransferService transferService)
    {
        _transferService = transferService;
    }

    /// <summary>
    /// Pre-evaluate transfer risk without executing the transaction.
    /// </summary>
    [HttpPost("precheck-risk")]
    [ProducesResponseType(typeof(ApiResponse<RiskAssessmentResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreCheckRisk([FromBody] InitiateTransferDto request, CancellationToken cancellationToken)
    {
        var result = await _transferService.PreCheckTransferRiskAsync(CurrentUserId, request, cancellationToken);
        return Ok(ApiResponse<RiskAssessmentResultDto>.Ok(result));
    }

    /// <summary>
    /// Execute an atomic, idempotent P2P transfer with row-level locking and Risk Shield protection.
    /// Header: Idempotency-Key: UUID
    /// Body: { recipientId: "...", amount: 2500, purpose: "Dinner" }
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<TransferResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Transfer(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] InitiateTransferDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(ApiResponse.Fail(
                ErrorCodes.IdempotencyKeyRequired,
                "Missing required 'Idempotency-Key' HTTP header. Every financial transfer request must specify a client-generated UUID idempotency key."));
        }

        var result = await _transferService.SendMoneyAsync(CurrentUserId, request, idempotencyKey.Trim(), cancellationToken);
        return Ok(ApiResponse<TransferResultDto>.Ok(result));
    }

    /// <summary>
    /// Retrieve paginated transaction history for the authenticated user's wallet.
    /// </summary>
    [HttpGet]
    [HttpGet("history")]
    [ProducesResponseType(typeof(ApiResponse<List<TransactionDetailDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransfers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _transferService.GetUserTransactionHistoryAsync(CurrentUserId, page, pageSize, cancellationToken);
        return Ok(ApiResponse<List<TransactionDetailDto>>.Ok(result));
    }

    /// <summary>
    /// Retrieve full transaction details, chronological timeline, and risk signals.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TransactionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransfer(Guid id, CancellationToken cancellationToken)
    {
        var result = await _transferService.GetTransactionDetailAsync(id, CurrentUserId, cancellationToken);
        return Ok(ApiResponse<TransactionDetailDto>.Ok(result));
    }

    /// <summary>
    /// Retrieve explainable risk calculation and triggered rule signals for a specific transfer.
    /// </summary>
    [HttpGet("{id:guid}/risk")]
    [ProducesResponseType(typeof(ApiResponse<List<RiskSignalDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransferRisk(Guid id, CancellationToken cancellationToken)
    {
        var txn = await _transferService.GetTransactionDetailAsync(id, CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<RiskSignalDto>>.Ok(txn.RiskSignals));
    }
}
