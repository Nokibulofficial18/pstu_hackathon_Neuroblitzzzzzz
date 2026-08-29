using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCash.Application.Common;
using NCash.Application.Modules.GroupCollect;
using NCash.Application.Modules.GroupCollect.DTOs;
using NCash.Application.Modules.PaymentEngine.DTOs;

namespace NCash.Web.Controllers;

[Authorize]
[Route("api/groups")]
[Route("api/group-collections")]
public class GroupCollectionsController : BaseApiController
{
    private readonly IGroupCollectService _groupCollectService;

    public GroupCollectionsController(IGroupCollectService groupCollectService)
    {
        _groupCollectService = groupCollectService;
    }

    /// <summary>
    /// Create a new Group Expense / Collection with target amount and optional initial members.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<GroupCollectionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateCollection([FromBody] CreateGroupCollectionDto dto, CancellationToken cancellationToken)
    {
        var result = await _groupCollectService.CreateCollectionAsync(CurrentUserId, dto, cancellationToken);
        return Ok(ApiResponse<GroupCollectionDetailDto>.Ok(result));
    }

    /// <summary>
    /// Invite a new member to an active Group Collection.
    /// </summary>
    [HttpPost("{id:guid}/members")]
    [HttpPost("{id:guid}/invite")]
    [ProducesResponseType(typeof(ApiResponse<GroupCollectionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> InviteMember(Guid id, [FromBody] InviteMemberRequestDto dto, CancellationToken cancellationToken)
    {
        var result = await _groupCollectService.InviteMemberAsync(CurrentUserId, id, dto, cancellationToken);
        return Ok(ApiResponse<GroupCollectionDetailDto>.Ok(result));
    }

    /// <summary>
    /// View collection status, target, remaining, and breakdown of all member contributions.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<GroupCollectionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCollection(Guid id, CancellationToken cancellationToken)
    {
        var result = await _groupCollectService.GetCollectionByIdAsync(CurrentUserId, id, cancellationToken);
        return Ok(ApiResponse<GroupCollectionDetailDto>.Ok(result));
    }

    /// <summary>
    /// List all Group Collections created by or involving the authenticated user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<GroupCollectionDetailDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyCollections(CancellationToken cancellationToken)
    {
        var result = await _groupCollectService.GetUserCollectionsAsync(CurrentUserId, cancellationToken);
        return Ok(ApiResponse<List<GroupCollectionDetailDto>>.Ok(result));
    }

    /// <summary>
    /// Pay member contribution toward a Group Collection (isolated via Payment Engine).
    /// </summary>
    [HttpPost("{id:guid}/pay")]
    [ProducesResponseType(typeof(ApiResponse<TransferResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PayContribution(Guid id, [FromBody] PayContributionDto dto, CancellationToken cancellationToken)
    {
        var result = await _groupCollectService.PayContributionAsync(CurrentUserId, id, dto, cancellationToken);
        return Ok(ApiResponse<TransferResultDto>.Ok(result));
    }

    /// <summary>
    /// Cancel an active Group Collection.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<GroupCollectionDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelCollection(Guid id, CancellationToken cancellationToken)
    {
        var result = await _groupCollectService.CancelCollectionAsync(CurrentUserId, id, cancellationToken);
        return Ok(ApiResponse<GroupCollectionDetailDto>.Ok(result));
    }
}
