using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly ILogger<ChatHub> _logger;
    private readonly AppDbContext _context;

    public ChatHub(ILogger<ChatHub> logger, AppDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _logger.LogInformation("User {UserId} connected to chat hub", userId);
        await base.OnConnectedAsync();
    }

    // Kept for SignalR clients that send directly through the hub.
    // The web client currently persists messages through MessagesController so that
    // database writes and notifications use the same path.
    public async Task SendMessage(string taskId, string message)
    {
        var task = await EnsureTaskMember(taskId);
        if (task.TaskStatus == "RunnerPaid" || task.TaskStatus == "Cancelled")
            throw new HubException("This conversation is closed.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1000)
            throw new HubException("Invalid message.");

        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        await Clients.Group($"task-{task.TaskId}").SendAsync("ReceiveMessage", new
        {
            taskId = task.TaskId,
            senderId = userId,
            message,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task JoinTaskChat(string taskId)
    {
        var task = await EnsureTaskMember(taskId);
        // Canonical public ID is the shared SignalR group key. Also join the
        // requested alias for compatibility with existing numeric-ID clients.
        await Groups.AddToGroupAsync(Context.ConnectionId, $"task-{task.TaskId}");
        if (!string.Equals(taskId, task.TaskId, StringComparison.Ordinal))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"task-{taskId}");
        _logger.LogDebug("User {UserId} joined task chat {TaskId}",
            Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, task.TaskId);
    }

    public async Task LeaveTaskChat(string taskId)
    {
        var task = await EnsureTaskMember(taskId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task-{task.TaskId}");
        if (!string.Equals(taskId, task.TaskId, StringComparison.Ordinal))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"task-{taskId}");
    }

    private async Task<DoForYou.API.Models.Task> EnsureTaskMember(string taskId)
    {
        if (!int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new HubException("Unauthorized.");

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);

        if (task == null && int.TryParse(taskId, out var numericTaskId))
            task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == numericTaskId);

        if (task == null || (task.CreatedByUserId != userId && task.AcceptedByUserId != userId))
            throw new HubException("You are not a member of this task.");

        return task;
    }
}
