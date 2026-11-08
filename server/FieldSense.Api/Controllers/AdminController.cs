using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record GrantRelayDto(bool CanRelaySms);

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public class AdminController(AppDbContext db) : ControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                u.Id,
                u.Email,
                role = u.Role.ToString(),
                u.CanRelaySms,
                u.ForwardEmailEnabled,
                u.CreatedAt,
                u.LastLoginAt,
                deviceCount = u.Devices.Count,
                messageCount = u.Messages.Count
            })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPut("users/{id:guid}/relay")]
    public async Task<IActionResult> SetRelay(Guid id, [FromBody] GrantRelayDto dto)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        user.CanRelaySms = dto.CanRelaySms;
        await db.SaveChangesAsync();
        return Ok(new { user.Id, user.Email, user.CanRelaySms });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        return Ok(new
        {
            users = await db.Users.CountAsync(),
            devices = await db.Devices.CountAsync(),
            messages = await db.Messages.CountAsync(),
            queuedOutbound = await db.OutboundJobs.CountAsync(j => j.Status == "queued")
        });
    }
}
