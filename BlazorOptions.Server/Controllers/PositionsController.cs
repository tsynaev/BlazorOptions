using System.Security.Claims;
using BlazorOptions.API.Positions;
using BlazorOptions.Server.Authentication;
using BlazorOptions.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorOptions.Server.Controllers;

[ApiController]
[Route("api/positions")]
[Authorize(AuthenticationSchemes = UserTokenAuthenticationOptions.SchemeName)]
public sealed class PositionsController : ControllerBase, IPositionsPort
{
    private readonly PositionsStore _store;

    public PositionsController(PositionsStore store)
    {
        _store = store;
    }

    [HttpGet]
    public async Task<IActionResult> LoadPositionsAsync()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var positions = await _store.LoadPositionsAsync(userId);
        return Ok(positions);
    }

    [HttpPost]
    public async Task<IActionResult> SavePositionsAsync([FromBody] List<PositionModel> positions)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.SavePositionsAsync(userId, positions);
        return Ok();
    }

    [HttpPut("{positionId:guid}")]
    public async Task<IActionResult> SavePositionAsync([FromRoute] Guid positionId, [FromBody] PositionModel position)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        if (positionId == Guid.Empty)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Position id is required.");
        }

        position.Id = positionId;
        await _store.SavePositionAsync(userId, position);
        return Ok();
    }

    [HttpDelete("{positionId:guid}")]
    public async Task<IActionResult> DeletePositionAsync([FromRoute] Guid positionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        if (positionId == Guid.Empty)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Position id is required.");
        }

        await _store.DeletePositionAsync(userId, positionId);
        return Ok();
    }

    async Task<IReadOnlyList<PositionModel>> IPositionsPort.LoadPositionsAsync()
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadPositionsAsync(userId);
    }

    async Task IPositionsPort.SavePositionsAsync(IReadOnlyList<PositionModel> positions)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.SavePositionsAsync(userId, positions);
    }

    async Task IPositionsPort.SavePositionAsync(PositionModel position)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.SavePositionAsync(userId, position);
    }

    async Task IPositionsPort.DeletePositionAsync(Guid positionId)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.DeletePositionAsync(userId, positionId);
    }

    private string? ResolveUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private string ResolveUserIdOrThrow()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("No authenticated user is available for position operations.");
        }

        return userId;
    }
}
