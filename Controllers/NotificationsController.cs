using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _context;

    public NotificationsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetNotifications(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? type = null,
        [FromQuery] bool unreadOnly = false)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var query = _context.Notifications.Where(n => n.UserId == userId);
        if (!string.IsNullOrEmpty(type)) query = query.Where(n => n.Type == type);
        if (unreadOnly) query = query.Where(n => !n.IsRead);

        var entries = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new
            {
                Notification = n,
                RelatedTaskStringId = n.RelatedTask != null ? n.RelatedTask.TaskId : null
            })
            .ToListAsync();

        var legacyLinks = entries
            .Where(entry => !entry.Notification.RelatedTaskId.HasValue)
            .Select(entry => new
            {
                entry.Notification,
                Description = GetLegacyTaskDescription(entry.Notification)
            })
            .Where(entry => entry.Description != null)
            .ToList();
        var creatorDescriptions = legacyLinks
            .Where(entry => entry.Notification.Type is "task_claimed" or "task_completed")
            .Select(entry => entry.Description!)
            .Distinct()
            .ToArray();
        var runnerDescriptions = legacyLinks
            .Where(entry => entry.Notification.Type == "payout_pending")
            .Select(entry => entry.Description!)
            .Distinct()
            .ToArray();
        var legacyTasks = new List<(int Id, string TaskId, string Description, int CreatorId, int? RunnerId, DateTime UpdatedAt)>();

        if (creatorDescriptions.Length > 0)
        {
            var creatorTasks = await _context.Tasks
                .Where(task => task.CreatedByUserId == userId && creatorDescriptions.Contains(task.TaskDescription))
                .Select(task => new
                {
                    task.Id,
                    task.TaskId,
                    Description = task.TaskDescription,
                    CreatorId = task.CreatedByUserId,
                    task.AcceptedByUserId,
                    task.UpdatedAt
                })
                .ToListAsync();
            legacyTasks.AddRange(creatorTasks.Select(task =>
                (task.Id, task.TaskId, task.Description, task.CreatorId, task.AcceptedByUserId, task.UpdatedAt)));
        }

        if (runnerDescriptions.Length > 0)
        {
            var runnerTasks = await _context.Tasks
                .Where(task => task.AcceptedByUserId == userId && runnerDescriptions.Contains(task.TaskDescription))
                .Select(task => new
                {
                    task.Id,
                    task.TaskId,
                    Description = task.TaskDescription,
                    CreatorId = task.CreatedByUserId,
                    task.AcceptedByUserId,
                    task.UpdatedAt
                })
                .ToListAsync();
            legacyTasks.AddRange(runnerTasks.Select(task =>
                (task.Id, task.TaskId, task.Description, task.CreatorId, task.AcceptedByUserId, task.UpdatedAt)));
        }

        var notifications = entries.Select(entry =>
        {
            var relatedTaskId = entry.Notification.RelatedTaskId;
            var relatedTaskStringId = entry.RelatedTaskStringId;
            if (!relatedTaskId.HasValue)
            {
                var description = GetLegacyTaskDescription(entry.Notification);
                var candidates = legacyTasks.Where(task =>
                    task.Description == description &&
                    (entry.Notification.Type is "task_claimed" or "task_completed"
                        ? task.CreatorId == userId
                        : task.RunnerId == userId));
                var match = candidates
                    .OrderBy(task => Math.Abs((task.UpdatedAt - entry.Notification.CreatedAt).Ticks))
                    .FirstOrDefault();
                if (match.Id != 0)
                {
                    relatedTaskId = match.Id;
                    relatedTaskStringId = match.TaskId;
                }
            }

            return (object)new
            {
                id = entry.Notification.Id,
                type = entry.Notification.Type,
                title = entry.Notification.Title,
                message = entry.Notification.Message,
                isRead = entry.Notification.IsRead,
                createdAt = entry.Notification.CreatedAt,
                relatedTaskId,
                relatedTaskStringId
            };
        }).ToList();

        return Ok(new ApiResponse<List<object>>
        {
            Success = true,
            Data = notifications,
            Message = "Notifications retrieved successfully"
        });
    }

    [HttpPost("{id}/mark-read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkAsRead(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Notification not found" });

        notification.IsRead = true;
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Notification marked as read"
        });
    }

    [HttpPost("mark-task-read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkTaskNotificationsRead([FromBody] MarkTaskReadRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var taskNotifications = await _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead && n.Type == "new_message" && n.RelatedTaskId == request.TaskId)
            .ToListAsync();

        foreach (var notification in taskNotifications)
        {
            notification.IsRead = true;
        }

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task notifications marked as read" });
    }

    [HttpPost("mark-all-read")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkAllAsRead()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notifications = await _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ToListAsync();

        foreach (var notification in notifications)
        {
            notification.IsRead = true;
        }

        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "All notifications marked as read"
        });
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<ApiResponse<int>>> GetUnreadCount()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var count = await _context.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead);

        return Ok(new ApiResponse<int>
        {
            Success = true,
            Data = count
        });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteNotification(int id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notification = await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

        if (notification == null)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Notification not found" });

        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Notification deleted successfully"
        });
    }

    [HttpDelete("clear-read")]
    public async Task<ActionResult<ApiResponse<bool>>> ClearReadNotifications()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var notifications = await _context.Notifications
            .Where(n => n.UserId == userId && n.IsRead)
            .ToListAsync();

        _context.Notifications.RemoveRange(notifications);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Read notifications cleared" });
    }

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private static string? GetLegacyTaskDescription(DoForYou.API.Models.Notification notification)
    {
        var marker = notification.Type switch
        {
            "task_claimed" or "task_completed" => "task: ",
            "payout_pending" => "for: ",
            _ => null
        };
        if (marker == null) return null;

        var markerIndex = notification.Message.LastIndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0 ? null : notification.Message[(markerIndex + marker.Length)..];
    }
}

public class MarkTaskReadRequest
{
    public int TaskId { get; set; }
}