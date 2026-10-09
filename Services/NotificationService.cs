using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Hubs;

namespace DoForYou.API.Services;

public interface INotificationService
{
    System.Threading.Tasks.Task CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null);
    System.Threading.Tasks.Task NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null);
    System.Threading.Tasks.Task NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null);
    System.Threading.Tasks.Task NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null);
    System.Threading.Tasks.Task NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null);
    System.Threading.Tasks.Task NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null);
    System.Threading.Tasks.Task NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null);
}

public class NotificationService : INotificationService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<ChatHub> _hubContext;

    public NotificationService(AppDbContext context, IHubContext<ChatHub> hubContext)
    {
        _context = context;
        _hubContext = hubContext;
    }

    public async System.Threading.Tasks.Task CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null)
    {
        var notification = new Notification
        {
            UserId = userId,
            Type = type,
            Title = title,
            Message = message,
            RelatedTaskId = relatedTaskId,
            IsRead = false
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();
        var relatedTaskStringId = relatedTaskId.HasValue
            ? await _context.Tasks
                .Where(task => task.Id == relatedTaskId.Value)
                .Select(task => task.TaskId)
                .FirstOrDefaultAsync()
            : null;

        await _hubContext.Clients.User(userId.ToString()).SendAsync("NewNotification", new
        {
            id = notification.Id,
            type = notification.Type,
            title = notification.Title,
            message = notification.Message,
            isRead = notification.IsRead,
            createdAt = notification.CreatedAt,
            relatedTaskId = notification.RelatedTaskId,
            relatedTaskStringId
        });
    }

    public async System.Threading.Tasks.Task NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null)
    {
        await CreateNotificationAsync(
            creatorId,
            "task_claimed",
            "Task Claimed",
            $"{runnerName} has claimed your task: {taskDescription}",
            taskId
        );
    }

    public async System.Threading.Tasks.Task NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null)
    {
        await CreateNotificationAsync(
            creatorId,
            "task_completed",
            "Task Completed",
            $"{runnerName} has completed your task: {taskDescription}",
            taskId
        );
    }

    // Kept as the existing interface entry point used by the task confirmation flow.
    // The payout is queued here; Paystack's successful transfer webhook is authoritative.
    public async System.Threading.Tasks.Task NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null)
    {
        await CreateNotificationAsync(
            runnerId,
            "payout_pending",
            "Payout Initiated",
            $"Your R{amount:F2} runner payout has been queued and is being processed through Paystack for: {taskDescription}",
            taskId
        );
    }

    public async System.Threading.Tasks.Task NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null)
    {
        await CreateNotificationAsync(
            runnerId,
            "payout_completed",
            "Payout Completed",
            $"Your R{amount:F2} runner payout has been completed through Paystack for: {taskDescription}",
            taskId
        );
    }

    public async System.Threading.Tasks.Task NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null)
    {
        await CreateNotificationAsync(
            recipientId,
            "new_message",
            "New Message",
            $"You have a new message from {senderName} about: {taskDescription}",
            taskId
        );
    }

    public async System.Threading.Tasks.Task NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null)
    {
        var adminIds = await _context.Users
            .Where(u => u.Roles != null && u.Roles.Contains("Admin"))
            .Select(u => u.Id)
            .ToListAsync();

        foreach (var adminId in adminIds)
            await CreateNotificationAsync(adminId, type, title, message, relatedTaskId);
    }
}
