using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Lumi.Infrastructure.Hubs
{
    [Authorize]
    public class CallHub : Hub
    {
        private readonly ApplicationDbContext _context;

        public CallHub(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetUserId()
        {
            var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null ? int.Parse(claim.Value) : 0;
        }

        private string GetUserDisplayName()
        {
            return Context.User?.FindFirst("FullName")?.Value 
                ?? Context.User?.Identity?.Name 
                ?? "User";
        }

        [HubMethodName("JoinCall")]
        public async Task JoinCall(string callId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, callId);
            var displayName = GetUserDisplayName();
            var userId = GetUserId();
            // Gửi cả ConnectionId và UserId để Client biết bản đồ User -> Conn
            await Clients.OthersInGroup(callId).SendAsync("UserJoined", Context.ConnectionId, userId, displayName);
        }

        public async Task LeaveCall(string callId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, callId);
            var displayName = GetUserDisplayName();
            var userId = GetUserId();
            await Clients.OthersInGroup(callId).SendAsync("UserLeft", Context.ConnectionId, userId, displayName);
        }

        // Signaling cho WebRTC (Mesh): A -> B
        public async Task SendOffer(string callId, int targetUserId, object offer)
        {
            var senderUserId = GetUserId();
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveOffer", offer, senderUserId);
        }

        public async Task SendAnswer(string callId, int targetUserId, object answer)
        {
            var senderUserId = GetUserId();
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveAnswer", answer, senderUserId);
        }

        public async Task SendIceCandidate(string callId, int targetUserId, object candidate)
        {
            var senderUserId = GetUserId();
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveIceCandidate", candidate, senderUserId);
        }
    }
}