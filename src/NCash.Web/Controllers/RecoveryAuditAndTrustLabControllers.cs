using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCash.Application.Common;
using NCash.Application.Modules.Audit;
using NCash.Application.Modules.Ledger;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.TrustLab;
using NCash.Application.Modules.TrustLab.DTOs;

namespace NCash.Web.Controllers;

[Authorize]
[Route("api/recovery")]
public class RecoveryController : BaseApiController
{
    private readonly IRecoveryCenterService _recoveryService;

    public RecoveryController(IRecoveryCenterService recoveryService)
    {
        _recoveryService = recoveryService;
    }

    /// <summary>
    /// File a recovery case for a stuck, uncredited, or disputed transaction.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<RecoveryCaseDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> FileRecoveryCase([FromBody] CreateRecoveryCaseDto dto, CancellationToken cancellationToken)
    {
        var result = await _recoveryService.FileRecoveryCaseAsync(CurrentUserId, dto, cancellationToken);
        return Ok(ApiResponse<RecoveryCaseDetailDto>.Ok(result));
    }

    /// <summary>
    /// Get details of a specific recovery case with audit diagnosis and verifiable ledger state.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<RecoveryCaseDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecoveryCase(Guid id, CancellationToken cancellationToken)
    {
        var result = await _recoveryService.GetRecoveryCaseByIdAsync(id, CurrentUserId, cancellationToken);
        return Ok(ApiResponse<RecoveryCaseDetailDto>.Ok(result));
    }

    /// <summary>
    /// Get all recovery cases filed by the authenticated user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<RecoveryCaseDetailDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyRecoveryCases(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin") || User.IsInRole("Auditor"))
        {
            var allResult = await _recoveryService.GetAllRecoveryCasesAsync(cancellationToken);
            return Ok(ApiResponse<List<RecoveryCaseDetailDto>>.Ok(allResult));
        }

        var result = await _recoveryService.GetUserRecoveryCasesAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<RecoveryCaseDetailDto>>.Ok(result));
    }

    /// <summary>
    /// Trigger automated state inspection and recovery diagnosis for an UNKNOWN / STUCK transaction.
    /// </summary>
    [HttpPost("{id:guid}/investigate")]
    [ProducesResponseType(typeof(ApiResponse<RecoveryCaseDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> InvestigateCase(Guid id, CancellationToken cancellationToken)
    {
        var result = await _recoveryService.InvestigateAndResolveCaseAsync(id, cancellationToken);
        return Ok(ApiResponse<RecoveryCaseDetailDto>.Ok(result));
    }

    /// <summary>
    /// (Admin/Auditor) List all recovery cases across the platform.
    /// </summary>
    [HttpGet("all")]
    [Authorize(Roles = "Admin,Auditor")]
    [ProducesResponseType(typeof(ApiResponse<List<RecoveryCaseDetailDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllRecoveryCases(CancellationToken cancellationToken)
    {
        var result = await _recoveryService.GetAllRecoveryCasesAsync(cancellationToken);
        return Ok(ApiResponse<List<RecoveryCaseDetailDto>>.Ok(result));
    }

    /// <summary>
    /// (Admin/Auditor) Manually resolve or close a recovery case with auditor resolution note.
    /// </summary>
    [HttpPost("{id:guid}/resolve")]
    [Authorize(Roles = "Admin,Auditor")]
    [ProducesResponseType(typeof(ApiResponse<RecoveryCaseDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResolveRecoveryCase(Guid id, [FromBody] ResolveRecoveryCaseDto dto, CancellationToken cancellationToken)
    {
        var result = await _recoveryService.ManualResolveCaseAsync(id, dto, cancellationToken);
        return Ok(ApiResponse<RecoveryCaseDetailDto>.Ok(result));
    }
}

[Authorize]
[Route("api/audit")]
public class AuditController : BaseApiController
{
    private readonly IAuditService _auditService;

    public AuditController(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet("logs")]
    [Authorize(Roles = "Admin,Auditor")]
    [ProducesResponseType(typeof(ApiResponse<List<AuditLogDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var result = await _auditService.GetRecentLogsAsync(page, pageSize, cancellationToken);
        return Ok(ApiResponse<List<AuditLogDto>>.Ok(result));
    }

    [HttpGet("user/{userId:guid}")]
    [Authorize(Roles = "Admin,Auditor")]
    [ProducesResponseType(typeof(ApiResponse<List<AuditLogDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserAuditTrail(Guid userId, CancellationToken cancellationToken)
    {
        var result = await _auditService.GetUserAuditLogsAsync(userId, cancellationToken);
        return Ok(ApiResponse<List<AuditLogDto>>.Ok(result));
    }
}

[Authorize(Roles = "Auditor,Admin")]
[Route("api/trust-lab")]
public class TrustLabController : BaseApiController
{
    private readonly ITrustLabService _trustLabService;

    public TrustLabController(ITrustLabService trustLabService)
    {
        _trustLabService = trustLabService;
    }

    /// <summary>
    /// TEST 1: Simulate 5 duplicate requests using the same idempotency key.
    /// </summary>
    [HttpPost("duplicate-test")]
    [ProducesResponseType(typeof(ApiResponse<DuplicateTestResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunDuplicateTest([FromQuery] decimal amount = 1000m, CancellationToken cancellationToken = default)
    {
        var result = await _trustLabService.RunDuplicateTestAsync(CurrentUserId, amount, cancellationToken);
        return Ok(ApiResponse<DuplicateTestResultDto>.Ok(result));
    }

    /// <summary>
    /// TEST 2: Simulate two simultaneous BDT 700 transfers against BDT 1,000 balance to verify race safety.
    /// </summary>
    [HttpPost("concurrency-test")]
    [ProducesResponseType(typeof(ApiResponse<ConcurrencyTestResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunConcurrencyTest(CancellationToken cancellationToken)
    {
        var result = await _trustLabService.RunConcurrencyTestAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<ConcurrencyTestResultDto>.Ok(result));
    }

    /// <summary>
    /// TEST 3: Simulate network timeout where transaction committed but client retried.
    /// </summary>
    [HttpPost("retry-test")]
    [ProducesResponseType(typeof(ApiResponse<NetworkRetryTestResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunNetworkRetryTest([FromQuery] decimal amount = 500m, CancellationToken cancellationToken = default)
    {
        var result = await _trustLabService.RunNetworkRetryTestAsync(CurrentUserId, amount, cancellationToken);
        return Ok(ApiResponse<NetworkRetryTestResultDto>.Ok(result));
    }

    /// <summary>
    /// TEST 4: Simulate PROCESSING -> UNKNOWN uncertain state and automated recovery.
    /// </summary>
    [HttpPost("timeout-test")]
    [ProducesResponseType(typeof(ApiResponse<TimeoutRecoveryTestResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunTimeoutTest([FromQuery] decimal amount = 1200m, CancellationToken cancellationToken = default)
    {
        var result = await _trustLabService.RunTimeoutTestAsync(CurrentUserId, amount, cancellationToken);
        return Ok(ApiResponse<TimeoutRecoveryTestResultDto>.Ok(result));
    }

    /// <summary>
    /// TEST 5: Test boundary defenses against 6 invalid input vectors.
    /// </summary>
    [HttpPost("invalid-input-test")]
    [ProducesResponseType(typeof(ApiResponse<InvalidInputTestResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunInvalidInputTest(CancellationToken cancellationToken)
    {
        var result = await _trustLabService.RunInvalidInputTestAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<InvalidInputTestResultDto>.Ok(result));
    }

    /// <summary>
    /// TEST 6: Audit mathematical ledger consistency (Total Debits == Total Credits, Zero Variance).
    /// </summary>
    [HttpGet("ledger-integrity")]
    [ProducesResponseType(typeof(ApiResponse<LedgerIntegrityReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunLedgerIntegrityAudit(CancellationToken cancellationToken)
    {
        var result = await _trustLabService.RunLedgerIntegrityAuditAsync(cancellationToken);
        return Ok(ApiResponse<LedgerIntegrityReportDto>.Ok(result));
    }

    /// <summary>
    /// TEST 7: Attempt multiple payments on the same Money Request to verify overpayment protection.
    /// </summary>
    [HttpPost("repeated-request-test")]
    [ProducesResponseType(typeof(ApiResponse<RepeatedRequestTestResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunRepeatedRequestAcceptTest(CancellationToken cancellationToken)
    {
        var result = await _trustLabService.RunRepeatedRequestAcceptTestAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<RepeatedRequestTestResultDto>.Ok(result));
    }
}
