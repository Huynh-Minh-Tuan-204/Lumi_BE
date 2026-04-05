using Lumi.Application.DTOs;
using Lumi.Application.Exceptions;
using Lumi.Application.Interfaces;
using Lumi.Domain.Entities;
using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumi.Infrastructure.Services
{
    public class AuthService : IAuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly IPasswordService _passwordService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AuthService> _logger;
        private readonly IConfiguration _config;

        public AuthService(
            ApplicationDbContext context,
            IPasswordService passwordService,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AuthService> logger,
            IConfiguration config)
        {
            _context = context;
            _passwordService = passwordService;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
            _config = config;
        }

        // =========================================================
        // 1. ĐĂNG KÝ (REGISTER)
        // =========================================================
        public async Task<string> RegisterAsync(RegisterDto request)
        {
            if (request == null)
                throw new ValidationException("Dữ liệu đầu vào không hợp lệ.");

            string normalizedUsername = request.Username?.Trim().ToLower();
            string normalizedEmail = request.Email?.Trim().ToLower();
            string employeeCode = request.EmployeeCode?.Trim();

            if (string.IsNullOrEmpty(normalizedUsername) || normalizedUsername.Length < 4)
                throw new ValidationException("Tên đăng nhập phải có ít nhất 4 ký tự.");

            var emailValidator = new System.ComponentModel.DataAnnotations.EmailAddressAttribute();
            if (string.IsNullOrEmpty(normalizedEmail) || !emailValidator.IsValid(normalizedEmail))
                throw new ValidationException("Định dạng Email không hợp lệ.");

            ValidatePasswordStrength(request.Password);

            if (await _context.Users.AnyAsync(u => u.Username == normalizedUsername ||
                                                   u.EmployeeCode == employeeCode ||
                                                   u.Email == normalizedEmail))
            {
                throw new ValidationException("Tên đăng nhập, Mã nhân viên hoặc Email đã tồn tại!");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                _passwordService.CreatePasswordHash(request.Password, out string hash, out string salt);

                var user = new User
                {
                    EmployeeCode = employeeCode,
                    Username = normalizedUsername,
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    FullName = request.FullName?.Trim(),
                    Email = normalizedEmail,
                    Phone = request.Phone ?? "",
                    AvatarPath = "",
                    IsActive = true,
                    RoleId = request.RoleId != 0 ? request.RoleId : 4,
                    CreatedAt = DateTime.UtcNow,
                    FailedLoginAttempts = 0,
                    MustChangePassword = false
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                string clientIp = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "Unknown";

                var auditLog = new AuditLog
                {
                    UserId = user.Id,
                    Action = "USER_REGISTERED",
                    TargetType = "User",
                    TargetId = user.Id,
                    PreviousHash = "NONE",
                    RecordHash = "GENESIS_RECORD",
                    CreatedAt = DateTime.UtcNow,
                    IpAddress = clientIp
                };
                _context.AuditLogs.Add(auditLog);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return user.Username;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Lỗi khi đăng ký tài khoản: {Username}", normalizedUsername);
                throw new Exception("Đăng ký thất bại do lỗi hệ thống.");
            }
        }

        // =========================================================
        // 2. ĐĂNG NHẬP (LOGIN)
        // =========================================================
        public async Task<TokenResponseDto> LoginAsync(LoginDto request)
        {
            var normalizedUsername = request.Username?.Trim().ToLower();

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username);

            if (user == null)
                throw new ValidationException("Tên đăng nhập hoặc mật khẩu không chính xác.");

            if (!user.IsActive)
                throw new ValidationException("Tài khoản đã bị vô hiệu hóa.");

            if (user.LockedUntil.HasValue && user.LockedUntil > DateTime.UtcNow)
            {
                var remainingMinutes = Math.Ceiling((user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes);
                throw new ValidationException($"Tài khoản đang bị khóa. Thử lại sau {remainingMinutes} phút.");
            }

            // 1. Kiểm tra mật khẩu
            bool isPasswordValid = _passwordService.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt);

            if (!isPasswordValid)
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= 5)
                {
                    user.LockedUntil = DateTime.UtcNow.AddMinutes(15);
                }
                await _context.SaveChangesAsync();
                throw new ValidationException("Tên đăng nhập hoặc mật khẩu không chính xác.");
            }

            // Reset trạng thái đăng nhập sai
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            user.LastLogin = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // 2. Tạo Token string
            var accessToken = GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken();

            // 3. Xử lý Device (Phải chạy TRƯỚC khi return)
            var device = await _context.UserDevices
                .Where(d => d.UserId == user.Id && d.IsActive)
                .OrderByDescending(d => d.LastSeen)
                .FirstOrDefaultAsync();

            if (device == null)
            {
                device = new UserDevice
                {
                    UserId = user.Id,
                    DeviceName = "Auto-Registered Device",
                    DeviceType = "Web",
                    DeviceIdentifier = Guid.NewGuid().ToString(),
                    IsActive = true,
                    IsRevoked = false,
                    CreatedAt = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow
                };
                _context.UserDevices.Add(device);
                await _context.SaveChangesAsync();
            }

            // 4. Lưu RefreshToken vào Database
            var refreshTokenEntity = new RefreshToken
            {
                UserId = user.Id,
                DeviceId = device.Id,
                TokenHash = HashRefreshToken(refreshToken), // Đảm bảo hàm này có tồn tại
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.RefreshTokens.Add(refreshTokenEntity);
            await _context.SaveChangesAsync();

            // 5. Trả về kết quả duy nhất ở cuối hàm
            return new TokenResponseDto
            {
                AccessToken = accessToken, // Đã sửa từ 'token' thành 'accessToken'
                RefreshToken = refreshToken,
                IsFirstLogin = user.IsFirstLogin
            };
        }

        // =========================================================
        // 3. LÀM MỚI TOKEN (REFRESH TOKEN)
        // =========================================================
        public async Task LogoutAllDevicesAsync(int userId)
        {
            // 1. Tìm tất cả Token đang còn Active của User này
            var activeTokens = await _context.RefreshTokens
                .Where(t => t.UserId == userId && t.IsActive && t.RevokedAt == null)
                .ToListAsync();

            if (activeTokens.Any())
            {
                foreach (var token in activeTokens)
                {
                    token.RevokedAt = DateTime.UtcNow;
                    token.IsActive = false;
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation("Security: User {UserId} đã thực hiện đăng xuất khỏi tất cả thiết bị.", userId);
            }
        }

        public async Task ChangePasswordFirstTimeAsync(int userId, ChangePasswordFirstTimeDto request)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) throw new ValidationException("User không tồn tại.");
            if (!user.IsFirstLogin) throw new ValidationException("Tài khoản này đã thực hiện đổi mật khẩu lần đầu rồi.");

            // 1. Kiểm tra mật khẩu cũ
            if (!_passwordService.VerifyPassword(request.OldPassword, user.PasswordHash, user.PasswordSalt))
            {
                throw new ValidationException("Mật khẩu cũ không chính xác.");
            }

            // 2. Validate mật khẩu mới
            if (request.NewPassword == request.OldPassword)
            {
                throw new ValidationException("Mật khẩu mới không được trùng với mật khẩu cũ.");
            }
            ValidatePasswordStrength(request.NewPassword);

            // 3. Hash mật khẩu mới
            _passwordService.CreatePasswordHash(request.NewPassword, out string newHash, out string newSalt);
            user.PasswordHash = newHash;
            user.PasswordSalt = newSalt;
            user.IsFirstLogin = false;
            user.MustChangePassword = false;
            user.PasswordChangedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Security: User {UserId} đã cập nhật mật khẩu lần đầu thành công.", userId);
        }
        public async Task<TokenResponseDto> RefreshTokenAsync(RefreshTokenRequestDto request)
        {
            var hashedToken = HashRefreshToken(request.RefreshToken);

            var tokenEntity = await _context.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hashedToken);

            if (tokenEntity == null || !tokenEntity.IsActive || tokenEntity.RevokedAt != null)
                throw new SecurityException("Phiên đăng nhập không hợp lệ hoặc đã bị thu hồi.");

            if (tokenEntity.ExpiresAt < DateTime.UtcNow)
                throw new ValidationException("Token đã hết hạn.");

            var user = await _context.Users.FindAsync(tokenEntity.UserId);
            if (user == null) throw new ValidationException("User không tồn tại.");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var newAccessToken = GenerateJwtToken(user);
                var newRefreshToken = GenerateRefreshToken();

                var newRefreshTokenEntity = new RefreshToken
                {
                    UserId = user.Id,
                    DeviceId = tokenEntity.DeviceId,
                    TokenHash = HashRefreshToken(newRefreshToken),
                    ExpiresAt = DateTime.UtcNow.AddDays(7),
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.RefreshTokens.Add(newRefreshTokenEntity);
                await _context.SaveChangesAsync();

                tokenEntity.RevokedAt = DateTime.UtcNow;
                tokenEntity.IsActive = false;
                tokenEntity.ReplacedByTokenId = newRefreshTokenEntity.Id;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return new TokenResponseDto
                {
                    AccessToken = newAccessToken,
                    RefreshToken = newRefreshToken
                };
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        // =========================================================
        // 4. HÀM PHỤ TRỢ (HELPER METHODS)
        // =========================================================
        private string GenerateJwtToken(User user)
        {
            var jwtSettings = _config.GetSection("JwtSettings");

            var secretKey = jwtSettings["Key"] ?? "Lumi_Default_Secret_Key_2026";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username ?? ""),
        new Claim("FullName", user.FullName ?? user.Username ?? ""),
        new Claim(ClaimTypes.Email, user.Email ?? ""),
        new Claim("EmployeeCode", user.EmployeeCode ?? ""),
        new Claim(ClaimTypes.Role, user.Role?.Name ?? "Employee"),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };

            var token = new JwtSecurityToken(
                issuer: jwtSettings["Issuer"],
                audience: jwtSettings["Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(
                    Convert.ToDouble(jwtSettings["DurationInMinutes"] ?? "60")
                ),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private string GenerateRefreshToken()
        {
            var randomNumber = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private string HashRefreshToken(string token)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
            return Convert.ToBase64String(hash);
        }

        private void ValidatePasswordStrength(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ValidationException("Mật khẩu không được để trống.");

            var strongPasswordRegex = new Regex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[\W_]).{8,}$");
            if (!strongPasswordRegex.IsMatch(password))
            {
                throw new ValidationException("Mật khẩu phải dài ít nhất 8 ký tự, bao gồm chữ hoa, chữ thường, số và ký tự đặc biệt.");
            }
        }
    }
}