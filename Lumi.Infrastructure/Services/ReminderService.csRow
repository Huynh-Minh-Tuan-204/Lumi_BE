using Lumi.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace Lumi.Infrastructure.Services
{
    public interface IReminderService
    {
        Task TriggerReminder(int userId, int conversationId, string content);
    }

    public class ReminderService : IReminderService
    {
        private readonly IHubContext<ChatHub> _hubContext;

        public ReminderService(IHubContext<ChatHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task TriggerReminder(int userId, int conversationId, string content)
        {
            await _hubContext.Clients.User(userId.ToString()).SendAsync("ReminderTriggered", new {
                conversationId,
                content,
                triggeredAt = System.DateTime.UtcNow.ToString("o")
            });
        }
    }
}
