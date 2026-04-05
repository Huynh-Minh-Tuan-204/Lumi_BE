#nullable disable

using System;

namespace Lumi.Domain.Entities
{
    public class UserDevice
    {
        // 1. Định danh
        public int Id { get; set; }
        public int UserId { get; set; }
        public virtual User User { get; set; }

        // 2. Thông tin thiết bị
        public string DeviceIdentifier { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType { get; set; } // Thêm trường này để khớp với AuthService
        public string DevicePublicKey { get; set; }
        public string RefreshTokenHash { get; set; }

        // 3. Trạng thái và thời gian (CHỈ KHAI BÁO 1 LẦN)
        public bool IsActive { get; set; } = true;
        public bool IsRevoked { get; set; } = false;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}