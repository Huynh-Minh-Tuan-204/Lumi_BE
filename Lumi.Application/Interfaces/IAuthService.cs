using Lumi.Application.DTOs;
using System.Threading.Tasks;

namespace Lumi.Application.Interfaces
{
    public interface IAuthService
    {
        Task<string> RegisterAsync(RegisterDto request);
        Task<TokenResponseDto> LoginAsync(LoginDto request);
        Task<TokenResponseDto> RefreshTokenAsync(RefreshTokenRequestDto request);

        Task LogoutAllDevicesAsync(int userId);
        Task ChangePasswordFirstTimeAsync(int userId, ChangePasswordFirstTimeDto request);
    }
}