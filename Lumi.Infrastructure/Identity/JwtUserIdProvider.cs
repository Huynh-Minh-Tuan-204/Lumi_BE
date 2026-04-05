using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace Lumi.Infrastructure.Identity // Namespace phải khớp với thư mục
{
    public class JwtUserIdProvider : IUserIdProvider
    {
        public string GetUserId(HubConnectionContext connection)
        {
            // Lấy ID từ Claim NameIdentifier của JWT
            return connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }
    }
}