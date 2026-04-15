using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Lumi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Lumi.WebAPI.Middleware
{
    public class FirstLoginMiddleware
    {
        private readonly RequestDelegate _next;

        public FirstLoginMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ApplicationDbContext dbContext)
        {
            // KHÔNG chặn request OPTIONS (CORS Pre-flight)
            if (context.Request.Method == "OPTIONS")
            {
                await _next(context);
                return;
            }

            var user = context.User;
            if (user.Identity?.IsAuthenticated == true)
            {
                var userIdStr = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userIdStr) && int.TryParse(userIdStr, out var userId))
                {
                    // Allow access to ChangePasswordFirstTime and other auth endpoints to avoid loops
                    var path = context.Request.Path.Value?.ToLower() ?? "";
                    
                    // WHITE LIST: Cho phép các API bắt buộc để đổi mật khẩu, Logout và SignalR Negotiate.
                    bool isAllowedPath = 
                        path.Contains("/auth/change-password-first-time") ||
                        path.Contains("/auth/logout") ||
                        path.Contains("/auth/me") ||
                        path.Contains("/chathub") || 
                        path.Contains("/callhub") ||
                        path.Contains("/api/attachments/upload");

                    if (!isAllowedPath)
                    {
                        var isFirstLogin = await dbContext.Users
                            .Where(u => u.Id == userId)
                            .Select(u => u.IsFirstLogin)
                            .FirstOrDefaultAsync();

                        if (isFirstLogin)
                        {
                            // Trả về 403 Forbidden kèm thông tin bảo mật
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            await context.Response.WriteAsJsonAsync(new { 
                                error = "MUST_CHANGE_PASSWORD",
                                message = "BẠN PHẢI ĐỔI MẬT KHẨU ĐỂ TIẾP TỤC TRUY CẬP HỆ THỐNG.",
                                isFirstLogin = true 
                            });
                            return;
                        }
                    }
                }
            }

            await _next(context);
        }
    }
}
