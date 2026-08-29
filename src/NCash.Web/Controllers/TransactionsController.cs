using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCash.Application.Common;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;

namespace NCash.Web.Controllers;

[Authorize]
[Route("api/[controller]")]
public class TransactionsController : BaseApiController
{
    private readonly ITransferService _transferService;

    public TransactionsController(ITransferService transferService)
    {
        _transferService = transferService;
    }

    /// <summary>
    /// Retrieve full transaction details, status, risk breakdown, and ledger reconciliation.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TransactionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransaction(Guid id, CancellationToken cancellationToken)
    {
        var result = await _transferService.GetTransactionDetailAsync(id, CurrentUserId, cancellationToken);
        return Ok(ApiResponse<TransactionDetailDto>.Ok(result));
    }

    /// <summary>
    /// Retrieve chronological, immutable transaction timeline events (CREATED, VALIDATED, PROCESSING, DEBITED, CREDITED, COMPLETED).
    /// </summary>
    [HttpGet("{id:guid}/timeline")]
    [ProducesResponseType(typeof(ApiResponse<List<TransactionEventDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactionTimeline(Guid id, CancellationToken cancellationToken)
    {
        var result = await _transferService.GetTransactionDetailAsync(id, CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<TransactionEventDto>>.Ok(result.Timeline));
    }
}
