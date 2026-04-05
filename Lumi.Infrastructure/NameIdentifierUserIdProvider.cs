using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace Lumi.Infrastructure.Identity
{
    public class NameIdentifierUserIdProvider : IUserIdProvider
    {
        public string GetUserId(HubConnectionContext connection)
        {
            // Ép SignalR sử dụng Claim "nameidentifier" làm ID định danh duy nhất
            return connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }
    }
}